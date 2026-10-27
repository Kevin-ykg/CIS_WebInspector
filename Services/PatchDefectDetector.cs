using System;
using System.Collections.Generic;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 零件算法的托管入口。局部配准、二值化、容差差分、边缘屏蔽、连通域、
    /// 细线骨架及物理测量均由 CISVisionCore.dll 完成。
    /// C# 保留批次调度、日志和诊断图保存，与 PatchCropper 的业务接口保持一致。
    /// </summary>
    public static partial class PatchDefectDetector
    {
        private static readonly ImageEncodingParam[] AlignedPatchJpegParameters =
            { new ImageEncodingParam(ImwriteFlags.JpegQuality, 95) };

        /// <summary>
        /// 同步检测一个零件。输入是同尺寸、原分辨率的 Alpha/CIS 搜索上下文；
        /// core 是该上下文中的理论检测区域，返回缺陷矩形相对 core 而不是搜索外框。
        /// 调用方负责输入 Mat 和 worker 的生命周期；本方法只拥有并释放原生结果句柄。
        /// 配准复核状态独立于缺陷 Pass/NG，保存失败也不改变检测结论。
        /// </summary>
        internal static PatchDefectResult Detect(Mat alphaImg, Mat cisImg, int cisBaseThresh,
            AppConfig config, string outputPath, string cisOutputPath, string edgeExclusionOutputPath,
            PatchSiftWorker alignmentWorker, string partId, IAppLogger logger, Rect core = default(Rect))
        {
            if (alignmentWorker == null)
            {
                throw new ArgumentNullException(nameof(alignmentWorker));
            }

            logger = logger ?? NullAppLogger.Instance;
            // 同一 worker 的调用与 Dispose 串行。Mat 的真实像素由调用方持有，保持至同步调用返回。
            lock (alignmentWorker.SyncRoot)
            {
                var alpha = PatchNativeInterop.Image.Borrow(alphaImg);
                var cis = PatchNativeInterop.Image.Borrow(cisImg);
                int outputs = (!string.IsNullOrEmpty(outputPath) ? 1 : 0) |
                    (!string.IsNullOrEmpty(edgeExclusionOutputPath) ? 2 : 0) |
                    (!string.IsNullOrEmpty(cisOutputPath) && config.SaveCroppedImages ? 4 : 0);
                var snapshot = PatchNativeInterop.Config.From(config, cisBaseThresh, outputs, core);
                byte[] error = alignmentWorker.Error;
                IntPtr pointer;
                try
                {
                    PatchNativeInterop.Check(PatchNativeInterop.cis_patch_detect(alignmentWorker.Handle,
                        ref alpha, ref cis, ref snapshot, out pointer, error, (uint)error.Length), error);
                }
                finally
                {
                    GC.KeepAlive(alphaImg);
                    GC.KeepAlive(cisImg);
                }

                // 结果句柄拥有所有原生输出；finally/SafeHandle 在读取或保存失败时仍释放。
                using (var native = new PatchNativeInterop.ResultHandle(pointer))
                {
                    var summary = new PatchNativeInterop.Summary { Size = 80 };
                    PatchNativeInterop.Check(PatchNativeInterop.cis_patch_result_summary(native, ref summary,
                        error, (uint)error.Length), error);
                    var alignment = new PatchNativeInterop.AlignmentInfo { Size = 72 };
                    PatchNativeInterop.Check(PatchNativeInterop.cis_patch_result_alignment(native, ref alignment,
                        error, (uint)error.Length), error);
                    var defects = new PatchNativeInterop.Defect[checked((int)summary.DefectCount)];
                    PatchNativeInterop.Check(PatchNativeInterop.cis_patch_result_defects(native, defects,
                        (uint)defects.Length, error, (uint)error.Length), error);
                    var result = new PatchDefectResult
                    {
                        IsPass = summary.Pass != 0,
                        InnerDefectCount = summary.InnerCount,
                        OuterDefectCount = summary.OuterCount,
                        FineLineBreakCount = summary.FineCount,
                        MaxAreaInnerMm2 = summary.MaxInner,
                        MaxAreaOuterMm2 = summary.MaxOuter,
                        MaxFineLineBreakLengthMm = summary.MaxLength,
                        MaxFineLineBreakWidthMm = summary.MaxWidth,
                        AlignmentStatus = (LocalAlignmentStatus)alignment.Status,
                        AlignmentModel = (LocalAlignmentModel)alignment.Model,
                        AlignmentReasonCode = alignment.Reason,
                        AlignmentMatches = alignment.Matches,
                        AlignmentInliers = alignment.Inliers,
                        AlignmentRmsOriginalPx = alignment.RmsOriginalPx,
                        AlignmentEdgeBeforeWorkPx = alignment.EdgeBeforeWorkPx,
                        AlignmentEdgeAfterWorkPx = alignment.EdgeAfterWorkPx,
                        AlignmentStabilityOriginalPx = alignment.LeaveOutChangeOriginalPx,
                        ProcessingStatus = alignment.Status == (int)LocalAlignmentStatus.AppliedDegraded ||
                                           alignment.Status == (int)LocalAlignmentStatus.Review ||
                                           alignment.Status == (int)LocalAlignmentStatus.Error
                            ? PatchProcessingStatus.AlignmentReview : PatchProcessingStatus.Completed
                    };
                    var innerWork = new List<Rect>();
                    var outerWork = new List<Rect>();
                    var fineWork = new List<Rect>();
                    foreach (var defect in defects)
                    {
                        // 仅转换已通过最终阈值的结果，不在 C# 再次面积筛选或合并分类。
                        switch (defect.Kind)
                        {
                            case 0:
                                result.InnerRects.Add(defect.OriginalRect);
                                result.InnerDefectMeasurements.Add(defect.Measurement);
                                innerWork.Add(defect.WorkRect);
                                break;
                            case 1:
                                result.OuterRects.Add(defect.OriginalRect);
                                result.OuterDefectMeasurements.Add(defect.Measurement);
                                outerWork.Add(defect.WorkRect);
                                break;
                            case 2:
                                result.FineLineBreakRects.Add(defect.OriginalRect);
                                result.FineLineBreakMeasurements.Add(defect.Measurement);
                                fineWork.Add(defect.WorkRect);
                                break;
                            default:
                                throw new InvalidOperationException("原生返回未知缺陷类型。");
                        }
                    }
                    string diagnostic = PatchNativeInterop.ReadLog(native);
                    if (!string.IsNullOrEmpty(diagnostic))
                    {
                        AppLog.Diagnostic(logger, "[LocalAlign] " +
                            (string.IsNullOrWhiteSpace(partId) ? "<unknown>" : partId) + " " + diagnostic);
                        if (result.NeedsAlignmentReview)
                        {
                            // 机器状态独立于诊断文本，不通过字符串包含关系推断可信度。
                            string imageSource = result.AlignmentStatus == LocalAlignmentStatus.AppliedDegraded
                                ? "已采用整体更优的局部矩阵正常检测，但局部区域质量降级"
                                : "已用全局对准图正常检测";
                            AppLog.Write(logger, $"[局部配准][需复核] {partId}：" +
                                $"原因 {result.AlignmentReason}({result.AlignmentReasonCode})，匹配 {result.AlignmentMatches}，" +
                                $"内点 {result.AlignmentInliers}；{imageSource}，建议复核结果。");
                        }
                    }

                    // 保存开关仅决定请求哪些诊断图，不影响配准分支和缺陷阈值。
                    // 所有保存失败只写日志，不能把磁盘错误当作产品缺陷。
                    try
                    {
                        if ((outputs & 4) != 0)
                        {
                            using (Mat image = PatchNativeInterop.ReadImage(native, 5, error))
                            {
                                Cv2.ImWrite(cisOutputPath, image, AlignedPatchJpegParameters);
                            }
                        }
                    }
                    catch (Exception ex) { AppLog.Write(logger, "[LocalAlign][WARN] 保存二值化对齐图失败: " + ex.Message); }
                    try
                    {
                        if ((outputs & 2) != 0)
                        {
                            using (Mat image = PatchNativeInterop.ReadImage(native, 4, error))
                            {
                                SaveEdgeExclusionMask(image, core.Width > 0 && core.Height > 0 ? core.Size : alphaImg.Size(), edgeExclusionOutputPath, logger);
                            }
                        }
                    }
                    catch (Exception ex) { AppLog.Write(logger, "[PatchDefectDetector][WARN] 读取屏蔽图失败: " + ex.Message); }
                    try
                    {
                        if ((outputs & 1) != 0)
                        {
                            using (Mat a = PatchNativeInterop.ReadImage(native, 0, error))
                            using (Mat c = PatchNativeInterop.ReadImage(native, 1, error))
                            using (Mat i = PatchNativeInterop.ReadImage(native, 2, error))
                            using (Mat o = PatchNativeInterop.ReadImage(native, 3, error))
                            {
                                SaveVisualization(a, c, i, o, innerWork, outerWork, fineWork, result.IsPass,
                                    result.AlignmentStatus, result.AlignmentModel, outputPath, logger);
                            }
                        }
                    }
                    catch (Exception ex) { AppLog.Write(logger, "[PatchDefectDetector][WARN] 读取缺陷图失败: " + ex.Message); }
                    return result;
                }
            }
        }
    }
}
