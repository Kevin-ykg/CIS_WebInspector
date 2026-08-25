using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// CIS 到 TIFF 全局变换：检测上下两排 20 mm Mark，并通过 RANSAC 计算全局单应矩阵。
    /// </summary>
    public static partial class ImageAligner
    {
        public static AlignmentResult ComputeTransform(
            Mat cisMat,
            Mat tiffMat,
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            out int optimalThresh,
            out string diagnostic)
        {
            return ComputeTransform(
                cisMat, tiffMat, qrAnchor, options,
                out optimalThresh, out diagnostic, out WhiteInkInspectionResult _);
        }

        /// <summary>
        /// 在返回对准矩阵的同时返回底排 Mark 白墨检查结果。即使对准因 Mark 不足而失败，
        /// 调用方仍可记录已经完成的白墨质量判定。
        /// </summary>
        public static AlignmentResult ComputeTransform(
            Mat cisMat,
            Mat tiffMat,
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            out int optimalThresh,
            out string diagnostic,
            out WhiteInkInspectionResult whiteInkInspection)
        {
            optimalThresh = 127;
            diagnostic = null;
            whiteInkInspection = WhiteInkInspectionResult.Disabled();

            if (!ValidateInputs(cisMat, tiffMat, qrAnchor, options, out diagnostic))
                return null;

            Stopwatch detectionWatch = Stopwatch.StartNew();

            // TIFF 使用固定 DPI；CIS 的纵向比例由已知 60 mm 二维码在当前处理图中的像素高度标定。
            double tiffPixelsPerMm = options.LayoutDpi / 25.4;
            double cisPixelsPerMm = qrAnchor.PixelHeight / options.QrPhysicalHeightMm;
            double cisPixelsPerMmX = qrAnchor.PixelWidth / options.QrPhysicalWidthMm;
            // 行内缺点配对只把二维码宽度换算结果作为尺度先验；最终二维矩阵仍完全由
            // 实测 Mark 对应点求解，不会把二维码位置直接写入 Homography。
            double expectedHorizontalScale = tiffPixelsPerMm / cisPixelsPerMmX;
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
                var detectedRows = new List<GlobalMarkRowDetection>();
                var topCisPoints = new List<MarkerPoint>();
                double referenceArea = 0;

                for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
                {
                    MarkerRegionSpec region = regions[regionIndex];

                    RowDetectionResult tiffRow = DetectTiffMarkRow(tiffMat, region, options);
                    RowDetectionResult cisRow = DetectCisMarkRow(
                        cisGray, region, options, referenceArea);

                    if (regionIndex == 0)
                        FilterOversizedTopCandidates(cisRow, cisGray.Width);

                    if (regionIndex == 0 && cisRow.Points.Count > 0)
                    {
                        // 顶排建立本次曝光下的面积/阈值参考，底排复用它抑制尺寸完全不同的伪圆。
                        referenceArea = Median(cisRow.Points.Select(p => p.Area));
                        optimalThresh = cisRow.Threshold;
                        // 底排完全缺墨时可能没有白色轮廓；保留上排同列 X 作为底排采样中心后备。
                        topCisPoints = cisRow.Points.OrderBy(point => point.X).ToList();
                    }

                    if (region.Name == "Bottom" && options.EnableWhiteInkInspection)
                    {
                        whiteInkInspection = InspectWhiteInk(
                            cisGray, region, cisRow, topCisPoints, options);
                    }

                    detectedRows.Add(new GlobalMarkRowDetection
                    {
                        Region = region,
                        TiffRow = tiffRow,
                        CisRow = cisRow
                    });
                }

                BuildGlobalRowCorrespondences(
                    detectedRows,
                    out List<Point2f> allTiffPoints,
                    out List<Point2f> allCisPoints,
                    out List<AlignmentGlobalMarkPoint> globalMarkPoints,
                    out List<double> rowScaleValues,
                    out List<double> rowCoverageValues,
                    out List<string> rowDiagnostics,
                    out string rowFailure,
                    expectedHorizontalScale);

                if (rowFailure != null)
                {
                    diagnostic = rowFailure + FormatWhiteInkDiagnostic(whiteInkInspection);
                    return null;
                }

                if (allTiffPoints.Count < MinimumGlobalCorrespondenceCount ||
                    allCisPoints.Count < MinimumGlobalCorrespondenceCount)
                {
                    diagnostic =
                        $"上下排合计有效对应点少于 {MinimumGlobalCorrespondenceCount} 个，" +
                        "拒绝使用少量点生成不稳定的二维变换。" +
                                 FormatWhiteInkDiagnostic(whiteInkInspection);
                    return null;
                }

                if (rowCoverageValues.Count < 2 ||
                    rowCoverageValues.Max() < MinimumStrongRowCoverage ||
                    rowCoverageValues.Average() < MinimumAverageRowCoverage)
                {
                    diagnostic =
                        $"上下排 Mark 的横向覆盖不足：" +
                        string.Join(", ", rowCoverageValues.Select(value => value.ToString("P1"))) +
                        $"；至少一排需达到 {MinimumStrongRowCoverage:P0}，平均需达到 " +
                        $"{MinimumAverageRowCoverage:P0}。" + string.Join(" | ", rowDiagnostics);
                    return null;
                }

                if (TryGetRowScaleDifference(rowScaleValues, out double scaleDifference) &&
                    scaleDifference > MaximumRowScaleDifference)
                {
                    // 新流程已经在单次宽条带内完成倾斜行拟合和缺点序列配对；尺度仍不一致
                    // 表明编号或候选本身不可靠，继续放大 ROI 重复检测只会增加耗时和误配风险。
                    diagnostic =
                        $"上下排同编号 Mark 的横向尺度差异 {scaleDifference:P1} 超过允许值 " +
                        $"{MaximumRowScaleDifference:P0}。" + string.Join(" | ", rowDiagnostics);
                    return null;
                }

                // H0 方向固定为 CIS→TIFF；Remap 需要的 TIFF→CIS 逆矩阵在此一次求出并随结果持有。
                double ransacThresholdPixels = Math.Max(
                    3.0, GlobalRansacThresholdMm * tiffPixelsPerMm);
                Mat transform = ComputeRobustTransform(
                    allCisPoints, allTiffPoints, ransacThresholdPixels);
                if (transform == null || transform.Empty() || !IsFiniteTransform(transform))
                {
                    transform?.Dispose();
                    diagnostic = "RANSAC 未能计算出有效的 CIS→TIFF 变换矩阵。" +
                                 string.Join(" | ", rowDiagnostics);
                    return null;
                }

                if (!ValidateGlobalTransformQuality(
                        transform,
                        allCisPoints,
                        allTiffPoints,
                        cisMat.Size(),
                        tiffPixelsPerMm,
                        ransacThresholdPixels,
                        out string transformQualityDiagnostic))
                {
                    transform.Dispose();
                    diagnostic = "全局 Homography 质量门控未通过：" +
                                 transformQualityDiagnostic + " | " +
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
                    $"cisPxPerMmY={cisPixelsPerMm:F4}, cisPxPerMmX={cisPixelsPerMmX:F4}) | " +
                    string.Join(" | ", rowDiagnostics) + " | " + transformQualityDiagnostic;

                AlignmentResult result = null;
                try
                {
                    result = BuildAlignmentResult(
                        cisGray, tiffMat, qrAnchor, options, transform, inverseTransform,
                        globalMarkPoints, out string nonlinearDiagnostic);
                    detectionWatch.Stop();
                    result.DetectionMilliseconds = detectionWatch.Elapsed.TotalMilliseconds;
                    result.PeakWorkingSetBytes = GetPeakWorkingSetBytes();
                    result.WhiteInkInspection = whiteInkInspection;
                    diagnostic = globalDiagnostic + " | " + nonlinearDiagnostic +
                                 FormatWhiteInkDiagnostic(whiteInkInspection);
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

        /// <summary>剔除顶排中面积显著大于同排主体的反光或背景连通域。</summary>
        private static void FilterOversizedTopCandidates(
            RowDetectionResult cisRow,
            int fullImageWidth)
        {
            if (cisRow == null || cisRow.Points.Count < 3)
                return;

            double medianArea = Median(cisRow.Points.Select(point => point.Area));
            cisRow.Points = cisRow.Points
                .Where(point => point.Area < medianArea * 2.5)
                .ToList();
            UpdateRowGeometry(cisRow, fullImageWidth);
        }

        /// <summary>
        /// 将各排检测结果统一转换为变换矩阵所需的对应点，同时集中执行缺点序列配对、
        /// 覆盖率统计和诊断生成，避免检测入口与 Homography 入口各自维护一套编号规则。
        /// </summary>
        private static void BuildGlobalRowCorrespondences(
            IList<GlobalMarkRowDetection> detectedRows,
            out List<Point2f> allTiffPoints,
            out List<Point2f> allCisPoints,
            out List<AlignmentGlobalMarkPoint> globalMarkPoints,
            out List<double> rowScaleValues,
            out List<double> rowCoverageValues,
            out List<string> rowDiagnostics,
            out string rowFailure,
            double expectedHorizontalScale)
        {
            allTiffPoints = new List<Point2f>();
            allCisPoints = new List<Point2f>();
            globalMarkPoints = new List<AlignmentGlobalMarkPoint>();
            rowScaleValues = new List<double>();
            rowCoverageValues = new List<double>();
            rowDiagnostics = new List<string>();
            rowFailure = null;

            foreach (GlobalMarkRowDetection detectedRow in detectedRows)
            {
                MarkerRegionSpec region = detectedRow.Region;
                RowDetectionResult tiffRow = detectedRow.TiffRow;
                RowDetectionResult cisRow = detectedRow.CisRow;

                RowMatchResult matched = MatchRows(
                    tiffRow, cisRow, expectedHorizontalScale);
                int matchedCount = Math.Min(
                    matched.TiffPoints.Count, matched.CisPoints.Count);
                double cisTiltDegrees = Math.Atan(cisRow.Slope) * 180.0 / Math.PI;
                rowDiagnostics.Add(
                    $"{region.Name}: TIFF={tiffRow.Points.Count}, CIS={cisRow.Points.Count}, " +
                    $"Matched={matchedCount}, TIFF-ROI={FormatRect(tiffRow.SearchRect)}, " +
                    $"CIS-ROI={FormatRect(cisRow.SearchRect)}, " +
                    $"SinglePass=True, Tilt={cisTiltDegrees:F2}deg, " +
                    $"YDrift={cisRow.EndToEndYDrift:F1}px, " +
                    $"LineResidual={cisRow.MedianLineResidual:F2}px, " +
                    $"MatchResidual={matched.MedianResidual:F2}px, " +
                    $"Coverage={matched.Coverage:P1}");

                if (matchedCount < MinimumPointsPerRow)
                {
                    rowFailure = rowFailure ??
                        $"{region.Name} 排有效对应 Mark 少于 {MinimumPointsPerRow} 个。";
                    continue;
                }

                if (!double.IsNaN(matched.Scale) && !double.IsInfinity(matched.Scale))
                    rowScaleValues.Add(matched.Scale);
                rowCoverageValues.Add(matched.Coverage);

                allTiffPoints.AddRange(matched.TiffPoints.Select(
                    point => new Point2f((float)point.X, (float)point.Y)));
                allCisPoints.AddRange(matched.CisPoints.Select(
                    point => new Point2f((float)point.X, (float)point.Y)));
                for (int pointIndex = 0; pointIndex < matchedCount; pointIndex++)
                {
                    globalMarkPoints.Add(new AlignmentGlobalMarkPoint
                    {
                        RowName = region.Name,
                        Index = matched.TemplateIndices[pointIndex],
                        TiffPoint = new Point2d(
                            matched.TiffPoints[pointIndex].X,
                            matched.TiffPoints[pointIndex].Y),
                        CisPoint = new Point2d(
                            matched.CisPoints[pointIndex].X,
                            matched.CisPoints[pointIndex].Y)
                    });
                }
            }

            if (rowFailure != null)
                rowFailure += string.Join(" | ", rowDiagnostics);
        }

        private static bool TryGetRowScaleDifference(
            IList<double> rowScaleValues,
            out double scaleDifference)
        {
            scaleDifference = double.NaN;
            if (rowScaleValues == null || rowScaleValues.Count < 2)
                return false;

            scaleDifference = Math.Abs(rowScaleValues[0] - rowScaleValues[1]) /
                              Math.Max(Math.Abs(rowScaleValues[0]), 1e-6);
            return !double.IsNaN(scaleDifference) && !double.IsInfinity(scaleDifference);
        }

        /// <summary>
        /// 对最终 CIS→TIFF Homography 做独立质量检查。RANSAC 能排除局部离群点，但不会
        /// 自动保证整体方向、内点比例和毫米级误差满足工程要求，因此这里必须再次门控。
        /// </summary>
        private static bool ValidateGlobalTransformQuality(
            Mat transform,
            IList<Point2f> cisPoints,
            IList<Point2f> tiffPoints,
            Size cisImageSize,
            double tiffPixelsPerMm,
            double inlierThresholdPixels,
            out string diagnostic)
        {
            var errors = new List<double>(Math.Min(cisPoints.Count, tiffPoints.Count));
            int pointCount = Math.Min(cisPoints.Count, tiffPoints.Count);
            for (int index = 0; index < pointCount; index++)
            {
                if (!TryProjectPoint(transform, cisPoints[index], out Point2d projected))
                {
                    diagnostic = $"第 {index + 1} 个对应点投影结果无效。";
                    return false;
                }

                double deltaX = projected.X - tiffPoints[index].X;
                double deltaY = projected.Y - tiffPoints[index].Y;
                errors.Add(Math.Sqrt(deltaX * deltaX + deltaY * deltaY));
            }

            List<double> inlierErrors = errors
                .Where(error => error <= inlierThresholdPixels)
                .ToList();
            double inlierRatio = inlierErrors.Count / (double)Math.Max(1, errors.Count);
            int requiredInliers = Math.Max(
                4,
                (int)Math.Ceiling(errors.Count * MinimumGlobalInlierRatio));
            if (inlierErrors.Count < requiredInliers || inlierRatio < MinimumGlobalInlierRatio)
            {
                diagnostic =
                    $"RANSAC 有效内点 {inlierErrors.Count}/{errors.Count} " +
                    $"({inlierRatio:P1})，低于最低要求 {MinimumGlobalInlierRatio:P0}。";
                return false;
            }

            double medianErrorPixels = Median(inlierErrors);
            double medianErrorMm = medianErrorPixels / Math.Max(tiffPixelsPerMm, 1e-6);
            if (medianErrorMm > MaximumMedianReprojectionErrorMm)
            {
                diagnostic =
                    $"内点重投影误差中位数 {medianErrorMm:F3}mm，超过允许值 " +
                    $"{MaximumMedianReprojectionErrorMm:F3}mm。";
                return false;
            }

            // 以源图左上角的两个坐标轴验证方向。叉积必须为正，防止错误编号产生镜像矩阵。
            if (!TryProjectPoint(transform, new Point2f(0, 0), out Point2d origin) ||
                !TryProjectPoint(
                    transform,
                    new Point2f(Math.Max(1, cisImageSize.Width - 1), 0),
                    out Point2d xAxis) ||
                !TryProjectPoint(
                    transform,
                    new Point2f(0, Math.Max(1, cisImageSize.Height - 1)),
                    out Point2d yAxis))
            {
                diagnostic = "无法验证 Homography 的全图方向。";
                return false;
            }

            double cross = (xAxis.X - origin.X) * (yAxis.Y - origin.Y) -
                           (xAxis.Y - origin.Y) * (yAxis.X - origin.X);
            if (cross <= 0 || double.IsNaN(cross) || double.IsInfinity(cross))
            {
                diagnostic = "Homography 发生镜像、翻折或方向退化。";
                return false;
            }

            diagnostic =
                $"HQuality=Passed(inliers={inlierErrors.Count}/{errors.Count}, " +
                $"ratio={inlierRatio:P1}, median={medianErrorMm:F3}mm)";
            return true;
        }

        private static bool TryProjectPoint(
            Mat transform,
            Point2f source,
            out Point2d projected)
        {
            projected = default(Point2d);
            double x = source.X;
            double y = source.Y;
            double denominator = transform.At<double>(2, 0) * x +
                                 transform.At<double>(2, 1) * y +
                                 transform.At<double>(2, 2);
            if (Math.Abs(denominator) <= 1e-9 ||
                double.IsNaN(denominator) || double.IsInfinity(denominator))
                return false;

            double targetX = (transform.At<double>(0, 0) * x +
                              transform.At<double>(0, 1) * y +
                              transform.At<double>(0, 2)) / denominator;
            double targetY = (transform.At<double>(1, 0) * x +
                              transform.At<double>(1, 1) * y +
                              transform.At<double>(1, 2)) / denominator;
            if (double.IsNaN(targetX) || double.IsInfinity(targetX) ||
                double.IsNaN(targetY) || double.IsInfinity(targetY))
                return false;

            projected = new Point2d(targetX, targetY);
            return true;
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
    }
}
