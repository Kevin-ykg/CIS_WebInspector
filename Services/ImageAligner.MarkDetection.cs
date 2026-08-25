using System;
using System.Collections.Generic;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// Mark ROI 与候选检测，统一处理坐标换算、条带搜索、轮廓评分、行匹配和全局矩阵求解。
    /// </summary>
    public static partial class ImageAligner
    {
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
                options.QrPhysicalWidthMm,
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

            if (qrAnchor.PixelWidth <= 1 || double.IsNaN(qrAnchor.PixelWidth) || double.IsInfinity(qrAnchor.PixelWidth))
            {
                error = $"第二个二维码像素宽度无效：{qrAnchor.PixelWidth:F3}。";
                return false;
            }

            if (options.LayoutDpi <= 0 || options.TiffHeightMm <= 0 ||
                options.TiffTopCenterYmm < 0 || options.TiffBottomOffsetMm < 0 ||
                options.TiffBottomOffsetMm >= options.TiffHeightMm ||
                options.MarkDiameterMm <= 0 || options.CisRowSpacingMm <= 0 ||
                options.QrPhysicalHeightMm <= 0 || options.QrPhysicalWidthMm <= 0 ||
                options.InitialSearchMarginMm < 0 ||
                options.ExpandedSearchMarginMm < options.InitialSearchMarginMm ||
                options.MinCircularityTiff <= 0 || options.MinCircularityTiff > 1 ||
                options.MinCircularityCis <= 0 || options.MinCircularityCis > 1 ||
                (options.EnableWhiteInkInspection &&
                 (double.IsNaN(options.WhiteInkNormalGray) ||
                  double.IsInfinity(options.WhiteInkNormalGray) ||
                  double.IsNaN(options.WhiteInkStreakStdDevThreshold) ||
                  double.IsInfinity(options.WhiteInkStreakStdDevThreshold) ||
                  options.WhiteInkNormalGray <= 0 ||
                  options.WhiteInkNormalGray > 255 ||
                  options.WhiteInkStreakStdDevThreshold <= 0)))
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

        /// <summary>
        /// TIFF 排版坐标固定且 Mark 行理论上水平，使用预测位置附近的窄条带单次检测即可。
        /// 不再执行“点数不足后扩大窗口”的第二遍图像处理。
        /// </summary>
        private static RowDetectionResult DetectTiffMarkRow(
            Mat image,
            MarkerRegionSpec region,
            MarkAlignmentOptions options)
        {
            Rect searchRect = BuildSearchRect(
                image.Size(), region.TiffCenterY, region.TiffDiameterPixels,
                region.TiffPixelsPerMm, options.InitialSearchMarginMm, region.Name, "TIFF");

            List<MarkerPoint> points;
            using (var roi = new Mat(image, searchRect))
            {
                points = DetectTiff(
                    roi, searchRect.Y, options.MinCircularityTiff,
                    region.TiffDiameterPixels, region.TiffCenterY, image.Width);
            }

            var result = new RowDetectionResult { Points = points, SearchRect = searchRect };
            UpdateRowGeometry(result, image.Width);
            return result;
        }

        /// <summary>
        /// CIS 使用一次能够覆盖位置预测误差、圆半径和允许倾斜的宽条带。条带内的候选
        /// 由 RANSAC 倾斜行模型筛选，不再根据“检测到几个点”重复执行第二遍阈值扫描。
        /// 条带余量只由初始定位误差和 Mark 尺寸确定，不引用扩展搜索参数。
        /// </summary>
        private static RowDetectionResult DetectCisMarkRow(
            Mat imageGray,
            MarkerRegionSpec region,
            MarkAlignmentOptions options,
            double referenceArea)
        {
            // 单遍条带不直接取原 40 mm 最大扩展值：在初始定位余量上再增加 0.5 个
            // Mark 直径，足以覆盖当前约 1.6° 的端到端倾斜，同时减少约束外背景处理量。
            double singlePassMarginMm =
                options.InitialSearchMarginMm + options.MarkDiameterMm * 0.5;
            Rect searchRect = BuildSearchRect(
                imageGray.Size(), region.CisCenterY, region.CisDiameterPixels,
                region.CisPixelsPerMm, singlePassMarginMm, region.Name, "CIS");

            Tuple<List<MarkerPoint>, int> detected;
            using (var roi = new Mat(imageGray, searchRect))
            {
                detected = DetectJpg(
                    roi, searchRect.Y, options.MinCircularityCis, referenceArea,
                    region.CisDiameterPixels, region.CisCenterY, imageGray.Width);
            }

            var result = new RowDetectionResult
            {
                Points = detected.Item1,
                Threshold = detected.Item2,
                SearchRect = searchRect
            };
            UpdateRowGeometry(result, imageGray.Width);
            return result;
        }

        /// <summary>
        /// 复用底排 20 mm Mark 评估白墨质量。中心优先采用底排实测；底排轮廓消失时，
        /// 使用上排同列 X 与已知底排 Y 继续采样，使“完全无墨”不会因找不到白圆而失去判定。
        /// </summary>
        private static WhiteInkInspectionResult InspectWhiteInk(
            Mat cisGray,
            MarkerRegionSpec bottomRegion,
            RowDetectionResult bottomRow,
            IList<MarkerPoint> topRowPoints,
            MarkAlignmentOptions options)
        {
            var result = new WhiteInkInspectionResult
            {
                Status = WhiteInkInspectionStatus.UnableToEvaluate,
                SearchRegion = bottomRow.SearchRect
            };

            // 预测中心与实测中心一一对应，避免一个候选被重复用于多个 Mark。
            var samplingCenters = new List<KeyValuePair<Point2d, bool>>();
            List<MarkerPoint> detectedBottom = bottomRow.Points.OrderBy(point => point.X).ToList();
            if (topRowPoints != null && topRowPoints.Count >= MinimumPointsPerRow)
            {
                var usedBottom = new HashSet<int>();
                foreach (MarkerPoint topPoint in topRowPoints.OrderBy(point => point.X))
                {
                    int bestIndex = -1;
                    double bestDistance = double.MaxValue;
                    for (int index = 0; index < detectedBottom.Count; index++)
                    {
                        if (usedBottom.Contains(index))
                            continue;

                        double distance = Math.Abs(detectedBottom[index].X - topPoint.X);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestIndex = index;
                        }
                    }

                    // 上下排允许轻微倾斜；超过一个直径通常已不是同列 Mark。
                    if (bestIndex >= 0 && bestDistance <= bottomRegion.CisDiameterPixels)
                    {
                        MarkerPoint detected = detectedBottom[bestIndex];
                        samplingCenters.Add(new KeyValuePair<Point2d, bool>(
                            new Point2d(detected.X, detected.Y), true));
                        usedBottom.Add(bestIndex);
                    }
                    else
                    {
                        samplingCenters.Add(new KeyValuePair<Point2d, bool>(
                            new Point2d(topPoint.X, bottomRegion.CisCenterY), false));
                    }
                }
            }
            else
            {
                foreach (MarkerPoint detected in detectedBottom)
                {
                    samplingCenters.Add(new KeyValuePair<Point2d, bool>(
                        new Point2d(detected.X, detected.Y), true));
                }
            }

            if (samplingCenters.Count < MinimumPointsPerRow)
            {
                result.Diagnostic =
                    $"底排/上排可用 Mark 中心仅 {samplingCenters.Count} 个，无法形成可靠白墨统计。";
                return result;
            }

            double diameter = Math.Max(4.0, bottomRegion.CisDiameterPixels);
            // 只取圆心内部，避开轻微对准误差和轮廓边缘；背景取圆外局部环带。
            double markRadius = diameter * 0.30;
            double backgroundInnerRadius = diameter * 0.57;
            double backgroundOuterRadius = diameter * 0.82;
            var samples = new List<WhiteInkMarkSample>();

            for (int index = 0; index < samplingCenters.Count; index++)
            {
                Point2d center = samplingCenters[index].Key;
                int x0 = Math.Max(0, (int)Math.Floor(center.X - backgroundOuterRadius));
                int x1 = Math.Min(cisGray.Width, (int)Math.Ceiling(center.X + backgroundOuterRadius + 1));
                int y0 = Math.Max(0, (int)Math.Floor(center.Y - backgroundOuterRadius));
                int y1 = Math.Min(cisGray.Height, (int)Math.Ceiling(center.Y + backgroundOuterRadius + 1));
                if (x1 <= x0 || y1 <= y0)
                    continue;

                var sampleRect = new Rect(x0, y0, x1 - x0, y1 - y0);
                var localCenter = new Point(
                    (int)Math.Round(center.X - sampleRect.X),
                    (int)Math.Round(center.Y - sampleRect.Y));

                using (var roi = new Mat(cisGray, sampleRect))
                using (Mat markMask = Mat.Zeros(sampleRect.Height, sampleRect.Width, MatType.CV_8UC1).ToMat())
                using (Mat backgroundMask = Mat.Zeros(sampleRect.Height, sampleRect.Width, MatType.CV_8UC1).ToMat())
                {
                    Cv2.Circle(
                        markMask, localCenter, Math.Max(2, (int)Math.Round(markRadius)),
                        Scalar.White, -1, LineTypes.Link8);
                    Cv2.Circle(
                        backgroundMask, localCenter,
                        Math.Max(3, (int)Math.Round(backgroundOuterRadius)),
                        Scalar.White, -1, LineTypes.Link8);
                    Cv2.Circle(
                        backgroundMask, localCenter,
                        Math.Max(2, (int)Math.Round(backgroundInnerRadius)),
                        Scalar.Black, -1, LineTypes.Link8);

                    // 紧邻的双圆会进入当前背景环；显式排除其他 Mark 的物理区域。
                    for (int otherIndex = 0; otherIndex < samplingCenters.Count; otherIndex++)
                    {
                        if (otherIndex == index)
                            continue;

                        Point2d other = samplingCenters[otherIndex].Key;
                        if (other.X < sampleRect.X - diameter ||
                            other.X > sampleRect.X + sampleRect.Width + diameter ||
                            other.Y < sampleRect.Y - diameter ||
                            other.Y > sampleRect.Y + sampleRect.Height + diameter)
                            continue;

                        var otherLocal = new Point(
                            (int)Math.Round(other.X - sampleRect.X),
                            (int)Math.Round(other.Y - sampleRect.Y));
                        Cv2.Circle(
                            backgroundMask, otherLocal,
                            Math.Max(2, (int)Math.Round(backgroundInnerRadius)),
                            Scalar.Black, -1, LineTypes.Link8);
                    }

                    if (Cv2.CountNonZero(markMask) < 20 || Cv2.CountNonZero(backgroundMask) < 20)
                        continue;

                    Cv2.MeanStdDev(roi, out Scalar markMean, out Scalar markStdDev, markMask);
                    Cv2.MeanStdDev(
                        roi, out Scalar backgroundMean, out Scalar backgroundStdDev, backgroundMask);
                    double contrast = markMean.Val0 - backgroundMean.Val0;
                    samples.Add(new WhiteInkMarkSample
                    {
                        Index = samples.Count + 1,
                        Center = center,
                        DisplayRadius = diameter * 0.5,
                        UsedDetectedCenter = samplingCenters[index].Value,
                        MarkMean = markMean.Val0,
                        MarkVariance = markStdDev.Val0 * markStdDev.Val0,
                        BackgroundMean = backgroundMean.Val0,
                        Contrast = contrast
                    });
                }
            }

            result.Samples = samples.AsReadOnly();
            if (samples.Count < MinimumPointsPerRow)
            {
                result.Diagnostic =
                    $"有效灰度样本仅 {samples.Count} 个，无法形成可靠白墨统计。";
                return result;
            }

            // 多个 Mark 取中位数，单个污点、反光或局部图案不会主导整次供墨结论。
            result.MarkMean = Median(samples.Select(sample => sample.MarkMean));
            result.MarkVariance = Median(samples.Select(sample => sample.MarkVariance));
            result.BackgroundMean = Median(samples.Select(sample => sample.BackgroundMean));
            result.Contrast = Median(samples.Select(sample => sample.Contrast));

            // 正常白墨灰度在不同批次较稳定，而膜片背景会随曝光明显变化；
            // 以当前背景归一化“背景→正常白墨”的可用灰度范围。
            double normalContrast = Math.Max(
                1.0, options.WhiteInkNormalGray - result.BackgroundMean);
            result.InkLevelPercent = Math.Max(
                0.0, Math.Min(100.0, result.Contrast * 100.0 / normalContrast));
            result.HasStreaking =
                result.MarkStandardDeviation >= options.WhiteInkStreakStdDevThreshold;

            // 固定 20% 分档，避免引入过多互相牵制的现场阈值。
            if (result.InkLevelPercent < 20.0)
                result.Status = WhiteInkInspectionStatus.NoInk;
            else if (result.InkLevelPercent < 40.0)
                result.Status = WhiteInkInspectionStatus.SevereShortage;
            else if (result.InkLevelPercent < 60.0)
                result.Status = WhiteInkInspectionStatus.ModerateShortage;
            else if (result.InkLevelPercent < 80.0)
                result.Status = WhiteInkInspectionStatus.MildShortage;
            else if (result.HasStreaking)
                result.Status = WhiteInkInspectionStatus.Streaking;
            else
                result.Status = WhiteInkInspectionStatus.Normal;

            result.Diagnostic =
                $"状态={result.StatusDisplayName}, 相对白墨={result.InkLevelPercent:F1}%, " +
                $"Mark均值={result.MarkMean:F1}, 背景均值={result.BackgroundMean:F1}, " +
                $"对比度={result.Contrast:F1}, 标准差={result.MarkStandardDeviation:F1}, " +
                $"方差={result.MarkVariance:F1}, 拉丝={(result.HasStreaking ? "是" : "否")}, " +
                $"样本={samples.Count}";
            return result;
        }

        private static string FormatWhiteInkDiagnostic(WhiteInkInspectionResult result)
        {
            if (result == null || !result.IsEnabled)
                return string.Empty;
            return " | WhiteInk: " + result.Diagnostic;
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

        /// <summary>
        /// 为上下大圆构造全宽横向条带；纵向高度由物理直径和搜索余量决定。
        /// 当预测条带越过图像边界时，将整个条带平移回图像内部，而不是直接截短。
        /// 顶/底排本来就靠近边缘，如果截短条带，会把允许的位置预测误差无意中减小，
        /// 造成圆心仍在图内但完整轮廓落在 ROI 外的假性漏检。
        /// </summary>
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
            int rawY0 = (int)Math.Floor(centerY - halfHeight);
            int rawY1 = (int)Math.Ceiling(centerY + halfHeight);
            int desiredHeight = Math.Min(imageSize.Height, Math.Max(1, rawY1 - rawY0));

            // 保持条带高度不变：上越界向下平移，下越界向上平移。
            int y0 = rawY0;
            if (y0 < 0)
                y0 = 0;
            else if (y0 + desiredHeight > imageSize.Height)
                y0 = imageSize.Height - desiredHeight;

            int y1 = y0 + desiredHeight;
            if (imageSize.Width <= 0 || y0 < 0 || y1 > imageSize.Height || y1 <= y0)
                throw new ArgumentOutOfRangeException(nameof(imageSize), $"{imageName} {regionName} 搜索区域为空。");

            return new Rect(0, y0, imageSize.Width, y1 - y0);
        }

        /// <summary>以“像素颜色到白色的距离”分割 TIFF 彩色排版 Mark，再筛选圆形轮廓。</summary>
        private static List<MarkerPoint> DetectTiff(
            Mat strip,
            int yOffset,
            double minCircularity,
            double expectedDiameterPixels,
            double expectedCenterY,
            int fullImageWidth)
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

            return SelectBestSlantedMarkerRow(
                markers, expectedDiameterPixels, expectedCenterY, fullImageWidth);
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
            double expectedCenterY,
            int fullImageWidth)
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
                // 现场正常/轻度缺墨批次的有效阈值主要集中在 120～160，优先检查该范围；
                // 后半段仍保留低灰度阈值，以兼容严重缺墨。顺序变化不减少可覆盖的阈值集合。
                int[] thresholds = { 140, 120, 160, 100, 180, 80, 60, 40, 20, 30, 50, 70 };
                List<MarkerPoint> previousStableRow = null;
                int stableRowCount = 0;

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
                        circles = SelectBestSlantedMarkerRow(
                            circles, expectedDiameterPixels, expectedCenterY, fullImageWidth);

                        if (AreMarkerRowsEquivalent(
                                previousStableRow,
                                circles,
                                Math.Max(3.0, expectedDiameterPixels * 0.15)))
                        {
                            stableRowCount++;
                        }
                        else
                        {
                            stableRowCount = 1;
                            previousStableRow = circles;
                        }

                        if (circles.Count > bestCircles.Count ||
                            (circles.Count == bestCircles.Count &&
                             (RowHorizontalCoverage(circles, fullImageWidth) >
                                  RowHorizontalCoverage(bestCircles, fullImageWidth) + 1e-6 ||
                              (Math.Abs(RowHorizontalCoverage(circles, fullImageWidth) -
                                        RowHorizontalCoverage(bestCircles, fullImageWidth)) <= 1e-6 &&
                               RowDistance(circles, expectedCenterY) <
                                   RowDistance(bestCircles, expectedCenterY)))))
                        {
                            bestCircles = circles;
                            bestThreshold = threshold;
                        }

                        // 缺失数量不固定后不能再等待固定的 7 个点。连续三个阈值得到相同、
                        // 且横向覆盖充分的倾斜行，说明候选已经稳定，可提前结束余下阈值扫描。
                        if (bestCircles.Count >= 7 ||
                            (stableRowCount >= 3 &&
                             circles.Count >= MinimumPointsPerRow &&
                             RowHorizontalCoverage(circles, fullImageWidth) >=
                                 MinimumStrongRowCoverage))
                            break;
                    }
                }

                return Tuple.Create(bestCircles, bestThreshold);
            }
        }

        private static bool AreMarkerRowsEquivalent(
            IList<MarkerPoint> first,
            IList<MarkerPoint> second,
            double coordinateTolerance)
        {
            if (first == null || second == null || first.Count != second.Count || first.Count == 0)
                return false;

            List<MarkerPoint> orderedFirst = first.OrderBy(point => point.X).ToList();
            List<MarkerPoint> orderedSecond = second.OrderBy(point => point.X).ToList();
            for (int index = 0; index < orderedFirst.Count; index++)
            {
                double deltaX = orderedFirst[index].X - orderedSecond[index].X;
                double deltaY = orderedFirst[index].Y - orderedSecond[index].Y;
                if (Math.Sqrt(deltaX * deltaX + deltaY * deltaY) > coordinateTolerance)
                    return false;
            }
            return true;
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
                    Circularity = circularity,
                    Width = bounds.Width,
                    Height = bounds.Height
                });
            }
        }

        /// <summary>
        /// 在全部圆形候选中确定一条允许倾斜的 Mark 行。这里穷举两点直线而不是随机抽样，
        /// 候选数量很少，结果可重复且计算量可以忽略。最终内点还会经过最小二乘直线精修。
        /// </summary>
        private static List<MarkerPoint> SelectBestSlantedMarkerRow(
            List<MarkerPoint> markers,
            double expectedDiameterPixels,
            double expectedCenterY,
            int fullImageWidth)
        {
            if (markers == null || markers.Count <= 1)
                return markers?.OrderBy(marker => marker.X).ToList() ?? new List<MarkerPoint>();

            double inlierTolerance = Math.Max(
                3.0, expectedDiameterPixels * RowLineInlierDiameterRatio);
            List<MarkerPoint> bestInliers = null;
            double bestCoverage = -1;
            double bestMedianResidual = double.MaxValue;
            double bestCenterDistance = double.MaxValue;

            for (int first = 0; first < markers.Count - 1; first++)
            {
                for (int second = first + 1; second < markers.Count; second++)
                {
                    double deltaX = markers[second].X - markers[first].X;
                    if (Math.Abs(deltaX) < Math.Max(2.0, expectedDiameterPixels * 0.5))
                        continue;

                    double slope = (markers[second].Y - markers[first].Y) / deltaX;
                    double intercept = markers[first].Y - slope * markers[first].X;
                    List<MarkerPoint> inliers = markers.Where(marker =>
                        DistanceToLine(marker, slope, intercept) <= inlierTolerance).ToList();
                    if (inliers.Count < MinimumPointsPerRow)
                        continue;

                    FitMarkerRow(inliers, out slope, out intercept);
                    inliers = markers.Where(marker =>
                        DistanceToLine(marker, slope, intercept) <= inlierTolerance).ToList();
                    double coverage = RowHorizontalCoverage(inliers, fullImageWidth);
                    double medianResidual = Median(inliers.Select(marker =>
                        DistanceToLine(marker, slope, intercept)));
                    double predictedCenterY = slope * (Math.Max(1, fullImageWidth) * 0.5) + intercept;
                    double centerDistance = Math.Abs(predictedCenterY - expectedCenterY);

                    bool better = bestInliers == null ||
                                  inliers.Count > bestInliers.Count ||
                                  (inliers.Count == bestInliers.Count &&
                                   (coverage > bestCoverage + 1e-6 ||
                                    (Math.Abs(coverage - bestCoverage) <= 1e-6 &&
                                     (medianResidual < bestMedianResidual - 1e-6 ||
                                      (Math.Abs(medianResidual - bestMedianResidual) <= 1e-6 &&
                                       centerDistance < bestCenterDistance)))));
                    if (!better)
                        continue;

                    bestInliers = inliers;
                    bestCoverage = coverage;
                    bestMedianResidual = medianResidual;
                    bestCenterDistance = centerDistance;
                }
            }

            // 只有一个可用圆时无法定义行方向；保留最接近预测 Y 的候选，让上层输出明确的
            // “有效点不足”诊断，而不是在这里静默丢失所有检测信息。
            if (bestInliers == null || bestInliers.Count == 0)
            {
                return markers
                    .OrderBy(marker => Math.Abs(marker.Y - expectedCenterY))
                    .Take(1)
                    .OrderBy(marker => marker.X)
                    .ToList();
            }

            return bestInliers.OrderBy(marker => marker.X).ToList();
        }

        private static void UpdateRowGeometry(RowDetectionResult row, int fullImageWidth)
        {
            if (row == null || row.Points == null || row.Points.Count == 0)
                return;

            row.Points = row.Points.OrderBy(marker => marker.X).ToList();
            FitMarkerRow(row.Points, out double slope, out double intercept);
            row.Slope = slope;
            row.EndToEndYDrift = slope * Math.Max(0, fullImageWidth - 1);
            row.HorizontalCoverage = RowHorizontalCoverage(row.Points, fullImageWidth);
            row.MedianLineResidual = row.Points.Count >= 2
                ? Median(row.Points.Select(marker => DistanceToLine(marker, slope, intercept)))
                : 0;
        }

        private static void FitMarkerRow(
            IList<MarkerPoint> points,
            out double slope,
            out double intercept)
        {
            if (points == null || points.Count == 0)
            {
                slope = 0;
                intercept = 0;
                return;
            }

            double meanX = points.Average(point => point.X);
            double meanY = points.Average(point => point.Y);
            double denominator = points.Sum(point =>
                (point.X - meanX) * (point.X - meanX));
            if (denominator <= 1e-6)
            {
                slope = 0;
                intercept = meanY;
                return;
            }

            slope = points.Sum(point =>
                (point.X - meanX) * (point.Y - meanY)) / denominator;
            intercept = meanY - slope * meanX;
        }

        private static double DistanceToLine(
            MarkerPoint point,
            double slope,
            double intercept)
        {
            return Math.Abs(slope * point.X - point.Y + intercept) /
                   Math.Sqrt(slope * slope + 1.0);
        }

        private static double RowHorizontalCoverage(
            IList<MarkerPoint> points,
            int fullImageWidth)
        {
            if (points == null || points.Count < 2 || fullImageWidth <= 1)
                return 0;
            return Math.Max(0, Math.Min(1,
                (points.Max(point => point.X) - points.Min(point => point.X)) /
                (fullImageWidth - 1.0)));
        }

        /// <summary>
        /// 通过“横向尺度 + 平移”假设和单调动态匹配恢复 TIFF Mark 的真实编号。
        /// 匹配允许任意一侧跳点，因此首端、中间或连续缺失都不会令剩余圆被重新编号。
        /// </summary>
        private static RowMatchResult MatchRows(
            RowDetectionResult tiffRow,
            RowDetectionResult cisRow,
            double expectedHorizontalScale)
        {
            var empty = new RowMatchResult();
            if (tiffRow?.Points == null || cisRow?.Points == null ||
                tiffRow.Points.Count < MinimumPointsPerRow ||
                cisRow.Points.Count < MinimumPointsPerRow)
                return empty;

            List<MarkerPoint> tiff = tiffRow.Points.OrderBy(point => point.X).ToList();
            List<MarkerPoint> cis = cisRow.Points.OrderBy(point => point.X).ToList();
            double medianTiffDiameter = Median(tiff.Select(point =>
                Math.Max(point.Width, point.Height)));
            double matchGate = Math.Max(4.0, medianTiffDiameter * RowMatchGateDiameterRatio);
            double centeredOffsetPrior = tiffRow.SearchRect.Width * 0.5 -
                                         expectedHorizontalScale * cisRow.SearchRect.Width * 0.5;

            RowMatchResult best = null;
            for (int tiffFirst = 0; tiffFirst < tiff.Count - 1; tiffFirst++)
            {
                for (int tiffSecond = tiffFirst + 1; tiffSecond < tiff.Count; tiffSecond++)
                {
                    double tiffSpan = tiff[tiffSecond].X - tiff[tiffFirst].X;
                    if (tiffSpan <= 1e-6)
                        continue;

                    for (int cisFirst = 0; cisFirst < cis.Count - 1; cisFirst++)
                    {
                        for (int cisSecond = cisFirst + 1; cisSecond < cis.Count; cisSecond++)
                        {
                            double cisSpan = cis[cisSecond].X - cis[cisFirst].X;
                            if (cisSpan <= 1e-6)
                                continue;

                            double scale = tiffSpan / cisSpan;
                            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
                                continue;

                            double scaleDeviation = Math.Abs(scale - expectedHorizontalScale) /
                                                    Math.Max(Math.Abs(expectedHorizontalScale), 1e-6);
                            if (scaleDeviation > MaximumHorizontalScaleDeviationFromQr)
                                continue;

                            double offset = tiff[tiffFirst].X - scale * cis[cisFirst].X;
                            RowMatchResult candidate = BuildMonotonicRowMatch(
                                tiff, cis, scale, offset, matchGate, tiffRow.SearchRect.Width);
                            if (IsBetterRowMatch(
                                    candidate,
                                    best,
                                    expectedHorizontalScale,
                                    centeredOffsetPrior,
                                    tiffRow.SearchRect.Width))
                                best = candidate;
                        }
                    }
                }
            }

            return best ?? empty;
        }

        /// <summary>
        /// 固定尺度和平移后，通过动态规划寻找保持左右顺序的一一对应。状态优先最大化
        /// 匹配数量，再最小化总残差；跳过点不产生伪造的对应关系。
        /// </summary>
        private static RowMatchResult BuildMonotonicRowMatch(
            IList<MarkerPoint> tiff,
            IList<MarkerPoint> cis,
            double scale,
            double offset,
            double matchGate,
            int tiffImageWidth)
        {
            int tiffCount = tiff.Count;
            int cisCount = cis.Count;
            var matchCounts = new int[tiffCount + 1, cisCount + 1];
            var residualSums = new double[tiffCount + 1, cisCount + 1];
            var actions = new byte[tiffCount + 1, cisCount + 1];

            for (int tiffIndex = 1; tiffIndex <= tiffCount; tiffIndex++)
                actions[tiffIndex, 0] = 1; // 跳过 TIFF 点。
            for (int cisIndex = 1; cisIndex <= cisCount; cisIndex++)
                actions[0, cisIndex] = 2; // 跳过 CIS 点。

            for (int tiffIndex = 1; tiffIndex <= tiffCount; tiffIndex++)
            {
                for (int cisIndex = 1; cisIndex <= cisCount; cisIndex++)
                {
                    int bestCount = matchCounts[tiffIndex - 1, cisIndex];
                    double bestResidual = residualSums[tiffIndex - 1, cisIndex];
                    byte bestAction = 1;

                    if (IsBetterMatchState(
                            matchCounts[tiffIndex, cisIndex - 1],
                            residualSums[tiffIndex, cisIndex - 1],
                            bestCount,
                            bestResidual))
                    {
                        bestCount = matchCounts[tiffIndex, cisIndex - 1];
                        bestResidual = residualSums[tiffIndex, cisIndex - 1];
                        bestAction = 2;
                    }

                    double predictedTiffX = scale * cis[cisIndex - 1].X + offset;
                    double residual = Math.Abs(tiff[tiffIndex - 1].X - predictedTiffX);
                    if (residual <= matchGate)
                    {
                        int candidateCount = matchCounts[tiffIndex - 1, cisIndex - 1] + 1;
                        double candidateResidual =
                            residualSums[tiffIndex - 1, cisIndex - 1] + residual;
                        if (IsBetterMatchState(
                                candidateCount, candidateResidual, bestCount, bestResidual))
                        {
                            bestCount = candidateCount;
                            bestResidual = candidateResidual;
                            bestAction = 3;
                        }
                    }

                    matchCounts[tiffIndex, cisIndex] = bestCount;
                    residualSums[tiffIndex, cisIndex] = bestResidual;
                    actions[tiffIndex, cisIndex] = bestAction;
                }
            }

            var indexPairs = new List<Tuple<int, int>>();
            int ti = tiffCount;
            int ci = cisCount;
            while (ti > 0 || ci > 0)
            {
                byte action = actions[ti, ci];
                if (action == 3)
                {
                    indexPairs.Add(Tuple.Create(ti - 1, ci - 1));
                    ti--;
                    ci--;
                }
                else if (action == 1 && ti > 0)
                {
                    ti--;
                }
                else if (ci > 0)
                {
                    ci--;
                }
                else
                {
                    break;
                }
            }
            indexPairs.Reverse();

            var result = new RowMatchResult { Scale = scale, Offset = offset };
            foreach (Tuple<int, int> pair in indexPairs)
            {
                result.TiffPoints.Add(tiff[pair.Item1]);
                result.CisPoints.Add(cis[pair.Item2]);
                result.TemplateIndices.Add(pair.Item1 + 1);
            }

            if (indexPairs.Count > 0)
            {
                result.MedianResidual = Median(indexPairs.Select(pair =>
                    Math.Abs(tiff[pair.Item1].X -
                             (scale * cis[pair.Item2].X + offset))));
            }
            if (indexPairs.Count >= 2 && tiffImageWidth > 1)
            {
                result.Coverage = Math.Max(0, Math.Min(1,
                    (result.TiffPoints.Last().X - result.TiffPoints.First().X) /
                    (tiffImageWidth - 1.0)));
            }
            return result;
        }

        private static bool IsBetterMatchState(
            int candidateCount,
            double candidateResidual,
            int currentCount,
            double currentResidual)
        {
            return candidateCount > currentCount ||
                   (candidateCount == currentCount && candidateResidual < currentResidual - 1e-9);
        }

        private static bool IsBetterRowMatch(
            RowMatchResult candidate,
            RowMatchResult current,
            double expectedScale,
            double centeredOffsetPrior,
            int tiffImageWidth)
        {
            if (candidate == null)
                return false;
            if (current == null)
                return true;
            if (candidate.TiffPoints.Count != current.TiffPoints.Count)
                return candidate.TiffPoints.Count > current.TiffPoints.Count;
            if (Math.Abs(candidate.Coverage - current.Coverage) > 1e-6)
                return candidate.Coverage > current.Coverage;

            double candidateScaleError = Math.Abs(candidate.Scale - expectedScale);
            double currentScaleError = Math.Abs(current.Scale - expectedScale);
            if (Math.Abs(candidateScaleError - currentScaleError) > 1e-6)
                return candidateScaleError < currentScaleError;

            double width = Math.Max(1.0, tiffImageWidth);
            double candidateOffsetError = Math.Abs(candidate.Offset - centeredOffsetPrior) / width;
            double currentOffsetError = Math.Abs(current.Offset - centeredOffsetPrior) / width;
            if (Math.Abs(candidateOffsetError - currentOffsetError) > 1e-6)
                return candidateOffsetError < currentOffsetError;

            return candidate.MedianResidual < current.MedianResidual - 1e-6;
        }

        private static Mat ComputeRobustTransform(
            List<Point2f> cisPoints,
            List<Point2f> tiffPoints,
            double ransacReprojectionThresholdPixels)
        {
            Mat transform = null;
            // 点数充足时使用 RANSAC Homography；数据不足或求解失败时退到更受约束的仿射模型，
            // 并提升成 3×3 形式，使后续 Warp/Remap 使用统一接口。
            if (cisPoints.Count >= 6)
            {
                using (InputArray src = InputArray.Create(cisPoints))
                using (InputArray dst = InputArray.Create(tiffPoints))
                {
                    transform = Cv2.FindHomography(
                        src,
                        dst,
                        HomographyMethods.Ransac,
                        ransacReprojectionThresholdPixels);
                }
                if (transform != null && !transform.Empty())
                    return transform;
                transform?.Dispose();
            }

            using (var inliers = new Mat())
            using (InputArray src = InputArray.Create(cisPoints))
            using (InputArray dst = InputArray.Create(tiffPoints))
            using (Mat affine = Cv2.EstimateAffine2D(
                src,
                dst,
                inliers,
                RobustEstimationAlgorithms.RANSAC,
                ransacReprojectionThresholdPixels))
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
        /// 在缩小的 CIS 可视化副本上绘制 Bottom 搜索条带和采样圆并编码为 JPEG。
        /// 不修改参与 Warp/差分的 cisMat，避免标注线被后续缺陷检测当成真实图案。
        /// </summary>
    }
}
