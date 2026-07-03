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
        /// 根据排版坐标，从对齐后的 CIS 图、TIFF 图和 Alpha 掩膜上裁切零件小图，并进行上下翻转。
        /// 参照 align_diff.py: alpha_mask 作为原始设计二值掩膜一并裁切保存。
        /// </summary>
        /// <param name="cisWarped">变换到 TIFF 空间的 CIS 实拍图</param>
        /// <param name="tiffMat">TIFF 原始排版图 (BGR 3通道，已 alpha 融合到白底)</param>
        /// <param name="alphaMask">从 TIFF 原图分离出的 Alpha 通道掩膜 (单通道)，可为 null</param>
        /// <param name="parts">零件排版坐标列表</param>
        /// <param name="outputDir">输出目录</param>
        /// <param name="scale">DPI换算系数 (例如 300/25.4)</param>
        /// <param name="originXmm">X 原点偏移 (mm)</param>
        /// <param name="originYmm">Y 原点偏移 (mm)</param>
        public static void CropAndSave(Mat cisWarped, Mat tiffMat, Mat alphaMask, List<PartLocation> parts, string outputDir, double scale, double originXmm, double originYmm)
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

            // 翻转逻辑：readlog.cpp 中 flip(BW_org, BW_org, 0); 然后用翻转后的坐标进行裁切
            // 也就是说坐标原点 (originX, originY) 是在翻转后的图像上。
            Mat flippedCis = new Mat();
            Mat flippedTiff = new Mat();
            Cv2.Flip(cisWarped, flippedCis, FlipMode.X); // 上下翻转
            Cv2.Flip(tiffMat, flippedTiff, FlipMode.X); // 上下翻转

            // Alpha 掩膜也需要翻转
            Mat flippedAlpha = null;
            if (alphaMask != null)
            {
                flippedAlpha = new Mat();
                Cv2.Flip(alphaMask, flippedAlpha, FlipMode.X);
            }

            foreach (var part in parts)
            {
                if (part.HotInkTaskID != null && part.HotInkTaskID.Contains("QRCode"))
                {
                    continue; // 忽略二维码模块
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

                if (w <= 0 || h <= 0) continue;

                Rect roi = new Rect(x, y, w, h);

                try
                {
                    var prms = new ImageEncodingParam[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 95) };

                    using (Mat cisPatch = new Mat(flippedCis, roi))
                    using (Mat tiffPatch = new Mat(flippedTiff, roi))
                    {
                        string cisName = Path.Combine(outputDir, $"{part.HotInkTaskID}_cis.jpg");
                        string tiffName = Path.Combine(outputDir, $"{part.HotInkTaskID}_tiff.jpg");

                        Cv2.ImWrite(cisName, cisPatch, prms);
                        Cv2.ImWrite(tiffName, tiffPatch, prms);
                    }

                    // Alpha 掩膜裁切（作为设计二值图保存，供后续缺陷检测使用）
                    if (flippedAlpha != null)
                    {
                        using (Mat alphaPatch = new Mat(flippedAlpha, roi))
                        {
                            string alphaName = Path.Combine(outputDir, $"{part.HotInkTaskID}_alpha.png");
                            Cv2.ImWrite(alphaName, alphaPatch);
                        }
                    }

                    count++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"裁切 {part.HotInkTaskID} 异常: {ex.Message}");
                }
            }

            Console.WriteLine($"[PatchCropper] 共成功裁切保存 {count} 组零件小图。");
        }
    }
}
