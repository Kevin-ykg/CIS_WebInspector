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

        internal static PatchDefectResult Detect(Mat alphaImg, Mat cisImg, int cisBaseThresh,
            AppConfig config, string outputPath, string cisOutputPath, string edgeExclusionOutputPath,
            PatchSiftWorker alignmentWorker, string partId, IAppLogger logger)
        {
            if (alignmentWorker == null) throw new ArgumentNullException(nameof(alignmentWorker));
            logger = logger ?? NullAppLogger.Instance;
            // 同一 worker 的调用与 Dispose 串行。Mat 的真实像素由调用方持有，保持至同步调用返回。
            lock (alignmentWorker.SyncRoot)
            {
                var alpha = PatchNativeInterop.Image.Borrow(alphaImg);
                var cis = PatchNativeInterop.Image.Borrow(cisImg);
                int outputs = (!string.IsNullOrEmpty(outputPath) ? 1 : 0) |
                    (!string.IsNullOrEmpty(edgeExclusionOutputPath) ? 2 : 0) |
                    (!string.IsNullOrEmpty(cisOutputPath) && config.SaveCroppedImages ? 4 : 0);
                var snapshot = PatchNativeInterop.Config.From(config, cisBaseThresh, outputs);
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
                    var defects = new PatchNativeInterop.Defect[checked((int)summary.DefectCount)];
                    PatchNativeInterop.Check(PatchNativeInterop.cis_patch_result_defects(native, defects,
                        (uint)defects.Length, error, (uint)error.Length), error);
                    var result = new PatchDefectResult
                    {
                        IsPass = summary.Pass != 0, InnerDefectCount = summary.InnerCount,
                        OuterDefectCount = summary.OuterCount, FineLineBreakCount = summary.FineCount,
                        MaxAreaInnerMm2 = summary.MaxInner, MaxAreaOuterMm2 = summary.MaxOuter,
                        MaxFineLineBreakLengthMm = summary.MaxLength, MaxFineLineBreakWidthMm = summary.MaxWidth
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
                            default: throw new InvalidOperationException("原生返回未知缺陷类型。");
                        }
                    }
                    string diagnostic = PatchNativeInterop.ReadLog(native);
                    if (!string.IsNullOrEmpty(diagnostic))
                        AppLog.Diagnostic(logger, "[LocalAlign] " +
                            (string.IsNullOrWhiteSpace(partId) ? "<unknown>" : partId) + " " + diagnostic);

                    // 保存开关仅决定请求哪些诊断图，不影响配准分支和缺陷阈值。
                    // 所有保存失败只写日志，不能把磁盘错误当作产品缺陷。
                    try
                    {
                        if ((outputs & 4) != 0)
                            using (Mat image = PatchNativeInterop.ReadImage(native, 5, error))
                                Cv2.ImWrite(cisOutputPath, image, AlignedPatchJpegParameters);
                    }
                    catch (Exception ex) { AppLog.Write(logger, "[LocalAlign][WARN] 保存二值化对齐图失败: " + ex.Message); }
                    try
                    {
                        if ((outputs & 2) != 0)
                            using (Mat image = PatchNativeInterop.ReadImage(native, 4, error))
                                SaveEdgeExclusionMask(image, alphaImg.Size(), edgeExclusionOutputPath, logger);
                    }
                    catch (Exception ex) { AppLog.Write(logger, "[PatchDefectDetector][WARN] 读取屏蔽图失败: " + ex.Message); }
                    try
                    {
                        if ((outputs & 1) != 0)
                            using (Mat a = PatchNativeInterop.ReadImage(native, 0, error))
                            using (Mat c = PatchNativeInterop.ReadImage(native, 1, error))
                            using (Mat i = PatchNativeInterop.ReadImage(native, 2, error))
                            using (Mat o = PatchNativeInterop.ReadImage(native, 3, error))
                                SaveVisualization(a, c, i, o, innerWork, outerWork, fineWork, result.IsPass, outputPath, logger);
                    }
                    catch (Exception ex) { AppLog.Write(logger, "[PatchDefectDetector][WARN] 读取缺陷图失败: " + ex.Message); }
                    return result;
                }
            }
        }
    }
}
