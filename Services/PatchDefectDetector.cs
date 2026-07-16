using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 单个零件的缺陷检测结果。
    /// </summary>
    public class PatchDefectResult
    {
        public string PartId { get; set; }
        public int MaxAreaInner { get; set; }
        public int MaxAreaOuter { get; set; }
        public int InnerDefectCount { get; set; }
        public int OuterDefectCount { get; set; }
        public bool IsPass { get; set; }
        public Rect GlobalRoi { get; set; }
        public List<Rect> InnerRects { get; set; } = new List<Rect>();
        public List<Rect> OuterRects { get; set; } = new List<Rect>();
    }

    /// <summary>
    /// 单次裁切批次内复用的 TIFF/Alpha 模板特征缓存。
    /// 缓存只在一批零件处理期间存在，避免跨批次持有 Mat。
    /// </summary>
    internal sealed class PatchSiftTemplateCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<PatchSiftTemplateFeatures>> _templates =
            new ConcurrentDictionary<string, Lazy<PatchSiftTemplateFeatures>>();

        public int Count => _templates.Count;

        public PatchSiftTemplateFeatures GetOrCreate(Mat templateFeatureImage, SIFT sift)
        {
            string key = ComputeKey(templateFeatureImage);
            Lazy<PatchSiftTemplateFeatures> lazy = _templates.GetOrAdd(
                key,
                _ => new Lazy<PatchSiftTemplateFeatures>(
                    () => PatchSiftTemplateFeatures.Create(templateFeatureImage, sift),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        private static string ComputeKey(Mat image)
        {
            image.GetArray(out byte[] pixels);
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(pixels);
                return $"{image.Rows}x{image.Cols}:{image.Type()}:{BitConverter.ToString(hash).Replace("-", string.Empty)}";
            }
        }

        public void Dispose()
        {
            foreach (Lazy<PatchSiftTemplateFeatures> lazy in _templates.Values)
            {
                if (lazy.IsValueCreated)
                    lazy.Value.Dispose();
            }
            _templates.Clear();
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
            Mat alphaGray = ToGray(alphaImg);
            Mat cisGray = ToGray(cisImg);

            try
            {
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

                                bool needOriginalWarp = !string.IsNullOrEmpty(cisOutputPath) && config.SaveCroppedImages;
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
                            Cv2.Threshold(alphaScaled, alphaBinary, config.DefectAlphaBinaryThresh, 255, ThresholdTypes.Binary);
                            Cv2.Threshold(cisAligned, cisBinary, cisBaseThresh, 255, ThresholdTypes.Binary);

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
                            {
                                Cv2.Dilate(cisBinary, cisDilatedInner, kernelInner);
                                Cv2.Subtract(alphaBinary, cisDilatedInner, difInner);
                                Cv2.Dilate(alphaBinary, alphaDilatedOuter, kernelOuter);
                                Cv2.Subtract(cisBinary, alphaDilatedOuter, difOuter);

                                if (scaledEdgeThick > 0 || scaledEdgeSmall > 0)
                                    ApplyEdgeExclusion(alphaBinary, difInner, difOuter, scaledEdgeThick, scaledEdgeSmall);

                                List<Rect> innerRects = AnalyzeConnectedComponents(
                                    difInner, scaledAreaThreshInner, out int maxAreaInner, out int innerCount);
                                List<Rect> outerRects = AnalyzeConnectedComponents(
                                    difOuter, scaledAreaThreshOuter, out int maxAreaOuter, out int outerCount);

                                result.MaxAreaInner = maxAreaInner;
                                result.MaxAreaOuter = maxAreaOuter;
                                result.InnerDefectCount = innerCount;
                                result.OuterDefectCount = outerCount;
                                result.IsPass = maxAreaInner <= scaledAreaThreshInner &&
                                                maxAreaOuter <= scaledAreaThreshOuter;
                                result.InnerRects = innerRects.Select(r => new Rect(
                                    (int)(r.X / scale), (int)(r.Y / scale),
                                    (int)(r.Width / scale), (int)(r.Height / scale))).ToList();
                                result.OuterRects = outerRects.Select(r => new Rect(
                                    (int)(r.X / scale), (int)(r.Y / scale),
                                    (int)(r.Width / scale), (int)(r.Height / scale))).ToList();

                                if (!string.IsNullOrEmpty(outputPath))
                                {
                                    SaveVisualization(alphaBinary, cisBinary, difInner, difOuter,
                                        innerRects, outerRects, result.IsPass, outputPath);
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
        /// 边缘屏蔽：在差分图上消除轮廓边缘的假缺陷。
        /// 提取为独立方法以保持 Detect 主流程清晰。
        /// </summary>
        private static void ApplyEdgeExclusion(Mat alphaBinary, Mat difInner, Mat difOuter, int edgeThick, int edgeSmall)
        {
            using (Mat externalInput = alphaBinary.Clone())
            using (Mat alphaFilled = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1))
            using (Mat edgeMask = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1))
            {
                Cv2.FindContours(externalInput, out Point[][] contoursExt, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                Cv2.DrawContours(alphaFilled, contoursExt, -1, new Scalar(255), -1);

                using (Mat filledInput = alphaFilled.Clone())
                {
                    Cv2.FindContours(filledInput, out Point[][] contoursFilled, out _,
                        RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    if (edgeThick > 0)
                        Cv2.DrawContours(edgeMask, contoursFilled, -1, new Scalar(255), edgeThick);
                }

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

                difInner.SetTo(new Scalar(0), edgeMask);
                difOuter.SetTo(new Scalar(0), edgeMask);
            }
        }

        /// <summary>
        /// SIFT 特征匹配 + RANSAC + 仿射对齐。
        /// 返回对齐后的 CIS 图像 (comAligned)。失败时返回原始 comScaled。
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

                            Mat scaledOutput = new Mat();
                            Mat originalOutput = null;
                            try
                            {
                                Cv2.WarpAffine(cisScaled, scaledOutput, transform, alphaScaled.Size(), InterpolationFlags.Cubic);

                                // 原分辨率图只用于保存；不保存时省去这次大图 Warp，不影响检测结果。
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
            List<Rect> innerRects, List<Rect> outerRects, bool isPass, string outputPath)
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
