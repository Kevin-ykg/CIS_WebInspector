using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 全局对准/白墨业务入口。计算位于 Native/src/alignment_*.cpp，调用方接口保持不变。
    /// GlobalTransform/WhiteInk/Warp 负责跨语言适配，Diagnostics 与白墨预览只操作可视化副本。
    /// 坐标约定：CIS 段内像素 -> TIFF 原分辨率像素；二维码全局 Y 必须减去拼接段起点。
    /// </summary>
    public static partial class ImageAligner
    {
        private static readonly ImageEncodingParam[] PreviewJpegParameters =
        {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 90)
        };

        /// <summary>保留原公开 Mark 数据类型；算法内部候选已改由 C++ RAII 管理。</summary>
        public sealed class MarkerPoint
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Area { get; set; }
            public double Circularity { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Score { get; set; }
        }
    }
}
