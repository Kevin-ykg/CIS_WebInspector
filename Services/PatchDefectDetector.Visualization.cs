using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    // 只负责画框和文件保存；算法掩膜由 C++ 返回，显示过程不回流到缺陷判定。
    public static partial class PatchDefectDetector
    {
        /// <summary>
        /// 生成并保存可视化结果图。
        /// 左: 原图(二值化) | 中: 扫描图(二值化+标注缺陷) | 右: 差分图
        /// </summary>
        private static void SaveVisualization(Mat orgBin, Mat comBin, Mat difInner, Mat difOuter,
            List<Rect> innerRects, List<Rect> outerRects, List<Rect> fineLineRects,
            bool isPass, LocalAlignmentStatus alignmentStatus, LocalAlignmentModel alignmentModel,
            string outputPath, IAppLogger logger)
        {
            try
            {
                using (var orgRgb = new Mat())
                using (var comRgb = new Mat())
                using (var difRgb = new Mat())
                using (var difMerged = new Mat())
                using (var vis = new Mat())
                {
                    Cv2.CvtColor(orgBin, orgRgb, ColorConversionCodes.GRAY2BGR);
                    Cv2.CvtColor(comBin, comRgb, ColorConversionCodes.GRAY2BGR);
                    Cv2.Add(difInner, difOuter, difMerged);
                    Cv2.CvtColor(difMerged, difRgb, ColorConversionCodes.GRAY2BGR);

                    foreach (Rect rect in innerRects)
                    {
                        Cv2.Rectangle(comRgb, rect, new Scalar(0, 165, 255), 2);
                    }

                    foreach (Rect rect in outerRects)
                    {
                        Cv2.Rectangle(comRgb, rect, new Scalar(0, 0, 255), 2);
                    }

                    foreach (Rect rect in fineLineRects)
                    {
                        Cv2.Rectangle(comRgb, rect, new Scalar(255, 0, 255), 2);
                    }

                    double fontScale = Math.Max(0.5, orgBin.Width / 300.0);
                    int thickness = Math.Max(1, (int)(fontScale * 2));
                    // 产品 Pass/NG 与局部配准提示是两条独立信息；待复核不能
                    // 把正常绘制的缺陷结论替换成黄色 Review。
                    Scalar color = isPass ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                    Cv2.PutText(orgRgb, "Org(Bin)", new Point(10, orgRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);
                    Cv2.PutText(comRgb, isPass ? "Pass" : "Wrong", new Point(10, comRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, color, thickness);
                    Cv2.PutText(difRgb, "Diff", new Point(10, difRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);
                    if (alignmentStatus == LocalAlignmentStatus.Review ||
                        alignmentStatus == LocalAlignmentStatus.Error ||
                        alignmentStatus == LocalAlignmentStatus.AppliedDegraded)
                    {
                        int warningY = Math.Min(difRgb.Height - 10,
                            difRgb.Height / 8 + Math.Max(20, (int)(28 * fontScale)));
                        Cv2.PutText(difRgb, "ALIGN REVIEW", new Point(10, warningY),
                            HersheyFonts.HersheySimplex, fontScale * 0.75,
                            new Scalar(0, 255, 255), thickness);
                    }
                    // 区分“局部已应用但质量降级”和“局部拒绝而回退全局”，
                    // 两者均提示复核，但实际使用的图像与矩阵不同。
                    string alignmentLabel = alignmentStatus == LocalAlignmentStatus.Applied ||
                                            alignmentStatus == LocalAlignmentStatus.AppliedDegraded
                        ? alignmentModel == LocalAlignmentModel.Homography ? "ALIGN: Homography"
                        : alignmentModel == LocalAlignmentModel.ConstrainedAffine ? "ALIGN: Affine"
                        : "ALIGN: Similarity"
                        : alignmentStatus == LocalAlignmentStatus.NotNeeded ? "ALIGN: Global (good)"
                        : alignmentStatus == LocalAlignmentStatus.Disabled ? "ALIGN: Global (off)"
                        : "ALIGN: Global (review)";
                    if (alignmentStatus == LocalAlignmentStatus.AppliedDegraded)
                    {
                        alignmentLabel += " (review)";
                    }
                    double labelScale = Math.Max(0.45, Math.Min(1.2, difRgb.Width / 1200.0));
                    Point labelOrigin = new Point(10, difRgb.Height - Math.Max(12, (int)(14 * labelScale)));
                    Cv2.PutText(difRgb, alignmentLabel, labelOrigin, HersheyFonts.HersheySimplex,
                        labelScale, new Scalar(0, 255, 255), Math.Max(1, (int)Math.Ceiling(labelScale)));
                    Cv2.Rectangle(comRgb, new Rect(0, 0, comRgb.Width, comRgb.Height), color, 2);

                    Cv2.HConcat(new[] { orgRgb, comRgb, difRgb }, vis);
                    Cv2.ImWrite(outputPath, vis);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(logger, $"[PatchDefectDetector][WARN] 保存缺陷可视化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存普通缺陷通道实际使用的合并边缘屏蔽掩膜。PNG 保证二值边界不受 JPEG 压缩影响。
        /// </summary>
        private static void SaveEdgeExclusionMask(
            Mat edgeMask,
            Size originalPatchSize,
            string outputPath,
            IAppLogger logger)
        {
            try
            {
                Mat maskToSave = edgeMask;
                Mat restoredMask = null;
                try
                {
                    // 原生输出已恢复到原始零件大小；保留下面的尺寸保护用于独立调用。
                    // 若传入检测尺度 mask，最近邻只复制 0/255 标签，不制造额外灰度边缘。
                    if (edgeMask.Width != originalPatchSize.Width ||
                        edgeMask.Height != originalPatchSize.Height)
                    {
                        restoredMask = new Mat();
                        Cv2.Resize(
                            edgeMask,
                            restoredMask,
                            originalPatchSize,
                            0,
                            0,
                            InterpolationFlags.Nearest);
                        maskToSave = restoredMask;
                    }

                    if (!Cv2.ImWrite(outputPath, maskToSave))
                    {
                        AppLog.Write(
                            logger,
                            $"[PatchDefectDetector][WARN] 保存边缘屏蔽掩膜失败: {outputPath}");
                    }
                }
                finally
                {
                    restoredMask?.Dispose();
                }
            }
            catch (Exception ex)
            {
                // 诊断图保存失败不能改变零件检测结论。
                AppLog.Write(
                    logger,
                    $"[PatchDefectDetector][WARN] 保存边缘屏蔽掩膜异常: {ex.Message}");
            }
        }
    }
}
