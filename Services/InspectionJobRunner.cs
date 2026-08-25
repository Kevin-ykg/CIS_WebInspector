using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 执行一次完整的拼接段检测作业：排版日志解析 → TIFF/Alpha 加载 → 全局对准 →
    /// 零件裁切与缺陷检测 → 汇总输出。该类不依赖 WPF，UI 只负责启动、取消和展示结果。
    /// </summary>
    public sealed class InspectionJobRunner : IInspectionJobRunner
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
                throw new ArgumentNullException(nameof(stitchedResult));

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
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Failed,
                        InspectionJobIssueCode.InvalidConfiguration,
                        "[缺陷流水线] 未加载有效配置，终止流水线。");

                // 结束二维码是排版日志的业务主键，也是底部 Mark 的几何锚点。
                string qrCode = stitchedResult.EndQrText;
                if (string.IsNullOrEmpty(qrCode))
                    return CreateEarlyResult(
                        log,
                        InspectionJobStatus.Failed,
                        InspectionJobIssueCode.MissingEndQrCode,
                        "[缺陷流水线] 未找到有效的结束二维码，终止流水线。");

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
                        Log(log, $"[白墨检测] 诊断：{whiteInkDiagnostic}");
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

                if (tiffMat.Channels() == 4)
                {
                    int h = tiffMat.Height;
                    int w = tiffMat.Width;
                    // 只提取后续真正需要的 Alpha 通道。Cv2.Split 会额外创建 B/G/R/A 四张
                    // 全尺寸 Mat，对约 300 MB TIFF 会显著抬高峰值内存。
                    alphaMask = new Mat();
                    Cv2.ExtractChannel(tiffMat, alphaMask, 3);

                    // Alpha 是后续零件设计轮廓的判定依据，必须在 TIFF 合成白底前独立保留。
                    int nonZero = Cv2.CountNonZero(alphaMask);
                    Log(log, $"  提取Alpha通道: 非零像素={nonZero}, 覆盖率={nonZero * 100.0 / (h * w):F1}%");

                    // 设计图的透明区域按白底合成，保证其灰度语义与白色膜片背景一致。
                    Mat composited = new Mat(h, w, MatType.CV_8UC3);
                    try
                    {
                        var parallelOptions = new ParallelOptions
                        {
                            CancellationToken = cancellationToken
                        };

                        unsafe
                        {
                            Parallel.For(0, h, parallelOptions, row =>
                            {
                                byte* srcRow = (byte*)tiffMat.Ptr(row);
                                byte* dstRow = (byte*)composited.Ptr(row);

                                for (int col = 0; col < w; col++)
                                {
                                    byte sb = srcRow[0];
                                    byte sg = srcRow[1];
                                    byte sr = srcRow[2];
                                    byte sa = srcRow[3];

                                    if (sa == 255)
                                    {
                                        dstRow[0] = sb;
                                        dstRow[1] = sg;
                                        dstRow[2] = sr;
                                    }
                                    else if (sa == 0)
                                    {
                                        dstRow[0] = 255;
                                        dstRow[1] = 255;
                                        dstRow[2] = 255;
                                    }
                                    else
                                    {
                                        float a = sa * (1f / 255f);
                                        float inverseAlpha = 1f - a;
                                        dstRow[0] = (byte)(sb * a + 255f * inverseAlpha);
                                        dstRow[1] = (byte)(sg * a + 255f * inverseAlpha);
                                        dstRow[2] = (byte)(sr * a + 255f * inverseAlpha);
                                    }

                                    srcRow += 4;
                                    dstRow += 3;
                                }
                            });
                        }

                        tiffMat.Dispose();
                        tiffMat = composited;
                        composited = null;
                    }
                    finally
                    {
                        composited?.Dispose();
                    }
                }
                else
                {
                    Log(log, "  [WARN] TIFF无Alpha通道，将使用统一阈值检测。");
                }

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

                        int passCount = 0;
                        int failCount = 0;
                        int processingErrorCount = 0;
                        long defectDetailLogTicks = 0;
                        int loggedDefectDetailCount = 0;
                        foreach (PatchDefectResult defectResult in defectTaskResult.Results)
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
                                passCount++;
                            else
                                failCount++;

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
                                Log(log, defectDetail);
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

                        int totalParts = defectTaskResult.Results.Count;
                        string outputSummary = config.SaveCroppedImages
                            ? $"图像结果保存在: {outputDirectory}"
                            : "图像保存已关闭，检测结果仅用于界面显示和日志汇总";
                        string completedMessage =
                            $"[缺陷流水线] 全部完成！共 {totalParts} 个零件 | 合格 {passCount} | " +
                            $"不合格 {failCount} | 处理异常 {processingErrorCount} | " +
                            $"全局对准={alignment.Mode}/{alignment.QualityStatus} | " +
                            $"检测={alignment.DetectionMilliseconds:F1}ms, 建图={alignment.MapGenerationMilliseconds:F1}ms, " +
                            $"变换={alignment.RemapMilliseconds:F1}ms | {outputSummary}";
                        Log(log, completedMessage);

                        return new InspectionJobResult
                        {
                            Status = processingErrorCount > 0
                                ? InspectionJobStatus.CompletedWithProcessingErrors
                                : InspectionJobStatus.Completed,
                            IssueCode = processingErrorCount > 0
                                ? InspectionJobIssueCode.PartProcessingError
                                : InspectionJobIssueCode.None,
                            Message = completedMessage,
                            GlobalImageBytes = defectTaskResult.GlobalImageBytes,
                            WhiteInkPreviewBytes = whiteInkPreviewBytes,
                            WhiteInkInspection = whiteInkInspection,
                            OutputDirectory = outputDirectory,
                            TotalParts = totalParts,
                            PassCount = passCount,
                            FailCount = failCount,
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
                    cisDataHandle.Free();
                alphaMask?.Dispose();
                tiffMat?.Dispose();
            }
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
                return string.Empty;

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
                return;

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
