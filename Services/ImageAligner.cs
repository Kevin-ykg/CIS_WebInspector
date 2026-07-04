using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    public class ImageAligner
    {
        // 返回找到的标志点中心坐标 (X, Y)
        public class MarkerPoint
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Area { get; set; }
            public double Circularity { get; set; }
        }

        /// <summary>
        /// 计算 CIS 实拍图到 TIFF 排版图的变换矩阵。
        /// </summary>
        /// <param name="cisMat">CIS 扫描大图 (BGR或单通道)</param>
        /// <param name="tiffMat">TIFF 排版原图 (BGR, 已处理完 alpha)</param>
        /// <param name="stripRatioTiff">TIFF 设计图上下端检测条带比例</param>
        /// <param name="stripRatioCisTop">CIS 实拍图上端检测条带比例</param>
        /// <param name="stripRatioCisBot">CIS 实拍图下端检测条带比例</param>
        /// <param name="minCircTiff">TIFF 圆度阈值</param>
        /// <param name="minCircCis">CIS 圆度阈值</param>
        /// <returns>3x3 变换矩阵 (Homography)，失败返回 null</returns>
        public static Mat ComputeTransform(Mat cisMat, Mat tiffMat, out int optimalThresh,
            double stripRatioTiff = 0.08, double stripRatioCisTop = 0.2, double stripRatioCisBot = 0.08,
            double minCircTiff = 0.85, double minCircCis = 0.75)
        {
            optimalThresh = 127; // 默认值

            int hTiff = tiffMat.Height;
            int wTiff = tiffMat.Width;
            int hCis = cisMat.Height;
            int wCis = cisMat.Width;

            int stripHTiff = (int)(hTiff * stripRatioTiff);
            int stripHCisTop = (int)(hCis * stripRatioCisTop);
            int stripHCisBot = (int)(hCis * stripRatioCisBot);

            // TIFF 上下端条带检测
            Mat tiffTop = tiffMat.Clone(new Rect(0, 0, wTiff, stripHTiff));
            Mat tiffBot = tiffMat.Clone(new Rect(0, hTiff - stripHTiff, wTiff, stripHTiff));

            // CIS 图像通道统一
            Mat cisGray;
            if (cisMat.Channels() == 1)
                cisGray = cisMat.Clone();
            else if (cisMat.Channels() == 4)
            {
                Mat cisBgr = new Mat();
                Cv2.CvtColor(cisMat, cisBgr, ColorConversionCodes.BGRA2BGR);
                cisGray = cisBgr.CvtColor(ColorConversionCodes.BGR2GRAY);
            }
            else
                cisGray = cisMat.CvtColor(ColorConversionCodes.BGR2GRAY);

            // CIS 上下端使用不同的 strip 比例
            Mat cisTop = cisGray.Clone(new Rect(0, 0, wCis, stripHCisTop));
            Mat cisBot = cisGray.Clone(new Rect(0, hCis - stripHCisBot, wCis, stripHCisBot));

            var tiffTopPts = DetectTiff(tiffTop, 0, minCircTiff);
            var tiffBotPts = DetectTiff(tiffBot, hTiff - stripHTiff, minCircTiff);

            var cisTopResult = DetectJpg(cisTop, 0, minCircCis);
            var cisTopPtsRaw = cisTopResult.Item1;
            int topThresh = cisTopResult.Item2;
            optimalThresh = topThresh; // 记录得到的最佳阈值

            
            // CIS Top 面积过滤
            List<MarkerPoint> cisTopPts = cisTopPtsRaw;
            double refArea = 0;
            if (cisTopPtsRaw.Count >= 3)
            {
                var areas = cisTopPtsRaw.Select(p => p.Area).OrderBy(a => a).ToList();
                double medArea = areas[areas.Count / 2];
                cisTopPts = cisTopPtsRaw.Where(p => p.Area < medArea * 2.5).ToList();
                refArea = medArea;
            }

            var cisBotResult = DetectJpg(cisBot, hCis - stripHCisBot, minCircCis, refArea);
            var cisBotPts = cisBotResult.Item1;
            int botThresh = cisBotResult.Item2;

            var matchedTop = MatchRows(tiffTopPts, cisTopPts);
            var matchedBot = MatchRows(tiffBotPts, cisBotPts);

            // 验证下端匹配质量
            bool bottomOk = true;
            if (matchedBot.Item1.Count >= 2 && matchedBot.Item2.Count >= 2)
            {
                if (matchedTop.Item1.Count >= 2 && matchedTop.Item2.Count >= 2)
                {
                    double topSx = (matchedTop.Item2.Last().X - matchedTop.Item2.First().X) / Math.Max(matchedTop.Item1.Last().X - matchedTop.Item1.First().X, 1);
                    double botSx = (matchedBot.Item2.Last().X - matchedBot.Item2.First().X) / Math.Max(matchedBot.Item1.Last().X - matchedBot.Item1.First().X, 1);
                    if (Math.Abs(topSx - botSx) / Math.Max(topSx, 0.01) > 0.15)
                    {
                        bottomOk = false;
                    }
                }
            }
            else
            {
                bottomOk = false;
            }

            var allTiffPts = new List<Point2f>();
            var allCisPts = new List<Point2f>();

            allTiffPts.AddRange(matchedTop.Item1.Select(p => new Point2f((float)p.X, (float)p.Y)));
            allCisPts.AddRange(matchedTop.Item2.Select(p => new Point2f((float)p.X, (float)p.Y)));

            if (bottomOk)
            {
                allTiffPts.AddRange(matchedBot.Item1.Select(p => new Point2f((float)p.X, (float)p.Y)));
                allCisPts.AddRange(matchedBot.Item2.Select(p => new Point2f((float)p.X, (float)p.Y)));
            }

            if (allTiffPts.Count < 3)
            {
                Console.WriteLine("[ImageAligner] 匹配点太少，无法计算矩阵。");
                return null;
            }

            // 计算变换矩阵 (CIS -> TIFF)
            // allCisPts 是 src, allTiffPts 是 dst
            Mat H = null;
            
            if (allTiffPts.Count >= 6)
            {
                H = Cv2.FindHomography(InputArray.Create(allCisPts), InputArray.Create(allTiffPts), HomographyMethods.Ransac, 5.0);
                // 这里为了稳健性可以加角度或缩放限制判断，不满足退化为 Affine
            }
            else if (allTiffPts.Count >= 4)
            {
                var inliers = new Mat();
                var affine = Cv2.EstimateAffine2D(InputArray.Create(allCisPts), InputArray.Create(allTiffPts), inliers, RobustEstimationAlgorithms.RANSAC, 5.0);
                if (affine != null && !affine.Empty())
                {
                    H = new Mat(3, 3, MatType.CV_64FC1);
                    H.Set<double>(0, 0, affine.At<double>(0, 0));
                    H.Set<double>(0, 1, affine.At<double>(0, 1));
                    H.Set<double>(0, 2, affine.At<double>(0, 2));
                    H.Set<double>(1, 0, affine.At<double>(1, 0));
                    H.Set<double>(1, 1, affine.At<double>(1, 1));
                    H.Set<double>(1, 2, affine.At<double>(1, 2));
                    H.Set<double>(2, 0, 0);
                    H.Set<double>(2, 1, 0);
                    H.Set<double>(2, 2, 1);
                }
            }

            // 兜底方案 (3点约束仿射)
            if (H == null || H.Empty())
            {
                double expectedSy = (double)hTiff / hCis;
                double sxEst = 1.0;
                double angleEst = 0.0;
                
                if (matchedTop.Item2.Count >= 2)
                {
                    sxEst = (matchedTop.Item1.Last().X - matchedTop.Item1.First().X) / Math.Max(matchedTop.Item2.Last().X - matchedTop.Item2.First().X, 1);
                    double dy = matchedTop.Item1.Last().Y - matchedTop.Item1.First().Y;
                    double dx = matchedTop.Item1.Last().X - matchedTop.Item1.First().X;
                    double dy_s = matchedTop.Item2.Last().Y - matchedTop.Item2.First().Y;
                    double dx_s = matchedTop.Item2.Last().X - matchedTop.Item2.First().X;
                    angleEst = Math.Atan2(dy, dx) - Math.Atan2(dy_s, dx_s);
                }

                double cosA = Math.Cos(angleEst);
                double sinA = Math.Sin(angleEst);
                
                double sMeanX = allCisPts.Average(p => p.X);
                double sMeanY = allCisPts.Average(p => p.Y);
                double dMeanX = allTiffPts.Average(p => p.X);
                double dMeanY = allTiffPts.Average(p => p.Y);

                double tx = dMeanX - (sxEst * cosA * sMeanX - expectedSy * sinA * sMeanY);
                double ty = dMeanY - (sxEst * sinA * sMeanX + expectedSy * cosA * sMeanY);

                H = new Mat(3, 3, MatType.CV_64FC1);
                H.Set<double>(0, 0, sxEst * cosA);
                H.Set<double>(0, 1, -expectedSy * sinA);
                H.Set<double>(0, 2, tx);
                H.Set<double>(1, 0, sxEst * sinA);
                H.Set<double>(1, 1, expectedSy * cosA);
                H.Set<double>(1, 2, ty);
                H.Set<double>(2, 0, 0);
                H.Set<double>(2, 1, 0);
                H.Set<double>(2, 2, 1);
            }

            return H;
        }

        private static List<MarkerPoint> DetectTiff(Mat stripBgr, int yOffset, double minCirc)
        {
            var markers = new List<MarkerPoint>();
            double stripArea = stripBgr.Width * stripBgr.Height;

            Mat stripF = new Mat();
            stripBgr.ConvertTo(stripF, MatType.CV_32FC3);

            // 计算与白色的距离
            Mat[] channels = Cv2.Split(stripF);
            
            Mat bDiff = new Mat();
            Cv2.Subtract(channels[0], new Scalar(255), bDiff);
            Mat bSq = new Mat();
            Cv2.Multiply(bDiff, bDiff, bSq);

            Mat gDiff = new Mat();
            Cv2.Subtract(channels[1], new Scalar(255), gDiff);
            Mat gSq = new Mat();
            Cv2.Multiply(gDiff, gDiff, gSq);

            Mat rDiff = new Mat();
            Cv2.Subtract(channels[2], new Scalar(255), rDiff);
            Mat rSq = new Mat();
            Cv2.Multiply(rDiff, rDiff, rSq);

            Mat distSq = new Mat();
            Cv2.Add(bSq, gSq, distSq);
            Cv2.Add(distSq, rSq, distSq);
            
            Mat dist = new Mat();
            Cv2.Sqrt(distSq, dist);

            Mat distU8 = new Mat();
            dist.ConvertTo(distU8, MatType.CV_8UC1, 255.0 / 441.7);
            
            Mat binary = new Mat();
            Cv2.Threshold(distU8, binary, 25, 255, ThresholdTypes.Binary);

            Point[][] contours;
            HierarchyIndex[] hierarchy;
            Cv2.FindContours(binary, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            foreach (var cnt in contours)
            {
                double area = Cv2.ContourArea(cnt);
                double perimeter = Cv2.ArcLength(cnt, true);
                if (perimeter == 0) continue;

                double ratio = area / stripArea;
                if (ratio < 0.001 || ratio > 0.05) continue;

                double circ = 4 * Math.PI * area / (perimeter * perimeter);
                if (circ < minCirc) continue;

                var M = Cv2.Moments(cnt);
                if (M.M00 == 0) continue;

                double cx = M.M10 / M.M00;
                double cy = M.M01 / M.M00 + yOffset;
                markers.Add(new MarkerPoint { X = cx, Y = cy, Area = area, Circularity = circ });
            }

            return ClusterY(markers, stripBgr.Height * 0.12);
        }

        private static Tuple<List<MarkerPoint>, int> DetectJpg(Mat stripGray, int yOffset, double minCirc, double refArea = 0)
        {
            double stripArea = stripGray.Width * stripGray.Height;
            
            Mat claheImg = new Mat();
            using (var clahe = Cv2.CreateCLAHE(2.0, new Size(4, 4)))
            {
                clahe.Apply(stripGray, claheImg);
            }
            
            Mat blurred = new Mat();
            Cv2.GaussianBlur(claheImg, blurred, new Size(3, 3), 0);

            List<MarkerPoint> bestCircles = new List<MarkerPoint>();
            int bestThresh = 120;
            int[] thresholds = new int[] { 20, 30, 40, 50, 60, 70, 80, 100, 120, 140, 160, 180 };

            foreach (int thresh in thresholds)
            {
                Mat binary = new Mat();
                Cv2.Threshold(blurred, binary, thresh, 255, ThresholdTypes.Binary);

                int nonZero = Cv2.CountNonZero(binary);
                if ((double)nonZero / (binary.Width * binary.Height) > 0.5)
                {
                    Cv2.BitwiseNot(binary, binary);
                }

                int border = 15;
                Cv2.Rectangle(binary, new Point(0, 0), new Point(binary.Width, border), Scalar.Black, -1);
                Cv2.Rectangle(binary, new Point(0, binary.Height - border), new Point(binary.Width, binary.Height), Scalar.Black, -1);
                Cv2.Rectangle(binary, new Point(0, 0), new Point(border, binary.Height), Scalar.Black, -1);
                Cv2.Rectangle(binary, new Point(binary.Width - border, 0), new Point(binary.Width, binary.Height), Scalar.Black, -1);

                Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
                Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel, null, 2);

                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(binary, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                var circles = new List<MarkerPoint>();
                foreach (var cnt in contours)
                {
                    double area = Cv2.ContourArea(cnt);
                    double perimeter = Cv2.ArcLength(cnt, true);
                    if (perimeter == 0) continue;

                    double ratio = area / stripArea;
                    if (ratio < 0.001 || ratio > 0.05) continue;

                    double circ = 4 * Math.PI * area / (perimeter * perimeter);
                    if (circ < minCirc) continue;

                    if (refArea > 0)
                    {
                        if (area < refArea * 0.3 || area > refArea * 3.0) continue;
                    }

                    var M = Cv2.Moments(cnt);
                    if (M.M00 == 0) continue;

                    double cx = M.M10 / M.M00;
                    double cy = M.M01 / M.M00 + yOffset;
                    circles.Add(new MarkerPoint { X = cx, Y = cy, Area = area, Circularity = circ });
                }

                circles = ClusterY(circles, stripGray.Height * 0.12);
                if (circles.Count > bestCircles.Count)
                {
                    bestCircles = circles;
                    bestThresh = thresh;
                }

                if (bestCircles.Count >= 7) // fallback to 3-5 is common, but loop breaks at 7
                {
                    break;
                }
            }

            return new Tuple<List<MarkerPoint>, int>(bestCircles, bestThresh);
        }

        private static List<MarkerPoint> ClusterY(List<MarkerPoint> markers, double tol)
        {
            if (markers.Count <= 3)
            {
                return markers.OrderBy(m => m.X).ToList();
            }

            var ys = markers.Select(m => m.Y).ToArray();
            var indices = Enumerable.Range(0, ys.Length).OrderBy(i => ys[i]).ToArray();
            var clusters = new List<List<int>>();
            var used = new HashSet<int>();

            foreach (int i in indices)
            {
                if (used.Contains(i)) continue;
                var cl = new List<int> { i };
                used.Add(i);
                for (int j = 0; j < ys.Length; j++)
                {
                    if (used.Contains(j)) continue;
                    if (Math.Abs(ys[j] - ys[i]) < tol)
                    {
                        cl.Add(j);
                        used.Add(j);
                    }
                }
                clusters.Add(cl);
            }

            var best = clusters.OrderByDescending(c => Math.Min(c.Count, 5))
                               .ThenByDescending(c => c.Sum(idx => markers[idx].Area))
                               .FirstOrDefault();

            if (best != null)
            {
                return best.Select(idx => markers[idx]).OrderBy(m => m.X).ToList();
            }
            return markers.OrderBy(m => m.X).ToList();
        }

        private static Tuple<List<MarkerPoint>, List<MarkerPoint>> MatchRows(List<MarkerPoint> ptsTiff, List<MarkerPoint> ptsCis)
        {
            if (ptsTiff.Count == 0 || ptsCis.Count == 0)
                return new Tuple<List<MarkerPoint>, List<MarkerPoint>>(new List<MarkerPoint>(), new List<MarkerPoint>());

            var s = ptsTiff.OrderBy(p => p.X).ToList();
            var d = ptsCis.OrderBy(p => p.X).ToList();

            if (s.Count == d.Count)
            {
                return new Tuple<List<MarkerPoint>, List<MarkerPoint>>(s, d);
            }

            var sNorm = new double[s.Count];
            double sRange = Math.Max(s.Last().X - s.First().X, 1);
            for (int i = 0; i < s.Count; i++) sNorm[i] = (s[i].X - s.First().X) / sRange;

            var dNorm = new double[d.Count];
            double dRange = Math.Max(d.Last().X - d.First().X, 1);
            for (int i = 0; i < d.Count; i++) dNorm[i] = (d[i].X - d.First().X) / dRange;

            var ms = new List<MarkerPoint>();
            var md = new List<MarkerPoint>();
            var used = new HashSet<int>();

            if (s.Count <= d.Count)
            {
                for (int i = 0; i < s.Count; i++)
                {
                    int bestJ = -1;
                    double bestDist = double.MaxValue;
                    for (int j = 0; j < d.Count; j++)
                    {
                        if (used.Contains(j)) continue;
                        double dist = Math.Abs(dNorm[j] - sNorm[i]);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestJ = j;
                        }
                    }
                    if (bestJ != -1)
                    {
                        ms.Add(s[i]);
                        md.Add(d[bestJ]);
                        used.Add(bestJ);
                    }
                }
            }
            else
            {
                for (int j = 0; j < d.Count; j++)
                {
                    int bestI = -1;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < s.Count; i++)
                    {
                        if (used.Contains(i)) continue;
                        double dist = Math.Abs(sNorm[i] - dNorm[j]);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestI = i;
                        }
                    }
                    if (bestI != -1)
                    {
                        ms.Add(s[bestI]);
                        md.Add(d[j]);
                        used.Add(bestI);
                    }
                }
            }

            return new Tuple<List<MarkerPoint>, List<MarkerPoint>>(ms, md);
        }

        public static Mat WarpToTiffSpace(Mat cisMat, Mat H, Size tiffSize)
        {
            Mat warped = new Mat();
            Cv2.WarpPerspective(cisMat, warped, H, tiffSize);
            return warped;
        }
    }
}
