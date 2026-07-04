using System;
using System.Collections.Generic;
using System.IO;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    public class PatchCropper
    {
        /// <summary>
        /// 根据排版坐标，从对齐后的 CIS 图、TIFF 图和 Alpha 掩膜上裁切零件小图，
        /// 同时执行缺陷检测并返回结果列表。
        /// </summary>
        /// <param name="cisWarped">变换到 TIFF 空间的 CIS 实拍图</param>
        /// <param name="tiffMat">TIFF 原始排版图 (BGR 3通道，已 alpha 融合到白底)</param>
        /// <param name="alphaMask">从 TIFF 原图分离出的 Alpha 通道掩膜 (单通道)，可为 null</param>
        /// <param name="parts">零件排版坐标列表</param>
        /// <param name="outputDir">输出目录</param>
        /// <param name="scale">DPI换算系数 (例如 300/25.4)</param>
        /// <param name="originXmm">X 原点偏移 (mm)</param>
        /// <param name="originYmm">Y 原点偏移 (mm)</param>
        /// <param name="config">全局配置（用于裁切/检测参数）</param>
        /// <returns>每个零件的缺陷检测结果列表</returns>
        public static (List<PatchDefectResult> Results, string GlobalImagePath, byte[] GlobalImageBytes) CropAndSave(Mat cisWarped, Mat tiffMat, Mat alphaMask,
            List<PartLocation> parts, string outputDir, double scale, double originXmm, double originYmm,
            int cisBaseThresh, AppConfig config)
        {

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            int originX = (int)(originXmm * scale);
            int originY = (int)(originYmm * scale);

            int imgWidth = tiffMat.Width;
            int imgHeight = tiffMat.Height;

            int count = 0;

            // 翻转逻辑：readlog.cpp 中 flip(BW_org, BW_org, 0);
            Mat flippedCis = new Mat();
            Mat flippedTiff = new Mat();
            Cv2.Flip(cisWarped, flippedCis, FlipMode.X);
            Cv2.Flip(tiffMat, flippedTiff, FlipMode.X);

            Mat flippedAlpha = null;
            if (alphaMask != null)
            {
                flippedAlpha = new Mat();
                Cv2.Flip(alphaMask, flippedAlpha, FlipMode.X);
            }

            var resultsBag = new System.Collections.Concurrent.ConcurrentBag<PatchDefectResult>();

            System.Threading.Tasks.Parallel.ForEach(parts, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, part =>
            {
                if (part.HotInkTaskID != null && part.HotInkTaskID.Contains("QRCode"))
                {
                    return;
                }

                int x = (int)(originX + part.RelativeTopLeftX * scale);
                int y = (int)(originY + part.RelativeTopLeftY * scale);
                int w = (int)((part.RelativeBottomRightX - part.RelativeTopLeftX) * scale);
                int h = (int)((part.RelativeBottomRightY - part.RelativeTopLeftY) * scale);

                // 边界保护
                x = Math.Max(0, x);
                y = Math.Max(0, y);
                w = Math.Min(w, imgWidth - x);
                h = Math.Min(h, imgHeight - y);

                if (w <= 0 || h <= 0) return;

                Rect roi = new Rect(x, y, w, h);

                try
                {
                    using (Mat cisPatch = new Mat(flippedCis, roi))
                    using (Mat tiffPatch = new Mat(flippedTiff, roi))
                    {
                        // 保存裁切小图
                        string cisName = null;
                        if (config.SaveCroppedImages)
                        {
                            var prms = new ImageEncodingParam[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 95) };
                            cisName = Path.Combine(outputDir, $"{part.HotInkTaskID}_cis.jpg");
                            string tiffName = Path.Combine(outputDir, $"{part.HotInkTaskID}_tiff.jpg");
                            Cv2.ImWrite(tiffName, tiffPatch, prms);
                        }

                        // Alpha 掩膜裁切 + 缺陷检测
                        if (flippedAlpha != null)
                        {
                            using (Mat alphaPatch = new Mat(flippedAlpha, roi))
                            {
                                if (config.SaveCroppedImages)
                                {
                                    string alphaName = Path.Combine(outputDir, $"{part.HotInkTaskID}_alpha.png");
                                    Cv2.ImWrite(alphaName, alphaPatch);
                                }

                                // 执行缺陷检测
                                string defectPath = config.SaveDefectResultImages
                                    ? Path.Combine(outputDir, $"{part.HotInkTaskID}_defect.jpg")
                                    : null;

                                var defectResult = PatchDefectDetector.Detect(alphaPatch, cisPatch, cisBaseThresh, config, defectPath, cisName);
                                defectResult.PartId = part.HotInkTaskID;
                                defectResult.GlobalRoi = roi;
                                resultsBag.Add(defectResult);
                            }
                        }

                        System.Threading.Interlocked.Increment(ref count);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"裁切/检测 {part.HotInkTaskID} 异常: {ex.Message}");
                }
            });

            var results = new List<PatchDefectResult>(resultsBag);
            Console.WriteLine($"[PatchCropper] 共成功处理 {count} 个零件，检测完成 {results.Count} 个。");

            // --- 全局缺陷可视化保存与显示 ---
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

                // 1. 准备缩小后的 CIS 侧画板
                Mat cisSmall = new Mat();
                if (displayScale < 1.0)
                    Cv2.Resize(flippedCis, cisSmall, new Size(), displayScale, displayScale, InterpolationFlags.Nearest);
                else
                    flippedCis.CopyTo(cisSmall);

                Mat cisCanvas = new Mat();
                if (cisSmall.Channels() == 1)
                    Cv2.CvtColor(cisSmall, cisCanvas, ColorConversionCodes.GRAY2BGR);
                else
                    cisSmall.CopyTo(cisCanvas);
                
                cisSmall.Dispose();

                // 计算缩放后的画笔粗细
                int partThick = Math.Max(2, (int)(5 * displayScale));
                int defectThick = Math.Max(2, (int)(9 * displayScale));

                // 2. 将每个缺陷画到缩放后的 CIS 画板上
                foreach (var res in results)
                {
                    // 绘制零件边框
                    Rect scaledRoi = new Rect((int)(res.GlobalRoi.X * displayScale), (int)(res.GlobalRoi.Y * displayScale), 
                                              (int)(res.GlobalRoi.Width * displayScale), (int)(res.GlobalRoi.Height * displayScale));
                    Scalar partColor = res.IsPass ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);
                    Cv2.Rectangle(cisCanvas, scaledRoi, partColor, partThick);

                    // 绘制内部缺陷 (断墨/漏印，橙色框)
                    foreach (var r in res.InnerRects)
                    {
                        Rect scaledDefectRect = new Rect((int)((res.GlobalRoi.X + r.X) * displayScale), (int)((res.GlobalRoi.Y + r.Y) * displayScale), 
                                                         (int)(r.Width * displayScale), (int)(r.Height * displayScale));
                        Cv2.Rectangle(cisCanvas, scaledDefectRect, new Scalar(0, 165, 255), defectThick);
                    }

                    // 绘制外部缺陷 (飞墨/脏污，红色框)
                    foreach (var r in res.OuterRects)
                    {
                        Rect scaledDefectRect = new Rect((int)((res.GlobalRoi.X + r.X) * displayScale), (int)((res.GlobalRoi.Y + r.Y) * displayScale), 
                                                         (int)(r.Width * displayScale), (int)(r.Height * displayScale));
                        Cv2.Rectangle(cisCanvas, scaledDefectRect, new Scalar(0, 0, 255), defectThick);
                    }
                }

                // 3. (用户要求：不需要沿 X 轴翻转回原始图像的方向)
                Mat finalCisCanvas = cisCanvas;

                // 4. 准备 Tiff 画板用于拼接
                Mat tiffSmall = new Mat();
                if (displayScale < 1.0)
                    Cv2.Resize(flippedTiff, tiffSmall, new Size(), displayScale, displayScale, InterpolationFlags.Nearest);
                else
                    flippedTiff.CopyTo(tiffSmall);

                Mat tiffCanvas = new Mat();
                if (tiffSmall.Channels() == 1)
                    Cv2.CvtColor(tiffSmall, tiffCanvas, ColorConversionCodes.GRAY2BGR);
                else if (tiffSmall.Channels() == 4)
                    Cv2.CvtColor(tiffSmall, tiffCanvas, ColorConversionCodes.BGRA2BGR);
                else
                    tiffSmall.CopyTo(tiffCanvas);

                tiffSmall.Dispose();

                // 确保通道数一致后水平拼接 (防止崩溃)
                if (tiffCanvas.Channels() == 3 && finalCisCanvas.Channels() == 3)
                {
                    Mat combined = new Mat();
                    Cv2.HConcat(new Mat[] { tiffCanvas, finalCisCanvas }, combined);

                    // 编码为 JPG 字节数组供 UI 显示
                    var prms = new ImageEncodingParam[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 85) };
                    Cv2.ImEncode(".jpg", combined, out globalImageBytes, prms);

                    // 仅当开启了保存图片开关时，才写入到硬盘
                    if (config.SaveDefectResultImages)
                    {
                        combinedPath = Path.Combine(outputDir, "GlobalDefectResult.jpg");
                        Cv2.ImWrite(combinedPath, combined, prms);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PatchCropper] 生成全局缺陷图异常: {ex.Message}");
            }

            return (results, combinedPath, globalImageBytes);
        }
    }
}
