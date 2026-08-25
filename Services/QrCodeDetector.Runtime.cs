using System;
using System.Collections.Generic;
using System.IO;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// WeChatQRCode 调用与资源生命周期，负责最终解码、坐标还原、模型加载预热和确定性释放。
    /// </summary>
    public sealed partial class QrCodeDetector
    {
        private bool TryDecodeResampled(
            Mat source,
            double scaleX,
            double scaleY,
            out DecodeHit hit)
        {
            hit = null;
            if (source == null || source.Empty() ||
                scaleX <= 0 || scaleY <= 0 ||
                double.IsNaN(scaleX) || double.IsInfinity(scaleX) ||
                double.IsNaN(scaleY) || double.IsInfinity(scaleY))
                return false;

            int targetWidth = Math.Max(64, (int)Math.Round(source.Width * scaleX));
            int targetHeight = Math.Max(64, (int)Math.Round(source.Height * scaleY));
            double effectiveScaleX = targetWidth / (double)source.Width;
            double effectiveScaleY = targetHeight / (double)source.Height;

            using (var resampled = new Mat())
            {
                Cv2.Resize(
                    source,
                    resampled,
                    new OpenCvSharp.Size(targetWidth, targetHeight),
                    0,
                    0,
                    effectiveScaleX <= 1.0 && effectiveScaleY <= 1.0
                        ? InterpolationFlags.Area
                        : InterpolationFlags.Linear);

                if (!TryDecode(resampled, 1.0, out DecodeHit scaledHit))
                    return false;

                scaledHit.CenterX /= effectiveScaleX;
                scaledHit.CenterY /= effectiveScaleY;
                scaledHit.PixelWidth /= effectiveScaleX;
                scaledHit.PixelHeight /= effectiveScaleY;
                scaledHit.ScaleY = 1.0;
                hit = scaledHit;
                return true;
            }
        }

        /// <summary>以指定 Y 缩放补偿执行一次检测，并从首个有效解码框提取中心和像素尺寸。</summary>
        private bool TryDecode(Mat source, double scaleY, out DecodeHit hit)
        {
            Mat scaled = null;
            bool ownsScaled = false;
            Mat[] boxes = null;
            try
            {
                if (Math.Abs(scaleY - 1.0) < ScaleEpsilon)
                {
                    scaled = source;
                }
                else
                {
                    scaled = new Mat();
                    ownsScaled = true;
                    Cv2.Resize(source, scaled, new OpenCvSharp.Size(), 1.0, scaleY, InterpolationFlags.Linear);
                }

                string[] decodedTexts;
                // OpenCV DNN/超分辨率检测器实例不按线程安全使用，所有调用与释放共用同一把锁。
                lock (_decodeLock)
                {
                    _decodeAttemptCount++;
                    _detector.DetectAndDecode(scaled, out boxes, out decodedTexts);
                }

                int resultCount = Math.Min(decodedTexts?.Length ?? 0, boxes?.Length ?? 0);
                for (int i = 0; i < resultCount; i++)
                {
                    if (string.IsNullOrWhiteSpace(decodedTexts[i]) ||
                        !TryGetGeometry(
                            boxes[i], out double centerX, out double centerY,
                            out double pixelWidth, out double pixelHeight))
                        continue;

                    hit = new DecodeHit
                    {
                        Text = decodedTexts[i],
                        CenterX = centerX,
                        CenterY = centerY,
                        PixelWidth = pixelWidth,
                        PixelHeight = pixelHeight,
                        ScaleY = scaleY
                    };
                    return true;
                }

                hit = null;
                return false;
            }
            finally
            {
                if (boxes != null)
                {
                    for (int i = 0; i < boxes.Length; i++)
                        boxes[i]?.Dispose();
                }

                if (ownsScaled)
                    scaled?.Dispose();
            }
        }

        /// <summary>
        /// 从 WeChatQRCode 返回的顺时针四边形中提取中心和 Y 方向二维码高度。
        /// 两组对边中，Y 投影较大的一组视为二维码的纵向边，避免使用欧氏长度
        /// 时混入 CIS 横向分辨率。
        /// </summary>
        private static bool TryGetGeometry(
            Mat box,
            out double centerX,
            out double centerY,
            out double pixelWidth,
            out double pixelHeight)
        {
            centerX = 0;
            centerY = 0;
            pixelWidth = 0;
            pixelHeight = 0;
            if (box == null || box.Empty() || box.Depth() != MatType.CV_32F)
                return false;

            int scalarCount = checked((int)(box.Total() * box.Channels()));
            if (scalarCount < 8)
                return false;

            using (Mat flat = box.Reshape(1, 1))
            {
                var xs = new double[4];
                var ys = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    double x = flat.Get<float>(0, i * 2);
                    xs[i] = x;
                    ys[i] = flat.Get<float>(0, i * 2 + 1);
                    centerX += x;
                    centerY += ys[i];
                }

                double edge0X = Math.Abs(xs[1] - xs[0]);
                double edge1X = Math.Abs(xs[2] - xs[1]);
                double edge2X = Math.Abs(xs[3] - xs[2]);
                double edge3X = Math.Abs(xs[0] - xs[3]);
                double edge0Y = Math.Abs(ys[1] - ys[0]);
                double edge1Y = Math.Abs(ys[2] - ys[1]);
                double edge2Y = Math.Abs(ys[3] - ys[2]);
                double edge3Y = Math.Abs(ys[0] - ys[3]);
                double xPair02 = (edge0X + edge2X) * 0.5;
                double xPair13 = (edge1X + edge3X) * 0.5;
                double oppositePair02 = (edge0Y + edge2Y) * 0.5;
                double oppositePair13 = (edge1Y + edge3Y) * 0.5;
                pixelWidth = Math.Max(xPair02, xPair13);
                pixelHeight = Math.Max(oppositePair02, oppositePair13);
            }

            centerX /= 4.0;
            centerY /= 4.0;
            return true;
        }

        /// <summary>过滤无效或重复的纵向缩放配置；配置为空时至少保留原尺度 1.0。</summary>
        private double[] BuildScaleYCandidates()
        {
            var candidates = new List<double>();
            // Configure 已复制数组；检测过程中不会观察到设置窗口的中途修改。
            float[] configured = _scaleYCandidates;
            if (configured != null)
            {
                for (int i = 0; i < configured.Length; i++)
                {
                    double value = configured[i];
                    if (value < 0.25 || value > 4.0 || double.IsNaN(value) || double.IsInfinity(value))
                        continue;

                    bool duplicate = false;
                    for (int j = 0; j < candidates.Count; j++)
                    {
                        if (Math.Abs(candidates[j] - value) < 0.005)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        candidates.Add(value);
                }
            }

            if (candidates.Count == 0)
                candidates.Add(1.0);
            return candidates.ToArray();
        }

        /// <summary>检查四个模型文件并延迟创建唯一检测器实例；创建和并发解码共用同一锁。</summary>
        private bool EnsureDetector()
        {
            lock (_decodeLock)
            {
                if (_detector != null)
                    return true;

                string detectorProto = Path.Combine(_modelDirectory, "detect.prototxt");
                string detectorModel = Path.Combine(_modelDirectory, "detect.caffemodel");
                string superResolutionProto = Path.Combine(_modelDirectory, "sr.prototxt");
                string superResolutionModel = Path.Combine(_modelDirectory, "sr.caffemodel");
                string[] requiredFiles = { detectorProto, detectorModel, superResolutionProto, superResolutionModel };
                for (int i = 0; i < requiredFiles.Length; i++)
                {
                    if (File.Exists(requiredFiles[i]))
                        continue;

                    LastError = $"缺少 WeChatQRCode 模型文件：{requiredFiles[i]}";
                    return false;
                }

                _detector = WeChatQRCode.Create(
                    detectorProto,
                    detectorModel,
                    superResolutionProto,
                    superResolutionModel);
                return true;
            }
        }

        /// <summary>用小型空图预热 DNN，仅为消除首帧初始化抖动，预热结果不参与业务判断。</summary>
        private void WarmUpDetector()
        {
            lock (_decodeLock)
            {
                if (_isWarmedUp)
                    return;

                int warmUpWidth = Math.Max(64, RoiWidth);
                const int warmUpHeight = 2500;
                using (var warmUpImage = new Mat(warmUpHeight, warmUpWidth, MatType.CV_8UC1, Scalar.All(255)))
                {
                    double[] scaleCandidates = BuildScaleYCandidates();
                    for (int i = 0; i < scaleCandidates.Length; i++)
                    {
                        Mat scaled = null;
                        Mat[] boxes = null;
                        try
                        {
                            if (Math.Abs(scaleCandidates[i] - 1.0) < ScaleEpsilon)
                            {
                                scaled = warmUpImage;
                            }
                            else
                            {
                                scaled = new Mat();
                                Cv2.Resize(warmUpImage, scaled, new OpenCvSharp.Size(), 1.0, scaleCandidates[i], InterpolationFlags.Linear);
                            }

                            _detector.DetectAndDecode(scaled, out boxes, out string[] _);
                        }
                        finally
                        {
                            if (boxes != null)
                            {
                                for (int j = 0; j < boxes.Length; j++)
                                    boxes[j]?.Dispose();
                            }

                            if (!ReferenceEquals(scaled, warmUpImage))
                                scaled?.Dispose();
                        }
                    }
                }

                _isWarmedUp = true;
            }
        }

        /// <summary>在无并发解码时释放 WeChatQRCode 原生资源；可重复调用。</summary>
        public void Dispose()
        {
            lock (_decodeLock)
            {
                if (_disposed)
                    return;

                _detector?.Dispose();
                _detector = null;
                _isWarmedUp = false;
                _disposed = true;
            }
        }

        private sealed class DecodeHit
        {
            public string Text { get; set; }
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public double PixelWidth { get; set; }
            public double PixelHeight { get; set; }
            public double ScaleY { get; set; }
        }

        private sealed class FinderPatternEvidence
        {
            public Rect Bounds { get; set; }
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public double EquivalentSide { get; set; }
        }

        /// <summary>失焦定位框模板在原始 ROI 坐标系中的峰值证据。</summary>
        private sealed class BlurredFinderEvidence
        {
            public Point2f Center { get; set; }
            public double ModuleSize { get; set; }
            public double Score { get; set; }
            public bool Inverted { get; set; }
        }

        /// <summary>通过直角、边长和尺度一致性验证的三个失焦定位框。</summary>
        private sealed class BlurredFinderTriple
        {
            public BlurredFinderEvidence Corner { get; set; }
            public BlurredFinderEvidence FirstNeighbor { get; set; }
            public BlurredFinderEvidence SecondNeighbor { get; set; }
            public bool Inverted { get; set; }
            public double RightAngleCosine { get; set; }
            public double EstimatedDimension { get; set; }
            public double AverageTemplateScore { get; set; }
            public double Score { get; set; }
        }

        private sealed class AdaptiveScaleCandidate
        {
            public double TargetQrSide { get; set; }
            public double ScaleX { get; set; }
            public double ScaleY { get; set; }
        }

        private sealed class FinderPerspectiveCandidate
        {
            public Point2f[] Corners { get; set; }
            public int ModuleCount { get; set; }
            public double RightAngleCosine { get; set; }
            public double Score { get; set; }
        }

        /// <summary>
        /// 经典定位器推导出的局部码区。SourceRoi 是真实存在的像素，
        /// Pad* 是理论静区超出传感器画面的部分，仅在临时解码图中补白。
        /// </summary>
        private sealed class QrCandidateRegion
        {
            public Rect SourceRoi { get; set; }
            public int PadLeft { get; set; }
            public int PadTop { get; set; }
            public int PadRight { get; set; }
            public int PadBottom { get; set; }
            public double EstimatedCodeSide { get; set; }
            public double GeometricRelativeScaleY { get; set; }

            public bool HasPadding =>
                PadLeft > 0 || PadTop > 0 || PadRight > 0 || PadBottom > 0;
        }
    }
}
