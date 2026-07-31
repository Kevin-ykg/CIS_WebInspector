using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenCvSharp;

namespace CIS_WebInspector.Models
{
    /// <summary>最终矫正方式：仅全局 H0，或在 H0 上叠加侧边残差网格。</summary>
    public enum AlignmentMode
    {
        GlobalOnly,
        Nonlinear
    }

    /// <summary>非线性阶段不可用时标记 Degraded，但仍可明确回退到有效的全局对准。</summary>
    public enum AlignmentQualityStatus
    {
        Passed,
        Degraded
    }

    public enum AlignmentControlColumn
    {
        Left,
        Center,
        Right
    }

    /// <summary>
    /// 底排 20 mm Mark 的白墨质量状态。百分比分档表示相对于现场正常
    /// 灰度基准的估算结果，不等同于实验室墨层厚度测量。
    /// </summary>
    public enum WhiteInkInspectionStatus
    {
        Disabled,
        Normal,
        Streaking,
        MildShortage,
        ModerateShortage,
        SevereShortage,
        NoInk,
        UnableToEvaluate
    }

    /// <summary>一个底排 Mark 的灰度统计及可视化几何信息。</summary>
    public sealed class WhiteInkMarkSample
    {
        public int Index { get; internal set; }
        public Point2d Center { get; internal set; }
        public double DisplayRadius { get; internal set; }
        /// <summary>True 表示中心来自底排实测；False 表示由上排同列位置预测。</summary>
        public bool UsedDetectedCenter { get; internal set; }
        public double MarkMean { get; internal set; }
        public double MarkVariance { get; internal set; }
        public double BackgroundMean { get; internal set; }
        public double Contrast { get; internal set; }
    }

    /// <summary>
    /// 白墨出墨检查结果。告警状态与零件缺陷 Pass/Fail 相互独立，
    /// 算法层只返回数据，由 UI 线程决定是否弹窗。
    /// </summary>
    public sealed class WhiteInkInspectionResult
    {
        public WhiteInkInspectionStatus Status { get; internal set; }
        public bool IsEnabled => Status != WhiteInkInspectionStatus.Disabled;
        public bool CanEvaluate =>
            Status != WhiteInkInspectionStatus.Disabled &&
            Status != WhiteInkInspectionStatus.UnableToEvaluate;
        public bool RequiresWarning =>
            IsEnabled && Status != WhiteInkInspectionStatus.Normal;
        public double InkLevelPercent { get; internal set; }
        public double EstimatedMissingPercent => Math.Max(0, 100.0 - InkLevelPercent);
        public double MarkMean { get; internal set; }
        public double MarkVariance { get; internal set; }
        public double MarkStandardDeviation => Math.Sqrt(Math.Max(0, MarkVariance));
        public double BackgroundMean { get; internal set; }
        public double Contrast { get; internal set; }
        public bool HasStreaking { get; internal set; }
        public Rect SearchRegion { get; internal set; }
        public IReadOnlyList<WhiteInkMarkSample> Samples { get; internal set; } =
            new ReadOnlyCollection<WhiteInkMarkSample>(new List<WhiteInkMarkSample>());
        public string Diagnostic { get; internal set; }

        public string StatusDisplayName
        {
            get
            {
                switch (Status)
                {
                    case WhiteInkInspectionStatus.Normal:
                        return "正常";
                    case WhiteInkInspectionStatus.Streaking:
                        return "白墨拉丝";
                    case WhiteInkInspectionStatus.MildShortage:
                        return HasStreaking ? "缺墨并伴拉丝" : "轻度缺墨";
                    case WhiteInkInspectionStatus.ModerateShortage:
                        return HasStreaking ? "中度缺墨并伴拉丝" : "中度缺墨";
                    case WhiteInkInspectionStatus.SevereShortage:
                        return HasStreaking ? "严重缺墨并伴拉丝" : "严重缺墨";
                    case WhiteInkInspectionStatus.NoInk:
                        return "基本无白墨";
                    case WhiteInkInspectionStatus.UnableToEvaluate:
                        return "无法判定";
                    default:
                        return "未启用";
                }
            }
        }

        internal static WhiteInkInspectionResult Disabled()
        {
            return new WhiteInkInspectionResult
            {
                Status = WhiteInkInspectionStatus.Disabled,
                Diagnostic = "白墨出墨检测已关闭。"
            };
        }
    }

