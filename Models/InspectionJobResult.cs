namespace CIS_WebInspector.Models
{
    /// <summary>
    /// 一次离线检测作业的 UI 无关结果。算法服务只返回数据，界面层决定如何展示。
    /// </summary>
    public sealed class InspectionJobResult
    {
        // 状态与面向用户的汇总信息。
        public bool Succeeded { get; internal set; }
        public bool Cancelled { get; internal set; }
        public string Message { get; internal set; }
        /// <summary>供 UI 展示的全局结果 JPEG；不是原始检测图。</summary>
        public byte[] GlobalImageBytes { get; internal set; }
        public string OutputDirectory { get; internal set; }
        public int TotalParts { get; internal set; }
        public int PassCount { get; internal set; }
        public int FailCount { get; internal set; }
        // 全局对准模式和分阶段耗时，便于日志追溯本批次是否发生降级。
        public AlignmentMode AlignmentMode { get; internal set; }
        public AlignmentQualityStatus AlignmentQualityStatus { get; internal set; }
        public double DetectionMilliseconds { get; internal set; }
        public double MapGenerationMilliseconds { get; internal set; }
        public double RemapMilliseconds { get; internal set; }
    }
}
