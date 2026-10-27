using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 把排版日志中的毫米坐标映射为 TIFF 目标空间 ROI，并行执行零件级检测。
    /// 裁切 Mat 均为父图的零拷贝视图，必须在父图释放前完成使用；诊断图与性能输出分别见 Preview、Performance 职责文件。
    /// </summary>
    public static partial class PatchCropper
    {
        // 本文件负责 ROI 和 worker/批次缓存生命周期；显示图与性能落盘分别在 Preview/Performance。
        private static readonly ImageEncodingParam[] PatchJpegParameters =
        {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)
        };
        /// <summary>
        /// 根据排版坐标，从对齐后的 CIS 图、TIFF 图和 Alpha 掩膜上裁切零件小图，
        /// 同时执行缺陷检测并返回结果列表。
        /// </summary>
        /// <param name="cisWarped">变换到 TIFF 空间的 CIS 实拍图</param>
        /// <param name="tiffMat">TIFF 原始排版图 (BGR 3通道，已 alpha 融合到白底)</param>
        /// <param name="alphaMask">从 TIFF 原图分离出的 Alpha 通道掩膜 (单通道)，可为 null</param>
        /// <param name="parts">零件排版坐标列表</param>
        /// <param name="outputDir">输出目录</param>
        /// <param name="scale">排版像素密度 px/mm (例如 300/25.4)，不是 DefectDetectScale 缩放比例</param>
        /// <param name="originXmm">X 原点偏移 (mm)</param>
        /// <param name="originYmm">Y 原点偏移 (mm)</param>
        /// <param name="cisBaseThresh">已叠加 DefectCisThreshOffset 并限幅的最终 CIS 二值化阈值</param>
        /// <param name="config">全局配置（用于裁切/检测参数）</param>
        /// <returns>每个零件的缺陷检测结果列表</returns>
        public static (List<PatchDefectResult> Results, string GlobalImagePath, byte[] GlobalImageBytes) CropAndSave(Mat cisWarped, Mat tiffMat, Mat alphaMask,
            List<PartLocation> parts, string outputDir, double scale, double originXmm, double originYmm,
            int cisBaseThresh, AppConfig config, IAppLogger logger = null)
        {
            logger = logger ?? NullAppLogger.Instance;

            // 关闭图像总开关时不创建空的时间戳目录；跨批次性能数据写入父目录的汇总文件。
            if (config.SaveCroppedImages && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // origin 表示排版坐标系原点在 TIFF 图像中的毫米偏移；scale 为 TIFF px/mm。
            int originX = (int)(originXmm * scale);
            int originY = (int)(originYmm * scale);

            int imgWidth = tiffMat.Width;
            int imgHeight = tiffMat.Height;

            int count = 0;

            // 排版日志沿用 readlog.cpp 的纵向坐标约定；与其 flip(..., 0) 保持一致后，
            // 同一组 ROI 才能同时落在 TIFF、Alpha 和已对准 CIS 的对应零件上。
            Mat flippedCis = new Mat();
            Mat flippedTiff = new Mat();
            Cv2.Flip(cisWarped, flippedCis, FlipMode.X);
            Cv2.Flip(tiffMat, flippedTiff, FlipMode.X);

            Mat flippedAlpha = null;
            if (alphaMask != null)
            {
                flippedAlpha = new Mat();
                Cv2.Flip(alphaMask, flippedAlpha, FlipMode.X);
            }

            try
            {
                // ConcurrentBag 只用于线程安全汇总，结果顺序不参与判定；显示时依赖 PartId/GlobalRoi。
                var resultsBag = new ConcurrentBag<PatchDefectResult>();
                var partElapsedMilliseconds = new ConcurrentBag<long>();
                // 自动模式最多使用 4 个 worker，显式配置仍受 CPU 数和安全上限 16 约束。
                int automaticParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
                int maxParallelism = config.DefectMaxParallelism <= 0
                    ? automaticParallelism
                    : Math.Max(1, Math.Min(Math.Min(16, Environment.ProcessorCount), config.DefectMaxParallelism));
                var parallelOptions = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = maxParallelism
                };
                // 所有零件算法均由原生 worker 执行；关闭局部配准时缓存为空且不会创建 SIFT。
                PatchSiftTemplateCache templateCache = new PatchSiftTemplateCache();
                long managedBytesBefore = GC.GetTotalMemory(false);
                long privateBytesBefore = GetPrivateBytes();
                var patchStopwatch = Stopwatch.StartNew();

                try
                {
                    System.Threading.Tasks.Parallel.ForEach<PartLocation, PatchSiftWorker>(
                        parts,
                        parallelOptions,
                        // SIFT 不是跨线程共享对象；每个并行 worker 独占原生上下文，只共享只读模板特征缓存。
                        () => new PatchSiftWorker(templateCache),
                        (part, _, worker) =>
                        {
                            if (part.HotInkTaskID != null && part.HotInkTaskID.Contains("QRCode"))
                            {
                                // 排版日志中的二维码区域是定位基准，不属于需要判定的产品零件。
                                return worker;
                            }

                            int x = (int)(originX + part.RelativeTopLeftX * scale);
                            int y = (int)(originY + part.RelativeTopLeftY * scale);
                            int w = (int)((part.RelativeBottomRightX - part.RelativeTopLeftX) * scale);
                            int h = (int)((part.RelativeBottomRightY - part.RelativeTopLeftY) * scale);

                            // 边界零件允许 ROI 被图像边界裁剪；裁剪后为空则跳过，防止 OpenCV Rect 异常。
                            x = Math.Max(0, x);
                            y = Math.Max(0, y);
                            w = Math.Min(w, imgWidth - x);
                            h = Math.Min(h, imgHeight - y);

                            if (w <= 0 || h <= 0)
                            {
                                return worker;
                            }

                            Rect roi = new Rect(x, y, w, h);
                            var partStopwatch = Stopwatch.StartNew();
                            try
                            {
                                // 子 Mat 仅引用翻转后大图的 ROI，不复制像素；using 限定其生命周期，避免悬空引用。
                                using (Mat cisPatch = new Mat(flippedCis, roi))
                                using (Mat tiffPatch = new Mat(flippedTiff, roi))
                                {
                                    string cisName = null;
                                    if (config.SaveCroppedImages)
                                    {
                                        cisName = Path.Combine(outputDir, $"{part.HotInkTaskID}_cis.jpg");
                                        string tiffName = Path.Combine(outputDir, $"{part.HotInkTaskID}_tiff.jpg");
                                        Cv2.ImWrite(tiffName, tiffPatch, PatchJpegParameters);
                                    }

                                    // 缺陷模板以 TIFF Alpha 为准；没有 Alpha 时仅能保存裁图，不能产生可靠差分结果。
                                    if (flippedAlpha != null)
                                    {
                                        using (Mat alphaPatch = new Mat(flippedAlpha, roi))
                                        {
                                            if (config.SaveCroppedImages)
                                            {
                                                string alphaName = Path.Combine(outputDir, $"{part.HotInkTaskID}_alpha.png");
                                                // “裁切结果”中的 Alpha 图用于核对缺陷模板，应直接展示实际参与
                                                // 缺陷判断的二值语义。这里在原始裁切尺寸上应用同一个阈值；
                                                // 使用独立 Mat，不能原地修改共享的 flippedAlpha ROI，否则会污染
                                                // 后续并行零件检测所读取的 Alpha 模板。
                                                using (var alphaBinaryForSave = new Mat())
                                                {
                                                    Cv2.Threshold(
                                                        alphaPatch,
                                                        alphaBinaryForSave,
                                                        config.DefectAlphaBinaryThresh,
                                                        255,
                                                        ThresholdTypes.Binary);
                                                    Cv2.ImWrite(alphaName, alphaBinaryForSave);
                                                }
                                            }

                                            string defectPath = config.SaveCroppedImages && config.SaveDefectResultImages
                                                ? Path.Combine(outputDir, $"{part.HotInkTaskID}_defect.jpg")
                                                : null;
                                            string edgeExclusionPath = config.SaveCroppedImages
                                                ? Path.Combine(outputDir, $"{part.HotInkTaskID}_edge_exclusion.png")
                                                : null;

                                            // 搜索余量只给局部配准：缺陷与输出继续使用 core。
                                            // 相同核心目标在不同裁框原点下仍具有可用的真实外围像素。
                                            int padding = 0;
                                            if (config.EnableSiftLocalAlign && !double.IsNaN(config.LocalAlignmentPaddingMm) &&
                                                !double.IsInfinity(config.LocalAlignmentPaddingMm))
                                            {
                                                // 用户配置可能异常大；先限制数值再转 int，防止溢出造成负 ROI。
                                                padding = Math.Max(16, (int)Math.Min(2000,
                                                    Math.Ceiling(Math.Max(0, config.LocalAlignmentPaddingMm) * scale)));
                                            }
                                            int left = Math.Max(0, roi.X - padding);
                                            int top = Math.Max(0, roi.Y - padding);
                                            int right = Math.Min(imgWidth, roi.Right + padding);
                                            int bottom = Math.Min(imgHeight, roi.Bottom + padding);
                                            Rect searchRoi = new Rect(left, top, right - left, bottom - top);
                                            Rect coreInSearch = new Rect(roi.X - left, roi.Y - top, roi.Width, roi.Height);
                                            using (Mat alphaSearch = new Mat(flippedAlpha, searchRoi))
                                            using (Mat cisSearch = new Mat(flippedCis, searchRoi))
                                            {
                                                PatchDefectResult defectResult = PatchDefectDetector.Detect(
                                                    alphaSearch, cisSearch, cisBaseThresh, config, defectPath,
                                                    cisName, edgeExclusionPath, worker, part.HotInkTaskID, logger,
                                                    coreInSearch);
                                                defectResult.PartId = part.HotInkTaskID;
                                                defectResult.GlobalRoi = roi;
                                                resultsBag.Add(defectResult);
                                            }
                                        }
                                    }

                                    System.Threading.Interlocked.Increment(ref count);
                                }
                            }
                            catch (Exception ex)
                            {
                                // 工程异常不能从并行结果中静默消失，也不能冒充产品缺陷。
                                // 返回独立状态后，批次汇总会把它列为“处理异常”，等待复检或排障。
                                resultsBag.Add(new PatchDefectResult
                                {
                                    PartId = part.HotInkTaskID,
                                    GlobalRoi = roi,
                                    ProcessingStatus = PatchProcessingStatus.ProcessingError,
                                    ProcessingError = ex.Message,
                                    IsPass = false
                                });
                                AppLog.Write(
                                    logger,
                                    $"[零件处理][ERROR] {part.HotInkTaskID} 裁切/检测异常: {ex.Message}");
                            }
                            finally
                            {
                                partStopwatch.Stop();
                                partElapsedMilliseconds.Add(partStopwatch.ElapsedMilliseconds);
                            }

                            return worker;
                        },
                        worker => worker?.Dispose());

                    patchStopwatch.Stop();
                    long[] durations = partElapsedMilliseconds.OrderBy(value => value).ToArray();
                    long managedBytesAfter = GC.GetTotalMemory(false);
                    long privateBytesAfter = GetPrivateBytes();
                    long cacheHits = templateCache?.HitCount ?? 0;
                    long cacheMisses = templateCache?.MissCount ?? 0;
                    long cacheRequests = cacheHits + cacheMisses;
                    double cacheHitRate = cacheRequests == 0 ? 0.0 : (double)cacheHits / cacheRequests;
                    string summary =
                        $"[性能基线] 零件 {durations.Length}，总耗时 {patchStopwatch.ElapsedMilliseconds}ms，" +
                        $"并行度 {maxParallelism}，P50/P95/最大 {Percentile(durations, 0.50)}/" +
                        $"{Percentile(durations, 0.95)}/{Percentile(durations, 1.00)}ms，" +
                        $"模板 {templateCache?.Count ?? 0}，缓存命中 {cacheHits}/{cacheRequests} ({cacheHitRate:P0})。";

                    AppLog.Write(logger, summary);

                    // 性能 CSV 用于比较同一设备/数据集上的趋势，不参与 Pass/Fail，也不是验收阈值。
                    TryAppendPerformanceBaseline(
                        outputDir,
                        durations.Length,
                        resultsBag.Count,
                        patchStopwatch.ElapsedMilliseconds,
                        maxParallelism,
                        durations,
                        templateCache,
                        managedBytesAfter - managedBytesBefore,
                        privateBytesAfter - privateBytesBefore,
                        logger);
                }
                finally
                {
                    templateCache?.Dispose();
                }

                // 并行汇总顺序不稳定，但每个结果都带 PartId 和 GlobalRoi，不依赖列表下标关联零件。
                var results = new List<PatchDefectResult>(resultsBag);
                int processingErrors = results.Count(result => result.HasProcessingError);
                int alignmentReviews = results.Count(result => result.NeedsAlignmentReview);
                int similarityCount = results.Count(result =>
                    (result.AlignmentStatus == LocalAlignmentStatus.Applied ||
                     result.AlignmentStatus == LocalAlignmentStatus.AppliedDegraded) &&
                    result.AlignmentModel == LocalAlignmentModel.Similarity);
                int affineCount = results.Count(result =>
                    (result.AlignmentStatus == LocalAlignmentStatus.Applied ||
                     result.AlignmentStatus == LocalAlignmentStatus.AppliedDegraded) &&
                    result.AlignmentModel == LocalAlignmentModel.ConstrainedAffine);
                int homographyCount = results.Count(result =>
                    (result.AlignmentStatus == LocalAlignmentStatus.Applied ||
                     result.AlignmentStatus == LocalAlignmentStatus.AppliedDegraded) &&
                    result.AlignmentModel == LocalAlignmentModel.Homography);
                int degradedCount = results.Count(result => result.AlignmentStatus == LocalAlignmentStatus.AppliedDegraded);
                int notNeededCount = results.Count(result => result.AlignmentStatus == LocalAlignmentStatus.NotNeeded);
                AppLog.Write(
                    logger,
                    $"[PatchCropper] 共成功处理 {count} 个零件，返回 {results.Count} 个结果，" +
                    $"局部相似 {similarityCount}、受约束仿射 {affineCount}、单应性 {homographyCount}、" +
                    $"无需二次 {notNeededCount}、局部已用但降级 {degradedCount}、" +
                    $"待复核合计 {alignmentReviews}、工程异常 {processingErrors}。");

                var preview = CreateGlobalPreview(flippedCis, flippedTiff, results, outputDir, config, logger);
                return (results, preview.GlobalImagePath, preview.GlobalImageBytes);
            }
            finally
            {
                flippedAlpha?.Dispose();
                flippedTiff.Dispose();
                flippedCis.Dispose();
            }
        }
    }
}
