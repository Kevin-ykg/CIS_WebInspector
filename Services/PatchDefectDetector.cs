using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 零件级缺陷检测引擎。
    /// 移植自 localpeizhun.cpp 及 align_diff.py 结合策略：
    /// [可选]分级二次局部对齐（轮廓平移/SIFT/亚像素精修）→ 各自二值化 →
    /// 形态学容差差分 → 边缘屏蔽 → 连通域判定。
    /// </summary>
    public static partial class PatchDefectDetector
    {
        // 局部对比窗口只用于建立“相对周围背景仍然可见”的细线前景证据。
        // 固定为物理尺寸可避免分析缩放或 DPI 改变时窗口语义漂移；它不再由最大允许线宽控制。
        // 10 mm 与此前 FineLineMaxWidthMm=5 mm 时约 61 px 的默认窗口基本一致，降低算法改动风险。
        private const double FineLineLocalContrastWindowMm = 10.0;

        // 细线通道的几何容差使用独立物理尺寸，不能复用普通面积缺陷的 DefectToleranceInner。
        // 以下默认值等价于 LayoutDpi=300、analysisScale=0.5、旧版 DefectToleranceInner=6 px 时
        // 已通过真实断线样本验证的 1/2/6/12 px，保证解耦前后基准结果保持一致。
        private const double FineLineTangentialAlignmentToleranceMm = 0.17;
        private const double FineLineNormalAlignmentToleranceMm = 0.40;
        private const double FineLineEndpointSearchRadiusMm = 1.00;
        private const double FineLineEndpointAnchorLengthMm = 2.00;

        // 局部配准使用独立工作分辨率，避免 DefectDetectScale 同时改变缺陷检测精度、
        // SIFT 特征分布、RANSAC 物理容差和最大平移范围。当前常见零件宽约 2220 px，
        // 目标宽 700 px 与原 0.3 倍路径接近，可在基本不增加耗时的前提下稳定参数语义。
        private const int LocalAlignmentTargetWidthPx = 700;
        private const double LocalAlignmentRansacThresholdOriginalPx = 3.0;
        private const double LocalAlignmentMaxTranslationOriginalPx = 40.0;
        private const double LocalAlignmentMaxMatchDisplacementOriginalPx = 60.0;
        private const double LocalAlignmentMaxResidualRmsOriginalPx = 3.0;
        // 常规情况下要求 RANSAC 内点率达到 50%。复杂彩色/文字图案可能同时产生大量
        // 合法但不够唯一的 SIFT 候选，使“绝对内点充分”的正确矩阵被比例阈值误拒绝。
        // 条件通道只把候选送往后续覆盖率、RMS、矩阵和轮廓四重门控，并不直接接受矩阵。
        private const double LocalAlignmentMinimumInlierRatio = 0.50;
        private const double LocalAlignmentConditionalMinimumInlierRatio = 0.35;
        private const int LocalAlignmentConditionalMinimumInlierCount = 12;
        private const double LocalAlignmentConditionalMinimumBoundingCoverage = 0.08;
        private const double LocalAlignmentConditionalMaxResidualRmsOriginalPx = 2.5;
        private const double LocalAlignmentTranslationRefineRadiusOriginalPx = 10.0;
        // SIFT/RANSAC 或内点中位数已经给出可靠初值后，只允许距离场再修正 1 px；
        // 防止轮廓评分被真实缺陷牵引，覆盖掉特征点提供的几何共识。
        private const double LocalAlignmentCandidateRefineRadiusOriginalPx = 1.0;
        // 距离场先在工作图上做整数搜索，再以 0.25 px 步长搜索整数最优点周围的 2 px 邻域。
        // 700 px 固定工作宽度下，这已经能把最终平移细化到原图约 1 px 以内；最终 Warp 仍只执行一次。
        private const double LocalAlignmentSubpixelStepWorkPx = 0.25;
        private const double LocalAlignmentSubpixelRadiusWorkPx = 1.0;
        // 距离超过该值后不再继续增大惩罚，避免真实缺口、飞墨等少量异常轮廓牵引配准结果。
        private const double LocalAlignmentChamferDistanceCapWorkPx = 4.0;
        private const int LocalAlignmentMaxEdgeSamplesPerDirection = 6000;
        // 未配准轮廓平均误差低于此原图像素值时，继续重采样的收益很小，直接保留全局对齐结果。
        private const double LocalAlignmentNotNeededScoreOriginalPx = 0.50;
        private const double LocalAlignmentNotNeededShiftOriginalPx = 0.75;
        // 小于 10% 的全局轮廓收益很容易来自阈值噪声、JPEG 纹理或真实缺陷，而不是稳定错位。
        // 宁可保留 H0 结果，也不为小幅评分收益承担新增细线误检的风险。
        private const double LocalAlignmentMinEdgeImprovementRatio = 0.10;
        // 纯轮廓平移缺少特征几何共识，周期图案可能在错误的小位移上取得有限收益；
        // 因此快速分支使用更严格的 20%，不足时交给 SIFT 分支继续判断。
        private const double LocalAlignmentFastTranslationMinImprovementRatio = 0.20;
        // 快速搜索的最优点若贴近搜索边界，说明距离场仍想继续向外移动，当前结果不是可信极小值。
        // 这类候选不得直接应用，应交给具有特征几何约束的 SIFT 分支继续判断。
        private const double LocalAlignmentFastTranslationBoundaryRatio = 0.85;
        private const double LocalAlignmentStrongEdgeImprovementRatio = 0.20;
        private const double LocalAlignmentStrongCaseMaxLocalRegressionPixels = 0.75;
        private const double LocalAlignmentTranslationConsensusP80OriginalPx = 5.0;
        // 对明显整体平移，若 SIFT 内点位移高度一致，可直接信任纯平移模型；此时逐格轮廓
        // 可能被真实缺陷干扰而出现假退化。小位移仍必须通过局部稳定性，避免新增细线误检。
        private const double LocalAlignmentStrongTranslationConsensusP80OriginalPx = 2.0;
        private const double LocalAlignmentStrongTranslationMinMagnitudeOriginalPx = 5.0;
        private const ulong LocalAlignmentRansacSeed = 0x5EED2026UL;
        private const int LocalAlignmentValidationGridSize = 3;
        private const int LocalAlignmentMinReferenceEdgesPerCell = 30;
        private const double LocalAlignmentMaxLocalRegressionPixels = 0.25;
        // 局部稳定门控只允许绝对 0.25 个工作像素的轻微数值波动；不再按当前误差比例放宽，
        // 否则原本较差的局部区域反而会获得更大的退化额度，容易新增细线误检。

        // OpenCV 的随机数发生器会被 EstimateAffinePartial2D/RANSAC 使用。并行零件检测时如果任由
        // 各 worker 竞争随机状态，同一批图可能得到不同的内点集合和仿射矩阵。
        // 这里只串行化很短的 RANSAC 求解阶段；SIFT、匹配、Warp 和缺陷检测仍保持并行。
        private static readonly object LocalAlignmentRansacSync = new object();

        private static readonly ImageEncodingParam[] AlignedPatchJpegParameters =
        {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)
        };

        /// <summary>
        /// 批处理入口：复用调用方提供的 worker 和模板缓存，避免每个零件重建 SIFT/Matcher。
        /// 单件流程为 Alpha/CIS 二值化 → 可选 SIFT 局部对齐 → 容差差分 → 边缘屏蔽 →
        /// 普通连通域与细线连续性两类判定。
        /// </summary>
        internal static PatchDefectResult Detect(
            Mat alphaImg,
            Mat cisImg,
            int cisBaseThresh,
            AppConfig config,
            string outputPath,
            string cisOutputPath,
            string edgeExclusionOutputPath,
            PatchSiftWorker alignmentWorker,
            string partId,
            IAppLogger logger)
        {
            return DetectCore(
                alphaImg,
                cisImg,
                cisBaseThresh,
                config,
                outputPath,
                cisOutputPath,
                edgeExclusionOutputPath,
                alignmentWorker,
                partId,
                logger ?? NullAppLogger.Instance);
        }

        private static PatchDefectResult DetectCore(
            Mat alphaImg,
            Mat cisImg,
            int cisBaseThresh,
            AppConfig config,
            string outputPath,
            string cisOutputPath,
            string edgeExclusionOutputPath,
            PatchSiftWorker alignmentWorker,
            string partId,
            IAppLogger logger)
        {
            var result = new PatchDefectResult();
            // ToGray 对单通道输入返回原 Mat，对多通道输入返回新 Mat；finally 中按引用关系决定释放权。
            Mat alphaGray = ToGray(alphaImg);
            Mat cisGray = ToGray(cisImg);

            try
            {
                // 主差分通道允许缩小处理以控制节拍，但最小宽度会限制过度缩小造成的细节丢失。
                double scale = config.DefectDetectScale;
                if (scale <= 0)
                    throw new ArgumentOutOfRangeException(nameof(config.DefectDetectScale), "缺陷检测缩放比例必须大于 0。");

                int scaledW = (int)(alphaGray.Width * scale);
                if (scaledW < config.DefectMinScaledWidth && alphaGray.Width > config.DefectMinScaledWidth)
                    scale = (double)config.DefectMinScaledWidth / alphaGray.Width;

                using (var alphaScaled = new Mat())
                using (var cisScaled = new Mat())
                {
                    // 缺陷二值化仍使用 Nearest，保持原有检测语义。
                    Cv2.Resize(alphaGray, alphaScaled, new Size(), scale, scale, InterpolationFlags.Nearest);
                    Cv2.Resize(cisGray, cisScaled, alphaScaled.Size(), 0, 0, InterpolationFlags.Nearest);

                    Mat cisAlignedOwned = null;
                    Mat cisAlignedOriginalOwned = null;
                    Mat cisAligned = cisScaled;
                    Mat cisToSave = cisImg;

                    try
                    {
                        if (config.EnableSiftLocalAlign)
                        {
                            if (alignmentWorker == null)
                                throw new InvalidOperationException("启用局部配准时必须提供 SIFT worker。");

                            // 局部配准使用独立、固定目标宽度的工作图；DefectDetectScale 只负责后续缺陷差分。
                            // Area 缩小先抑制混叠，再用 3x3 均值滤波降低 CIS 纹理噪声；快速轮廓分支
                            // 与困难场景的 SIFT 分支共享同一输入，避免两套尺度定义造成参数耦合。
                            using (var alphaBlurred = new Mat())
                            using (var cisBlurred = new Mat())
                            using (var alphaAlignmentInput = new Mat())
                            using (var cisAlignmentInput = new Mat())
                            {
                                double alignmentScale = Math.Min(
                                    1.0,
                                    LocalAlignmentTargetWidthPx / (double)Math.Max(1, alphaGray.Width));
                                var alignmentSize = new Size(
                                    Math.Max(1, (int)Math.Round(alphaGray.Width * alignmentScale)),
                                    Math.Max(1, (int)Math.Round(alphaGray.Height * alignmentScale)));
                                Cv2.Resize(
                                    alphaGray,
                                    alphaAlignmentInput,
                                    alignmentSize,
                                    0,
                                    0,
                                    InterpolationFlags.Area);
                                Cv2.Resize(
                                    cisGray,
                                    cisAlignmentInput,
                                    alignmentSize,
                                    0,
                                    0,
                                    InterpolationFlags.Area);
                                Cv2.Blur(alphaAlignmentInput, alphaBlurred, new Size(3, 3));
                                Cv2.Blur(cisAlignmentInput, cisBlurred, new Size(3, 3));

                                // 细线断裂需要在原始分辨率上复核。这里仅复用已经求得的仿射矩阵
                                // 多生成一张原始分辨率对齐图，不改变现有 SIFT 匹配与矩阵估计路径。
                                bool needOriginalWarp = config.EnableFineLineBreakDetection ||
                                    (!string.IsNullOrEmpty(cisOutputPath) && config.SaveCroppedImages);
                                if (TryLocalAlign(
                                    alphaBlurred,
                                    cisBlurred,
                                    alphaScaled,
                                    cisScaled,
                                    cisImg,
                                    scale,
                                    alignmentScale,
                                    needOriginalWarp,
                                    config.DefectAlphaBinaryThresh,
                                    cisBaseThresh,
                                    alignmentWorker,
                                    partId,
                                    logger,
                                    out cisAlignedOwned,
                                    out cisAlignedOriginalOwned))
                                {
                                    cisAligned = cisAlignedOwned;
                                    if (cisAlignedOriginalOwned != null)
                                        cisToSave = cisAlignedOriginalOwned;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(cisOutputPath) && config.SaveCroppedImages)
                        {
                            try
                            {
                                // cisBaseThresh 已由调用方计算为：Mark 最佳阈值 + DefectCisThreshOffset，
                                // 并完成 0~255 限幅。裁切结果应展示该最终阈值真正生效后的图像，
                                // 因此对局部配准后的原始尺寸 CIS 灰度图进行二值化后再保存。
                                Mat cisGrayToSave = ToGray(cisToSave);
                                try
                                {
                                    using (var cisBinaryToSave = new Mat())
                                    {
                                        Cv2.Threshold(
                                            cisGrayToSave,
                                            cisBinaryToSave,
                                            cisBaseThresh,
                                            255,
                                            ThresholdTypes.Binary);
                                        Cv2.ImWrite(
                                            cisOutputPath,
                                            cisBinaryToSave,
                                            AlignedPatchJpegParameters);
                                    }
                                }
                                finally
                                {
                                    // 单通道输入由 ToGray 原样返回，不能释放其所有者 cisToSave；
                                    // 仅释放多通道转换时创建的临时灰度 Mat。
                                    if (!ReferenceEquals(cisGrayToSave, cisToSave))
                                        cisGrayToSave.Dispose();
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLog.Write(
                                    logger,
                                    $"[LocalAlign][WARN] {FormatPartId(partId)} 保存二值化对齐图失败: {ex.Message}");
                            }
                        }

                        using (var alphaBinary = new Mat())
                        using (var cisBinary = new Mat())
                        {
                            // 两幅图分别二值化：Alpha 表示“设计上应有的结构”，CIS 表示“实际采集到的结构”。
                            Cv2.Threshold(alphaScaled, alphaBinary, config.DefectAlphaBinaryThresh, 255, ThresholdTypes.Binary);
                            Cv2.Threshold(cisAligned, cisBinary, cisBaseThresh, 255, ThresholdTypes.Binary);

                            // 用户配置统一使用物理单位：长度按 px/mm × scale 换算，面积按 (px/mm × scale)² 换算。
                            // 这样修改 DefectDetectScale 只改变工作分辨率，不会改变参数代表的实际物理尺寸。
                            int scaledTolInner = Math.Max(
                                1,
                                ConvertLengthMmToScaledPixels(
                                    config.DefectToleranceInner,
                                    config.LayoutDpi,
                                    scale));
                            int scaledTolOuter = Math.Max(
                                1,
                                ConvertLengthMmToScaledPixels(
                                    config.DefectToleranceOuter,
                                    config.LayoutDpi,
                                    scale));
                            int scaledEdgeThick = ConvertLengthMmToScaledPixels(
                                config.DefectEdgeExclusionThick,
                                config.LayoutDpi,
                                scale);
                            int scaledEdgeSmall = ConvertLengthMmToScaledPixels(
                                config.DefectEdgeExclusionSmall,
                                config.LayoutDpi,
                                scale);
                            int scaledAreaThreshInner = ConvertAreaMm2ToScaledPixels(
                                config.DefectAreaThreshInner,
                                config.LayoutDpi,
                                scale);
                            int scaledAreaThreshOuter = ConvertAreaMm2ToScaledPixels(
                                config.DefectAreaThreshOuter,
                                config.LayoutDpi,
                                scale);

                            using (Mat kernelInner = Cv2.GetStructuringElement(
                                MorphShapes.Ellipse, new Size(scaledTolInner, scaledTolInner)))
                            using (var cisDilatedInner = new Mat())
                            using (var difInner = new Mat())
                            using (Mat kernelOuter = Cv2.GetStructuringElement(
                                MorphShapes.Ellipse, new Size(scaledTolOuter, scaledTolOuter)))
                            using (var alphaDilatedOuter = new Mat())
                            using (var difOuter = new Mat())
                            using (Mat edgeMask = BuildEdgeExclusionMask(
                                alphaBinary, scaledEdgeThick, scaledEdgeSmall))
                            using (Mat fineLineMask = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1))
                            {
                                // 保存当前检测真正使用的合并边缘屏蔽掩膜：白色为从普通内部/外部
                                // 差分中清零的区域，黑色为继续参与连通域分析的区域。
                                // 保存时仅用最近邻还原到原始零件分辨率，便于与 Alpha/CIS 逐像素对照；
                                // 掩膜内容仍来自本次检测使用的 edgeMask，不重新计算屏蔽区域。
                                if (!string.IsNullOrEmpty(edgeExclusionOutputPath))
                                    SaveEdgeExclusionMask(
                                        edgeMask,
                                        alphaGray.Size(),
                                        edgeExclusionOutputPath,
                                        logger);

                                // 普通面积通道：设计有而实拍缺失为内部缺陷，实拍多出而设计没有为外部缺陷；
                                // 膨胀半径提供少量位置容差，避免亚像素对齐误差直接形成整圈轮廓。
                                Cv2.Dilate(cisBinary, cisDilatedInner, kernelInner);
                                Cv2.Subtract(alphaBinary, cisDilatedInner, difInner);
                                Cv2.Dilate(alphaBinary, alphaDilatedOuter, kernelOuter);
                                Cv2.Subtract(cisBinary, alphaDilatedOuter, difOuter);

                                List<Rect> fineLineRects = new List<Rect>();
                                List<Rect> fineLineRectsOriginal = new List<Rect>();
                                int fineLineCount = 0;
                                double maxFineLineLengthMm = 0;
                                double maxFineLineWidthMm = 0;
                                // 细线连续性通道在边缘屏蔽清零前独立复核候选缺口，专门补回普通面积通道
                                // 容易漏掉的细轮廓断裂；其结果最终与普通通道做“任一命中即 NG”的合并。
                                if (config.EnableFineLineBreakDetection &&
                                    config.FineLineMinBreakLengthMm > 0 &&
                                    Cv2.CountNonZero(edgeMask) > 0)
                                {
                                    Mat fineLineCisSource = cisAlignedOriginalOwned ?? cisGray;
                                    Mat fineLineCisGray = ToGray(fineLineCisSource);
                                    try
                                    {
                                        // 细线复核使用不低于 0.5 的独立细节尺度，兼顾短断口像素数与节拍。
                                        double fineAnalysisScale = Math.Min(1.0, Math.Max(0.5, scale));
                                        var fineAnalysisSize = new Size(
                                            Math.Max(1, (int)Math.Round(alphaGray.Width * fineAnalysisScale)),
                                            Math.Max(1, (int)Math.Round(alphaGray.Height * fineAnalysisScale)));
                                        using (var fineAlpha = new Mat())
                                        using (var fineCis = new Mat())
                                        using (Mat fineLineMaskAnalysis = Mat.Zeros(
                                            fineAnalysisSize, MatType.CV_8UC1))
                                        {
                                            Cv2.Resize(
                                                alphaGray,
                                                fineAlpha,
                                                fineAnalysisSize,
                                                0,
                                                0,
                                                InterpolationFlags.Area);
                                            Cv2.Resize(
                                                fineLineCisGray,
                                                fineCis,
                                                fineAnalysisSize,
                                                0,
                                                0,
                                                InterpolationFlags.Area);

                                            List<Rect> fineLineRectsAnalysis = DetectFineLineBreaksAtDetailScale(
                                                fineAlpha,
                                                fineCis,
                                                cisBaseThresh,
                                                fineAnalysisScale,
                                                config,
                                                fineLineMaskAnalysis,
                                                out fineLineCount,
                                                out maxFineLineLengthMm,
                                                out maxFineLineWidthMm,
                                                out List<Rect> fineLineTightRectsAnalysis,
                                                out List<int> fineLineAreasPixels);

                                            // 细线尺寸基于断口紧致框，而不是为了醒目标注而扩展后的显示框。
                                            result.FineLineBreakMeasurements = BuildDefectGeometryMeasurements(
                                                fineLineTightRectsAnalysis,
                                                fineLineAreasPixels,
                                                config.LayoutDpi,
                                                fineAnalysisScale);

                                            double analysisToOriginalX =
                                                alphaGray.Width / (double)fineAnalysisSize.Width;
                                            double analysisToOriginalY =
                                                alphaGray.Height / (double)fineAnalysisSize.Height;
                                            fineLineRectsOriginal = fineLineRectsAnalysis
                                                .Select(rect => ScaleRect(
                                                    rect,
                                                    analysisToOriginalX,
                                                    analysisToOriginalY,
                                                    alphaGray.Size()))
                                                .ToList();

                                            double analysisToDetectX =
                                                alphaBinary.Width / (double)fineAnalysisSize.Width;
                                            double analysisToDetectY =
                                                alphaBinary.Height / (double)fineAnalysisSize.Height;
                                            fineLineRects = fineLineRectsAnalysis
                                                .Select(rect => ScaleRect(
                                                    rect,
                                                    analysisToDetectX,
                                                    analysisToDetectY,
                                                    alphaBinary.Size()))
                                                .ToList();

                                            if (fineLineCount > 0)
                                            {
                                                Cv2.Resize(
                                                    fineLineMaskAnalysis,
                                                    fineLineMask,
                                                    alphaBinary.Size(),
                                                    0,
                                                    0,
                                                    InterpolationFlags.Nearest);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        if (!ReferenceEquals(fineLineCisGray, fineLineCisSource))
                                            fineLineCisGray.Dispose();
                                    }
                                }

                                // 边缘屏蔽只负责判断“整个原始连通域是否允许进入面积检测”。
                                // 完全位于屏蔽带内的轮廓被忽略；一旦越过屏蔽带，则恢复完整轮廓，
                                // 使用屏蔽前的原始面积和包围框，避免真实缺陷被切小或切碎后漏检。
                                List<Rect> innerRects = AnalyzeConnectedComponentsPreservingOriginalArea(
                                    difInner,
                                    edgeMask,
                                    scaledAreaThreshInner,
                                    out int maxAreaInner,
                                    out int innerCount,
                                    out List<int> innerAreasPixels);
                                List<Rect> outerRects = AnalyzeConnectedComponentsPreservingOriginalArea(
                                    difOuter,
                                    edgeMask,
                                    scaledAreaThreshOuter,
                                    out int maxAreaOuter,
                                    out int outerCount,
                                    out List<int> outerAreasPixels);

                                if (fineLineCount > 0)
                                    Cv2.BitwiseOr(difInner, fineLineMask, difInner);

                                // 普通通道以连通域面积判定，细线通道以结构连续性判定；任一命中都使零件 NG。
                                // 连通域分析发生在缩小后的检测图上；对外结果换算成 mm²，
                                // 使日志中的最大面积与用户配置阈值使用同一物理单位。
                                result.MaxAreaInnerMm2 = ConvertScaledAreaToMm2(
                                    maxAreaInner,
                                    config.LayoutDpi,
                                    scale);
                                result.MaxAreaOuterMm2 = ConvertScaledAreaToMm2(
                                    maxAreaOuter,
                                    config.LayoutDpi,
                                    scale);
                                result.InnerDefectCount = innerCount;
                                result.OuterDefectCount = outerCount;
                                result.FineLineBreakCount = fineLineCount;
                                result.MaxFineLineBreakLengthMm = maxFineLineLengthMm;
                                result.MaxFineLineBreakWidthMm = maxFineLineWidthMm;
                                result.IsPass = maxAreaInner <= scaledAreaThreshInner &&
                                                maxAreaOuter <= scaledAreaThreshOuter &&
                                                fineLineCount == 0;

                                // 对外矩形统一还原到零件原始分辨率，GlobalRoi 的叠加由 PatchCropper 完成。
                                result.FineLineBreakRects = fineLineRectsOriginal;
                                // innerRects/outerRects 已在连通域分析中通过最终面积门槛，
                                // 因此这里生成的明细不会包含被面积阈值过滤掉的候选。
                                result.InnerDefectMeasurements = BuildDefectGeometryMeasurements(
                                    innerRects,
                                    innerAreasPixels,
                                    config.LayoutDpi,
                                    scale);
                                result.OuterDefectMeasurements = BuildDefectGeometryMeasurements(
                                    outerRects,
                                    outerAreasPixels,
                                    config.LayoutDpi,
                                    scale);
                                result.InnerRects = innerRects.Select(r => new Rect(
                                    (int)(r.X / scale), (int)(r.Y / scale),
                                    Math.Max(1, (int)(r.Width / scale)),
                                    Math.Max(1, (int)(r.Height / scale)))).ToList();
                                result.OuterRects = outerRects.Select(r => new Rect(
                                    (int)(r.X / scale), (int)(r.Y / scale),
                                    Math.Max(1, (int)(r.Width / scale)),
                                    Math.Max(1, (int)(r.Height / scale)))).ToList();

                                if (!string.IsNullOrEmpty(outputPath))
                                {
                                    SaveVisualization(alphaBinary, cisBinary, difInner, difOuter,
                                        innerRects, outerRects, fineLineRects, result.IsPass, outputPath, logger);
                                }
                            }
                        }
                    }
                    finally
                    {
                        cisAlignedOriginalOwned?.Dispose();
                        cisAlignedOwned?.Dispose();
                    }
                }

                return result;
            }
            finally
            {
                if (!ReferenceEquals(cisGray, cisImg))
                    cisGray.Dispose();
                if (!ReferenceEquals(alphaGray, alphaImg))
                    alphaGray.Dispose();
            }
        }

        /// <summary>
        /// 转灰度，尽量避免不必要的内存分配。
        /// 输入如果已经是单通道，直接返回（不 Clone）。
        /// 注意：调用者不应修改返回的 Mat。
        /// </summary>
    }
}
