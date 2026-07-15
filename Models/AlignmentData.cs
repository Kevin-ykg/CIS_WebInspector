using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenCvSharp;

namespace CIS_WebInspector.Models
{
    public enum AlignmentMode
    {
        GlobalOnly,
        Nonlinear
    }

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

    /// <summary>用于诊断和绘制侧边非线性控制网格的单个控制点。</summary>
    public sealed class AlignmentControlPoint
    {
        public int RowIndex { get; internal set; }
        public AlignmentControlColumn Column { get; internal set; }
        public Point2d ExpectedTiffPoint { get; internal set; }
        public Point2d DetectedTiffPoint { get; internal set; }
        public Point2d CoarseCisPoint { get; internal set; }
        public Point2d DetectedCisPoint { get; internal set; }
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
    /// CIS 到 TIFF 的完整对准结果。对象拥有其中的两个 Mat，调用方必须 Dispose。
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

        public Mat GlobalTransform { get; private set; }
        internal Mat InverseGlobalTransform { get; private set; }
        internal double[] GridX { get; }
        internal double[] GridY { get; }
        internal Point2d[,] ResidualGrid { get; }
        public AlignmentMode Mode { get; }
        public AlignmentQualityStatus QualityStatus { get; }
        public bool IsNonlinear => Mode == AlignmentMode.Nonlinear;
        public IReadOnlyList<AlignmentControlPoint> ControlPoints { get; }
        public IReadOnlyList<AlignmentGlobalMarkPoint> GlobalMarkPoints { get; }
        public int StripeRows { get; }
        public string Diagnostic { get; internal set; }
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
    /// 全局 Y 是权威坐标，ImageAligner 在提取 ROI 时转换为拼接段内坐标。
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

    /// <summary>Mark 点配准所需的物理参数与检测阈值。</summary>
    public sealed class MarkAlignmentOptions
    {
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
