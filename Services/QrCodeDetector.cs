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
    public sealed partial class QrCodeDetector : IDisposable
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
        // 模糊恢复的几何候选来自模板匹配，普通文字也可能偶然组成三个近似直角的方框。
        // 在调用耗时较高的 WeChatQRCode 前，先核对三个定位框和两条时序线的模块结构；
        // 同时限制通过结构门控后的解码次数，避免无二维码帧出现秒级长尾。
        private const double BlurredStructureMinimumContrast = 12.0;
        private const double BlurredStructureMinimumFinderAgreement = 0.68;
        private const double BlurredStructureMinimumTimingAgreement = 0.65;
        private const int BlurredRecoveryMaxDnnAttempts = 4;
        private static readonly int[] BlurredFinderTemplateModuleSizes =
            { 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 16, 18 };
        private static readonly double[] BlurredRecoveryExpansionScales =
            { 1.0, 0.98, 1.035, 0.97 };
        private readonly object _decodeLock = new object();
        private readonly string _modelDirectory;
        private WeChatQRCode _detector;
        private bool _isWarmedUp;
        private bool _disposed;
        private int _roiX;
        private int _roiWidth;
        private bool _invertPolarity;
        private float[] _scaleYCandidates = Array.Empty<float>();
        private int _decodeAttemptCount;

        /// <summary>横向感兴趣区域的起始 X 坐标（自动换算后）。</summary>
        public int RoiX => _roiX;

        /// <summary>横向感兴趣区域的宽度（自动换算后）。</summary>
        public int RoiWidth => _roiWidth;

        /// <summary>最近一次调用发生的参数、模型或本机库异常；未识别到二维码不属于异常。</summary>
        public string LastError { get; private set; }

        /// <summary>最近一次成功识别使用的后端、极性和形变补偿系数。</summary>
        public string LastDecodeStrategy { get; private set; }

        /// <summary>最近一次检测实际调用 WeChatQRCode 的次数，用于定位异常耗时帧。</summary>
        public int LastDecodeAttemptCount => _decodeAttemptCount;

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
            Configure(new AppConfig());
        }

        /// <summary>
        /// 固定下一轮采集使用的 ROI、极性和纵向尺度候选。
        /// 必须在首帧进入检测器前调用；数组会复制，调用方后续修改配置不会影响本轮识别。
        /// </summary>
        public void Configure(AppConfig configSnapshot)
        {
            if (configSnapshot == null)
                throw new ArgumentNullException(nameof(configSnapshot));

            lock (_decodeLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(QrCodeDetector));

                int downscaleFactor = Math.Max(1, configSnapshot.DownscaleFactor);
                _roiX = configSnapshot.BaseRoiX / downscaleFactor;
                _roiWidth = configSnapshot.BaseRoiWidth / downscaleFactor;
                _invertPolarity = configSnapshot.QrInvertPolarity;
                _scaleYCandidates = configSnapshot.QrScaleYCandidates == null
                    ? Array.Empty<float>()
                    : (float[])configSnapshot.QrScaleYCandidates.Clone();

                // 工作尺度可能变化，下一次 Initialize 重新执行轻量预热；模型实例仍复用。
                _isWarmedUp = false;
            }
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

        /// <summary>每次公开调用前清空上次诊断，区分“本次未命中”与历史异常。</summary>
        private void ResetDiagnostics()
        {
            LastError = null;
            LastDecodeStrategy = null;
            _decodeAttemptCount = 0;
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
                    bool inverted = _invertPolarity;
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
    }
}