    /// <summary>用于诊断和绘制侧边非线性控制网格的单个控制点。</summary>
    public sealed class AlignmentControlPoint
    {
        public int RowIndex { get; internal set; }
        public AlignmentControlColumn Column { get; internal set; }
        public Point2d ExpectedTiffPoint { get; internal set; }
        public Point2d DetectedTiffPoint { get; internal set; }
        public Point2d CoarseCisPoint { get; internal set; }
        public Point2d DetectedCisPoint { get; internal set; }
        /// <summary>检测 CIS 点减去 H0 逆映射预测点，单位为 CIS 像素。</summary>
        public Point2d Residual { get; internal set; }
        public bool IsDetected { get; internal set; }
        public bool IsInterpolated { get; internal set; }
        public bool IsVirtual { get; internal set; }
    }

    /// <summary>参与全局 H0 拟合的上下两排大圆对应点。</summary>
    public sealed class AlignmentGlobalMarkPoint
    {
        public string RowName { get; internal set; }
        public int Index { get; internal set; }
        public Point2d TiffPoint { get; internal set; }
        public Point2d CisPoint { get; internal set; }
    }

    /// <summary>
    /// CIS 到 TIFF 的完整对准结果。GlobalTransform 表示 CIS→TIFF；逆矩阵供 Remap
    /// 从目标 TIFF 像素反查源 CIS 像素。对象拥有这两个 Mat，调用方必须 Dispose。
    /// </summary>
    public sealed class AlignmentResult : IDisposable
    {
        private bool _disposed;

        internal AlignmentResult(
            Mat globalTransform,
            Mat inverseGlobalTransform,
            AlignmentMode mode,
            AlignmentQualityStatus qualityStatus,
            double[] gridX,
            double[] gridY,
            Point2d[,] residualGrid,
            IList<AlignmentControlPoint> controlPoints,
            IList<AlignmentGlobalMarkPoint> globalMarkPoints,
            int stripeRows)
        {
            GlobalTransform = globalTransform ?? throw new ArgumentNullException(nameof(globalTransform));
            InverseGlobalTransform = inverseGlobalTransform ?? throw new ArgumentNullException(nameof(inverseGlobalTransform));
            Mode = mode;
            QualityStatus = qualityStatus;
            GridX = gridX;
            GridY = gridY;
            ResidualGrid = residualGrid;
            ControlPoints = new ReadOnlyCollection<AlignmentControlPoint>(
                new List<AlignmentControlPoint>(controlPoints ?? Array.Empty<AlignmentControlPoint>()));
            GlobalMarkPoints = new ReadOnlyCollection<AlignmentGlobalMarkPoint>(
                new List<AlignmentGlobalMarkPoint>(globalMarkPoints ?? Array.Empty<AlignmentGlobalMarkPoint>()));
            StripeRows = Math.Max(1, stripeRows);
        }

        /// <summary>CIS 源坐标到 TIFF 目标坐标的 3×3 H0。</summary>
        public Mat GlobalTransform { get; private set; }
        /// <summary>TIFF 目标坐标到 CIS 源坐标的 H0 逆矩阵，仅供内部逆向采样。</summary>
        internal Mat InverseGlobalTransform { get; private set; }
        // 网格坐标位于 TIFF 目标空间，ResidualGrid 的向量则位于 CIS 源空间。
        internal double[] GridX { get; }
        internal double[] GridY { get; }
        internal Point2d[,] ResidualGrid { get; }
        public AlignmentMode Mode { get; }
        public AlignmentQualityStatus QualityStatus { get; }
        public bool IsNonlinear => Mode == AlignmentMode.Nonlinear;
        public IReadOnlyList<AlignmentControlPoint> ControlPoints { get; }
        public IReadOnlyList<AlignmentGlobalMarkPoint> GlobalMarkPoints { get; }
        /// <summary>底排大圆的白墨质量检查；关闭开关时状态为 Disabled。</summary>
        public WhiteInkInspectionResult WhiteInkInspection { get; internal set; } =
            WhiteInkInspectionResult.Disabled();
        public int StripeRows { get; }
        /// <summary>包含 Mark 数量、ROI、残差和明确降级原因的完整诊断文本。</summary>
        public string Diagnostic { get; internal set; }
        // 以下指标用于离线性能/质量分析，不参与缺陷 Pass/Fail。
        public double DetectionMilliseconds { get; internal set; }
        public double MapGenerationMilliseconds { get; internal set; }
        public double RemapMilliseconds { get; internal set; }
        public double LeaveOneOutMedianMm { get; internal set; }
        public double LeaveOneOutMaximumMm { get; internal set; }
        public long PeakWorkingSetBytes { get; internal set; }
        public long PeakTemporaryBufferBytes { get; internal set; }

        public void Dispose()
        {
            if (_disposed)
                return;

            InverseGlobalTransform?.Dispose();
            InverseGlobalTransform = null;
            GlobalTransform?.Dispose();
            GlobalTransform = null;
            _disposed = true;
        }
    }

