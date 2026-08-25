using System;
using System.Collections.Generic;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 模糊二维码定位框恢复，利用定位框模板、候选三角形和局部透视展开处理失焦或边界受损样本。
    /// </summary>
    public sealed partial class QrCodeDetector
    {
        private bool TryDecodeBlurredFinderRecovery(
            Mat source,
            Rect roiRect,
            int sourceOffsetX,
            out QrDetectionResult result,
            out string strategy)
        {
            result = QrDetectionResult.NotFound;
            strategy = null;
            if (source == null || source.Empty() ||
                roiRect.Width < 96 || roiRect.Height < 96)
                return false;

            using (var sourceView = new Mat(source, roiRect))
            using (var searchChannel = new Mat())
            using (var secondaryChannel = new Mat())
            {
                // 绿色通道用于稳定定位三个模板峰；当前困难样本存在明显青色散焦边，
                // 所以找到几何后优先在红色通道解码。灰度输入保持原数据。
                if (sourceView.Channels() == 1)
                    sourceView.CopyTo(searchChannel);
                else
                {
                    Cv2.ExtractChannel(sourceView, searchChannel, 1);
                    Cv2.ExtractChannel(sourceView, secondaryChannel, 2);
                }

                Cv2.MeanStdDev(
                    searchChannel,
                    out Scalar _,
                    out Scalar channelStdDev);
                if (channelStdDev.Val0 < 8.0)
                    return false;

                List<BlurredFinderEvidence> evidence =
                    FindBlurredFinderTemplateEvidence(searchChannel);
                List<BlurredFinderTriple> triples =
                    BuildBlurredFinderTriples(evidence);

                // 白墨完全缺失时，二维码会从“白色前景”变成常规的黑色前景。
                // 主流程按照生产配置反色后，这类黑码会变成白码，常规解码和后续透视恢复
                // 都可能错过。模板阶段已经找到三个黑色定位框并通过直角、尺度和间距约束时，
                // 先保留原始极性，只做一次全局灰度拉伸后交给 WeChatQRCode。
                // 该分支由完整的三定位框几何门控触发，不会让普通无二维码帧无条件多跑一次 DNN。
                if (TryGetOriginalPolarityFinderTriple(
                        triples,
                        out BlurredFinderTriple originalPolarityTriple) &&
                    TryDecodeContrastNormalizedOriginalPolarity(
                        searchChannel,
                        sourceOffsetX,
                        originalPolarityTriple,
                        out result,
                        out strategy))
                {
                    return true;
                }

                int blurredDnnAttempts = 0;
                for (int tripleIndex = 0;
                    tripleIndex < triples.Count;
                    tripleIndex++)
                {
                    BlurredFinderTriple triple = triples[tripleIndex];
                    List<int> moduleCounts = BuildBlurredModuleCountCandidates(
                        triple.EstimatedDimension);
                    for (int moduleIndex = 0;
                        moduleIndex < moduleCounts.Count;
                        moduleIndex++)
                    {
                        int moduleCount = moduleCounts[moduleIndex];
                            Point2f[] codeCorners = BuildBlurredCodeCorners(
                            triple,
                            moduleCount);
                            if (!IsPlausibleBlurredQuadrilateral(
                                codeCorners,
                                searchChannel.Width,
                                searchChannel.Height))
                                continue;

                        int decodeChannelCount = secondaryChannel.Empty() ? 1 : 2;
                        for (int decodeChannelIndex = 0;
                            decodeChannelIndex < decodeChannelCount;
                            decodeChannelIndex++)
                        {
                            Mat decodeChannel = secondaryChannel.Empty()
                                ? searchChannel
                                : decodeChannelIndex == 0
                                    ? secondaryChannel
                                    : searchChannel;
                            string channelName = sourceView.Channels() == 1
                                ? "gray"
                                : decodeChannelIndex == 0 ? "red" : "green";
                            for (int expansionIndex = 0;
                                expansionIndex < BlurredRecoveryExpansionScales.Length;
                                expansionIndex++)
                            {
                                double expansion =
                                    BlurredRecoveryExpansionScales[expansionIndex];
                                Point2f[] expandedCorners = ExpandCorners(
                                    codeCorners,
                                    expansion);
                                if (!TryDecodeBlurredRectified(
                                    decodeChannel,
                                    expandedCorners,
                                    moduleCount,
                                    triple.Inverted,
                                    ref blurredDnnAttempts,
                                    out DecodeHit hit,
                                    out string rectifiedPreprocessing))
                                    continue;

                                double centerX = 0;
                                double centerY = 0;
                                for (int cornerIndex = 0;
                                    cornerIndex < codeCorners.Length;
                                    cornerIndex++)
                                {
                                    centerX += codeCorners[cornerIndex].X;
                                    centerY += codeCorners[cornerIndex].Y;
                                }
                                centerX /= codeCorners.Length;
                                centerY /= codeCorners.Length;

                                double pixelWidth =
                                    (PointDistance(codeCorners[0], codeCorners[1]) +
                                     PointDistance(codeCorners[3], codeCorners[2])) * 0.5;
                                double pixelHeight =
                                    (PointDistance(codeCorners[0], codeCorners[3]) +
                                     PointDistance(codeCorners[1], codeCorners[2])) * 0.5;
                                result = new QrDetectionResult
                                {
                                    Found = true,
                                    CenterX = Math.Max(
                                        0,
                                        (int)Math.Round(centerX + sourceOffsetX)),
                                    CenterY = Math.Max(0, (int)Math.Round(centerY)),
                                    PixelWidth = pixelWidth,
                                    PixelHeight = pixelHeight,
                                    DecodedText = hit.Text
                                };
                                strategy =
                                    $"WeChatQRCode, blurred-finder-template, channel=" +
                                    $"{channelName}, " +
                                    $"polarity={(triple.Inverted ? "inverted" : "original")}, " +
                                    $"templateScore={triple.AverageTemplateScore:F3}, " +
                                    $"rightAngleCosine={triple.RightAngleCosine:F3}, " +
                                    $"estimatedDimension={triple.EstimatedDimension:F1}, " +
                                    $"moduleCount={moduleCount}, expansion={expansion:F3}, " +
                                    $"rectified={rectifiedPreprocessing}";
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 判断候选中是否存在“黑色模块、较亮背景”的三定位框组合。
        /// BlurredFinderEvidence.Inverted=false 对应模板正相关，也就是标准黑码极性。
        /// </summary>
        private static bool TryGetOriginalPolarityFinderTriple(
            List<BlurredFinderTriple> triples,
            out BlurredFinderTriple originalPolarityTriple)
        {
            originalPolarityTriple = null;
            if (triples == null)
                return false;

            for (int i = 0; i < triples.Count; i++)
            {
                if (!triples[i].Inverted)
                {
                    originalPolarityTriple = triples[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 对已由三定位框几何确认的黑码执行一次轻量恢复。
        /// Normalize 只扩展当前 ROI 的灰度动态范围，不改变极性、不二值化数据模块；
        /// 最终仍必须由 WeChatQRCode 解出非空文本后才算识别成功。
        /// </summary>
        private bool TryDecodeContrastNormalizedOriginalPolarity(
            Mat source,
            int sourceOffsetX,
            BlurredFinderTriple diagnosticTriple,
            out QrDetectionResult result,
            out string strategy)
        {
            result = QrDetectionResult.NotFound;
            strategy = null;
            if (source == null || source.Empty())
                return false;

            using (var normalized = new Mat())
            {
                Cv2.Normalize(source, normalized, 0, 255, NormTypes.MinMax);
                if (!TryDecode(normalized, 1.0, out DecodeHit hit))
                    return false;

                result = new QrDetectionResult
                {
                    Found = true,
                    CenterX = Math.Max(
                        0,
                        (int)Math.Round(hit.CenterX + sourceOffsetX)),
                    CenterY = Math.Max(0, (int)Math.Round(hit.CenterY)),
                    PixelWidth = hit.PixelWidth,
                    PixelHeight = hit.PixelHeight,
                    DecodedText = hit.Text
                };
                strategy =
                    "WeChatQRCode, contrast-normalized-original-polarity, " +
                    $"templateScore={diagnosticTriple.AverageTemplateScore:F3}, " +
                    $"rightAngleCosine={diagnosticTriple.RightAngleCosine:F3}";
                return true;
            }
        }

        /// <summary>
        /// 在最长边 640 px 的工作图上匹配 7×7 定位框模板。
        /// 正相关代表黑码白底，负相关代表白码黑底；两种极性共用一次模板计算。
        /// </summary>
        private static List<BlurredFinderEvidence>
            FindBlurredFinderTemplateEvidence(Mat source)
        {
            var raw = new List<BlurredFinderEvidence>();
            double searchScale = Math.Min(
                1.0,
                BlurredFinderSearchLongEdge /
                (double)Math.Max(source.Width, source.Height));
            using (var searchImage = new Mat())
            {
                if (searchScale < 1.0 - ScaleEpsilon)
                {
                    Cv2.Resize(
                        source,
                        searchImage,
                        new OpenCvSharp.Size(
                            Math.Max(64, (int)Math.Round(source.Width * searchScale)),
                            Math.Max(64, (int)Math.Round(source.Height * searchScale))),
                        0,
                        0,
                        InterpolationFlags.Area);
                }
                else
                {
                    source.CopyTo(searchImage);
                }

                for (int scaleIndex = 0;
                    scaleIndex < BlurredFinderTemplateModuleSizes.Length;
                    scaleIndex++)
                {
                    int moduleSize =
                        BlurredFinderTemplateModuleSizes[scaleIndex];
                    int templateSide = moduleSize * 7;
                    if (templateSide >= searchImage.Width ||
                        templateSide >= searchImage.Height)
                        continue;

                    using (var idealTemplate = CreateBlurredFinderTemplate(
                        moduleSize))
                    using (var response = new Mat())
                    {
                        Cv2.MatchTemplate(
                            searchImage,
                            idealTemplate,
                            response,
                            TemplateMatchModes.CCoeffNormed);
                        CollectBlurredFinderPeaks(
                            response,
                            moduleSize,
                            templateSide,
                            searchScale,
                            false,
                            raw);
                        CollectBlurredFinderPeaks(
                            response,
                            moduleSize,
                            templateSide,
                            searchScale,
                            true,
                            raw);
                    }
                }
            }

            raw.Sort((left, right) => right.Score.CompareTo(left.Score));
            var distinct = new List<BlurredFinderEvidence>();
            for (int i = 0;
                i < raw.Count && distinct.Count < BlurredFinderMaxEvidence;
                i++)
            {
                BlurredFinderEvidence candidate = raw[i];
                bool duplicate = false;
                for (int j = 0; j < distinct.Count; j++)
                {
                    BlurredFinderEvidence accepted = distinct[j];
                    if (candidate.Inverted != accepted.Inverted)
                        continue;

                    double dx = candidate.Center.X - accepted.Center.X;
                    double dy = candidate.Center.Y - accepted.Center.Y;
                    double mergeDistance = Math.Max(
                        candidate.ModuleSize,
                        accepted.ModuleSize) * 7.0 * 0.45;
                    if (dx * dx + dy * dy <= mergeDistance * mergeDistance)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    distinct.Add(candidate);
            }
            return distinct;
        }

        private static Mat CreateBlurredFinderTemplate(int moduleSize)
        {
            int side = moduleSize * 7;
            var blurred = new Mat();
            using (var ideal = new Mat(
                side,
                side,
                MatType.CV_8UC1,
                Scalar.All(255)))
            {
                for (int row = 0; row < 7; row++)
                {
                    for (int column = 0; column < 7; column++)
                    {
                        bool dark =
                            row == 0 || row == 6 ||
                            column == 0 || column == 6 ||
                            (row >= 2 && row <= 4 &&
                             column >= 2 && column <= 4);
                        if (!dark)
                            continue;

                        Cv2.Rectangle(
                            ideal,
                            new Rect(
                                column * moduleSize,
                                row * moduleSize,
                                moduleSize,
                                moduleSize),
                            Scalar.All(0),
                            -1);
                    }
                }

                Cv2.GaussianBlur(
                    ideal,
                    blurred,
                    new OpenCvSharp.Size(0, 0),
                    Math.Max(0.8, moduleSize * 0.22));
            }
            return blurred;
        }

        private static void CollectBlurredFinderPeaks(
            Mat response,
            int moduleSize,
            int templateSide,
            double searchScale,
            bool inverted,
            List<BlurredFinderEvidence> output)
        {
            using (var work = response.Clone())
            {
                for (int peakIndex = 0;
                    peakIndex < BlurredFinderPeaksPerScale;
                    peakIndex++)
                {
                    Cv2.MinMaxLoc(
                        work,
                        out double minimum,
                        out double maximum,
                        out Point minimumLocation,
                        out Point maximumLocation);
                    double score = inverted ? -minimum : maximum;
                    Point location = inverted
                        ? minimumLocation
                        : maximumLocation;
                    if (score < BlurredFinderMinimumScore)
                        break;

                    output.Add(new BlurredFinderEvidence
                    {
                        Center = new Point2f(
                            (float)((location.X + templateSide * 0.5) /
                                searchScale),
                            (float)((location.Y + templateSide * 0.5) /
                                searchScale)),
                        ModuleSize = moduleSize / searchScale,
                        Score = score,
                        Inverted = inverted
                    });

                    int suppressionRadius = templateSide / 2;
                    int left = Math.Max(0, location.X - suppressionRadius);
                    int top = Math.Max(0, location.Y - suppressionRadius);
                    int right = Math.Min(
                        work.Width,
                        location.X + templateSide + suppressionRadius);
                    int bottom = Math.Min(
                        work.Height,
                        location.Y + templateSide + suppressionRadius);
                    if (right <= left || bottom <= top)
                        break;
                    Cv2.Rectangle(
                        work,
                        new Rect(left, top, right - left, bottom - top),
                        inverted ? Scalar.All(1.0) : Scalar.All(-1.0),
                        -1);
                }
            }
        }

        private static List<BlurredFinderTriple> BuildBlurredFinderTriples(
            List<BlurredFinderEvidence> evidence)
        {
            var triples = new List<BlurredFinderTriple>();
            if (evidence == null || evidence.Count < 3)
                return triples;

            for (int firstIndex = 0;
                firstIndex < evidence.Count - 2;
                firstIndex++)
            {
                for (int secondIndex = firstIndex + 1;
                    secondIndex < evidence.Count - 1;
                    secondIndex++)
                {
                    for (int thirdIndex = secondIndex + 1;
                        thirdIndex < evidence.Count;
                        thirdIndex++)
                    {
                        BlurredFinderEvidence[] current =
                        {
                            evidence[firstIndex],
                            evidence[secondIndex],
                            evidence[thirdIndex]
                        };
                        if (current[0].Inverted != current[1].Inverted ||
                            current[0].Inverted != current[2].Inverted)
                            continue;

                        double minimumModule = Math.Min(
                            current[0].ModuleSize,
                            Math.Min(
                                current[1].ModuleSize,
                                current[2].ModuleSize));
                        double maximumModule = Math.Max(
                            current[0].ModuleSize,
                            Math.Max(
                                current[1].ModuleSize,
                                current[2].ModuleSize));
                        if (minimumModule <= 0 ||
                            maximumModule / minimumModule > 1.60)
                            continue;

                        for (int cornerIndex = 0;
                            cornerIndex < 3;
                            cornerIndex++)
                        {
                            BlurredFinderEvidence corner = current[cornerIndex];
                            BlurredFinderEvidence first =
                                current[(cornerIndex + 1) % 3];
                            BlurredFinderEvidence second =
                                current[(cornerIndex + 2) % 3];
                            Point2f firstVector = first.Center - corner.Center;
                            Point2f secondVector = second.Center - corner.Center;
                            double firstLength = Math.Sqrt(
                                firstVector.X * firstVector.X +
                                firstVector.Y * firstVector.Y);
                            double secondLength = Math.Sqrt(
                                secondVector.X * secondVector.X +
                                secondVector.Y * secondVector.Y);
                            if (firstLength <= maximumModule * 8.0 ||
                                secondLength <= maximumModule * 8.0)
                                continue;

                            double cosine = Math.Abs(
                                (firstVector.X * secondVector.X +
                                 firstVector.Y * secondVector.Y) /
                                (firstLength * secondLength));
                            double legRatio =
                                Math.Max(firstLength, secondLength) /
                                Math.Min(firstLength, secondLength);
                            if (cosine > 0.28 || legRatio > 1.35)
                                continue;

                            double[] moduleSizes =
                            {
                                current[0].ModuleSize,
                                current[1].ModuleSize,
                                current[2].ModuleSize
                            };
                            Array.Sort(moduleSizes);
                            double medianModule = moduleSizes[1];
                            double estimatedDimension =
                                (firstLength + secondLength) * 0.5 /
                                medianModule + 7.0;
                            if (estimatedDimension < 17.0 ||
                                estimatedDimension > 65.0)
                                continue;

                            double averageTemplateScore =
                                (current[0].Score +
                                 current[1].Score +
                                 current[2].Score) / 3.0;
                            triples.Add(new BlurredFinderTriple
                            {
                                Corner = corner,
                                FirstNeighbor = first,
                                SecondNeighbor = second,
                                Inverted = corner.Inverted,
                                RightAngleCosine = cosine,
                                EstimatedDimension = estimatedDimension,
                                AverageTemplateScore = averageTemplateScore,
                                Score =
                                    cosine +
                                    Math.Abs(Math.Log(legRatio)) +
                                    Math.Abs(Math.Log(
                                        maximumModule / minimumModule)) +
                                    (1.0 - averageTemplateScore)
                            });
                        }
                    }
                }
            }

            triples.Sort((left, right) => left.Score.CompareTo(right.Score));
            if (triples.Count > BlurredFinderMaxTriples)
                triples.RemoveRange(
                    BlurredFinderMaxTriples,
                    triples.Count - BlurredFinderMaxTriples);
            return triples;
        }

        private static List<int> BuildBlurredModuleCountCandidates(
            double estimatedDimension)
        {
            int estimatedVersion = Math.Max(
                0,
                Math.Min(
                    9,
                    (int)Math.Round((estimatedDimension - 21.0) / 4.0)));
            var result = new List<int>();
            // 失焦会把定位框黑白环融合，使模板最佳模块尺寸偏小、推算版本偏大。
            // 因此先试低一级版本，再试四舍五入版本和高一级版本。
            int[] versionOffsets = { -1, 0, 1 };
            for (int i = 0; i < versionOffsets.Length; i++)
            {
                int version = estimatedVersion + versionOffsets[i];
                if (version < 0 || version > 9)
                    continue;
                int moduleCount = 21 + version * 4;
                if (!result.Contains(moduleCount))
                    result.Add(moduleCount);
            }
            return result;
        }

        private static Point2f[] BuildBlurredCodeCorners(
            BlurredFinderTriple triple,
            int moduleCount)
        {
            double centerSpan = moduleCount - 7.0;
            Point2f firstAxis = ScaleVector(
                triple.FirstNeighbor.Center - triple.Corner.Center,
                1.0 / centerSpan);
            Point2f secondAxis = ScaleVector(
                triple.SecondNeighbor.Center - triple.Corner.Center,
                1.0 / centerSpan);
            const double near = -3.5;
            double far = moduleCount - 3.5;
            return OrderQuadrilateralCorners(new[]
            {
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, near),
                    ScaleVector(secondAxis, near)),
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, far),
                    ScaleVector(secondAxis, near)),
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, far),
                    ScaleVector(secondAxis, far)),
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, near),
                    ScaleVector(secondAxis, far))
            });
        }

        private static Point2f[] ExpandCorners(
            Point2f[] corners,
            double scale)
        {
            float centerX = 0;
            float centerY = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                centerX += corners[i].X;
                centerY += corners[i].Y;
            }
            centerX /= corners.Length;
            centerY /= corners.Length;

            var expanded = new Point2f[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                expanded[i] = new Point2f(
                    centerX + (float)((corners[i].X - centerX) * scale),
                    centerY + (float)((corners[i].Y - centerY) * scale));
            }
            return expanded;
        }

        private static bool IsPlausibleBlurredQuadrilateral(
            Point2f[] corners,
            int sourceWidth,
            int sourceHeight)
        {
            if (corners == null || corners.Length != 4)
                return false;

            double width =
                (PointDistance(corners[0], corners[1]) +
                 PointDistance(corners[3], corners[2])) * 0.5;
            double height =
                (PointDistance(corners[0], corners[3]) +
                 PointDistance(corners[1], corners[2])) * 0.5;
            if (width < 96 || height < 96 ||
                Math.Max(width, height) / Math.Min(width, height) > 1.50)
                return false;

            double allowedOutside =
                Math.Max(sourceWidth, sourceHeight) * 0.15;
            for (int i = 0; i < corners.Length; i++)
            {
                if (corners[i].X < -allowedOutside ||
                    corners[i].Y < -allowedOutside ||
                    corners[i].X > sourceWidth + allowedOutside ||
                    corners[i].Y > sourceHeight + allowedOutside)
                    return false;
            }
            return true;
        }

        private bool TryDecodeBlurredRectified(
            Mat source,
            Point2f[] corners,
            int moduleCount,
            bool invertPolarity,
            ref int blurredDnnAttempts,
            out DecodeHit hit,
            out string preprocessing)
        {
            hit = null;
            preprocessing = null;
            int codeSide = moduleCount * BlurredRecoveryPixelsPerModule;
            int quietZone = 4 * BlurredRecoveryPixelsPerModule;
            Point2f[] destination =
            {
                new Point2f(0, 0),
                // 以模块边界而不是最后一个像素中心建立变换；这样每个模块正好占据
                // BlurredRecoveryPixelsPerModule 个像素，避免大失焦样本在边缘累积半像素相位误差。
                new Point2f(codeSide, 0),
                new Point2f(codeSide, codeSide),
                new Point2f(0, codeSide)
            };

            using (Mat transform = Cv2.GetPerspectiveTransform(
                corners,
                destination))
            using (var straight = new Mat())
            using (var normalizedPolarity = new Mat())
            using (var padded = new Mat())
            using (var normalized = new Mat())
            {
                Cv2.WarpPerspective(
                    source,
                    straight,
                    transform,
                    new OpenCvSharp.Size(codeSide, codeSide),
                    InterpolationFlags.Cubic,
                    BorderTypes.Replicate);
                if (invertPolarity)
                    Cv2.BitwiseNot(straight, normalizedPolarity);
                else
                    straight.CopyTo(normalizedPolarity);

                // 模板匹配只说明三个局部区域“像定位框”，不足以证明整块区域是二维码。
                // 真正的二维码还必须同时包含三个 7×7 定位框，以及从它们之间穿过的
                // 横、纵交替时序线。先在 moduleCount×moduleCount 网格上核对这些固定结构，
                // 普通文字/花纹在这里会被快速拒绝，不再反复调用 DNN 解码器。
                if (!HasExpectedQrModuleStructure(
                    normalizedPolarity,
                    moduleCount,
                    out double structureScore))
                    return false;

                if (blurredDnnAttempts >= BlurredRecoveryMaxDnnAttempts)
                    return false;

                Cv2.CopyMakeBorder(
                    normalizedPolarity,
                    padded,
                    quietZone,
                    quietZone,
                    quietZone,
                    quietZone,
                    BorderTypes.Constant,
                    Scalar.All(255));
                Cv2.Normalize(padded, normalized, 0, 255, NormTypes.MinMax);
                blurredDnnAttempts++;
                if (TryDecode(normalized, 1.0, out hit))
                {
                    preprocessing = $"normalized-gray, structure={structureScore:F3}";
                    return true;
                }

                using (var background = new Mat())
                using (var flattened = new Mat())
                {
                    if (blurredDnnAttempts >= BlurredRecoveryMaxDnnAttempts)
                        return false;

                    Cv2.GaussianBlur(
                        normalized,
                        background,
                        new OpenCvSharp.Size(0, 0),
                        BlurredRecoveryPixelsPerModule * 1.8);
                    Cv2.Divide(normalized, background, flattened, 255.0);
                    blurredDnnAttempts++;
                    if (!TryDecode(flattened, 1.0, out hit))
                        return false;
                    preprocessing = $"illumination-flattened, structure={structureScore:F3}";
                    return true;
                }
            }
        }

        /// <summary>
        /// 校验候选展开图是否具有二维码固定模块结构。这里不读取数据区，也不承担解码：
        /// 只检查左上、右上、左下三个定位框和横纵时序线，因此不会把某段业务文本写死到算法中。
        /// </summary>
        private static bool HasExpectedQrModuleStructure(
            Mat normalizedCode,
            int moduleCount,
            out double score)
        {
            score = 0;
            if (normalizedCode == null || normalizedCode.Empty() ||
                normalizedCode.Channels() != 1 || moduleCount < 21)
                return false;

            // Area 缩小后每个像素对应一个二维码模块的平均灰度。候选已经透视展开，
            // 所以无需逐模块创建 ROI，可把几何校验成本控制在亚毫秒级。
            using (var moduleGrid = new Mat())
            {
                Cv2.Resize(
                    normalizedCode,
                    moduleGrid,
                    new OpenCvSharp.Size(moduleCount, moduleCount),
                    0,
                    0,
                    InterpolationFlags.Area);

                int[,] finderOrigins =
                {
                    { 0, 0 },
                    { 0, moduleCount - 7 },
                    { moduleCount - 7, 0 }
                };
                double expectedDarkSum = 0;
                double expectedLightSum = 0;
                int expectedDarkCount = 0;
                int expectedLightCount = 0;
                for (int finder = 0; finder < 3; finder++)
                {
                    int originRow = finderOrigins[finder, 0];
                    int originColumn = finderOrigins[finder, 1];
                    for (int row = 0; row < 7; row++)
                    {
                        for (int column = 0; column < 7; column++)
                        {
                            bool expectedDark = IsFinderDarkModule(row, column);
                            byte value = moduleGrid.At<byte>(
                                originRow + row,
                                originColumn + column);
                            if (expectedDark)
                            {
                                expectedDarkSum += value;
                                expectedDarkCount++;
                            }
                            else
                            {
                                expectedLightSum += value;
                                expectedLightCount++;
                            }
                        }
                    }
                }

                double darkMean = expectedDarkSum / Math.Max(1, expectedDarkCount);
                double lightMean = expectedLightSum / Math.Max(1, expectedLightCount);
                if (lightMean - darkMean < BlurredStructureMinimumContrast)
                    return false;
                double decisionThreshold = (darkMean + lightMean) * 0.5;

                var finderAgreements = new double[3];
                for (int finder = 0; finder < 3; finder++)
                {
                    int correct = 0;
                    int originRow = finderOrigins[finder, 0];
                    int originColumn = finderOrigins[finder, 1];
                    for (int row = 0; row < 7; row++)
                    {
                        for (int column = 0; column < 7; column++)
                        {
                            bool expectedDark = IsFinderDarkModule(row, column);
                            bool observedDark = moduleGrid.At<byte>(
                                originRow + row,
                                originColumn + column) <= decisionThreshold;
                            if (observedDark == expectedDark)
                                correct++;
                        }
                    }
                    finderAgreements[finder] = correct / 49.0;
                }

                Array.Sort(finderAgreements);
                // 允许一个定位框局部缺损，但至少两个定位框必须保留足够清晰的固定结构。
                if (finderAgreements[1] < BlurredStructureMinimumFinderAgreement)
                    return false;

                int timingStart = 8;
                int timingEnd = moduleCount - 9;
                if (timingEnd < timingStart)
                    return false;
                int horizontalCorrect = 0;
                int verticalCorrect = 0;
                int timingCount = timingEnd - timingStart + 1;
                for (int index = timingStart; index <= timingEnd; index++)
                {
                    bool expectedDark = ((index - timingStart) & 1) == 0;
                    bool horizontalDark =
                        moduleGrid.At<byte>(6, index) <= decisionThreshold;
                    bool verticalDark =
                        moduleGrid.At<byte>(index, 6) <= decisionThreshold;
                    if (horizontalDark == expectedDark)
                        horizontalCorrect++;
                    if (verticalDark == expectedDark)
                        verticalCorrect++;
                }

                double horizontalAgreement = horizontalCorrect / (double)timingCount;
                double verticalAgreement = verticalCorrect / (double)timingCount;
                double timingAgreement = (horizontalAgreement + verticalAgreement) * 0.5;
                if (timingAgreement < BlurredStructureMinimumTimingAgreement)
                    return false;

                double finderAgreement =
                    (finderAgreements[0] + finderAgreements[1] + finderAgreements[2]) / 3.0;
                score = finderAgreement * 0.70 + timingAgreement * 0.30;
                return true;
            }
        }

        /// <summary>二维码 7×7 定位框的固定黑模块布局。</summary>
        private static bool IsFinderDarkModule(int row, int column)
        {
            return row == 0 || row == 6 || column == 0 || column == 6 ||
                   (row >= 2 && row <= 4 && column >= 2 && column <= 4);
        }

        /// <summary>
        /// 在配置极性的完整流程失败后，针对低对比度反极性二维码执行一次受限恢复。
        /// Gaussian/Otsu 只提供四角几何，最终文本始终从原灰度局部图由 WeChatQRCode 解出。
        /// </summary>
        private bool TryDecodeLowContrastOppositePolarity(
            Mat originalRoi,
            bool primaryInverted,
            int safeX,
            int finderEvidenceCount,
            out QrDetectionResult result,
            out string strategy)
        {
            result = null;
            strategy = null;
            Mat alternativeOwned = null;
            try
            {
                Mat alternativePolarity;
                if (primaryInverted)
                {
                    // 主流程使用了反色图，备用路径回到传感器原始灰度。
                    alternativePolarity = originalRoi;
                }
                else
                {
                    // 配置为原极性时，备用路径只构造一次反色 Mat。
                    alternativeOwned = new Mat();
                    Cv2.BitwiseNot(originalRoi, alternativeOwned);
                    alternativePolarity = alternativeOwned;
                }

                if (!TryFindLowContrastCandidateRegion(
                    alternativePolarity,
                    out QrCandidateRegion candidateRegion))
                    return false;

                using (var candidateView =
                    new Mat(alternativePolarity, candidateRegion.SourceRoi))
                {
                    Mat candidateInput = candidateView;
                    Mat paddedCandidate = null;
                    try
                    {
                        if (candidateRegion.HasPadding)
                        {
                            paddedCandidate = new Mat();
                            Cv2.CopyMakeBorder(
                                candidateView,
                                paddedCandidate,
                                candidateRegion.PadTop,
                                candidateRegion.PadBottom,
                                candidateRegion.PadLeft,
                                candidateRegion.PadRight,
                                BorderTypes.Constant,
                                Scalar.All(255));
                            candidateInput = paddedCandidate;
                        }

                        double scaleX =
                            LowContrastTargetQrSide /
                            candidateRegion.EstimatedCodeSide;
                        double scaleY =
                            scaleX *
                            Clamp(
                                candidateRegion.GeometricRelativeScaleY * 0.70,
                                AdaptiveMinRelativeScaleY,
                                AdaptiveMaxRelativeScaleY);
                        // 低对比度路径有意把约 700 px 的颗粒码降到约 224 px，
                        // 所需比例可能小于常规自适应路径的 0.35 下限，因此在本地单独校验。
                        if (scaleX < 0.20 || scaleX > AdaptiveMaxScale ||
                            scaleY < 0.20 || scaleY > AdaptiveMaxScale)
                            return false;

                        if (!TryDecodeResampled(
                            candidateInput,
                            scaleX,
                            scaleY,
                            out DecodeHit hit))
                            return false;

                        result = CreateCandidateResult(
                            hit,
                            safeX,
                            candidateRegion,
                            1.0,
                            1.0);
                        strategy =
                            $"WeChatQRCode, low-contrast-opposite-polarity, " +
                            $"finderEvidence={finderEvidenceCount}, locator=gaussian5-otsu, " +
                            $"roi={candidateRegion.SourceRoi.X}," +
                            $"{candidateRegion.SourceRoi.Y}," +
                            $"{candidateRegion.SourceRoi.Width}x" +
                            $"{candidateRegion.SourceRoi.Height}, " +
                            $"padding={candidateRegion.PadLeft}," +
                            $"{candidateRegion.PadTop}," +
                            $"{candidateRegion.PadRight}," +
                            $"{candidateRegion.PadBottom}, " +
                            $"estimatedSide={candidateRegion.EstimatedCodeSide:F1}, " +
                            $"targetSide={LowContrastTargetQrSide:F1}, " +
                            $"geometryScaleY={candidateRegion.GeometricRelativeScaleY:F3}, " +
                            $"scaleX={scaleX:F3}, scaleY={scaleY:F3}";
                        return true;
                    }
                    finally
                    {
                        paddedCandidate?.Dispose();
                    }
                }
            }
            finally
            {
                alternativeOwned?.Dispose();
            }
        }

        /// <summary>
        /// 使用 OpenCV 的经典二维码几何定位器取得四个角点，但不采用它的解码文本。
        /// 扩展后的局部区域仍交给 WeChatQRCode 做唯一的业务解码，避免维护两套结果规则。
        /// </summary>
        private static bool TryFindQrCandidateRegion(
            Mat source,
            out QrCandidateRegion candidateRegion)
        {
            candidateRegion = null;
            if (source == null || source.Empty() || source.Width < 64 || source.Height < 64)
                return false;

            using (var locator = new QRCodeDetector())
            {
                Point2f[] corners;
                try
                {
                    if (!locator.Detect(source, out corners))
                        return false;
                }
                catch
                {
                    // 几何定位器只是可选增强路径。个别纹理可能触发 OpenCV 内部断言，
                    // 此时按“没有候选框”处理，不能把正常的无二维码帧升级为采集异常。
                    return false;
                }

                return TryCreateQrCandidateRegion(
                    corners,
                    source.Width,
                    source.Height,
                    out candidateRegion);
            }
        }

        /// <summary>
        /// 低对比度黑码的模块纹理会破坏经典定位器的边缘连续性。先轻度模糊颗粒，
        /// 再用 Otsu 生成仅供几何定位的二值图；最终解码仍使用未经二值化的灰度局部图。
        /// </summary>
        private static bool TryFindLowContrastCandidateRegion(
            Mat source,
            out QrCandidateRegion candidateRegion)
        {
            candidateRegion = null;
            if (source == null || source.Empty() || source.Width < 64 || source.Height < 64)
                return false;

            using (var blurred = new Mat())
            using (var binary = new Mat())
            using (var locator = new QRCodeDetector())
            {
                Cv2.GaussianBlur(source, blurred, new OpenCvSharp.Size(5, 5), 0);
                Cv2.Threshold(
                    blurred,
                    binary,
                    0,
                    255,
                    ThresholdTypes.Binary | ThresholdTypes.Otsu);

                Point2f[] corners;
                try
                {
                    if (!locator.Detect(binary, out corners))
                        return false;
                }
                catch
                {
                    return false;
                }

                return TryCreateQrCandidateRegion(
                    corners,
                    source.Width,
                    source.Height,
                    out candidateRegion);
            }
        }

        /// <summary>把四角坐标转换为带理论静区和边界补白信息的局部候选。</summary>
        private static bool TryCreateQrCandidateRegion(
            Point2f[] corners,
            int sourceWidth,
            int sourceHeight,
            out QrCandidateRegion candidateRegion)
        {
            candidateRegion = null;
            if (corners == null || corners.Length < 4)
                return false;

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                double x = corners[i].X;
                double y = corners[i].Y;
                if (double.IsNaN(x) || double.IsInfinity(x) ||
                    double.IsNaN(y) || double.IsInfinity(y))
                    return false;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            double codeWidth = maxX - minX;
            double codeHeight = maxY - minY;
            if (codeWidth < 32 || codeHeight < 32)
                return false;

            // 四角点仅覆盖码区，向外保留约 20% 静区。这里先保留“理论区域”，
            // 再与真实图像求交，因此可以知道左/右/上/下究竟缺了多少像素。
            int quietZone = Math.Max(
                8,
                (int)Math.Round(Math.Max(codeWidth, codeHeight) * FinderQuietZoneScale));
            int rawX0 = (int)Math.Floor(minX) - quietZone;
            int rawY0 = (int)Math.Floor(minY) - quietZone;
            int rawX1 = (int)Math.Ceiling(maxX) + quietZone;
            int rawY1 = (int)Math.Ceiling(maxY) + quietZone;
            int rawWidth = rawX1 - rawX0;
            int rawHeight = rawY1 - rawY0;
            if (rawWidth < 64 || rawHeight < 64 ||
                rawWidth > sourceWidth * 2 ||
                rawHeight > sourceHeight * 2)
                return false;

            int x0 = Math.Max(0, rawX0);
            int y0 = Math.Max(0, rawY0);
            int x1 = Math.Min(sourceWidth, rawX1);
            int y1 = Math.Min(sourceHeight, rawY1);
            if (x1 - x0 < 64 || y1 - y0 < 64)
                return false;

            candidateRegion = new QrCandidateRegion
            {
                SourceRoi = new Rect(x0, y0, x1 - x0, y1 - y0),
                PadLeft = x0 - rawX0,
                PadTop = y0 - rawY0,
                PadRight = rawX1 - x1,
                PadBottom = rawY1 - y1,
                EstimatedCodeSide = Math.Max(codeWidth, codeHeight),
                GeometricRelativeScaleY = Clamp(
                    codeWidth / codeHeight,
                    AdaptiveMinRelativeScaleY,
                    AdaptiveMaxRelativeScaleY)
            };
            return true;
        }

        /// <summary>
        /// 把局部候选中的识别坐标还原到完整帧：先撤销局部缩放，再扣除人工补出的静区，
        /// 最后叠加真实 ROI 偏移。这样补白只服务于解码，不会改变拼接使用的二维码全局坐标。
        /// </summary>
        private static QrDetectionResult CreateCandidateResult(
            DecodeHit hit,
            int safeX,
            QrCandidateRegion candidateRegion,
            double scaleX,
            double scaleY)
        {
            double centerX =
                safeX +
                candidateRegion.SourceRoi.X +
                hit.CenterX / scaleX -
                candidateRegion.PadLeft;
            double centerY =
                candidateRegion.SourceRoi.Y +
                hit.CenterY / scaleY -
                candidateRegion.PadTop;
            return new QrDetectionResult
            {
                Found = true,
                CenterX = Math.Max(0, (int)Math.Round(centerX)),
                CenterY = Math.Max(0, (int)Math.Round(centerY)),
                PixelWidth = hit.PixelWidth / scaleX,
                PixelHeight = hit.PixelHeight / scaleY,
                DecodedText = hit.Text
            };
        }

        /// <summary>
        /// 从轮廓树提取二维码定位框候选，并按中心距离/重叠关系去除同一定位框的内外层重复轮廓。
        /// 这里只提供几何证据，不直接决定二维码是否存在，最终文本仍必须由 WeChatQRCode 解出。
        /// </summary>
        /// <summary>
        /// 使用三个定位框恢复二维码平面。展平图只用于寻找几何，透视变换和解码始终使用原灰度图。
        /// 该分支只在常规流程失败后执行，并要求三个同尺度定位框构成近似直角，避免把普通嵌套图案送入解码器。
        /// </summary>
    }
}
