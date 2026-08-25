using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.XImgProc;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 细线断裂检测通道。包含细线前景证据、骨架长度、端点锚定及抗错位门控。
    /// </summary>
    public static partial class PatchDefectDetector
    {
        private static Mat ToGray(Mat src)
        {
            if (src.Channels() == 1) return src;
            if (src.Channels() == 4)
            {
                Mat gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
                return gray;
            }
            {
                Mat gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                return gray;
            }
        }

        /// <summary>
        /// 生成原有轮廓屏蔽掩膜。掩膜与差分分开创建，使细线通道能够在清零前读取原始内部差分。
        /// </summary>
        private static Mat BuildEdgeExclusionMask(Mat alphaBinary, int edgeThick, int edgeSmall)
        {
            Mat edgeMask = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1);
            try
            {
                if (edgeThick <= 0 && edgeSmall <= 0)
                    return edgeMask;

                // 外轮廓使用填充后的整体轮廓控制较宽屏蔽，避免内部镂空边被误当成外边界。
                // 不再把物理宽度直接传给 DrawContours(thickness)：OpenCV 的偶数线宽会多覆盖
                // 一个栅格像素，在缩小检测后再放大查看时误差会被同步放大。
                using (Mat externalInput = alphaBinary.Clone())
                using (Mat alphaFilled = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1))
                {
                    Cv2.FindContours(externalInput, out Point[][] contoursExt, out _,
                        RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    Cv2.DrawContours(alphaFilled, contoursExt, -1, new Scalar(255), -1);

                    if (edgeThick > 0)
                    {
                        using (Mat outerBoundaryBand = BuildExactBoundaryBand(alphaFilled, edgeThick))
                        {
                            Cv2.BitwiseOr(edgeMask, outerBoundaryBand, edgeMask);
                        }
                    }
                }

                // Alpha 本身包含外边界、孔洞和细小结构；形态学边界带会同时保留这些轮廓，
                // 与原 Tree 轮廓的屏蔽目标一致，并严格使用换算后的离散总宽度。
                if (edgeSmall > 0)
                {
                    using (Mat smallBoundaryBand = BuildExactBoundaryBand(alphaBinary, edgeSmall))
                    {
                        Cv2.BitwiseOr(edgeMask, smallBoundaryBand, edgeMask);
                    }
                }

                return edgeMask;
            }
            catch
            {
                edgeMask.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 生成严格离散总宽度的二值边界带。宽度被拆分到轮廓外侧和内侧；奇数宽度时
        /// 多出的一个像素放在结构内侧，以保证 thickness=1 仍覆盖真实轮廓像素。
        /// </summary>
        private static Mat BuildExactBoundaryBand(Mat binaryMask, int totalThickness)
        {
            Mat boundaryBand = Mat.Zeros(binaryMask.Size(), MatType.CV_8UC1);
            if (totalThickness <= 0)
                return boundaryBand;

            int outsideRadius = totalThickness / 2;
            int insideRadius = totalThickness - outsideRadius;
            try
            {
                using (var dilated = new Mat())
                using (var eroded = new Mat())
                {
                    if (outsideRadius > 0)
                    {
                        int kernelSize = outsideRadius * 2 + 1;
                        using (Mat kernel = Cv2.GetStructuringElement(
                                   MorphShapes.Ellipse,
                                   new Size(kernelSize, kernelSize)))
                        {
                            Cv2.Dilate(binaryMask, dilated, kernel);
                        }
                    }
                    else
                    {
                        binaryMask.CopyTo(dilated);
                    }

                    if (insideRadius > 0)
                    {
                        int kernelSize = insideRadius * 2 + 1;
                        using (Mat kernel = Cv2.GetStructuringElement(
                                   MorphShapes.Ellipse,
                                   new Size(kernelSize, kernelSize)))
                        {
                            Cv2.Erode(binaryMask, eroded, kernel);
                        }
                    }
                    else
                    {
                        binaryMask.CopyTo(eroded);
                    }

                    // dilated 包含轮廓外侧，eroded 去掉轮廓内侧；二者之差即总宽度可控的边界带。
                    Cv2.Subtract(dilated, eroded, boundaryBand);
                }

                return boundaryBand;
            }
            catch
            {
                boundaryBand.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 在独立细节尺度上检测细线中间断口，所有断口统一执行同一条证据链：
        /// 1. 建立模板/CIS 前景；2. 提取轮廓屏蔽区内的缺失候选；
        /// 3. 在候选 ROI 上提取骨架；4. 校验物理长度和线宽；
        /// 5. 确认笔画横截面被切断；6. 确认缺口前后仍有结构；
        /// 7. 排除可由同一微小位移解释的配准残差；8. 合并同一物理断口。
        /// 返回的矩形和掩膜均位于 analysisScale 对应的检测坐标系；最长断口长度和
        /// 最大模板笔画宽度均使用毫米，并与用户配置的物理参数保持同一尺度。
        /// </summary>
        private static List<Rect> DetectFineLineBreaksAtDetailScale(
            Mat templateGray,
            Mat capturedGray,
            int capturedBaseThreshold,
            double analysisScale,
            AppConfig config,
            Mat acceptedGapMask,
            out int acceptedGapCount,
            out double longestAcceptedGapMm,
            out double widestAcceptedGapMm,
            out List<Rect> acceptedTightGapRects,
            out List<int> acceptedGapAreasPixels)
        {
            // 以下三个比例属于内部证据门槛，不作为用户参数暴露：
            // 端点覆盖用于证明断口前后仍有线；横截面缺失用于证明不是局部暗斑；
            // 绝对缺失用于低对比光晕情况下的保守恢复。
            const double minimumEndpointCoverage = 0.40;
            const double minimumCrossSectionMissingRatio = 0.75;
            const double minimumAbsoluteMissingRatio = 0.90;

            acceptedGapCount = 0;
            longestAcceptedGapMm = 0;
            widestAcceptedGapMm = 0;
            var acceptedGapRects = new List<Rect>();
            // 可视化框会为便于观察向外扩展；日志尺寸必须使用断口原始紧致框，二者分开维护。
            acceptedTightGapRects = new List<Rect>();
            acceptedGapAreasPixels = new List<int>();
            var acceptedExpandedGapRects = new List<Rect>();

            double pixelsPerMm = config.LayoutDpi > 0
                ? config.LayoutDpi / 25.4 * analysisScale
                : 0;
            if (pixelsPerMm <= 0 || config.FineLineMinBreakLengthMm <= 0 ||
                config.FineLineMaxWidthMm <= 0 || templateGray.Empty() || capturedGray.Empty())
            {
                return acceptedGapRects;
            }

            const double minimumSupportedGapMm = 0.5;
            double minimumGapMm = Math.Max(
                minimumSupportedGapMm,
                config.FineLineMinBreakLengthMm);
            int minimumGapPixels = Math.Max(
                2,
                (int)Math.Ceiling(minimumGapMm * pixelsPerMm));
            double maximumLineHalfWidthPixels =
                Math.Max(1.0, config.FineLineMaxWidthMm * pixelsPerMm * 0.5 + 1.25);
            int localContrastWindowPixels = Math.Max(
                3,
                (int)Math.Ceiling(FineLineLocalContrastWindowMm * pixelsPerMm));
            if ((localContrastWindowPixels & 1) == 0)
                localContrastWindowPixels++;
            double minimumAbsoluteRecoveryHalfWidthPixels =
                Math.Max(1.5, 0.30 * pixelsPerMm);

            // 局部 SIFT 已完成零件级精对准，但弯曲细线仍可能存在少量法向漂移。
            // 这里按细线自身的毫米语义计算搜索范围：法向允许吸收少量错位，沿线方向严格
            // 限制，避免把断口后方的正常线段平移过来。DefectToleranceInner 只服务于
            // 普通内部面积缺陷，不再改变细线候选、端点验证或错位排除结果。
            int maximumTangentialShift = Math.Max(
                1,
                (int)Math.Round(FineLineTangentialAlignmentToleranceMm * pixelsPerMm));
            int normalAlignmentSearchRadius = Math.Max(
                maximumTangentialShift,
                (int)Math.Round(FineLineNormalAlignmentToleranceMm * pixelsPerMm));
            int endpointSearchRadius = Math.Max(
                normalAlignmentSearchRadius + 1,
                Math.Max(
                    2,
                    (int)Math.Round(FineLineEndpointSearchRadiusMm * pixelsPerMm)));
            // 端点只用于确认断口两侧仍有真实线段，可使用比缺口本体更宽松的搜索半径。
            int endpointAnchorLength = Math.Max(
                minimumGapPixels * 2,
                (int)Math.Round(FineLineEndpointAnchorLengthMm * pixelsPerMm));
            // 正常前景由绝对亮度与局部对比度共同建立，避免把偏灰但连续的线条切断。
            int relaxedForegroundThreshold = Math.Max(
                8,
                Math.Min(capturedBaseThreshold - 1, (int)Math.Round(capturedBaseThreshold * 0.82)));
            // 绝对灰度恢复分支只处理“局部对比度把真实断口补亮”的情况。
            // 真断口的原始灰度应接近周围背景；若仍明显更亮，说明该处仍有墨迹，
            // 更可能是连续细线的局部变暗或轻微配准偏移。
            int recoveryBackgroundBrightnessTolerance = Math.Max(
                4,
                CalculateFineLineLocalContrastThreshold(relaxedForegroundThreshold) / 2);
            int recoveryBackgroundRingWidth = Math.Max(
                3,
                (int)Math.Ceiling(0.80 * pixelsPerMm));
            using (var templateBinary = new Mat())
            using (var capturedSized = new Mat())
            using (var capturedForeground = new Mat())
            using (var capturedAbsoluteForeground = new Mat())
            using (var templateEdgeMask = new Mat())
            using (var capturedNearEndpoints = new Mat())
            using (var missingCandidates = new Mat())
            using (var candidateLabels = new Mat())
            using (var candidateStats = new Mat())
            using (var candidateCentroids = new Mat())
            using (Mat endpointSearchKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(endpointSearchRadius * 2 + 1, endpointSearchRadius * 2 + 1)))
            {
                // 阶段 1：建立两类基础结构。
                // templateBinary 是“设计上应该有墨”的模板；capturedForeground 是“实拍中仍可确认有墨”的
                // 宽松前景。后者同时接受绝对亮度和局部对比度，避免整体偏灰时把连续线误切成缺口。
                Cv2.Threshold(
                    templateGray,
                    templateBinary,
                    config.DefectAlphaBinaryThresh,
                    255,
                    ThresholdTypes.Binary);

                if (capturedGray.Size() == templateGray.Size())
                    capturedGray.CopyTo(capturedSized);
                else
                    Cv2.Resize(capturedGray, capturedSized, templateGray.Size(), 0, 0, InterpolationFlags.Linear);

                BuildFineLineForegroundEvidence(
                    capturedSized,
                    capturedForeground,
                    relaxedForegroundThreshold,
                    localContrastWindowPixels);
                Cv2.Threshold(
                    capturedSized,
                    capturedAbsoluteForeground,
                    relaxedForegroundThreshold,
                    255,
                    ThresholdTypes.Binary);

                using (Mat builtEdgeMask = BuildEdgeExclusionMask(
                    templateBinary,
                    ConvertLengthMmToScaledPixels(
                        config.DefectEdgeExclusionThick,
                        config.LayoutDpi,
                        analysisScale),
                    ConvertLengthMmToScaledPixels(
                        config.DefectEdgeExclusionSmall,
                        config.LayoutDpi,
                        analysisScale)))
                {
                    builtEdgeMask.CopyTo(templateEdgeMask);
                }

                Cv2.Dilate(capturedForeground, capturedNearEndpoints, endpointSearchKernel);
                using (var inverseCapturedForeground = new Mat())
                using (var rawMissingForeground = new Mat())
                {
                    // 阶段 2：生成唯一候选源。
                    // “缺口”定义为：模板要求有前景、CIS 宽松前景不存在，并且该位置属于
                    // 普通差分会屏蔽的轮廓区域。后续无论断口长短，都只走同一条验证路径。
                    Cv2.BitwiseNot(capturedForeground, inverseCapturedForeground);
                    Cv2.BitwiseAnd(templateBinary, inverseCapturedForeground, rawMissingForeground);
                    Cv2.BitwiseAnd(rawMissingForeground, templateEdgeMask, missingCandidates);
                }
                if (Cv2.CountNonZero(missingCandidates) == 0)
                    return acceptedGapRects;

                int candidateCount = Cv2.ConnectedComponentsWithStats(
                    missingCandidates, candidateLabels, candidateStats, candidateCentroids);
                for (int label = 1; label < candidateCount; label++)
                {
                    // 阶段 3A：一个缺失区域内可能包含一段或多段模板中心线。
                    // 先建立包含端点搜索范围的局部 ROI，再只对该 ROI 做骨架化。
                    Rect candidateBounds = new Rect(
                        candidateStats.At<int>(label, 0),
                        candidateStats.At<int>(label, 1),
                        candidateStats.At<int>(label, 2),
                        candidateStats.At<int>(label, 3));

                    // 外接框对角线只是低成本预筛，不作为最终长度。明显不足最小长度的
                    // 单像素噪声无需进入骨架化；真正长度仍在后面按骨架路径重新计算。
                    double candidateSpan = Math.Sqrt(
                        candidateBounds.Width * candidateBounds.Width +
                        candidateBounds.Height * candidateBounds.Height);
                    if (candidateSpan < minimumGapPixels)
                        continue;

                    // ROI 边界外没有足够空间确认“缺口前后均有结构”。边界候选宁可不判，
                    // 也不能把零件裁切边界当作断口；如需检测边缘结构，应由 PatchCropper 提供 padding。
                    int borderMargin = endpointSearchRadius + 1;
                    if (candidateBounds.X <= borderMargin || candidateBounds.Y <= borderMargin ||
                        candidateBounds.Right >= templateBinary.Width - borderMargin ||
                        candidateBounds.Bottom >= templateBinary.Height - borderMargin)
                    {
                        continue;
                    }

                    Rect evidenceBounds = ExpandRect(
                        candidateBounds,
                        endpointAnchorLength + endpointSearchRadius,
                        templateBinary.Size());

                    using (Mat candidateLabelsRoi = new Mat(candidateLabels, evidenceBounds))
                    using (Mat templateRoi = new Mat(templateBinary, evidenceBounds))
                    using (Mat capturedGrayRoi = new Mat(capturedSized, evidenceBounds))
                    using (Mat capturedForegroundRoi = new Mat(capturedForeground, evidenceBounds))
                    using (Mat capturedAbsoluteForegroundRoi = new Mat(
                        capturedAbsoluteForeground, evidenceBounds))
                    using (Mat capturedNearEndpointsRoi = new Mat(
                        capturedNearEndpoints, evidenceBounds))
                    using (var candidateMask = new Mat())
                    using (var templateSkeletonRoi = new Mat())
                    using (var templateDistanceRoi = new Mat())
                    using (var gapSkeleton = new Mat())
                    using (var gapLabels = new Mat())
                    using (var gapStats = new Mat())
                    using (var gapCentroids = new Mat())
                    {
                        Cv2.InRange(
                            candidateLabelsRoi,
                            new Scalar(label),
                            new Scalar(label),
                            candidateMask);

                        // 阶段 3B：骨架化和距离变换都限定在候选 ROI。
                        // 当前零件图较大、候选数量较少；实测整幅预计算会显著增加耗时，
                        // 因此这里用小 ROI 换取更低的总计算量和更小的临时内存。
                        CvXImgProc.Thinning(
                            templateRoi,
                            templateSkeletonRoi,
                            ThinningTypes.GUOHALL);
                        Cv2.DistanceTransform(
                            templateRoi,
                            templateDistanceRoi,
                            DistanceTypes.L2,
                            DistanceTransformMasks.Mask3);
                        Cv2.BitwiseAnd(templateSkeletonRoi, candidateMask, gapSkeleton);
                        if (Cv2.CountNonZero(gapSkeleton) == 0)
                            continue;

                        int gapCount = Cv2.ConnectedComponentsWithStats(
                            gapSkeleton,
                            gapLabels,
                            gapStats,
                            gapCentroids);
                        for (int gapLabel = 1; gapLabel < gapCount; gapLabel++)
                        {
                            Rect gapBoundsLocal = new Rect(
                                gapStats.At<int>(gapLabel, 0),
                                gapStats.At<int>(gapLabel, 1),
                                gapStats.At<int>(gapLabel, 2),
                                gapStats.At<int>(gapLabel, 3));

                            using (var gapMask = new Mat())
                            {
                                Cv2.InRange(
                                    gapLabels,
                                    new Scalar(gapLabel),
                                    new Scalar(gapLabel),
                                    gapMask);

                                // 阶段 4：先用物理长度和模板局部线宽排除短噪声与宽实心区域。
                                double gapLengthPixels =
                                    CalculateSkeletonPathLengthPixels(gapMask);
                                double localLineHalfWidth =
                                    CalculateMedianDistance(templateDistanceRoi, gapMask);
                                if (gapLengthPixels < minimumGapPixels ||
                                    localLineHalfWidth > maximumLineHalfWidthPixels)
                                {
                                    continue;
                                }

                                // 阶段 5：真正的断线应横向切断模板笔画的大部分宽度。
                                // 宽松前景若被低对比光晕误导，只允许通过同一套绝对灰度恢复，
                                // 无论断口长短都不再进入其他旁路。
                                double crossSectionMissingRatio =
                                    CalculateCrossSectionMissingRatio(
                                        templateRoi,
                                        capturedForegroundRoi,
                                        gapMask,
                                        localLineHalfWidth);
                                bool useAbsoluteRecovery = false;
                                if (crossSectionMissingRatio < minimumCrossSectionMissingRatio)
                                {
                                    double absoluteMissingRatio =
                                        CalculateCrossSectionMissingRatio(
                                            templateRoi,
                                            capturedAbsoluteForegroundRoi,
                                            gapMask,
                                            localLineHalfWidth);
                                    useAbsoluteRecovery =
                                        localLineHalfWidth >=
                                            minimumAbsoluteRecoveryHalfWidthPixels &&
                                        absoluteMissingRatio >= minimumAbsoluteMissingRatio &&
                                        IsGapBrightnessConsistentWithBackground(
                                            capturedGrayRoi,
                                            templateRoi,
                                            gapMask,
                                            localLineHalfWidth,
                                            recoveryBackgroundRingWidth,
                                            recoveryBackgroundBrightnessTolerance);
                                    if (!useAbsoluteRecovery)
                                        continue;
                                }

                                // 阶段 6：“前后均有结构”排除线端、裁切边界和孤立暗点。
                                // 两个锚点必须位于缺口相反方向，并在 CIS 附近都能找到对应线段。
                                Mat firstAnchor = null;
                                Mat secondAnchor = null;
                                try
                                {
                                    if (!TryBuildEndpointAnchors(
                                        templateSkeletonRoi,
                                        gapMask,
                                        maximumTangentialShift + 1,
                                        endpointAnchorLength,
                                        out firstAnchor,
                                        out secondAnchor))
                                    {
                                        continue;
                                    }

                                    double firstEndpointCoverage =
                                        CalculateCoverage(
                                            firstAnchor,
                                            capturedNearEndpointsRoi);
                                    double secondEndpointCoverage =
                                        CalculateCoverage(
                                            secondAnchor,
                                            capturedNearEndpointsRoi);
                                    if (firstEndpointCoverage < minimumEndpointCoverage ||
                                        secondEndpointCoverage < minimumEndpointCoverage)
                                    {
                                        continue;
                                    }

                                    // 阶段 7：排除局部配准残差。
                                    // 只有同一个小位移能够同时覆盖缺口和两端，才说明线条实际连续；
                                    // 沿线位移限制更严格，防止用断口后的正常线段填补真实短断口。
                                    if (IsGapExplainedByMinorAlignmentOffset(
                                        useAbsoluteRecovery
                                            ? capturedAbsoluteForegroundRoi
                                            : capturedForegroundRoi,
                                        gapMask,
                                        firstAnchor,
                                        secondAnchor,
                                        normalAlignmentSearchRadius,
                                        maximumTangentialShift))
                                    {
                                        continue;
                                    }

                                    // 阶段 8：全部证据通过后，才写入最终掩膜、结果框和物理长度。
                                    using (Mat acceptedRoi =
                                        new Mat(acceptedGapMask, evidenceBounds))
                                    {
                                        Cv2.BitwiseOr(
                                            acceptedRoi,
                                            gapMask,
                                            acceptedRoi);
                                    }

                                    Rect gapBoundsGlobal = new Rect(
                                        evidenceBounds.X + gapBoundsLocal.X,
                                        evidenceBounds.Y + gapBoundsLocal.Y,
                                        gapBoundsLocal.Width,
                                        gapBoundsLocal.Height);
                                    acceptedTightGapRects.Add(gapBoundsGlobal);
                                    acceptedExpandedGapRects.Add(ExpandRect(
                                        gapBoundsGlobal,
                                        normalAlignmentSearchRadius + 1,
                                        templateBinary.Size()));
                                    longestAcceptedGapMm = Math.Max(
                                        longestAcceptedGapMm,
                                        gapLengthPixels / pixelsPerMm);
                                    // DistanceTransform 在骨架位置给出中心线到模板笔画边缘的半宽。
                                    // 乘 2 后按当前细节尺度 px/mm 换算，和上方最大线宽过滤使用
                                    // 完全相同的几何定义，不会因日志统计引入新的阈值或候选分支。
                                    widestAcceptedGapMm = Math.Max(
                                        widestAcceptedGapMm,
                                        localLineHalfWidth * 2.0 / pixelsPerMm);
                                }
                                finally
                                {
                                    secondAnchor?.Dispose();
                                    firstAnchor?.Dispose();
                                }
                            }
                        }
                    }
                }

                // 阶段 9：同一物理断口可能因骨架离散化被切成相邻小段，统一合并后再计数。
                acceptedGapRects = MergeNearbyRects(acceptedExpandedGapRects, endpointSearchRadius);
                acceptedTightGapRects = BuildTightRectsForMergedGroups(
                    acceptedGapRects,
                    acceptedExpandedGapRects,
                    acceptedTightGapRects);
                // acceptedGapMask 是所有最终通过门控断口的像素并集。按合并后的紧致框计数，
                // 可避免同一物理断口被多个骨架候选重复累计面积。
                acceptedGapAreasPixels = CountForegroundPixelsByRect(
                    acceptedGapMask,
                    acceptedTightGapRects);
                acceptedGapCount = acceptedGapRects.Count;

            }

            return acceptedGapRects;
        }

        /// <summary>统计每个最终断口紧致框内的真实缺失前景像素数。</summary>
        private static List<int> CountForegroundPixelsByRect(
            Mat acceptedGapMask,
            IReadOnlyList<Rect> acceptedTightGapRects)
        {
            var areas = new List<int>(acceptedTightGapRects.Count);
            foreach (Rect rect in acceptedTightGapRects)
            {
                using (var roi = new Mat(acceptedGapMask, rect))
                    areas.Add(Cv2.CountNonZero(roi));
            }
            return areas;
        }

        /// <summary>
        /// 细线骨架离散化可能把同一断口拆成多个候选。可视化框合并后，按相同分组把原始
        /// 紧致框取并集，确保日志中的缺陷数量与最终计数一致，同时不把显示用扩展边距算入尺寸。
        /// </summary>
        private static List<Rect> BuildTightRectsForMergedGroups(
            IReadOnlyList<Rect> mergedExpandedRects,
            IReadOnlyList<Rect> sourceExpandedRects,
            IReadOnlyList<Rect> sourceTightRects)
        {
            var result = new List<Rect>(mergedExpandedRects.Count);
            foreach (Rect mergedRect in mergedExpandedRects)
            {
                bool found = false;
                int left = int.MaxValue;
                int top = int.MaxValue;
                int right = int.MinValue;
                int bottom = int.MinValue;

                int pairCount = Math.Min(sourceExpandedRects.Count, sourceTightRects.Count);
                for (int index = 0; index < pairCount; index++)
                {
                    Rect expanded = sourceExpandedRects[index];
                    bool belongsToGroup =
                        expanded.X >= mergedRect.X && expanded.Y >= mergedRect.Y &&
                        expanded.Right <= mergedRect.Right && expanded.Bottom <= mergedRect.Bottom;
                    if (!belongsToGroup)
                        continue;

                    Rect tight = sourceTightRects[index];
                    left = Math.Min(left, tight.X);
                    top = Math.Min(top, tight.Y);
                    right = Math.Max(right, tight.Right);
                    bottom = Math.Max(bottom, tight.Bottom);
                    found = true;
                }

                // 理论上每个合并框都至少对应一个紧致框；保护性回退仅避免异常数据丢失统计项。
                result.Add(found
                    ? new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top))
                    : mergedRect);
            }

            return result;
        }

        /// <summary>
        /// 计算缺口附近模板笔画有多少宽度在 CIS 前景中确实缺失。
        /// 结果接近 1 表示笔画被完整截断；仅中心变暗而两侧仍连接时结果明显较低。
        /// </summary>
        private static double CalculateCrossSectionMissingRatio(
            Mat templateForeground,
            Mat capturedForeground,
            Mat gapSkeleton,
            double localLineHalfWidth)
        {
            int crossSectionRadius = Math.Max(1, (int)Math.Ceiling(localLineHalfWidth));
            using (Mat crossSectionKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(crossSectionRadius * 2 + 1, crossSectionRadius * 2 + 1)))
            using (var expandedGap = new Mat())
            using (var templateCrossSection = new Mat())
            using (var inverseCapturedForeground = new Mat())
            using (var missingCrossSection = new Mat())
            {
                Cv2.Dilate(gapSkeleton, expandedGap, crossSectionKernel);
                Cv2.BitwiseAnd(templateForeground, expandedGap, templateCrossSection);
                int templatePixels = Cv2.CountNonZero(templateCrossSection);
                if (templatePixels <= 0)
                    return 0;

                Cv2.BitwiseNot(capturedForeground, inverseCapturedForeground);
                Cv2.BitwiseAnd(
                    templateCrossSection,
                    inverseCapturedForeground,
                    missingCrossSection);
                return Cv2.CountNonZero(missingCrossSection) / (double)templatePixels;
            }
        }

        /// <summary>
        /// 判断绝对灰度恢复候选是否真的已经退回到局部背景。
        /// 该检查用于区分两种外观相近的情况：真实断口与仍有墨迹、但局部偏灰的连续细线。
        /// </summary>
        private static bool IsGapBrightnessConsistentWithBackground(
            Mat capturedGray,
            Mat templateForeground,
            Mat gapSkeleton,
            double localLineHalfWidth,
            int backgroundRingWidth,
            int maximumBrightnessExcess)
        {
            int crossSectionRadius = Math.Max(1, (int)Math.Ceiling(localLineHalfWidth));
            int innerRadius = crossSectionRadius + 1;
            int outerRadius = innerRadius + Math.Max(2, backgroundRingWidth);

            using (Mat crossSectionKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(crossSectionRadius * 2 + 1, crossSectionRadius * 2 + 1)))
            using (Mat innerKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(innerRadius * 2 + 1, innerRadius * 2 + 1)))
            using (Mat outerKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(outerRadius * 2 + 1, outerRadius * 2 + 1)))
            using (var expandedGap = new Mat())
            using (var gapSampleMask = new Mat())
            using (var innerArea = new Mat())
            using (var outerArea = new Mat())
            using (var inverseInnerArea = new Mat())
            using (var backgroundRing = new Mat())
            using (var inverseTemplate = new Mat())
            using (var backgroundSampleMask = new Mat())
            {
                // 缺口亮度在模板笔画的完整横截面上统计，避免只取骨架中心造成偶然性。
                Cv2.Dilate(gapSkeleton, expandedGap, crossSectionKernel);
                Cv2.BitwiseAnd(templateForeground, expandedGap, gapSampleMask);

                // 背景样本取缺口外围的环带，并排除模板中本来就应有图案的区域。
                Cv2.Dilate(gapSkeleton, innerArea, innerKernel);
                Cv2.Dilate(gapSkeleton, outerArea, outerKernel);
                Cv2.BitwiseNot(innerArea, inverseInnerArea);
                Cv2.BitwiseAnd(outerArea, inverseInnerArea, backgroundRing);
                Cv2.BitwiseNot(templateForeground, inverseTemplate);
                Cv2.BitwiseAnd(backgroundRing, inverseTemplate, backgroundSampleMask);

                int gapSampleCount = Cv2.CountNonZero(gapSampleMask);
                int backgroundSampleCount = Cv2.CountNonZero(backgroundSampleMask);
                if (gapSampleCount < 3 || backgroundSampleCount < 8)
                    return false;

                double gapMean = Cv2.Mean(capturedGray, gapSampleMask).Val0;
                double backgroundMean = Cv2.Mean(capturedGray, backgroundSampleMask).Val0;
                return gapMean <= backgroundMean + maximumBrightnessExcess;
            }
        }

        /// <summary>
        /// 构造细线前景证据：绝对亮度负责稳定识别正常白色图案，白顶帽负责保留
        /// 光照不均或整体偏灰、但相对局部背景仍然清晰连续的细线。
        /// </summary>
        private static void BuildFineLineForegroundEvidence(
            Mat gray,
            Mat foreground,
            int absoluteThreshold,
            int openingDiameter)
        {
            Cv2.Threshold(gray, foreground, absoluteThreshold, 255, ThresholdTypes.Binary);

            openingDiameter = Math.Max(3, openingDiameter);
            if ((openingDiameter & 1) == 0)
                openingDiameter++;

            // 局部对比度用于补回“绝对灰度偏低但仍连续”的细线。11% 可保留模糊弧线的
            // 连续证据，同时不会把当前回归样本中约 0.75 mm 的真实空档当成弱纹理补回。
            int localContrastThreshold = CalculateFineLineLocalContrastThreshold(absoluteThreshold);
            using (Mat openingKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(openingDiameter, openingDiameter)))
            using (var opened = new Mat())
            using (var whiteTopHat = new Mat())
            using (var localContrast = new Mat())
            {
                Cv2.MorphologyEx(gray, opened, MorphTypes.Open, openingKernel);
                Cv2.Subtract(gray, opened, whiteTopHat);
                Cv2.Threshold(
                    whiteTopHat,
                    localContrast,
                    localContrastThreshold,
                    255,
                    ThresholdTypes.Binary);
                Cv2.BitwiseOr(foreground, localContrast, foreground);
            }
        }

        /// <summary>统一计算细线局部对比度阈值，保证前景提取与恢复复核使用同一尺度。</summary>
        private static int CalculateFineLineLocalContrastThreshold(int absoluteThreshold)
        {
            return Math.Max(
                9,
                Math.Min(20, (int)Math.Round(absoluteThreshold * 0.11)));
        }

        /// <summary>
        /// 计算骨架主路径长度，而不是把分叉的所有支路长度相加。
        /// 对连通骨架执行两次最远点搜索：第一次找到远端，第二次得到主路径跨度；
        /// 水平/垂直相邻按 1 px、对角相邻按 √2 px。这样交叉点或 T 形结构不会虚增断口长度。
        /// </summary>
        private static double CalculateSkeletonPathLengthPixels(Mat skeletonComponent)
        {
            skeletonComponent.GetArray(out byte[] pixels);
            int width = skeletonComponent.Width;
            int height = skeletonComponent.Height;
            var skeletonIndices = new List<int>();
            var skeletonSet = new HashSet<int>();
            for (int index = 0; index < pixels.Length; index++)
            {
                if (pixels[index] == 0)
                    continue;
                skeletonIndices.Add(index);
                skeletonSet.Add(index);
            }

            if (skeletonIndices.Count == 0)
                return 0;
            if (skeletonIndices.Count == 1)
                return 1.0;

            FindFarthestSkeletonPoint(
                skeletonIndices[0],
                skeletonIndices,
                skeletonSet,
                width,
                height,
                out int firstEnd);
            double mainPathLength = FindFarthestSkeletonPoint(
                firstEnd,
                skeletonIndices,
                skeletonSet,
                width,
                height,
                out _);
            // 像素中心间距离比可见骨架少约一个像素，补 1 与最小长度的像素语义保持一致。
            return mainPathLength + 1.0;
        }

        /// <summary>
        /// 在八邻域骨架图上执行小规模 Dijkstra，返回起点到最远骨架点的距离。
        /// 候选骨架通常只有几十个像素，使用清晰的 O(N²) 实现可避免引入复杂堆结构。
        /// </summary>
        private static double FindFarthestSkeletonPoint(
            int startIndex,
            List<int> skeletonIndices,
            HashSet<int> skeletonSet,
            int width,
            int height,
            out int farthestIndex)
        {
            var distances = new Dictionary<int, double>(skeletonIndices.Count);
            var visited = new HashSet<int>();
            foreach (int index in skeletonIndices)
                distances[index] = double.PositiveInfinity;
            distances[startIndex] = 0;

            double diagonalStep = Math.Sqrt(2.0);
            while (visited.Count < skeletonIndices.Count)
            {
                int current = -1;
                double currentDistance = double.PositiveInfinity;
                foreach (int index in skeletonIndices)
                {
                    if (!visited.Contains(index) && distances[index] < currentDistance)
                    {
                        current = index;
                        currentDistance = distances[index];
                    }
                }
                if (current < 0)
                    break;

                visited.Add(current);
                int currentX = current % width;
                int currentY = current / width;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                            continue;

                        int neighborX = currentX + offsetX;
                        int neighborY = currentY + offsetY;
                        if (neighborX < 0 || neighborX >= width ||
                            neighborY < 0 || neighborY >= height)
                        {
                            continue;
                        }

                        int neighbor = neighborY * width + neighborX;
                        if (!skeletonSet.Contains(neighbor) || visited.Contains(neighbor))
                            continue;

                        double step = offsetX == 0 || offsetY == 0 ? 1.0 : diagonalStep;
                        double candidateDistance = currentDistance + step;
                        if (candidateDistance < distances[neighbor])
                            distances[neighbor] = candidateDistance;
                    }
                }
            }

            farthestIndex = startIndex;
            double farthestDistance = 0;
            foreach (int index in skeletonIndices)
            {
                double distance = distances[index];
                if (!double.IsInfinity(distance) && distance > farthestDistance)
                {
                    farthestDistance = distance;
                    farthestIndex = index;
                }
            }
            return farthestDistance;
        }

        /// <summary>读取掩膜内距离变换的中位数，用于判断候选是否位于设计细线而非宽实心区域。</summary>
        private static double CalculateMedianDistance(Mat distance, Mat mask)
        {
            distance.GetArray(out float[] distanceValues);
            mask.GetArray(out byte[] maskValues);
            var selected = new List<float>();
            int length = Math.Min(distanceValues.Length, maskValues.Length);
            for (int index = 0; index < length; index++)
            {
                if (maskValues[index] != 0)
                    selected.Add(distanceValues[index]);
            }

            if (selected.Count == 0)
                return double.PositiveInfinity;

            selected.Sort();
            int middle = selected.Count / 2;
            return selected.Count % 2 == 0
                ? (selected[middle - 1] + selected[middle]) * 0.5
                : selected[middle];
        }

        /// <summary>
        /// 从缺口外环的模板骨架中选取分居缺口两侧的两个连通分量，作为“断口前后结构”锚点。
        /// 成功返回的两个 Mat 由调用方释放。
        /// </summary>
        private static bool TryBuildEndpointAnchors(
            Mat skeleton,
            Mat componentMask,
            int removalRadius,
            int anchorReach,
            out Mat firstAnchor,
            out Mat secondAnchor)
        {
            firstAnchor = null;
            secondAnchor = null;

            int outerRadius = removalRadius + Math.Max(2, anchorReach);
            using (Mat innerKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(removalRadius * 2 + 1, removalRadius * 2 + 1)))
            using (Mat outerKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(outerRadius * 2 + 1, outerRadius * 2 + 1)))
            using (var inner = new Mat())
            using (var outer = new Mat())
            using (var ring = new Mat())
            using (var anchorPixels = new Mat())
            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centroids = new Mat())
            {
                Cv2.Dilate(componentMask, inner, innerKernel);
                Cv2.Dilate(componentMask, outer, outerKernel);
                Cv2.Subtract(outer, inner, ring);
                Cv2.BitwiseAnd(skeleton, ring, anchorPixels);

                int count = Cv2.ConnectedComponentsWithStats(
                    anchorPixels, labels, stats, centroids);
                var candidates = new List<int>();
                for (int label = 1; label < count; label++)
                {
                    if (stats.At<int>(label, 4) >= 2)
                        candidates.Add(label);
                }

                if (candidates.Count < 2)
                    return false;

                Moments gapMoments = Cv2.Moments(componentMask, true);
                if (gapMoments.M00 <= 0)
                    return false;
                double gapCenterX = gapMoments.M10 / gapMoments.M00;
                double gapCenterY = gapMoments.M01 / gapMoments.M00;

                double bestScore = double.PositiveInfinity;
                int bestFirst = -1;
                int bestSecond = -1;
                foreach (int first in candidates)
                {
                    double firstDx = centroids.At<double>(first, 0) - gapCenterX;
                    double firstDy = centroids.At<double>(first, 1) - gapCenterY;
                    double firstDistance = Math.Sqrt(firstDx * firstDx + firstDy * firstDy);
                    if (firstDistance < 1e-6)
                        continue;

                    foreach (int second in candidates)
                    {
                        if (second <= first)
                            continue;

                        double secondDx = centroids.At<double>(second, 0) - gapCenterX;
                        double secondDy = centroids.At<double>(second, 1) - gapCenterY;
                        double secondDistance = Math.Sqrt(
                            secondDx * secondDx + secondDy * secondDy);
                        if (secondDistance < 1e-6)
                            continue;

                        // 两锚点相对缺口中心应大致反向；同侧分支或邻近噪声不构成一条线的两端。
                        double cosine =
                            (firstDx * secondDx + firstDy * secondDy) /
                            (firstDistance * secondDistance);
                        if (cosine > -0.15)
                            continue;

                        double score = firstDistance + secondDistance +
                            (cosine + 1.0) * anchorReach;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestFirst = first;
                            bestSecond = second;
                        }
                    }
                }

                if (bestFirst < 0 || bestSecond < 0)
                {
                    return false;
                }

                firstAnchor = new Mat();
                secondAnchor = new Mat();
                Cv2.InRange(labels, new Scalar(bestFirst), new Scalar(bestFirst), firstAnchor);
                Cv2.InRange(labels, new Scalar(bestSecond), new Scalar(bestSecond), secondAnchor);
                return true;
            }
        }

        /// <summary>计算模板锚点被 CIS 宽容前景覆盖的比例。</summary>
        private static double CalculateCoverage(Mat anchor, Mat nearbyForeground)
        {
            int anchorPixels = Cv2.CountNonZero(anchor);
            if (anchorPixels <= 0)
                return 0;

            using (var covered = new Mat())
            {
                Cv2.BitwiseAnd(anchor, nearbyForeground, covered);
                return Cv2.CountNonZero(covered) / (double)anchorPixels;
            }
        }

        /// <summary>
        /// 在限定搜索半径内寻找一个共同位移，要求缺口区域和两个端点同时被 CIS 前景解释。
        /// 找到时说明差分主要来自整体错位，应拒绝该断裂候选。
        /// </summary>
        private static bool IsGapExplainedByMinorAlignmentOffset(
            Mat cisForeground,
            Mat gap,
            Mat firstAnchor,
            Mat secondAnchor,
            int searchRadius,
            int maximumTangentialShift)
        {
            const double minimumGapCoverage = 0.60;
            const double minimumAnchorCoverage = 0.55;

            cisForeground.GetArray(out byte[] foregroundValues);
            List<Point> gapPoints = CollectMaskPoints(gap);
            List<Point> firstPoints = CollectMaskPoints(firstAnchor);
            List<Point> secondPoints = CollectMaskPoints(secondAnchor);
            if (gapPoints.Count == 0 || firstPoints.Count == 0 || secondPoints.Count == 0)
                return false;

            Moments firstMoments = Cv2.Moments(firstAnchor, true);
            Moments secondMoments = Cv2.Moments(secondAnchor, true);
            if (firstMoments.M00 <= 0 || secondMoments.M00 <= 0)
                return false;
            double tangentX = secondMoments.M10 / secondMoments.M00 -
                              firstMoments.M10 / firstMoments.M00;
            double tangentY = secondMoments.M01 / secondMoments.M00 -
                              firstMoments.M01 / firstMoments.M00;
            double tangentLength = Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
            if (tangentLength <= 1e-6)
                return false;
            tangentX /= tangentLength;
            tangentY /= tangentLength;

            int width = cisForeground.Width;
            int height = cisForeground.Height;
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    double tangentialShift = Math.Abs(dx * tangentX + dy * tangentY);
                    if (tangentialShift > Math.Max(1, maximumTangentialShift))
                        continue;

                    if (CalculateShiftedCoverage(
                            gapPoints, foregroundValues, width, height, dx, dy) < minimumGapCoverage)
                    {
                        continue;
                    }

                    if (CalculateShiftedCoverage(
                            firstPoints, foregroundValues, width, height, dx, dy) < minimumAnchorCoverage ||
                        CalculateShiftedCoverage(
                            secondPoints, foregroundValues, width, height, dx, dy) < minimumAnchorCoverage)
                    {
                        continue;
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>把二值掩膜转换为稀疏点集，供小范围平移覆盖率搜索复用。</summary>
        private static List<Point> CollectMaskPoints(Mat mask)
        {
            mask.GetArray(out byte[] values);
            int width = mask.Width;
            var points = new List<Point>();
            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] != 0)
                    points.Add(new Point(index % width, index / width));
            }
            return points;
        }

        /// <summary>计算点集平移 (dx,dy) 后落在 CIS 前景中的比例，越界点按未覆盖处理。</summary>
        private static double CalculateShiftedCoverage(
            List<Point> points,
            byte[] foreground,
            int width,
            int height,
            int dx,
            int dy)
        {
            int covered = 0;
            foreach (Point point in points)
            {
                int x = point.X + dx;
                int y = point.Y + dy;
                if (x >= 0 && x < width && y >= 0 && y < height &&
                    foreground[y * width + x] != 0)
                {
                    covered++;
                }
            }
            return covered / (double)points.Count;
        }

        /// <summary>把检测尺度矩形映射回另一尺度，并裁剪到目标图像边界。</summary>
        private static Rect ScaleRect(Rect rect, double scaleX, double scaleY, Size bounds)
        {
            int x = Math.Max(0, (int)Math.Floor(rect.X * scaleX));
            int y = Math.Max(0, (int)Math.Floor(rect.Y * scaleY));
            int right = Math.Min(bounds.Width, (int)Math.Ceiling(rect.Right * scaleX));
            int bottom = Math.Min(bounds.Height, (int)Math.Ceiling(rect.Bottom * scaleY));
            return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
        }

        /// <summary>向四周扩展矩形并限制在图像内，用于构造候选局部证据 ROI。</summary>
        private static Rect ExpandRect(Rect rect, int margin, Size bounds)
        {
            int x = Math.Max(0, rect.X - margin);
            int y = Math.Max(0, rect.Y - margin);
            int right = Math.Min(bounds.Width, rect.Right + margin);
            int bottom = Math.Min(bounds.Height, rect.Bottom + margin);
            return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
        }

        /// <summary>合并相交或间距小于 margin 的候选框，避免同一物理断口被多个骨架段重复计数。</summary>
        private static List<Rect> MergeNearbyRects(List<Rect> source, int margin)
        {
            var merged = new List<Rect>();
            foreach (Rect sourceRect in source)
            {
                Rect current = sourceRect;
                bool combined;
                do
                {
                    combined = false;
                    for (int i = merged.Count - 1; i >= 0; i--)
                    {
                        Rect existing = merged[i];
                        bool nearby =
                            current.X <= existing.Right + margin &&
                            current.Right + margin >= existing.X &&
                            current.Y <= existing.Bottom + margin &&
                            current.Bottom + margin >= existing.Y;
                        if (!nearby)
                            continue;

                        int x = Math.Min(current.X, existing.X);
                        int y = Math.Min(current.Y, existing.Y);
                        int right = Math.Max(current.Right, existing.Right);
                        int bottom = Math.Max(current.Bottom, existing.Bottom);
                        current = new Rect(x, y, right - x, bottom - y);
                        merged.RemoveAt(i);
                        combined = true;
                    }
                }
                while (combined);

                merged.Add(current);
            }
            return merged;
        }

        /// <summary>
        /// 零件级分级局部对齐：已对齐跳过 → 轮廓距离场快速平移 →
        /// SIFT + RANSAC 相似变换 → 统一亚像素轮廓精修与局部稳定性门控。
        /// 成功时输出新建且由调用方负责释放的缩放图/可选原图；失败时输出 null，
        /// 调用方继续使用未局部变换的 cisScaled，从而让配准失败与缺陷判定解耦。
        /// </summary>
    }
}
