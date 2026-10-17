using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    // 作业结果与日志口径；返回给 UI 的状态模型和已有日志格式在这里集中维护。
    public sealed partial class InspectionJobRunner
    {
        /// <summary>
        /// 汇总产品结论及最终缺陷的真实面积。每件先检查取消，处理异常不计入产品 NG。
        /// 此处仅消费结果，不重新应用阈值，也不按绘图框面积重新测量。
        /// </summary>
        private static (int PassCount, int FailCount, int ProcessingErrorCount) LogPartResults(
            List<PatchDefectResult> results, AppConfig config, IAppLogger log,
            CancellationToken cancellationToken)
        {
            int passCount = 0;
            int failCount = 0;
            int processingErrorCount = 0;
            long defectDetailLogTicks = 0;
            int loggedDefectDetailCount = 0;
            foreach (PatchDefectResult defectResult in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (defectResult.HasProcessingError)
                {
                    processingErrorCount++;
                    Log(
                        log,
                        $"  [⚠ 处理异常] {defectResult.PartId} — " +
                        $"未生成产品缺陷结论：{defectResult.ProcessingError}");
                    continue;
                }

                if (defectResult.IsPass)
                {
                    passCount++;
                }
                else
                {
                    failCount++;
                }

                string status = defectResult.IsPass ? "✓ Pass" : "✗ FAIL";
                string fineLineSummary = defectResult.FineLineBreakCount > 0
                    ? $" | 细线断裂: {defectResult.FineLineBreakCount}个 " +
                      $"(最长 {defectResult.MaxFineLineBreakLengthMm:F2}mm，" +
                      $"最大宽度 {defectResult.MaxFineLineBreakWidthMm:F2}mm)"
                    : string.Empty;
                Log(log,
                    $"  [{status}] {defectResult.PartId} — 内部缺陷: {defectResult.InnerDefectCount}个 " +
                    $"(最大 {defectResult.MaxAreaInnerMm2:F3}mm² / 阈值 {config.DefectAreaThreshInner:F3}mm²) | " +
                    $"外部缺陷: {defectResult.OuterDefectCount}个 " +
                    $"(最大 {defectResult.MaxAreaOuterMm2:F3}mm² / 阈值 {config.DefectAreaThreshOuter:F3}mm²)" +
                    $"{fineLineSummary}");

                // 单独计时新增的逐缺陷尺寸日志，便于判断该追溯功能是否需要配置开关。
                // 明细集合均来自最终检测结果，不会记录未通过面积/长度/线宽门槛的候选。
                long detailStartTicks = Stopwatch.GetTimestamp();
                string defectDetail = BuildDefectMeasurementLog(
                    defectResult,
                    out int partDetailCount);
                if (!string.IsNullOrEmpty(defectDetail))
                {
                    Log(log, defectDetail);
                }

                defectDetailLogTicks += Stopwatch.GetTimestamp() - detailStartTicks;
                loggedDefectDetailCount += partDetailCount;
            }

            double defectDetailLogMilliseconds =
                defectDetailLogTicks * 1000.0 / Stopwatch.Frequency;
            Log(
                log,
                $"[缺陷尺寸统计] 共记录 {loggedDefectDetailCount} 个最终缺陷，" +
                $"新增统计与日志耗时 {defectDetailLogMilliseconds:F1}ms" +
                (defectDetailLogMilliseconds > 500
                    ? "（超过 500ms，建议启用可配置开关）"
                    : "（未超过 500ms，无需增加控制开关）"));
            return (passCount, failCount, processingErrorCount);
        }

        /// <summary>
        /// 生成一个零件的最终缺陷尺寸明细。用单次日志写入承载多行内容，减少磁盘 Flush 和
        /// UI 消息发布次数；每个缺陷仍按类型和序号独立列出，便于现场追溯。
        /// </summary>
        private static string BuildDefectMeasurementLog(
            PatchDefectResult result,
            out int defectCount)
        {
            defectCount = 0;
            var builder = new StringBuilder();
            AppendDefectMeasurements(
                builder,
                "内部缺陷",
                result.InnerDefectMeasurements,
                ref defectCount);
            AppendDefectMeasurements(
                builder,
                "外部缺陷",
                result.OuterDefectMeasurements,
                ref defectCount);
            AppendDefectMeasurements(
                builder,
                "细线断裂",
                result.FineLineBreakMeasurements,
                ref defectCount);

            if (defectCount == 0)
            {
                return string.Empty;
            }

            return $"  [缺陷尺寸] {result.PartId}（外接矩形尺寸 + 缺陷实际面积）{Environment.NewLine}" +
                   builder.ToString().TrimEnd();
        }

        private static void AppendDefectMeasurements(
            StringBuilder builder,
            string defectType,
            IReadOnlyList<DefectGeometryMeasurement> measurements,
            ref int totalCount)
        {
            if (measurements == null)
            {
                return;
            }

            for (int index = 0; index < measurements.Count; index++)
            {
                DefectGeometryMeasurement measurement = measurements[index];
                builder.Append("    ")
                    .Append(defectType)
                    .Append(" #")
                    .Append(index + 1)
                    .Append(": 宽=")
                    .Append(measurement.WidthMm.ToString("F3"))
                    .Append("mm，高=")
                    .Append(measurement.HeightMm.ToString("F3"))
                    .Append("mm，缺陷实际面积=")
                    .Append(measurement.AreaMm2.ToString("F3"))
                    .Append("mm²")
                    .AppendLine();
                totalCount++;
            }
        }

        /// <summary>
        /// 统一创建提前结束结果。Skipped 表示外部资料尚未就绪，Failed 表示本作业自身无法完成；
        /// 两者都不会抛到采集线程，也不会丢失已完成的白墨检查和预览。
        /// </summary>
        private static InspectionJobResult CreateEarlyResult(
            IAppLogger log,
            InspectionJobStatus status,
            InspectionJobIssueCode issueCode,
            string message,
            WhiteInkInspectionResult whiteInkInspection = null,
            byte[] whiteInkPreviewBytes = null,
            string outputDirectory = null)
        {
            Log(log, message);
            return new InspectionJobResult
            {
                Status = status,
                IssueCode = issueCode,
                Message = message,
                WhiteInkInspection = whiteInkInspection ?? WhiteInkInspectionResult.Disabled(),
                WhiteInkPreviewBytes = whiteInkPreviewBytes,
                OutputDirectory = outputDirectory
            };
        }

        /// <summary>记录白墨原始量化数据，便于现场按批次追溯和重新标定。</summary>
        private static void LogWhiteInk(IAppLogger log, WhiteInkInspectionResult result)
        {
            string prefix = result.RequiresWarning ? "[白墨检测][WARN]" : "[白墨检测]";
            Log(
                log,
                $"{prefix} 状态={result.StatusDisplayName}，相对白墨={result.InkLevelPercent:F1}% " +
                $"(估算缺少={result.EstimatedMissingPercent:F1}%)，Mark均值={result.MarkMean:F1}，" +
                $"背景均值={result.BackgroundMean:F1}，对比度={result.Contrast:F1}，" +
                $"标准差={result.MarkStandardDeviation:F1}，方差={result.MarkVariance:F1}，" +
                $"拉丝={(result.HasStreaking ? "是" : "否")}，样本={result.Samples.Count}，" +
                $"ROI=({result.SearchRegion.X},{result.SearchRegion.Y}," +
                $"{result.SearchRegion.Width},{result.SearchRegion.Height})。");
        }

        /// <summary>日志回调异常不得反向中断检测流水线。</summary>
        private static void Log(IAppLogger log, string message)
        {
            AppLog.Write(log, message);
        }
    }
}
