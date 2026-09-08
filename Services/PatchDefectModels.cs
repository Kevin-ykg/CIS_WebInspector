using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 零件处理链路的工程状态，与产品本身的 Pass/Fail 缺陷结论相互独立。
    /// </summary>
    public enum PatchProcessingStatus
    {
        Completed,
        ProcessingError
    }

    /// <summary>
    /// 一个最终缺陷的几何统计。宽、高来自紧致轴对齐外接矩形；面积来自缺陷
    /// 连通域的真实前景像素数，而不是宽×高。所有数值统一换算到 TIFF 目标空间。
    /// 该对象仅用于结果追溯和日志展示，不参与缺陷阈值判定。
    /// </summary>
    public sealed class DefectGeometryMeasurement
    {
        /// <summary>外接矩形水平方向宽度，单位 mm。</summary>
        public double WidthMm { get; set; }
        /// <summary>外接矩形垂直方向高度，单位 mm。</summary>
        public double HeightMm { get; set; }
        /// <summary>缺陷连通域的真实像素面积换算值，单位 mm²。</summary>
        public double AreaMm2 { get; set; }
    }

    /// <summary>
    /// 单个零件的缺陷检测结果。矩形坐标均相对零件原始分辨率 ROI；
    /// GlobalRoi 则位于翻转后的 TIFF/CIS 全局目标空间，供批次结果图定位。
    /// 内部缺陷、外部缺陷和细线断裂保持独立分类，计数与矩形集合不得相互合并。
    /// </summary>
    public class PatchDefectResult
    {
        public string PartId { get; set; }
        public PatchProcessingStatus ProcessingStatus { get; set; } = PatchProcessingStatus.Completed;
        /// <summary>仅在 ProcessingStatus=ProcessingError 时填写，不参与产品缺陷判定。</summary>
        public string ProcessingError { get; set; }
        public bool HasProcessingError => ProcessingStatus == PatchProcessingStatus.ProcessingError;
        /// <summary>普通内部缺陷的最大连通域物理面积，单位 mm²。</summary>
        public double MaxAreaInnerMm2 { get; set; }
        /// <summary>普通外部缺陷的最大连通域物理面积，单位 mm²。</summary>
        public double MaxAreaOuterMm2 { get; set; }
        public int InnerDefectCount { get; set; }
        public int OuterDefectCount { get; set; }
        public int FineLineBreakCount { get; set; }
        /// <summary>已接受细线断裂中最长断口的骨架路径长度，单位 mm。</summary>
        public double MaxFineLineBreakLengthMm { get; set; }
        /// <summary>
        /// 已接受细线断裂所在模板笔画的最大宽度，单位 mm。
        /// 与 FineLineMaxWidthMm 使用同一距离场线宽定义。
        /// </summary>
        public double MaxFineLineBreakWidthMm { get; set; }
        public bool IsPass { get; set; }
        public Rect GlobalRoi { get; set; }
        public List<Rect> InnerRects { get; set; } = new List<Rect>();
        public List<Rect> OuterRects { get; set; } = new List<Rect>();
        public List<Rect> FineLineBreakRects { get; set; } = new List<Rect>();
        /// <summary>已通过内部缺陷面积阈值的最终缺陷尺寸，顺序与 InnerRects 一致。</summary>
        public List<DefectGeometryMeasurement> InnerDefectMeasurements { get; set; } =
            new List<DefectGeometryMeasurement>();
        /// <summary>已通过外部缺陷面积阈值的最终缺陷尺寸，顺序与 OuterRects 一致。</summary>
        public List<DefectGeometryMeasurement> OuterDefectMeasurements { get; set; } =
            new List<DefectGeometryMeasurement>();
        /// <summary>
        /// 已通过最小断裂长度、最大细线宽度及连续性门控的最终细线断裂尺寸。
        /// 这里使用断口本身的紧致外接框，不使用可视化时向外扩展的标注框。
        /// </summary>
        public List<DefectGeometryMeasurement> FineLineBreakMeasurements { get; set; } =
            new List<DefectGeometryMeasurement>();
    }

    /// <summary>
    /// 批次内原生模板缓存。快速签名分桶 + 像素精确比较全部由 C++ 实现。
    /// worker 通过 shared_ptr 保持原生缓存有效；正常流程先释放 worker，再释放批次缓存。
    /// </summary>
    internal sealed class PatchSiftTemplateCache : IDisposable
    {
        internal PatchNativeInterop.CacheHandle Handle { get; }
        private readonly byte[] _error = new byte[4096];
        private readonly object _sync = new object();
        internal PatchSiftTemplateCache()
        {
            PatchNativeInterop.ValidateAbi();
            PatchNativeInterop.Check(PatchNativeInterop.cis_patch_cache_create(out IntPtr pointer, _error, (uint)_error.Length), _error);
            Handle = new PatchNativeInterop.CacheHandle(pointer);
        }
        private PatchNativeInterop.CacheStats Snapshot()
        {
            lock (_sync)
            {
                var stats = new PatchNativeInterop.CacheStats { Size = 48 };
                PatchNativeInterop.Check(PatchNativeInterop.cis_patch_cache_stats(Handle, ref stats, _error, (uint)_error.Length), _error);
                return stats;
            }
        }
        public int Count => checked((int)Snapshot().Entries);
        public long HitCount => checked((long)Snapshot().Hits);
        public long MissCount => checked((long)Snapshot().Misses);
        public long ExactComparisonCount => checked((long)Snapshot().Comparisons);
        public double ExactComparisonElapsedMilliseconds => Snapshot().ComparisonMs;
        public double QuickKeyElapsedMilliseconds => Snapshot().QuickKeyMs;
        public void Dispose() { lock (_sync) Handle.Dispose(); }
    }

    /// <summary>
    /// Parallel.ForEach 每个 worker 独占一个原生上下文。SIFT/BFMatcher 按需创建并复用；
    /// 普通差分也使用该上下文，但关闭局部配准不会创建 SIFT 对象。
    /// </summary>
    internal sealed class PatchSiftWorker : IDisposable
    {
        internal PatchNativeInterop.WorkerHandle Handle { get; }
        internal byte[] Error { get; } = new byte[4096];
        internal object SyncRoot { get; } = new object();
        internal PatchSiftWorker(PatchSiftTemplateCache cache)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            PatchNativeInterop.Check(PatchNativeInterop.cis_patch_worker_create(cache.Handle, out IntPtr pointer,
                Error, (uint)Error.Length), Error);
            Handle = new PatchNativeInterop.WorkerHandle(pointer);
        }
        public void Dispose() { lock (SyncRoot) Handle.Dispose(); }
    }
}
