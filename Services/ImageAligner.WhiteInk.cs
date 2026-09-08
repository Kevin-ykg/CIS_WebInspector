using System;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    public static partial class ImageAligner
    {
        /// <summary>
        /// 不依赖 TIFF/排版日志的底排白墨检查。C++ 负责有限阈值扫描、低对比 Hough/共线约束、
        /// 圆心核心/背景环采样、20% 相对灰度分档及拉丝判定；C# 保留告警数据模型和预览入口。
        /// 完全无墨也必须走此独立入口，不能等待全局对准成功后才检查。
        /// </summary>
        public static WhiteInkInspectionResult InspectBottomWhiteInk(Mat cisMat, CisQrAnchor qrAnchor,
            MarkAlignmentOptions options, out string diagnostic)
        {
            diagnostic = null;
            if (options == null || !options.EnableWhiteInkInspection) return WhiteInkInspectionResult.Disabled();
            if (cisMat == null || cisMat.Empty()) diagnostic = "CIS 拼接图为空。";
            else if (qrAnchor == null) diagnostic = "第二个二维码高度无效，无法定位 Bottom 条带。";
            if (diagnostic != null) return Unable(diagnostic);

            try
            {
                using (var handle = AlignmentNativeInterop.Compute(cisMat, null, qrAnchor, options, true))
                {
                    var result = AlignmentNativeInterop.ReadWhite(handle, AlignmentNativeInterop.ReadSummary(handle));
                    diagnostic = result.Diagnostic;
                    return result; // 统计及采样点均已复制，释放原生句柄后仍可供界面/日志使用。
                }
            }
            catch (Exception ex)
            {
                diagnostic = "Bottom 白墨检查异常：" + ex.Message;
                return Unable(diagnostic); // 无法评估必须告警，不得把调用失败当成正常供墨。
            }
        }
        private static WhiteInkInspectionResult Unable(string diagnostic) =>
            new WhiteInkInspectionResult { Status = WhiteInkInspectionStatus.UnableToEvaluate, Diagnostic = diagnostic };
    }
}
