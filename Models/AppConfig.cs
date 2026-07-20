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

        /// <summary>
        /// 二维码检测与拼接的有界帧队列容量。队列只允许一个消费者，确保帧顺序稳定。
        /// 建议保持较小，避免大尺寸帧在内存中堆积。
        /// </summary>
        public int FrameProcessingQueueCapacity { get; set; } = 3;

        /// <summary>
        /// 在线采集向处理队列提交一帧时允许等待的最长时间（ms）。
        /// 超时将明确告警并安全停采，不会静默丢帧。
        /// </summary>
        public int FrameProcessingEnqueueTimeoutMs { get; set; } = 50;

        /// <summary>
        /// 自动保存后台队列容量。队列满时仅跳过诊断性质的单帧保存，不影响检测与拼接。
        /// </summary>
        public int ImageSaveQueueCapacity { get; set; } = 4;

        // ==========================================
        // 2. 图像拼接核心参数 (相对于原图坐标)
        // ==========================================

        /// <summary>
        /// 切割点偏移量：QR 中心 Y 坐标往下的固定行数。
        /// </summary>
        public int BaseQrOffsetRows { get; set; } = 1800;

        /// <summary>
        /// 跨帧重叠寻找区域的高度。
        /// 必须大于 BaseQrOffsetRows。
        /// </summary>
        public int BaseOverlapRows { get; set; } = 3800;


        // ==========================================
        // 3. 二维码检测参数 (相对于原图坐标)
        // ==========================================

        /// <summary>
        /// 横向感兴趣区域的起始 X 坐标。
        /// </summary>
        public int BaseRoiX { get; set; } = 500;

        /// <summary>
        /// 横向感兴趣区域的宽度。
        /// </summary>
        public int BaseRoiWidth { get; set; } = 6000;

        /// <summary>
        /// 是否在送入 WeChatQRCode 前反转灰度极性。
        /// 当前 CIS 图像为黑底白码，因此默认开启，转换为识别器更稳定的白底黑码。
        /// </summary>
        public bool QrInvertPolarity { get; set; } = true;

        /// <summary>
        /// WeChatQRCode 的 Y 轴形变补偿候选值，按尝试优先级排列。
        /// 1.0 为原图；0.67 用于补偿纵向拉伸；1.5 用于补偿纵向压缩。
        /// </summary>
        public float[] QrScaleYCandidates { get; set; } = new float[] { 1.0f, 0.67f, 1.5f };


        // ==========================================
        // 4. 排版数据捞取/全图配准参数
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

        /// <summary>TIFF 排版图物理高度（mm）</summary>
        public double MarkTiffHeightMm { get; set; } = 1000.0;

        /// <summary>TIFF 上排 Mark 圆心距离图像顶端的物理距离（mm）</summary>
        public double MarkTiffTopCenterYmm { get; set; } = 50.0;

        /// <summary>TIFF 下排 Mark 圆心距离图像底端的物理距离（mm）</summary>
        public double MarkTiffBottomOffsetMm { get; set; } = 30.0;

        /// <summary>圆形 Mark 的物理直径（mm）</summary>
        public double MarkDiameterMm { get; set; } = 20.0;

        /// <summary>CIS 上下两排 Mark 圆心的物理间距（mm）</summary>
        public double MarkCisRowSpacingMm { get; set; } = 920.0;

        /// <summary>二维码的物理高度（mm），用于换算 CIS 纵向像素/mm</summary>
        public double MarkQrPhysicalHeightMm { get; set; } = 60.0;

        /// <summary>二维码物理宽度（mm），用于换算 CIS 横向像素/mm。</summary>
        public double MarkQrPhysicalWidthMm { get; set; } = 60.0;

        /// <summary>Mark 首次搜索时，圆边缘以外的物理边距（mm）</summary>
        public double MarkInitialSearchMarginMm { get; set; } = 15.0;

        /// <summary>首次搜索点数不足时使用的扩展物理边距（mm）</summary>
        public double MarkExpandedSearchMarginMm { get; set; } = 40.0;

        /// <summary>TIFF 标志点最低圆度</summary>
        public double MinCircularityTiff { get; set; } = 0.85;

        /// <summary>CIS 实拍标志点最低圆度</summary>
        public double MinCircularityCis { get; set; } = 0.85;

        /// <summary>
        /// 是否使用左右侧边 4 mm Mark 构建非线性残差网格。
        /// 默认关闭；关闭或网格质量不合格时仅使用上下两排 20 mm Mark 的 H0。
        /// </summary>
        public bool EnableSideMarkNonlinearAlignment { get; set; } = false;

        /// <summary>左右侧边小圆的对数。</summary>
        public int SideMarkPairCount { get; set; } = 9;

        /// <summary>侧边小圆物理直径（mm）。</summary>
        public double SideMarkDiameterMm { get; set; } = 4.0;

        /// <summary>材料总宽度（mm）。</summary>
        public double MarkSheetWidthMm { get; set; } = 586.0;

        /// <summary>TIFF 左右侧小圆圆心到最近材料边缘的距离（mm）。</summary>
        public double TiffSideMarkEdgeOffsetMm { get; set; } = 2.5;

        /// <summary>CIS 第二个二维码中心到左侧小圆圆心的横向距离（mm）。</summary>
        public double CisQrToLeftMarkMm { get; set; } = 36.5;

        /// <summary>CIS 左右侧小圆圆心之间的物理距离（mm）。</summary>
        public double CisSideMarkSpanMm { get; set; } = 581.0;

        /// <summary>侧边小圆首次搜索窗口在圆半径外增加的边距（mm）。</summary>
        public double SideMarkInitialSearchMarginMm { get; set; } = 3.0;

        /// <summary>首次未检出时使用的扩展搜索边距（mm）。</summary>
        public double SideMarkExpandedSearchMarginMm { get; set; } = 8.0;

        /// <summary>每侧至少需要的有效内部小圆数量。</summary>
        public int SideMarkMinValidPerColumn { get; set; } = 7;

        /// <summary>非线性 Remap 每次生成和处理的目标图行数。</summary>
        public int NonlinearRemapStripeRows { get; set; } = 256;


        // ==========================================
        // 5. 零件缺陷检测参数 (localpeizhun.cpp & align_diff.py)
        // ==========================================

        /// <summary>是否启用零件级 SIFT 二次局部对齐（全局已对齐时可关闭以大幅提速）</summary>
        public bool EnableSiftLocalAlign { get; set; } = true;

        /// <summary>
        /// 零件裁切、局部配准和缺陷检测的最大并行度。
        /// 0 表示自动（最多 4 个且不超过逻辑处理器数）；正数表示显式上限。
        /// </summary>
        public int DefectMaxParallelism { get; set; } = 8;

        /// <summary>缺陷检测时小图缩放比例，影响SIFT 二次局部对齐，以及缺陷检测的时间和图像大小</summary>
        public double DefectDetectScale { get; set; } = 0.3;

        /// <summary>缺陷检测自适应最小宽度（像素，防缩放过度导致 SIFT 失败）</summary>
        public int DefectMinScaledWidth { get; set; } = 200;

        /// <summary>Alpha 掩膜二值化阈值（align_diff.py L546: threshold(alpha_mask, 60)）</summary>
        public int DefectAlphaBinaryThresh { get; set; } = 60;

        /// <summary>CIS 实拍图二值化阈值偏移量（将与 Mark 点检测到的 optimal_thresh 相加）</summary>
        public int DefectCisThreshOffset { get; set; } = 10;

        /// <summary>内部缺陷面积判定阈值（断墨、漏印），与缩放比例相关</summary>
        public int DefectAreaThreshInner { get; set; } = 100;

        /// <summary>外部缺陷面积判定阈值（飞墨、脏污），与缩放比例相关</summary>
        public int DefectAreaThreshOuter { get; set; } = 144;

        /// <summary>内部缺陷形态学容差，与缩放比例相关（像素数，align_diff.py L565: TOLERANCE_inner=5）</summary>
        public int DefectToleranceInner { get; set; } = 6;

        /// <summary>外部缺陷形态学容差，与缩放比例相关（像素数，align_diff.py L566: TOLERANCE_outer=80）</summary>
        public int DefectToleranceOuter { get; set; } = 12;

        /// <summary>边缘屏蔽轮廓厚度-外包围，与缩放比例相关（align_diff.py L585）</summary>
        public int DefectEdgeExclusionThick { get; set; } = 6;

        /// <summary>边缘屏蔽轮廓厚度-内包围，与缩放比例相关（align_diff.py L591）</summary>
        public int DefectEdgeExclusionSmall { get; set; } = 6;

        /// <summary>
        /// 是否在轮廓屏蔽区内额外检测细线断裂。
        /// 普通差分仍使用原边缘屏蔽；本通道只补回具有连续缺失、局部墨量下降且不呈成对错位残差的内部缺陷。
        /// </summary>
        public bool EnableFineLineBreakDetection { get; set; } = true;

        /// <summary>
        /// 细线断裂的最小连续长度（mm）。使用 TIFF 排版 DPI 换算，避免缩放比例变化时检测语义漂移。
        /// </summary>
        public double FineLineMinBreakLengthMm { get; set; } = 0.5;

        /// <summary>
        /// 进入细线专用通道的模板线宽上限（mm）。默认 2 mm 可覆盖当前图库右侧钩线的实际线宽，
        /// 作为 JSON 高级参数保留，不在常用设置界面暴露。
        /// </summary>
        public double FineLineMaxWidthMm { get; set; } = 2.0;

        /// <summary>是否保存缺陷检测可视化结果图</summary>
        public bool SaveDefectResultImages { get; set; } = true;
    }
}
