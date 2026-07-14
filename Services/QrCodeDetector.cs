using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 面向 CIS 线扫图像的 WeChatQRCode 二维码检测器。
    /// 每个形变候选只调用同一个 CNN 检测/增强/解码后端，不再依赖 ZXing-C++ 回退。
    /// </summary>
    public sealed class QrCodeDetector : IDisposable
    {
        private const double ScaleEpsilon = 0.0001;
        private readonly object _decodeLock = new object();
        private readonly string _modelDirectory;
        private WeChatQRCode _detector;
        private bool _isWarmedUp;
        private bool _disposed;

        /// <summary>横向感兴趣区域的起始 X 坐标（自动换算后）。</summary>
        public int RoiX => ConfigManager.Config.BaseRoiX / Math.Max(1, ConfigManager.Config.DownscaleFactor);

        /// <summary>横向感兴趣区域的宽度（自动换算后）。</summary>
        public int RoiWidth => ConfigManager.Config.BaseRoiWidth / Math.Max(1, ConfigManager.Config.DownscaleFactor);

        /// <summary>最近一次调用发生的参数、模型或本机库异常；未识别到二维码不属于异常。</summary>
        public string LastError { get; private set; }

        /// <summary>最近一次成功识别使用的后端、极性和形变补偿系数。</summary>
        public string LastDecodeStrategy { get; private set; }

        public QrCodeDetector()
            : this(null)
        {
        }

        /// <summary>允许测试或独立组件显式指定四个 WeChatQRCode 模型所在目录。</summary>
        public QrCodeDetector(string modelDirectory)
        {
            _modelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "WeChatQRCode")
                : Path.GetFullPath(modelDirectory);
        }

        /// <summary>
        /// 预加载 CNN 与超分辨率模型。应在开始采集前调用，避免首帧承担模型加载耗时。
        /// </summary>
        public bool Initialize()
        {
            ResetDiagnostics();
            if (_disposed)
            {
                LastError = "二维码检测器已经释放。";
                return false;
            }

            try
            {
                if (!EnsureDetector())
                    return false;

                WarmUpDetector();
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"WeChatQRCode 模型初始化失败：{ex.Message}";
                return false;
            }
        }

        public QrDetectionResult Detect(byte[] data, int width, int height, int stride, int bitsPerPixel)
        {
            ResetDiagnostics();
            if (_disposed)
            {
                LastError = "二维码检测器已经释放。";
                return QrDetectionResult.NotFound;
            }

            if (!TryGetMatType(width, height, stride, bitsPerPixel, out MatType matType))
                return QrDetectionResult.NotFound;

            long requiredBytes = (long)stride * height;
            if (data == null || data.Length < requiredBytes)
            {
                LastError = $"图像缓冲区不足：需要 {requiredBytes} 字节，实际 {data?.Length ?? 0} 字节。";
                return QrDetectionResult.NotFound;
            }

            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                using (var mat = Mat.FromPixelData(height, width, matType, handle.AddrOfPinnedObject(), stride))
                    return DetectCore(mat);
            }
            catch (Exception ex)
            {
                LastError = $"二维码检测失败：{ex.Message}";
                return QrDetectionResult.NotFound;
            }
            finally
            {
                handle.Free();
            }
        }

        public QrDetectionResult Detect(IntPtr dataPtr, int width, int height, int stride, int bitsPerPixel)
        {
            ResetDiagnostics();
            if (_disposed)
            {
                LastError = "二维码检测器已经释放。";
                return QrDetectionResult.NotFound;
            }

            if (dataPtr == IntPtr.Zero)
            {
                LastError = "图像缓冲区指针为空。";
                return QrDetectionResult.NotFound;
            }

            if (!TryGetMatType(width, height, stride, bitsPerPixel, out MatType matType))
                return QrDetectionResult.NotFound;

            try
            {
                using (var mat = Mat.FromPixelData(height, width, matType, dataPtr, stride))
                    return DetectCore(mat);
            }
            catch (Exception ex)
            {
                LastError = $"二维码检测失败：{ex.Message}";
                return QrDetectionResult.NotFound;
            }
        }

        private void ResetDiagnostics()
        {
            LastError = null;
            LastDecodeStrategy = null;
        }

        private bool TryGetMatType(int width, int height, int stride, int bitsPerPixel, out MatType matType)
        {
            matType = default;
            if (width <= 0 || height <= 0 || stride <= 0)
            {
                LastError = $"无效的图像尺寸或步长：{width}x{height}, stride={stride}。";
                return false;
            }

            int channels;
            switch (bitsPerPixel)
            {
                case 8:
                    channels = 1;
                    matType = MatType.CV_8UC1;
                    break;
                case 24:
                    channels = 3;
                    matType = MatType.CV_8UC3;
                    break;
                case 32:
                    channels = 4;
                    matType = MatType.CV_8UC4;
                    break;
                default:
                    LastError = $"不支持 {bitsPerPixel} bpp 图像；仅支持 Gray8、BGR24 和 BGRA32。";
                    return false;
            }

            if ((long)stride < (long)width * channels)
            {
                LastError = $"图像步长 {stride} 小于每行有效字节数 {width * channels}。";
                return false;
            }

            return true;
        }

        private QrDetectionResult DetectCore(Mat source)
        {
            Mat gray = null;
            bool ownsGray = false;
            try
            {
                if (source.Empty() || !EnsureDetector())
                    return QrDetectionResult.NotFound;

                if (source.Channels() == 1)
                {
                    gray = source;
                }
                else
                {
                    gray = new Mat();
                    ownsGray = true;
                    Cv2.CvtColor(
                        source,
                        gray,
                        source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
                }

                int safeX = Math.Max(0, Math.Min(RoiX, gray.Width - 1));
                int safeWidth = RoiWidth > 0
                    ? Math.Min(RoiWidth, gray.Width - safeX)
                    : gray.Width - safeX;
                if (safeWidth <= 0)
                {
                    safeX = 0;
                    safeWidth = gray.Width;
                }

                using (var roi = new Mat(gray, new Rect(safeX, 0, safeWidth, gray.Height)))
                using (var normalizedPolarity = new Mat())
                {
                    bool inverted = ConfigManager.Config.QrInvertPolarity;
                    if (inverted)
                        Cv2.BitwiseNot(roi, normalizedPolarity);
                    else
                        roi.CopyTo(normalizedPolarity);

                    double[] scaleCandidates = BuildScaleYCandidates();
                    for (int i = 0; i < scaleCandidates.Length; i++)
                    {
                        if (!TryDecode(normalizedPolarity, scaleCandidates[i], out DecodeHit hit))
                            continue;

                        LastDecodeStrategy = $"WeChatQRCode, polarity={(inverted ? "inverted" : "original")}, scaleY={hit.ScaleY:F3}";
                        return new QrDetectionResult
                        {
                            Found = true,
                            CenterX = (int)Math.Round(hit.CenterX + safeX),
                            CenterY = (int)Math.Round(hit.CenterY / hit.ScaleY),
                            PixelHeight = hit.PixelHeight / hit.ScaleY,
                            DecodedText = hit.Text
                        };
                    }
                }

                return QrDetectionResult.NotFound;
            }
            catch (Exception ex)
            {
                LastError = $"WeChatQRCode 检测失败：{ex.Message}";
                return QrDetectionResult.NotFound;
            }
            finally
            {
                if (ownsGray)
                    gray?.Dispose();
            }
        }

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
                lock (_decodeLock)
                    _detector.DetectAndDecode(scaled, out boxes, out decodedTexts);

                int resultCount = Math.Min(decodedTexts?.Length ?? 0, boxes?.Length ?? 0);
                for (int i = 0; i < resultCount; i++)
                {
                    if (string.IsNullOrWhiteSpace(decodedTexts[i]) ||
                        !TryGetGeometry(boxes[i], out double centerX, out double centerY, out double pixelHeight))
                        continue;

                    hit = new DecodeHit
                    {
                        Text = decodedTexts[i],
                        CenterX = centerX,
                        CenterY = centerY,
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
        private static bool TryGetGeometry(Mat box, out double centerX, out double centerY, out double pixelHeight)
        {
            centerX = 0;
            centerY = 0;
            pixelHeight = 0;
            if (box == null || box.Empty() || box.Depth() != MatType.CV_32F)
                return false;

            int scalarCount = checked((int)(box.Total() * box.Channels()));
            if (scalarCount < 8)
                return false;

            using (Mat flat = box.Reshape(1, 1))
            {
                var ys = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    double x = flat.Get<float>(0, i * 2);
                    ys[i] = flat.Get<float>(0, i * 2 + 1);
                    centerX += x;
                    centerY += ys[i];
                }

                double edge0Y = Math.Abs(ys[1] - ys[0]);
                double edge1Y = Math.Abs(ys[2] - ys[1]);
                double edge2Y = Math.Abs(ys[3] - ys[2]);
                double edge3Y = Math.Abs(ys[0] - ys[3]);
                double oppositePair02 = (edge0Y + edge2Y) * 0.5;
                double oppositePair13 = (edge1Y + edge3Y) * 0.5;
                pixelHeight = Math.Max(oppositePair02, oppositePair13);
            }

            centerX /= 4.0;
            centerY /= 4.0;
            return true;
        }

        private double[] BuildScaleYCandidates()
        {
            var candidates = new List<double>();
            float[] configured = ConfigManager.Config.QrScaleYCandidates;
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
            public double PixelHeight { get; set; }
            public double ScaleY { get; set; }
        }
    }
}
