using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CIS_WebInspector.Services
{
    // 性能日志和跨批次 CSV。仅记录观测值，不参与判定，也不受图像保存开关控制。
    public static partial class PatchCropper
    {
        private static readonly object PerformanceFileSync = new object();

        /// <summary>从已排序耗时数组读取 nearest-rank 分位数；空数组返回 0。</summary>
        private static long Percentile(long[] sortedValues, double percentile)
        {
            if (sortedValues == null || sortedValues.Length == 0)
            {
                return 0;
            }

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
                {
                    return process.PrivateMemorySize64;
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 追加跨批次性能 CSV。每批次的耗时明细仍写入运行日志，不再在时间戳目录重复保存
        /// PerformanceBaseline.csv。任何文件错误都只记录日志，不影响已经完成的缺陷结果。
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
            long privateBytesDelta,
            IAppLogger logger)
        {
            try
            {
                string parentDirectory = Directory.GetParent(outputDir)?.FullName;
                string aggregatePath = string.IsNullOrWhiteSpace(parentDirectory)
                    ? null
                    : Path.Combine(parentDirectory, "PerformanceBaselines.csv");
                if (string.IsNullOrWhiteSpace(aggregatePath))
                {
                    return;
                }

                Directory.CreateDirectory(parentDirectory);
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
                    AppendPerformanceRow(aggregatePath, header, row);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(logger, $"[PatchCropper][WARN] 写入性能基线失败: {ex.Message}");
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
