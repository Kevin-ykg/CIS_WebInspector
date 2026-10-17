using System;
using System.Collections.Generic;
using System.IO;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    // 全局预览/落盘；与裁切、并行检测使用同一坐标契约，显示比例不参与算法。
    public static partial class PatchCropper
    {
        private static readonly ImageEncodingParam[] GlobalPreviewJpegParameters =
        {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 85)
        };

        /// <summary>
        /// 输入与 GlobalRoi 均在纵向翻转后的排版空间；标注只画到缩小副本。
        /// 返回编码后的独立 JPEG 字节，不把 Mat 或父图引用交给 UI。
        /// </summary>
        private static (string GlobalImagePath, byte[] GlobalImageBytes) CreateGlobalPreview(
            Mat flippedCis, Mat flippedTiff, List<PatchDefectResult> results,
            string outputDir, AppConfig config, IAppLogger logger)
        {
            // --- 全局缺陷可视化保存与显示 ---
            // 下列缩放和绘框仅服务于 UI/报告，不回流到检测算法，避免预览分辨率影响判定。
            // UI 仍使用 TIFF + CIS 的左右对照图；磁盘则分别保存 TIFF 基准图和带标注 CIS 图，
            // 方便直接打开、核对或用于报告，不必再从横向合成图中人工裁切。
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
                    {
                        Cv2.Resize(flippedCis, cisSmall, new Size(), displayScale, displayScale, InterpolationFlags.Nearest);
                    }
                    else
                    {
                        flippedCis.CopyTo(cisSmall);
                    }

                    if (cisSmall.Channels() == 1)
                    {
                        Cv2.CvtColor(cisSmall, cisCanvas, ColorConversionCodes.GRAY2BGR);
                    }
                    else
                    {
                        cisSmall.CopyTo(cisCanvas);
                    }

                    int partThick = Math.Max(2, (int)(5 * displayScale));
                    int defectThick = Math.Max(2, (int)(9 * displayScale));
                    foreach (PatchDefectResult res in results)
                    {
                        Rect scaledRoi = new Rect(
                            (int)(res.GlobalRoi.X * displayScale),
                            (int)(res.GlobalRoi.Y * displayScale),
                            (int)(res.GlobalRoi.Width * displayScale),
                            (int)(res.GlobalRoi.Height * displayScale));
                        // 工程异常使用黄色，与产品 NG 的红色分开，避免全局图把算法失败误报成缺陷。
                        Scalar partColor = res.HasProcessingError
                            ? new Scalar(0, 255, 255)
                            : (res.IsPass ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255));
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

                        // 与零件级缺陷图保持同一颜色约定：细线断裂使用洋红色，
                        // 不再借用内部缺陷的橙色矩形，便于从全局图直接判断缺陷类型。
                        foreach (Rect rect in res.FineLineBreakRects)
                        {
                            Rect scaledDefectRect = new Rect(
                                (int)((res.GlobalRoi.X + rect.X) * displayScale),
                                (int)((res.GlobalRoi.Y + rect.Y) * displayScale),
                                (int)(rect.Width * displayScale),
                                (int)(rect.Height * displayScale));
                            Cv2.Rectangle(cisCanvas, scaledDefectRect, new Scalar(255, 0, 255), defectThick);
                        }
                    }

                    if (displayScale < 1.0)
                    {
                        Cv2.Resize(flippedTiff, tiffSmall, new Size(), displayScale, displayScale, InterpolationFlags.Nearest);
                    }
                    else
                    {
                        flippedTiff.CopyTo(tiffSmall);
                    }

                    if (tiffSmall.Channels() == 1)
                    {
                        Cv2.CvtColor(tiffSmall, tiffCanvas, ColorConversionCodes.GRAY2BGR);
                    }
                    else if (tiffSmall.Channels() == 4)
                    {
                        Cv2.CvtColor(tiffSmall, tiffCanvas, ColorConversionCodes.BGRA2BGR);
                    }
                    else
                    {
                        tiffSmall.CopyTo(tiffCanvas);
                    }

                    if (tiffCanvas.Channels() == 3 && cisCanvas.Channels() == 3)
                    {
                        if (config.SaveCroppedImages && config.SaveDefectResultImages)
                        {
                            string tiffReferencePath = Path.Combine(outputDir, "GlobalTiffReference.jpg");
                            string cisDefectPath = Path.Combine(outputDir, "GlobalCisDefectResult.jpg");

                            Cv2.ImWrite(tiffReferencePath, tiffCanvas, GlobalPreviewJpegParameters);
                            Cv2.ImWrite(cisDefectPath, cisCanvas, GlobalPreviewJpegParameters);

                            // 兼容现有返回值：全局结果路径指向最有诊断价值的带标注 CIS 图。
                            combinedPath = cisDefectPath;
                            AppLog.Write(
                                logger,
                                $"[PatchCropper] 全局结果已分开保存：TIFF={tiffReferencePath}；CIS={cisDefectPath}");
                        }

                        // 左右合成图只编码到内存，继续供主界面“全局缺陷图”页签对照显示，
                        // 不再写入 GlobalDefectResult.jpg，避免磁盘结果含义不清。
                        using (var combined = new Mat())
                        {
                            Cv2.HConcat(new[] { tiffCanvas, cisCanvas }, combined);
                            Cv2.ImEncode(".jpg", combined, out globalImageBytes, GlobalPreviewJpegParameters);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(logger, $"[PatchCropper][ERROR] 生成全局缺陷图异常: {ex.Message}");
            }
            return (combinedPath, globalImageBytes);
        }
    }
}
