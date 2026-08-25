using System.Collections.Generic;
using CIS_WebInspector.Models;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 根据物理位置约束提取圆形 Mark，并计算 CIS 实拍图到 TIFF 排版图的变换。
    /// 上下两排 20 mm Mark 始终负责全局 H0；启用开关后，左右 4 mm Mark 只描述
    /// H0 无法解释的边缘残差，不参与 H0 拟合，也不会改变中心列的全局基准。
    /// </summary>
    public static partial class ImageAligner
    {
        private static readonly ImageEncodingParam[] PreviewJpegParameters =
        {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 90)
        };

        private const int MinimumPointsPerRow = 2;
        private const int MinimumGlobalCorrespondenceCount = 6;
        private const double MaximumRowScaleDifference = 0.15;
        private const double MaximumHorizontalScaleDeviationFromQr = 0.45;
        private const double RowLineInlierDiameterRatio = 0.40;
        private const double RowMatchGateDiameterRatio = 0.65;
        private const double MinimumStrongRowCoverage = 0.50;
        private const double MinimumAverageRowCoverage = 0.28;
        private const double GlobalRansacThresholdMm = 0.75;
        private const double MaximumMedianReprojectionErrorMm = 0.50;
        private const double MinimumGlobalInlierRatio = 0.60;
        private const double MaximumAdjacentJacobianScaleRatio = 2.0;

        /// <summary>统一描述 TIFF/CIS 中的圆形候选；Score 越小表示越接近期望位置和尺寸。</summary>
        public class MarkerPoint
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Area { get; set; }
            public double Circularity { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Score { get; set; }
        }

        /// <summary>一排大 Mark 在两个图像坐标系中的预测几何信息。</summary>
        private sealed class MarkerRegionSpec
        {
            public string Name { get; set; }
            public double TiffCenterY { get; set; }
            public double CisCenterY { get; set; }
            public double TiffDiameterPixels { get; set; }
            public double CisDiameterPixels { get; set; }
            public double TiffPixelsPerMm { get; set; }
            public double CisPixelsPerMm { get; set; }
        }

        /// <summary>
        /// 单排 Mark 检测结果。Slope 描述 CIS 扫描造成的整排倾斜；其余指标用于判断
        /// 当前候选是否覆盖了足够宽度，而不是仅凭候选数量判断检测是否可靠。
        /// </summary>
        private sealed class RowDetectionResult
        {
            public List<MarkerPoint> Points { get; set; } = new List<MarkerPoint>();
            public int Threshold { get; set; } = 127;
            public Rect SearchRect { get; set; }
            public double Slope { get; set; }
            public double MedianLineResidual { get; set; }
            public double HorizontalCoverage { get; set; }
            public double EndToEndYDrift { get; set; }
        }

        /// <summary>
        /// 一排 Mark 的缺点容忍配对结果。TemplateIndices 保存 TIFF 中的真实编号，
        /// 即使 CIS 中间缺失若干圆，诊断图也不会把剩余圆错误地重新连续编号。
        /// </summary>
        private sealed class RowMatchResult
        {
            public List<MarkerPoint> TiffPoints { get; set; } = new List<MarkerPoint>();
            public List<MarkerPoint> CisPoints { get; set; } = new List<MarkerPoint>();
            public List<int> TemplateIndices { get; set; } = new List<int>();
            public double Scale { get; set; } = double.NaN;
            public double Offset { get; set; } = double.NaN;
            public double MedianResidual { get; set; } = double.NaN;
            public double Coverage { get; set; }
        }

        /// <summary>
        /// 保存一排 TIFF/CIS Mark 的检测结果。检测与配对分离后，可在尺度门控失败时
        /// 只重新检测 CIS 扩展条带，而不重复 TIFF 检测和其他前置计算。
        /// </summary>
        private sealed class GlobalMarkRowDetection
        {
            public MarkerRegionSpec Region { get; set; }
            public RowDetectionResult TiffRow { get; set; }
            public RowDetectionResult CisRow { get; set; }
        }

        /// <summary>单个侧边小 Mark 的检测状态；失败原因用于决定是否降级到 H0。</summary>
        private sealed class SideMarkerDetection
        {
            public MarkerPoint Point { get; set; }
            public Rect SearchRect { get; set; }
            public bool UsedExpandedWindow { get; set; }
            public bool UsedHomographyFallback { get; set; }
            public string Error { get; set; }
            public bool Found => Point != null;
        }

        /// <summary>通过质量门控后的三列残差网格及留一验证指标。</summary>
        private sealed class SideGridData
        {
            public double[] GridX { get; set; }
            public double[] GridY { get; set; }
            public Point2d[,] Residuals { get; set; }
            public List<AlignmentControlPoint> ControlPoints { get; set; }
            public double LeaveOneOutMedianMm { get; set; }
            public double LeaveOneOutMaximumMm { get; set; }
            public string Diagnostic { get; set; }
        }

        /// <summary>
        /// 仅依赖 CIS 拼接图和第二个二维码锚点执行 Bottom 白墨检查。
        /// 该入口不需要 Debug.log、TIFF 或全局变换，适合在完整缺陷流水线之前做独立质量门控。
        /// </summary>
    }
}
