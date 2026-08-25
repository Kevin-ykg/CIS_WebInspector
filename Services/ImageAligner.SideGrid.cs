using System;
using System.Collections.Generic;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 侧边 Mark 非线性残差网格：检测左右 4 mm Mark、剔除异常残差并建立三列控制网格。
    /// </summary>
    public static partial class ImageAligner
    {
        private static bool ValidateSideAlignmentInputs(
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            out string error)
        {
            if (qrAnchor.CenterX < 0 || qrAnchor.PixelWidth <= 1 ||
                double.IsNaN(qrAnchor.CenterX) || double.IsInfinity(qrAnchor.CenterX) ||
                double.IsNaN(qrAnchor.PixelWidth) || double.IsInfinity(qrAnchor.PixelWidth))
            {
                error = $"第二个二维码的 X/宽度无效：X={qrAnchor.CenterX:F2}, Width={qrAnchor.PixelWidth:F2}。";
                return false;
            }

            if (options.SideMarkPairCount < 1 ||
                options.SideMarkMinValidPerColumn < 1 ||
                options.SideMarkMinValidPerColumn > options.SideMarkPairCount ||
                options.SideMarkDiameterMm <= 0 || options.SheetWidthMm <= 0 ||
                options.TiffSideMarkEdgeOffsetMm <= 0 ||
                options.TiffSideMarkEdgeOffsetMm * 2 >= options.SheetWidthMm ||
                options.CisQrToLeftMarkMm <= 0 || options.CisSideMarkSpanMm <= 0 ||
                options.QrPhysicalWidthMm <= 0 ||
                options.SideMarkInitialSearchMarginMm < 0 ||
                options.SideMarkExpandedSearchMarginMm < options.SideMarkInitialSearchMarginMm ||
                options.NonlinearRemapStripeRows < 1)
            {
                error = "侧边 Mark 几何参数、有效点数量或 Remap 分块参数无效。";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// 在 TIFF 目标空间建立左/中/右三列控制网格，检测左右实点并计算 CIS 源空间残差。
        /// 中心列和上下边界为虚拟零残差点；任何点数或拓扑质量不合格都返回 false。
        /// </summary>
        private static bool TryBuildSideGrid(
            Mat cisGray,
            Mat tiffMat,
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            Mat inverseGlobalTransform,
            out SideGridData grid,
            out string error)
        {
            grid = null;
            // 三列控制网格：左右列来自实测残差，中心列与上下边界固定为零。
            // 因此边缘补偿会向中心平滑衰减，且不改写 H0 在中心区域的结果。
            int pairCount = options.SideMarkPairCount;
            int rowCount = pairCount + 2;
            double tiffPxPerMm = options.LayoutDpi / 25.4;
            double cisPxPerMmX = qrAnchor.PixelWidth / options.QrPhysicalWidthMm;
            double cisPxPerMmY = qrAnchor.PixelHeight / options.QrPhysicalHeightMm;
            double topYmm = options.TiffTopCenterYmm;
            double bottomYmm = options.TiffHeightMm - options.TiffBottomOffsetMm;
            if (bottomYmm <= topYmm)
            {
                error = "侧边控制网格的上下边界物理位置无效。";
                return false;
            }

            double[] gridX =
            {
                options.TiffSideMarkEdgeOffsetMm * tiffPxPerMm,
                options.SheetWidthMm * 0.5 * tiffPxPerMm,
                (options.SheetWidthMm - options.TiffSideMarkEdgeOffsetMm) * tiffPxPerMm
            };
            var gridY = new double[rowCount];
            double stepMm = (bottomYmm - topYmm) / (pairCount + 1.0);
            for (int row = 0; row < rowCount; row++)
                gridY[row] = (topYmm + row * stepMm) * tiffPxPerMm;

            if (gridX[0] < 0 || gridX[2] >= tiffMat.Width ||
                gridY[0] < 0 || gridY[rowCount - 1] >= tiffMat.Height)
            {
                error = "侧边控制网格预测位置超出 TIFF 图像范围。";
                return false;
            }

            // H0 逆映射提供随 Y 倾斜的列趋势；二维码物理距离只校正底部的水平整体偏移，
            // 不强迫 CIS 左右 Mark 列竖直，也不强迫同层左右点具有完全相同的 Y。
            double leftBottomAnchorX = qrAnchor.CenterX - options.CisQrToLeftMarkMm * cisPxPerMmX;
            double rightBottomAnchorX = leftBottomAnchorX + options.CisSideMarkSpanMm * cisPxPerMmX;
            Point2d leftBottomCoarse = ApplyHomography(
                inverseGlobalTransform, new Point2d(gridX[0], gridY[rowCount - 1]));
            Point2d rightBottomCoarse = ApplyHomography(
                inverseGlobalTransform, new Point2d(gridX[2], gridY[rowCount - 1]));
            double leftXCorrection = leftBottomAnchorX - leftBottomCoarse.X;
            double rightXCorrection = rightBottomAnchorX - rightBottomCoarse.X;

            var residuals = new Point2d[rowCount, 3];
            var records = new AlignmentControlPoint[rowCount, 3];
            var leftValid = new bool[rowCount];
            var rightValid = new bool[rowCount];
            leftValid[0] = leftValid[rowCount - 1] = true;
            rightValid[0] = rightValid[rowCount - 1] = true;
            var diagnostics = new List<string>();

            for (int row = 0; row < rowCount; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    // 先为所有网格点建立 H0 粗预测；只有左右内部九层会被实际小圆检测覆盖。
                    var expected = new Point2d(gridX[column], gridY[row]);
                    Point2d coarse = ApplyHomography(inverseGlobalTransform, expected);
                    records[row, column] = new AlignmentControlPoint
                    {
                        RowIndex = row,
                        Column = (AlignmentControlColumn)column,
                        ExpectedTiffPoint = expected,
                        CoarseCisPoint = coarse,
                        DetectedCisPoint = coarse,
                        Residual = new Point2d(0, 0),
                        IsVirtual = row == 0 || row == rowCount - 1 || column == 1,
                        IsDetected = false
                    };
                }
            }

            double tiffDiameter = options.SideMarkDiameterMm * tiffPxPerMm;
            double cisDiameterX = options.SideMarkDiameterMm * cisPxPerMmX;
            double cisDiameterY = options.SideMarkDiameterMm * cisPxPerMmY;

            for (int row = 1; row <= pairCount; row++)
            {
                double markYmm = topYmm + row * stepMm;
                double predictedCisY = qrAnchor.GlobalCenterY -
                                       (bottomYmm - markYmm) * cisPxPerMmY -
                                       qrAnchor.SegmentStartGlobalY;

                for (int side = 0; side < 2; side++)
                {
                    int column = side == 0 ? 0 : 2;
                    var expectedTiff = new Point2d(gridX[column], gridY[row]);
                    Point2d coarseCis = ApplyHomography(inverseGlobalTransform, expectedTiff);
                    double xCorrection = side == 0 ? leftXCorrection : rightXCorrection;
                    var physicalPrediction = new Point2d(coarseCis.X + xCorrection, predictedCisY);

                    SideMarkerDetection tiffDetection = DetectSideMarkerAdaptive(
                        tiffMat, expectedTiff, tiffDiameter, tiffDiameter,
                        tiffPxPerMm, tiffPxPerMm,
                        options.SideMarkInitialSearchMarginMm,
                        options.SideMarkExpandedSearchMarginMm,
                        Math.Min(options.MinCircularityTiff, 0.75));
                    SideMarkerDetection cisDetection = DetectSideMarkerAdaptive(
                        cisGray, physicalPrediction, cisDiameterX, cisDiameterY,
                        cisPxPerMmX, cisPxPerMmY,
                        options.SideMarkInitialSearchMarginMm,
                        options.SideMarkExpandedSearchMarginMm,
                        Math.Min(options.MinCircularityCis, 0.60));

                    var homographyPrediction = new Point2d(coarseCis.X + xCorrection, coarseCis.Y);
                    if (!cisDetection.Found &&
                        Math.Abs(homographyPrediction.Y - physicalPrediction.Y) > 1.0)
                    {
                        // 物理 Y 预测未命中时再尝试 H0 的 Y，兼容二维码尺度估计误差，但不会扩大到全图搜索。
                        SideMarkerDetection fallback = DetectSideMarkerAdaptive(
                            cisGray, homographyPrediction, cisDiameterX, cisDiameterY,
                            cisPxPerMmX, cisPxPerMmY,
                            options.SideMarkInitialSearchMarginMm,
                            options.SideMarkExpandedSearchMarginMm,
                            Math.Min(options.MinCircularityCis, 0.60));
                        if (fallback.Found)
                        {
                            fallback.UsedHomographyFallback = true;
                            cisDetection = fallback;
                        }
                    }

                    bool valid = tiffDetection.Found && cisDetection.Found;
                    Point2d residual = new Point2d(0, 0);
                    if (valid)
                    {
                        // 残差定义在 CIS 源空间：实测点 - H0^-1(TIFF 期望点)。
                        residual = new Point2d(
                            cisDetection.Point.X - coarseCis.X,
                            cisDetection.Point.Y - coarseCis.Y);
                        residuals[row, column] = residual;
                    }

                    if (side == 0)
                        leftValid[row] = valid;
                    else
                        rightValid[row] = valid;

                    AlignmentControlPoint record = records[row, column];
                    record.IsVirtual = false;
                    record.IsDetected = valid;
                    record.Residual = residual;
                    if (tiffDetection.Found)
                        record.DetectedTiffPoint = new Point2d(tiffDetection.Point.X, tiffDetection.Point.Y);
                    if (cisDetection.Found)
                        record.DetectedCisPoint = new Point2d(cisDetection.Point.X, cisDetection.Point.Y);

                    string prefix = side == 0 ? "L" : "R";
                    diagnostics.Add(
                        $"{prefix}{row}: Texp={FormatPoint(expectedTiff)}, " +
                        $"Tdet={(tiffDetection.Found ? FormatPoint(record.DetectedTiffPoint) : "MISS")}, " +
                        $"Cpred={FormatPoint(physicalPrediction)}, " +
                        $"Cdet={(cisDetection.Found ? FormatPoint(record.DetectedCisPoint) : "MISS")}, " +
                        $"R={(valid ? FormatPoint(residual) : "N/A")}, " +
                        $"TiffROI=[{FormatRect(tiffDetection.SearchRect)}], " +
                        $"CisROI=[{FormatRect(cisDetection.SearchRect)}], " +
                        $"expanded={tiffDetection.UsedExpandedWindow || cisDetection.UsedExpandedWindow}, " +
                        $"h0Fallback={cisDetection.UsedHomographyFallback}");
                }
            }

            // 先把与同列上下趋势明显不一致的误检当作缺点，再统一执行有效点/连续缺失检查。
            int leftOutliers = RemoveResidualOutliers(
                residuals, 0, leftValid, gridY, cisPxPerMmX, cisPxPerMmY);
            int rightOutliers = RemoveResidualOutliers(
                residuals, 2, rightValid, gridY, cisPxPerMmX, cisPxPerMmY);
            int leftCount = CountInternalValid(leftValid);
            int rightCount = CountInternalValid(rightValid);
            if (leftCount < options.SideMarkMinValidPerColumn ||
                rightCount < options.SideMarkMinValidPerColumn)
            {
                error =
                    $"侧边有效 Mark 不足：Left={leftCount}/{pairCount}, Right={rightCount}/{pairCount}, " +
                    $"要求每侧至少 {options.SideMarkMinValidPerColumn}。 | " +
                    string.Join(" | ", diagnostics);
                return false;
            }

            if (HasConsecutiveMissing(leftValid) || HasConsecutiveMissing(rightValid))
            {
                error = "侧边 Mark 某一列连续两个内部层缺失。 | " + string.Join(" | ", diagnostics);
                return false;
            }

            // 仅允许孤立缺点用同列上下残差线性补齐；连续缺失已在上方拒绝整个非线性网格。
            FillMissingResiduals(residuals, 0, leftValid, gridY, records);
            FillMissingResiduals(residuals, 2, rightValid, gridY, records);
            for (int row = 1; row <= pairCount; row++)
            {
                records[row, 0].IsDetected = leftValid[row];
                records[row, 0].Residual = residuals[row, 0];
                records[row, 2].IsDetected = rightValid[row];
                records[row, 2].Residual = residuals[row, 2];
            }

            var finalDiagnostics = new List<string>();
            for (int row = 1; row <= pairCount; row++)
            {
                foreach (int column in new[] { 0, 2 })
                {
                    AlignmentControlPoint record = records[row, column];
                    string prefix = column == 0 ? "L" : "R";
                    string state = record.IsInterpolated ? "Interpolated" :
                        record.IsDetected ? "Detected" : "Missing";
                    finalDiagnostics.Add(
                        $"{prefix}{row}:{state}, C={FormatPoint(record.DetectedCisPoint)}, " +
                        $"R={FormatPoint(record.Residual)}");
                }
            }

            if (!ValidateControlGridTopology(
                    inverseGlobalTransform, gridX, gridY, residuals, out string topologyError))
            {
                error = "侧边控制网格质量无效：" + topologyError;
                return false;
            }

            List<double> leaveOneOutErrors = ComputeLeaveOneOutErrors(
                residuals, leftValid, rightValid, gridY, cisPxPerMmX, cisPxPerMmY);
            // 留一指标只写入诊断，不在这里增加新的可调门槛，避免过度复杂化降级条件。
            double leaveOneOutMedian = Median(leaveOneOutErrors);
            double leaveOneOutMaximum = leaveOneOutErrors.Count == 0 ? 0 : leaveOneOutErrors.Max();

            var flatRecords = new List<AlignmentControlPoint>(rowCount * 3);
            for (int row = 0; row < rowCount; row++)
                for (int column = 0; column < 3; column++)
                    flatRecords.Add(records[row, column]);

            grid = new SideGridData
            {
                GridX = gridX,
                GridY = gridY,
                Residuals = residuals,
                ControlPoints = flatRecords,
                LeaveOneOutMedianMm = leaveOneOutMedian,
                LeaveOneOutMaximumMm = leaveOneOutMaximum,
                Diagnostic =
                    $"SideMarks L={leftCount}/{pairCount}, R={rightCount}/{pairCount}, " +
                    $"outliers L={leftOutliers}, R={rightOutliers}, " +
                    $"LOO median={leaveOneOutMedian:F3}mm, max={leaveOneOutMaximum:F3}mm | " +
                    string.Join(" | ", diagnostics) + " | Final: " +
                    string.Join(" | ", finalDiagnostics)
            };
            error = null;
            return true;
        }

        /// <summary>
        /// 用同列上下邻点插值预测当前残差，以中位数/MAD 剔除孤立异常值；返回被剔除点数。
        /// </summary>
        private static int RemoveResidualOutliers(
            Point2d[,] residuals,
            int column,
            bool[] valid,
            double[] gridY,
            double pixelsPerMmX,
            double pixelsPerMmY)
        {
            // 留一预测误差使用物理毫米计算，避免 CIS X/Y 像素分辨率不同导致阈值偏向某一方向。
            var errors = new List<Tuple<int, double>>();
            for (int row = 1; row < valid.Length - 1; row++)
            {
                if (!valid[row])
                    continue;

                int previous = FindPreviousValid(valid, row - 1);
                int next = FindNextValid(valid, row + 1);
                if (previous < 0 || next < 0)
                    continue;

                double t = (gridY[row] - gridY[previous]) /
                           Math.Max(gridY[next] - gridY[previous], 1e-6);
                Point2d predicted = Lerp(residuals[previous, column], residuals[next, column], t);
                errors.Add(Tuple.Create(
                    row,
                    ResidualDistanceMm(
                        residuals[row, column], predicted, pixelsPerMmX, pixelsPerMmY)));
            }

            if (errors.Count < 5)
                return 0;

            double median = Median(errors.Select(item => item.Item2));
            double mad = Median(errors.Select(item => Math.Abs(item.Item2 - median)));
            double threshold = Math.Max(2.0, median + 3.0 * Math.Max(mad, 0.25));
            int removed = 0;
            foreach (Tuple<int, double> item in errors)
            {
                if (item.Item2 <= threshold)
                    continue;
                valid[item.Item1] = false;
                residuals[item.Item1, column] = new Point2d(0, 0);
                removed++;
            }
            return removed;
        }

        private static int CountInternalValid(bool[] valid)
        {
            int count = 0;
            for (int row = 1; row < valid.Length - 1; row++)
                if (valid[row])
                    count++;
            return count;
        }

        private static bool HasConsecutiveMissing(bool[] valid)
        {
            for (int row = 1; row < valid.Length - 2; row++)
                if (!valid[row] && !valid[row + 1])
                    return true;
            return false;
        }

        /// <summary>只对已经通过“无连续缺点”检查的孤立缺层进行同列线性插值。</summary>
        private static void FillMissingResiduals(
            Point2d[,] residuals,
            int column,
            bool[] valid,
            double[] gridY,
            AlignmentControlPoint[,] records)
        {
            for (int row = 1; row < valid.Length - 1; row++)
            {
                if (valid[row])
                    continue;

                int previous = FindPreviousValid(valid, row - 1);
                int next = FindNextValid(valid, row + 1);
                if (previous < 0 || next < 0)
                    continue;

                double t = (gridY[row] - gridY[previous]) /
                           Math.Max(gridY[next] - gridY[previous], 1e-6);
                Point2d residual = Lerp(residuals[previous, column], residuals[next, column], t);
                residuals[row, column] = residual;
                AlignmentControlPoint record = records[row, column];
                record.Residual = residual;
                record.DetectedCisPoint = new Point2d(
                    record.CoarseCisPoint.X + residual.X,
                    record.CoarseCisPoint.Y + residual.Y);
                record.IsInterpolated = true;
            }
        }

        private static int FindPreviousValid(bool[] valid, int start)
        {
            for (int row = start; row >= 0; row--)
                if (valid[row])
                    return row;
            return -1;
        }

        private static int FindNextValid(bool[] valid, int start)
        {
            for (int row = start; row < valid.Length; row++)
                if (valid[row])
                    return row;
            return -1;
        }

        /// <summary>逐个隐藏实测内部点，用上下邻点预测并统计毫米误差，避免用建模点自评精度。</summary>
        private static List<double> ComputeLeaveOneOutErrors(
            Point2d[,] residuals,
            bool[] leftValid,
            bool[] rightValid,
            double[] gridY,
            double pixelsPerMmX,
            double pixelsPerMmY)
        {
            var errors = new List<double>();
            AddColumnLeaveOneOutErrors(
                residuals, 0, leftValid, gridY, pixelsPerMmX, pixelsPerMmY, errors);
            AddColumnLeaveOneOutErrors(
                residuals, 2, rightValid, gridY, pixelsPerMmX, pixelsPerMmY, errors);
            return errors;
        }

        private static void AddColumnLeaveOneOutErrors(
            Point2d[,] residuals,
            int column,
            bool[] valid,
            double[] gridY,
            double pixelsPerMmX,
            double pixelsPerMmY,
            ICollection<double> output)
        {
            for (int row = 1; row < valid.Length - 1; row++)
            {
                if (!valid[row])
                    continue;

                int previous = FindPreviousValid(valid, row - 1);
                int next = FindNextValid(valid, row + 1);
                if (previous < 0 || next < 0)
                    continue;

                double t = (gridY[row] - gridY[previous]) /
                           Math.Max(gridY[next] - gridY[previous], 1e-6);
                Point2d predicted = Lerp(residuals[previous, column], residuals[next, column], t);
                output.Add(ResidualDistanceMm(
                    residuals[row, column], predicted, pixelsPerMmX, pixelsPerMmY));
            }
        }

        private static double ResidualDistanceMm(
            Point2d first,
            Point2d second,
            double pixelsPerMmX,
            double pixelsPerMmY)
        {
            double dx = (first.X - second.X) / Math.Max(pixelsPerMmX, 1e-6);
            double dy = (first.Y - second.Y) / Math.Max(pixelsPerMmY, 1e-6);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point2d Lerp(Point2d first, Point2d second, double t)
        {
            return new Point2d(
                first.X + (second.X - first.X) * t,
                first.Y + (second.Y - first.Y) * t);
        }

        /// <summary>验证残差网格映射后仍保持左右/上下顺序，且每个网格单元无翻折和突变。</summary>
        private static bool ValidateControlGridTopology(
            Mat inverseTransform,
            double[] gridX,
            double[] gridY,
            Point2d[,] residuals,
            out string error)
        {
            // 把网格逆映射到 CIS 后检查点序、单元有向面积和相邻尺度变化，
            // 防止错误 Mark 生成局部翻折或突变，即使单点残差看起来并不大。
            int rows = gridY.Length;
            int columns = gridX.Length;
            var source = new Point2d[rows, columns];
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    Point2d coarse = ApplyHomography(
                        inverseTransform, new Point2d(gridX[column], gridY[row]));
                    source[row, column] = new Point2d(
                        coarse.X + residuals[row, column].X,
                        coarse.Y + residuals[row, column].Y);
                    if (!IsFinitePoint(source[row, column]))
                    {
                        error = $"控制点 ({row},{column}) 包含非有限数值。";
                        return false;
                    }
                }
            }

            for (int row = 0; row < rows; row++)
            {
                if (!(source[row, 0].X < source[row, 1].X &&
                      source[row, 1].X < source[row, 2].X))
                {
                    error = $"第 {row} 层控制点左右顺序发生翻转。";
                    return false;
                }
            }

            for (int column = 0; column < columns; column++)
            {
                for (int row = 0; row < rows - 1; row++)
                {
                    if (source[row + 1, column].Y <= source[row, column].Y)
                    {
                        error = $"第 {column} 列第 {row}/{row + 1} 层控制点 Y 顺序发生翻转。";
                        return false;
                    }
                }
            }

            var jacobianScales = new double[rows - 1, columns - 1];
            for (int row = 0; row < rows - 1; row++)
            {
                for (int column = 0; column < columns - 1; column++)
                {
                    Point2d[] targetCell =
                    {
                        new Point2d(gridX[column], gridY[row]),
                        new Point2d(gridX[column + 1], gridY[row]),
                        new Point2d(gridX[column + 1], gridY[row + 1]),
                        new Point2d(gridX[column], gridY[row + 1])
                    };
                    Point2d[] sourceCell =
                    {
                        source[row, column], source[row, column + 1],
                        source[row + 1, column + 1], source[row + 1, column]
                    };
                    double targetArea = SignedPolygonArea(targetCell);
                    double sourceArea = SignedPolygonArea(sourceCell);
                    double ratio = Math.Abs(sourceArea) / Math.Max(Math.Abs(targetArea), 1e-6);
                    if (targetArea * sourceArea <= 0 || ratio < 0.2 || ratio > 5.0)
                    {
                        error = $"控制网格单元 ({row},{column}) 翻折或尺度异常，ratio={ratio:F3}。";
                        return false;
                    }
                    jacobianScales[row, column] = ratio;
                }
            }

            for (int row = 0; row < rows - 1; row++)
            {
                for (int column = 0; column < columns - 1; column++)
                {
                    if (row > 0 && HasAbruptScaleChange(
                            jacobianScales[row, column], jacobianScales[row - 1, column]))
                    {
                        error = $"控制网格单元 ({row},{column}) 与上一层 Jacobian 尺度变化过大。";
                        return false;
                    }
                    if (column > 0 && HasAbruptScaleChange(
                            jacobianScales[row, column], jacobianScales[row, column - 1]))
                    {
                        error = $"控制网格单元 ({row},{column}) 与左侧 Jacobian 尺度变化过大。";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static bool HasAbruptScaleChange(double first, double second)
        {
            double minimum = Math.Min(Math.Abs(first), Math.Abs(second));
            double maximum = Math.Max(Math.Abs(first), Math.Abs(second));
            return minimum <= 1e-9 || maximum / minimum > MaximumAdjacentJacobianScaleRatio;
        }

        private static double SignedPolygonArea(IReadOnlyList<Point2d> points)
        {
            double twiceArea = 0;
            for (int index = 0; index < points.Count; index++)
            {
                Point2d current = points[index];
                Point2d next = points[(index + 1) % points.Count];
                twiceArea += current.X * next.Y - next.X * current.Y;
            }
            return twiceArea * 0.5;
        }

        private static Point2d ApplyHomography(Mat transform, Point2d point)
        {
            double denominator = transform.At<double>(2, 0) * point.X +
                                 transform.At<double>(2, 1) * point.Y +
                                 transform.At<double>(2, 2);
            if (Math.Abs(denominator) < 1e-12)
                return new Point2d(double.NaN, double.NaN);
            return new Point2d(
                (transform.At<double>(0, 0) * point.X +
                 transform.At<double>(0, 1) * point.Y + transform.At<double>(0, 2)) / denominator,
                (transform.At<double>(1, 0) * point.X +
                 transform.At<double>(1, 1) * point.Y + transform.At<double>(1, 2)) / denominator);
        }

        private static bool IsFinitePoint(Point2d point)
        {
            return !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
                   !double.IsNaN(point.Y) && !double.IsInfinity(point.Y);
        }

        /// <summary>校验全局对准所需图像、二维码锚点和物理参数，并检查锚点确实位于当前拼接段。</summary>
    }
}
