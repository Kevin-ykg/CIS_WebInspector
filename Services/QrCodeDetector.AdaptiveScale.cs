using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 二维码几何证据与自适应尺度，依据定位框大小和 ROI 占比生成受限的缩放候选。
    /// </summary>
    public sealed partial class QrCodeDetector
    {
        private static List<FinderPatternEvidence> FindFinderPatternEvidence(Mat source)
        {
            using (var binary = new Mat())
            {
                Cv2.Threshold(
                    source,
                    binary,
                    0,
                    255,
                    ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Cv2.FindContours(
                    binary,
                    out Point[][] contours,
                    out HierarchyIndex[] hierarchy,
                    RetrievalModes.Tree,
                    ContourApproximationModes.ApproxSimple);

                var rawEvidence = new List<FinderPatternEvidence>();
                if (contours == null || hierarchy == null || contours.Length != hierarchy.Length)
                    return rawEvidence;

                int minSide = Math.Max(12, Math.Min(source.Width, source.Height) / 120);
                int maxSide = Math.Max(minSide + 1, (int)Math.Round(
                    Math.Min(source.Width, source.Height) * 0.45));
                for (int i = 0; i < contours.Length; i++)
                {
                    Rect bounds = Cv2.BoundingRect(contours[i]);
                    int shortSide = Math.Min(bounds.Width, bounds.Height);
                    int longSide = Math.Max(bounds.Width, bounds.Height);
                    double aspect = bounds.Width / (double)Math.Max(1, bounds.Height);
                    if (shortSide < minSide ||
                        longSide > maxSide ||
                        aspect < 0.65 ||
                        aspect > 1.50)
                        continue;

                    int nestedDepth = 0;
                    int child = hierarchy[i].Child;
                    while (child >= 0 && child < hierarchy.Length && nestedDepth < 4)
                    {
                        nestedDepth++;
                        child = hierarchy[child].Child;
                    }

                    if (nestedDepth < 2)
                        continue;

                    rawEvidence.Add(new FinderPatternEvidence
                    {
                        Bounds = bounds,
                        CenterX = bounds.X + bounds.Width * 0.5,
                        CenterY = bounds.Y + bounds.Height * 0.5,
                        EquivalentSide = Math.Sqrt((double)bounds.Width * bounds.Height)
                    });
                }

                // 同一定位框通常会贡献多层嵌套轮廓。先保留面积更大的外层，再剔除中心接近或高度重叠的内层。
                rawEvidence.Sort((left, right) =>
                    (right.Bounds.Width * right.Bounds.Height).CompareTo(
                        left.Bounds.Width * left.Bounds.Height));
                var distinctEvidence = new List<FinderPatternEvidence>();
                for (int i = 0; i < rawEvidence.Count; i++)
                {
                    FinderPatternEvidence candidate = rawEvidence[i];
                    bool duplicate = false;
                    for (int j = 0; j < distinctEvidence.Count; j++)
                    {
                        FinderPatternEvidence accepted = distinctEvidence[j];
                        double dx = candidate.CenterX - accepted.CenterX;
                        double dy = candidate.CenterY - accepted.CenterY;
                        double mergeDistance =
                            Math.Max(candidate.EquivalentSide, accepted.EquivalentSide) * 0.45;
                        if (dx * dx + dy * dy <= mergeDistance * mergeDistance ||
                            CalculateIntersectionOverUnion(candidate.Bounds, accepted.Bounds) >= 0.30)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        distinctEvidence.Add(candidate);
                }

                return distinctEvidence;
            }
        }

        /// <summary>
        /// 从至少两个尺寸一致、间距合理的定位框估算二维码原始边长和Y方向比例，
        /// 再把二维码归一化到有限的解码工作尺寸。缩放系数由当前图像实时计算，而非绑定具体样本。
        /// </summary>
        private static bool TryBuildAdaptiveScaleCandidates(
            List<FinderPatternEvidence> evidence,
            out List<AdaptiveScaleCandidate> candidates,
            out int coherentFinderCount,
            out double estimatedQrSide,
            out double geometricRelativeScaleY)
        {
            candidates = new List<AdaptiveScaleCandidate>();
            coherentFinderCount = 0;
            estimatedQrSide = 0;
            geometricRelativeScaleY = 1.0;
            if (evidence == null || evidence.Count < 2)
                return false;

            int bestFirst = -1;
            int bestSecond = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < evidence.Count - 1; i++)
            {
                for (int j = i + 1; j < evidence.Count; j++)
                {
                    FinderPatternEvidence first = evidence[i];
                    FinderPatternEvidence second = evidence[j];
                    double smallerSide = Math.Min(first.EquivalentSide, second.EquivalentSide);
                    double largerSide = Math.Max(first.EquivalentSide, second.EquivalentSide);
                    if (smallerSide <= 0 || largerSide / smallerSide > 1.65)
                        continue;

                    double dx = second.CenterX - first.CenterX;
                    double dy = second.CenterY - first.CenterY;
                    double separation = Math.Sqrt(dx * dx + dy * dy);
                    double averageSide = (first.EquivalentSide + second.EquivalentSide) * 0.5;
                    double majorAxisDistance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    double minorAxisDistance = Math.Min(Math.Abs(dx), Math.Abs(dy));
                    double axisDeviation = minorAxisDistance / Math.Max(1.0, majorAxisDistance);
                    double separationInFinderSides = separation / Math.Max(1.0, averageSide);
                    // 同一二维码的两个定位框位于顶部横边或左侧竖边，允许线扫和轻微旋转，
                    // 但不接受任意斜向的两个方框。中心间距也必须符合常见二维码版本范围。
                    if (separationInFinderSides < 1.60 ||
                        separationInFinderSides > 8.50 ||
                        axisDeviation > 0.45)
                        continue;

                    // 优先选择尺寸一致且相互分离的定位框；第三个定位框稍后再按同一尺寸族加入。
                    double score =
                        Math.Abs(Math.Log(largerSide / smallerSide)) +
                        axisDeviation * 0.50 +
                        averageSide / Math.Max(1.0, separation) * 0.05;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestFirst = i;
                        bestSecond = j;
                    }
                }
            }

            if (bestFirst < 0 || bestSecond < 0)
                return false;

            var coherent = new List<FinderPatternEvidence>
            {
                evidence[bestFirst],
                evidence[bestSecond]
            };
            double pairMedianSide =
                (evidence[bestFirst].EquivalentSide + evidence[bestSecond].EquivalentSide) * 0.5;
            var remaining = new List<FinderPatternEvidence>();
            for (int i = 0; i < evidence.Count; i++)
            {
                if (i != bestFirst && i != bestSecond)
                    remaining.Add(evidence[i]);
            }
            // 第三个定位框不能只因尺寸相近就加入；它还应与已选边共享一个直角顶点，
            // 且两条边长度接近。否则文字中的三个方框会被误当成完整二维码几何。
            FinderPatternEvidence thirdFinder = null;
            double thirdFinderScore = double.MaxValue;
            for (int i = 0; i < remaining.Count; i++)
            {
                double ratio = remaining[i].EquivalentSide / pairMedianSide;
                if (ratio < 0.67 || ratio > 1.50)
                    continue;

                for (int cornerIndex = 0; cornerIndex < 2; cornerIndex++)
                {
                    FinderPatternEvidence corner = coherent[cornerIndex];
                    FinderPatternEvidence pairNeighbor = coherent[1 - cornerIndex];
                    double pairDx = pairNeighbor.CenterX - corner.CenterX;
                    double pairDy = pairNeighbor.CenterY - corner.CenterY;
                    double thirdDx = remaining[i].CenterX - corner.CenterX;
                    double thirdDy = remaining[i].CenterY - corner.CenterY;
                    double pairLength = Math.Sqrt(pairDx * pairDx + pairDy * pairDy);
                    double thirdLength = Math.Sqrt(thirdDx * thirdDx + thirdDy * thirdDy);
                    if (pairLength <= 0 || thirdLength <= pairMedianSide * 1.60)
                        continue;

                    double cosine = Math.Abs(
                        (pairDx * thirdDx + pairDy * thirdDy) /
                        (pairLength * thirdLength));
                    double legRatio =
                        Math.Max(pairLength, thirdLength) /
                        Math.Min(pairLength, thirdLength);
                    if (cosine > 0.35 || legRatio > 1.50)
                        continue;

                    double geometryScore =
                        cosine +
                        Math.Abs(Math.Log(legRatio)) +
                        Math.Abs(Math.Log(ratio));
                    if (geometryScore < thirdFinderScore)
                    {
                        thirdFinderScore = geometryScore;
                        thirdFinder = remaining[i];
                    }
                }
            }
            if (thirdFinder != null)
                coherent.Add(thirdFinder);

            double medianWidth = Median(coherent, item => item.Bounds.Width);
            double medianHeight = Median(coherent, item => item.Bounds.Height);
            if (medianWidth <= 0 || medianHeight <= 0)
                return false;

            geometricRelativeScaleY = Clamp(
                medianWidth / medianHeight,
                AdaptiveMinRelativeScaleY,
                AdaptiveMaxRelativeScaleY);
            double minCenterX = double.MaxValue;
            double maxCenterX = double.MinValue;
            double minCenterY = double.MaxValue;
            double maxCenterY = double.MinValue;
            for (int i = 0; i < coherent.Count; i++)
            {
                minCenterX = Math.Min(minCenterX, coherent[i].CenterX);
                maxCenterX = Math.Max(maxCenterX, coherent[i].CenterX);
                minCenterY = Math.Min(minCenterY, coherent[i].CenterY);
                maxCenterY = Math.Max(maxCenterY, coherent[i].CenterY);
            }

            double widthEstimate = maxCenterX - minCenterX + medianWidth;
            double heightEstimate =
                (maxCenterY - minCenterY + medianHeight) * geometricRelativeScaleY;
            estimatedQrSide = Math.Max(widthEstimate, heightEstimate);
            double finderSide = Math.Sqrt(medianWidth * medianHeight);
            if (estimatedQrSide < finderSide * 2.20)
                return false;

            coherentFinderCount = coherent.Count;
            for (int i = 0; i < AdaptiveTargetQrSides.Length; i++)
            {
                AddAdaptiveCandidate(
                    candidates,
                    AdaptiveTargetQrSides[i],
                    estimatedQrSide,
                    geometricRelativeScaleY);
            }

            // 只有三个定位框共同通过直角几何校验时，才扩展线扫形变邻域。
            // 两点证据仍保留三个标准工作尺寸，足以覆盖已验证的低对比度样本；不再因为
            // 普通图案中偶然出现两个嵌套方框而连续执行八次整幅 DNN 解码。
            if (coherent.Count >= 3)
            {
                double centerTarget = AdaptiveTargetQrSides[0];
                AddAdaptiveCandidate(candidates, centerTarget, estimatedQrSide, 1.0);
                AddAdaptiveCandidate(
                    candidates,
                    centerTarget,
                    estimatedQrSide,
                    geometricRelativeScaleY * 0.80);
                AddAdaptiveCandidate(
                    candidates,
                    centerTarget,
                    estimatedQrSide,
                    geometricRelativeScaleY * 1.25);
                AddAdaptiveCandidate(candidates, AdaptiveTargetQrSides[1], estimatedQrSide, 1.0);
                AddAdaptiveCandidate(candidates, AdaptiveTargetQrSides[2], estimatedQrSide, 1.0);
            }

            return candidates.Count > 0;
        }

        /// <summary>
        /// 为已通过双重几何确认的局部候选生成少量工作尺度。完整二维码优先尝试
        /// 轻微纵向补偿；边缘截断二维码保留更大的纵向补偿范围。三个目标边长用于
        /// 覆盖模块清晰度差异，总候选严格限流以控制无二维码帧耗时。
        /// </summary>
        private static List<AdaptiveScaleCandidate> BuildLocalAdaptiveScaleCandidates(
            double estimatedCodeSide,
            double geometricRelativeScaleY,
            bool hasBoundaryPadding)
        {
            var candidates = new List<AdaptiveScaleCandidate>();
            double[] relativeScaleCandidates = hasBoundaryPadding
                ? new[]
                {
                    geometricRelativeScaleY * 1.30,
                    geometricRelativeScaleY
                }
                : new[]
                {
                    geometricRelativeScaleY * 1.15,
                    geometricRelativeScaleY
                };

            for (int relativeIndex = 0;
                relativeIndex < relativeScaleCandidates.Length;
                relativeIndex++)
            {
                for (int targetIndex = 0;
                    targetIndex < LocalCandidateTargetQrSides.Length;
                    targetIndex++)
                {
                    AddAdaptiveCandidate(
                        candidates,
                        LocalCandidateTargetQrSides[targetIndex],
                        estimatedCodeSide,
                        relativeScaleCandidates[relativeIndex]);
                    if (candidates.Count >= LocalAdaptiveMaxCandidates)
                        return candidates;
                }
            }

            // 传感器边界已经截掉部分数据模块时，小尺寸归一化可能继续丢失纠错信息。
            // 额外保留一个较高横向采样、较强纵向压缩的动态候选；仅对确实发生补白的
            // 局部码区启用，不增加普通二维码和无二维码帧的尝试次数。
            if (hasBoundaryPadding)
            {
                AddAdaptiveCandidate(
                    candidates,
                    BoundaryRecoveryTargetQrSide,
                    estimatedCodeSide,
                    geometricRelativeScaleY * 0.70);
            }

            return candidates;
        }

        private static void AddAdaptiveCandidate(
            List<AdaptiveScaleCandidate> candidates,
            double targetQrSide,
            double estimatedQrSide,
            double relativeScaleY)
        {
            if (candidates.Count >= AdaptiveMaxCandidates || estimatedQrSide <= 0)
                return;

            double scaleX = targetQrSide / estimatedQrSide;
            double safeRelativeScaleY = Clamp(
                relativeScaleY,
                AdaptiveMinRelativeScaleY,
                AdaptiveMaxRelativeScaleY);
            double scaleY = scaleX * safeRelativeScaleY;
            if (scaleX < AdaptiveMinScale || scaleX > AdaptiveMaxScale ||
                scaleY < AdaptiveMinScale || scaleY > AdaptiveMaxScale)
                return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (Math.Abs(candidates[i].ScaleX - scaleX) < 0.01 &&
                    Math.Abs(candidates[i].ScaleY - scaleY) < 0.01)
                    return;
            }

            candidates.Add(new AdaptiveScaleCandidate
            {
                TargetQrSide = targetQrSide,
                ScaleX = scaleX,
                ScaleY = scaleY
            });
        }

        private static double Median(
            List<FinderPatternEvidence> evidence,
            Func<FinderPatternEvidence, double> selector)
        {
            var values = new List<double>(evidence.Count);
            for (int i = 0; i < evidence.Count; i++)
                values.Add(selector(evidence[i]));
            values.Sort();

            int middle = values.Count / 2;
            return values.Count % 2 == 0
                ? (values[middle - 1] + values[middle]) * 0.5
                : values[middle];
        }

        private static double CalculateIntersectionOverUnion(Rect first, Rect second)
        {
            int left = Math.Max(first.X, second.X);
            int top = Math.Max(first.Y, second.Y);
            int right = Math.Min(first.Right, second.Right);
            int bottom = Math.Min(first.Bottom, second.Bottom);
            int intersectionWidth = Math.Max(0, right - left);
            int intersectionHeight = Math.Max(0, bottom - top);
            double intersection = (double)intersectionWidth * intersectionHeight;
            double union =
                (double)first.Width * first.Height +
                (double)second.Width * second.Height -
                intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        /// <summary>
        /// 按指定 X/Y 比例先做一次 Area 重采样，再调用同一个 WeChatQRCode 后端。
        /// 返回前把中心和宽高恢复到 source 坐标系，调用方无需感知中间图像尺寸。
        /// </summary>
    }
}
