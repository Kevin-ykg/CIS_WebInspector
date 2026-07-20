using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 执行一次完整的离线检测作业：排版日志解析 → TIFF/Alpha 加载 → 全局对准 →
    /// 零件裁切与缺陷检测 → 汇总输出。该类不依赖 WPF，UI 只负责启动、取消和展示结果。
    /// </summary>
    public sealed class InspectionJobRunner
    {
        private readonly string _baseDirectory;

        public InspectionJobRunner(string baseDirectory = null)
        {
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
            Action<string> log,
            CancellationToken cancellationToken)
        {
            if (stitchedResult == null)
                throw new ArgumentNullException(nameof(stitchedResult));

            Mat tiffMat = null;
            Mat alphaMask = null;
            Mat cisMat = null;

            try
            {
                Log(log, "开始执行离线缺陷检测流水线...");
                cancellationToken.ThrowIfCancellationRequested();

                if (config == null)
                    return Failure(log, "[缺陷流水线] 未加载有效配置，终止流水线。");

                // 结束二维码是排版日志的业务主键，也是底部 Mark 的几何锚点。
                string qrCode = stitchedResult.EndQrText;
                if (string.IsNullOrEmpty(qrCode))
                    return Failure(log, "[缺陷流水线] 未找到有效的结束二维码，终止流水线。");

                Log(log, $"正在解析 Debug.log，目标二维码: {qrCode} ...");
                var layoutInfo = DebugLogParser.ParseForQrCode(config.DebugLogPath, qrCode, config.TiffImageDir);
                if (layoutInfo == null)
                    return Failure(log, "[缺陷流水线] 解析失败或未找到对应的排版日志。");

                Log(log, $"成功解析排版日志，原图: {layoutInfo.TiffFileName}，共 {layoutInfo.Parts.Count} 个有效零件。");
                cancellationToken.ThrowIfCancellationRequested();

                Log(log, "正在加载 TIFF 原图...");
                if (!File.Exists(layoutInfo.TiffFullPath))
                    return Failure(log, $"[缺陷流水线] 无法找到 TIFF 原图文件: {layoutInfo.TiffFullPath}");

                tiffMat = Cv2.ImRead(layoutInfo.TiffFullPath, ImreadModes.Unchanged);
                if (tiffMat.Empty())
                    return Failure(log, "[缺陷流水线] TIFF 图像加载失败。");

                if (tiffMat.Channels() == 4)
                {
                    int h = tiffMat.Height;
                    int w = tiffMat.Width;
                    Mat[] channels = null;
                    try
                    {
                        channels = Cv2.Split(tiffMat);
                        alphaMask = channels[3].Clone();
                    }
                    finally
                    {
                        if (channels != null)
                        {
                            foreach (Mat channel in channels)
                                channel?.Dispose();
                        }
                    }

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

                MatType cisType = stitchedResult.BitsPerPixel == 8 ? MatType.CV_8UC1 : MatType.CV_8UC3;
                GCHandle handle = GCHandle.Alloc(stitchedResult.Data, GCHandleType.Pinned);
                try
                {
                    // FromPixelData 只是托管数组上的非拥有视图；立即 Clone，才能在解除固定后继续安全使用。
                    cisMat = Mat.FromPixelData(
                        stitchedResult.Height,
                        stitchedResult.Width,
                        cisType,
                        handle.AddrOfPinnedObject(),
                        stitchedResult.Stride).Clone();
                }
                finally
                {
                    handle.Free();
                }

                int optimalThreshold = 127;
                var qrAnchor = new CisQrAnchor
                {
                    CenterX = stitchedResult.EndQrCenterX,
                    GlobalCenterY = stitchedResult.EndQrGlobalY,
                    SegmentStartGlobalY = stitchedResult.SegmentStartGlobalY,
                    PixelWidth = stitchedResult.EndQrPixelWidth,
                    PixelHeight = stitchedResult.EndQrPixelHeight
                };
                MarkAlignmentOptions alignmentOptions = MarkAlignmentOptions.FromConfig(config);

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
                        return Failure(log, $"[缺陷流水线] 图像对齐失败：{alignmentDiagnostic}");

                    Log(log,
                        $"变换矩阵计算成功！模式={alignment.Mode}, 质量={alignment.QualityStatus}, " +
                        $"自动最佳二值化阈值={optimalThreshold}，{alignmentDiagnostic}");

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
                        string outputDirectory = Path.Combine(
                            _baseDirectory,
                            config.CroppedOutputDir,
                            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
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
                            message => Log(log, message));

                        int passCount = 0;
                        int failCount = 0;
                        foreach (PatchDefectResult defectResult in defectTaskResult.Results)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (defectResult.IsPass)
                                passCount++;
                            else
                                failCount++;

                            string status = defectResult.IsPass ? "✓ Pass" : "✗ FAIL";
                            string fineLineSummary = defectResult.FineLineBreakCount > 0
                                ? $" | 细线断裂: {defectResult.FineLineBreakCount}个 " +
                                  $"(最长 {defectResult.MaxFineLineBreakLengthMm:F2}mm)"
                                : string.Empty;
                            Log(log,
                                $"  [{status}] {defectResult.PartId} — 内部缺陷: {defectResult.InnerDefectCount}个 " +
                                $"(最大 {defectResult.MaxAreaInner}px²) | 外部缺陷: {defectResult.OuterDefectCount}个 " +
                                $"(最大 {defectResult.MaxAreaOuter}px²){fineLineSummary}");
                        }

                        int totalParts = defectTaskResult.Results.Count;
                        string completedMessage =
                            $"[缺陷流水线] 全部完成！共 {totalParts} 个零件 | 合格 {passCount} | " +
                            $"不合格 {failCount} | 全局对准={alignment.Mode}/{alignment.QualityStatus} | " +
                            $"检测={alignment.DetectionMilliseconds:F1}ms, 建图={alignment.MapGenerationMilliseconds:F1}ms, " +
                            $"变换={alignment.RemapMilliseconds:F1}ms | 结果保存在: {outputDirectory}";
                        Log(log, completedMessage);

                        return new InspectionJobResult
                        {
                            Succeeded = true,
                            Message = completedMessage,
                            GlobalImageBytes = defectTaskResult.GlobalImageBytes,
                            OutputDirectory = outputDirectory,
                            TotalParts = totalParts,
                            PassCount = passCount,
                            FailCount = failCount,
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
                return new InspectionJobResult { Cancelled = true, Message = message };
            }
            catch (Exception ex)
            {
                string message = $"[缺陷流水线] 执行发生严重异常: {ex.Message}\n{ex.StackTrace}";
                Log(log, message);
                return new InspectionJobResult { Message = message };
            }
            finally
            {
                // 本方法拥有所有在作业内创建/读取的 Mat；零件检测完成后按依赖反向释放。
                cisMat?.Dispose();
                alphaMask?.Dispose();
                tiffMat?.Dispose();
            }
        }

        /// <summary>统一记录可预期失败并返回未成功结果，避免各阶段抛出无业务语义的异常。</summary>
        private static InspectionJobResult Failure(Action<string> log, string message)
        {
            Log(log, message);
            return new InspectionJobResult { Message = message };
        }

        /// <summary>日志回调异常不得反向中断检测流水线。</summary>
        private static void Log(Action<string> log, string message)
        {
            if (log == null)
                return;

            try
            {
                log(message);
            }
            catch
            {
                // 日志失败不应中断视觉处理作业。
            }
        }
    }
}
