using System;
using System.Collections.Generic;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 定位框透视恢复，根据三个定位框估计二维码四边形并生成透视归一化候选。
    /// </summary>
    public sealed partial class QrCodeDetector
    {
        private bool TryDecodeFinderPerspective(
            Mat source,
            Rect evidenceRoi,
            int sourceOffsetX,
            out QrDetectionResult result,
            out string strategy)
        {
            result = QrDetectionResult.NotFound;
            strategy = null;
            if (source == null || source.Empty())
                return false;

            int left = Math.Max(0, evidenceRoi.X);
            int top = Math.Max(0, evidenceRoi.Y);
            int right = Math.Min(source.Width, evidenceRoi.Right);
            int bottom = Math.Min(source.Height, evidenceRoi.Bottom);
            if (right - left < 64 || bottom - top < 64)
                return false;

            var safeEvidenceRoi = new Rect(
                left,
                top,
                right - left,
                bottom - top);
            List<FinderPatternEvidence> evidence;
            using (var evidenceView = new Mat(source, safeEvidenceRoi))
            using (var background = new Mat())
            using (var flattened = new Mat())
            {
                // 大尺度背景估计抑制白带过曝、渐变和 CIS 条纹；不对数据模块做二值化。
                // 背景尺度不能跟整张截图尺寸同步放大：同一个二维码放进更大的画布后，
                // 过大的 sigma 会把定位框外边缘一并纳入背景并造成数像素中心漂移。
                // 18 px 只用于定位框取证；真正解码仍回到原始灰度图。
                const double sigma = 18.0;
                Cv2.GaussianBlur(
                    evidenceView,
                    background,
                    new OpenCvSharp.Size(0, 0),
                    sigma);
                Cv2.Divide(evidenceView, background, flattened, 255.0);
                evidence = FindFinderPatternEvidence(flattened);
            }

            List<FinderPerspectiveCandidate> candidates =
                BuildFinderPerspectiveCandidates(
                    evidence,
                    safeEvidenceRoi.X,
                    safeEvidenceRoi.Y,
                    source.Width,
                    source.Height);
            for (int candidateIndex = 0;
                candidateIndex < candidates.Count;
                candidateIndex++)
            {
                FinderPerspectiveCandidate candidate = candidates[candidateIndex];
                int codeSide =
                    candidate.ModuleCount * FinderPerspectivePixelsPerModule;
                int quietZone =
                    4 * FinderPerspectivePixelsPerModule;
                Point2f[] destination =
                {
                    new Point2f(0, 0),
                    new Point2f(codeSide - 1, 0),
                    new Point2f(codeSide - 1, codeSide - 1),
                    new Point2f(0, codeSide - 1)
                };

                using (Mat transform = Cv2.GetPerspectiveTransform(
                    candidate.Corners,
                    destination))
                using (var straight = new Mat())
                using (var padded = new Mat())
                using (var normalized = new Mat())
                {
                    Cv2.WarpPerspective(
                        source,
                        straight,
                        transform,
                        new OpenCvSharp.Size(codeSide, codeSide),
                        InterpolationFlags.Cubic,
                        BorderTypes.Constant,
                        Scalar.All(255));
                    Cv2.CopyMakeBorder(
                        straight,
                        padded,
                        quietZone,
                        quietZone,
                        quietZone,
                        quietZone,
                        BorderTypes.Constant,
                        Scalar.All(255));
                    Cv2.Normalize(
                        padded,
                        normalized,
                        0,
                        255,
                        NormTypes.MinMax);

                    string rectifiedPreprocessing = "gray";
                    bool decoded = TryDecode(
                        normalized,
                        1.0,
                        out DecodeHit decodedHit);
                    if (!decoded)
                    {
                        // 大截图中的白带过曝在透视拉正后仍可能形成缓慢亮度梯度。
                        // 仅在原灰度校验失败时，对这个约 350 px 的小图做一次局部光照展平；
                        // 它不会增加无定位框帧的开销，也不会修改原始数据模块。
                        using (var localBackground = new Mat())
                        using (var localFlattened = new Mat())
                        {
                            Cv2.GaussianBlur(
                                normalized,
                                localBackground,
                                new OpenCvSharp.Size(0, 0),
                                FinderPerspectivePixelsPerModule * 1.8);
                            Cv2.Divide(
                                normalized,
                                localBackground,
                                localFlattened,
                                255.0);
                            decoded = TryDecode(
                                localFlattened,
                                1.0,
                                out decodedHit);
                            if (decoded)
                                rectifiedPreprocessing = "illumination-flattened";
                        }
                    }

                    if (!decoded)
                        continue;

                    Point2f[] corners = candidate.Corners;
                    double centerX =
                        (corners[0].X + corners[1].X +
                         corners[2].X + corners[3].X) * 0.25;
                    double centerY =
                        (corners[0].Y + corners[1].Y +
                         corners[2].Y + corners[3].Y) * 0.25;
                    double pixelWidth =
                        (PointDistance(corners[0], corners[1]) +
                         PointDistance(corners[3], corners[2])) * 0.5;
                    double pixelHeight =
                        (PointDistance(corners[0], corners[3]) +
                         PointDistance(corners[1], corners[2])) * 0.5;

                    result = new QrDetectionResult
                    {
                        Found = true,
                        CenterX = Math.Max(
                            0,
                            (int)Math.Round(centerX + sourceOffsetX)),
                        CenterY = Math.Max(0, (int)Math.Round(centerY)),
                        PixelWidth = pixelWidth,
                        PixelHeight = pixelHeight,
                        DecodedText = decodedHit.Text
                    };
                    strategy =
                        $"WeChatQRCode, finder-perspective, finderCount=3, " +
                        $"moduleCount={candidate.ModuleCount}, " +
                        $"rightAngleCosine={candidate.RightAngleCosine:F3}, " +
                        $"rectified={rectifiedPreprocessing}";
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 在有限数量的定位框中枚举三元组，筛选尺寸一致、两条边接近垂直且长度相近的组合，
        /// 再由定位框中心距估算 21、25、29… 等合法二维码模块数。
        /// </summary>
        private static List<FinderPerspectiveCandidate>
            BuildFinderPerspectiveCandidates(
                List<FinderPatternEvidence> evidence,
                int evidenceOffsetX,
                int evidenceOffsetY,
                int sourceWidth,
                int sourceHeight)
        {
            var candidates = new List<FinderPerspectiveCandidate>();
            if (evidence == null || evidence.Count < 3)
                return candidates;

            int count = Math.Min(
                evidence.Count,
                FinderPerspectiveMaxEvidence);
            for (int firstIndex = 0; firstIndex < count - 2; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1;
                    secondIndex < count - 1;
                    secondIndex++)
                {
                    for (int thirdIndex = secondIndex + 1;
                        thirdIndex < count;
                        thirdIndex++)
                    {
                        FinderPatternEvidence[] triple =
                        {
                            evidence[firstIndex],
                            evidence[secondIndex],
                            evidence[thirdIndex]
                        };
                        double minimumSide = Math.Min(
                            triple[0].EquivalentSide,
                            Math.Min(
                                triple[1].EquivalentSide,
                                triple[2].EquivalentSide));
                        double maximumSide = Math.Max(
                            triple[0].EquivalentSide,
                            Math.Max(
                                triple[1].EquivalentSide,
                                triple[2].EquivalentSide));
                        if (minimumSide <= 0 ||
                            maximumSide / minimumSide > 1.50)
                            continue;

                        int cornerIndex = -1;
                        double bestCosine = double.MaxValue;
                        double firstLeg = 0;
                        double secondLeg = 0;
                        for (int index = 0; index < 3; index++)
                        {
                            Point2f cornerCenter = FinderCenter(
                                triple[index],
                                evidenceOffsetX,
                                evidenceOffsetY);
                            Point2f firstCenter = FinderCenter(
                                triple[(index + 1) % 3],
                                evidenceOffsetX,
                                evidenceOffsetY);
                            Point2f secondCenter = FinderCenter(
                                triple[(index + 2) % 3],
                                evidenceOffsetX,
                                evidenceOffsetY);
                            Point2f firstVector = firstCenter - cornerCenter;
                            Point2f secondVector = secondCenter - cornerCenter;
                            double firstLength = Math.Sqrt(
                                firstVector.X * firstVector.X +
                                firstVector.Y * firstVector.Y);
                            double secondLength = Math.Sqrt(
                                secondVector.X * secondVector.X +
                                secondVector.Y * secondVector.Y);
                            if (firstLength <= 1 || secondLength <= 1)
                                continue;

                            double cosine = Math.Abs(
                                (firstVector.X * secondVector.X +
                                 firstVector.Y * secondVector.Y) /
                                (firstLength * secondLength));
                            if (cosine >= bestCosine)
                                continue;

                            bestCosine = cosine;
                            cornerIndex = index;
                            firstLeg = firstLength;
                            secondLeg = secondLength;
                        }

                        if (cornerIndex < 0 ||
                            bestCosine > 0.25 ||
                            Math.Max(firstLeg, secondLeg) /
                            Math.Min(firstLeg, secondLeg) > 1.35)
                            continue;

                        FinderPatternEvidence corner = triple[cornerIndex];
                        FinderPatternEvidence firstNeighbor =
                            triple[(cornerIndex + 1) % 3];
                        FinderPatternEvidence secondNeighbor =
                            triple[(cornerIndex + 2) % 3];
                        FinderPatternEvidence horizontal =
                            Math.Abs(firstNeighbor.CenterX - corner.CenterX) >=
                            Math.Abs(secondNeighbor.CenterX - corner.CenterX)
                                ? firstNeighbor
                                : secondNeighbor;
                        FinderPatternEvidence vertical =
                            ReferenceEquals(horizontal, firstNeighbor)
                                ? secondNeighbor
                                : firstNeighbor;

                        double[] sides =
                        {
                            triple[0].EquivalentSide,
                            triple[1].EquivalentSide,
                            triple[2].EquivalentSide
                        };
                        Array.Sort(sides);
                        double medianFinderSide = sides[1];
                        double moduleSize = medianFinderSide / 7.0;
                        double dimensionEstimate =
                            (firstLeg + secondLeg) * 0.5 /
                            moduleSize + 7.0;
                        int moduleCount = 21 + 4 * Math.Max(
                            0,
                            (int)Math.Round(
                                (dimensionEstimate - 21.0) / 4.0));
                        if (moduleCount < 21 ||
                            moduleCount > 177 ||
                            Math.Abs(moduleCount - dimensionEstimate) > 2.25)
                            continue;

                        int finderCenterSpan = moduleCount - 7;
                        Point2f cornerCenterWithOffset = FinderCenter(
                            corner,
                            evidenceOffsetX,
                            evidenceOffsetY);
                        Point2f horizontalCenter = FinderCenter(
                            horizontal,
                            evidenceOffsetX,
                            evidenceOffsetY);
                        Point2f verticalCenter = FinderCenter(
                            vertical,
                            evidenceOffsetX,
                            evidenceOffsetY);
                        Point2f xAxis = new Point2f(
                            (cornerCenterWithOffset.X - horizontalCenter.X) /
                            finderCenterSpan,
                            (cornerCenterWithOffset.Y - horizontalCenter.Y) /
                            finderCenterSpan);
                        Point2f yAxis = new Point2f(
                            (cornerCenterWithOffset.X - verticalCenter.X) /
                            finderCenterSpan,
                            (cornerCenterWithOffset.Y - verticalCenter.Y) /
                            finderCenterSpan);
                        double far = moduleCount - 3.5;
                        const double near = 3.5;
                        Point2f[] corners = OrderQuadrilateralCorners(
                            new[]
                            {
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, -far),
                                    ScaleVector(yAxis, -far)),
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, near),
                                    ScaleVector(yAxis, -far)),
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, near),
                                    ScaleVector(yAxis, near)),
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, -far),
                                    ScaleVector(yAxis, near))
                            });

                        double allowedOutside =
                            Math.Max(sourceWidth, sourceHeight) * 0.15;
                        bool outside = false;
                        for (int cornerNumber = 0;
                            cornerNumber < corners.Length;
                            cornerNumber++)
                        {
                            if (corners[cornerNumber].X < -allowedOutside ||
                                corners[cornerNumber].Y < -allowedOutside ||
                                corners[cornerNumber].X >
                                    sourceWidth + allowedOutside ||
                                corners[cornerNumber].Y >
                                    sourceHeight + allowedOutside)
                            {
                                outside = true;
                                break;
                            }
                        }
                        if (outside)
                            continue;

                        double score =
                            bestCosine +
                            Math.Abs(Math.Log(firstLeg / secondLeg)) +
                            Math.Abs(Math.Log(maximumSide / minimumSide)) +
                            Math.Abs(moduleCount - dimensionEstimate) * 0.05;
                        candidates.Add(new FinderPerspectiveCandidate
                        {
                            Corners = corners,
                            ModuleCount = moduleCount,
                            RightAngleCosine = bestCosine,
                            Score = score
                        });
                    }
                }
            }

            candidates.Sort((leftCandidate, rightCandidate) =>
                leftCandidate.Score.CompareTo(rightCandidate.Score));
            if (candidates.Count > FinderPerspectiveMaxCandidates)
                candidates.RemoveRange(
                    FinderPerspectiveMaxCandidates,
                    candidates.Count - FinderPerspectiveMaxCandidates);
            return candidates;
        }

        private static Point2f FinderCenter(
            FinderPatternEvidence evidence,
            int offsetX,
            int offsetY)
        {
            return new Point2f(
                (float)(evidence.CenterX + offsetX),
                (float)(evidence.CenterY + offsetY));
        }

        private static Point2f ScaleVector(Point2f vector, double scale)
        {
            return new Point2f(
                (float)(vector.X * scale),
                (float)(vector.Y * scale));
        }

        private static Point2f AddVectors(
            Point2f origin,
            Point2f first,
            Point2f second)
        {
            return new Point2f(
                origin.X + first.X + second.X,
                origin.Y + first.Y + second.Y);
        }

        private static double PointDistance(Point2f first, Point2f second)
        {
            double deltaX = first.X - second.X;
            double deltaY = first.Y - second.Y;
            return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        private static Point2f[] OrderQuadrilateralCorners(Point2f[] corners)
        {
            Point2f topLeft = corners[0];
            Point2f topRight = corners[0];
            Point2f bottomRight = corners[0];
            Point2f bottomLeft = corners[0];
            double minimumSum = double.MaxValue;
            double maximumSum = double.MinValue;
            double minimumDifference = double.MaxValue;
            double maximumDifference = double.MinValue;
            for (int index = 0; index < corners.Length; index++)
            {
                Point2f point = corners[index];
                double sum = point.X + point.Y;
                double difference = point.X - point.Y;
                if (sum < minimumSum)
                {
                    minimumSum = sum;
                    topLeft = point;
                }
                if (sum > maximumSum)
                {
                    maximumSum = sum;
                    bottomRight = point;
                }
                if (difference > maximumDifference)
                {
                    maximumDifference = difference;
                    topRight = point;
                }
                if (difference < minimumDifference)
                {
                    minimumDifference = difference;
                    bottomLeft = point;
                }
            }
            return new[] { topLeft, topRight, bottomRight, bottomLeft };
        }
    }
}
