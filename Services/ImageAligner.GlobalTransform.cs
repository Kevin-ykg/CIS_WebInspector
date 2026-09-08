using System;
using System.Diagnostics;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    public static partial class ImageAligner
    {
        public static AlignmentResult ComputeTransform(Mat cisMat, Mat tiffMat, CisQrAnchor qrAnchor,
            MarkAlignmentOptions options, out int optimalThresh, out string diagnostic)
        {
            return ComputeTransform(cisMat, tiffMat, qrAnchor, options, out optimalThresh, out diagnostic, out _);
        }

        /// <summary>
        /// 大圆候选 -> 倾斜行拟合 -> 缺点序列配对 -> RANSAC H0/质量门控 -> 可选侧边残差网格。
        /// 实现在 alignment_global/marks/side_grid.cpp。白墨结果独立于矩阵成功与否，Mark 不足时仍返回已完成的统计。
        /// </summary>
        public static AlignmentResult ComputeTransform(Mat cisMat, Mat tiffMat, CisQrAnchor qrAnchor,
            MarkAlignmentOptions options, out int optimalThresh, out string diagnostic,
            out WhiteInkInspectionResult whiteInkInspection)
        {
            optimalThresh = 127; diagnostic = null; whiteInkInspection = WhiteInkInspectionResult.Disabled();
            if (cisMat == null || cisMat.Empty()) diagnostic = "CIS 图像为空。";
            else if (tiffMat == null || tiffMat.Empty()) diagnostic = "TIFF 图像为空。";
            else if (qrAnchor == null) diagnostic = "缺少第二个二维码的全局坐标锚点。";
            else if (options == null) diagnostic = "缺少 Mark 配准参数。";
            if (diagnostic != null) return null;

            AlignmentNativeInterop.ResultHandle handle = null;
            AlignmentResult result = null;
            try
            {
                handle = AlignmentNativeInterop.Compute(cisMat, tiffMat, qrAnchor, options, false);
                var summary = AlignmentNativeInterop.ReadSummary(handle);
                optimalThresh = summary.Threshold;
                diagnostic = AlignmentNativeInterop.ReadLog(handle);
                whiteInkInspection = AlignmentNativeInterop.ReadWhite(handle, summary);
                if (summary.HasTransform == 0) return null; // 明确失败原因，不重新调用旧 C# 算法。

                result = AlignmentNativeInterop.ReadAlignment(handle, summary);
                result.Diagnostic = diagnostic;
                result.WhiteInkInspection = whiteInkInspection;
                UpdateNativeMetrics(result, summary);
                // 小型结果全部读取成功才转交原生所有权；此前任意异常都会在 finally 中释放 handle。
                result.NativeResource = handle;
                handle = null;
                return result;
            }
            catch { result?.Dispose(); throw; }
            finally { handle?.Dispose(); }
        }

        private static void UpdateNativeMetrics(AlignmentResult result, AlignmentNativeInterop.Summary summary)
        {
            result.DetectionMilliseconds = summary.DetectionMs;
            result.MapGenerationMilliseconds = summary.MapMs;
            result.RemapMilliseconds = summary.RemapMs;
            result.LeaveOneOutMedianMm = summary.LooMedian;
            result.LeaveOneOutMaximumMm = summary.LooMaximum;
            result.PeakTemporaryBufferBytes = checked((long)summary.TemporaryBytes);
            result.PeakWorkingSetBytes = Math.Max(result.PeakWorkingSetBytes, GetPeakWorkingSetBytes());
        }

        private static long GetPeakWorkingSetBytes()
        {
            try { using (Process process = Process.GetCurrentProcess()) return process.PeakWorkingSet64; }
            catch { return 0; }
        }
    }
}
