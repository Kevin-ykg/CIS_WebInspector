using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 执行一次完整的拼接段检测作业：独立白墨检查 → 排版解析/Alpha 加载 → 全局对准 →
    /// 零件裁切与缺陷检测 → 汇总输出。该类不依赖 WPF，UI 只负责启动、取消和展示结果。
    /// 排版像素准备见 LayoutImage 分文件，日志/状态组装见 Reporting；本文件只编排阶段及资源边界。
    /// </summary>
    public sealed partial class InspectionJobRunner : IInspectionJobRunner
    {
        private readonly string _baseDirectory;
        private readonly IAppLogger _logger;

        public InspectionJobRunner(string baseDirectory = null, IAppLogger logger = null)
        {
            _logger = logger ?? NullAppLogger.Instance;
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
        }

        /// <summary>
        /// 同步执行一整段拼接图的检测。调用方通常在后台 Task 中运行；取消令牌在各阶段边界检查，
        /// 但正在执行的单次 OpenCV 原生调用不能被强制中断。
        /// </summary>
        public InspectionJobResult Run(
            StitchedImageResult stitchedResult,
            AppConfig config,
            CancellationToken cancellationToken)
        {
            if (stitchedResult == null)
            {
                throw new ArgumentNullException(nameof(stitchedResult));
            }

            Mat tiffMat = null;
            Mat alphaMask = null;
            Mat cisMat = null;
            GCHandle cisDataHandle = default(GCHandle);
            bool cisDataPinned = false;
            WhiteInkInspectionResult whiteInkInspection = WhiteInkInspectionResult.Disabled();
            byte[] whiteInkPreviewBytes = null;
            string outputDirectory = null;
            IAppLogger log = _logger;

            try
            {
                Log(log, "开始执行拼接段缺陷检测流水线...");
                cancellationToken.ThrowIfCancellationRequested();

                if (config == null)
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Failed,
                        InspectionJobIssueCode.InvalidConfiguration,
                        "[缺陷流水线] 未加载有效配置，终止流水线。");
                }

                // 结束二维码是排版日志的业务主键，也是底部 Mark 的几何锚点。
                string qrCode = stitchedResult.EndQrText;
                if (string.IsNullOrEmpty(qrCode))
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Failed,
                        InspectionJobIssueCode.MissingEndQrCode,
                        "[缺陷流水线] 未找到有效的结束二维码，终止流水线。");
                }

                // 白墨检查只依赖拼接图和结束二维码锚点，必须先于 Debug.log/TIFF 流水线执行。
                // 这样即使当前图库没有排版日志或 TIFF，供墨异常仍会产生日志、预览和 UI 告警。
                MatType cisType = stitchedResult.BitsPerPixel == 8 ? MatType.CV_8UC1 : MatType.CV_8UC3;
                // StitchedImageResult 在整个 Run 调用期间强引用且只读。让 Mat 借用固定数组可避免
                // 对超大拼接图再做一次完整 Clone；finally 必须先 Dispose Mat，再解除固定。
                cisDataHandle = GCHandle.Alloc(stitchedResult.Data, GCHandleType.Pinned);
                cisDataPinned = true;
                cisMat = Mat.FromPixelData(
                    stitchedResult.Height,
                    stitchedResult.Width,
                    cisType,
                    cisDataHandle.AddrOfPinnedObject(),
                    stitchedResult.Stride);

                var qrAnchor = new CisQrAnchor
                {
                    CenterX = stitchedResult.EndQrCenterX,
                    GlobalCenterY = stitchedResult.EndQrGlobalY,
                    SegmentStartGlobalY = stitchedResult.SegmentStartGlobalY,
                    PixelWidth = stitchedResult.EndQrPixelWidth,
                    PixelHeight = stitchedResult.EndQrPixelHeight
                };
                MarkAlignmentOptions alignmentOptions = MarkAlignmentOptions.FromConfig(config);
                Log(
                    log,
                    $"[白墨检测] 开关={(alignmentOptions.EnableWhiteInkInspection ? "开启" : "关闭")}，" +
                    $"正常灰度基准={alignmentOptions.WhiteInkNormalGray:F1}，" +
                    $"拉丝标准差阈值={alignmentOptions.WhiteInkStreakStdDevThreshold:F1}。");

                if (alignmentOptions.EnableWhiteInkInspection)
                {
                    whiteInkInspection = ImageAligner.InspectBottomWhiteInk(
                        cisMat, qrAnchor, alignmentOptions, out string whiteInkDiagnostic);
                    LogWhiteInk(log, whiteInkInspection);
                    try
                    {
                        whiteInkPreviewBytes = ImageAligner.CreateWhiteInkInspectionPreview(
                            cisMat, whiteInkInspection);
                        // 预览字节始终保留给 UI；只有图像总开关开启时才写入裁切结果目录。
                        if (config.SaveCroppedImages &&
                            whiteInkPreviewBytes != null &&
                            whiteInkPreviewBytes.Length > 0)
                        {
                            outputDirectory = Path.Combine(
                                _baseDirectory,
                                config.CroppedOutputDir,
                                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                            Directory.CreateDirectory(outputDirectory);
                            string previewPath = Path.Combine(
                                outputDirectory, "WhiteInk_BottomMarks_Preview.jpg");
                            File.WriteAllBytes(previewPath, whiteInkPreviewBytes);
                            Log(log, $"[白墨检测] Bottom Mark 标注预览已保存：{previewPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(log, $"[白墨检测][WARN] 生成 Bottom Mark 标注预览失败：{ex.Message}");
                    }

                    if (!string.IsNullOrWhiteSpace(whiteInkDiagnostic))
                    {
                        Log(log, $"[白墨检测] 诊断：{whiteInkDiagnostic}");
                    }
                }

                // 在线生产可能尚未配置排版资料。缺少 Debug.log 或 TIFF 目录属于可预期状态：
                // 白墨检查仍保留结果，零件级检测跳过，调用方继续采集而不是抛异常或弹窗阻塞。
                if (string.IsNullOrWhiteSpace(config.DebugLogPath))
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Skipped,
                        InspectionJobIssueCode.DebugLogNotConfigured,
                        "[缺陷流水线][WARN] 未配置 Debug.log，已跳过本拼接段的排版解析、图像对准和零件缺陷检测；本次作业正常结束，不阻塞程序运行。",
                        whiteInkInspection,
                        whiteInkPreviewBytes,
                        outputDirectory);
                }

                if (!File.Exists(config.DebugLogPath))
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Skipped,
                        InspectionJobIssueCode.DebugLogNotFound,
                        $"[缺陷流水线][WARN] Debug.log 不存在：{config.DebugLogPath}；已跳过本拼接段的零件缺陷检测，本次作业正常结束。",
                        whiteInkInspection,
                        whiteInkPreviewBytes,
                        outputDirectory);
                }

                if (string.IsNullOrWhiteSpace(config.TiffImageDir))
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Skipped,
                        InspectionJobIssueCode.LayoutImageDirectoryNotConfigured,
                        "[缺陷流水线][WARN] 未配置 TIFF 原图目录，已跳过本拼接段的图像对准和零件缺陷检测；本次作业正常结束，不阻塞程序运行。",
                        whiteInkInspection,
                        whiteInkPreviewBytes,
                        outputDirectory);
                }

                if (!Directory.Exists(config.TiffImageDir))
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Skipped,
                        InspectionJobIssueCode.LayoutImageDirectoryNotFound,
                        $"[缺陷流水线][WARN] TIFF 原图目录不存在：{config.TiffImageDir}；已跳过本拼接段的零件缺陷检测，本次作业正常结束。",
                        whiteInkInspection,
                        whiteInkPreviewBytes,
                        outputDirectory);
                }

                Log(log, $"正在解析 Debug.log，目标二维码: {qrCode} ...");
                var layoutInfo = DebugLogParser.ParseForQrCode(
                    config.DebugLogPath,
                    qrCode,
                    config.TiffImageDir,
                    log);
                if (layoutInfo == null)
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Skipped,
                        InspectionJobIssueCode.LayoutRecordNotFound,
                        "[缺陷流水线][WARN] Debug.log 中未找到当前二维码对应的排版记录；已跳过本拼接段的零件缺陷检测，本次作业正常结束，白墨检查结果不受影响。",
                        whiteInkInspection,
                        whiteInkPreviewBytes,
                        outputDirectory);
                }

                Log(log, $"成功解析排版日志，原图: {layoutInfo.TiffFileName}，共 {layoutInfo.Parts.Count} 个有效零件。");
                cancellationToken.ThrowIfCancellationRequested();

                Log(log, "正在加载 TIFF 原图...");
                if (!File.Exists(layoutInfo.TiffFullPath))
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Skipped,
                        InspectionJobIssueCode.LayoutImageNotFound,
                        $"[缺陷流水线][WARN] 无法找到 TIFF 原图文件：{layoutInfo.TiffFullPath}；已跳过本拼接段的零件缺陷检测，本次作业正常结束。",
                        whiteInkInspection,
                        whiteInkPreviewBytes,
                        outputDirectory);
                }

                tiffMat = Cv2.ImRead(layoutInfo.TiffFullPath, ImreadModes.Unchanged);
                if (tiffMat.Empty())
                {
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Failed,
                        InspectionJobIssueCode.LayoutImageLoadFailed,
                        "[缺陷流水线] TIFF 图像加载失败。",
                        whiteInkInspection,
                        whiteInkPreviewBytes,
                        outputDirectory);
                }

                PrepareLayoutImage(ref tiffMat, ref alphaMask, log, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                Log(log, "正在计算图像对齐变换矩阵...");

                int optimalThreshold = 127;
                // 独立 Bottom 检查已完成；全局对准阶段不再重复做同一组灰度统计。
                alignmentOptions.EnableWhiteInkInspection = false;

                using (AlignmentResult alignment = ImageAligner.ComputeTransform(
                           cisMat,
                           tiffMat,
                           qrAnchor,
                           alignmentOptions,
                           out optimalThreshold,
                           out string alignmentDiagnostic))
                {
                    // AlignmentResult 持有 H0 及逆矩阵，using 保证 OpenCV 非托管矩阵在本作业结束时释放。
                    if (alignment?.GlobalTransform == null || alignment.GlobalTransform.Empty())
                    {
                        return CreateEarlyResult(
                            log,
                            InspectionJobStatus.Failed,
                            InspectionJobIssueCode.AlignmentFailed,
                            $"[缺陷流水线] 图像对齐失败：{alignmentDiagnostic}",
                            whiteInkInspection,
                            whiteInkPreviewBytes,
                            outputDirectory);
                    }

                    Log(log,
                        $"变换矩阵计算成功！模式={alignment.Mode}, 质量={alignment.QualityStatus}, " +
                        $"自动最佳二值化阈值={optimalThreshold}，{alignmentDiagnostic}");

                    // 对准结果生成后再保存诊断图：这里只读取最终 Mark 对应点和侧边控制点，
                    // 不重新检测、不修改矩阵。SaveCroppedImages 是所有裁切结果图像的落盘总开关。
                    if (config.SaveCroppedImages)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(outputDirectory))
                            {
                                outputDirectory = Path.Combine(
                                    _baseDirectory,
                                    config.CroppedOutputDir,
                                    DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                            }

                            var markPreviewPaths = ImageAligner.SaveAlignmentMarkPreviews(
                                cisMat,
                                tiffMat,
                                alignment,
                                outputDirectory);
                            Log(
                                log,
                                "[全局对准诊断] Mark 标注图已保存：" +
                                string.Join("；", markPreviewPaths));
                        }
                        catch (Exception ex)
                        {
                            // 诊断图保存失败只记日志，不能阻断正式 Warp 与缺陷检测。
                            Log(log, $"[全局对准诊断][WARN] Mark 标注图保存失败：{ex.Message}");
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Log(log, "正在将 CIS 图像变换到 TIFF 空间...");
                    using (Mat cisWarped = ImageAligner.WarpToTiffSpace(cisMat, alignment, tiffMat.Size()))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // 全局 Mark 检测得到的最佳阈值提供曝光基线，再叠加缺陷检测专用偏移并限幅。
                        int finalCisThreshold = Math.Max(
                            0,
                            Math.Min(255, optimalThreshold + config.DefectCisThreshOffset));
                        Log(log, $"正在按排版坐标裁切零件小图并执行缺陷检测 (应用 CIS 阈值={finalCisThreshold})...");

                        // Debug.log 中的零件位置是毫米；这里统一换算成 TIFF 目标空间像素。
                        double scale = config.LayoutDpi / 25.4;
                        if (string.IsNullOrWhiteSpace(outputDirectory))
                        {
                            outputDirectory = Path.Combine(
                                _baseDirectory,
                                config.CroppedOutputDir,
                                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                        }
                        var defectTaskResult = PatchCropper.CropAndSave(
                            cisWarped,
                            tiffMat,
                            alphaMask,
                            layoutInfo.Parts,
                            outputDirectory,
                            scale,
                            config.LayoutOriginXmm,
                            config.LayoutOriginYmm,
                            finalCisThreshold,
                            config,
                            log);

                        var (passCount, failCount, processingErrorCount, reviewCount) = LogPartResults(
                            defectTaskResult.Results, config, log, cancellationToken);

                        int totalParts = defectTaskResult.Results.Count;
                        string productStatus = failCount > 0 ? "NG" : totalParts == 0 || processingErrorCount > 0
                            ? "待复核" : "Pass";
                        string outputSummary = config.SaveCroppedImages
                            ? $"图像结果保存在: {outputDirectory}"
                            : "图像保存已关闭，检测结果仅用于界面显示和日志汇总";
                        string completedMessage =
                            $"[缺陷流水线] 全部完成！产品结论 {productStatus} | 共 {totalParts} 个零件 | 合格 {passCount} | " +
                            $"不合格 {failCount} | 配准待复核 {reviewCount} | 处理异常 {processingErrorCount} | " +
                            $"全局对准={alignment.Mode}/{alignment.QualityStatus} | " +
                            $"检测={alignment.DetectionMilliseconds:F1}ms, 建图={alignment.MapGenerationMilliseconds:F1}ms, " +
                            $"变换={alignment.RemapMilliseconds:F1}ms | {outputSummary}";
                        Log(log, completedMessage);

                        return new InspectionJobResult
                        {
                            Status = processingErrorCount > 0 ? InspectionJobStatus.CompletedWithProcessingErrors
                                : reviewCount > 0 ? InspectionJobStatus.CompletedWithReview
                                : InspectionJobStatus.Completed,
                            IssueCode = processingErrorCount > 0 ? InspectionJobIssueCode.PartProcessingError
                                : reviewCount > 0 ? InspectionJobIssueCode.PartAlignmentReview
                                : InspectionJobIssueCode.None,
                            Message = completedMessage,
                            GlobalImageBytes = defectTaskResult.GlobalImageBytes,
                            WhiteInkPreviewBytes = whiteInkPreviewBytes,
                            WhiteInkInspection = whiteInkInspection,
                            OutputDirectory = outputDirectory,
                            TotalParts = totalParts,
                            PassCount = passCount,
                            FailCount = failCount,
                            AlignmentReviewCount = reviewCount,
                            ProcessingErrorCount = processingErrorCount,
                            AlignmentMode = alignment.Mode,
                            AlignmentQualityStatus = alignment.QualityStatus,
                            DetectionMilliseconds = alignment.DetectionMilliseconds,
                            MapGenerationMilliseconds = alignment.MapGenerationMilliseconds,
                            RemapMilliseconds = alignment.RemapMilliseconds
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                const string message = "[缺陷流水线] 作业已取消。";
                Log(log, message);
                return new InspectionJobResult
                {
                    Status = InspectionJobStatus.Cancelled,
                    IssueCode = InspectionJobIssueCode.None,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                string message = $"[缺陷流水线] 执行发生严重异常: {ex.Message}\n{ex.StackTrace}";
                Log(log, message);
                return new InspectionJobResult
                {
                    Status = InspectionJobStatus.Failed,
                    IssueCode = InspectionJobIssueCode.UnhandledException,
                    Message = message,
                    WhiteInkInspection = whiteInkInspection,
                    WhiteInkPreviewBytes = whiteInkPreviewBytes,
                    OutputDirectory = outputDirectory
                };
            }
            finally
            {
                // cisMat 仅拥有 Mat 头，像素由固定的 stitchedResult.Data 持有；必须先释放 Mat 再解除固定。
                // TIFF、Alpha 等本作业创建的 Mat 则由本方法完整拥有。
                cisMat?.Dispose();
                if (cisDataPinned)
                {
                    cisDataHandle.Free();
                }

                alphaMask?.Dispose();
                tiffMat?.Dispose();
            }
        }
    }
}
