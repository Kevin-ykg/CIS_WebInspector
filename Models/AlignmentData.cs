namespace CIS_WebInspector.Models
{
    /// <summary>
    /// CIS 配准使用的第二个二维码锚点。
    /// 全局 Y 是权威坐标，ImageAligner 在提取 ROI 时转换为拼接段内坐标。
    /// </summary>
    public sealed class CisQrAnchor
    {
        public long GlobalCenterY { get; set; }
        public long SegmentStartGlobalY { get; set; }
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
        public double InitialSearchMarginMm { get; set; }
        public double ExpandedSearchMarginMm { get; set; }
        public double MinCircularityTiff { get; set; }
        public double MinCircularityCis { get; set; }

        public static MarkAlignmentOptions FromConfig(AppConfig config)
        {
            return new MarkAlignmentOptions
            {
                LayoutDpi = config.LayoutDpi,
                TiffHeightMm = config.MarkTiffHeightMm,
                TiffTopCenterYmm = config.MarkTiffTopCenterYmm,
                TiffBottomOffsetMm = config.MarkTiffBottomOffsetMm,
                MarkDiameterMm = config.MarkDiameterMm,
                CisRowSpacingMm = config.MarkCisRowSpacingMm,
                QrPhysicalHeightMm = config.MarkQrPhysicalHeightMm,
                InitialSearchMarginMm = config.MarkInitialSearchMarginMm,
                ExpandedSearchMarginMm = config.MarkExpandedSearchMarginMm,
                MinCircularityTiff = config.MinCircularityTiff,
                MinCircularityCis = config.MinCircularityCis
            };
        }
    }
}
