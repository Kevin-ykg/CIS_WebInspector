using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CIS_WebInspector.Models;
using OpenCvSharp;
using ZXingCpp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 面向 CIS 线扫图像的鲁棒二维码检测器。
    /// 使用 ZXing-C++ 作为解码后端，并针对反色、反光、局部低对比度和 Y 轴拉伸形变执行分级回退。
    /// </summary>
    public sealed class QrCodeDetector : IDisposable
    {
        private const double ScaleEpsilon = 0.0001;
        private readonly BarcodeReader _reader;
        private readonly object _decodeLock = new object();
        private bool _disposed;

        /// <summary>横向感兴趣区域的起始 X 坐标（自动换算后）。</summary>
        public int RoiX => ConfigManager.Config.BaseRoiX / Math.Max(1, ConfigManager.Config.DownscaleFactor);

        /// <summary>横向感兴趣区域的宽度（自动换算后）。</summary>
        public int RoiWidth => ConfigManager.Config.BaseRoiWidth / Math.Max(1, ConfigManager.Config.DownscaleFactor);

        /// <summary>X 轴附加缩放系数。正常保持 1.0。</summary>
        public float DownscaleFactorX { get; set; } = 1.0f;

        /// <summary>Y 轴附加缩放系数。正常保持 1.0。</summary>
        public float DownscaleFactorY { get; set; } = 1.0f;

        /// <summary>
        /// 最近一次调用发生的参数、图像格式或本机库异常。未识别到二维码本身不属于异常。
        /// </summary>
        public string LastError { get; private set; }

        /// <summary>最近一次成功识别使用的预处理路径和缩放系数，便于现场调参。</summary>
        public string LastDecodeStrategy { get; private set; }

        public QrCodeDetector()
        {
            _reader = new BarcodeReader
            {
                Formats = BarcodeFormat.QRCode,
                Binarizer = Binarizer.LocalAverage,
                TryHarder = true,
                TryRotate = true,
                TryInvert = true,
                TryDownscale = true,
                IsPure = false,
                ReturnErrors = false,
                MaxNumberOfSymbols = 1
            };
        }

        public QrDetectionResult Detect(byte[] data, int width, int height, int stride, int bitsPerPixel)
        {
            LastError = null;
            LastDecodeStrategy = null;
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
                {
                    return DetectCore(mat);
                }
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
            LastError = null;
            LastDecodeStrategy = null;
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
                {
                    return DetectCore(mat);
                }
            }
            catch (Exception ex)
            {
                LastError = $"二维码检测失败：{ex.Message}";
                return QrDetectionResult.NotFound;
            }
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
                if (source.Empty())
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
                int configuredWidth = RoiWidth;
                int safeWidth = configuredWidth > 0
                    ? Math.Min(configuredWidth, gray.Width - safeX)
                    : gray.Width - safeX;
                if (safeWidth <= 0)
                {
                    safeX = 0;
                    safeWidth = gray.Width;
                }

                using (var roi = new Mat(gray, new Rect(safeX, 0, safeWidth, gray.Height)))
                {
                    double scaleX = NormalizeScale(DownscaleFactorX);
                    double[] scaleYs = BuildScaleYCandidates();

                    // 第一层：保留原始灰度，让 ZXing-C++ 的局部均值二值化器自行处理亮度不均和反色。
                    if (TryDecodeScaleSweep(roi, scaleX, scaleYs, InterpolationFlags.Linear, "原始灰度", out DecodeHit hit))
                        return ToDetectionResult(hit, safeX);

                    // 第二层：CLAHE 只在首轮失败后启用，用于反光或暗部导致的局部低对比度。
                    using (var contrast = new Mat())
                    using (var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8)))
                    {
                        clahe.Apply(roi, contrast);
                        if (TryDecodeScaleSweep(contrast, scaleX, scaleYs, InterpolationFlags.Linear, "CLAHE", out hit))
                            return ToDetectionResult(hit, safeX);

                        // 第三层：自适应阈值是最终回退，不再用单一 Otsu 阈值抹掉反光区的灰度细节。
                        int blockSize = GetAdaptiveBlockSize(contrast.Width, contrast.Height);
                        if (blockSize >= 3)
                        {
                            using (var binary = new Mat())
                            {
                                Cv2.AdaptiveThreshold(
                                    contrast,
                                    binary,
                                    255,
                                    AdaptiveThresholdTypes.GaussianC,
                                    ThresholdTypes.Binary,
                                    blockSize,
                                    3);
                                if (TryDecodeScaleSweep(binary, scaleX, scaleYs, InterpolationFlags.Nearest, "自适应阈值", out hit))
                                    return ToDetectionResult(hit, safeX);
                            }
                        }
                    }
                }

                return QrDetectionResult.NotFound;
            }
            catch (Exception ex)
            {
                LastError = $"二维码检测失败：{ex.Message}";
                return QrDetectionResult.NotFound;
            }
            finally
            {
                if (ownsGray)
                    gray?.Dispose();
            }
        }

        private bool TryDecodeScaleSweep(
            Mat source,
            double scaleX,
            IReadOnlyList<double> scaleYs,
            InterpolationFlags interpolation,
            string strategy,
            out DecodeHit hit)
        {
            for (int i = 0; i < scaleYs.Count; i++)
            {
                if (TryDecode(source, scaleX, scaleYs[i], interpolation, out hit))
                {
                    hit.Strategy = strategy;
                    return true;
                }
            }

            hit = null;
            return false;
        }

        private bool TryDecode(
            Mat source,
            double scaleX,
            double scaleY,
            InterpolationFlags interpolation,
            out DecodeHit hit)
        {
            Mat scaled = null;
            bool ownsScaled = false;
            Barcode[] barcodes = null;
            try
            {
                if (Math.Abs(scaleX - 1.0) < ScaleEpsilon && Math.Abs(scaleY - 1.0) < ScaleEpsilon)
                {
                    scaled = source;
                }
                else
                {
                    scaled = new Mat();
                    ownsScaled = true;
                    Cv2.Resize(source, scaled, new OpenCvSharp.Size(), scaleX, scaleY, interpolation);
                }

                var image = new ImageView(
                    scaled.Data,
                    scaled.Width,
                    scaled.Height,
                    ImageFormat.Lum,
                    checked((int)scaled.Step()),
                    1);

                lock (_decodeLock)
                {
                    barcodes = _reader.From(image);
                }

                if (barcodes == null)
                {
                    hit = null;
                    return false;
                }

                for (int i = 0; i < barcodes.Length; i++)
                {
                    Barcode barcode = barcodes[i];
                    if (barcode == null || !barcode.IsValid || string.IsNullOrWhiteSpace(barcode.Text))
                        continue;

                    Position position = barcode.Position;
                    hit = new DecodeHit
                    {
                        Text = barcode.Text,
                        CenterX = (position.TopLeft.X + position.TopRight.X + position.BottomRight.X + position.BottomLeft.X) / 4.0,
                        CenterY = (position.TopLeft.Y + position.TopRight.Y + position.BottomRight.Y + position.BottomLeft.Y) / 4.0,
                        ScaleX = scaleX,
                        ScaleY = scaleY
                    };
                    return true;
                }

                hit = null;
                return false;
            }
            finally
            {
                if (barcodes != null)
                {
                    for (int i = 0; i < barcodes.Length; i++)
                        barcodes[i]?.Dispose();
                }

                if (ownsScaled)
                    scaled?.Dispose();
            }
        }

        private QrDetectionResult ToDetectionResult(DecodeHit hit, int roiX)
        {
            LastDecodeStrategy = $"{hit.Strategy}, scaleX={hit.ScaleX:F3}, scaleY={hit.ScaleY:F3}";
            return new QrDetectionResult
            {
                Found = true,
                CenterX = (int)Math.Round(hit.CenterX / hit.ScaleX + roiX),
                CenterY = (int)Math.Round(hit.CenterY / hit.ScaleY),
                DecodedText = hit.Text
            };
        }

        private double[] BuildScaleYCandidates()
        {
            var candidates = new List<double>();
            double extraScaleY = NormalizeScale(DownscaleFactorY);
            AddScaleCandidate(candidates, extraScaleY);

            float[] configuredScales = ConfigManager.Config.BaseScaleYs;
            if (configuredScales != null)
            {
                for (int i = 0; i < configuredScales.Length; i++)
                {
                    double configured = configuredScales[i];
                    if (configured > 0.05 && configured < 10 && !double.IsNaN(configured) && !double.IsInfinity(configured))
                    {
                        AddScaleCandidate(candidates, extraScaleY / configured);
                        AddScaleCandidate(candidates, extraScaleY * configured);
                    }
                }
            }

            candidates.Sort((left, right) => Math.Abs(Math.Log(left)).CompareTo(Math.Abs(Math.Log(right))));
            return candidates.ToArray();
        }

        private static void AddScaleCandidate(List<double> candidates, double value)
        {
            value = Math.Max(0.25, Math.Min(4.0, value));
            for (int i = 0; i < candidates.Count; i++)
            {
                if (Math.Abs(candidates[i] - value) < 0.005)
                    return;
            }

            candidates.Add(value);
        }

        private static double NormalizeScale(float value)
        {
            return value > 0.05f && value < 10f && !float.IsNaN(value) && !float.IsInfinity(value)
                ? value
                : 1.0;
        }

        private static int GetAdaptiveBlockSize(int width, int height)
        {
            int maxSize = Math.Min(width, height);
            if (maxSize < 3)
                return 0;

            int blockSize = Math.Min(51, maxSize);
            if (blockSize % 2 == 0)
                blockSize--;
            return Math.Max(3, blockSize);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private sealed class DecodeHit
        {
            public string Text { get; set; }
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public double ScaleX { get; set; }
            public double ScaleY { get; set; }
            public string Strategy { get; set; }
        }
    }
}
