using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 对准预览与图像重映射，按对准模式执行全局透视变换或分条带逆向 Remap。
    /// </summary>
    public static partial class ImageAligner
    {
        private const int WhiteInkPreviewLegendHeight = 42;

        public static byte[] CreateWhiteInkInspectionPreview(
            Mat cisMat,
            WhiteInkInspectionResult inspection,
            int maximumWidth = 2000)
        {
            if (cisMat == null || cisMat.Empty() ||
                inspection == null || !inspection.IsEnabled)
                return null;

            double scale = Math.Min(1.0, Math.Max(1, maximumWidth) / (double)cisMat.Width);
            using (var resized = new Mat())
            using (var canvas = new Mat())
            {
                if (scale < 1.0)
                    Cv2.Resize(cisMat, resized, new Size(), scale, scale, InterpolationFlags.Area);
                else
                    cisMat.CopyTo(resized);

                if (resized.Channels() == 1)
                    Cv2.CvtColor(resized, canvas, ColorConversionCodes.GRAY2BGR);
                else if (resized.Channels() == 4)
                    Cv2.CvtColor(resized, canvas, ColorConversionCodes.BGRA2BGR);
                else
                    resized.CopyTo(canvas);

                Scalar statusColor = inspection.RequiresWarning
                    ? new Scalar(0, 0, 255)
                    : new Scalar(0, 220, 0);
                Rect sourceRegion = inspection.SearchRegion;
                var scaledRegion = new Rect(
                    Math.Max(0, (int)Math.Round(sourceRegion.X * scale)),
                    Math.Max(0, (int)Math.Round(sourceRegion.Y * scale)),
                    Math.Max(1, (int)Math.Round(sourceRegion.Width * scale)),
                    Math.Max(1, (int)Math.Round(sourceRegion.Height * scale)));
                if (scaledRegion.X + scaledRegion.Width > canvas.Width)
                    scaledRegion.Width = canvas.Width - scaledRegion.X;
                if (scaledRegion.Y + scaledRegion.Height > canvas.Height)
                    scaledRegion.Height = canvas.Height - scaledRegion.Y;
                if (scaledRegion.Width > 0 && scaledRegion.Height > 0)
                    Cv2.Rectangle(canvas, scaledRegion, new Scalar(255, 255, 0), 2);

                foreach (WhiteInkMarkSample sample in inspection.Samples)
                {
                    var center = new Point(
                        (int)Math.Round(sample.Center.X * scale),
                        (int)Math.Round(sample.Center.Y * scale));
                    int radius = Math.Max(3, (int)Math.Round(sample.DisplayRadius * scale));
                    Scalar circleColor = sample.UsedDetectedCenter
                        ? statusColor
                        : new Scalar(0, 165, 255);
                    Cv2.Circle(canvas, center, radius, circleColor, 2, LineTypes.AntiAlias);
                    Cv2.Circle(canvas, center, 3, circleColor, -1, LineTypes.AntiAlias);
                    Cv2.PutText(
                        canvas,
                        (sample.UsedDetectedCenter ? "D" : "P") + sample.Index,
                        center + new Point(5, -5),
                        HersheyFonts.HersheySimplex,
                        0.45,
                        circleColor,
                        1,
                        LineTypes.AntiAlias);
                }

                string statusText =
                    $"WHITE INK {inspection.InkLevelPercent:F1}%  {inspection.Status.ToString().ToUpperInvariant()}";

                // 状态栏必须位于独立画布中，不能直接覆盖 CIS 顶部像素。Bottom 区域和圆心
                // 标注仍按原缩放图坐标绘制，随后将整幅图拼到状态栏下方，不引入坐标偏移。
                using (var legend = new Mat(
                           WhiteInkPreviewLegendHeight,
                           canvas.Width,
                           MatType.CV_8UC3,
                           new Scalar(20, 20, 20)))
                using (var preview = new Mat())
                {
                    Cv2.PutText(
                        legend,
                        statusText,
                        new Point(12, 28),
                        HersheyFonts.HersheySimplex,
                        0.72,
                        statusColor,
                        2,
                        LineTypes.AntiAlias);
                    Cv2.VConcat(new[] { legend, canvas }, preview);
                    Cv2.ImEncode(".jpg", preview, out byte[] encoded, PreviewJpegParameters);
                    return encoded;
                }
            }
        }

        /// <summary>
        /// 把 CIS 变换到 TIFF 目标尺寸。GlobalOnly 走 WarpPerspective；非线性模式按条带生成逆映射并 Remap。
        /// 返回新 Mat，所有权交给调用方。
        /// </summary>
        public static Mat WarpToTiffSpace(Mat cisMat, AlignmentResult alignment, Size tiffSize)
        {
            if (cisMat == null || cisMat.Empty())
                throw new ArgumentException("CIS 图像为空。", nameof(cisMat));
            if (alignment?.GlobalTransform == null || alignment.GlobalTransform.Empty())
                throw new ArgumentException("对准结果不包含有效全局变换。", nameof(alignment));

            if (!alignment.IsNonlinear || alignment.GridX == null ||
                alignment.GridY == null || alignment.ResidualGrid == null)
            {
                // 关闭侧边功能或网格降级时，完整复用传统 H0 + WarpPerspective 路径。
                var globalWarped = new Mat();
                Stopwatch warpWatch = Stopwatch.StartNew();
                Cv2.WarpPerspective(
                    cisMat, globalWarped, alignment.GlobalTransform, tiffSize,
                    InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                warpWatch.Stop();
                alignment.MapGenerationMilliseconds = 0;
                alignment.RemapMilliseconds = warpWatch.Elapsed.TotalMilliseconds;
                alignment.PeakWorkingSetBytes = Math.Max(
                    alignment.PeakWorkingSetBytes, GetPeakWorkingSetBytes());
                return globalWarped;
            }

            // 非线性路径使用目标驱动的逆映射：每个 TIFF 像素先经 H0^-1 找到 CIS，
            // 再叠加双线性插值残差，最终仅由 Remap 采样一次，避免二次 Warp 模糊。
            var warped = new Mat(tiffSize, cisMat.Type(), Scalar.All(0));
            int stripeRows = Math.Max(1, alignment.StripeRows);
            var leftWeights = new float[tiffSize.Width];
            var rightWeights = new float[tiffSize.Width];
            BuildHorizontalResidualWeights(alignment.GridX, leftWeights, rightWeights);
            double mapMilliseconds = 0;
            double remapMilliseconds = 0;

            // mapX/mapY 只覆盖一个条带并循环复用，避免为超大 TIFF 常驻两张全幅 float 映射。
            using (var mapX = new Mat())
            using (var mapY = new Mat())
            using (var remappedStripe = new Mat())
            {
                for (int targetStartY = 0; targetStartY < tiffSize.Height; targetStartY += stripeRows)
                {
                    int rows = Math.Min(stripeRows, tiffSize.Height - targetStartY);
                    mapX.Create(rows, tiffSize.Width, MatType.CV_32FC1);
                    mapY.Create(rows, tiffSize.Width, MatType.CV_32FC1);
                    long temporaryBytes = (long)rows * tiffSize.Width *
                                          (sizeof(float) * 2 + cisMat.ElemSize());
                    alignment.PeakTemporaryBufferBytes = Math.Max(
                        alignment.PeakTemporaryBufferBytes, temporaryBytes);

                    Stopwatch stageWatch = Stopwatch.StartNew();
                    FillRemapStripe(
                        mapX, mapY, targetStartY, alignment.InverseGlobalTransform,
                        alignment.GridY, alignment.ResidualGrid,
                        leftWeights, rightWeights);
                    stageWatch.Stop();
                    mapMilliseconds += stageWatch.Elapsed.TotalMilliseconds;

                    stageWatch.Restart();
                    Cv2.Remap(
                        cisMat, remappedStripe, mapX, mapY,
                        InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                    using (var destination = new Mat(
                               warped, new Rect(0, targetStartY, tiffSize.Width, rows)))
                    {
                        remappedStripe.CopyTo(destination);
                    }
                    stageWatch.Stop();
                    remapMilliseconds += stageWatch.Elapsed.TotalMilliseconds;
                }
            }

            alignment.MapGenerationMilliseconds = mapMilliseconds;
            alignment.RemapMilliseconds = remapMilliseconds;
            alignment.PeakWorkingSetBytes = Math.Max(
                alignment.PeakWorkingSetBytes, GetPeakWorkingSetBytes());
            return warped;
        }

        private static void BuildHorizontalResidualWeights(
            double[] gridX,
            float[] leftWeights,
            float[] rightWeights)
        {
            // 左残差从左边缘线性衰减到中心 0，右残差从中心 0 线性增长到右边缘；
            // 纸张标记范围之外保持最近侧的残差，纵向网格之外则由 FillRemapStripe 仅使用 H0。
            double leftX = gridX[0];
            double centerX = gridX[1];
            double rightX = gridX[2];
            for (int x = 0; x < leftWeights.Length; x++)
            {
                if (x <= leftX)
                {
                    leftWeights[x] = 1;
                    rightWeights[x] = 0;
                }
                else if (x < centerX)
                {
                    leftWeights[x] = (float)((centerX - x) / Math.Max(centerX - leftX, 1e-6));
                    rightWeights[x] = 0;
                }
                else if (x <= rightX)
                {
                    leftWeights[x] = 0;
                    rightWeights[x] = (float)((x - centerX) / Math.Max(rightX - centerX, 1e-6));
                }
                else
                {
                    leftWeights[x] = 0;
                    rightWeights[x] = 1;
                }
            }
        }

        private static unsafe void FillRemapStripe(
            Mat mapX,
            Mat mapY,
            int targetStartY,
            Mat inverseTransform,
            double[] gridY,
            Point2d[,] residuals,
            float[] leftWeights,
            float[] rightWeights)
        {
            // 逆矩阵元素在循环外读取，行内直接写 float 指针；各 Parallel.For 行互不重叠。
            double h00 = inverseTransform.At<double>(0, 0);
            double h01 = inverseTransform.At<double>(0, 1);
            double h02 = inverseTransform.At<double>(0, 2);
            double h10 = inverseTransform.At<double>(1, 0);
            double h11 = inverseTransform.At<double>(1, 1);
            double h12 = inverseTransform.At<double>(1, 2);
            double h20 = inverseTransform.At<double>(2, 0);
            double h21 = inverseTransform.At<double>(2, 1);
            double h22 = inverseTransform.At<double>(2, 2);

            Parallel.For(0, mapX.Rows, localY =>
            {
                int targetY = targetStartY + localY;
                float* mapXRow = (float*)mapX.Ptr(localY);
                float* mapYRow = (float*)mapY.Ptr(localY);
                bool withinGrid = TryGetGridInterval(gridY, targetY, out int gridRow, out double v);
                Point2d leftResidual = new Point2d(0, 0);
                Point2d rightResidual = new Point2d(0, 0);
                if (withinGrid)
                {
                    leftResidual = Lerp(
                        residuals[gridRow, 0], residuals[gridRow + 1, 0], v);
                    rightResidual = Lerp(
                        residuals[gridRow, 2], residuals[gridRow + 1, 2], v);
                }

                for (int x = 0; x < mapX.Cols; x++)
                {
                    double denominator = h20 * x + h21 * targetY + h22;
                    double sourceX = (h00 * x + h01 * targetY + h02) / denominator;
                    double sourceY = (h10 * x + h11 * targetY + h12) / denominator;
                    if (withinGrid)
                    {
                        double leftWeight = leftWeights[x];
                        double rightWeight = rightWeights[x];
                        sourceX += leftResidual.X * leftWeight + rightResidual.X * rightWeight;
                        sourceY += leftResidual.Y * leftWeight + rightResidual.Y * rightWeight;
                    }
                    mapXRow[x] = (float)sourceX;
                    mapYRow[x] = (float)sourceY;
                }
            });
        }

        /// <summary>定位目标 Y 所在的相邻网格层并返回 0..1 插值系数；网格外返回 false。</summary>
        private static bool TryGetGridInterval(
            double[] gridY,
            double targetY,
            out int row,
            out double v)
        {
            row = -1;
            v = 0;
            if (targetY < gridY[0] || targetY > gridY[gridY.Length - 1])
                return false;
            if (targetY >= gridY[gridY.Length - 1])
            {
                row = gridY.Length - 2;
                v = 1;
                return true;
            }
            for (int index = 0; index < gridY.Length - 1; index++)
            {
                if (targetY < gridY[index] || targetY >= gridY[index + 1])
                    continue;
                row = index;
                v = (targetY - gridY[index]) /
                    Math.Max(gridY[index + 1] - gridY[index], 1e-6);
                return true;
            }
            return false;
        }

        private static long GetPeakWorkingSetBytes()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.PeakWorkingSet64;
            }
            catch
            {
                return 0;
            }
        }
    }
}
