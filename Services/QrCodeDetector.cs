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
        // OpenCV WeChatQRCode 默认会把大图压到固定面积后再检测。对于 CIS 的长条 ROI，
        // 二维码在整条中的占比偏小，容易在缩放时丢失定位点；局部候选最长边控制在约 640 px，
        // 可在不放大噪声的前提下保留足够的模块边缘。
        private const int FinderCandidateTargetLongEdge = 640;
        private const double FinderQuietZoneScale = 0.20;
        // 对可靠的局部二维码候选，把“码区本身”归一化到 WeChatQRCode 更容易处理的
        // 中等尺寸；若候选被传感器边缘截断，会先补出缺失静区。这里仍由当前候选框
        // 实时换算比例，不保存任何针对单张历史样本的固定 scaleX/scaleY。
        private static readonly double[] LocalCandidateTargetQrSides = { 300.0, 320.0, 280.0 };
        private const double BoundaryRecoveryTargetQrSide = 576.0;
        private const double LowContrastTargetQrSide = 224.0;
        private const int LocalAdaptiveMaxCandidates = 7;
        // 自适应路径先根据定位框间距估算原二维码边长，再归一化到若干稳定工作尺寸。
        // 这里保存的是解码输入尺寸，不是某个失败样本对应的缩放系数。
        private static readonly double[] AdaptiveTargetQrSides = { 544.0, 512.0, 576.0 };
        private const int AdaptiveMaxCandidates = 8;
        private const double AdaptiveMinScale = 0.35;
        private const double AdaptiveMaxScale = 1.75;
        private const double AdaptiveMinRelativeScaleY = 0.55;
        private const double AdaptiveMaxRelativeScaleY = 1.80;
        // 当常规检测只能看到定位框、却无法通过校验时，将三个定位框恢复为标准二维码平面。
        // 统一到每模块 12 px 后再交回 WeChatQRCode，可避免长条 ROI 缩放和轻微透视共同破坏模块采样。
        private const int FinderPerspectivePixelsPerModule = 12;
        private const int FinderPerspectiveMaxEvidence = 12;
        private const int FinderPerspectiveMaxCandidates = 3;
        // 严重失焦时，定位框的黑白环会互相融合，基于二值轮廓层级的常规证据可能完全消失。
        // 最终兜底仅在前述路径全部失败后，于缩小图上用定位框固定结构寻找三点几何；
        // 找不到满足直角、尺度和间距约束的三点组时，不会调用额外的 DNN 解码。
        private const int BlurredFinderSearchLongEdge = 640;
        private const double BlurredFinderMinimumScore = 0.60;
        private const int BlurredFinderPeaksPerScale = 5;
        private const int BlurredFinderMaxEvidence = 16;
        private const int BlurredFinderMaxTriples = 2;
        private const int BlurredRecoveryPixelsPerModule = 24;
        private static readonly int[] BlurredFinderTemplateModuleSizes =
            { 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 16, 18 };
        private static readonly double[] BlurredRecoveryExpansionScales =
            { 1.0, 0.98, 1.035, 0.97 };
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

        /// <summary>从托管像素缓冲区同步检测；方法返回前不会保留 data 的指针或 Mat 视图。</summary>
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
                // Mat 只借用固定后的数组内存，不拥有像素；DetectCore 必须在 handle.Free 前同步完成。
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

        /// <summary>兼容原生缓冲区入口；调用方必须保证指针在整个同步检测期间有效。</summary>
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

        /// <summary>每次公开调用前清空上次诊断，区分“本次未命中”与历史异常。</summary>
        private void ResetDiagnostics()
        {
            LastError = null;
            LastDecodeStrategy = null;
        }

        /// <summary>校验尺寸、位深和 stride，并映射到 OpenCV MatType。</summary>
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

        /// <summary>
        /// 统一检测主路径：转灰度 → 截取横向 ROI → 极性归一化 → 常规纵向尺度 →
        /// 定位框证据门控的受限重采样/局部候选，所有路径最终均由 WeChatQRCode 解码。
        /// </summary>
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

                // 核心 ROI 严格对应用户配置，用于提取定位框轮廓证据；保护带只用于
                // WeChatQRCode 和经典四角定位，避免额外大面积背景改变 Otsu 阈值及轮廓层级。
                int coreRoiX = safeX;
                int coreRoiWidth = safeWidth;

                // 配置 ROI 表示二维码的常规安装区域，但二维码贴近传感器边缘时，
                // 其定位框可能落在 ROI 左边界之外，几何定位也需要少量右侧背景判断边界。
                // 因此在两侧各增加约 1/6 ROI 宽度的受限保护带，而不是把整幅 7800 px
                // 图像都交给二维码网络。
                int horizontalGuard = Math.Max(64, safeWidth / 6);
                int leftGuard = Math.Min(safeX, horizontalGuard);
                int rightGuard = Math.Min(
                    gray.Width - (safeX + safeWidth),
                    horizontalGuard);
                safeX -= leftGuard;
                safeWidth = Math.Min(
                    gray.Width - safeX,
                    safeWidth + leftGuard + rightGuard);

                // CIS 上二维码只限制横向安装区域，纵向保留全帧，以覆盖二维码跨帧拼接后的任意 Y。
                using (var roi = new Mat(gray, new Rect(safeX, 0, safeWidth, gray.Height)))

                using (var normalizedPolarity = new Mat())
                {
                    bool inverted = ConfigManager.Config.QrInvertPolarity;
                    if (inverted)
                        Cv2.BitwiseNot(roi, normalizedPolarity);
                    else
                        roi.CopyTo(normalizedPolarity);

                    // 线扫速度变化主要表现为纵向压缩/拉伸，因此只枚举 scaleY；命中后再把 Y/高度还原。
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
                            PixelWidth = hit.PixelWidth,
                            PixelHeight = hit.PixelHeight / hit.ScaleY,
                            DecodedText = hit.Text
                        };
                    }

                    // 常规路径失败后，从嵌套方框中提取并去重二维码定位框证据。
                    // 单帧和跨帧组合使用完全相同的门控与候选生成逻辑。
                    List<FinderPatternEvidence> finderEvidence;
                    try
                    {
                        int evidenceOffsetX = coreRoiX - safeX;
                        using (var evidenceView = new Mat(
                            normalizedPolarity,
                            new Rect(
                                evidenceOffsetX,
                                0,
                                coreRoiWidth,
                                normalizedPolarity.Height)))
                        {
                            finderEvidence = FindFinderPatternEvidence(evidenceView);
                        }
                    }
                    catch
                    {
                        finderEvidence = new List<FinderPatternEvidence>();
                    }

                    bool hasReliableFinderEvidence = TryBuildAdaptiveScaleCandidates(
                        finderEvidence,
                        out List<AdaptiveScaleCandidate> adaptiveCandidates,
                        out int coherentFinderCount,
                        out double estimatedQrSide,
                        out double geometricRelativeScaleY);
                    if (hasReliableFinderEvidence)
                    {
                        for (int i = 0; i < adaptiveCandidates.Count; i++)
                        {
                            AdaptiveScaleCandidate candidate = adaptiveCandidates[i];
                            if (!TryDecodeResampled(
                                normalizedPolarity,
                                candidate.ScaleX,
                                candidate.ScaleY,
                                out DecodeHit adaptiveHit))
                                continue;

                            LastDecodeStrategy =
                                $"WeChatQRCode, adaptive-finder, finderCount={coherentFinderCount}, " +
                                $"estimatedSide={estimatedQrSide:F1}, targetSide={candidate.TargetQrSide:F1}, " +
                                $"geometryScaleY={geometricRelativeScaleY:F3}, " +
                                $"polarity={(inverted ? "inverted" : "original")}, " +
                                $"scaleX={candidate.ScaleX:F3}, scaleY={candidate.ScaleY:F3}";
                            return new QrDetectionResult
                            {
                                Found = true,
                                CenterX = (int)Math.Round(adaptiveHit.CenterX + safeX),
                                CenterY = (int)Math.Round(adaptiveHit.CenterY),
                                PixelWidth = adaptiveHit.PixelWidth,
                                PixelHeight = adaptiveHit.PixelHeight,
                                DecodedText = adaptiveHit.Text
                            };
                        }
                    }

                    // 受限重采样仍未解码时，再用经典二维码几何定位器圈出四角点候选区，
                    // 并对局部区域做一次 Area 降采样。这既提高二维码在 CNN 输入中的占比，
                    // 也抑制 CIS 横向条纹和重采样混叠。
                    if (hasReliableFinderEvidence &&
                        TryFindQrCandidateRegion(
                            normalizedPolarity,
                            out QrCandidateRegion candidateRegion))
                    {
                        using (var candidateView =
                            new Mat(normalizedPolarity, candidateRegion.SourceRoi))
                        {
                            Mat candidateInput = candidateView;
                            Mat paddedCandidate = null;
                            Mat resizedCandidate = null;
                            try
                            {
                                // 经典定位器允许角点落到图像外，因此可以反推出被物理边界截断的
                                // 码区/静区。用白色补齐这部分只恢复二维码规范要求的背景，
                                // 不臆造任何数据模块；纠错仍完全交给 WeChatQRCode。
                                if (candidateRegion.HasPadding)
                                {
                                    paddedCandidate = new Mat();
                                    Cv2.CopyMakeBorder(
                                        candidateView,
                                        paddedCandidate,
                                        candidateRegion.PadTop,
                                        candidateRegion.PadBottom,
                                        candidateRegion.PadLeft,
                                        candidateRegion.PadRight,
                                        BorderTypes.Constant,
                                        Scalar.All(255));
                                    candidateInput = paddedCandidate;
                                }

                                // 全帧自适应路径仍失败时，二维码在长条 ROI 中的占比通常过小。
                                // 这里只对已由“嵌套定位框 + 经典四角点”双重确认的局部码区
                                // 尝试少量动态工作尺度，不增加锐化、二值化等不稳定预处理分支。
                                List<AdaptiveScaleCandidate> localCandidates =
                                    BuildLocalAdaptiveScaleCandidates(
                                        candidateRegion.EstimatedCodeSide,
                                        candidateRegion.GeometricRelativeScaleY,
                                        candidateRegion.HasPadding);
                                for (int i = 0; i < localCandidates.Count; i++)
                                {
                                    AdaptiveScaleCandidate localCandidate = localCandidates[i];
                                    if (!TryDecodeResampled(
                                        candidateInput,
                                        localCandidate.ScaleX,
                                        localCandidate.ScaleY,
                                        out DecodeHit localHit))
                                        continue;

                                    LastDecodeStrategy =
                                        $"WeChatQRCode, " +
                                        $"{(candidateRegion.HasPadding ? "edge-padded" : "finder-local-adaptive")}, " +
                                        $"finderCount={coherentFinderCount}, " +
                                        $"roi={candidateRegion.SourceRoi.X}," +
                                        $"{candidateRegion.SourceRoi.Y}," +
                                        $"{candidateRegion.SourceRoi.Width}x" +
                                        $"{candidateRegion.SourceRoi.Height}, " +
                                        $"padding={candidateRegion.PadLeft}," +
                                        $"{candidateRegion.PadTop}," +
                                        $"{candidateRegion.PadRight}," +
                                        $"{candidateRegion.PadBottom}, " +
                                        $"estimatedSide={candidateRegion.EstimatedCodeSide:F1}, " +
                                        $"targetSide={localCandidate.TargetQrSide:F1}, " +
                                        $"geometryScaleY={candidateRegion.GeometricRelativeScaleY:F3}, " +
                                        $"polarity={(inverted ? "inverted" : "original")}, " +
                                        $"scaleX={localCandidate.ScaleX:F3}, " +
                                        $"scaleY={localCandidate.ScaleY:F3}";
                                    return CreateCandidateResult(
                                        localHit,
                                        safeX,
                                        candidateRegion,
                                        1.0,
                                        1.0);
                                }

                                double preScale = Math.Min(
                                    1.0,
                                    FinderCandidateTargetLongEdge /
                                    (double)Math.Max(candidateInput.Width, candidateInput.Height));
                                if (preScale < 1.0 - ScaleEpsilon)
                                {
                                    resizedCandidate = new Mat();
                                    int targetWidth = Math.Max(
                                        64,
                                        (int)Math.Round(candidateInput.Width * preScale));
                                    int targetHeight = Math.Max(
                                        64,
                                        (int)Math.Round(candidateInput.Height * preScale));
                                    Cv2.Resize(
                                        candidateInput,
                                        resizedCandidate,
                                        new OpenCvSharp.Size(targetWidth, targetHeight),
                                        0,
                                        0,
                                        InterpolationFlags.Area);
                                    candidateInput = resizedCandidate;
                                }

                                for (int i = 0; i < scaleCandidates.Length; i++)
                                {
                                    if (!TryDecode(candidateInput, scaleCandidates[i], out DecodeHit hit))
                                        continue;

                                    double scaleX = preScale;
                                    double scaleY = preScale * hit.ScaleY;
                                    LastDecodeStrategy =
                                        $"WeChatQRCode, finder-roi={candidateRegion.SourceRoi.X}," +
                                        $"{candidateRegion.SourceRoi.Y}," +
                                        $"{candidateRegion.SourceRoi.Width}x" +
                                        $"{candidateRegion.SourceRoi.Height}, " +
                                        $"padding={candidateRegion.PadLeft}," +
                                        $"{candidateRegion.PadTop}," +
                                        $"{candidateRegion.PadRight}," +
                                        $"{candidateRegion.PadBottom}, " +
                                        $"preScale={preScale:F3}, polarity={(inverted ? "inverted" : "original")}, " +
                                        $"scaleY={hit.ScaleY:F3}";
                                    return CreateCandidateResult(
                                        hit,
                                        safeX,
                                        candidateRegion,
                                        scaleX,
                                        scaleY);
                                }
                            }
                            finally
                            {
                                resizedCandidate?.Dispose();
                                paddedCandidate?.Dispose();
                            }
                        }
                    }

                    // 白墨缺失时，原本的白码可能变成低对比度黑码。只有主流程失败且仍有
                    // 至少一个嵌套定位框迹象时，才尝试相反极性的受限低对比度定位路径，
                    // 避免在普通无二维码帧上无条件把完整识别流程执行两遍。
                    // 常规缩放和轴对齐局部 ROI 都失败后，才启用三定位框透视恢复。
                    // 光照展平图只负责找定位框；真正解码仍使用原灰度，避免二值化或锐化改坏数据模块。
                    var perspectiveCoreRoi = new Rect(
                        coreRoiX - safeX,
                        0,
                        coreRoiWidth,
                        normalizedPolarity.Height);
                    if (finderEvidence.Count > 0 &&
                        TryDecodeFinderPerspective(
                        normalizedPolarity,
                        perspectiveCoreRoi,
                        safeX,
                        out QrDetectionResult perspectiveResult,
                        out string perspectiveStrategy))
                    {
                        LastDecodeStrategy =
                            $"{perspectiveStrategy}, polarity={(inverted ? "inverted" : "original")}";
                        return perspectiveResult;
                    }

                    // 配置极性适合常规生产二维码；少数样本的前景/背景极性相反。
                    // 仅在上一步失败且两幅图确实不同时，再用原始灰度复用同一套受约束几何恢复。
                    if (finderEvidence.Count > 0 &&
                        inverted &&
                        TryDecodeFinderPerspective(
                            roi,
                            perspectiveCoreRoi,
                            safeX,
                            out perspectiveResult,
                            out perspectiveStrategy))
                    {
                        LastDecodeStrategy =
                            $"{perspectiveStrategy}, polarity=opposite-original";
                        return perspectiveResult;
                    }

                    if (finderEvidence.Count > 0 &&
                        TryDecodeLowContrastOppositePolarity(
                            roi,
                            inverted,
                            safeX,
                            finderEvidence.Count,
                            out QrDetectionResult lowContrastResult,
                            out string lowContrastStrategy))
                    {
                        LastDecodeStrategy = lowContrastStrategy;
                        return lowContrastResult;
                    }

                    // 严重失焦会抹掉嵌套轮廓，但三个定位框的整体明暗结构仍可能存在。
                    // 该路径不枚举大量锐化/阈值参数，而是在缩小后的高对比颜色（或灰度）通道上
                    // 寻找三个满足二维码几何约束的模板峰，再恢复标准平面交给同一 WeChatQRCode。
                    // 模板相关的正负号同时覆盖黑码与白码，因此无需把完整流程无条件跑两遍。
                    if (TryDecodeBlurredFinderRecovery(
                        source,
                        new Rect(safeX, 0, safeWidth, source.Height),
                        safeX,
                        out QrDetectionResult blurredResult,
                        out string blurredStrategy))
                    {
                        LastDecodeStrategy = blurredStrategy;
                        return blurredResult;
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

        /// <summary>
        /// 对“模块边缘已经失焦，但三个定位框仍保留整体结构”的图像做受限恢复。
        /// 模板只负责定位和估算模块尺度；识别成功仍以 WeChatQRCode 解出非空文本为准。
        /// </summary>
        private bool TryDecodeBlurredFinderRecovery(
            Mat source,
            Rect roiRect,
            int sourceOffsetX,
            out QrDetectionResult result,
            out string strategy)
        {
            result = QrDetectionResult.NotFound;
            strategy = null;
            if (source == null || source.Empty() ||
                roiRect.Width < 96 || roiRect.Height < 96)
                return false;

            using (var sourceView = new Mat(source, roiRect))
            using (var searchChannel = new Mat())
            using (var secondaryChannel = new Mat())
            {
                // 绿色通道用于稳定定位三个模板峰；当前困难样本存在明显青色散焦边，
                // 所以找到几何后优先在红色通道解码。灰度输入保持原数据。
                if (sourceView.Channels() == 1)
                    sourceView.CopyTo(searchChannel);
                else
                {
                    Cv2.ExtractChannel(sourceView, searchChannel, 1);
                    Cv2.ExtractChannel(sourceView, secondaryChannel, 2);
                }

                Cv2.MeanStdDev(
                    searchChannel,
                    out Scalar _,
                    out Scalar channelStdDev);
                if (channelStdDev.Val0 < 8.0)
                    return false;

                List<BlurredFinderEvidence> evidence =
                    FindBlurredFinderTemplateEvidence(searchChannel);
                List<BlurredFinderTriple> triples =
                    BuildBlurredFinderTriples(evidence);
                for (int tripleIndex = 0;
                    tripleIndex < triples.Count;
                    tripleIndex++)
                {
                    BlurredFinderTriple triple = triples[tripleIndex];
                    List<int> moduleCounts = BuildBlurredModuleCountCandidates(
                        triple.EstimatedDimension);
                    for (int moduleIndex = 0;
                        moduleIndex < moduleCounts.Count;
                        moduleIndex++)
                    {
                        int moduleCount = moduleCounts[moduleIndex];
                            Point2f[] codeCorners = BuildBlurredCodeCorners(
                            triple,
                            moduleCount);
                            if (!IsPlausibleBlurredQuadrilateral(
                                codeCorners,
                                searchChannel.Width,
                                searchChannel.Height))
                                continue;

                        int decodeChannelCount = secondaryChannel.Empty() ? 1 : 2;
                        for (int decodeChannelIndex = 0;
                            decodeChannelIndex < decodeChannelCount;
                            decodeChannelIndex++)
                        {
                            Mat decodeChannel = secondaryChannel.Empty()
                                ? searchChannel
                                : decodeChannelIndex == 0
                                    ? secondaryChannel
                                    : searchChannel;
                            string channelName = sourceView.Channels() == 1
                                ? "gray"
                                : decodeChannelIndex == 0 ? "red" : "green";
                            for (int expansionIndex = 0;
                                expansionIndex < BlurredRecoveryExpansionScales.Length;
                                expansionIndex++)
                            {
                                double expansion =
                                    BlurredRecoveryExpansionScales[expansionIndex];
                                Point2f[] expandedCorners = ExpandCorners(
                                    codeCorners,
                                    expansion);
                                if (!TryDecodeBlurredRectified(
                                    decodeChannel,
                                    expandedCorners,
                                    moduleCount,
                                    triple.Inverted,
                                    out DecodeHit hit,
                                    out string rectifiedPreprocessing))
                                    continue;

                                double centerX = 0;
                                double centerY = 0;
                                for (int cornerIndex = 0;
                                    cornerIndex < codeCorners.Length;
                                    cornerIndex++)
                                {
                                    centerX += codeCorners[cornerIndex].X;
                                    centerY += codeCorners[cornerIndex].Y;
                                }
                                centerX /= codeCorners.Length;
                                centerY /= codeCorners.Length;

                                double pixelWidth =
                                    (PointDistance(codeCorners[0], codeCorners[1]) +
                                     PointDistance(codeCorners[3], codeCorners[2])) * 0.5;
                                double pixelHeight =
                                    (PointDistance(codeCorners[0], codeCorners[3]) +
                                     PointDistance(codeCorners[1], codeCorners[2])) * 0.5;
                                result = new QrDetectionResult
                                {
                                    Found = true,
                                    CenterX = Math.Max(
                                        0,
                                        (int)Math.Round(centerX + sourceOffsetX)),
                                    CenterY = Math.Max(0, (int)Math.Round(centerY)),
                                    PixelWidth = pixelWidth,
                                    PixelHeight = pixelHeight,
                                    DecodedText = hit.Text
                                };
                                strategy =
                                    $"WeChatQRCode, blurred-finder-template, channel=" +
                                    $"{channelName}, " +
                                    $"polarity={(triple.Inverted ? "inverted" : "original")}, " +
                                    $"templateScore={triple.AverageTemplateScore:F3}, " +
                                    $"rightAngleCosine={triple.RightAngleCosine:F3}, " +
                                    $"estimatedDimension={triple.EstimatedDimension:F1}, " +
                                    $"moduleCount={moduleCount}, expansion={expansion:F3}, " +
                                    $"rectified={rectifiedPreprocessing}";
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 在最长边 640 px 的工作图上匹配 7×7 定位框模板。
        /// 正相关代表黑码白底，负相关代表白码黑底；两种极性共用一次模板计算。
        /// </summary>
        private static List<BlurredFinderEvidence>
            FindBlurredFinderTemplateEvidence(Mat source)
        {
            var raw = new List<BlurredFinderEvidence>();
            double searchScale = Math.Min(
                1.0,
                BlurredFinderSearchLongEdge /
                (double)Math.Max(source.Width, source.Height));
            using (var searchImage = new Mat())
            {
                if (searchScale < 1.0 - ScaleEpsilon)
                {
                    Cv2.Resize(
                        source,
                        searchImage,
                        new OpenCvSharp.Size(
                            Math.Max(64, (int)Math.Round(source.Width * searchScale)),
                            Math.Max(64, (int)Math.Round(source.Height * searchScale))),
                        0,
                        0,
                        InterpolationFlags.Area);
                }
                else
                {
                    source.CopyTo(searchImage);
                }

                for (int scaleIndex = 0;
                    scaleIndex < BlurredFinderTemplateModuleSizes.Length;
                    scaleIndex++)
                {
                    int moduleSize =
                        BlurredFinderTemplateModuleSizes[scaleIndex];
                    int templateSide = moduleSize * 7;
                    if (templateSide >= searchImage.Width ||
                        templateSide >= searchImage.Height)
                        continue;

                    using (var idealTemplate = CreateBlurredFinderTemplate(
                        moduleSize))
                    using (var response = new Mat())
                    {
                        Cv2.MatchTemplate(
                            searchImage,
                            idealTemplate,
                            response,
                            TemplateMatchModes.CCoeffNormed);
                        CollectBlurredFinderPeaks(
                            response,
                            moduleSize,
                            templateSide,
                            searchScale,
                            false,
                            raw);
                        CollectBlurredFinderPeaks(
                            response,
                            moduleSize,
                            templateSide,
                            searchScale,
                            true,
                            raw);
                    }
                }
            }

            raw.Sort((left, right) => right.Score.CompareTo(left.Score));
            var distinct = new List<BlurredFinderEvidence>();
            for (int i = 0;
                i < raw.Count && distinct.Count < BlurredFinderMaxEvidence;
                i++)
            {
                BlurredFinderEvidence candidate = raw[i];
                bool duplicate = false;
                for (int j = 0; j < distinct.Count; j++)
                {
                    BlurredFinderEvidence accepted = distinct[j];
                    if (candidate.Inverted != accepted.Inverted)
                        continue;

                    double dx = candidate.Center.X - accepted.Center.X;
                    double dy = candidate.Center.Y - accepted.Center.Y;
                    double mergeDistance = Math.Max(
                        candidate.ModuleSize,
                        accepted.ModuleSize) * 7.0 * 0.45;
                    if (dx * dx + dy * dy <= mergeDistance * mergeDistance)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    distinct.Add(candidate);
            }
            return distinct;
        }

        private static Mat CreateBlurredFinderTemplate(int moduleSize)
        {
            int side = moduleSize * 7;
            var blurred = new Mat();
            using (var ideal = new Mat(
                side,
                side,
                MatType.CV_8UC1,
                Scalar.All(255)))
            {
                for (int row = 0; row < 7; row++)
                {
                    for (int column = 0; column < 7; column++)
                    {
                        bool dark =
                            row == 0 || row == 6 ||
                            column == 0 || column == 6 ||
                            (row >= 2 && row <= 4 &&
                             column >= 2 && column <= 4);
                        if (!dark)
                            continue;

                        Cv2.Rectangle(
                            ideal,
                            new Rect(
                                column * moduleSize,
                                row * moduleSize,
                                moduleSize,
                                moduleSize),
                            Scalar.All(0),
                            -1);
                    }
                }

                Cv2.GaussianBlur(
                    ideal,
                    blurred,
                    new OpenCvSharp.Size(0, 0),
                    Math.Max(0.8, moduleSize * 0.22));
            }
            return blurred;
        }

        private static void CollectBlurredFinderPeaks(
            Mat response,
            int moduleSize,
            int templateSide,
            double searchScale,
            bool inverted,
            List<BlurredFinderEvidence> output)
        {
            using (var work = response.Clone())
            {
                for (int peakIndex = 0;
                    peakIndex < BlurredFinderPeaksPerScale;
                    peakIndex++)
                {
                    Cv2.MinMaxLoc(
                        work,
                        out double minimum,
                        out double maximum,
                        out Point minimumLocation,
                        out Point maximumLocation);
                    double score = inverted ? -minimum : maximum;
                    Point location = inverted
                        ? minimumLocation
                        : maximumLocation;
                    if (score < BlurredFinderMinimumScore)
                        break;

                    output.Add(new BlurredFinderEvidence
                    {
                        Center = new Point2f(
                            (float)((location.X + templateSide * 0.5) /
                                searchScale),
                            (float)((location.Y + templateSide * 0.5) /
                                searchScale)),
                        ModuleSize = moduleSize / searchScale,
                        Score = score,
                        Inverted = inverted
                    });

                    int suppressionRadius = templateSide / 2;
                    int left = Math.Max(0, location.X - suppressionRadius);
                    int top = Math.Max(0, location.Y - suppressionRadius);
                    int right = Math.Min(
                        work.Width,
                        location.X + templateSide + suppressionRadius);
                    int bottom = Math.Min(
                        work.Height,
                        location.Y + templateSide + suppressionRadius);
                    if (right <= left || bottom <= top)
                        break;
                    Cv2.Rectangle(
                        work,
                        new Rect(left, top, right - left, bottom - top),
                        inverted ? Scalar.All(1.0) : Scalar.All(-1.0),
                        -1);
                }
            }
        }

        private static List<BlurredFinderTriple> BuildBlurredFinderTriples(
            List<BlurredFinderEvidence> evidence)
        {
            var triples = new List<BlurredFinderTriple>();
            if (evidence == null || evidence.Count < 3)
                return triples;

            for (int firstIndex = 0;
                firstIndex < evidence.Count - 2;
                firstIndex++)
            {
                for (int secondIndex = firstIndex + 1;
                    secondIndex < evidence.Count - 1;
                    secondIndex++)
                {
                    for (int thirdIndex = secondIndex + 1;
                        thirdIndex < evidence.Count;
                        thirdIndex++)
                    {
                        BlurredFinderEvidence[] current =
                        {
                            evidence[firstIndex],
                            evidence[secondIndex],
                            evidence[thirdIndex]
                        };
                        if (current[0].Inverted != current[1].Inverted ||
                            current[0].Inverted != current[2].Inverted)
                            continue;

                        double minimumModule = Math.Min(
                            current[0].ModuleSize,
                            Math.Min(
                                current[1].ModuleSize,
                                current[2].ModuleSize));
                        double maximumModule = Math.Max(
                            current[0].ModuleSize,
                            Math.Max(
                                current[1].ModuleSize,
                                current[2].ModuleSize));
                        if (minimumModule <= 0 ||
                            maximumModule / minimumModule > 1.60)
                            continue;

                        for (int cornerIndex = 0;
                            cornerIndex < 3;
                            cornerIndex++)
                        {
                            BlurredFinderEvidence corner = current[cornerIndex];
                            BlurredFinderEvidence first =
                                current[(cornerIndex + 1) % 3];
                            BlurredFinderEvidence second =
                                current[(cornerIndex + 2) % 3];
                            Point2f firstVector = first.Center - corner.Center;
                            Point2f secondVector = second.Center - corner.Center;
                            double firstLength = Math.Sqrt(
                                firstVector.X * firstVector.X +
                                firstVector.Y * firstVector.Y);
                            double secondLength = Math.Sqrt(
                                secondVector.X * secondVector.X +
                                secondVector.Y * secondVector.Y);
                            if (firstLength <= maximumModule * 8.0 ||
                                secondLength <= maximumModule * 8.0)
                                continue;

                            double cosine = Math.Abs(
                                (firstVector.X * secondVector.X +
                                 firstVector.Y * secondVector.Y) /
                                (firstLength * secondLength));
                            double legRatio =
                                Math.Max(firstLength, secondLength) /
                                Math.Min(firstLength, secondLength);
                            if (cosine > 0.28 || legRatio > 1.35)
                                continue;

                            double[] moduleSizes =
                            {
                                current[0].ModuleSize,
                                current[1].ModuleSize,
                                current[2].ModuleSize
                            };
                            Array.Sort(moduleSizes);
                            double medianModule = moduleSizes[1];
                            double estimatedDimension =
                                (firstLength + secondLength) * 0.5 /
                                medianModule + 7.0;
                            if (estimatedDimension < 17.0 ||
                                estimatedDimension > 65.0)
                                continue;

                            double averageTemplateScore =
                                (current[0].Score +
                                 current[1].Score +
                                 current[2].Score) / 3.0;
                            triples.Add(new BlurredFinderTriple
                            {
                                Corner = corner,
                                FirstNeighbor = first,
                                SecondNeighbor = second,
                                Inverted = corner.Inverted,
                                RightAngleCosine = cosine,
                                EstimatedDimension = estimatedDimension,
                                AverageTemplateScore = averageTemplateScore,
                                Score =
                                    cosine +
                                    Math.Abs(Math.Log(legRatio)) +
                                    Math.Abs(Math.Log(
                                        maximumModule / minimumModule)) +
                                    (1.0 - averageTemplateScore)
                            });
                        }
                    }
                }
            }

            triples.Sort((left, right) => left.Score.CompareTo(right.Score));
            if (triples.Count > BlurredFinderMaxTriples)
                triples.RemoveRange(
                    BlurredFinderMaxTriples,
                    triples.Count - BlurredFinderMaxTriples);
            return triples;
        }

        private static List<int> BuildBlurredModuleCountCandidates(
            double estimatedDimension)
        {
            int estimatedVersion = Math.Max(
                0,
                Math.Min(
                    9,
                    (int)Math.Round((estimatedDimension - 21.0) / 4.0)));
            var result = new List<int>();
            // 失焦会把定位框黑白环融合，使模板最佳模块尺寸偏小、推算版本偏大。
            // 因此先试低一级版本，再试四舍五入版本和高一级版本。
            int[] versionOffsets = { -1, 0, 1 };
            for (int i = 0; i < versionOffsets.Length; i++)
            {
                int version = estimatedVersion + versionOffsets[i];
                if (version < 0 || version > 9)
                    continue;
                int moduleCount = 21 + version * 4;
                if (!result.Contains(moduleCount))
                    result.Add(moduleCount);
            }
            return result;
        }

        private static Point2f[] BuildBlurredCodeCorners(
            BlurredFinderTriple triple,
            int moduleCount)
        {
            double centerSpan = moduleCount - 7.0;
            Point2f firstAxis = ScaleVector(
                triple.FirstNeighbor.Center - triple.Corner.Center,
                1.0 / centerSpan);
            Point2f secondAxis = ScaleVector(
                triple.SecondNeighbor.Center - triple.Corner.Center,
                1.0 / centerSpan);
            const double near = -3.5;
            double far = moduleCount - 3.5;
            return OrderQuadrilateralCorners(new[]
            {
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, near),
                    ScaleVector(secondAxis, near)),
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, far),
                    ScaleVector(secondAxis, near)),
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, far),
                    ScaleVector(secondAxis, far)),
                AddVectors(
                    triple.Corner.Center,
                    ScaleVector(firstAxis, near),
                    ScaleVector(secondAxis, far))
            });
        }

        private static Point2f[] ExpandCorners(
            Point2f[] corners,
            double scale)
        {
            float centerX = 0;
            float centerY = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                centerX += corners[i].X;
                centerY += corners[i].Y;
            }
            centerX /= corners.Length;
            centerY /= corners.Length;

            var expanded = new Point2f[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                expanded[i] = new Point2f(
                    centerX + (float)((corners[i].X - centerX) * scale),
                    centerY + (float)((corners[i].Y - centerY) * scale));
            }
            return expanded;
        }

        private static bool IsPlausibleBlurredQuadrilateral(
            Point2f[] corners,
            int sourceWidth,
            int sourceHeight)
        {
            if (corners == null || corners.Length != 4)
                return false;

            double width =
                (PointDistance(corners[0], corners[1]) +
                 PointDistance(corners[3], corners[2])) * 0.5;
            double height =
                (PointDistance(corners[0], corners[3]) +
                 PointDistance(corners[1], corners[2])) * 0.5;
            if (width < 96 || height < 96 ||
                Math.Max(width, height) / Math.Min(width, height) > 1.50)
                return false;

            double allowedOutside =
                Math.Max(sourceWidth, sourceHeight) * 0.15;
            for (int i = 0; i < corners.Length; i++)
            {
                if (corners[i].X < -allowedOutside ||
                    corners[i].Y < -allowedOutside ||
                    corners[i].X > sourceWidth + allowedOutside ||
                    corners[i].Y > sourceHeight + allowedOutside)
                    return false;
            }
            return true;
        }

        private bool TryDecodeBlurredRectified(
            Mat source,
            Point2f[] corners,
            int moduleCount,
            bool invertPolarity,
            out DecodeHit hit,
            out string preprocessing)
        {
            hit = null;
            preprocessing = null;
            int codeSide = moduleCount * BlurredRecoveryPixelsPerModule;
            int quietZone = 4 * BlurredRecoveryPixelsPerModule;
            Point2f[] destination =
            {
                new Point2f(0, 0),
                // 以模块边界而不是最后一个像素中心建立变换；这样每个模块正好占据
                // BlurredRecoveryPixelsPerModule 个像素，避免大失焦样本在边缘累积半像素相位误差。
                new Point2f(codeSide, 0),
                new Point2f(codeSide, codeSide),
                new Point2f(0, codeSide)
            };

            using (Mat transform = Cv2.GetPerspectiveTransform(
                corners,
                destination))
            using (var straight = new Mat())
            using (var normalizedPolarity = new Mat())
            using (var padded = new Mat())
            using (var normalized = new Mat())
            {
                Cv2.WarpPerspective(
                    source,
                    straight,
                    transform,
                    new OpenCvSharp.Size(codeSide, codeSide),
                    InterpolationFlags.Cubic,
                    BorderTypes.Replicate);
                if (invertPolarity)
                    Cv2.BitwiseNot(straight, normalizedPolarity);
                else
                    straight.CopyTo(normalizedPolarity);

                Cv2.CopyMakeBorder(
                    normalizedPolarity,
                    padded,
                    quietZone,
                    quietZone,
                    quietZone,
                    quietZone,
                    BorderTypes.Constant,
                    Scalar.All(255));
                Cv2.Normalize(padded, normalized, 0, 255, NormTypes.MinMax);
                if (TryDecode(normalized, 1.0, out hit))
                {
                    preprocessing = "normalized-gray";
                    return true;
                }

                using (var background = new Mat())
                using (var flattened = new Mat())
                {
                    Cv2.GaussianBlur(
                        normalized,
                        background,
                        new OpenCvSharp.Size(0, 0),
                        BlurredRecoveryPixelsPerModule * 1.8);
                    Cv2.Divide(normalized, background, flattened, 255.0);
                    if (!TryDecode(flattened, 1.0, out hit))
                        return false;
                    preprocessing = "illumination-flattened";
                    return true;
                }
            }
        }

        /// <summary>
        /// 在配置极性的完整流程失败后，针对低对比度反极性二维码执行一次受限恢复。
        /// Gaussian/Otsu 只提供四角几何，最终文本始终从原灰度局部图由 WeChatQRCode 解出。
        /// </summary>
        private bool TryDecodeLowContrastOppositePolarity(
            Mat originalRoi,
            bool primaryInverted,
            int safeX,
            int finderEvidenceCount,
            out QrDetectionResult result,
            out string strategy)
        {
            result = null;
            strategy = null;
            Mat alternativeOwned = null;
            try
            {
                Mat alternativePolarity;
                if (primaryInverted)
                {
                    // 主流程使用了反色图，备用路径回到传感器原始灰度。
                    alternativePolarity = originalRoi;
                }
                else
                {
                    // 配置为原极性时，备用路径只构造一次反色 Mat。
                    alternativeOwned = new Mat();
                    Cv2.BitwiseNot(originalRoi, alternativeOwned);
                    alternativePolarity = alternativeOwned;
                }

                if (!TryFindLowContrastCandidateRegion(
                    alternativePolarity,
                    out QrCandidateRegion candidateRegion))
                    return false;

                using (var candidateView =
                    new Mat(alternativePolarity, candidateRegion.SourceRoi))
                {
                    Mat candidateInput = candidateView;
                    Mat paddedCandidate = null;
                    try
                    {
                        if (candidateRegion.HasPadding)
                        {
                            paddedCandidate = new Mat();
                            Cv2.CopyMakeBorder(
                                candidateView,
                                paddedCandidate,
                                candidateRegion.PadTop,
                                candidateRegion.PadBottom,
                                candidateRegion.PadLeft,
                                candidateRegion.PadRight,
                                BorderTypes.Constant,
                                Scalar.All(255));
                            candidateInput = paddedCandidate;
                        }

                        double scaleX =
                            LowContrastTargetQrSide /
                            candidateRegion.EstimatedCodeSide;
                        double scaleY =
                            scaleX *
                            Clamp(
                                candidateRegion.GeometricRelativeScaleY * 0.70,
                                AdaptiveMinRelativeScaleY,
                                AdaptiveMaxRelativeScaleY);
                        // 低对比度路径有意把约 700 px 的颗粒码降到约 224 px，
                        // 所需比例可能小于常规自适应路径的 0.35 下限，因此在本地单独校验。
                        if (scaleX < 0.20 || scaleX > AdaptiveMaxScale ||
                            scaleY < 0.20 || scaleY > AdaptiveMaxScale)
                            return false;

                        if (!TryDecodeResampled(
                            candidateInput,
                            scaleX,
                            scaleY,
                            out DecodeHit hit))
                            return false;

                        result = CreateCandidateResult(
                            hit,
                            safeX,
                            candidateRegion,
                            1.0,
                            1.0);
                        strategy =
                            $"WeChatQRCode, low-contrast-opposite-polarity, " +
                            $"finderEvidence={finderEvidenceCount}, locator=gaussian5-otsu, " +
                            $"roi={candidateRegion.SourceRoi.X}," +
                            $"{candidateRegion.SourceRoi.Y}," +
                            $"{candidateRegion.SourceRoi.Width}x" +
                            $"{candidateRegion.SourceRoi.Height}, " +
                            $"padding={candidateRegion.PadLeft}," +
                            $"{candidateRegion.PadTop}," +
                            $"{candidateRegion.PadRight}," +
                            $"{candidateRegion.PadBottom}, " +
                            $"estimatedSide={candidateRegion.EstimatedCodeSide:F1}, " +
                            $"targetSide={LowContrastTargetQrSide:F1}, " +
                            $"geometryScaleY={candidateRegion.GeometricRelativeScaleY:F3}, " +
                            $"scaleX={scaleX:F3}, scaleY={scaleY:F3}";
                        return true;
                    }
                    finally
                    {
                        paddedCandidate?.Dispose();
                    }
                }
            }
            finally
            {
                alternativeOwned?.Dispose();
            }
        }

        /// <summary>
        /// 使用 OpenCV 的经典二维码几何定位器取得四个角点，但不采用它的解码文本。
        /// 扩展后的局部区域仍交给 WeChatQRCode 做唯一的业务解码，避免维护两套结果规则。
        /// </summary>
        private static bool TryFindQrCandidateRegion(
            Mat source,
            out QrCandidateRegion candidateRegion)
        {
            candidateRegion = null;
            if (source == null || source.Empty() || source.Width < 64 || source.Height < 64)
                return false;

            using (var locator = new QRCodeDetector())
            {
                Point2f[] corners;
                try
                {
                    if (!locator.Detect(source, out corners))
                        return false;
                }
                catch
                {
                    // 几何定位器只是可选增强路径。个别纹理可能触发 OpenCV 内部断言，
                    // 此时按“没有候选框”处理，不能把正常的无二维码帧升级为采集异常。
                    return false;
                }

                return TryCreateQrCandidateRegion(
                    corners,
                    source.Width,
                    source.Height,
                    out candidateRegion);
            }
        }

        /// <summary>
        /// 低对比度黑码的模块纹理会破坏经典定位器的边缘连续性。先轻度模糊颗粒，
        /// 再用 Otsu 生成仅供几何定位的二值图；最终解码仍使用未经二值化的灰度局部图。
        /// </summary>
        private static bool TryFindLowContrastCandidateRegion(
            Mat source,
            out QrCandidateRegion candidateRegion)
        {
            candidateRegion = null;
            if (source == null || source.Empty() || source.Width < 64 || source.Height < 64)
                return false;

            using (var blurred = new Mat())
            using (var binary = new Mat())
            using (var locator = new QRCodeDetector())
            {
                Cv2.GaussianBlur(source, blurred, new OpenCvSharp.Size(5, 5), 0);
                Cv2.Threshold(
                    blurred,
                    binary,
                    0,
                    255,
                    ThresholdTypes.Binary | ThresholdTypes.Otsu);

                Point2f[] corners;
                try
                {
                    if (!locator.Detect(binary, out corners))
                        return false;
                }
                catch
                {
                    return false;
                }

                return TryCreateQrCandidateRegion(
                    corners,
                    source.Width,
                    source.Height,
                    out candidateRegion);
            }
        }

        /// <summary>把四角坐标转换为带理论静区和边界补白信息的局部候选。</summary>
        private static bool TryCreateQrCandidateRegion(
            Point2f[] corners,
            int sourceWidth,
            int sourceHeight,
            out QrCandidateRegion candidateRegion)
        {
            candidateRegion = null;
            if (corners == null || corners.Length < 4)
                return false;

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                double x = corners[i].X;
                double y = corners[i].Y;
                if (double.IsNaN(x) || double.IsInfinity(x) ||
                    double.IsNaN(y) || double.IsInfinity(y))
                    return false;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            double codeWidth = maxX - minX;
            double codeHeight = maxY - minY;
            if (codeWidth < 32 || codeHeight < 32)
                return false;

            // 四角点仅覆盖码区，向外保留约 20% 静区。这里先保留“理论区域”，
            // 再与真实图像求交，因此可以知道左/右/上/下究竟缺了多少像素。
            int quietZone = Math.Max(
                8,
                (int)Math.Round(Math.Max(codeWidth, codeHeight) * FinderQuietZoneScale));
            int rawX0 = (int)Math.Floor(minX) - quietZone;
            int rawY0 = (int)Math.Floor(minY) - quietZone;
            int rawX1 = (int)Math.Ceiling(maxX) + quietZone;
            int rawY1 = (int)Math.Ceiling(maxY) + quietZone;
            int rawWidth = rawX1 - rawX0;
            int rawHeight = rawY1 - rawY0;
            if (rawWidth < 64 || rawHeight < 64 ||
                rawWidth > sourceWidth * 2 ||
                rawHeight > sourceHeight * 2)
                return false;

            int x0 = Math.Max(0, rawX0);
            int y0 = Math.Max(0, rawY0);
            int x1 = Math.Min(sourceWidth, rawX1);
            int y1 = Math.Min(sourceHeight, rawY1);
            if (x1 - x0 < 64 || y1 - y0 < 64)
                return false;

            candidateRegion = new QrCandidateRegion
            {
                SourceRoi = new Rect(x0, y0, x1 - x0, y1 - y0),
                PadLeft = x0 - rawX0,
                PadTop = y0 - rawY0,
                PadRight = rawX1 - x1,
                PadBottom = rawY1 - y1,
                EstimatedCodeSide = Math.Max(codeWidth, codeHeight),
                GeometricRelativeScaleY = Clamp(
                    codeWidth / codeHeight,
                    AdaptiveMinRelativeScaleY,
                    AdaptiveMaxRelativeScaleY)
            };
            return true;
        }

        /// <summary>
        /// 把局部候选中的识别坐标还原到完整帧：先撤销局部缩放，再扣除人工补出的静区，
        /// 最后叠加真实 ROI 偏移。这样补白只服务于解码，不会改变拼接使用的二维码全局坐标。
        /// </summary>
        private static QrDetectionResult CreateCandidateResult(
            DecodeHit hit,
            int safeX,
            QrCandidateRegion candidateRegion,
            double scaleX,
            double scaleY)
        {
            double centerX =
                safeX +
                candidateRegion.SourceRoi.X +
                hit.CenterX / scaleX -
                candidateRegion.PadLeft;
            double centerY =
                candidateRegion.SourceRoi.Y +
                hit.CenterY / scaleY -
                candidateRegion.PadTop;
            return new QrDetectionResult
            {
                Found = true,
                CenterX = Math.Max(0, (int)Math.Round(centerX)),
                CenterY = Math.Max(0, (int)Math.Round(centerY)),
                PixelWidth = hit.PixelWidth / scaleX,
                PixelHeight = hit.PixelHeight / scaleY,
                DecodedText = hit.Text
            };
        }

        /// <summary>
        /// 从轮廓树提取二维码定位框候选，并按中心距离/重叠关系去除同一定位框的内外层重复轮廓。
        /// 这里只提供几何证据，不直接决定二维码是否存在，最终文本仍必须由 WeChatQRCode 解出。
        /// </summary>
        /// <summary>
        /// 使用三个定位框恢复二维码平面。展平图只用于寻找几何，透视变换和解码始终使用原灰度图。
        /// 该分支只在常规流程失败后执行，并要求三个同尺度定位框构成近似直角，避免把普通嵌套图案送入解码器。
        /// </summary>
        private bool TryDecodeFinderPerspective(
            Mat source,
            Rect evidenceRoi,
            int sourceOffsetX,
            out QrDetectionResult result,
            out string strategy)
        {
            result = QrDetectionResult.NotFound;
            strategy = null;
            if (source == null || source.Empty())
                return false;

            int left = Math.Max(0, evidenceRoi.X);
            int top = Math.Max(0, evidenceRoi.Y);
            int right = Math.Min(source.Width, evidenceRoi.Right);
            int bottom = Math.Min(source.Height, evidenceRoi.Bottom);
            if (right - left < 64 || bottom - top < 64)
                return false;

            var safeEvidenceRoi = new Rect(
                left,
                top,
                right - left,
                bottom - top);
            List<FinderPatternEvidence> evidence;
            using (var evidenceView = new Mat(source, safeEvidenceRoi))
            using (var background = new Mat())
            using (var flattened = new Mat())
            {
                // 大尺度背景估计抑制白带过曝、渐变和 CIS 条纹；不对数据模块做二值化。
                // 背景尺度不能跟整张截图尺寸同步放大：同一个二维码放进更大的画布后，
                // 过大的 sigma 会把定位框外边缘一并纳入背景并造成数像素中心漂移。
                // 18 px 只用于定位框取证；真正解码仍回到原始灰度图。
                const double sigma = 18.0;
                Cv2.GaussianBlur(
                    evidenceView,
                    background,
                    new OpenCvSharp.Size(0, 0),
                    sigma);
                Cv2.Divide(evidenceView, background, flattened, 255.0);
                evidence = FindFinderPatternEvidence(flattened);
            }

            List<FinderPerspectiveCandidate> candidates =
                BuildFinderPerspectiveCandidates(
                    evidence,
                    safeEvidenceRoi.X,
                    safeEvidenceRoi.Y,
                    source.Width,
                    source.Height);
            for (int candidateIndex = 0;
                candidateIndex < candidates.Count;
                candidateIndex++)
            {
                FinderPerspectiveCandidate candidate = candidates[candidateIndex];
                int codeSide =
                    candidate.ModuleCount * FinderPerspectivePixelsPerModule;
                int quietZone =
                    4 * FinderPerspectivePixelsPerModule;
                Point2f[] destination =
                {
                    new Point2f(0, 0),
                    new Point2f(codeSide - 1, 0),
                    new Point2f(codeSide - 1, codeSide - 1),
                    new Point2f(0, codeSide - 1)
                };

                using (Mat transform = Cv2.GetPerspectiveTransform(
                    candidate.Corners,
                    destination))
                using (var straight = new Mat())
                using (var padded = new Mat())
                using (var normalized = new Mat())
                {
                    Cv2.WarpPerspective(
                        source,
                        straight,
                        transform,
                        new OpenCvSharp.Size(codeSide, codeSide),
                        InterpolationFlags.Cubic,
                        BorderTypes.Constant,
                        Scalar.All(255));
                    Cv2.CopyMakeBorder(
                        straight,
                        padded,
                        quietZone,
                        quietZone,
                        quietZone,
                        quietZone,
                        BorderTypes.Constant,
                        Scalar.All(255));
                    Cv2.Normalize(
                        padded,
                        normalized,
                        0,
                        255,
                        NormTypes.MinMax);

                    string rectifiedPreprocessing = "gray";
                    bool decoded = TryDecode(
                        normalized,
                        1.0,
                        out DecodeHit decodedHit);
                    if (!decoded)
                    {
                        // 大截图中的白带过曝在透视拉正后仍可能形成缓慢亮度梯度。
                        // 仅在原灰度校验失败时，对这个约 350 px 的小图做一次局部光照展平；
                        // 它不会增加无定位框帧的开销，也不会修改原始数据模块。
                        using (var localBackground = new Mat())
                        using (var localFlattened = new Mat())
                        {
                            Cv2.GaussianBlur(
                                normalized,
                                localBackground,
                                new OpenCvSharp.Size(0, 0),
                                FinderPerspectivePixelsPerModule * 1.8);
                            Cv2.Divide(
                                normalized,
                                localBackground,
                                localFlattened,
                                255.0);
                            decoded = TryDecode(
                                localFlattened,
                                1.0,
                                out decodedHit);
                            if (decoded)
                                rectifiedPreprocessing = "illumination-flattened";
                        }
                    }

                    if (!decoded)
                        continue;

                    Point2f[] corners = candidate.Corners;
                    double centerX =
                        (corners[0].X + corners[1].X +
                         corners[2].X + corners[3].X) * 0.25;
                    double centerY =
                        (corners[0].Y + corners[1].Y +
                         corners[2].Y + corners[3].Y) * 0.25;
                    double pixelWidth =
                        (PointDistance(corners[0], corners[1]) +
                         PointDistance(corners[3], corners[2])) * 0.5;
                    double pixelHeight =
                        (PointDistance(corners[0], corners[3]) +
                         PointDistance(corners[1], corners[2])) * 0.5;

                    result = new QrDetectionResult
                    {
                        Found = true,
                        CenterX = Math.Max(
                            0,
                            (int)Math.Round(centerX + sourceOffsetX)),
                        CenterY = Math.Max(0, (int)Math.Round(centerY)),
                        PixelWidth = pixelWidth,
                        PixelHeight = pixelHeight,
                        DecodedText = decodedHit.Text
                    };
                    strategy =
                        $"WeChatQRCode, finder-perspective, finderCount=3, " +
                        $"moduleCount={candidate.ModuleCount}, " +
                        $"rightAngleCosine={candidate.RightAngleCosine:F3}, " +
                        $"rectified={rectifiedPreprocessing}";
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 在有限数量的定位框中枚举三元组，筛选尺寸一致、两条边接近垂直且长度相近的组合，
        /// 再由定位框中心距估算 21、25、29… 等合法二维码模块数。
        /// </summary>
        private static List<FinderPerspectiveCandidate>
            BuildFinderPerspectiveCandidates(
                List<FinderPatternEvidence> evidence,
                int evidenceOffsetX,
                int evidenceOffsetY,
                int sourceWidth,
                int sourceHeight)
        {
            var candidates = new List<FinderPerspectiveCandidate>();
            if (evidence == null || evidence.Count < 3)
                return candidates;

            int count = Math.Min(
                evidence.Count,
                FinderPerspectiveMaxEvidence);
            for (int firstIndex = 0; firstIndex < count - 2; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1;
                    secondIndex < count - 1;
                    secondIndex++)
                {
                    for (int thirdIndex = secondIndex + 1;
                        thirdIndex < count;
                        thirdIndex++)
                    {
                        FinderPatternEvidence[] triple =
                        {
                            evidence[firstIndex],
                            evidence[secondIndex],
                            evidence[thirdIndex]
                        };
                        double minimumSide = Math.Min(
                            triple[0].EquivalentSide,
                            Math.Min(
                                triple[1].EquivalentSide,
                                triple[2].EquivalentSide));
                        double maximumSide = Math.Max(
                            triple[0].EquivalentSide,
                            Math.Max(
                                triple[1].EquivalentSide,
                                triple[2].EquivalentSide));
                        if (minimumSide <= 0 ||
                            maximumSide / minimumSide > 1.50)
                            continue;

                        int cornerIndex = -1;
                        double bestCosine = double.MaxValue;
                        double firstLeg = 0;
                        double secondLeg = 0;
                        for (int index = 0; index < 3; index++)
                        {
                            Point2f cornerCenter = FinderCenter(
                                triple[index],
                                evidenceOffsetX,
                                evidenceOffsetY);
                            Point2f firstCenter = FinderCenter(
                                triple[(index + 1) % 3],
                                evidenceOffsetX,
                                evidenceOffsetY);
                            Point2f secondCenter = FinderCenter(
                                triple[(index + 2) % 3],
                                evidenceOffsetX,
                                evidenceOffsetY);
                            Point2f firstVector = firstCenter - cornerCenter;
                            Point2f secondVector = secondCenter - cornerCenter;
                            double firstLength = Math.Sqrt(
                                firstVector.X * firstVector.X +
                                firstVector.Y * firstVector.Y);
                            double secondLength = Math.Sqrt(
                                secondVector.X * secondVector.X +
                                secondVector.Y * secondVector.Y);
                            if (firstLength <= 1 || secondLength <= 1)
                                continue;

                            double cosine = Math.Abs(
                                (firstVector.X * secondVector.X +
                                 firstVector.Y * secondVector.Y) /
                                (firstLength * secondLength));
                            if (cosine >= bestCosine)
                                continue;

                            bestCosine = cosine;
                            cornerIndex = index;
                            firstLeg = firstLength;
                            secondLeg = secondLength;
                        }

                        if (cornerIndex < 0 ||
                            bestCosine > 0.25 ||
                            Math.Max(firstLeg, secondLeg) /
                            Math.Min(firstLeg, secondLeg) > 1.35)
                            continue;

                        FinderPatternEvidence corner = triple[cornerIndex];
                        FinderPatternEvidence firstNeighbor =
                            triple[(cornerIndex + 1) % 3];
                        FinderPatternEvidence secondNeighbor =
                            triple[(cornerIndex + 2) % 3];
                        FinderPatternEvidence horizontal =
                            Math.Abs(firstNeighbor.CenterX - corner.CenterX) >=
                            Math.Abs(secondNeighbor.CenterX - corner.CenterX)
                                ? firstNeighbor
                                : secondNeighbor;
                        FinderPatternEvidence vertical =
                            ReferenceEquals(horizontal, firstNeighbor)
                                ? secondNeighbor
                                : firstNeighbor;

                        double[] sides =
                        {
                            triple[0].EquivalentSide,
                            triple[1].EquivalentSide,
                            triple[2].EquivalentSide
                        };
                        Array.Sort(sides);
                        double medianFinderSide = sides[1];
                        double moduleSize = medianFinderSide / 7.0;
                        double dimensionEstimate =
                            (firstLeg + secondLeg) * 0.5 /
                            moduleSize + 7.0;
                        int moduleCount = 21 + 4 * Math.Max(
                            0,
                            (int)Math.Round(
                                (dimensionEstimate - 21.0) / 4.0));
                        if (moduleCount < 21 ||
                            moduleCount > 177 ||
                            Math.Abs(moduleCount - dimensionEstimate) > 2.25)
                            continue;

                        int finderCenterSpan = moduleCount - 7;
                        Point2f cornerCenterWithOffset = FinderCenter(
                            corner,
                            evidenceOffsetX,
                            evidenceOffsetY);
                        Point2f horizontalCenter = FinderCenter(
                            horizontal,
                            evidenceOffsetX,
                            evidenceOffsetY);
                        Point2f verticalCenter = FinderCenter(
                            vertical,
                            evidenceOffsetX,
                            evidenceOffsetY);
                        Point2f xAxis = new Point2f(
                            (cornerCenterWithOffset.X - horizontalCenter.X) /
                            finderCenterSpan,
                            (cornerCenterWithOffset.Y - horizontalCenter.Y) /
                            finderCenterSpan);
                        Point2f yAxis = new Point2f(
                            (cornerCenterWithOffset.X - verticalCenter.X) /
                            finderCenterSpan,
                            (cornerCenterWithOffset.Y - verticalCenter.Y) /
                            finderCenterSpan);
                        double far = moduleCount - 3.5;
                        const double near = 3.5;
                        Point2f[] corners = OrderQuadrilateralCorners(
                            new[]
                            {
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, -far),
                                    ScaleVector(yAxis, -far)),
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, near),
                                    ScaleVector(yAxis, -far)),
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, near),
                                    ScaleVector(yAxis, near)),
                                AddVectors(
                                    cornerCenterWithOffset,
                                    ScaleVector(xAxis, -far),
                                    ScaleVector(yAxis, near))
                            });

                        double allowedOutside =
                            Math.Max(sourceWidth, sourceHeight) * 0.15;
                        bool outside = false;
                        for (int cornerNumber = 0;
                            cornerNumber < corners.Length;
                            cornerNumber++)
                        {
                            if (corners[cornerNumber].X < -allowedOutside ||
                                corners[cornerNumber].Y < -allowedOutside ||
                                corners[cornerNumber].X >
                                    sourceWidth + allowedOutside ||
                                corners[cornerNumber].Y >
                                    sourceHeight + allowedOutside)
                            {
                                outside = true;
                                break;
                            }
                        }
                        if (outside)
                            continue;

                        double score =
                            bestCosine +
                            Math.Abs(Math.Log(firstLeg / secondLeg)) +
                            Math.Abs(Math.Log(maximumSide / minimumSide)) +
                            Math.Abs(moduleCount - dimensionEstimate) * 0.05;
                        candidates.Add(new FinderPerspectiveCandidate
                        {
                            Corners = corners,
                            ModuleCount = moduleCount,
                            RightAngleCosine = bestCosine,
                            Score = score
                        });
                    }
                }
            }

            candidates.Sort((leftCandidate, rightCandidate) =>
                leftCandidate.Score.CompareTo(rightCandidate.Score));
            if (candidates.Count > FinderPerspectiveMaxCandidates)
                candidates.RemoveRange(
                    FinderPerspectiveMaxCandidates,
                    candidates.Count - FinderPerspectiveMaxCandidates);
            return candidates;
        }

        private static Point2f FinderCenter(
            FinderPatternEvidence evidence,
            int offsetX,
            int offsetY)
        {
            return new Point2f(
                (float)(evidence.CenterX + offsetX),
                (float)(evidence.CenterY + offsetY));
        }

        private static Point2f ScaleVector(Point2f vector, double scale)
        {
            return new Point2f(
                (float)(vector.X * scale),
                (float)(vector.Y * scale));
        }

        private static Point2f AddVectors(
            Point2f origin,
            Point2f first,
            Point2f second)
        {
            return new Point2f(
                origin.X + first.X + second.X,
                origin.Y + first.Y + second.Y);
        }

        private static double PointDistance(Point2f first, Point2f second)
        {
            double deltaX = first.X - second.X;
            double deltaY = first.Y - second.Y;
            return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        private static Point2f[] OrderQuadrilateralCorners(Point2f[] corners)
        {
            Point2f topLeft = corners[0];
            Point2f topRight = corners[0];
            Point2f bottomRight = corners[0];
            Point2f bottomLeft = corners[0];
            double minimumSum = double.MaxValue;
            double maximumSum = double.MinValue;
            double minimumDifference = double.MaxValue;
            double maximumDifference = double.MinValue;
            for (int index = 0; index < corners.Length; index++)
            {
                Point2f point = corners[index];
                double sum = point.X + point.Y;
                double difference = point.X - point.Y;
                if (sum < minimumSum)
                {
                    minimumSum = sum;
                    topLeft = point;
                }
                if (sum > maximumSum)
                {
                    maximumSum = sum;
                    bottomRight = point;
                }
                if (difference > maximumDifference)
                {
                    maximumDifference = difference;
                    topRight = point;
                }
                if (difference < minimumDifference)
                {
                    minimumDifference = difference;
                    bottomLeft = point;
                }
            }
            return new[] { topLeft, topRight, bottomRight, bottomLeft };
        }

        private static List<FinderPatternEvidence> FindFinderPatternEvidence(Mat source)
        {
            using (var binary = new Mat())
            {
                Cv2.Threshold(
                    source,
                    binary,
                    0,
                    255,
                    ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Cv2.FindContours(
                    binary,
                    out Point[][] contours,
                    out HierarchyIndex[] hierarchy,
                    RetrievalModes.Tree,
                    ContourApproximationModes.ApproxSimple);

                var rawEvidence = new List<FinderPatternEvidence>();
                if (contours == null || hierarchy == null || contours.Length != hierarchy.Length)
                    return rawEvidence;

                int minSide = Math.Max(12, Math.Min(source.Width, source.Height) / 120);
                int maxSide = Math.Max(minSide + 1, (int)Math.Round(
                    Math.Min(source.Width, source.Height) * 0.45));
                for (int i = 0; i < contours.Length; i++)
                {
                    Rect bounds = Cv2.BoundingRect(contours[i]);
                    int shortSide = Math.Min(bounds.Width, bounds.Height);
                    int longSide = Math.Max(bounds.Width, bounds.Height);
                    double aspect = bounds.Width / (double)Math.Max(1, bounds.Height);
                    if (shortSide < minSide ||
                        longSide > maxSide ||
                        aspect < 0.65 ||
                        aspect > 1.50)
                        continue;

                    int nestedDepth = 0;
                    int child = hierarchy[i].Child;
                    while (child >= 0 && child < hierarchy.Length && nestedDepth < 4)
                    {
                        nestedDepth++;
                        child = hierarchy[child].Child;
                    }

                    if (nestedDepth < 2)
                        continue;

                    rawEvidence.Add(new FinderPatternEvidence
                    {
                        Bounds = bounds,
                        CenterX = bounds.X + bounds.Width * 0.5,
                        CenterY = bounds.Y + bounds.Height * 0.5,
                        EquivalentSide = Math.Sqrt((double)bounds.Width * bounds.Height)
                    });
                }

                // 同一定位框通常会贡献多层嵌套轮廓。先保留面积更大的外层，再剔除中心接近或高度重叠的内层。
                rawEvidence.Sort((left, right) =>
                    (right.Bounds.Width * right.Bounds.Height).CompareTo(
                        left.Bounds.Width * left.Bounds.Height));
                var distinctEvidence = new List<FinderPatternEvidence>();
                for (int i = 0; i < rawEvidence.Count; i++)
                {
                    FinderPatternEvidence candidate = rawEvidence[i];
                    bool duplicate = false;
                    for (int j = 0; j < distinctEvidence.Count; j++)
                    {
                        FinderPatternEvidence accepted = distinctEvidence[j];
                        double dx = candidate.CenterX - accepted.CenterX;
                        double dy = candidate.CenterY - accepted.CenterY;
                        double mergeDistance =
                            Math.Max(candidate.EquivalentSide, accepted.EquivalentSide) * 0.45;
                        if (dx * dx + dy * dy <= mergeDistance * mergeDistance ||
                            CalculateIntersectionOverUnion(candidate.Bounds, accepted.Bounds) >= 0.30)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        distinctEvidence.Add(candidate);
                }

                return distinctEvidence;
            }
        }

        /// <summary>
        /// 从至少两个尺寸一致、间距合理的定位框估算二维码原始边长和Y方向比例，
        /// 再把二维码归一化到有限的解码工作尺寸。缩放系数由当前图像实时计算，而非绑定具体样本。
        /// </summary>
        private static bool TryBuildAdaptiveScaleCandidates(
            List<FinderPatternEvidence> evidence,
            out List<AdaptiveScaleCandidate> candidates,
            out int coherentFinderCount,
            out double estimatedQrSide,
            out double geometricRelativeScaleY)
        {
            candidates = new List<AdaptiveScaleCandidate>();
            coherentFinderCount = 0;
            estimatedQrSide = 0;
            geometricRelativeScaleY = 1.0;
            if (evidence == null || evidence.Count < 2)
                return false;

            int bestFirst = -1;
            int bestSecond = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < evidence.Count - 1; i++)
            {
                for (int j = i + 1; j < evidence.Count; j++)
                {
                    FinderPatternEvidence first = evidence[i];
                    FinderPatternEvidence second = evidence[j];
                    double smallerSide = Math.Min(first.EquivalentSide, second.EquivalentSide);
                    double largerSide = Math.Max(first.EquivalentSide, second.EquivalentSide);
                    if (smallerSide <= 0 || largerSide / smallerSide > 1.65)
                        continue;

                    double dx = second.CenterX - first.CenterX;
                    double dy = second.CenterY - first.CenterY;
                    double separation = Math.Sqrt(dx * dx + dy * dy);
                    double averageSide = (first.EquivalentSide + second.EquivalentSide) * 0.5;
                    if (separation < averageSide * 1.25)
                        continue;

                    // 优先选择尺寸一致且相互分离的定位框；第三个定位框稍后再按同一尺寸族加入。
                    double score =
                        Math.Abs(Math.Log(largerSide / smallerSide)) +
                        averageSide / Math.Max(1.0, separation) * 0.05;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestFirst = i;
                        bestSecond = j;
                    }
                }
            }

            if (bestFirst < 0 || bestSecond < 0)
                return false;

            var coherent = new List<FinderPatternEvidence>
            {
                evidence[bestFirst],
                evidence[bestSecond]
            };
            double pairMedianSide =
                (evidence[bestFirst].EquivalentSide + evidence[bestSecond].EquivalentSide) * 0.5;
            var remaining = new List<FinderPatternEvidence>();
            for (int i = 0; i < evidence.Count; i++)
            {
                if (i != bestFirst && i != bestSecond)
                    remaining.Add(evidence[i]);
            }
            remaining.Sort((left, right) =>
                Math.Abs(Math.Log(left.EquivalentSide / pairMedianSide)).CompareTo(
                    Math.Abs(Math.Log(right.EquivalentSide / pairMedianSide))));
            for (int i = 0; i < remaining.Count && coherent.Count < 3; i++)
            {
                double ratio = remaining[i].EquivalentSide / pairMedianSide;
                if (ratio >= 0.67 && ratio <= 1.50)
                    coherent.Add(remaining[i]);
            }

            double medianWidth = Median(coherent, item => item.Bounds.Width);
            double medianHeight = Median(coherent, item => item.Bounds.Height);
            if (medianWidth <= 0 || medianHeight <= 0)
                return false;

            geometricRelativeScaleY = Clamp(
                medianWidth / medianHeight,
                AdaptiveMinRelativeScaleY,
                AdaptiveMaxRelativeScaleY);
            double minCenterX = double.MaxValue;
            double maxCenterX = double.MinValue;
            double minCenterY = double.MaxValue;
            double maxCenterY = double.MinValue;
            for (int i = 0; i < coherent.Count; i++)
            {
                minCenterX = Math.Min(minCenterX, coherent[i].CenterX);
                maxCenterX = Math.Max(maxCenterX, coherent[i].CenterX);
                minCenterY = Math.Min(minCenterY, coherent[i].CenterY);
                maxCenterY = Math.Max(maxCenterY, coherent[i].CenterY);
            }

            double widthEstimate = maxCenterX - minCenterX + medianWidth;
            double heightEstimate =
                (maxCenterY - minCenterY + medianHeight) * geometricRelativeScaleY;
            estimatedQrSide = Math.Max(widthEstimate, heightEstimate);
            double finderSide = Math.Sqrt(medianWidth * medianHeight);
            if (estimatedQrSide < finderSide * 2.20)
                return false;

            coherentFinderCount = coherent.Count;
            for (int i = 0; i < AdaptiveTargetQrSides.Length; i++)
            {
                AddAdaptiveCandidate(
                    candidates,
                    AdaptiveTargetQrSides[i],
                    estimatedQrSide,
                    geometricRelativeScaleY);
            }

            // 几何外框可能保持方形，但数据模块仍有局部线扫形变；在中心工作尺寸上补充有限比例邻域。
            double centerTarget = AdaptiveTargetQrSides[0];
            AddAdaptiveCandidate(candidates, centerTarget, estimatedQrSide, 1.0);
            AddAdaptiveCandidate(
                candidates,
                centerTarget,
                estimatedQrSide,
                geometricRelativeScaleY * 0.80);
            AddAdaptiveCandidate(
                candidates,
                centerTarget,
                estimatedQrSide,
                geometricRelativeScaleY * 1.25);
            AddAdaptiveCandidate(candidates, AdaptiveTargetQrSides[1], estimatedQrSide, 1.0);
            AddAdaptiveCandidate(candidates, AdaptiveTargetQrSides[2], estimatedQrSide, 1.0);

            return candidates.Count > 0;
        }

        /// <summary>
        /// 为已通过双重几何确认的局部候选生成少量工作尺度。完整二维码优先尝试
        /// 轻微纵向补偿；边缘截断二维码保留更大的纵向补偿范围。三个目标边长用于
        /// 覆盖模块清晰度差异，总候选严格限流以控制无二维码帧耗时。
        /// </summary>
        private static List<AdaptiveScaleCandidate> BuildLocalAdaptiveScaleCandidates(
            double estimatedCodeSide,
            double geometricRelativeScaleY,
            bool hasBoundaryPadding)
        {
            var candidates = new List<AdaptiveScaleCandidate>();
            double[] relativeScaleCandidates = hasBoundaryPadding
                ? new[]
                {
                    geometricRelativeScaleY * 1.30,
                    geometricRelativeScaleY
                }
                : new[]
                {
                    geometricRelativeScaleY * 1.15,
                    geometricRelativeScaleY
                };

            for (int relativeIndex = 0;
                relativeIndex < relativeScaleCandidates.Length;
                relativeIndex++)
            {
                for (int targetIndex = 0;
                    targetIndex < LocalCandidateTargetQrSides.Length;
                    targetIndex++)
                {
                    AddAdaptiveCandidate(
                        candidates,
                        LocalCandidateTargetQrSides[targetIndex],
                        estimatedCodeSide,
                        relativeScaleCandidates[relativeIndex]);
                    if (candidates.Count >= LocalAdaptiveMaxCandidates)
                        return candidates;
                }
            }

            // 传感器边界已经截掉部分数据模块时，小尺寸归一化可能继续丢失纠错信息。
            // 额外保留一个较高横向采样、较强纵向压缩的动态候选；仅对确实发生补白的
            // 局部码区启用，不增加普通二维码和无二维码帧的尝试次数。
            if (hasBoundaryPadding)
            {
                AddAdaptiveCandidate(
                    candidates,
                    BoundaryRecoveryTargetQrSide,
                    estimatedCodeSide,
                    geometricRelativeScaleY * 0.70);
            }

            return candidates;
        }

        private static void AddAdaptiveCandidate(
            List<AdaptiveScaleCandidate> candidates,
            double targetQrSide,
            double estimatedQrSide,
            double relativeScaleY)
        {
            if (candidates.Count >= AdaptiveMaxCandidates || estimatedQrSide <= 0)
                return;

            double scaleX = targetQrSide / estimatedQrSide;
            double safeRelativeScaleY = Clamp(
                relativeScaleY,
                AdaptiveMinRelativeScaleY,
                AdaptiveMaxRelativeScaleY);
            double scaleY = scaleX * safeRelativeScaleY;
            if (scaleX < AdaptiveMinScale || scaleX > AdaptiveMaxScale ||
                scaleY < AdaptiveMinScale || scaleY > AdaptiveMaxScale)
                return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (Math.Abs(candidates[i].ScaleX - scaleX) < 0.01 &&
                    Math.Abs(candidates[i].ScaleY - scaleY) < 0.01)
                    return;
            }

            candidates.Add(new AdaptiveScaleCandidate
            {
                TargetQrSide = targetQrSide,
                ScaleX = scaleX,
                ScaleY = scaleY
            });
        }

        private static double Median(
            List<FinderPatternEvidence> evidence,
            Func<FinderPatternEvidence, double> selector)
        {
            var values = new List<double>(evidence.Count);
            for (int i = 0; i < evidence.Count; i++)
                values.Add(selector(evidence[i]));
            values.Sort();

            int middle = values.Count / 2;
            return values.Count % 2 == 0
                ? (values[middle - 1] + values[middle]) * 0.5
                : values[middle];
        }

        private static double CalculateIntersectionOverUnion(Rect first, Rect second)
        {
            int left = Math.Max(first.X, second.X);
            int top = Math.Max(first.Y, second.Y);
            int right = Math.Min(first.Right, second.Right);
            int bottom = Math.Min(first.Bottom, second.Bottom);
            int intersectionWidth = Math.Max(0, right - left);
            int intersectionHeight = Math.Max(0, bottom - top);
            double intersection = (double)intersectionWidth * intersectionHeight;
            double union =
                (double)first.Width * first.Height +
                (double)second.Width * second.Height -
                intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        /// <summary>
        /// 按指定 X/Y 比例先做一次 Area 重采样，再调用同一个 WeChatQRCode 后端。
        /// 返回前把中心和宽高恢复到 source 坐标系，调用方无需感知中间图像尺寸。
        /// </summary>
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
                    _detector.DetectAndDecode(scaled, out boxes, out decodedTexts);

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
