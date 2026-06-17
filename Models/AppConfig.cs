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
        public int DownscaleFactor { get; set; } = 4;

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
        public int BaseQrOffsetRows { get; set; } = 2000;

        /// <summary>
        /// 跨帧重叠寻找区域的高度。
        /// 必须大于 BaseQrOffsetRows。
        /// </summary>
        public int BaseOverlapRows { get; set; } = 3000;


        // ==========================================
        // 3. 二维码检测参数 (相对于原图坐标)
        // ==========================================

        /// <summary>
        /// 横向感兴趣区域的起始 X 坐标。
        /// </summary>
        public int BaseRoiX { get; set; } = 1000;

        /// <summary>
        /// 横向感兴趣区域的宽度。
        /// </summary>
        public int BaseRoiWidth { get; set; } = 5000;

        /// <summary>
        /// 动态差速拉伸补偿系数。
        /// 用于补偿线扫相机的拉伸形变（例如 Y 轴被压扁到 0.5）。
        /// 这里的系数是相对于“原图”的基准变形率。
        /// </summary>
        public float[] BaseScaleYs { get; set; } = new float[] { 0.45f, 0.50f, 0.55f, 0.60f, 0.95f,1.0f,1.05f};
    }
}
