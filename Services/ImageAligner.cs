using System;
using System.Collections.Generic;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 根据物理位置约束提取圆形 Mark，并计算 CIS 实拍图到 TIFF 排版图的变换矩阵。
    /// </summary>
    public class ImageAligner
    {
        private const int MinimumPointsPerRow = 2;
        private const double MaximumRowScaleDifference = 0.15;

        public class MarkerPoint
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Area { get; set; }
            public double Circularity { get; set; }
        }

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

        private sealed class RowDetectionResult
        {
            public List<MarkerPoint> Points { get; set; } = new List<MarkerPoint>();
            public int Threshold { get; set; } = 127;
            public Rect SearchRect { get; set; }
            public bool UsedExpandedWindow { get; set; }
        }

        /// <summary>
        /// 计算 CIS 实拍图到 TIFF 排版图的变换矩阵。
        /// 第二个二维码的全局 Y 是 CIS 下排 Mark 圆心的权威坐标；提取 ROI 时，
        /// 通过减去拼接段全局起始 Y 转换为 cisMat 内的局部坐标。
        /// </summary>
        public static Mat ComputeTransform(
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

            double tiffPixelsPerMm = options.LayoutDpi / 25.4;
            double cisPixelsPerMm = qrAnchor.PixelHeight / options.QrPhysicalHeightMm;
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
                        double medianArea = Median(cisRow.Points.Select(p => p.Area));
                        cisRow.Points = cisRow.Points.Where(p => p.Area < medianArea * 2.5).ToList();
                    }

                    if (regionIndex == 0 && cisRow.Points.Count > 0)
                    {
                        referenceArea = Median(cisRow.Points.Select(p => p.Area));
                        optimalThresh = cisRow.Threshold;
                    }

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
                }

                if (allTiffPoints.Count < 4 || allCisPoints.Count < 4)
                {
                    diagnostic = "有效对应点少于 4 个，无法计算稳定的二维变换。";
                    return null;
                }

                if (rowScaleValues.Count >= 2)
                {
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

                Mat transform = ComputeRobustTransform(allCisPoints, allTiffPoints);
                if (transform == null || transform.Empty() || !IsFiniteTransform(transform))
                {
                    transform?.Dispose();
                    diagnostic = "RANSAC 未能计算出有效的 CIS→TIFF 变换矩阵。" +
                                 string.Join(" | ", rowDiagnostics);
                    return null;
                }

                diagnostic =
                    $"QR(globalY={qrAnchor.GlobalCenterY}, segmentStart={qrAnchor.SegmentStartGlobalY}, " +
                    $"localY={cisBottomCenterY:F1}, height={qrAnchor.PixelHeight:F1}, " +
                    $"cisPxPerMm={cisPixelsPerMm:F4}) | " + string.Join(" | ", rowDiagnostics);
                return transform;
            }
        }

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

        public static Mat WarpToTiffSpace(Mat cisMat, Mat transform, Size tiffSize)
        {
            var warped = new Mat();
            Cv2.WarpPerspective(cisMat, warped, transform, tiffSize);
            return warped;
        }
    }
}