    /// <summary>
    /// CIS 配准使用的第二个二维码锚点。
    /// 全局 Y 是权威坐标，ImageAligner 通过 GlobalCenterY-SegmentStartGlobalY
    /// 转换为当前拼接段内坐标；二维码物理宽高用于分别标定 CIS 的 X/Y 像素比例。
    /// </summary>
    public sealed class CisQrAnchor
    {
        public double CenterX { get; set; }
        public long GlobalCenterY { get; set; }
        public long SegmentStartGlobalY { get; set; }
        public double PixelWidth { get; set; }
        public double PixelHeight { get; set; }

        public double CenterYInSegment => GlobalCenterY - SegmentStartGlobalY;
    }

    /// <summary>
    /// Mark 点配准所需的物理参数与检测阈值。这里保留物理毫米语义，
    /// 进入 ImageAligner 后才分别按 TIFF DPI 与二维码尺寸换算为像素。
    /// </summary>
    public sealed class MarkAlignmentOptions
    {
        // 上下两排 20 mm Mark 与 H0 的物理几何。
        public double LayoutDpi { get; set; }
        public double TiffHeightMm { get; set; }
        public double TiffTopCenterYmm { get; set; }
        public double TiffBottomOffsetMm { get; set; }
        public double MarkDiameterMm { get; set; }
        public double CisRowSpacingMm { get; set; }
        public double QrPhysicalHeightMm { get; set; }
        public double QrPhysicalWidthMm { get; set; }
        public double InitialSearchMarginMm { get; set; }
        public double ExpandedSearchMarginMm { get; set; }
        public double MinCircularityTiff { get; set; }
        public double MinCircularityCis { get; set; }

        // 白墨检查复用底排 20 mm Mark，不额外搜索整幅图像。
        public bool EnableWhiteInkInspection { get; set; }
        public double WhiteInkNormalGray { get; set; }
        public double WhiteInkStreakStdDevThreshold { get; set; }

        // 左右 4 mm Mark 非线性增强；开关关闭时这些参数不会参与对准。
        public bool EnableSideMarkNonlinearAlignment { get; set; }
        public int SideMarkPairCount { get; set; }
        public double SideMarkDiameterMm { get; set; }
        public double SheetWidthMm { get; set; }
        public double TiffSideMarkEdgeOffsetMm { get; set; }
        public double CisQrToLeftMarkMm { get; set; }
        public double CisSideMarkSpanMm { get; set; }
        public double SideMarkInitialSearchMarginMm { get; set; }
        public double SideMarkExpandedSearchMarginMm { get; set; }
        public int SideMarkMinValidPerColumn { get; set; }
        public int NonlinearRemapStripeRows { get; set; }

        public static MarkAlignmentOptions FromConfig(AppConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            // 生成作业级参数快照，避免 ImageAligner 在计算中反复读取可变的全局配置单例。
            return new MarkAlignmentOptions
            {
                LayoutDpi = config.LayoutDpi,
                TiffHeightMm = config.MarkTiffHeightMm,
                TiffTopCenterYmm = config.MarkTiffTopCenterYmm,
                TiffBottomOffsetMm = config.MarkTiffBottomOffsetMm,
                MarkDiameterMm = config.MarkDiameterMm,
                CisRowSpacingMm = config.MarkCisRowSpacingMm,
                QrPhysicalHeightMm = config.MarkQrPhysicalHeightMm,
                QrPhysicalWidthMm = config.MarkQrPhysicalWidthMm,
                InitialSearchMarginMm = config.MarkInitialSearchMarginMm,
                ExpandedSearchMarginMm = config.MarkExpandedSearchMarginMm,
                MinCircularityTiff = config.MinCircularityTiff,
                MinCircularityCis = config.MinCircularityCis,
                EnableWhiteInkInspection = config.EnableWhiteInkInspection,
                WhiteInkNormalGray = config.WhiteInkNormalGray,
                WhiteInkStreakStdDevThreshold = config.WhiteInkStreakStdDevThreshold,
                EnableSideMarkNonlinearAlignment = config.EnableSideMarkNonlinearAlignment,
                SideMarkPairCount = config.SideMarkPairCount,
                SideMarkDiameterMm = config.SideMarkDiameterMm,
                SheetWidthMm = config.MarkSheetWidthMm,
                TiffSideMarkEdgeOffsetMm = config.TiffSideMarkEdgeOffsetMm,
                CisQrToLeftMarkMm = config.CisQrToLeftMarkMm,
                CisSideMarkSpanMm = config.CisSideMarkSpanMm,
                SideMarkInitialSearchMarginMm = config.SideMarkInitialSearchMarginMm,
                SideMarkExpandedSearchMarginMm = config.SideMarkExpandedSearchMarginMm,
                SideMarkMinValidPerColumn = config.SideMarkMinValidPerColumn,
                NonlinearRemapStripeRows = config.NonlinearRemapStripeRows
            };
        }
    }
}
