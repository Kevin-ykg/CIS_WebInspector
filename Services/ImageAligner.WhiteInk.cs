using System;
using System.Collections.Generic;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 底排 Mark 白墨质量检查，负责 Bottom 区域定位、圆心约束、灰度采样和墨量/拉丝分级。
    /// </summary>
    public static partial class ImageAligner
    {
        public static WhiteInkInspectionResult InspectBottomWhiteInk(
            Mat cisMat,
            CisQrAnchor qrAnchor,
            MarkAlignmentOptions options,
            out string diagnostic)
        {
            diagnostic = null;
            if (options == null || !options.EnableWhiteInkInspection)
                return WhiteInkInspectionResult.Disabled();

            var unable = new WhiteInkInspectionResult
            {
                Status = WhiteInkInspectionStatus.UnableToEvaluate
            };
            if (cisMat == null || cisMat.Empty())
            {
                unable.Diagnostic = diagnostic = "CIS 拼接图为空。";
                return unable;
            }

            if (qrAnchor == null ||
                qrAnchor.PixelHeight <= 1 ||
                double.IsNaN(qrAnchor.PixelHeight) ||
                double.IsInfinity(qrAnchor.PixelHeight))
            {
                unable.Diagnostic = diagnostic = "第二个二维码高度无效，无法定位 Bottom 条带。";
                return unable;
            }

            if (options.QrPhysicalHeightMm <= 0 ||
                options.MarkDiameterMm <= 0 ||
                options.WhiteInkNormalGray <= 0 ||
                options.WhiteInkNormalGray > 255 ||
                options.WhiteInkStreakStdDevThreshold <= 0)
            {
                unable.Diagnostic = diagnostic = "白墨检测物理参数或灰度标定参数无效。";
                return unable;
            }

            double cisPixelsPerMm = qrAnchor.PixelHeight / options.QrPhysicalHeightMm;
            double bottomCenterY = qrAnchor.CenterYInSegment;
            if (bottomCenterY < 0 || bottomCenterY >= cisMat.Height)
            {
                unable.Diagnostic = diagnostic =
                    $"Bottom 圆心 Y={bottomCenterY:F1} 超出 CIS 高度 {cisMat.Height}。";
                return unable;
            }

            var bottomRegion = new MarkerRegionSpec
            {
                Name = "Bottom",
                CisCenterY = bottomCenterY,
                CisDiameterPixels = options.MarkDiameterMm * cisPixelsPerMm,
                CisPixelsPerMm = cisPixelsPerMm
            };

            try
            {
                using (Mat cisGray = ConvertToGray(cisMat))
                {
                    // 独立检查没有上排面积参考；DetectJpg 仍会按尺寸、圆度和物理 Y 约束筛选底排圆。
                    RowDetectionResult bottomRow = DetectCisMarkRow(
                        cisGray, bottomRegion, options, 0);
                    if (bottomRow.Points.Count < MinimumPointsPerRow)
                    {
                        // 完全无白墨时圆内纹理会破坏二值轮廓的圆度；仅在常规检测不足时
                        // 使用物理半径受限的 Hough 圆后备，不增加正常批次的处理成本。
                        List<MarkerPoint> houghPoints = DetectWhiteInkHoughMarkers(
                            cisGray, bottomRow.SearchRect, bottomRegion, qrAnchor);
                        if (houghPoints.Count > bottomRow.Points.Count)
                            bottomRow.Points = houghPoints;
                    }
                    WhiteInkInspectionResult result = InspectWhiteInk(
                        cisGray, bottomRegion, bottomRow,
                        Array.Empty<MarkerPoint>(), options);
                    diagnostic = result.Diagnostic;
                    return result;
                }
            }
            catch (Exception ex)
            {
                unable.Diagnostic = diagnostic = "Bottom 白墨检查异常：" + ex.Message;
                return unable;
            }
        }

        /// <summary>
        /// 为“圆已变暗且内部纹理明显”的完全无墨场景提供几何后备。
        /// 搜索严格限制在 Bottom 条带、已知直径和二维码外部，避免退化成全图泛化圆检测。
        /// </summary>
        private static List<MarkerPoint> DetectWhiteInkHoughMarkers(
            Mat cisGray,
            Rect searchRect,
            MarkerRegionSpec region,
            CisQrAnchor qrAnchor)
        {
            var candidates = new List<MarkerPoint>();
            using (var roi = new Mat(cisGray, searchRect))
            using (var blurred = new Mat())
            {
                int blurSize = Math.Max(
                    5, ((int)Math.Round(region.CisDiameterPixels * 0.05)) | 1);
                blurSize = Math.Min(blurSize, 21);
                Cv2.GaussianBlur(roi, blurred, new Size(blurSize, blurSize), 2.0);

                CircleSegment[] circles = Cv2.HoughCircles(
                    blurred,
                    HoughModes.Gradient,
                    1.2,
                    Math.Max(20.0, region.CisDiameterPixels * 0.48),
                    40,
                    18,
                    Math.Max(3, (int)Math.Round(region.CisDiameterPixels * 0.28)),
                    Math.Max(4, (int)Math.Round(region.CisDiameterPixels * 0.72)));

                double qrExclusionHalfWidth = Math.Max(
                    qrAnchor.PixelWidth * 0.65, region.CisDiameterPixels);
                foreach (CircleSegment circle in circles)
                {
                    double globalX = circle.Center.X + searchRect.X;
                    double globalY = circle.Center.Y + searchRect.Y;
                    if (Math.Abs(globalY - region.CisCenterY) >
                        region.CisDiameterPixels * 0.65)
                        continue;

                    // QR 的三个定位框容易产生伪圆；按识别到的二维码中心/宽度直接排除。
                    if (Math.Abs(globalX - qrAnchor.CenterX) <= qrExclusionHalfWidth)
                        continue;

                    double radiusError = Math.Abs(
                        circle.Radius - region.CisDiameterPixels * 0.5);
                    candidates.Add(new MarkerPoint
                    {
                        X = globalX,
                        Y = globalY,
                        Area = Math.PI * circle.Radius * circle.Radius,
                        Circularity = 1.0,
                        Width = circle.Radius * 2.0,
                        Height = circle.Radius * 2.0,
                        Score =
                            Math.Abs(globalY - region.CisCenterY) +
                            radiusError * 0.5
                    });
                }
            }

            // 同一个物理圆偶尔会产生两个相近 Hough 候选，保留位置/半径误差较小者。
            var selected = new List<MarkerPoint>();
            double duplicateDistance = region.CisDiameterPixels * 0.70;
            foreach (MarkerPoint candidate in candidates.OrderBy(point => point.Score))
            {
                bool duplicate = selected.Any(point =>
                {
                    double dx = point.X - candidate.X;
                    double dy = point.Y - candidate.Y;
                    return dx * dx + dy * dy < duplicateDistance * duplicateDistance;
                });
                if (!duplicate)
                    selected.Add(candidate);
            }

            // 无白墨时单个圆的边缘可能只剩局部纹理，Hough 圆心会向清晰边缘一侧偏移。
            // 底排 Mark 的设计几何是“圆心共线、直径相同”，因此在单圆检测之后再用整排
            // 多数点拟合直线，并把各圆中心校正回该直线。这样 D4 一类低对比度圆不会
            // 仅因上半圈更清晰而出现纵向偏心，同时保留每个圆独立检测得到的 X 坐标。
            List<MarkerPoint> rowConstrained = ApplyWhiteInkRowGeometryConstraints(
                selected, region.CisDiameterPixels);
            return rowConstrained.OrderBy(point => point.X).Take(12).ToList();
        }

        /// <summary>
        /// 对完全无墨场景的 Hough 候选施加底排设计约束：圆心共线且物理直径一致。
        /// 采用 Theil-Sen 中位斜率拟合，少数偏心候选不会把整条圆心线拉偏。
        /// </summary>
        private static List<MarkerPoint> ApplyWhiteInkRowGeometryConstraints(
            IList<MarkerPoint> candidates,
            double expectedDiameter)
        {
            var ordered = candidates
                .OrderBy(point => point.X)
                .ToList();
            if (ordered.Count == 0)
                return ordered;

            // 预览和后续灰度采样统一使用设计直径，不再采用低对比度条件下波动较大的
            // Hough 半径。圆心数量不足 3 个时无法稳健拟合，只统一直径而不修改圆心。
            double normalizedDiameter = Math.Max(4.0, expectedDiameter);
            double normalizedArea = Math.PI * normalizedDiameter * normalizedDiameter * 0.25;
            NormalizeMarkerDiameters(ordered, normalizedDiameter, normalizedArea);
            if (ordered.Count < 3)
                return ordered;

            if (!TryFitRobustMarkerRow(
                ordered, normalizedDiameter, out double slope, out double intercept))
                return ordered;

            // 先剔除距离初始直线过远的伪圆，再用同排多数点复算一次。
            // 阈值按物理直径缩放，既允许无墨纹理造成的中心波动，也不会把相邻结构
            // 误识别出的圆直接吸附到 Mark 行。
            double inlierTolerance = Math.Max(4.0, normalizedDiameter * 0.35);
            List<MarkerPoint> inliers = ordered
                .Where(point =>
                    Math.Abs(point.Y - (slope * point.X + intercept)) <= inlierTolerance)
                .ToList();
            if (inliers.Count >= 3 &&
                TryFitRobustMarkerRow(
                    inliers, normalizedDiameter, out double refinedSlope, out double refinedIntercept))
            {
                slope = refinedSlope;
                intercept = refinedIntercept;
            }

            // X 方向由每个物理圆自身的响应确定；Y 方向使用整排直线约束校正。
            // 保留通过初筛的候选，防止明显伪圆被强行投影成一个看似合法的 Mark。
            var constrained = ordered
                .Where(point =>
                    Math.Abs(point.Y - (slope * point.X + intercept)) <= inlierTolerance)
                .ToList();
            foreach (MarkerPoint point in constrained)
                point.Y = slope * point.X + intercept;

            NormalizeMarkerDiameters(constrained, normalizedDiameter, normalizedArea);
            return constrained;
        }

        /// <summary>
        /// 使用所有跨圆点对斜率的中位数拟合圆心行；相比普通最小二乘，
        /// 单个类似 D4 的偏心圆不会明显改变最终直线。
        /// </summary>
        private static bool TryFitRobustMarkerRow(
            IList<MarkerPoint> points,
            double expectedDiameter,
            out double slope,
            out double intercept)
        {
            slope = 0;
            intercept = 0;
            if (points == null || points.Count < 3)
                return false;

            var slopes = new List<double>();
            double minimumPairSpan = Math.Max(1.0, expectedDiameter);
            for (int first = 0; first < points.Count - 1; first++)
            {
                for (int second = first + 1; second < points.Count; second++)
                {
                    double dx = points[second].X - points[first].X;
                    if (Math.Abs(dx) < minimumPairSpan)
                        continue;
                    slopes.Add((points[second].Y - points[first].Y) / dx);
                }
            }
            if (slopes.Count == 0)
                return false;

            slope = Median(slopes);
            // CIS 横向安装只允许轻微倾斜；限制异常斜率，避免少量伪圆形成陡峭直线。
            const double maximumAbsoluteSlope = 0.05;
            slope = Math.Max(-maximumAbsoluteSlope, Math.Min(maximumAbsoluteSlope, slope));
            double fittedSlope = slope;
            intercept = Median(points.Select(point => point.Y - fittedSlope * point.X));
            return true;
        }

        private static void NormalizeMarkerDiameters(
            IEnumerable<MarkerPoint> points,
            double diameter,
            double area)
        {
            foreach (MarkerPoint point in points)
            {
                point.Width = diameter;
                point.Height = diameter;
                point.Area = area;
                point.Circularity = 1.0;
            }
        }

        /// <summary>
        /// 计算 CIS 实拍图到 TIFF 排版图的变换矩阵。
        /// 第二个二维码的全局 Y 是 CIS 下排 Mark 圆心的权威坐标；提取 ROI 时，
        /// 通过减去拼接段全局起始 Y 转换为 cisMat 内的局部坐标。
        /// </summary>
    }
}
