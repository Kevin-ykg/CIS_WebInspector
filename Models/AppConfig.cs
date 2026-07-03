using System.Text.Json.Serialization;

namespace CIS_WebInspector.Models
{
    /// <summary>
    /// 系统全局配置类。
    /// 所有的数值均以“原始相机图像（例如 31212x10000）”的绝对物理坐标作为基础锚点。
    /// 程序在运行时会根据 DownscaleFactor 自动进行缩小/放大的倍率换算。
    /// </summary>
    public class AppConfig
    {
        // ==========================================
        // 1. 基础运行参数
        // ==========================================
        
        /// <summary>
        /// 全局图像压缩比例。
        /// 4 表示宽、高分别压缩到 1/4（面积为 1/16）。
        /// 修改此值无需修改下方的物理坐标锚点，引擎会自动换算。
        /// </summary>
        public int DownscaleFactor { get; set; } = 4/4;

        /// <summary>
        /// 在线模式下的相机 DMA 环形缓存队列长度。
        /// </summary>
        public int BufferCount { get; set; } = 8;
        
        /// <summary>
        /// 连续多少帧未检测到二维码就触发报警。
        /// 用于监控打印质量或漏印异常。
        /// </summary>
        public int MaxFramesWithoutQr { get; set; } = 10;

        // ==========================================
        // 2. 图像拼接核心参数 (相对于原图坐标)
        // ==========================================

        /// <summary>
        /// 切割点偏移量：QR 中心 Y 坐标往下的固定行数。
        /// </summary>
        public int BaseQrOffsetRows { get; set; } = 1800/4;

        /// <summary>
        /// 跨帧重叠寻找区域的高度。
        /// 必须大于 BaseQrOffsetRows。
        /// </summary>
        public int BaseOverlapRows { get; set; } = 3800/4;


        // ==========================================
        // 3. 二维码检测参数 (相对于原图坐标)
        // ==========================================

        /// <summary>
        /// 横向感兴趣区域的起始 X 坐标。
        /// </summary>
        public int BaseRoiX { get; set; } = 500 / 4;

        /// <summary>
        /// 横向感兴趣区域的宽度。
        /// </summary>
        public int BaseRoiWidth { get; set; } = 6000 / 4;

        /// <summary>
        /// 动态差速拉伸补偿系数。
        /// 用于补偿线扫相机的拉伸形变（例如 Y 轴被压扁到 0.5）。
        /// 这里的系数是相对于“原图”的基准变形率。
        /// </summary>
        public float[] BaseScaleYs { get; set; } = new float[] { 0.45f, 0.50f, 0.55f, 0.60f, 0.95f,1.0f,1.05f};

        // ==========================================
        // 4. 离线缺陷检测参数
        // ==========================================

        /// <summary>Debug.log 文件路径</summary>
        public string DebugLogPath { get; set; } = @"E:\Software\feishudocs\test\log\Debug.log";

        /// <summary>TIFF 原图存放目录</summary>
        public string TiffImageDir { get; set; } = @"E:\Software\feishudocs\test\tiff";

        /// <summary>裁切小图输出目录</summary>
        public string CroppedOutputDir { get; set; } = "裁切结果";

        /// <summary>是否保存裁切出的小图</summary>
        public bool SaveCroppedImages { get; set; } = true;

        /// <summary>排版坐标系物理 DPI（readlog.cpp 中的 scale = 300/25.4）</summary>
        public double LayoutDpi { get; set; } = 300.0;

        /// <summary>排版原点 X 偏移（mm）</summary>
        public double LayoutOriginXmm { get; set; } = 5.0;

        /// <summary>排版原点 Y 偏移（mm）</summary>
        public double LayoutOriginYmm { get; set; } = 65.0;

        /// <summary>标志点检测：TIFF 设计图上下端条带占图像高度的比例</summary>
        public double MarkStripRatioTiff { get; set; } = 0.08;

        /// <summary>标志点检测：CIS 实拍图上端条带占图像高度的比例（mark点靠下时可加大）</summary>
        public double MarkStripRatioCisTop { get; set; } = 0.2;

        /// <summary>标志点检测：CIS 实拍图下端条带占图像高度的比例</summary>
        public double MarkStripRatioCisBot { get; set; } = 0.08;

        /// <summary>TIFF 标志点最低圆度</summary>
        public double MinCircularityTiff { get; set; } = 0.85;

        /// <summary>CIS 实拍标志点最低圆度</summary>
        public double MinCircularityCis { get; set; } = 0.75;
    }
}
