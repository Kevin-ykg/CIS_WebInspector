using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 全局对准 Mark 诊断图。这里只读取已经完成的匹配结果，不重新检测 Mark，
    /// 也不修改 H0、侧边残差网格或后续缺陷检测输入。
    /// </summary>
    public static partial class ImageAligner
    {
        // 诊断图限制最大尺寸，避免为核对 Mark 再复制一张完整的超大 CIS/TIFF 图。
        private const int AlignmentPreviewMaximumWidth = 2400;
        private const int AlignmentPreviewMaximumHeight = 3200;
        private const int AlignmentPreviewLegendHeight = 76;

        /// <summary>
        /// 分别在 CIS 源图和 TIFF 排版图的缩略图上标注上下 20 mm 大 Mark，
        /// 以及已启用并通过质量检查的左右 4 mm 小 Mark，返回成功保存的绝对路径。
        /// </summary>
        public static IReadOnlyList<string> SaveAlignmentMarkPreviews(
            Mat cisMat,
            Mat tiffMat,
            AlignmentResult alignment,
            string outputDirectory)
        {
            if (cisMat == null || cisMat.Empty())
                throw new ArgumentException("CIS 图像为空。", nameof(cisMat));
            if (tiffMat == null || tiffMat.Empty())
                throw new ArgumentException("TIFF 图像为空。", nameof(tiffMat));
            if (alignment == null)
                throw new ArgumentNullException(nameof(alignment));
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("对准诊断图输出目录为空。", nameof(outputDirectory));

            Directory.CreateDirectory(outputDirectory);
            var savedPaths = new List<string>(2);

            // 两张预览顺序保存、顺序释放，峰值只包含一张缩略图。
            string cisPath = Path.Combine(outputDirectory, "AlignmentMarks_CIS_Source.jpg");
            using (Mat cisPreview = CreateAlignmentMarkPreview(cisMat, alignment, false))
            {
                if (!Cv2.ImWrite(cisPath, cisPreview, PreviewJpegParameters))
                    throw new IOException("OpenCV 保存 CIS Mark 标注图失败：" + cisPath);
            }
            savedPaths.Add(Path.GetFullPath(cisPath));

            string tiffPath = Path.Combine(outputDirectory, "AlignmentMarks_TIFF_Layout.jpg");
            using (Mat tiffPreview = CreateAlignmentMarkPreview(tiffMat, alignment, true))
            {
                if (!Cv2.ImWrite(tiffPath, tiffPreview, PreviewJpegParameters))
                    throw new IOException("OpenCV 保存 TIFF Mark 标注图失败：" + tiffPath);
            }
            savedPaths.Add(Path.GetFullPath(tiffPath));

            return savedPaths;
        }

        private static Mat CreateAlignmentMarkPreview(
            Mat source,
            AlignmentResult alignment,
            bool useTiffCoordinates)
        {
            // 标题栏使用独立的顶部画布，不能直接覆盖源图。缩放时先为标题栏预留高度，
            // 保证最终输出仍不超过诊断图最大高度，同时完整保留源图第 0 行开始的内容。
            int imageMaximumHeight = Math.Max(
                1,
                AlignmentPreviewMaximumHeight - AlignmentPreviewLegendHeight);
            double scale = Math.Min(
                1.0,
                Math.Min(
                    AlignmentPreviewMaximumWidth / (double)Math.Max(1, source.Width),
                    imageMaximumHeight / (double)Math.Max(1, source.Height)));

            using (var resized = new Mat())
            {
                if (scale < 1.0)
                    Cv2.Resize(source, resized, new Size(), scale, scale, InterpolationFlags.Area);
                else
                    source.CopyTo(resized);

                using (var imageCanvas = new Mat())
                {
                    if (resized.Channels() == 1)
                        Cv2.CvtColor(resized, imageCanvas, ColorConversionCodes.GRAY2BGR);
                    else if (resized.Channels() == 4)
                        Cv2.CvtColor(resized, imageCanvas, ColorConversionCodes.BGRA2BGR);
                    else
                        resized.CopyTo(imageCanvas);

                    // Mark 坐标仍以缩放后的源图左上角为原点。先在源图画标记，随后整体拼到
                    // 标题栏下方，避免额外引入 Y 偏移并保证任何顶部 Mark 都不会被标题遮住。
                    DrawGlobalMarkOverlay(imageCanvas, alignment.GlobalMarkPoints, scale, useTiffCoordinates);
                    DrawSideMarkOverlay(imageCanvas, alignment.ControlPoints, scale, useTiffCoordinates);
                    return AddAlignmentPreviewLegend(imageCanvas, alignment, useTiffCoordinates);
                }
            }
        }

        /// <summary>
        /// 在已标注源图上方增加独立说明栏。返回值拥有自己的像素缓冲区，调用方负责释放。
        /// </summary>
        private static Mat AddAlignmentPreviewLegend(
            Mat imageCanvas,
            AlignmentResult alignment,
            bool useTiffCoordinates)
        {
            using (var legend = new Mat(
                       AlignmentPreviewLegendHeight,
                       imageCanvas.Width,
                       MatType.CV_8UC3,
                       new Scalar(20, 20, 20)))
            {
                DrawAlignmentPreviewLegend(legend, alignment, useTiffCoordinates);
                var output = new Mat();
                Cv2.VConcat(new[] { legend, imageCanvas }, output);
                return output;
            }
        }

        /// <summary>
        /// 绿色圆为参与 H0 求解的上下大 Mark 对应点。CIS/TIFF 使用相同的排名标签，
        /// 可直接核对是否发生跨序号配对。当前版本未保存 RANSAC mask，因此不虚构内/外点颜色。
        /// </summary>
        private static void DrawGlobalMarkOverlay(
            Mat canvas,
            IReadOnlyList<AlignmentGlobalMarkPoint> points,
            double scale,
            bool useTiffCoordinates)
        {
            if (points == null)
                return;

            foreach (AlignmentGlobalMarkPoint mark in points)
            {
                Point2d sourcePoint = useTiffCoordinates ? mark.TiffPoint : mark.CisPoint;
                if (!TryScaleDiagnosticPoint(sourcePoint, scale, canvas.Size(), out Point center))
                    continue;

                var color = new Scalar(40, 220, 40);
                Cv2.Circle(canvas, center, 11, color, 3, LineTypes.AntiAlias);
                Cv2.Circle(canvas, center, 3, color, -1, LineTypes.AntiAlias);
                string label = (mark.RowName == "Top" ? "T" : "B") + mark.Index.ToString("00");
                DrawDiagnosticLabel(canvas, label, center + new Point(8, -10), color);
            }
        }

        /// <summary>
        /// 紫色十字是 TIFF 理论位置或 H0 逆映射的 CIS 粗预测位置；青色圆是实测小 Mark；
        /// 橙色圆是孤立缺点插值。CIS 图上的箭头直观显示 H0 预测到实测点的残差方向。
        /// </summary>
        private static void DrawSideMarkOverlay(
            Mat canvas,
            IReadOnlyList<AlignmentControlPoint> points,
            double scale,
            bool useTiffCoordinates)
        {
            if (points == null || points.Count == 0)
                return;

            foreach (AlignmentControlPoint controlPoint in points.Where(point =>
                         !point.IsVirtual && point.Column != AlignmentControlColumn.Center))
            {
                Point2d prediction = useTiffCoordinates
                    ? controlPoint.ExpectedTiffPoint
                    : controlPoint.CoarseCisPoint;
                if (!TryScaleDiagnosticPoint(prediction, scale, canvas.Size(), out Point predictedCenter))
                    continue;

                DrawDiagnosticCross(canvas, predictedCenter, 7, new Scalar(220, 60, 220));

                bool hasFinalPoint = controlPoint.IsDetected || controlPoint.IsInterpolated;
                if (!hasFinalPoint)
                    continue;

                Point2d finalPoint = useTiffCoordinates
                    ? (controlPoint.IsDetected && IsUsableDiagnosticPoint(controlPoint.DetectedTiffPoint)
                        ? controlPoint.DetectedTiffPoint
                        : controlPoint.ExpectedTiffPoint)
                    : controlPoint.DetectedCisPoint;
                if (!TryScaleDiagnosticPoint(finalPoint, scale, canvas.Size(), out Point detectedCenter))
                    continue;

                Scalar color = controlPoint.IsInterpolated
                    ? new Scalar(0, 165, 255)
                    : new Scalar(255, 220, 0);
                if (!useTiffCoordinates && controlPoint.IsDetected && predictedCenter != detectedCenter)
                {
                    Cv2.ArrowedLine(
                        canvas,
                        predictedCenter,
                        detectedCenter,
                        color,
                        2,
                        LineTypes.AntiAlias,
                        0,
                        0.25);
                }

                Cv2.Circle(canvas, detectedCenter, 8, color, 2, LineTypes.AntiAlias);
                Cv2.Circle(canvas, detectedCenter, 2, color, -1, LineTypes.AntiAlias);
                string side = controlPoint.Column == AlignmentControlColumn.Left ? "L" : "R";
                DrawDiagnosticLabel(
                    canvas,
                    side + controlPoint.RowIndex.ToString("00"),
                    detectedCenter + new Point(7, -7),
                    color);
            }
        }

        private static void DrawAlignmentPreviewLegend(
            Mat canvas,
            AlignmentResult alignment,
            bool useTiffCoordinates)
        {
            int bannerHeight = Math.Min(AlignmentPreviewLegendHeight, canvas.Height);
            if (bannerHeight <= 0)
                return;

            Cv2.Rectangle(
                canvas,
                new Rect(0, 0, canvas.Width, bannerHeight),
                new Scalar(20, 20, 20),
                -1);
            string sourceName = useTiffCoordinates ? "TIFF LAYOUT" : "CIS SOURCE";
            string firstLine =
                $"{sourceName} | ALIGN={alignment.Mode} | GLOBAL 20mm: GREEN=MATCHED";
            string secondLine = alignment.ControlPoints != null && alignment.ControlPoints.Count > 0
                ? "SIDE 4mm: CYAN=DETECTED ORANGE=INTERPOLATED MAGENTA=PREDICTED"
                : "SIDE 4mm: DISABLED OR UNAVAILABLE";
            Cv2.PutText(
                canvas,
                firstLine,
                new Point(12, Math.Min(29, bannerHeight - 4)),
                HersheyFonts.HersheySimplex,
                0.62,
                new Scalar(245, 245, 245),
                2,
                LineTypes.AntiAlias);
            if (bannerHeight >= 52)
            {
                Cv2.PutText(
                    canvas,
                    secondLine,
                    new Point(12, 59),
                    HersheyFonts.HersheySimplex,
                    0.56,
                    new Scalar(245, 245, 245),
                    1,
                    LineTypes.AntiAlias);
            }
        }

        private static bool TryScaleDiagnosticPoint(
            Point2d sourcePoint,
            double scale,
            Size canvasSize,
            out Point scaledPoint)
        {
            scaledPoint = default(Point);
            if (!IsUsableDiagnosticPoint(sourcePoint))
                return false;

            int x = (int)Math.Round(sourcePoint.X * scale);
            int y = (int)Math.Round(sourcePoint.Y * scale);
            if (x < 0 || y < 0 || x >= canvasSize.Width || y >= canvasSize.Height)
                return false;

            scaledPoint = new Point(x, y);
            return true;
        }

        private static bool IsUsableDiagnosticPoint(Point2d point)
        {
            return !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
                   !double.IsNaN(point.Y) && !double.IsInfinity(point.Y) &&
                   (Math.Abs(point.X) > 1e-6 || Math.Abs(point.Y) > 1e-6);
        }

        private static void DrawDiagnosticCross(Mat canvas, Point center, int radius, Scalar color)
        {
            Cv2.Line(
                canvas,
                center + new Point(-radius, 0),
                center + new Point(radius, 0),
                color,
                2,
                LineTypes.AntiAlias);
            Cv2.Line(
                canvas,
                center + new Point(0, -radius),
                center + new Point(0, radius),
                color,
                2,
                LineTypes.AntiAlias);
        }

        private static void DrawDiagnosticLabel(Mat canvas, string text, Point origin, Scalar color)
        {
            int x = Math.Max(2, Math.Min(canvas.Width - 2, origin.X));
            int y = Math.Max(14, Math.Min(canvas.Height - 2, origin.Y));
            Cv2.PutText(
                canvas,
                text,
                new Point(x, y),
                HersheyFonts.HersheySimplex,
                0.48,
                new Scalar(15, 15, 15),
                3,
                LineTypes.AntiAlias);
            Cv2.PutText(
                canvas,
                text,
                new Point(x, y),
                HersheyFonts.HersheySimplex,
                0.48,
                color,
                1,
                LineTypes.AntiAlias);
        }
    }
}
