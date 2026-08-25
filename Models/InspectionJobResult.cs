namespace CIS_WebInspector.Models
{
    /// <summary>一次拼接段检测作业的工程执行状态，不代表产品是否存在缺陷。</summary>
    public enum InspectionJobStatus
    {
        Unknown,
        Completed,
        CompletedWithProcessingErrors,
        Skipped,
        Failed,
        Cancelled
    }

    /// <summary>作业未完整执行时的机器可读原因，便于 UI、日志和后续 DCS 接口统一处理。</summary>
    public enum InspectionJobIssueCode
    {
        None,
        InvalidConfiguration,
        MissingEndQrCode,
        DebugLogNotConfigured,
        DebugLogNotFound,
        LayoutImageDirectoryNotConfigured,
        LayoutImageDirectoryNotFound,
        LayoutRecordNotFound,
        LayoutImageNotFound,
        LayoutImageLoadFailed,
        AlignmentFailed,
        PartProcessingError,
        UnhandledException
    }

    /// <summary>
    /// 一次在线或离线拼接段检测作业的 UI 无关结果。算法服务只返回数据，界面层决定如何展示。
    /// </summary>
    public sealed class InspectionJobResult
    {
        // 状态与面向用户的汇总信息。产品 Pass/Fail 仍由 PassCount/FailCount 表示。
        public InspectionJobStatus Status { get; internal set; }
        public InspectionJobIssueCode IssueCode { get; internal set; }
        public bool Succeeded => Status == InspectionJobStatus.Completed ||
                                 Status == InspectionJobStatus.CompletedWithProcessingErrors;
        public bool Cancelled => Status == InspectionJobStatus.Cancelled;
        public string Message { get; internal set; }
        /// <summary>供 UI 展示的全局结果 JPEG；不是原始检测图。</summary>
        public byte[] GlobalImageBytes { get; internal set; }
        /// <summary>带 Bottom 条带和 Mark 轮廓的 CIS 预览；不会写回算法原图。</summary>
        public byte[] WhiteInkPreviewBytes { get; internal set; }
        public WhiteInkInspectionResult WhiteInkInspection { get; internal set; } =
            WhiteInkInspectionResult.Disabled();
        public string OutputDirectory { get; internal set; }
        public int TotalParts { get; internal set; }
        public int PassCount { get; internal set; }
        public int FailCount { get; internal set; }
        /// <summary>算法/文件/原生调用异常的零件数，不计入产品不合格数。</summary>
        public int ProcessingErrorCount { get; internal set; }
        public bool HasProcessingErrors => ProcessingErrorCount > 0;
        // 全局对准模式和分阶段耗时，便于日志追溯本批次是否发生降级。
        public AlignmentMode AlignmentMode { get; internal set; }
        public AlignmentQualityStatus AlignmentQualityStatus { get; internal set; }
        public double DetectionMilliseconds { get; internal set; }
        public double MapGenerationMilliseconds { get; internal set; }
        public double RemapMilliseconds { get; internal set; }
    }
}
