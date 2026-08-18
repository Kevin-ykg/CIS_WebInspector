using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.XImgProc;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 单个零件的缺陷检测结果。矩形坐标均相对零件原始分辨率 ROI；
    /// GlobalRoi 则位于翻转后的 TIFF/CIS 全局目标空间，供批次结果图定位。
    /// 内部缺陷、外部缺陷和细线断裂保持独立分类，计数与矩形集合不得相互合并。
    /// </summary>
    public class PatchDefectResult
    {
        public string PartId { get; set; }
        /// <summary>普通内部缺陷的最大连通域物理面积，单位 mm²。</summary>
        public double MaxAreaInnerMm2 { get; set; }
        /// <summary>普通外部缺陷的最大连通域物理面积，单位 mm²。</summary>
        public double MaxAreaOuterMm2 { get; set; }
        public int InnerDefectCount { get; set; }
        public int OuterDefectCount { get; set; }
        public int FineLineBreakCount { get; set; }
        public double MaxFineLineBreakLengthMm { get; set; }
        public bool IsPass { get; set; }
        public Rect GlobalRoi { get; set; }
        public List<Rect> InnerRects { get; set; } = new List<Rect>();
        public List<Rect> OuterRects { get; set; } = new List<Rect>();
        public List<Rect> FineLineBreakRects { get; set; } = new List<Rect>();
    }

    /// <summary>
    /// 单次裁切批次内复用的 TIFF/Alpha 模板特征缓存。
    /// 缓存只在一批零件处理期间存在，避免跨批次持有 Mat。快速签名只负责分桶，
    /// 命中前仍做逐像素精确比较，因此哈希碰撞不会串用不同模板的 SIFT 特征。
    /// </summary>
    internal sealed class PatchSiftTemplateCache : IDisposable
    {
        private const int QuickKeySignatureSize = 16;

        private sealed class TemplateEntry : IDisposable
        {
            public Mat Representative { get; }
            public PatchSiftTemplateFeatures Features { get; }

            public TemplateEntry(Mat representative, PatchSiftTemplateFeatures features)
            {
                Representative = representative;
                Features = features;
            }

            public void Dispose()
            {
                Features.Dispose();
                Representative.Dispose();
            }
        }

        private sealed class TemplateBucket : IDisposable
        {
            public object SyncRoot { get; } = new object();
            public List<TemplateEntry> Entries { get; } = new List<TemplateEntry>();

            public void Dispose()
            {
                lock (SyncRoot)
                {
                    foreach (TemplateEntry entry in Entries)
                        entry.Dispose();
                    Entries.Clear();
                }
            }
        }

        private readonly ConcurrentDictionary<string, TemplateBucket> _buckets =
            new ConcurrentDictionary<string, TemplateBucket>();
        private int _entryCount;
        private long _hitCount;
        private long _missCount;
        private long _exactComparisonCount;
        private long _exactComparisonTicks;
        private long _quickKeyTicks;

        public int Count => Volatile.Read(ref _entryCount);
        public long HitCount => Interlocked.Read(ref _hitCount);
        public long MissCount => Interlocked.Read(ref _missCount);
        public long ExactComparisonCount => Interlocked.Read(ref _exactComparisonCount);
        public double ExactComparisonElapsedMilliseconds =>
            Interlocked.Read(ref _exactComparisonTicks) * 1000.0 / Stopwatch.Frequency;
        public double QuickKeyElapsedMilliseconds =>
            Interlocked.Read(ref _quickKeyTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>按图像内容复用模板 SIFT 特征；新条目持有模板副本和描述子，随批次缓存释放。</summary>
        public PatchSiftTemplateFeatures GetOrCreate(Mat templateFeatureImage, SIFT sift)
        {
            if (templateFeatureImage == null || templateFeatureImage.Empty())
                throw new ArgumentException("模板特征图不能为空。", nameof(templateFeatureImage));
            if (sift == null)
                throw new ArgumentNullException(nameof(sift));

            long keyStart = Stopwatch.GetTimestamp();
            string quickKey = ComputeQuickKey(templateFeatureImage);
            Interlocked.Add(ref _quickKeyTicks, Stopwatch.GetTimestamp() - keyStart);

            // 16×16 二维网格签名只用于快速分桶；命中缓存前仍进行原生像素级精确比较，
            // 因此签名碰撞不会导致不同模板复用同一组 SIFT 特征。
            TemplateBucket bucket = _buckets.GetOrAdd(quickKey, _ => new TemplateBucket());
            lock (bucket.SyncRoot)
            {
                foreach (TemplateEntry entry in bucket.Entries)
                {
                    Interlocked.Increment(ref _exactComparisonCount);
                    long compareStart = Stopwatch.GetTimestamp();
                    bool exactMatch = AreExactlyEqual(templateFeatureImage, entry.Representative);
                    Interlocked.Add(ref _exactComparisonTicks, Stopwatch.GetTimestamp() - compareStart);
                    if (exactMatch)
                    {
                        Interlocked.Increment(ref _hitCount);
                        return entry.Features;
                    }
                }

                Mat representative = templateFeatureImage.Clone();
                try
                {
                    PatchSiftTemplateFeatures features = PatchSiftTemplateFeatures.Create(templateFeatureImage, sift);
                    bucket.Entries.Add(new TemplateEntry(representative, features));
                    representative = null;
                    Interlocked.Increment(ref _entryCount);
                    Interlocked.Increment(ref _missCount);
                    return features;
                }
                finally
                {
                    representative?.Dispose();
                }
            }
        }

        private static bool AreExactlyEqual(Mat first, Mat second)
        {
            return first.Rows == second.Rows &&
                   first.Cols == second.Cols &&
                   first.Type() == second.Type() &&
                   Cv2.Norm(first, second, NormTypes.L1) == 0.0;
        }

        /// <summary>对规则采样的 16×16 像素生成快速分桶键，不把该键当作最终相等判据。</summary>
        private static unsafe string ComputeQuickKey(Mat image)
        {
            const ulong offsetBasis = 1469598103934665603UL;
            const ulong prime = 1099511628211UL;
            int pixelBytes = checked((int)image.ElemSize());
            byte* data = image.DataPointer;
            long step = (long)image.Step();
            ulong hash = offsetBasis;

            for (int gridY = 0; gridY < QuickKeySignatureSize; gridY++)
            {
                int row = Math.Min(
                    image.Rows - 1,
                    (int)(((2L * gridY + 1) * image.Rows) / (2L * QuickKeySignatureSize)));
                for (int gridX = 0; gridX < QuickKeySignatureSize; gridX++)
                {
                    int column = Math.Min(
                        image.Cols - 1,
                        (int)(((2L * gridX + 1) * image.Cols) / (2L * QuickKeySignatureSize)));
                    byte* pixel = data + row * step + column * pixelBytes;
                    for (int channelByte = 0; channelByte < pixelBytes; channelByte++)
                    {
                        hash ^= *(pixel + channelByte);
                        hash *= prime;
                    }
                }
            }

            return $"{image.Rows}x{image.Cols}:{image.Type()}:{hash:X16}";
        }

        public void Dispose()
        {
            foreach (TemplateBucket bucket in _buckets.Values)
                bucket.Dispose();
            _buckets.Clear();
            Volatile.Write(ref _entryCount, 0);
        }
    }

    internal sealed class PatchSiftTemplateFeatures : IDisposable
    {
        public KeyPoint[] KeyPoints { get; private set; }
        public Mat Descriptors { get; private set; }

        private PatchSiftTemplateFeatures(KeyPoint[] keyPoints, Mat descriptors)
        {
            KeyPoints = keyPoints;
            Descriptors = descriptors;
        }

        /// <summary>提取模板关键点和描述子；返回对象拥有 descriptors Mat。</summary>
        public static PatchSiftTemplateFeatures Create(Mat featureImage, SIFT sift)
        {
            var descriptors = new Mat();
            try
            {
                sift.DetectAndCompute(featureImage, null, out KeyPoint[] keyPoints, descriptors);
                return new PatchSiftTemplateFeatures(keyPoints, descriptors);
            }
            catch
            {
                descriptors.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Descriptors?.Dispose();
            Descriptors = null;
            KeyPoints = Array.Empty<KeyPoint>();
        }
    }

    /// <summary>Parallel.ForEach 每个 worker 独占的非线程安全 OpenCV 对象。</summary>
    internal sealed class PatchSiftWorker : IDisposable
    {
        public SIFT Sift { get; }
        public BFMatcher Matcher { get; }
        public PatchSiftTemplateCache TemplateCache { get; }

        public PatchSiftWorker(PatchSiftTemplateCache templateCache)
        {
            TemplateCache = templateCache ?? throw new ArgumentNullException(nameof(templateCache));
            // 与原始二次配准保持一致；worker 只负责复用对象，不改变算法参数。
            Sift = SIFT.Create(100);
            Matcher = new BFMatcher(NormTypes.L2);
        }

        public void Dispose()
        {
            Matcher.Dispose();
            Sift.Dispose();
        }
    }

    /// <summary>
    /// 零件级缺陷检测引擎。
    /// 移植自 localpeizhun.cpp 及 align_diff.py 结合策略：
    /// [可选]SIFT 二次局部对齐 → 各自二值化 → 形态学容差差分 → 边缘屏蔽 → 连通域判定。
    /// </summary>
    public static class PatchDefectDetector
    {
        // 局部对比窗口只用于建立“相对周围背景仍然可见”的细线前景证据。
        // 固定为物理尺寸可避免分析缩放或 DPI 改变时窗口语义漂移；它不再由最大允许线宽控制。
        // 10 mm 与此前 FineLineMaxWidthMm=5 mm 时约 61 px 的默认窗口基本一致，降低算法改动风险。
        private const double FineLineLocalContrastWindowMm = 10.0;

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
            PatchSiftWorker alignmentWorker,
            string partId)
        {
            return DetectCore(alphaImg, cisImg, cisBaseThresh, config, outputPath, cisOutputPath, alignmentWorker, partId);
        }

        private static PatchDefectResult DetectCore(
            Mat alphaImg,
            Mat cisImg,
            int cisBaseThresh,
            AppConfig config,
            string outputPath,
            string cisOutputPath,
            PatchSiftWorker alignmentWorker,
            string partId)
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

                            // 特征图严格沿用原始路径：Nearest 缩放后的图像 + 3x3 均值滤波。
                            using (var alphaBlurred = new Mat())
                            using (var cisBlurred = new Mat())
                            {
                                Cv2.Blur(alphaScaled, alphaBlurred, new Size(3, 3));
                                Cv2.Blur(cisScaled, cisBlurred, new Size(3, 3));

                                // 细线断裂需要在原始分辨率上复核。这里仅复用已经求得的仿射矩阵
                                // 多生成一张原始分辨率对齐图，不改变现有 SIFT 匹配与矩阵估计路径。
                                bool needOriginalWarp = config.EnableFineLineBreakDetection ||
                                    (!string.IsNullOrEmpty(cisOutputPath) && config.SaveCroppedImages);
                                if (TrySiftAlign(
                                    alphaBlurred,
                                    cisBlurred,
                                    alphaScaled,
                                    cisScaled,
                                    cisImg,
                                    scale,
                                    needOriginalWarp,
                                    alignmentWorker,
                                    partId,
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
                                Cv2.ImWrite(cisOutputPath, cisToSave, AlignedPatchJpegParameters);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[LocalAlign] {FormatPartId(partId)} 保存对齐图失败: {ex.Message}");
                            }
                        }

                        using (var alphaBinary = new Mat())
                        using (var cisBinary = new Mat())
                        {
                            // 两幅图分别二值化：Alpha 表示“设计上应有的结构”，CIS 表示“实际采集到的结构”。
                            Cv2.Threshold(alphaScaled, alphaBinary, config.DefectAlphaBinaryThresh, 255, ThresholdTypes.Binary);
                            Cv2.Threshold(cisAligned, cisBinary, cisBaseThresh, 255, ThresholdTypes.Binary);

                            // 长度参数按 scale 换算，面积阈值必须按 scale² 换算，才能保持原图尺度语义。
                            int scaledTolInner = Math.Max(1, (int)Math.Round(config.DefectToleranceInner * scale));
                            int scaledTolOuter = Math.Max(1, (int)Math.Round(config.DefectToleranceOuter * scale));
                            int scaledEdgeThick = config.DefectEdgeExclusionThick > 0
                                ? Math.Max(1, (int)Math.Round(config.DefectEdgeExclusionThick * scale))
                                : 0;
                            int scaledEdgeSmall = config.DefectEdgeExclusionSmall > 0
                                ? Math.Max(1, (int)Math.Round(config.DefectEdgeExclusionSmall * scale))
                                : 0;
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
                                                out maxFineLineLengthMm);

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

                                // 普通面积通道仍沿用原来的轮廓屏蔽语义；细线通道在清零前已完成独立判定。
                                ApplyEdgeExclusion(edgeMask, difInner, difOuter);

                                List<Rect> innerRects = AnalyzeConnectedComponents(
                                    difInner, scaledAreaThreshInner, out int maxAreaInner, out int innerCount);
                                List<Rect> outerRects = AnalyzeConnectedComponents(
                                    difOuter, scaledAreaThreshOuter, out int maxAreaOuter, out int outerCount);

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
                                result.IsPass = maxAreaInner <= scaledAreaThreshInner &&
                                                maxAreaOuter <= scaledAreaThreshOuter &&
                                                fineLineCount == 0;

                                // 对外矩形统一还原到零件原始分辨率，GlobalRoi 的叠加由 PatchCropper 完成。
                                result.FineLineBreakRects = fineLineRectsOriginal;
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
                                        innerRects, outerRects, fineLineRects, result.IsPass, outputPath);
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
                using (Mat externalInput = alphaBinary.Clone())
                using (Mat alphaFilled = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1))
                {
                    Cv2.FindContours(externalInput, out Point[][] contoursExt, out _,
                        RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    Cv2.DrawContours(alphaFilled, contoursExt, -1, new Scalar(255), -1);

                    if (edgeThick > 0)
                    {
                        using (Mat filledInput = alphaFilled.Clone())
                        {
                            Cv2.FindContours(filledInput, out Point[][] contoursFilled, out _,
                                RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                            Cv2.DrawContours(edgeMask, contoursFilled, -1, new Scalar(255), edgeThick);
                        }
                    }
                }

                // Tree 模式保留全部内外轮廓，以较窄宽度屏蔽文字孔洞和细小设计边缘。
                if (edgeSmall > 0)
                {
                    using (Mat allInput = alphaBinary.Clone())
                    using (Mat smallMask = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1))
                    {
                        Cv2.FindContours(allInput, out Point[][] contoursAll, out _,
                            RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);
                        Cv2.DrawContours(smallMask, contoursAll, -1, new Scalar(255), edgeSmall);
                        Cv2.BitwiseOr(edgeMask, smallMask, edgeMask);
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

        /// <summary>普通面积检测继续使用原有的硬屏蔽语义。</summary>
        private static void ApplyEdgeExclusion(Mat edgeMask, Mat difInner, Mat difOuter)
        {
            if (edgeMask == null || edgeMask.Empty())
                return;

            difInner.SetTo(new Scalar(0), edgeMask);
            difOuter.SetTo(new Scalar(0), edgeMask);
        }

        /// <summary>
        /// 在独立细节尺度上检测细线中间断口，所有断口统一执行同一条证据链：
        /// 1. 建立模板/CIS 前景；2. 提取轮廓屏蔽区内的缺失候选；
        /// 3. 在候选 ROI 上提取骨架；4. 校验物理长度和线宽；
        /// 5. 确认笔画横截面被切断；6. 确认缺口前后仍有结构；
        /// 7. 排除可由同一微小位移解释的配准残差；8. 合并同一物理断口。
        /// 返回的矩形和掩膜均位于 analysisScale 对应的检测坐标系，最长长度使用毫米。
        /// </summary>
        private static List<Rect> DetectFineLineBreaksAtDetailScale(
            Mat templateGray,
            Mat capturedGray,
            int capturedBaseThreshold,
            double analysisScale,
            AppConfig config,
            Mat acceptedGapMask,
            out int acceptedGapCount,
            out double longestAcceptedGapMm)
        {
            // 以下三个比例属于内部证据门槛，不作为用户参数暴露：
            // 端点覆盖用于证明断口前后仍有线；横截面缺失用于证明不是局部暗斑；
            // 绝对缺失用于低对比光晕情况下的保守恢复。
            const double minimumEndpointCoverage = 0.40;
            const double minimumCrossSectionMissingRatio = 0.75;
            const double minimumAbsoluteMissingRatio = 0.90;

            acceptedGapCount = 0;
            longestAcceptedGapMm = 0;
            var acceptedGapRects = new List<Rect>();

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
            // 法向可以使用内轮廓容差吸收局部错位；沿线方向必须严格限制，
            // 否则搜索窗口会把断口后方的正常线段平移过来，掩盖约 0.5 mm 的真实缺口。
            int scaledInnerTolerance = Math.Max(
                1,
                (int)Math.Round(config.DefectToleranceInner * analysisScale));
            int maximumTangentialShift = Math.Max(
                1,
                Math.Min(
                    Math.Max(1, scaledInnerTolerance / 2),
                    (int)Math.Round(0.17 * pixelsPerMm)));
            int normalAlignmentSearchRadius = Math.Max(
                maximumTangentialShift,
                Math.Min(
                    scaledInnerTolerance,
                    (int)Math.Round(0.40 * pixelsPerMm)));
            int endpointSearchRadius = Math.Max(
                normalAlignmentSearchRadius + 1,
                Math.Max(2, scaledInnerTolerance * 2));
            // 端点只用于确认断口两侧仍有真实线段，可使用比缺口本体更宽松的搜索半径。
            int endpointAnchorLength = Math.Max(minimumGapPixels * 2, endpointSearchRadius * 2);
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
                    ScalePositiveLength(config.DefectEdgeExclusionThick, analysisScale),
                    ScalePositiveLength(config.DefectEdgeExclusionSmall, analysisScale)))
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
                                    acceptedGapRects.Add(ExpandRect(
                                        gapBoundsGlobal,
                                        normalAlignmentSearchRadius + 1,
                                        templateBinary.Size()));
                                    longestAcceptedGapMm = Math.Max(
                                        longestAcceptedGapMm,
                                        gapLengthPixels / pixelsPerMm);
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
                acceptedGapRects = MergeNearbyRects(acceptedGapRects, endpointSearchRadius);
                acceptedGapCount = acceptedGapRects.Count;

            }

            return acceptedGapRects;
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

        /// <summary>缩放正长度参数；配置为 0 时保持禁用语义。</summary>
        private static int ScalePositiveLength(int value, double scale)
        {
            return value <= 0 ? 0 : Math.Max(1, (int)Math.Round(value * scale));
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
        /// SIFT 特征匹配 + RANSAC + 仿射对齐。
        /// 成功时输出新建且由调用方负责释放的缩放图/可选原图；失败时输出 null，
        /// 调用方继续使用未局部变换的 cisScaled，从而让配准失败与缺陷判定解耦。
        /// </summary>
        private static bool TrySiftAlign(
            Mat alphaFeature,
            Mat cisFeature,
            Mat alphaScaled,
            Mat cisScaled,
            Mat cisImgOrig,
            double scale,
            bool needOriginalWarp,
            PatchSiftWorker worker,
            string partId,
            out Mat cisAligned,
            out Mat cisAlignedOrig)
        {
            cisAligned = null;
            cisAlignedOrig = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                PatchSiftTemplateFeatures template = worker.TemplateCache.GetOrCreate(alphaFeature, worker.Sift);
                if (template.KeyPoints.Length == 0 || template.Descriptors == null || template.Descriptors.Empty())
                {
                    LogAlignmentFailure(partId, "模板特征为空", stopwatch.ElapsedMilliseconds);
                    return false;
                }

                using (var cisDescriptors = new Mat())
                {
                    worker.Sift.DetectAndCompute(cisFeature, null, out KeyPoint[] cisKeyPoints, cisDescriptors);
                    if (cisKeyPoints.Length == 0 || cisDescriptors.Empty())
                    {
                        LogAlignmentFailure(partId, "CIS 特征为空", stopwatch.ElapsedMilliseconds);
                        return false;
                    }

                    // 匹配方向为 TIFF 模板→CIS；后续估计的矩阵方向则为 CIS→TIFF。
                    DMatch[][] knnMatches = worker.Matcher.KnnMatch(template.Descriptors, cisDescriptors, 2);
                    var goodMatches = new List<DMatch>(knnMatches.Length);
                    const float ratioThreshold = 0.6f;
                    foreach (DMatch[] matches in knnMatches)
                    {
                        if (matches.Length >= 2 && matches[0].Distance < ratioThreshold * matches[1].Distance)
                            goodMatches.Add(matches[0]);
                    }

                    const int minimumMatches = 4;
                    if (goodMatches.Count < minimumMatches)
                    {
                        LogAlignmentFailure(partId, $"有效匹配不足({goodMatches.Count}/{minimumMatches})", stopwatch.ElapsedMilliseconds);
                        return false;
                    }

                    Point2f[] templatePoints = goodMatches
                        .Select(match => template.KeyPoints[match.QueryIdx].Pt)
                        .ToArray();
                    Point2f[] cisPoints = goodMatches
                        .Select(match => cisKeyPoints[match.TrainIdx].Pt)
                        .ToArray();

                    // 保留原始两阶段技术路径：先用基础矩阵 RANSAC 过滤误匹配，
                    // 再使用过滤后的点估计完整仿射矩阵。
                    using (InputArray templateInput = InputArray.Create(
                        templatePoints.Select(point => new Point2d(point.X, point.Y)).ToArray()))
                    using (InputArray cisInput = InputArray.Create(
                        cisPoints.Select(point => new Point2d(point.X, point.Y)).ToArray()))
                    using (var fundamentalMask = new Mat())
                    using (Mat fundamental = Cv2.FindFundamentalMat(
                        templateInput,
                        cisInput,
                        FundamentalMatMethods.Ransac,
                        3.0,
                        0.99,
                        fundamentalMask))
                    {
                        if (fundamentalMask.Empty())
                        {
                            LogAlignmentFailure(partId, "Fundamental Matrix RANSAC 未得到内点", stopwatch.ElapsedMilliseconds);
                            return false;
                        }

                        fundamentalMask.GetArray(out byte[] maskValues);
                        var templateInliers = new List<Point2f>(goodMatches.Count);
                        var cisInliers = new List<Point2f>(goodMatches.Count);
                        int maskLength = Math.Min(maskValues.Length, goodMatches.Count);
                        for (int i = 0; i < maskLength; i++)
                        {
                            if (maskValues[i] != 0)
                            {
                                templateInliers.Add(templatePoints[i]);
                                cisInliers.Add(cisPoints[i]);
                            }
                        }

                        if (templateInliers.Count < minimumMatches || cisInliers.Count < minimumMatches)
                        {
                            LogAlignmentFailure(
                                partId,
                                $"Fundamental Matrix 内点不足({templateInliers.Count}/{minimumMatches})",
                                stopwatch.ElapsedMilliseconds);
                            return false;
                        }

                        using (InputArray affineSource = InputArray.Create(cisInliers.ToArray()))
                        using (InputArray affineTarget = InputArray.Create(templateInliers.ToArray()))
                        using (Mat transform = Cv2.EstimateAffine2D(affineSource, affineTarget))
                        {
                            if (transform == null || transform.Empty())
                            {
                                LogAlignmentFailure(partId, "EstimateAffine2D 未得到矩阵", stopwatch.ElapsedMilliseconds);
                                return false;
                            }

                            // 与原始版本相同：只检查两个对角元素及 X/Y 平移。
                            double sx = transform.At<double>(0, 0);
                            double sy = transform.At<double>(1, 1);
                            double dx = transform.At<double>(0, 2);
                            double dy = transform.At<double>(1, 2);
                            bool transformAccepted =
                                sx > 0.9 && sx < 1.1 &&
                                sy > 0.9 && sy < 1.1 &&
                                dx > -10 && dx < 10 &&
                                dy > -10 && dy < 10;
                            if (!transformAccepted)
                            {
                                LogAlignmentFailure(
                                    partId,
                                    $"仿射矩阵超出原始约束: sx={sx:F4}, sy={sy:F4}, dx={dx:F3}, dy={dy:F3}",
                                    stopwatch.ElapsedMilliseconds);
                                return false;
                            }

                            // 只有矩阵通过原有尺度/平移约束后才创建输出，避免失败路径遗留半成品 Mat。
                            Mat scaledOutput = new Mat();
                            Mat originalOutput = null;
                            try
                            {
                                Cv2.WarpAffine(cisScaled, scaledOutput, transform, alphaScaled.Size(), InterpolationFlags.Cubic);

                                // 原分辨率对齐图仅在细线复核或裁图保存需要时生成；普通面积通道不承担这次大图 Warp。
                                if (needOriginalWarp)
                                {
                                    originalOutput = new Mat();
                                    using (Mat originalTransform = transform.Clone())
                                    {
                                        originalTransform.Set(0, 2, transform.At<double>(0, 2) / scale);
                                        originalTransform.Set(1, 2, transform.At<double>(1, 2) / scale);
                                        Cv2.WarpAffine(
                                            cisImgOrig,
                                            originalOutput,
                                            originalTransform,
                                            cisImgOrig.Size(),
                                            InterpolationFlags.Cubic);
                                    }
                                }

                                cisAligned = scaledOutput;
                                cisAlignedOrig = originalOutput;
                                scaledOutput = null;
                                originalOutput = null;

                                stopwatch.Stop();
                                Console.WriteLine(
                                    $"[LocalAlign] {FormatPartId(partId)} Applied(original): " +
                                    $"kp={template.KeyPoints.Length}/{cisKeyPoints.Length}, " +
                                    $"matches={goodMatches.Count}, fundamentalInliers={templateInliers.Count}, " +
                                    $"sx={sx:F4}, sy={sy:F4}, dx={dx:F3}, dy={dy:F3}, " +
                                    $"time={stopwatch.ElapsedMilliseconds}ms");
                                return true;
                            }
                            finally
                            {
                                scaledOutput?.Dispose();
                                originalOutput?.Dispose();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogAlignmentFailure(partId, $"异常: {ex.Message}", stopwatch.ElapsedMilliseconds);
                cisAligned?.Dispose();
                cisAlignedOrig?.Dispose();
                cisAligned = null;
                cisAlignedOrig = null;
                return false;
            }
        }

        private static void LogAlignmentFailure(string partId, string reason, long elapsedMilliseconds)
        {
            Console.WriteLine($"[LocalAlign] {FormatPartId(partId)} skipped: {reason}, time={elapsedMilliseconds}ms");
        }

        private static string FormatPartId(string partId)
        {
            return string.IsNullOrWhiteSpace(partId) ? "<unknown>" : partId;
        }

        /// <summary>
        /// 连通域分析：统计超过面积阈值的缺陷数量与最大面积，返回缺陷外接矩形列表。
        /// </summary>
        private static List<Rect> AnalyzeConnectedComponents(Mat binaryImg, int areaThresh, out int maxArea, out int defectCount)
        {
            maxArea = 0;
            defectCount = 0;
            var rects = new List<Rect>();

            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centroids = new Mat())
            {
                int nLabels = Cv2.ConnectedComponentsWithStats(binaryImg, labels, stats, centroids);
                for (int i = 1; i < nLabels; i++)
                {
                    int area = stats.At<int>(i, 4); // CC_STAT_AREA
                    if (area > maxArea) maxArea = area;
                    if (area > areaThresh)
                    {
                        defectCount++;
                        rects.Add(new Rect(
                            stats.At<int>(i, 0), stats.At<int>(i, 1),
                            stats.At<int>(i, 2), stats.At<int>(i, 3)));
                    }
                }
            }
            return rects;
        }

        /// <summary>
        /// 把用户配置的物理面积阈值换算到当前检测尺度的像素面积。
        /// LayoutDpi 定义 TIFF/对齐目标空间的像素密度，线性缩放后面积再乘 scale²。
        /// </summary>
        private static int ConvertAreaMm2ToScaledPixels(
            double areaMm2,
            double layoutDpi,
            double linearScale)
        {
            double pixelsPerMm = GetValidPixelsPerMm(layoutDpi);
            double validScale = linearScale > 0 && !double.IsNaN(linearScale) && !double.IsInfinity(linearScale)
                ? linearScale
                : 1.0;
            double scaledPixelArea = Math.Max(0, areaMm2) *
                                     pixelsPerMm * pixelsPerMm *
                                     validScale * validScale;
            if (scaledPixelArea >= int.MaxValue)
                return int.MaxValue;
            return Math.Max(
                1,
                (int)Math.Round(scaledPixelArea, MidpointRounding.AwayFromZero));
        }

        /// <summary>把检测尺度连通域的像素面积换算为 TIFF 目标空间中的物理面积 mm²。</summary>
        private static double ConvertScaledAreaToMm2(
            int scaledArea,
            double layoutDpi,
            double linearScale)
        {
            if (scaledArea <= 0)
                return 0;

            double pixelsPerMm = GetValidPixelsPerMm(layoutDpi);
            double validScale = linearScale > 0 && !double.IsNaN(linearScale) && !double.IsInfinity(linearScale)
                ? linearScale
                : 1.0;
            return scaledArea /
                   (pixelsPerMm * pixelsPerMm * validScale * validScale);
        }

        private static double GetValidPixelsPerMm(double layoutDpi)
        {
            double effectiveDpi = layoutDpi > 0 && !double.IsNaN(layoutDpi) && !double.IsInfinity(layoutDpi)
                ? layoutDpi
                : 300.0;
            return effectiveDpi / 25.4;
        }

        /// <summary>
        /// 生成并保存可视化结果图。
        /// 左: 原图(二值化) | 中: 扫描图(二值化+标注缺陷) | 右: 差分图
        /// </summary>
        private static void SaveVisualization(Mat orgBin, Mat comBin, Mat difInner, Mat difOuter,
            List<Rect> innerRects, List<Rect> outerRects, List<Rect> fineLineRects,
            bool isPass, string outputPath)
        {
            try
            {
                using (var orgRgb = new Mat())
                using (var comRgb = new Mat())
                using (var difRgb = new Mat())
                using (var difMerged = new Mat())
                using (var vis = new Mat())
                {
                    Cv2.CvtColor(orgBin, orgRgb, ColorConversionCodes.GRAY2BGR);
                    Cv2.CvtColor(comBin, comRgb, ColorConversionCodes.GRAY2BGR);
                    Cv2.Add(difInner, difOuter, difMerged);
                    Cv2.CvtColor(difMerged, difRgb, ColorConversionCodes.GRAY2BGR);

                    foreach (Rect rect in innerRects)
                        Cv2.Rectangle(comRgb, rect, new Scalar(0, 165, 255), 2);
                    foreach (Rect rect in outerRects)
                        Cv2.Rectangle(comRgb, rect, new Scalar(0, 0, 255), 2);
                    foreach (Rect rect in fineLineRects)
                        Cv2.Rectangle(comRgb, rect, new Scalar(255, 0, 255), 2);

                    double fontScale = Math.Max(0.5, orgBin.Width / 300.0);
                    int thickness = Math.Max(1, (int)(fontScale * 2));
                    Scalar color = isPass ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                    Cv2.PutText(orgRgb, "Org(Bin)", new Point(10, orgRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);
                    Cv2.PutText(comRgb, isPass ? "Pass" : "Wrong", new Point(10, comRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, color, thickness);
                    Cv2.PutText(difRgb, "Diff", new Point(10, difRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);
                    Cv2.Rectangle(comRgb, new Rect(0, 0, comRgb.Width, comRgb.Height), color, 2);

                    Cv2.HConcat(new[] { orgRgb, comRgb, difRgb }, vis);
                    Cv2.ImWrite(outputPath, vis);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PatchDefectDetector] 保存缺陷可视化失败: {ex.Message}");
            }
        }
    }
}
