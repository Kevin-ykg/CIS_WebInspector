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
    /// </summary>
    public class PatchDefectResult
    {
        public string PartId { get; set; }
        public int MaxAreaInner { get; set; }
        public int MaxAreaOuter { get; set; }
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
        /// <summary>
        /// 对单个零件执行缺陷检测。
        /// 流程参照 align_diff.py L544-600:
        ///   1) Alpha → threshold(60) → binary_tiff (设计图案)
        ///   2) CIS → [可选SIFT对齐] → threshold(cisBaseThresh) → binary_jpg (实拍图案)
        ///   3) 形态学容差差分 (内部/外部分离)
        ///   4) 边缘屏蔽 (消除轮廓错位噪声)
        ///   5) 连通域判定
        /// </summary>
        public static PatchDefectResult Detect(Mat alphaImg, Mat cisImg, int cisBaseThresh, AppConfig config, string outputPath = null, string cisOutputPath = null)
        {
            PatchSiftTemplateCache cache = null;
            PatchSiftWorker worker = null;
            try
            {
                if (config.EnableSiftLocalAlign)
                {
                    cache = new PatchSiftTemplateCache();
                    worker = new PatchSiftWorker(cache);
                }
                return DetectCore(alphaImg, cisImg, cisBaseThresh, config, outputPath, cisOutputPath, worker, null);
            }
            finally
            {
                worker?.Dispose();
                cache?.Dispose();
            }
        }

        /// <summary>批处理入口：复用调用方提供的 worker 和模板缓存，避免每个零件重建 SIFT/Matcher。</summary>
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
                                var prms = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 95) };
                                Cv2.ImWrite(cisOutputPath, cisToSave, prms);
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
                            int scaledAreaThreshInner = Math.Max(1, (int)Math.Round(config.DefectAreaThreshInner * scale * scale));
                            int scaledAreaThreshOuter = Math.Max(1, (int)Math.Round(config.DefectAreaThreshOuter * scale * scale));

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
                                int maxFineLineArea = 0;
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
                                                out int maxFineLineAreaAnalysis,
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
                                            maxFineLineArea = Math.Max(
                                                0,
                                                (int)Math.Round(
                                                    maxFineLineAreaAnalysis *
                                                    analysisToDetectX * analysisToDetectY));

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
                                result.MaxAreaInner = Math.Max(maxAreaInner, maxFineLineArea);
                                result.MaxAreaOuter = maxAreaOuter;
                                result.InnerDefectCount = innerCount + fineLineCount;
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
                                    Math.Max(1, (int)(r.Height / scale))))
                                    .Concat(fineLineRectsOriginal)
                                    .ToList();
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
        /// 在独立细节尺度上检测细线闭合断口。
        /// 候选两侧必须仍有可靠线段，并且 CIS 在模板线附近的窄走廊内不能连接两端。
        /// 因此轻微平移、线宽变化或阈值波动形成的成对轮廓不会被当成断裂。
        /// </summary>
        private static List<Rect> DetectFineLineBreaksAtDetailScale(
            Mat alphaGray,
            Mat cisGray,
            int cisBaseThresh,
            double analysisScale,
            AppConfig config,
            Mat acceptedFineLineMask,
            out int maxAcceptedArea,
            out int acceptedCount,
            out double maxAcceptedLengthMm)
        {
            const double minimumEndpointCoverage = 0.40;

            maxAcceptedArea = 0;
            acceptedCount = 0;
            maxAcceptedLengthMm = 0;
            var acceptedRects = new List<Rect>();

            double pixelsPerMm = config.LayoutDpi > 0
                ? config.LayoutDpi / 25.4 * analysisScale
                : 0;
            if (pixelsPerMm <= 0 || config.FineLineMinBreakLengthMm <= 0 ||
                config.FineLineMaxWidthMm <= 0 || alphaGray.Empty() || cisGray.Empty())
            {
                return acceptedRects;
            }

            int minimumLengthPixels = Math.Max(
                2,
                (int)Math.Ceiling(config.FineLineMinBreakLengthMm * pixelsPerMm));
            double maximumThinLineHalfWidthPixels =
                Math.Max(1.0, config.FineLineMaxWidthMm * pixelsPerMm * 0.5 + 1.25);

            // 横向只允许约 0.17 mm 的偏差；更大的错位交给下方“是否仍有连续桥接线”判断。
            int scaledToleranceInner = Math.Max(
                1,
                (int)Math.Round(config.DefectToleranceInner * analysisScale));
            int transverseAllowance = Math.Max(
                1,
                Math.Min(
                    Math.Max(1, scaledToleranceInner / 2),
                    (int)Math.Round(0.17 * pixelsPerMm)));
            int corridorRadius = Math.Max(
                transverseAllowance + 1,
                Math.Max(2, scaledToleranceInner * 2));
            // 端点只用于确认断口两侧仍有真实线段，可使用完整轮廓容差；
            // 缺口本体仍由未膨胀证据计算，并由后续平移/桥接检查排除错位。
            int endpointAllowance = corridorRadius;
            int anchorReach = Math.Max(minimumLengthPixels * 2, corridorRadius * 2);
            // 正常前景由绝对亮度与局部对比度共同建立，避免把偏灰但连续的线条切断。
            // 深色核心只作为亚毫米候选的辅助证据，不再限制较长的自然灰度断线。
            int relaxedThreshold = Math.Max(
                8,
                Math.Min(cisBaseThresh - 1, (int)Math.Round(cisBaseThresh * 0.82)));
            int darkCoreThreshold = Math.Max(
                5,
                Math.Min(relaxedThreshold - 1, (int)Math.Round(cisBaseThresh * 0.20)));
            int maximumLineWidthPixels = Math.Max(
                1,
                (int)Math.Ceiling(config.FineLineMaxWidthMm * pixelsPerMm));

            using (var alphaBinary = new Mat())
            using (var cisSized = new Mat())
            using (var cisRelaxed = new Mat())
            using (var edgeMask = new Mat())
            using (var cisNearby = new Mat())
            using (var missingEdge = new Mat())
            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centroids = new Mat())
            using (Mat nearbyKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(endpointAllowance * 2 + 1, endpointAllowance * 2 + 1)))
            {
                Cv2.Threshold(
                    alphaGray,
                    alphaBinary,
                    config.DefectAlphaBinaryThresh,
                    255,
                    ThresholdTypes.Binary);

                if (cisGray.Size() == alphaGray.Size())
                    cisGray.CopyTo(cisSized);
                else
                    Cv2.Resize(cisGray, cisSized, alphaGray.Size(), 0, 0, InterpolationFlags.Linear);

                BuildFineLineForegroundEvidence(
                    cisSized,
                    cisRelaxed,
                    relaxedThreshold,
                    maximumLineWidthPixels * 2 + 1);

                using (Mat builtEdgeMask = BuildEdgeExclusionMask(
                    alphaBinary,
                    ScalePositiveLength(config.DefectEdgeExclusionThick, analysisScale),
                    ScalePositiveLength(config.DefectEdgeExclusionSmall, analysisScale)))
                {
                    builtEdgeMask.CopyTo(edgeMask);
                }

                Cv2.Dilate(cisRelaxed, cisNearby, nearbyKernel);
                using (var notForeground = new Mat())
                using (var rawMissing = new Mat())
                {
                    // “缺口”定义：模板要求有前景、CIS 宽松前景中却不存在，并且位于原轮廓屏蔽区。
                    // 缺口长度由未膨胀证据计算；cisNearby 仅用于确认断口前后仍有结构。
                    Cv2.BitwiseNot(cisRelaxed, notForeground);
                    Cv2.BitwiseAnd(alphaBinary, notForeground, rawMissing);
                    Cv2.BitwiseAnd(rawMissing, edgeMask, missingEdge);
                }
                if (Cv2.CountNonZero(missingEdge) == 0)
                    return acceptedRects;

                int labelCount = Cv2.ConnectedComponentsWithStats(
                    missingEdge, labels, stats, centroids);
                for (int label = 1; label < labelCount; label++)
                {
                    Rect componentRect = new Rect(
                        stats.At<int>(label, 0),
                        stats.At<int>(label, 1),
                        stats.At<int>(label, 2),
                        stats.At<int>(label, 3));
                    double visibleMissingLength = Math.Sqrt(
                        componentRect.Width * componentRect.Width +
                        componentRect.Height * componentRect.Height);
                    double estimatedBreakLength = visibleMissingLength;
                    if (estimatedBreakLength < minimumLengthPixels)
                        continue;

                    int borderMargin = corridorRadius + 1;
                    if (componentRect.X <= borderMargin || componentRect.Y <= borderMargin ||
                        componentRect.Right >= alphaBinary.Width - borderMargin ||
                        componentRect.Bottom >= alphaBinary.Height - borderMargin)
                        continue;

                    Rect evidenceRect = ExpandRect(
                        componentRect,
                        anchorReach + corridorRadius,
                        alphaBinary.Size());

                    using (Mat labelsRoi = new Mat(labels, evidenceRect))
                    using (Mat alphaRoi = new Mat(alphaBinary, evidenceRect))
                    using (Mat cisGrayRoi = new Mat(cisSized, evidenceRect))
                    using (Mat cisRelaxedRoi = new Mat(cisRelaxed, evidenceRect))
                    using (Mat cisNearbyRoi = new Mat(cisNearby, evidenceRect))
                    using (var componentMask = new Mat())
                    using (var templateSkeleton = new Mat())
                    using (var gapSkeleton = new Mat())
                    using (var distanceInside = new Mat())
                    {
                        Cv2.InRange(
                            labelsRoi,
                            new Scalar(label),
                            new Scalar(label),
                            componentMask);
                        // 只在候选附近的小 ROI 内细化，避免对整张 2K×3K 零件图执行高代价骨架化。
                        CvXImgProc.Thinning(alphaRoi, templateSkeleton, ThinningTypes.GUOHALL);
                        Cv2.BitwiseAnd(templateSkeleton, componentMask, gapSkeleton);
                        if (Cv2.CountNonZero(gapSkeleton) == 0)
                            continue;

                        Cv2.DistanceTransform(
                            alphaRoi,
                            distanceInside,
                            DistanceTypes.L2,
                            DistanceTransformMasks.Mask3);
                        using (var gapLabels = new Mat())
                        using (var gapStats = new Mat())
                        using (var gapCentroids = new Mat())
                        {
                            int gapCount = Cv2.ConnectedComponentsWithStats(
                                gapSkeleton, gapLabels, gapStats, gapCentroids);
                            for (int gapLabel = 1; gapLabel < gapCount; gapLabel++)
                            {
                                Rect gapRectLocal = new Rect(
                                    gapStats.At<int>(gapLabel, 0),
                                    gapStats.At<int>(gapLabel, 1),
                                    gapStats.At<int>(gapLabel, 2),
                                    gapStats.At<int>(gapLabel, 3));
                                double gapVisibleLength = Math.Sqrt(
                                    gapRectLocal.Width * gapRectLocal.Width +
                                    gapRectLocal.Height * gapRectLocal.Height);
                                double gapEstimatedLength = gapVisibleLength;
                                if (gapEstimatedLength < minimumLengthPixels)
                                    continue;

                                using (var gapComponent = new Mat())
                                {
                                    Cv2.InRange(
                                        gapLabels,
                                        new Scalar(gapLabel),
                                        new Scalar(gapLabel),
                                        gapComponent);
                                    if (CalculateMedianDistance(distanceInside, gapComponent) >
                                        maximumThinLineHalfWidthPixels)
                                        continue;

                                    double gapLengthMm = gapEstimatedLength / pixelsPerMm;
                                    if (gapLengthMm < 1.0 &&
                                        CalculateDarkCoreRatio(
                                            cisGrayRoi,
                                            gapComponent,
                                            darkCoreThreshold) < 0.20)
                                    {
                                        continue;
                                    }
                                    Mat firstAnchor = null;
                                    Mat secondAnchor = null;
                                    try
                                    {
                                        // “前后均有结构”指断口外环中存在位于缺口相反方向的两段模板骨架，
                                        // 且两段骨架在 CIS 附近均有足够覆盖；单端缺失或边界截断不会通过。
                                        if (!TryBuildEndpointAnchors(
                                            templateSkeleton,
                                            gapComponent,
                                            transverseAllowance + 1,
                                            anchorReach,
                                            out firstAnchor,
                                            out secondAnchor))
                                        {
                                            continue;
                                        }

                                        double firstEndpointCoverage = CalculateCoverage(firstAnchor, cisNearbyRoi);
                                        double secondEndpointCoverage = CalculateCoverage(secondAnchor, cisNearbyRoi);
                                        if (firstEndpointCoverage < minimumEndpointCoverage ||
                                            secondEndpointCoverage < minimumEndpointCoverage)
                                        {
                                            continue;
                                        }

                                        // 若同一平移可同时解释缺口和两端结构，优先判为局部错位而非真实断裂。
                                        if (HasConsistentLocalTranslation(
                                            cisRelaxedRoi,
                                            gapComponent,
                                            firstAnchor,
                                            secondAnchor,
                                            Math.Max(
                                                1,
                                                Math.Max(corridorRadius, (int)Math.Round(1.5 * pixelsPerMm))),
                                            transverseAllowance))
                                        {
                                            continue;
                                        }

                                        // 即使差分中有缺口，只要 CIS 在模板走廊内仍能连通两端，也不是结构断裂。
                                        if (HasContinuousForegroundBridge(
                                            templateSkeleton,
                                            gapComponent,
                                            cisRelaxedRoi,
                                            firstAnchor,
                                            secondAnchor,
                                            corridorRadius,
                                            anchorReach))
                                        {
                                            continue;
                                        }

                                        using (Mat acceptedRoi = new Mat(acceptedFineLineMask, evidenceRect))
                                            Cv2.BitwiseOr(acceptedRoi, gapComponent, acceptedRoi);

                                        Rect gapRectGlobal = new Rect(
                                            evidenceRect.X + gapRectLocal.X,
                                            evidenceRect.Y + gapRectLocal.Y,
                                            gapRectLocal.Width,
                                            gapRectLocal.Height);
                                        Rect acceptedRect = ExpandRect(
                                            gapRectGlobal,
                                            transverseAllowance + 1,
                                            alphaBinary.Size());
                                        acceptedRects.Add(acceptedRect);
                                        maxAcceptedLengthMm = Math.Max(
                                            maxAcceptedLengthMm,
                                            gapLengthMm);
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
                }

                List<Rect> longBreakRects = DetectLongThinBreaks(
                    alphaBinary,
                    cisSized,
                    relaxedThreshold,
                    maximumLineWidthPixels,
                    maximumThinLineHalfWidthPixels,
                    pixelsPerMm,
                    Math.Max(
                        config.FineLineMinBreakLengthMm,
                        config.FineLineMaxWidthMm * 2.0),
                    transverseAllowance + 1,
                    acceptedFineLineMask,
                    out double maxLongBreakLengthMm);
                acceptedRects.AddRange(longBreakRects);
                maxAcceptedLengthMm = Math.Max(
                    maxAcceptedLengthMm,
                    maxLongBreakLengthMm);

                // 短断口分支和长细分支可能命中同一物理位置，合并后再计数。
                acceptedRects = MergeNearbyRects(acceptedRects, corridorRadius);
                acceptedCount = acceptedRects.Count;
                foreach (Rect rect in acceptedRects)
                {
                    using (Mat defectRoi = new Mat(acceptedFineLineMask, rect))
                        maxAcceptedArea = Math.Max(maxAcceptedArea, Cv2.CountNonZero(defectRoi));
                }

            }

            return acceptedRects;
        }

        /// <summary>
        /// 复核长度明显大于线宽的连续缺口。该分支只使用长度/线宽关系，
        /// 不增加可调阈值，用于覆盖偏灰但已经完整断开的细线。
        /// </summary>
        private static List<Rect> DetectLongThinBreaks(
            Mat alphaBinary,
            Mat cisGray,
            int foregroundThreshold,
            int maximumLineWidthPixels,
            double maximumThinLineHalfWidthPixels,
            double pixelsPerMm,
            double minimumLengthMm,
            int rectMargin,
            Mat acceptedMask,
            out double maxLengthMm)
        {
            maxLengthMm = 0;
            var result = new List<Rect>();
            int minimumLengthPixels = Math.Max(
                2,
                (int)Math.Ceiling(minimumLengthMm * pixelsPerMm));
            int translationSearchRadius = Math.Max(
                1,
                (int)Math.Round(1.5 * pixelsPerMm));

            using (var foreground = new Mat())
            using (var nearbyForeground = new Mat())
            using (var notForeground = new Mat())
            using (var missingRegion = new Mat())
            using (var distanceInside = new Mat())
            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centroids = new Mat())
            using (Mat nearbyKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse, new Size(3, 3)))
            {
                BuildFineLineForegroundEvidence(
                    cisGray,
                    foreground,
                    foregroundThreshold,
                    maximumLineWidthPixels + 1);
                // 仅容忍一个像素的重采样偏差，不能沿线方向填平真实长断口。
                Cv2.Dilate(foreground, nearbyForeground, nearbyKernel);
                Cv2.BitwiseNot(nearbyForeground, notForeground);
                Cv2.BitwiseAnd(alphaBinary, notForeground, missingRegion);
                Cv2.DistanceTransform(
                    alphaBinary,
                    distanceInside,
                    DistanceTypes.L2,
                    DistanceTransformMasks.Mask3);

                int componentCount = Cv2.ConnectedComponentsWithStats(
                    missingRegion,
                    labels,
                    stats,
                    centroids);
                for (int label = 1; label < componentCount; label++)
                {
                    Rect candidateRect = new Rect(
                        stats.At<int>(label, 0),
                        stats.At<int>(label, 1),
                        stats.At<int>(label, 2),
                        stats.At<int>(label, 3));
                    double candidateLength = Math.Sqrt(
                        candidateRect.Width * candidateRect.Width +
                        candidateRect.Height * candidateRect.Height);
                    if (candidateLength < minimumLengthPixels ||
                        candidateRect.X <= rectMargin || candidateRect.Y <= rectMargin ||
                        candidateRect.Right >= alphaBinary.Width - rectMargin ||
                        candidateRect.Bottom >= alphaBinary.Height - rectMargin)
                    {
                        continue;
                    }

                    Rect evidenceRect = ExpandRect(
                        candidateRect,
                        translationSearchRadius,
                        alphaBinary.Size());
                    using (Mat labelsRoi = new Mat(labels, evidenceRect))
                    using (Mat alphaRoi = new Mat(alphaBinary, evidenceRect))
                    using (Mat distanceRoi = new Mat(distanceInside, evidenceRect))
                    using (Mat foregroundRoi = new Mat(foreground, evidenceRect))
                    using (var candidateMask = new Mat())
                    using (var localSkeleton = new Mat())
                    using (var gapSkeleton = new Mat())
                    using (var gapLabels = new Mat())
                    using (var gapStats = new Mat())
                    using (var gapCentroids = new Mat())
                    {
                        Cv2.InRange(
                            labelsRoi,
                            new Scalar(label),
                            new Scalar(label),
                            candidateMask);
                        if (CalculateMedianDistance(distanceRoi, candidateMask) >
                            maximumThinLineHalfWidthPixels)
                        {
                            continue;
                        }

                        CvXImgProc.Thinning(
                            alphaRoi,
                            localSkeleton,
                            ThinningTypes.GUOHALL);
                        Cv2.BitwiseAnd(localSkeleton, candidateMask, gapSkeleton);
                        int gapCount = Cv2.ConnectedComponentsWithStats(
                            gapSkeleton,
                            gapLabels,
                            gapStats,
                            gapCentroids);
                        for (int gapLabel = 1; gapLabel < gapCount; gapLabel++)
                        {
                            Rect gapRectLocal = new Rect(
                                gapStats.At<int>(gapLabel, 0),
                                gapStats.At<int>(gapLabel, 1),
                                gapStats.At<int>(gapLabel, 2),
                                gapStats.At<int>(gapLabel, 3));
                            double lengthPixels = Math.Sqrt(
                                gapRectLocal.Width * gapRectLocal.Width +
                                gapRectLocal.Height * gapRectLocal.Height);
                            if (lengthPixels < minimumLengthPixels)
                                continue;

                            using (var gapComponent = new Mat())
                            {
                                Cv2.InRange(
                                    gapLabels,
                                    new Scalar(gapLabel),
                                    new Scalar(gapLabel),
                                    gapComponent);
                                if (CalculateMedianDistance(distanceRoi, gapComponent) >
                                    maximumThinLineHalfWidthPixels)
                                {
                                    continue;
                                }

                                if (HasTranslatedGap(
                                    foregroundRoi,
                                    gapComponent,
                                    translationSearchRadius))
                                {
                                    continue;
                                }

                                using (Mat acceptedRoi = new Mat(acceptedMask, evidenceRect))
                                    Cv2.BitwiseOr(acceptedRoi, gapComponent, acceptedRoi);
                            }

                            Rect gapRectGlobal = new Rect(
                                evidenceRect.X + gapRectLocal.X,
                                evidenceRect.Y + gapRectLocal.Y,
                                gapRectLocal.Width,
                                gapRectLocal.Height);
                            result.Add(ExpandRect(
                                gapRectGlobal,
                                rectMargin,
                                alphaBinary.Size()));
                            maxLengthMm = Math.Max(
                                maxLengthMm,
                                lengthPixels / pixelsPerMm);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>搜索局部平移后缺口是否能被 CIS 前景覆盖；能覆盖说明更像配准偏移而非真实长断口。</summary>
        private static bool HasTranslatedGap(
            Mat foreground,
            Mat gap,
            int searchRadius)
        {
            const double minimumCoverage = 0.80;
            List<Point> gapPoints = CollectMaskPoints(gap);
            if (gapPoints.Count == 0)
                return false;

            foreground.GetArray(out byte[] foregroundValues);
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    if (CalculateShiftedCoverage(
                        gapPoints,
                        foregroundValues,
                        foreground.Width,
                        foreground.Height,
                        dx,
                        dy) >= minimumCoverage)
                    {
                        return true;
                    }
                }
            }
            return false;
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

            int localContrastThreshold = Math.Max(
                6,
                Math.Min(20, (int)Math.Round(absoluteThreshold * 0.08)));
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

        /// <summary>统计候选中深暗像素比例；只作为很短断口的附加证据，避免灰度波动误报。</summary>
        private static double CalculateDarkCoreRatio(Mat gray, Mat mask, int threshold)
        {
            gray.GetArray(out byte[] grayValues);
            mask.GetArray(out byte[] maskValues);
            int selected = 0;
            int dark = 0;
            int length = Math.Min(grayValues.Length, maskValues.Length);
            for (int index = 0; index < length; index++)
            {
                if (maskValues[index] == 0)
                    continue;

                selected++;
                if (grayValues[index] <= threshold)
                    dark++;
            }

            return selected == 0 ? 0 : dark / (double)selected;
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
        private static bool HasConsistentLocalTranslation(
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

        /// <summary>
        /// 限定在模板骨架附近的窄走廊内检查 CIS 连通域；同一连通域触达两端即说明线仍连续。
        /// </summary>
        private static bool HasContinuousForegroundBridge(
            Mat skeleton,
            Mat componentMask,
            Mat cisForeground,
            Mat firstAnchor,
            Mat secondAnchor,
            int corridorRadius,
            int anchorReach)
        {
            int supportRadius = corridorRadius + Math.Max(2, anchorReach);
            using (Mat supportKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(supportRadius * 2 + 1, supportRadius * 2 + 1)))
            using (Mat corridorKernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(corridorRadius * 2 + 1, corridorRadius * 2 + 1)))
            using (Mat closeKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect, new Size(3, 3)))
            using (var support = new Mat())
            using (var supportedSkeleton = new Mat())
            using (var corridor = new Mat())
            using (var foregroundPath = new Mat())
            using (var firstTouch = new Mat())
            using (var secondTouch = new Mat())
            using (var labels = new Mat())
            {
                Cv2.Dilate(componentMask, support, supportKernel);
                Cv2.BitwiseAnd(skeleton, support, supportedSkeleton);
                Cv2.Dilate(supportedSkeleton, corridor, corridorKernel);
                Cv2.BitwiseAnd(cisForeground, corridor, foregroundPath);
                Cv2.MorphologyEx(foregroundPath, foregroundPath, MorphTypes.Close, closeKernel);

                Cv2.Dilate(firstAnchor, firstTouch, corridorKernel);
                Cv2.Dilate(secondAnchor, secondTouch, corridorKernel);
                int componentCount = Cv2.ConnectedComponents(foregroundPath, labels);
                if (componentCount <= 1)
                    return false;

                labels.GetArray(out int[] labelValues);
                firstTouch.GetArray(out byte[] firstValues);
                secondTouch.GetArray(out byte[] secondValues);
                var firstLabels = new HashSet<int>();
                for (int index = 0; index < labelValues.Length; index++)
                {
                    int label = labelValues[index];
                    if (label > 0 && firstValues[index] != 0)
                        firstLabels.Add(label);
                }

                if (firstLabels.Count == 0)
                    return false;

                for (int index = 0; index < labelValues.Length; index++)
                {
                    int label = labelValues[index];
                    if (label > 0 && secondValues[index] != 0 && firstLabels.Contains(label))
                        return true;
                }

                return false;
            }
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
