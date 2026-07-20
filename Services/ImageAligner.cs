using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 根据物理位置约束提取圆形 Mark，并计算 CIS 实拍图到 TIFF 排版图的变换。
    /// 上下两排 20 mm Mark 始终负责全局 H0；启用开关后，左右 4 mm Mark 只描述
    /// H0 无法解释的边缘残差，不参与 H0 拟合，也不会改变中心列的全局基准。
    /// </summary>
    public class ImageAligner
    {
        private const int MinimumPointsPerRow = 2;
        private const double MaximumRowScaleDifference = 0.15;
        private const double MaximumAdjacentJacobianScaleRatio = 2.0;

        /// <summary>统一描述 TIFF/CIS 中的圆形候选；Score 越小表示越接近期望位置和尺寸。</summary>
        public class MarkerPoint
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Area { get; set; }
            public double Circularity { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Score { get; set; }
        }

        /// <summary>一排大 Mark 在两个图像坐标系中的预测几何信息。</summary>
        private sealed class MarkerRegionSpec
        {
            public string Name { get; set; }
            public double TiffCenterY { get; set; }
            public double CisCenterY { get; set; }
            public double TiffDiameterPixels { get; set; }
            public double CisDiameterPixels { get; set; }
            public double TiffPixelsPerMm { get; set; }
            public double CisPixelsPerMm { get; set; }
        }

        /// <summary>单排自适应检测结果，同时保留阈值和最终搜索窗口用于诊断。</summary>
        private sealed class RowDetectionResult
        {
            public List<MarkerPoint> Points { get; set; } = new List<MarkerPoint>();
            public int Threshold { get; set; } = 127;
            public Rect SearchRect { get; set; }
            public bool UsedExpandedWindow { get; set; }
        }

        /// <summary>单个侧边小 Mark 的检测状态；失败原因用于决定是否降级到 H0。</summary>
        private sealed class SideMarkerDetection
        {
            public MarkerPoint Point { get; set; }
            public Rect SearchRect { get; set; }
            public bool UsedExpandedWindow { get; set; }
            public bool UsedHomographyFallback { get; set; }
            public string Error { get; set; }
            public bool Found => Point != null;
        }

        /// <summary>通过质量门控后的三列残差网格及留一验证指标。</summary>
        private sealed class SideGridData
        {
            public double[] GridX { get; set; }
            public double[] GridY { get; set; }
            public Point2d[,] Residuals { get; set; }
            public List<AlignmentControlPoint> ControlPoints { get; set; }
            public double LeaveOneOutMedianMm { get; set; }
            public double LeaveOneOutMaximumMm { get; set; }
            public string Diagnostic { get; set; }
        }

        /// <summary>
        /// 计算 CIS 实拍图到 TIFF 排版图的变换矩阵。
        /// 第二个二维码的全局 Y 是 CIS 下排 Mark 圆心的权威坐标；提取 ROI 时，
        /// 通过减去拼接段全局起始 Y 转换为 cisMat 内的局部坐标。
        /// </summary>
        public static AlignmentResult ComputeTransform(
            Mat cisMat,
            Mat tiffMat,
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            out int optimalThresh,
            out string diagnostic)
        {
            optimalThresh = 127;
            diagnostic = null;

            if (!ValidateInputs(cisMat, tiffMat, qrAnchor, options, out diagnostic))
                return null;

            Stopwatch detectionWatch = Stopwatch.StartNew();

            // TIFF 使用固定 DPI；CIS 的纵向比例由已知 60 mm 二维码在当前处理图中的像素高度标定。
            double tiffPixelsPerMm = options.LayoutDpi / 25.4;
            double cisPixelsPerMm = qrAnchor.PixelHeight / options.QrPhysicalHeightMm;
            // 二维码 Y 是连续采集坐标，减去段起点后才是本张 cisMat 内的局部行号。
            double cisBottomCenterY = qrAnchor.GlobalCenterY - qrAnchor.SegmentStartGlobalY;
            double cisTopCenterY = cisBottomCenterY - options.CisRowSpacingMm * cisPixelsPerMm;

            var regions = new List<MarkerRegionSpec>
            {
                new MarkerRegionSpec
                {
                    Name = "Top",
                    TiffCenterY = options.TiffTopCenterYmm * tiffPixelsPerMm,
                    CisCenterY = cisTopCenterY,
                    TiffDiameterPixels = options.MarkDiameterMm * tiffPixelsPerMm,
                    CisDiameterPixels = options.MarkDiameterMm * cisPixelsPerMm,
                    TiffPixelsPerMm = tiffPixelsPerMm,
                    CisPixelsPerMm = cisPixelsPerMm
                },
                new MarkerRegionSpec
                {
                    Name = "Bottom",
                    TiffCenterY = (options.TiffHeightMm - options.TiffBottomOffsetMm) * tiffPixelsPerMm,
                    CisCenterY = cisBottomCenterY,
                    TiffDiameterPixels = options.MarkDiameterMm * tiffPixelsPerMm,
                    CisDiameterPixels = options.MarkDiameterMm * cisPixelsPerMm,
                    TiffPixelsPerMm = tiffPixelsPerMm,
                    CisPixelsPerMm = cisPixelsPerMm
                }
            };

            foreach (MarkerRegionSpec region in regions)
            {
                if (region.TiffCenterY < 0 || region.TiffCenterY >= tiffMat.Height)
                {
                    diagnostic =
                        $"TIFF {region.Name} 预测圆心 Y={region.TiffCenterY:F1} " +
                        $"超出图像高度 {tiffMat.Height}。";
                    return null;
                }

                if (region.CisCenterY < 0 || region.CisCenterY >= cisMat.Height)
                {
                    diagnostic =
                        $"CIS {region.Name} 预测圆心 Y={region.CisCenterY:F1} " +
                        $"超出图像高度 {cisMat.Height}。";
                    return null;
                }
            }

            using (Mat cisGray = ConvertToGray(cisMat))
            {
                var allTiffPoints = new List<Point2f>();
                var allCisPoints = new List<Point2f>();
                var globalMarkPoints = new List<AlignmentGlobalMarkPoint>();
                var rowScaleValues = new List<double>();
                var rowDiagnostics = new List<string>();
                double referenceArea = 0;

                for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
                {
                    MarkerRegionSpec region = regions[regionIndex];

                    RowDetectionResult tiffRow = DetectTiffAdaptive(tiffMat, region, options);
                    RowDetectionResult cisRow = DetectCisAdaptive(cisGray, region, options, referenceArea);

                    if (regionIndex == 0 && cisRow.Points.Count >= 3)
                    {
                        // 顶排偶尔会把大块反光/背景连通域纳入候选，使用本排中位面积剔除明显巨型轮廓。
                        double medianArea = Median(cisRow.Points.Select(p => p.Area));
                        cisRow.Points = cisRow.Points.Where(p => p.Area < medianArea * 2.5).ToList();
                    }

                    if (regionIndex == 0 && cisRow.Points.Count > 0)
                    {
                        // 顶排建立本次曝光下的面积/阈值参考，底排复用它抑制尺寸完全不同的伪圆。
                        referenceArea = Median(cisRow.Points.Select(p => p.Area));
                        optimalThresh = cisRow.Threshold;
                    }

                    // 不直接按候选数量或绝对 X 配对，而按一排 Mark 的归一化横向次序寻找对应关系。
                    var matched = MatchRows(tiffRow.Points, cisRow.Points);
                    int matchedCount = Math.Min(matched.Item1.Count, matched.Item2.Count);
                    rowDiagnostics.Add(
                        $"{region.Name}: TIFF={tiffRow.Points.Count}, CIS={cisRow.Points.Count}, " +
                        $"Matched={matchedCount}, TIFF-ROI={FormatRect(tiffRow.SearchRect)}, " +
                        $"CIS-ROI={FormatRect(cisRow.SearchRect)}, " +
                        $"Expanded={(tiffRow.UsedExpandedWindow || cisRow.UsedExpandedWindow)}");

                    if (matchedCount < MinimumPointsPerRow)
                    {
                        diagnostic =
                            $"{region.Name} 排有效对应 Mark 少于 {MinimumPointsPerRow} 个。" +
                            string.Join(" | ", rowDiagnostics);
                        return null;
                    }

                    double rowScale = EstimateHorizontalScale(matched.Item1, matched.Item2);
                    if (!double.IsNaN(rowScale) && !double.IsInfinity(rowScale))
                        rowScaleValues.Add(rowScale);

                    allTiffPoints.AddRange(matched.Item1.Select(p => new Point2f((float)p.X, (float)p.Y)));
                    allCisPoints.AddRange(matched.Item2.Select(p => new Point2f((float)p.X, (float)p.Y)));
                    for (int pointIndex = 0; pointIndex < matchedCount; pointIndex++)
                    {
                        globalMarkPoints.Add(new AlignmentGlobalMarkPoint
                        {
                            RowName = region.Name,
                            Index = pointIndex + 1,
                            TiffPoint = new Point2d(matched.Item1[pointIndex].X, matched.Item1[pointIndex].Y),
                            CisPoint = new Point2d(matched.Item2[pointIndex].X, matched.Item2[pointIndex].Y)
                        });
                    }
                }

                if (allTiffPoints.Count < 4 || allCisPoints.Count < 4)
                {
                    diagnostic = "有效对应点少于 4 个，无法计算稳定的二维变换。";
                    return null;
                }

                if (rowScaleValues.Count >= 2)
                {
                    // 上下排横向尺度应基本一致；差异过大通常意味着一排发生错配，而不是合理透视。
                    double scaleDifference = Math.Abs(rowScaleValues[0] - rowScaleValues[1]) /
                                             Math.Max(Math.Abs(rowScaleValues[0]), 1e-6);
                    if (scaleDifference > MaximumRowScaleDifference)
                    {
                        diagnostic =
                            $"上下排 Mark 的横向尺度差异 {scaleDifference:P1} 超过允许值 " +
                            $"{MaximumRowScaleDifference:P0}。" + string.Join(" | ", rowDiagnostics);
                        return null;
                    }
                }

                // H0 方向固定为 CIS→TIFF；Remap 需要的 TIFF→CIS 逆矩阵在此一次求出并随结果持有。
                Mat transform = ComputeRobustTransform(allCisPoints, allTiffPoints);
                if (transform == null || transform.Empty() || !IsFiniteTransform(transform))
                {
                    transform?.Dispose();
                    diagnostic = "RANSAC 未能计算出有效的 CIS→TIFF 变换矩阵。" +
                                 string.Join(" | ", rowDiagnostics);
                    return null;
                }

                Mat inverseTransform = transform.Inv();
                if (inverseTransform == null || inverseTransform.Empty() || !IsFiniteTransform(inverseTransform))
                {
                    inverseTransform?.Dispose();
                    transform.Dispose();
                    diagnostic = "无法计算有效的 TIFF→CIS 逆变换矩阵。";
                    return null;
                }

                string globalDiagnostic =
                    $"QR(globalY={qrAnchor.GlobalCenterY}, segmentStart={qrAnchor.SegmentStartGlobalY}, " +
                    $"localY={cisBottomCenterY:F1}, height={qrAnchor.PixelHeight:F1}, " +
                    $"cisPxPerMm={cisPixelsPerMm:F4}) | " + string.Join(" | ", rowDiagnostics);

                AlignmentResult result = null;
                try
                {
                    result = BuildAlignmentResult(
                        cisGray, tiffMat, qrAnchor, options, transform, inverseTransform,
                        globalMarkPoints, out string nonlinearDiagnostic);
                    detectionWatch.Stop();
                    result.DetectionMilliseconds = detectionWatch.Elapsed.TotalMilliseconds;
                    result.PeakWorkingSetBytes = GetPeakWorkingSetBytes();
                    diagnostic = globalDiagnostic + " | " + nonlinearDiagnostic;
                    result.Diagnostic = diagnostic;
                    return result;
                }
                catch
                {
                    if (result == null)
                    {
                        inverseTransform.Dispose();
                        transform.Dispose();
                    }
                    throw;
                }
            }
        }

        private static AlignmentResult BuildAlignmentResult(
            Mat cisGray,
            Mat tiffMat,
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            Mat globalTransform,
            Mat inverseGlobalTransform,
            IList<AlignmentGlobalMarkPoint> globalMarkPoints,
            out string diagnostic)
        {
            var noControlPoints = new List<AlignmentControlPoint>();
            // 侧边功能是可选增强项：关闭或质量检查失败时都显式返回 GlobalOnly，
            // 保证已有上下 Mark 的 H0 路径可继续运行，并通过质量状态/诊断信息暴露降级原因。
            if (!options.EnableSideMarkNonlinearAlignment)
            {
                diagnostic = "Nonlinear=DisabledByConfig: 侧边 4 mm Mark 功能已关闭，仅使用上下两排 20 mm Mark 计算 H0。";
                return new AlignmentResult(
                    globalTransform, inverseGlobalTransform,
                    AlignmentMode.GlobalOnly, AlignmentQualityStatus.Passed,
                    null, null, null, noControlPoints, globalMarkPoints,
                    options.NonlinearRemapStripeRows);
            }

            if (!ValidateSideAlignmentInputs(qrAnchor, options, out string validationError))
            {
                diagnostic = "Nonlinear=GlobalOnly: " + validationError;
                return new AlignmentResult(
                    globalTransform, inverseGlobalTransform,
                    AlignmentMode.GlobalOnly, AlignmentQualityStatus.Degraded,
                    null, null, null, noControlPoints, globalMarkPoints,
                    options.NonlinearRemapStripeRows);
            }

            try
            {
                if (!TryBuildSideGrid(
                        cisGray, tiffMat, qrAnchor, options, inverseGlobalTransform,
                        out SideGridData sideGrid, out string sideError))
                {
                    diagnostic = "Nonlinear=GlobalOnly: " + sideError;
                    return new AlignmentResult(
                        globalTransform, inverseGlobalTransform,
                        AlignmentMode.GlobalOnly, AlignmentQualityStatus.Degraded,
                        null, null, null, noControlPoints, globalMarkPoints,
                        options.NonlinearRemapStripeRows);
                }

                var result = new AlignmentResult(
                    globalTransform, inverseGlobalTransform,
                    AlignmentMode.Nonlinear, AlignmentQualityStatus.Passed,
                    sideGrid.GridX, sideGrid.GridY, sideGrid.Residuals,
                    sideGrid.ControlPoints, globalMarkPoints,
                    options.NonlinearRemapStripeRows)
                {
                    LeaveOneOutMedianMm = sideGrid.LeaveOneOutMedianMm,
                    LeaveOneOutMaximumMm = sideGrid.LeaveOneOutMaximumMm
                };
                diagnostic = "Nonlinear=Enabled: " + sideGrid.Diagnostic;
                return result;
            }
            catch (Exception ex)
            {
                diagnostic = "Nonlinear=GlobalOnly: 侧边非线性网格构建异常：" + ex.Message;
                return new AlignmentResult(
                    globalTransform, inverseGlobalTransform,
                    AlignmentMode.GlobalOnly, AlignmentQualityStatus.Degraded,
                    null, null, null, noControlPoints, globalMarkPoints,
                    options.NonlinearRemapStripeRows);
            }
        }

        /// <summary>验证侧边增强专用参数；失败不否定 H0，只阻止构建非线性网格。</summary>
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
        private static bool ValidateInputs(
            Mat cisMat,
            Mat tiffMat,
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            out string error)
        {
            if (cisMat == null || cisMat.Empty())
            {
                error = "CIS 图像为空。";
                return false;
            }

            if (tiffMat == null || tiffMat.Empty())
            {
                error = "TIFF 图像为空。";
                return false;
            }

            if (qrAnchor == null)
            {
                error = "缺少第二个二维码的全局坐标锚点。";
                return false;
            }

            if (options == null)
            {
                error = "缺少 Mark 配准参数。";
                return false;
            }

            double[] numericOptions =
            {
                options.LayoutDpi,
                options.TiffHeightMm,
                options.TiffTopCenterYmm,
                options.TiffBottomOffsetMm,
                options.MarkDiameterMm,
                options.CisRowSpacingMm,
                options.QrPhysicalHeightMm,
                options.InitialSearchMarginMm,
                options.ExpandedSearchMarginMm,
                options.MinCircularityTiff,
                options.MinCircularityCis
            };
            if (numericOptions.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
            {
                error = "Mark 配准参数包含 NaN 或无穷大。";
                return false;
            }

            if (qrAnchor.GlobalCenterY < 0 || qrAnchor.SegmentStartGlobalY < 0)
            {
                error = "二维码全局 Y 或拼接段起始全局 Y 无效。";
                return false;
            }

            if (qrAnchor.PixelHeight <= 1 || double.IsNaN(qrAnchor.PixelHeight) || double.IsInfinity(qrAnchor.PixelHeight))
            {
                error = $"第二个二维码像素高度无效：{qrAnchor.PixelHeight:F3}。";
                return false;
            }

            if (options.LayoutDpi <= 0 || options.TiffHeightMm <= 0 ||
                options.TiffTopCenterYmm < 0 || options.TiffBottomOffsetMm < 0 ||
                options.TiffBottomOffsetMm >= options.TiffHeightMm ||
                options.MarkDiameterMm <= 0 || options.CisRowSpacingMm <= 0 ||
                options.QrPhysicalHeightMm <= 0 || options.InitialSearchMarginMm < 0 ||
                options.ExpandedSearchMarginMm < options.InitialSearchMarginMm ||
                options.MinCircularityTiff <= 0 || options.MinCircularityTiff > 1 ||
                options.MinCircularityCis <= 0 || options.MinCircularityCis > 1)
            {
                error = "Mark 配准物理参数或圆度阈值无效。";
                return false;
            }

            double centerYInSegment = qrAnchor.CenterYInSegment;
            if (centerYInSegment < 0 || centerYInSegment >= cisMat.Height)
            {
                error =
                    $"第二个二维码全局 Y 转换后的图内坐标 {centerYInSegment:F1} " +
                    $"超出 CIS 高度 {cisMat.Height}。";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>返回独立灰度 Mat；即使输入已是单通道也复制，调用方始终拥有返回值。</summary>
        private static Mat ConvertToGray(Mat source)
        {
            var gray = new Mat();
            if (source.Channels() == 1)
                source.CopyTo(gray);
            else if (source.Channels() == 4)
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
            else if (source.Channels() == 3)
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
            else
            {
                gray.Dispose();
                throw new ArgumentException($"不支持的 CIS 通道数：{source.Channels()}。");
            }
            return gray;
        }

        /// <summary>先在物理预测的小条带检测 TIFF 大圆，点数不足时才扩大条带，减少干扰轮廓。</summary>
        private static RowDetectionResult DetectTiffAdaptive(
            Mat image,
            MarkerRegionSpec region,
            MarkAlignmentOptions options)
        {
            Rect initialRect = BuildSearchRect(
                image.Size(), region.TiffCenterY, region.TiffDiameterPixels,
                region.TiffPixelsPerMm, options.InitialSearchMarginMm, region.Name, "TIFF");

            List<MarkerPoint> points;
            using (var roi = new Mat(image, initialRect))
            {
                points = DetectTiff(
                    roi, initialRect.Y, options.MinCircularityTiff,
                    region.TiffDiameterPixels, region.TiffCenterY);
            }

            var result = new RowDetectionResult { Points = points, SearchRect = initialRect };
            if (points.Count >= MinimumPointsPerRow ||
                options.ExpandedSearchMarginMm <= options.InitialSearchMarginMm)
                return result;

            Rect expandedRect = BuildSearchRect(
                image.Size(), region.TiffCenterY, region.TiffDiameterPixels,
                region.TiffPixelsPerMm, options.ExpandedSearchMarginMm, region.Name, "TIFF");
            using (var roi = new Mat(image, expandedRect))
            {
                result.Points = DetectTiff(
                    roi, expandedRect.Y, options.MinCircularityTiff,
                    region.TiffDiameterPixels, region.TiffCenterY);
            }
            result.SearchRect = expandedRect;
            result.UsedExpandedWindow = true;
            return result;
        }

        /// <summary>在 CIS 预测条带执行多阈值大圆检测，并在必要时使用扩展窗口重试。</summary>
        private static RowDetectionResult DetectCisAdaptive(
            Mat imageGray,
            MarkerRegionSpec region,
            MarkAlignmentOptions options,
            double referenceArea)
        {
            Rect initialRect = BuildSearchRect(
                imageGray.Size(), region.CisCenterY, region.CisDiameterPixels,
                region.CisPixelsPerMm, options.InitialSearchMarginMm, region.Name, "CIS");

            Tuple<List<MarkerPoint>, int> detected;
            using (var roi = new Mat(imageGray, initialRect))
            {
                detected = DetectJpg(
                    roi, initialRect.Y, options.MinCircularityCis, referenceArea,
                    region.CisDiameterPixels, region.CisCenterY);
            }

            var result = new RowDetectionResult
            {
                Points = detected.Item1,
                Threshold = detected.Item2,
                SearchRect = initialRect
            };

            if (result.Points.Count >= MinimumPointsPerRow ||
                options.ExpandedSearchMarginMm <= options.InitialSearchMarginMm)
                return result;

            Rect expandedRect = BuildSearchRect(
                imageGray.Size(), region.CisCenterY, region.CisDiameterPixels,
                region.CisPixelsPerMm, options.ExpandedSearchMarginMm, region.Name, "CIS");
            using (var roi = new Mat(imageGray, expandedRect))
            {
                detected = DetectJpg(
                    roi, expandedRect.Y, options.MinCircularityCis, referenceArea,
                    region.CisDiameterPixels, region.CisCenterY);
            }
            result.Points = detected.Item1;
            result.Threshold = detected.Item2;
            result.SearchRect = expandedRect;
            result.UsedExpandedWindow = true;
            return result;
        }

        /// <summary>检测单个 4 mm 侧边 Mark；仅首次 ROI 失败时扩大窗口，不进行全图搜索。</summary>
        private static SideMarkerDetection DetectSideMarkerAdaptive(
            Mat image,
            Point2d expectedCenter,
            double expectedDiameterX,
            double expectedDiameterY,
            double pixelsPerMmX,
            double pixelsPerMmY,
            double initialMarginMm,
            double expandedMarginMm,
            double minimumCircularity)
        {
            var result = new SideMarkerDetection();
            if (!IsFinitePoint(expectedCenter) ||
                expectedCenter.X < 0 || expectedCenter.X >= image.Width ||
                expectedCenter.Y < 0 || expectedCenter.Y >= image.Height)
            {
                result.Error =
                    $"预测中心 ({expectedCenter.X:F1},{expectedCenter.Y:F1}) 超出图像范围。";
                return result;
            }

            Rect initialRect = BuildPointSearchRect(
                image.Size(), expectedCenter, expectedDiameterX, expectedDiameterY,
                pixelsPerMmX, pixelsPerMmY, initialMarginMm);
            result.SearchRect = initialRect;
            result.Point = DetectBestSideMarker(
                image, initialRect, expectedCenter,
                expectedDiameterX, expectedDiameterY, minimumCircularity);
            if (result.Found || expandedMarginMm <= initialMarginMm)
                return result;

            Rect expandedRect = BuildPointSearchRect(
                image.Size(), expectedCenter, expectedDiameterX, expectedDiameterY,
                pixelsPerMmX, pixelsPerMmY, expandedMarginMm);
            result.SearchRect = expandedRect;
            result.UsedExpandedWindow = true;
            result.Point = DetectBestSideMarker(
                image, expandedRect, expectedCenter,
                expectedDiameterX, expectedDiameterY, minimumCircularity);
            if (!result.Found)
                result.Error = $"ROI {FormatRect(expandedRect)} 未找到满足条件的侧边 Mark。";
            return result;
        }

        /// <summary>按 X/Y 独立像素比例把圆直径和毫米余量换算为侧边小 ROI，并裁剪到图像边界。</summary>
        private static Rect BuildPointSearchRect(
            Size imageSize,
            Point2d center,
            double diameterX,
            double diameterY,
            double pixelsPerMmX,
            double pixelsPerMmY,
            double marginMm)
        {
            double halfWidth = diameterX * 0.5 + marginMm * pixelsPerMmX;
            double halfHeight = diameterY * 0.5 + marginMm * pixelsPerMmY;
            int x0 = Math.Max(0, (int)Math.Floor(center.X - halfWidth));
            int x1 = Math.Min(imageSize.Width, (int)Math.Ceiling(center.X + halfWidth));
            int y0 = Math.Max(0, (int)Math.Floor(center.Y - halfHeight));
            int y1 = Math.Min(imageSize.Height, (int)Math.Ceiling(center.Y + halfHeight));
            if (x1 <= x0 || y1 <= y0)
                throw new ArgumentOutOfRangeException(nameof(center), "侧边 Mark 搜索窗口为空。 ");
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// 对 ROI 做局部对比度增强，并同时尝试明/暗两种极性；候选综合位置、面积、尺寸和圆度评分。
        /// </summary>
        private static MarkerPoint DetectBestSideMarker(
            Mat image,
            Rect searchRect,
            Point2d expectedCenter,
            double expectedDiameterX,
            double expectedDiameterY,
            double minimumCircularity)
        {
            using (var roi = new Mat(image, searchRect))
            using (Mat gray = ConvertToGray(roi))
            using (var enhanced = new Mat())
            using (var blurred = new Mat())
            using (var binary = new Mat())
            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3)))
            {
                using (CLAHE clahe = Cv2.CreateCLAHE(2.0, new Size(4, 4)))
                    clahe.Apply(gray, enhanced);
                Cv2.GaussianBlur(enhanced, blurred, new Size(3, 3), 0);

                MarkerPoint best = null;
                // TIFF 与 CIS 的前景极性可能不同，双极性检测可避免把材料亮暗约定写死。
                for (int polarity = 0; polarity < 2; polarity++)
                {
                    ThresholdTypes thresholdType = ThresholdTypes.Otsu |
                        (polarity == 0 ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv);
                    Cv2.Threshold(blurred, binary, 0, 255, thresholdType);

                    int border = Math.Min(3, Math.Max(1, Math.Min(binary.Width, binary.Height) / 20));
                    Cv2.Rectangle(binary, new Rect(0, 0, binary.Width, border), Scalar.Black, -1);
                    Cv2.Rectangle(binary, new Rect(0, binary.Height - border, binary.Width, border), Scalar.Black, -1);
                    Cv2.Rectangle(binary, new Rect(0, 0, border, binary.Height), Scalar.Black, -1);
                    Cv2.Rectangle(binary, new Rect(binary.Width - border, 0, border, binary.Height), Scalar.Black, -1);
                    Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel, null, 1);

                    Cv2.FindContours(
                        binary, out Point[][] contours, out HierarchyIndex[] _,
                        RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    foreach (Point[] contour in contours)
                    {
                        double area = Cv2.ContourArea(contour);
                        double perimeter = Cv2.ArcLength(contour, true);
                        if (area <= 0 || perimeter <= 0)
                            continue;

                        double expectedArea = Math.PI * expectedDiameterX * expectedDiameterY * 0.25;
                        if (area < expectedArea * 0.2 || area > expectedArea * 3.0)
                            continue;

                        Rect bounds = Cv2.BoundingRect(contour);
                        if (bounds.Width < expectedDiameterX * 0.35 ||
                            bounds.Width > expectedDiameterX * 2.2 ||
                            bounds.Height < expectedDiameterY * 0.35 ||
                            bounds.Height > expectedDiameterY * 2.2)
                            continue;

                        double circularity = 4.0 * Math.PI * area / (perimeter * perimeter);
                        if (circularity < minimumCircularity)
                            continue;

                        Moments moments = Cv2.Moments(contour);
                        if (Math.Abs(moments.M00) < double.Epsilon)
                            continue;

                        double centerX = moments.M10 / moments.M00 + searchRect.X;
                        double centerY = moments.M01 / moments.M00 + searchRect.Y;
                        double normalizedX = (centerX - expectedCenter.X) /
                                             Math.Max(searchRect.Width * 0.5, 1.0);
                        double normalizedY = (centerY - expectedCenter.Y) /
                                             Math.Max(searchRect.Height * 0.5, 1.0);
                        double areaError = Math.Abs(Math.Log(Math.Max(area / expectedArea, 1e-6)));
                        double sizeError =
                            Math.Abs(bounds.Width / Math.Max(expectedDiameterX, 1e-6) - 1.0) +
                            Math.Abs(bounds.Height / Math.Max(expectedDiameterY, 1e-6) - 1.0);
                        // 位置偏差权重最高：局部 ROI 中宁可拒绝形状相似的邻近结构，也不跨层串点。
                        double score =
                            4.0 * Math.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY) +
                            1.5 * areaError + sizeError + (1.0 - circularity);
                        if (best != null && score >= best.Score)
                            continue;

                        best = new MarkerPoint
                        {
                            X = centerX,
                            Y = centerY,
                            Area = area,
                            Circularity = circularity,
                            Width = bounds.Width,
                            Height = bounds.Height,
                            Score = score
                        };
                    }
                }
                return best;
            }
        }

        /// <summary>为上下大圆构造全宽横向条带；纵向高度由物理直径和搜索余量决定。</summary>
        private static Rect BuildSearchRect(
            Size imageSize,
            double centerY,
            double diameterPixels,
            double pixelsPerMm,
            double marginMm,
            string regionName,
            string imageName)
        {
            if (centerY < 0 || centerY >= imageSize.Height)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(centerY),
                    $"{imageName} {regionName} 预测圆心 Y={centerY:F1} 超出图像高度 {imageSize.Height}。");
            }

            double halfHeight = diameterPixels * 0.5 + marginMm * pixelsPerMm;
            int y0 = Math.Max(0, (int)Math.Floor(centerY - halfHeight));
            int y1 = Math.Min(imageSize.Height, (int)Math.Ceiling(centerY + halfHeight));
            if (imageSize.Width <= 0 || y1 <= y0)
                throw new ArgumentOutOfRangeException(nameof(imageSize), $"{imageName} {regionName} 搜索区域为空。");

            return new Rect(0, y0, imageSize.Width, y1 - y0);
        }

        /// <summary>以“像素颜色到白色的距离”分割 TIFF 彩色排版 Mark，再筛选圆形轮廓。</summary>
        private static List<MarkerPoint> DetectTiff(
            Mat strip,
            int yOffset,
            double minCircularity,
            double expectedDiameterPixels,
            double expectedCenterY)
        {
            var markers = new List<MarkerPoint>();
            double stripArea = Math.Max(1.0, (double)strip.Width * strip.Height);
            Mat bgr = strip;
            bool ownsBgr = false;

            try
            {
                if (strip.Channels() == 1)
                {
                    bgr = new Mat();
                    ownsBgr = true;
                    Cv2.CvtColor(strip, bgr, ColorConversionCodes.GRAY2BGR);
                }
                else if (strip.Channels() == 4)
                {
                    bgr = new Mat();
                    ownsBgr = true;
                    Cv2.CvtColor(strip, bgr, ColorConversionCodes.BGRA2BGR);
                }
                else if (strip.Channels() != 3)
                {
                    return markers;
                }

                using (var stripFloat = new Mat())
                using (var bDiff = new Mat())
                using (var bSq = new Mat())
                using (var gDiff = new Mat())
                using (var gSq = new Mat())
                using (var rDiff = new Mat())
                using (var rSq = new Mat())
                using (var distSq = new Mat())
                using (var dist = new Mat())
                using (var distU8 = new Mat())
                using (var binary = new Mat())
                {
                    // 透明背景合成白底后，使用三通道到纯白的欧氏距离比单通道阈值更适合彩色 Mark。
                    bgr.ConvertTo(stripFloat, MatType.CV_32FC3);
                    Mat[] channels = Cv2.Split(stripFloat);
                    try
                    {
                        Cv2.Subtract(channels[0], Scalar.All(255), bDiff);
                        Cv2.Multiply(bDiff, bDiff, bSq);
                        Cv2.Subtract(channels[1], Scalar.All(255), gDiff);
                        Cv2.Multiply(gDiff, gDiff, gSq);
                        Cv2.Subtract(channels[2], Scalar.All(255), rDiff);
                        Cv2.Multiply(rDiff, rDiff, rSq);
                        Cv2.Add(bSq, gSq, distSq);
                        Cv2.Add(distSq, rSq, distSq);
                        Cv2.Sqrt(distSq, dist);
                        dist.ConvertTo(distU8, MatType.CV_8UC1, 255.0 / 441.7);
                        Cv2.Threshold(distU8, binary, 25, 255, ThresholdTypes.Binary);
                    }
                    finally
                    {
                        foreach (Mat channel in channels)
                            channel.Dispose();
                    }

                    Cv2.FindContours(
                        binary, out Point[][] contours, out HierarchyIndex[] _,
                        RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    AddValidMarkers(
                        contours, markers, stripArea, yOffset, minCircularity,
                        expectedDiameterPixels, false);
                }
            }
            finally
            {
                if (ownsBgr)
                    bgr.Dispose();
            }

            return ClusterY(markers, Math.Max(3.0, expectedDiameterPixels * 0.5), expectedCenterY);
        }

        /// <summary>
        /// 对 CIS 条带扫描一组灰度阈值，选择有效圆数量最多且 Y 最接近预测排的结果，并返回所用阈值。
        /// </summary>
        private static Tuple<List<MarkerPoint>, int> DetectJpg(
            Mat stripGray,
            int yOffset,
            double minCircularity,
            double referenceArea,
            double expectedDiameterPixels,
            double expectedCenterY)
        {
            double stripArea = Math.Max(1.0, (double)stripGray.Width * stripGray.Height);
            using (var claheImage = new Mat())
            using (var blurred = new Mat())
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5)))
            {
                using (var clahe = Cv2.CreateCLAHE(2.0, new Size(4, 4)))
                    clahe.Apply(stripGray, claheImage);
                Cv2.GaussianBlur(claheImage, blurred, new Size(3, 3), 0);

                var bestCircles = new List<MarkerPoint>();
                int bestThreshold = 120;
                int[] thresholds = { 20, 30, 40, 50, 60, 70, 80, 100, 120, 140, 160, 180 };

                // 反光和曝光会改变前景灰度，固定阈值不稳定；有限阈值表保持成本可控且结果可诊断。
                foreach (int threshold in thresholds)
                {
                    using (var binary = new Mat())
                    {
                        Cv2.Threshold(blurred, binary, threshold, 255, ThresholdTypes.Binary);
                        int nonZero = Cv2.CountNonZero(binary);
                        if ((double)nonZero / Math.Max(1, binary.Width * binary.Height) > 0.5)
                            Cv2.BitwiseNot(binary, binary);

                        int border = Math.Min(15, Math.Max(2, binary.Height / 20));
                        Cv2.Rectangle(binary, new Rect(0, 0, binary.Width, Math.Min(border, binary.Height)), Scalar.Black, -1);
                        Cv2.Rectangle(binary, new Rect(0, Math.Max(0, binary.Height - border), binary.Width, Math.Min(border, binary.Height)), Scalar.Black, -1);
                        Cv2.Rectangle(binary, new Rect(0, 0, Math.Min(border, binary.Width), binary.Height), Scalar.Black, -1);
                        Cv2.Rectangle(binary, new Rect(Math.Max(0, binary.Width - border), 0, Math.Min(border, binary.Width), binary.Height), Scalar.Black, -1);
                        Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel, null, 2);

                        Cv2.FindContours(
                            binary, out Point[][] contours, out HierarchyIndex[] _,
                            RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                        var circles = new List<MarkerPoint>();
                        AddValidMarkers(
                            contours, circles, stripArea, yOffset, minCircularity,
                            expectedDiameterPixels, true, referenceArea);
                        circles = ClusterY(
                            circles, Math.Max(3.0, expectedDiameterPixels * 0.5), expectedCenterY);

                        if (circles.Count > bestCircles.Count ||
                            (circles.Count == bestCircles.Count &&
                             RowDistance(circles, expectedCenterY) < RowDistance(bestCircles, expectedCenterY)))
                        {
                            bestCircles = circles;
                            bestThreshold = threshold;
                        }

                        if (bestCircles.Count >= 7)
                            break;
                    }
                }

                return Tuple.Create(bestCircles, bestThreshold);
            }
        }

        /// <summary>按面积占比、圆度、直径和可选参考面积把轮廓转换为 Mark 圆心。</summary>
        private static void AddValidMarkers(
            Point[][] contours,
            List<MarkerPoint> output,
            double stripArea,
            int yOffset,
            double minCircularity,
            double expectedDiameterPixels,
            bool allowWiderSizeRange,
            double referenceArea = 0)
        {
            double minHeightFactor = allowWiderSizeRange ? 0.35 : 0.55;
            double maxHeightFactor = allowWiderSizeRange ? 2.20 : 1.60;

            foreach (Point[] contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                double perimeter = Cv2.ArcLength(contour, true);
                if (perimeter <= 0)
                    continue;

                double areaRatio = area / stripArea;
                if (areaRatio < 0.0001 || areaRatio > 0.20)
                    continue;

                double circularity = 4 * Math.PI * area / (perimeter * perimeter);
                if (circularity < minCircularity)
                    continue;

                Rect bounds = Cv2.BoundingRect(contour);
                if (expectedDiameterPixels > 0 &&
                    (bounds.Height < expectedDiameterPixels * minHeightFactor ||
                     bounds.Height > expectedDiameterPixels * maxHeightFactor))
                    continue;

                if (referenceArea > 0 && (area < referenceArea * 0.3 || area > referenceArea * 3.0))
                    continue;

                Moments moments = Cv2.Moments(contour);
                if (Math.Abs(moments.M00) < double.Epsilon)
                    continue;

                output.Add(new MarkerPoint
                {
                    X = moments.M10 / moments.M00,
                    Y = moments.M01 / moments.M00 + yOffset,
                    Area = area,
                    Circularity = circularity
                });
            }
        }

        /// <summary>把同一条带内的候选按 Y 聚类，选择最接近物理预测且点数充足的一排。</summary>
        private static List<MarkerPoint> ClusterY(
            List<MarkerPoint> markers,
            double tolerance,
            double expectedCenterY)
        {
            if (markers.Count <= 1)
                return markers.OrderBy(m => m.X).ToList();

            var clusters = new List<List<MarkerPoint>>();
            foreach (MarkerPoint marker in markers.OrderBy(m => m.Y))
            {
                List<MarkerPoint> target = clusters.FirstOrDefault(cluster =>
                    Math.Abs(marker.Y - cluster.Average(p => p.Y)) <= tolerance);
                if (target == null)
                {
                    target = new List<MarkerPoint>();
                    clusters.Add(target);
                }
                target.Add(marker);
            }

            List<List<MarkerPoint>> eligible = clusters.Where(c => c.Count >= MinimumPointsPerRow).ToList();
            if (eligible.Count == 0)
                eligible = clusters;

            List<MarkerPoint> best = eligible
                .OrderBy(c => Math.Abs(c.Average(p => p.Y) - expectedCenterY))
                .ThenByDescending(c => c.Count)
                .ThenByDescending(c => c.Sum(p => p.Area))
                .First();
            return best.OrderBy(p => p.X).ToList();
        }

        /// <summary>
        /// 对上下大圆按归一化 X 顺序一一配对；两侧候选数不一致时，每个候选最多使用一次。
        /// </summary>
        private static Tuple<List<MarkerPoint>, List<MarkerPoint>> MatchRows(
            List<MarkerPoint> tiffPoints,
            List<MarkerPoint> cisPoints)
        {
            if (tiffPoints.Count == 0 || cisPoints.Count == 0)
                return Tuple.Create(new List<MarkerPoint>(), new List<MarkerPoint>());

            List<MarkerPoint> tiff = tiffPoints.OrderBy(p => p.X).ToList();
            List<MarkerPoint> cis = cisPoints.OrderBy(p => p.X).ToList();
            if (tiff.Count == cis.Count)
                return Tuple.Create(tiff, cis);

            double[] tiffNormalized = NormalizeX(tiff);
            double[] cisNormalized = NormalizeX(cis);
            var matchedTiff = new List<MarkerPoint>();
            var matchedCis = new List<MarkerPoint>();

            if (tiff.Count <= cis.Count)
            {
                var usedCis = new HashSet<int>();
                for (int i = 0; i < tiff.Count; i++)
                {
                    int best = FindNearestUnused(tiffNormalized[i], cisNormalized, usedCis);
                    if (best < 0)
                        continue;
                    matchedTiff.Add(tiff[i]);
                    matchedCis.Add(cis[best]);
                    usedCis.Add(best);
                }
            }
            else
            {
                var usedTiff = new HashSet<int>();
                for (int i = 0; i < cis.Count; i++)
                {
                    int best = FindNearestUnused(cisNormalized[i], tiffNormalized, usedTiff);
                    if (best < 0)
                        continue;
                    matchedTiff.Add(tiff[best]);
                    matchedCis.Add(cis[i]);
                    usedTiff.Add(best);
                }
            }

            return Tuple.Create(matchedTiff, matchedCis);
        }

        private static double[] NormalizeX(List<MarkerPoint> points)
        {
            double min = points.First().X;
            double range = Math.Max(points.Last().X - min, 1.0);
            return points.Select(p => (p.X - min) / range).ToArray();
        }

        private static int FindNearestUnused(double target, double[] candidates, HashSet<int> used)
        {
            int bestIndex = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (used.Contains(i))
                    continue;
                double distance = Math.Abs(candidates[i] - target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        /// <summary>用一排最左/最右匹配点估计横向尺度，供上下排一致性门控使用。</summary>
        private static double EstimateHorizontalScale(
            List<MarkerPoint> tiffPoints,
            List<MarkerPoint> cisPoints)
        {
            if (tiffPoints.Count < 2 || cisPoints.Count < 2)
                return double.NaN;
            double cisSpan = cisPoints.Last().X - cisPoints.First().X;
            if (Math.Abs(cisSpan) < 1e-6)
                return double.NaN;
            return (tiffPoints.Last().X - tiffPoints.First().X) / cisSpan;
        }

        private static Mat ComputeRobustTransform(List<Point2f> cisPoints, List<Point2f> tiffPoints)
        {
            Mat transform = null;
            // 点数充足时使用 RANSAC Homography；数据不足或求解失败时退到更受约束的仿射模型，
            // 并提升成 3×3 形式，使后续 Warp/Remap 使用统一接口。
            if (cisPoints.Count >= 6)
            {
                using (InputArray src = InputArray.Create(cisPoints))
                using (InputArray dst = InputArray.Create(tiffPoints))
                {
                    transform = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 5.0);
                }
                if (transform != null && !transform.Empty())
                    return transform;
                transform?.Dispose();
            }

            using (var inliers = new Mat())
            using (InputArray src = InputArray.Create(cisPoints))
            using (InputArray dst = InputArray.Create(tiffPoints))
            using (Mat affine = Cv2.EstimateAffine2D(
                src, dst, inliers, RobustEstimationAlgorithms.RANSAC, 5.0))
            {
                if (affine == null || affine.Empty())
                    return null;

                var homography = Mat.Eye(3, 3, MatType.CV_64FC1).ToMat();
                for (int row = 0; row < 2; row++)
                {
                    for (int col = 0; col < 3; col++)
                        homography.Set(row, col, affine.At<double>(row, col));
                }
                return homography;
            }
        }

        private static bool IsFiniteTransform(Mat transform)
        {
            if (transform.Rows != 3 || transform.Cols != 3)
                return false;
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    double value = transform.At<double>(row, col);
                    if (double.IsNaN(value) || double.IsInfinity(value))
                        return false;
                }
            }
            return true;
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0)
                return 0;
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) * 0.5
                : sorted[middle];
        }

        private static double RowDistance(List<MarkerPoint> points, double expectedCenterY)
        {
            return points.Count == 0
                ? double.MaxValue
                : Math.Abs(points.Average(p => p.Y) - expectedCenterY);
        }

        private static string FormatRect(Rect rect)
        {
            return $"({rect.X},{rect.Y},{rect.Width},{rect.Height})";
        }

        private static string FormatPoint(Point2d point)
        {
            return $"({point.X:F1},{point.Y:F1})";
        }

        /// <summary>
        /// 把 CIS 变换到 TIFF 目标尺寸。GlobalOnly 走 WarpPerspective；非线性模式按条带生成逆映射并 Remap。
        /// 返回新 Mat，所有权交给调用方。
        /// </summary>
        public static Mat WarpToTiffSpace(Mat cisMat, AlignmentResult alignment, Size tiffSize)
        {
            if (cisMat == null || cisMat.Empty())
                throw new ArgumentException("CIS 图像为空。", nameof(cisMat));
            if (alignment?.GlobalTransform == null || alignment.GlobalTransform.Empty())
                throw new ArgumentException("对准结果不包含有效全局变换。", nameof(alignment));

            if (!alignment.IsNonlinear || alignment.GridX == null ||
                alignment.GridY == null || alignment.ResidualGrid == null)
            {
                // 关闭侧边功能或网格降级时，完整复用传统 H0 + WarpPerspective 路径。
                var globalWarped = new Mat();
                Stopwatch warpWatch = Stopwatch.StartNew();
                Cv2.WarpPerspective(
                    cisMat, globalWarped, alignment.GlobalTransform, tiffSize,
                    InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                warpWatch.Stop();
                alignment.MapGenerationMilliseconds = 0;
                alignment.RemapMilliseconds = warpWatch.Elapsed.TotalMilliseconds;
                alignment.PeakWorkingSetBytes = Math.Max(
                    alignment.PeakWorkingSetBytes, GetPeakWorkingSetBytes());
                return globalWarped;
            }

            // 非线性路径使用目标驱动的逆映射：每个 TIFF 像素先经 H0^-1 找到 CIS，
            // 再叠加双线性插值残差，最终仅由 Remap 采样一次，避免二次 Warp 模糊。
            var warped = new Mat(tiffSize, cisMat.Type(), Scalar.All(0));
            int stripeRows = Math.Max(1, alignment.StripeRows);
            var leftWeights = new float[tiffSize.Width];
            var rightWeights = new float[tiffSize.Width];
            BuildHorizontalResidualWeights(alignment.GridX, leftWeights, rightWeights);
            double mapMilliseconds = 0;
            double remapMilliseconds = 0;

            // mapX/mapY 只覆盖一个条带并循环复用，避免为超大 TIFF 常驻两张全幅 float 映射。
            using (var mapX = new Mat())
            using (var mapY = new Mat())
            using (var remappedStripe = new Mat())
            {
                for (int targetStartY = 0; targetStartY < tiffSize.Height; targetStartY += stripeRows)
                {
                    int rows = Math.Min(stripeRows, tiffSize.Height - targetStartY);
                    mapX.Create(rows, tiffSize.Width, MatType.CV_32FC1);
                    mapY.Create(rows, tiffSize.Width, MatType.CV_32FC1);
                    long temporaryBytes = (long)rows * tiffSize.Width *
                                          (sizeof(float) * 2 + cisMat.ElemSize());
                    alignment.PeakTemporaryBufferBytes = Math.Max(
                        alignment.PeakTemporaryBufferBytes, temporaryBytes);

                    Stopwatch stageWatch = Stopwatch.StartNew();
                    FillRemapStripe(
                        mapX, mapY, targetStartY, alignment.InverseGlobalTransform,
                        alignment.GridY, alignment.ResidualGrid,
                        leftWeights, rightWeights);
                    stageWatch.Stop();
                    mapMilliseconds += stageWatch.Elapsed.TotalMilliseconds;

                    stageWatch.Restart();
                    Cv2.Remap(
                        cisMat, remappedStripe, mapX, mapY,
                        InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                    using (var destination = new Mat(
                               warped, new Rect(0, targetStartY, tiffSize.Width, rows)))
                    {
                        remappedStripe.CopyTo(destination);
                    }
                    stageWatch.Stop();
                    remapMilliseconds += stageWatch.Elapsed.TotalMilliseconds;
                }
            }

            alignment.MapGenerationMilliseconds = mapMilliseconds;
            alignment.RemapMilliseconds = remapMilliseconds;
            alignment.PeakWorkingSetBytes = Math.Max(
                alignment.PeakWorkingSetBytes, GetPeakWorkingSetBytes());
            return warped;
        }

        private static void BuildHorizontalResidualWeights(
            double[] gridX,
            float[] leftWeights,
            float[] rightWeights)
        {
            // 左残差从左边缘线性衰减到中心 0，右残差从中心 0 线性增长到右边缘；
            // 纸张标记范围之外保持最近侧的残差，纵向网格之外则由 FillRemapStripe 仅使用 H0。
            double leftX = gridX[0];
            double centerX = gridX[1];
            double rightX = gridX[2];
            for (int x = 0; x < leftWeights.Length; x++)
            {
                if (x <= leftX)
                {
                    leftWeights[x] = 1;
                    rightWeights[x] = 0;
                }
                else if (x < centerX)
                {
                    leftWeights[x] = (float)((centerX - x) / Math.Max(centerX - leftX, 1e-6));
                    rightWeights[x] = 0;
                }
                else if (x <= rightX)
                {
                    leftWeights[x] = 0;
                    rightWeights[x] = (float)((x - centerX) / Math.Max(rightX - centerX, 1e-6));
                }
                else
                {
                    leftWeights[x] = 0;
                    rightWeights[x] = 1;
                }
            }
        }

        private static unsafe void FillRemapStripe(
            Mat mapX,
            Mat mapY,
            int targetStartY,
            Mat inverseTransform,
            double[] gridY,
            Point2d[,] residuals,
            float[] leftWeights,
            float[] rightWeights)
        {
            // 逆矩阵元素在循环外读取，行内直接写 float 指针；各 Parallel.For 行互不重叠。
            double h00 = inverseTransform.At<double>(0, 0);
            double h01 = inverseTransform.At<double>(0, 1);
            double h02 = inverseTransform.At<double>(0, 2);
            double h10 = inverseTransform.At<double>(1, 0);
            double h11 = inverseTransform.At<double>(1, 1);
            double h12 = inverseTransform.At<double>(1, 2);
            double h20 = inverseTransform.At<double>(2, 0);
            double h21 = inverseTransform.At<double>(2, 1);
            double h22 = inverseTransform.At<double>(2, 2);

            Parallel.For(0, mapX.Rows, localY =>
            {
                int targetY = targetStartY + localY;
                float* mapXRow = (float*)mapX.Ptr(localY);
                float* mapYRow = (float*)mapY.Ptr(localY);
                bool withinGrid = TryGetGridInterval(gridY, targetY, out int gridRow, out double v);
                Point2d leftResidual = new Point2d(0, 0);
                Point2d rightResidual = new Point2d(0, 0);
                if (withinGrid)
                {
                    leftResidual = Lerp(
                        residuals[gridRow, 0], residuals[gridRow + 1, 0], v);
                    rightResidual = Lerp(
                        residuals[gridRow, 2], residuals[gridRow + 1, 2], v);
                }

                for (int x = 0; x < mapX.Cols; x++)
                {
                    double denominator = h20 * x + h21 * targetY + h22;
                    double sourceX = (h00 * x + h01 * targetY + h02) / denominator;
                    double sourceY = (h10 * x + h11 * targetY + h12) / denominator;
                    if (withinGrid)
                    {
                        double leftWeight = leftWeights[x];
                        double rightWeight = rightWeights[x];
                        sourceX += leftResidual.X * leftWeight + rightResidual.X * rightWeight;
                        sourceY += leftResidual.Y * leftWeight + rightResidual.Y * rightWeight;
                    }
                    mapXRow[x] = (float)sourceX;
                    mapYRow[x] = (float)sourceY;
                }
            });
        }

        /// <summary>定位目标 Y 所在的相邻网格层并返回 0..1 插值系数；网格外返回 false。</summary>
        private static bool TryGetGridInterval(
            double[] gridY,
            double targetY,
            out int row,
            out double v)
        {
            row = -1;
            v = 0;
            if (targetY < gridY[0] || targetY > gridY[gridY.Length - 1])
                return false;
            if (targetY >= gridY[gridY.Length - 1])
            {
                row = gridY.Length - 2;
                v = 1;
                return true;
            }
            for (int index = 0; index < gridY.Length - 1; index++)
            {
                if (targetY < gridY[index] || targetY >= gridY[index + 1])
                    continue;
                row = index;
                v = (targetY - gridY[index]) /
                    Math.Max(gridY[index + 1] - gridY[index], 1e-6);
                return true;
            }
            return false;
        }

        private static long GetPeakWorkingSetBytes()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.PeakWorkingSet64;
            }
            catch
            {
                return 0;
            }
        }
    }
}
