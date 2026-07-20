using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 把排版日志中的毫米坐标映射为 TIFF 目标空间 ROI，并行执行零件级检测。
    /// 裁切 Mat 均为父图的零拷贝视图，必须在父图释放前完成使用；本类同时负责批次诊断图和性能基线输出。
    /// </summary>
    public class PatchCropper
    {
        private static readonly object PerformanceFileSync = new object();

        /// <summary>
        /// 根据排版坐标，从对齐后的 CIS 图、TIFF 图和 Alpha 掩膜上裁切零件小图，
        /// 同时执行缺陷检测并返回结果列表。
        /// </summary>
        /// <param name="cisWarped">变换到 TIFF 空间的 CIS 实拍图</param>
        /// <param name="tiffMat">TIFF 原始排版图 (BGR 3通道，已 alpha 融合到白底)</param>
        /// <param name="alphaMask">从 TIFF 原图分离出的 Alpha 通道掩膜 (单通道)，可为 null</param>
        /// <param name="parts">零件排版坐标列表</param>
        /// <param name="outputDir">输出目录</param>
        /// <param name="scale">DPI换算系数 (例如 300/25.4)</param>
        /// <param name="originXmm">X 原点偏移 (mm)</param>
        /// <param name="originYmm">Y 原点偏移 (mm)</param>
        /// <param name="config">全局配置（用于裁切/检测参数）</param>
        /// <returns>每个零件的缺陷检测结果列表</returns>
        public static (List<PatchDefectResult> Results, string GlobalImagePath, byte[] GlobalImageBytes) CropAndSave(Mat cisWarped, Mat tiffMat, Mat alphaMask,
            List<PartLocation> parts, string outputDir, double scale, double originXmm, double originYmm,
            int cisBaseThresh, AppConfig config, Action<string> performanceLog = null)
        {

            // 每个检测批次使用独立时间目录，避免覆盖上一批零件图和性能基线。
            if (!Directory.Exists(outputDir))
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
            PatchSiftTemplateCache templateCache = config.EnableSiftLocalAlign
                ? new PatchSiftTemplateCache()
                : null;
            long managedBytesBefore = GC.GetTotalMemory(false);
            long privateBytesBefore = GetPrivateBytes();
            var patchStopwatch = Stopwatch.StartNew();

            try
            {
                System.Threading.Tasks.Parallel.ForEach<PartLocation, PatchSiftWorker>(
                    parts,
                    parallelOptions,
                    // SIFT/Matcher 不是跨线程共享对象；每个并行 worker 独占一组，只共享只读模板特征缓存。
                    () => templateCache != null ? new PatchSiftWorker(templateCache) : null,
                    (part, _, worker) =>
                    {
                        if (part.HotInkTaskID != null && part.HotInkTaskID.Contains("QRCode"))
                            // 排版日志中的二维码区域是定位基准，不属于需要判定的产品零件。
                            return worker;

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
                            return worker;

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
                                    var prms = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 95) };
                                    cisName = Path.Combine(outputDir, $"{part.HotInkTaskID}_cis.jpg");
                                    string tiffName = Path.Combine(outputDir, $"{part.HotInkTaskID}_tiff.jpg");
                                    Cv2.ImWrite(tiffName, tiffPatch, prms);
                                }

                                // 缺陷模板以 TIFF Alpha 为准；没有 Alpha 时仅能保存裁图，不能产生可靠差分结果。
                                if (flippedAlpha != null)
                                {
                                    using (Mat alphaPatch = new Mat(flippedAlpha, roi))
                                    {
                                        if (config.SaveCroppedImages)
                                        {
                                            string alphaName = Path.Combine(outputDir, $"{part.HotInkTaskID}_alpha.png");
                                            Cv2.ImWrite(alphaName, alphaPatch);
                                        }

                                        string defectPath = config.SaveDefectResultImages
                                            ? Path.Combine(outputDir, $"{part.HotInkTaskID}_defect.jpg")
                                            : null;

                                        PatchDefectResult defectResult = PatchDefectDetector.Detect(
                                            alphaPatch,
                                            cisPatch,
                                            cisBaseThresh,
                                            config,
                                            defectPath,
                                            cisName,
                                            worker,
                                            part.HotInkTaskID);
                                        defectResult.PartId = part.HotInkTaskID;
                                        defectResult.GlobalRoi = roi;
                                        resultsBag.Add(defectResult);
                                    }
                                }

                                System.Threading.Interlocked.Increment(ref count);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"裁切/检测 {part.HotInkTaskID} 异常: {ex.Message}");
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

                Console.WriteLine(summary);
                try
                {
                    performanceLog?.Invoke(summary);
                }
                catch
                {
                    // 性能日志不能影响检测主流程。
                }

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
                    privateBytesAfter - privateBytesBefore);
            }
            finally
            {
                templateCache?.Dispose();
            }

            // 并行汇总顺序不稳定，但每个结果都带 PartId 和 GlobalRoi，不依赖列表下标关联零件。
            var results = new List<PatchDefectResult>(resultsBag);
            Console.WriteLine($"[PatchCropper] 共成功处理 {count} 个零件，检测完成 {results.Count} 个。");

            // --- 全局缺陷可视化保存与显示 ---
            // 下列缩放和绘框仅服务于 UI/报告，不回流到检测算法，避免预览分辨率影响判定。
            byte[] globalImageBytes = null;
            string combinedPath = null;
            try
            {
                // 动态计算显示缩放比例，限制最大高度以降低内存消耗和处理耗时
                double displayScale = 1.0;
                int maxDisplayHeight = 3000;
                if (flippedCis.Height > maxDisplayHeight)
                {
                    displayScale = (double)maxDisplayHeight / flippedCis.Height;
                }

                using (var cisSmall = new Mat())
                using (var cisCanvas = new Mat())
                using (var tiffSmall = new Mat())
                using (var tiffCanvas = new Mat())
                {
                    if (displayScale < 1.0)
                        Cv2.Resize(flippedCis, cisSmall, new Size(), displayScale, displayScale, InterpolationFlags.Nearest);
                    else
                        flippedCis.CopyTo(cisSmall);

                    if (cisSmall.Channels() == 1)
                        Cv2.CvtColor(cisSmall, cisCanvas, ColorConversionCodes.GRAY2BGR);
                    else
                        cisSmall.CopyTo(cisCanvas);

                    int partThick = Math.Max(2, (int)(5 * displayScale));
                    int defectThick = Math.Max(2, (int)(9 * displayScale));
                    foreach (PatchDefectResult res in results)
                    {
                        Rect scaledRoi = new Rect(
                            (int)(res.GlobalRoi.X * displayScale),
                            (int)(res.GlobalRoi.Y * displayScale),
                            (int)(res.GlobalRoi.Width * displayScale),
                            (int)(res.GlobalRoi.Height * displayScale));
                        Scalar partColor = res.IsPass ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);
                        Cv2.Rectangle(cisCanvas, scaledRoi, partColor, partThick);

                        foreach (Rect rect in res.InnerRects)
                        {
                            Rect scaledDefectRect = new Rect(
                                (int)((res.GlobalRoi.X + rect.X) * displayScale),
                                (int)((res.GlobalRoi.Y + rect.Y) * displayScale),
                                (int)(rect.Width * displayScale),
                                (int)(rect.Height * displayScale));
                            Cv2.Rectangle(cisCanvas, scaledDefectRect, new Scalar(0, 165, 255), defectThick);
                        }

                        foreach (Rect rect in res.OuterRects)
                        {
                            Rect scaledDefectRect = new Rect(
                                (int)((res.GlobalRoi.X + rect.X) * displayScale),
                                (int)((res.GlobalRoi.Y + rect.Y) * displayScale),
                                (int)(rect.Width * displayScale),
                                (int)(rect.Height * displayScale));
                            Cv2.Rectangle(cisCanvas, scaledDefectRect, new Scalar(0, 0, 255), defectThick);
                        }
                    }

                    if (displayScale < 1.0)
                        Cv2.Resize(flippedTiff, tiffSmall, new Size(), displayScale, displayScale, InterpolationFlags.Nearest);
                    else
                        flippedTiff.CopyTo(tiffSmall);

                    if (tiffSmall.Channels() == 1)
                        Cv2.CvtColor(tiffSmall, tiffCanvas, ColorConversionCodes.GRAY2BGR);
                    else if (tiffSmall.Channels() == 4)
                        Cv2.CvtColor(tiffSmall, tiffCanvas, ColorConversionCodes.BGRA2BGR);
                    else
                        tiffSmall.CopyTo(tiffCanvas);

                    if (tiffCanvas.Channels() == 3 && cisCanvas.Channels() == 3)
                    {
                        using (var combined = new Mat())
                        {
                            Cv2.HConcat(new[] { tiffCanvas, cisCanvas }, combined);
                            var prms = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 85) };
                            Cv2.ImEncode(".jpg", combined, out globalImageBytes, prms);

                            if (config.SaveDefectResultImages)
                            {
                                combinedPath = Path.Combine(outputDir, "GlobalDefectResult.jpg");
                                Cv2.ImWrite(combinedPath, combined, prms);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PatchCropper] 生成全局缺陷图异常: {ex.Message}");
            }

                return (results, combinedPath, globalImageBytes);
            }
            finally
            {
                flippedAlpha?.Dispose();
                flippedTiff.Dispose();
                flippedCis.Dispose();
            }
        }

        /// <summary>从已排序耗时数组读取 nearest-rank 分位数；空数组返回 0。</summary>
        private static long Percentile(long[] sortedValues, double percentile)
        {
            if (sortedValues == null || sortedValues.Length == 0)
                return 0;

            int index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
            index = Math.Max(0, Math.Min(sortedValues.Length - 1, index));
            return sortedValues[index];
        }

        /// <summary>读取进程 Private Bytes，失败时返回 0；该数据只用于趋势诊断。</summary>
        private static long GetPrivateBytes()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.PrivateMemorySize64;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 追加本批次和跨批次性能 CSV。任何文件错误都只记录日志，不影响已经完成的缺陷结果。
        /// </summary>
        private static void TryAppendPerformanceBaseline(
            string outputDir,
            int partCount,
            int resultCount,
            long totalElapsedMilliseconds,
            int maxParallelism,
            long[] sortedDurations,
            PatchSiftTemplateCache templateCache,
            long managedBytesDelta,
            long privateBytesDelta)
        {
            try
            {
                string batchPath = Path.Combine(outputDir, "PerformanceBaseline.csv");
                string parentDirectory = Directory.GetParent(outputDir)?.FullName;
                string aggregatePath = string.IsNullOrWhiteSpace(parentDirectory)
                    ? null
                    : Path.Combine(parentDirectory, "PerformanceBaselines.csv");
                string header =
                    "Timestamp,Parts,Results,ElapsedMs,Parallelism,P50Ms,P95Ms,MaxMs," +
                    "CacheUnique,CacheHits,CacheMisses,CacheExactComparisons,CacheKeyMs,CacheCompareMs," +
                    "ManagedDeltaMB,PrivateDeltaMB" + Environment.NewLine;
                string row = string.Join(",", new[]
                {
                    DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                    partCount.ToString(CultureInfo.InvariantCulture),
                    resultCount.ToString(CultureInfo.InvariantCulture),
                    totalElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                    maxParallelism.ToString(CultureInfo.InvariantCulture),
                    Percentile(sortedDurations, 0.50).ToString(CultureInfo.InvariantCulture),
                    Percentile(sortedDurations, 0.95).ToString(CultureInfo.InvariantCulture),
                    Percentile(sortedDurations, 1.00).ToString(CultureInfo.InvariantCulture),
                    (templateCache?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                    (templateCache?.HitCount ?? 0).ToString(CultureInfo.InvariantCulture),
                    (templateCache?.MissCount ?? 0).ToString(CultureInfo.InvariantCulture),
                    (templateCache?.ExactComparisonCount ?? 0).ToString(CultureInfo.InvariantCulture),
                    (templateCache?.QuickKeyElapsedMilliseconds ?? 0.0).ToString("F3", CultureInfo.InvariantCulture),
                    (templateCache?.ExactComparisonElapsedMilliseconds ?? 0.0).ToString("F3", CultureInfo.InvariantCulture),
                    (managedBytesDelta / 1048576.0).ToString("F3", CultureInfo.InvariantCulture),
                    (privateBytesDelta / 1048576.0).ToString("F3", CultureInfo.InvariantCulture)
                }) + Environment.NewLine;

                lock (PerformanceFileSync)
                {
                    AppendPerformanceRow(batchPath, header, row);
                    if (!string.IsNullOrWhiteSpace(aggregatePath) &&
                        !string.Equals(batchPath, aggregatePath, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendPerformanceRow(aggregatePath, header, row);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PatchCropper] 写入性能基线失败: {ex.Message}");
            }
        }

        /// <summary>首次创建时写表头，后续只追加一行；外层锁负责同进程并发写入互斥。</summary>
        private static void AppendPerformanceRow(string path, string header, string row)
        {
            bool writeHeader = !File.Exists(path);
            File.AppendAllText(path, (writeHeader ? header : string.Empty) + row);
        }
    }
}
