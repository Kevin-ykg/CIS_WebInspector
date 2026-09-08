using System;
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
        /// 输出由 C# Mat 分配、C++ 直接写入；GlobalOnly 走 WarpPerspective，Nonlinear 分块逆向 Remap。
        /// 不修改 CIS 原图，不建立全幅 map；原生结果释放后，返回的 Mat 仍由调用方独立持有。
        /// </summary>
        public static Mat WarpToTiffSpace(Mat cisMat, AlignmentResult alignment, Size tiffSize)
        {
            if (cisMat == null || cisMat.Empty()) throw new ArgumentException("CIS 图像为空。", nameof(cisMat));
            if (alignment == null) throw new ArgumentNullException(nameof(alignment));
            var handle = alignment.NativeResource as AlignmentNativeInterop.ResultHandle;
            if (handle == null || handle.IsClosed || handle.IsInvalid)
                throw new ObjectDisposedException(nameof(alignment), "对准结果已释放或缺少原生计算结果。");
            if (tiffSize.Width <= 0 || tiffSize.Height <= 0) throw new ArgumentOutOfRangeException(nameof(tiffSize));
            var output = new Mat(tiffSize, cisMat.Type());
            try
            {
                AlignmentNativeInterop.Warp(handle, cisMat, output);
                UpdateNativeMetrics(alignment, AlignmentNativeInterop.ReadSummary(handle));
                return output;
            }
            catch { output.Dispose(); throw; }
        }
    }
}
