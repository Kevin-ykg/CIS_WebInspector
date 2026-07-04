using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 单个零件的缺陷检测结果。
    /// </summary>
    public class PatchDefectResult
    {
        public string PartId { get; set; }
        public int MaxAreaInner { get; set; }
        public int MaxAreaOuter { get; set; }
        public int InnerDefectCount { get; set; }
        public int OuterDefectCount { get; set; }
        public bool IsPass { get; set; }
        public Rect GlobalRoi { get; set; }
        public List<Rect> InnerRects { get; set; } = new List<Rect>();
        public List<Rect> OuterRects { get; set; } = new List<Rect>();
    }

    /// <summary>
    /// 零件级缺陷检测引擎。
    /// 移植自 localpeizhun.cpp 及 align_diff.py 结合策略：
    /// [可选]SIFT 二次局部对齐 → 各自二值化 → 形态学容差差分 → 边缘屏蔽 → 连通域判定。
    /// </summary>
    public static class PatchDefectDetector
    {
        /// <summary>
        /// 对单个零件执行缺陷检测。
        /// 流程参照 align_diff.py L544-600:
        ///   1) Alpha → threshold(60) → binary_tiff (设计图案)
        ///   2) CIS → [可选SIFT对齐] → threshold(cisBaseThresh) → binary_jpg (实拍图案)
        ///   3) 形态学容差差分 (内部/外部分离)
        ///   4) 边缘屏蔽 (消除轮廓错位噪声)
        ///   5) 连通域判定
        /// </summary>
        public static PatchDefectResult Detect(Mat alphaImg, Mat cisImg, int cisBaseThresh, AppConfig config, string outputPath = null, string cisOutputPath = null)
        {
            var result = new PatchDefectResult();

            // --- 0. 通道统一：确保都是灰度图（避免不必要的 Clone） ---
            Mat alphaGray = ToGray(alphaImg);
            Mat cisGray = ToGray(cisImg);

            // --- 1. 自适应缩放 ---
            double scale = config.DefectDetectScale;
            int scaledW = (int)(alphaGray.Width * scale);
            if (scaledW < config.DefectMinScaledWidth && alphaGray.Width > config.DefectMinScaledWidth)
            {
                scale = (double)config.DefectMinScaledWidth / alphaGray.Width;
            }

            Mat alphaScaled = new Mat();
            Mat cisScaled = new Mat();
            Cv2.Resize(alphaGray, alphaScaled, new Size(), scale, scale, InterpolationFlags.Nearest);
            Cv2.Resize(cisGray, cisScaled, new Size(), scale, scale, InterpolationFlags.Nearest);

            // --- 2. [可选] SIFT 二次局部对齐 ---
            Mat cisAligned;
            Mat cisToSave = cisImg; // default to original
            if (config.EnableSiftLocalAlign)
            {
                Mat alphaBlurred = new Mat();
                Mat cisBlurred = new Mat();
                Cv2.Blur(alphaScaled, alphaBlurred, new Size(3, 3));
                Cv2.Blur(cisScaled, cisBlurred, new Size(3, 3));
                TrySiftAlign(alphaBlurred, cisBlurred, alphaScaled, cisScaled, cisImg, scale, out cisAligned, out cisToSave);
            }
            else
            {
                // 全局已经由 ImageAligner 对齐，直接使用缩放后的 CIS 图像
                cisAligned = cisScaled;
                cisToSave = cisImg;
            }

            if (cisOutputPath != null && config.SaveCroppedImages)
            {
                try
                {
                    var prms = new ImageEncodingParam[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 95) };
                    Cv2.ImWrite(cisOutputPath, cisToSave, prms);
                }
                catch { }
            }

            // --- 3. 二值化 (align_diff.py L544-552) ---
            Mat alphaBinary = new Mat();
            Cv2.Threshold(alphaScaled, alphaBinary, config.DefectAlphaBinaryThresh, 255, ThresholdTypes.Binary);

            Mat cisBinary = new Mat();
            Cv2.Threshold(cisAligned, cisBinary, cisBaseThresh, 255, ThresholdTypes.Binary);
            //Cv2.ImWrite("cisBinary.png", cisBinary);
            //Cv2.ImWrite("alphaBinary.png", alphaBinary);
            // --- 4. 动态缩放参数 ---
            int scaledTolInner = Math.Max(1, (int)Math.Round(config.DefectToleranceInner * scale));
            int scaledTolOuter = Math.Max(1, (int)Math.Round(config.DefectToleranceOuter * scale));
            int scaledEdgeThick = config.DefectEdgeExclusionThick > 0 ? Math.Max(1, (int)Math.Round(config.DefectEdgeExclusionThick * scale)) : 0;
            int scaledEdgeSmall = config.DefectEdgeExclusionSmall > 0 ? Math.Max(1, (int)Math.Round(config.DefectEdgeExclusionSmall * scale)) : 0;
            int scaledAreaThreshInner = Math.Max(1, (int)Math.Round(config.DefectAreaThreshInner * scale * scale));
            int scaledAreaThreshOuter = Math.Max(1, (int)Math.Round(config.DefectAreaThreshOuter * scale * scale));

            // --- 5. 形态学容差差分 (align_diff.py L564-577) ---
            // 内部缺陷 (断墨/漏印): alpha_binary - dilate(cis_binary, TOLERANCE_inner)
            Mat kernelInner = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                new Size(scaledTolInner, scaledTolInner));
            Mat cisDilatedInner = new Mat();
            Cv2.Dilate(cisBinary, cisDilatedInner, kernelInner);
            Mat difInner = new Mat();
            Cv2.Subtract(alphaBinary, cisDilatedInner, difInner);

            // 外部缺陷 (飞墨/脏污): cis_binary - dilate(alpha_binary, TOLERANCE_outer)
            Mat kernelOuter = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                new Size(scaledTolOuter, scaledTolOuter));
            Mat alphaDilatedOuter = new Mat();
            Cv2.Dilate(alphaBinary, alphaDilatedOuter, kernelOuter);
            Mat difOuter = new Mat();
            Cv2.Subtract(cisBinary, alphaDilatedOuter, difOuter);

            // --- 6. 边缘屏蔽 (align_diff.py L579-600) ---
            if (scaledEdgeThick > 0 || scaledEdgeSmall > 0)
            {
                ApplyEdgeExclusion(alphaBinary, difInner, difOuter, scaledEdgeThick, scaledEdgeSmall);
            }

            // --- 7. 连通域分析 ---
            int maxAreaInner = 0, innerCount = 0;
            var innerRects = AnalyzeConnectedComponents(difInner, scaledAreaThreshInner, out maxAreaInner, out innerCount);

            int maxAreaOuter = 0, outerCount = 0;
            var outerRects = AnalyzeConnectedComponents(difOuter, scaledAreaThreshOuter, out maxAreaOuter, out outerCount);

            // --- 8. 判定 ---
            result.MaxAreaInner = maxAreaInner;
            result.MaxAreaOuter = maxAreaOuter;
            result.InnerDefectCount = innerCount;
            result.OuterDefectCount = outerCount;
            result.IsPass = (maxAreaInner <= scaledAreaThreshInner) &&
                            (maxAreaOuter <= scaledAreaThreshOuter);

            // 还原缺陷坐标到 Patch 原始尺度
            result.InnerRects = innerRects.Select(r => new Rect((int)(r.X / scale), (int)(r.Y / scale), (int)(r.Width / scale), (int)(r.Height / scale))).ToList();
            result.OuterRects = outerRects.Select(r => new Rect((int)(r.X / scale), (int)(r.Y / scale), (int)(r.Width / scale), (int)(r.Height / scale))).ToList();

            // --- 8. 可视化结果图（仅在需要时生成） ---
            if (outputPath != null)
            {
                SaveVisualization(alphaBinary, cisBinary, difInner, difOuter,
                    innerRects, outerRects, result.IsPass, outputPath);
            }

            return result;
        }

        /// <summary>
        /// 转灰度，尽量避免不必要的内存分配。
        /// 输入如果已经是单通道，直接返回（不 Clone）。
        /// 注意：调用者不应修改返回的 Mat。
        /// </summary>
        private static Mat ToGray(Mat src)
        {
            if (src.Channels() == 1) return src;
            if (src.Channels() == 4)
            {
                Mat gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
                return gray;
            }
            {
                Mat gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                return gray;
            }
        }

        /// <summary>
        /// 边缘屏蔽：在差分图上消除轮廓边缘的假缺陷。
        /// 提取为独立方法以保持 Detect 主流程清晰。
        /// </summary>
        private static void ApplyEdgeExclusion(Mat alphaBinary, Mat difInner, Mat difOuter, int edgeThick, int edgeSmall)
        {
            // 大包围圈屏蔽 (Fill up)
            Point[][] contoursExt;
            HierarchyIndex[] hierExt;
            Cv2.FindContours(alphaBinary.Clone(), out contoursExt, out hierExt,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            Mat alphaFilled = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1);
            Cv2.DrawContours(alphaFilled, contoursExt, -1, new Scalar(255), -1);

            Point[][] contoursFilled;
            HierarchyIndex[] hierFilled;
            Cv2.FindContours(alphaFilled, out contoursFilled, out hierFilled,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            Mat edgeMask = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1);
            if (edgeThick > 0)
            {
                Cv2.DrawContours(edgeMask, contoursFilled, -1, new Scalar(255), edgeThick);
            }

            // 内外全细节屏蔽
            if (edgeSmall > 0)
            {
                Point[][] contoursAll;
                HierarchyIndex[] hierAll;
                Cv2.FindContours(alphaBinary.Clone(), out contoursAll, out hierAll,
                    RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);

                Mat smallMask = Mat.Zeros(alphaBinary.Size(), MatType.CV_8UC1);
                Cv2.DrawContours(smallMask, contoursAll, -1, new Scalar(255), edgeSmall);
                Cv2.BitwiseOr(edgeMask, smallMask, edgeMask);
            }
            //Cv2.ImWrite("edgeMask.png", edgeMask);
            //Cv2.ImWrite("difInnerBeforeMask.png", difInner);
            //Cv2.ImWrite("difOuterBeforeMask.png", difOuter);
            // 在差分图上执行屏蔽
            difInner.SetTo(new Scalar(0), edgeMask);
            difOuter.SetTo(new Scalar(0), edgeMask);
        }

        /// <summary>
        /// SIFT 特征匹配 + RANSAC + 仿射对齐。
        /// 返回对齐后的 CIS 图像 (comAligned)。失败时返回原始 comScaled。
        /// </summary>
        private static bool TrySiftAlign(Mat alphaBlurred, Mat cisBlurred, Mat alphaScaled, Mat cisScaled, Mat cisImgOrig, double scale, out Mat cisAligned, out Mat cisAlignedOrig)
        {
            cisAligned = cisScaled; // 默认：不复制，直接引用
            cisAlignedOrig = cisImgOrig;

            try
            {
                using (var sift = SIFT.Create(100))
                {
                    KeyPoint[] kp1, kp2;
                    Mat desc1 = new Mat(), desc2 = new Mat();
                    sift.DetectAndCompute(alphaBlurred, null, out kp1, desc1);
                    sift.DetectAndCompute(cisBlurred, null, out kp2, desc2);

                    if (kp1.Length == 0 || kp2.Length == 0 || desc1.Empty() || desc2.Empty())
                        return false;

                    using (var matcher = new BFMatcher(NormTypes.L2))
                    {
                        DMatch[][] knnMatches = matcher.KnnMatch(desc1, desc2, 2);
                        if (knnMatches.Length < 4) return false;

                        float ratioThresh = 0.6f;
                        var goodMatches = new List<DMatch>();
                        foreach (var m in knnMatches)
                        {
                            if (m.Length >= 2 && m[0].Distance < ratioThresh * m[1].Distance)
                                goodMatches.Add(m[0]);
                        }
                        if (goodMatches.Count < 4) return false;

                        var ptsDb = goodMatches.Select(m => kp1[m.QueryIdx].Pt).ToArray();
                        var ptsTest = goodMatches.Select(m => kp2[m.TrainIdx].Pt).ToArray();

                        var ptsDbMat = InputArray.Create(ptsDb.Select(p => new Point2d(p.X, p.Y)).ToArray());
                        var ptsTestMat = InputArray.Create(ptsTest.Select(p => new Point2d(p.X, p.Y)).ToArray());
                        Mat inliersMask = new Mat();
                        Cv2.FindFundamentalMat(ptsDbMat, ptsTestMat, FundamentalMatMethods.Ransac, 3.0, 0.99, inliersMask);

                        var dbOk = new List<Point2f>();
                        var testOk = new List<Point2f>();
                        for (int i = 0; i < inliersMask.Rows; i++)
                        {
                            if (inliersMask.At<byte>(i, 0) != 0)
                            {
                                dbOk.Add(ptsDb[i]);
                                testOk.Add(ptsTest[i]);
                            }
                        }
                        if (dbOk.Count < 4 || testOk.Count < 4) return false;

                        Mat T = Cv2.EstimateAffine2D(
                            InputArray.Create(testOk.ToArray()),
                            InputArray.Create(dbOk.ToArray()));
                        if (T == null || T.Empty()) return false;

                        double sx = T.At<double>(0, 0), sy = T.At<double>(1, 1);
                        double dx = T.At<double>(0, 2), dy = T.At<double>(1, 2);
                        if (sx > 0.9 && sx < 1.1 && sy > 0.9 && sy < 1.1 &&
                            dx > -10 && dx < 10 && dy > -10 && dy < 10)
                        {
                            cisAligned = new Mat();
                            Cv2.WarpAffine(cisScaled, cisAligned, T, alphaScaled.Size(), InterpolationFlags.Cubic);

                            cisAlignedOrig = new Mat();
                            Mat T_orig = T.Clone();
                            T_orig.Set<double>(0, 2, T.At<double>(0, 2) / scale);
                            T_orig.Set<double>(1, 2, T.At<double>(1, 2) / scale);
                            Cv2.WarpAffine(cisImgOrig, cisAlignedOrig, T_orig, cisImgOrig.Size(), InterpolationFlags.Cubic);

                            return true;
                        }
                        return false;
                    }
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 连通域分析：统计超过面积阈值的缺陷数量与最大面积，返回缺陷外接矩形列表。
        /// </summary>
        private static List<Rect> AnalyzeConnectedComponents(Mat binaryImg, int areaThresh, out int maxArea, out int defectCount)
        {
            maxArea = 0;
            defectCount = 0;
            var rects = new List<Rect>();

            Mat labels = new Mat(), stats = new Mat(), centroids = new Mat();
            int nLabels = Cv2.ConnectedComponentsWithStats(binaryImg, labels, stats, centroids);

            for (int i = 1; i < nLabels; i++)
            {
                int area = stats.At<int>(i, 4); // CC_STAT_AREA
                if (area > maxArea) maxArea = area;
                if (area > areaThresh)
                {
                    defectCount++;
                    rects.Add(new Rect(
                        stats.At<int>(i, 0), stats.At<int>(i, 1),
                        stats.At<int>(i, 2), stats.At<int>(i, 3)));
                }
            }
            return rects;
        }

        /// <summary>
        /// 生成并保存可视化结果图。
        /// 左: 原图(二值化) | 中: 扫描图(二值化+标注缺陷) | 右: 差分图
        /// </summary>
        private static void SaveVisualization(Mat orgBin, Mat comBin, Mat difInner, Mat difOuter,
            List<Rect> innerRects, List<Rect> outerRects, bool isPass, string outputPath)
        {
            try
            {
                Mat orgRgb = new Mat(), comRgb = new Mat(), difRgb = new Mat();
                Cv2.CvtColor(orgBin, orgRgb, ColorConversionCodes.GRAY2BGR);
                Cv2.CvtColor(comBin, comRgb, ColorConversionCodes.GRAY2BGR);

                Mat difMerged = new Mat();
                Cv2.Add(difInner, difOuter, difMerged);
                Cv2.CvtColor(difMerged, difRgb, ColorConversionCodes.GRAY2BGR);

                foreach (var r in innerRects)
                    Cv2.Rectangle(comRgb, r, new Scalar(0, 165, 255), 2);
                foreach (var r in outerRects)
                    Cv2.Rectangle(comRgb, r, new Scalar(0, 0, 255), 2);

                double fontScale = Math.Max(0.5, orgBin.Width / 300.0);
                int thickness = Math.Max(1, (int)(fontScale * 2));
                var color = isPass ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                Cv2.PutText(orgRgb, "Org(Bin)", new Point(10, orgRgb.Height / 8),
                    HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);
                Cv2.PutText(comRgb, isPass ? "Pass" : "Wrong", new Point(10, comRgb.Height / 8),
                    HersheyFonts.HersheySimplex, fontScale, color, thickness);
                Cv2.PutText(difRgb, "Diff", new Point(10, difRgb.Height / 8),
                    HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 255, 0), thickness);

                Cv2.Rectangle(comRgb, new Rect(0, 0, comRgb.Width, comRgb.Height), color, 2);

                Mat vis = new Mat();
                Cv2.HConcat(new Mat[] { orgRgb, comRgb, difRgb }, vis);
                Cv2.ImWrite(outputPath, vis);
            }
            catch { }
        }
    }
}
