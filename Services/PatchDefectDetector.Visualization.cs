using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 连通域统计、单位换算与结果可视化，集中维护缺陷颜色和诊断图输出约定。
    /// </summary>
    public static partial class PatchDefectDetector
    {
        /// <summary>
        /// 对屏蔽前的原始差分执行连通域分析，并用边缘屏蔽掩膜决定整个连通域是否放行。
        /// 完全位于屏蔽区内的连通域被忽略；只要有任意像素越过屏蔽区，就恢复该连通域
        /// 的完整轮廓，并以屏蔽前的完整面积参与阈值判断。方法返回前会把 binaryImg
        /// 更新为放行后的完整连通域掩膜，供结果图和后续处理使用。
        /// </summary>
        private static List<Rect> AnalyzeConnectedComponentsPreservingOriginalArea(
            Mat binaryImg,
            Mat edgeMask,
            int areaThresh,
            out int maxArea,
            out int defectCount,
            out List<int> acceptedDefectAreasPixels)
        {
            maxArea = 0;
            defectCount = 0;
            var rects = new List<Rect>();
            acceptedDefectAreasPixels = new List<int>();

            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centroids = new Mat())
            {
                int nLabels = Cv2.ConnectedComponentsWithStats(binaryImg, labels, stats, centroids);
                var hasPixelOutsideExclusion = new bool[nLabels];

                // 第一次扫描只判断每个原始连通域是否越过屏蔽区。这里不能先修改 binaryImg，
                // 否则屏蔽带会切断轮廓，面积和包围框就不再代表原始缺陷。
                unsafe
                {
                    for (int y = 0; y < labels.Rows; y++)
                    {
                        int* labelRow = (int*)labels.Ptr(y);
                        byte* exclusionRow = (byte*)edgeMask.Ptr(y);
                        for (int x = 0; x < labels.Cols; x++)
                        {
                            int label = labelRow[x];
                            if (label > 0 && exclusionRow[x] == 0)
                                hasPixelOutsideExclusion[label] = true;
                        }
                    }

                    // 第二次扫描恢复所有已放行连通域的完整像素，包括其落在屏蔽区内的部分。
                    for (int y = 0; y < labels.Rows; y++)
                    {
                        int* labelRow = (int*)labels.Ptr(y);
                        byte* outputRow = (byte*)binaryImg.Ptr(y);
                        for (int x = 0; x < labels.Cols; x++)
                        {
                            int label = labelRow[x];
                            outputRow[x] = label > 0 && hasPixelOutsideExclusion[label]
                                ? (byte)255
                                : (byte)0;
                        }
                    }
                }

                for (int i = 1; i < nLabels; i++)
                {
                    if (!hasPixelOutsideExclusion[i])
                        continue;

                    int area = stats.At<int>(i, 4); // CC_STAT_AREA
                    if (area > maxArea) maxArea = area;
                    if (area > areaThresh)
                    {
                        defectCount++;
                        // 与 rects 使用完全相同的加入条件和顺序，供日志按真实连通域面积换算。
                        acceptedDefectAreasPixels.Add(area);
                        rects.Add(new Rect(
                            stats.At<int>(i, 0), stats.At<int>(i, 1),
                            stats.At<int>(i, 2), stats.At<int>(i, 3)));
                    }
                }

            }
            return rects;
        }

        /// <summary>
        /// 把用户配置的物理面积阈值换算到当前检测尺度的像素面积。
        /// LayoutDpi 定义 TIFF/对齐目标空间的像素密度，线性缩放后面积再乘 scale²。
        /// </summary>
        private static int ConvertAreaMm2ToScaledPixels(
            double areaMm2,
            double layoutDpi,
            double linearScale)
        {
            double pixelsPerMm = GetValidPixelsPerMm(layoutDpi);
            double validScale = linearScale > 0 && !double.IsNaN(linearScale) && !double.IsInfinity(linearScale)
                ? linearScale
                : 1.0;
            double scaledPixelArea = Math.Max(0, areaMm2) *
                                     pixelsPerMm * pixelsPerMm *
                                     validScale * validScale;
            if (scaledPixelArea >= int.MaxValue)
                return int.MaxValue;
            return Math.Max(
                1,
                (int)Math.Round(scaledPixelArea, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// 把用户配置的物理长度换算为当前检测尺度的像素长度。
        /// 形态学核和边缘屏蔽均在 TIFF 对齐空间中执行，因此统一使用 LayoutDpi；
        /// 返回 0 表示配置关闭，正长度即使缩小后不足 1 px 也至少保留 1 px。
        /// </summary>
        private static int ConvertLengthMmToScaledPixels(
            double lengthMm,
            double layoutDpi,
            double linearScale)
        {
            if (lengthMm <= 0 || double.IsNaN(lengthMm) || double.IsInfinity(lengthMm))
                return 0;

            double validScale = linearScale > 0 && !double.IsNaN(linearScale) && !double.IsInfinity(linearScale)
                ? linearScale
                : 1.0;
            double scaledPixelLength = lengthMm * GetValidPixelsPerMm(layoutDpi) * validScale;
            if (scaledPixelLength >= int.MaxValue)
                return int.MaxValue;

            return Math.Max(
                1,
                (int)Math.Round(scaledPixelLength, MidpointRounding.AwayFromZero));
        }

        /// <summary>把检测尺度连通域的像素面积换算为 TIFF 目标空间中的物理面积 mm²。</summary>
        private static double ConvertScaledAreaToMm2(
            int scaledArea,
            double layoutDpi,
            double linearScale)
        {
            if (scaledArea <= 0)
                return 0;

            double pixelsPerMm = GetValidPixelsPerMm(layoutDpi);
            double validScale = linearScale > 0 && !double.IsNaN(linearScale) && !double.IsInfinity(linearScale)
                ? linearScale
                : 1.0;
            return scaledArea /
                   (pixelsPerMm * pixelsPerMm * validScale * validScale);
        }

        private static double GetValidPixelsPerMm(double layoutDpi)
        {
            double effectiveDpi = layoutDpi > 0 && !double.IsNaN(layoutDpi) && !double.IsInfinity(layoutDpi)
                ? layoutDpi
                : 300.0;
            return effectiveDpi / 25.4;
        }

        /// <summary>
        /// 将最终缺陷在某一检测尺度下的外接矩形宽高和真实连通域面积换算为毫米。
        /// 调用方必须传入已经通过最终门控、且顺序一一对应的矩形与像素面积集合；
        /// 本方法只做单位换算，不增加任何候选筛选、形态学处理或阈值判断。
        /// </summary>
        private static List<DefectGeometryMeasurement> BuildDefectGeometryMeasurements(
            IReadOnlyList<Rect> acceptedRects,
            IReadOnlyList<int> acceptedDefectAreasPixels,
            double layoutDpi,
            double linearScale)
        {
            var measurements = new List<DefectGeometryMeasurement>();
            if (acceptedRects == null)
                return measurements;

            double validScale = linearScale > 0 &&
                                !double.IsNaN(linearScale) &&
                                !double.IsInfinity(linearScale)
                ? linearScale
                : 1.0;
            double scaledPixelsPerMm = GetValidPixelsPerMm(layoutDpi) * validScale;

            int measurementCount = acceptedDefectAreasPixels == null
                ? 0
                : Math.Min(acceptedRects.Count, acceptedDefectAreasPixels.Count);
            for (int index = 0; index < measurementCount; index++)
            {
                Rect rect = acceptedRects[index];
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                double widthMm = rect.Width / scaledPixelsPerMm;
                double heightMm = rect.Height / scaledPixelsPerMm;
                measurements.Add(new DefectGeometryMeasurement
                {
                    WidthMm = widthMm,
                    HeightMm = heightMm,
                    AreaMm2 = ConvertScaledAreaToMm2(
                        acceptedDefectAreasPixels[index],
                        layoutDpi,
                        validScale)
                });
            }

            return measurements;
        }

        /// <summary>
        /// 生成并保存可视化结果图。
        /// 左: 原图(二值化) | 中: 扫描图(二值化+标注缺陷) | 右: 差分图
        /// </summary>
        private static void SaveVisualization(Mat orgBin, Mat comBin, Mat difInner, Mat difOuter,
            List<Rect> innerRects, List<Rect> outerRects, List<Rect> fineLineRects,
            bool isPass, string outputPath, IAppLogger logger)
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
                        Cv2.Rectangle(comRgb, rect, new Scalar(0, 165, 255), 2);
                    foreach (Rect rect in outerRects)
                        Cv2.Rectangle(comRgb, rect, new Scalar(0, 0, 255), 2);
                    foreach (Rect rect in fineLineRects)
                        Cv2.Rectangle(comRgb, rect, new Scalar(255, 0, 255), 2);

                    double fontScale = Math.Max(0.5, orgBin.Width / 300.0);
                    int thickness = Math.Max(1, (int)(fontScale * 2));
                    Scalar color = isPass ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                    Cv2.PutText(orgRgb, "Org(Bin)", new Point(10, orgRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);
                    Cv2.PutText(comRgb, isPass ? "Pass" : "Wrong", new Point(10, comRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, color, thickness);
                    Cv2.PutText(difRgb, "Diff", new Point(10, difRgb.Height / 8),
                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);
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
                    // edgeMask 位于 DefectDetectScale 对应的检测尺度。最近邻恢复只复制 0/255
                    // 标签，不会像线性插值那样在屏蔽边缘产生未参与算法的灰度像素。
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
