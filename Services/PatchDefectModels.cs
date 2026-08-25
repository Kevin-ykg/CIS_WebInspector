using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Features2D;

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
    /// 单次裁切批次内复用的 TIFF/Alpha 模板特征缓存。
    /// 缓存只在一批零件处理期间存在，避免跨批次持有 Mat。快速签名只负责分桶，
    /// 命中前仍做逐像素精确比较，因此哈希碰撞不会串用不同模板的 SIFT 特征。
    /// </summary>
    internal sealed class PatchSiftTemplateCache : IDisposable
    {
        private const int QuickKeySignatureSize = 16;

        private sealed class TemplateEntry : IDisposable
        {
            public Mat Representative { get; }
            public PatchSiftTemplateFeatures Features { get; }

            public TemplateEntry(Mat representative, PatchSiftTemplateFeatures features)
            {
                Representative = representative;
                Features = features;
            }

            public void Dispose()
            {
                Features.Dispose();
                Representative.Dispose();
            }
        }

        private sealed class TemplateBucket : IDisposable
        {
            public object SyncRoot { get; } = new object();
            public List<TemplateEntry> Entries { get; } = new List<TemplateEntry>();

            public void Dispose()
            {
                lock (SyncRoot)
                {
                    foreach (TemplateEntry entry in Entries)
                        entry.Dispose();
                    Entries.Clear();
                }
            }
        }

        private readonly ConcurrentDictionary<string, TemplateBucket> _buckets =
            new ConcurrentDictionary<string, TemplateBucket>();
        private int _entryCount;
        private long _hitCount;
        private long _missCount;
        private long _exactComparisonCount;
        private long _exactComparisonTicks;
        private long _quickKeyTicks;

        public int Count => Volatile.Read(ref _entryCount);
        public long HitCount => Interlocked.Read(ref _hitCount);
        public long MissCount => Interlocked.Read(ref _missCount);
        public long ExactComparisonCount => Interlocked.Read(ref _exactComparisonCount);
        public double ExactComparisonElapsedMilliseconds =>
            Interlocked.Read(ref _exactComparisonTicks) * 1000.0 / Stopwatch.Frequency;
        public double QuickKeyElapsedMilliseconds =>
            Interlocked.Read(ref _quickKeyTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>按图像内容复用模板 SIFT 特征；新条目持有模板副本和描述子，随批次缓存释放。</summary>
        public PatchSiftTemplateFeatures GetOrCreate(Mat templateFeatureImage, SIFT sift)
        {
            if (templateFeatureImage == null || templateFeatureImage.Empty())
                throw new ArgumentException("模板特征图不能为空。", nameof(templateFeatureImage));
            if (sift == null)
                throw new ArgumentNullException(nameof(sift));

            long keyStart = Stopwatch.GetTimestamp();
            string quickKey = ComputeQuickKey(templateFeatureImage);
            Interlocked.Add(ref _quickKeyTicks, Stopwatch.GetTimestamp() - keyStart);

            // 16×16 二维网格签名只用于快速分桶；命中缓存前仍进行原生像素级精确比较，
            // 因此签名碰撞不会导致不同模板复用同一组 SIFT 特征。
            TemplateBucket bucket = _buckets.GetOrAdd(quickKey, _ => new TemplateBucket());
            lock (bucket.SyncRoot)
            {
                foreach (TemplateEntry entry in bucket.Entries)
                {
                    Interlocked.Increment(ref _exactComparisonCount);
                    long compareStart = Stopwatch.GetTimestamp();
                    bool exactMatch = AreExactlyEqual(templateFeatureImage, entry.Representative);
                    Interlocked.Add(ref _exactComparisonTicks, Stopwatch.GetTimestamp() - compareStart);
                    if (exactMatch)
                    {
                        Interlocked.Increment(ref _hitCount);
                        return entry.Features;
                    }
                }

                Mat representative = templateFeatureImage.Clone();
                try
                {
                    PatchSiftTemplateFeatures features = PatchSiftTemplateFeatures.Create(templateFeatureImage, sift);
                    bucket.Entries.Add(new TemplateEntry(representative, features));
                    representative = null;
                    Interlocked.Increment(ref _entryCount);
                    Interlocked.Increment(ref _missCount);
                    return features;
                }
                finally
                {
                    representative?.Dispose();
                }
            }
        }

        private static bool AreExactlyEqual(Mat first, Mat second)
        {
            return first.Rows == second.Rows &&
                   first.Cols == second.Cols &&
                   first.Type() == second.Type() &&
                   Cv2.Norm(first, second, NormTypes.L1) == 0.0;
        }

        /// <summary>对规则采样的 16×16 像素生成快速分桶键，不把该键当作最终相等判据。</summary>
        private static unsafe string ComputeQuickKey(Mat image)
        {
            const ulong offsetBasis = 1469598103934665603UL;
            const ulong prime = 1099511628211UL;
            int pixelBytes = checked((int)image.ElemSize());
            byte* data = image.DataPointer;
            long step = (long)image.Step();
            ulong hash = offsetBasis;

            for (int gridY = 0; gridY < QuickKeySignatureSize; gridY++)
            {
                int row = Math.Min(
                    image.Rows - 1,
                    (int)(((2L * gridY + 1) * image.Rows) / (2L * QuickKeySignatureSize)));
                for (int gridX = 0; gridX < QuickKeySignatureSize; gridX++)
                {
                    int column = Math.Min(
                        image.Cols - 1,
                        (int)(((2L * gridX + 1) * image.Cols) / (2L * QuickKeySignatureSize)));
                    byte* pixel = data + row * step + column * pixelBytes;
                    for (int channelByte = 0; channelByte < pixelBytes; channelByte++)
                    {
                        hash ^= *(pixel + channelByte);
                        hash *= prime;
                    }
                }
            }

            return $"{image.Rows}x{image.Cols}:{image.Type()}:{hash:X16}";
        }

        public void Dispose()
        {
            foreach (TemplateBucket bucket in _buckets.Values)
                bucket.Dispose();
            _buckets.Clear();
            Volatile.Write(ref _entryCount, 0);
        }
    }

    internal sealed class PatchSiftTemplateFeatures : IDisposable
    {
        public KeyPoint[] KeyPoints { get; private set; }
        public Mat Descriptors { get; private set; }

        private PatchSiftTemplateFeatures(KeyPoint[] keyPoints, Mat descriptors)
        {
            KeyPoints = keyPoints;
            Descriptors = descriptors;
        }

        /// <summary>提取模板关键点和描述子；返回对象拥有 descriptors Mat。</summary>
        public static PatchSiftTemplateFeatures Create(Mat featureImage, SIFT sift)
        {
            var descriptors = new Mat();
            try
            {
                sift.DetectAndCompute(featureImage, null, out KeyPoint[] keyPoints, descriptors);
                return new PatchSiftTemplateFeatures(keyPoints, descriptors);
            }
            catch
            {
                descriptors.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Descriptors?.Dispose();
            Descriptors = null;
            KeyPoints = Array.Empty<KeyPoint>();
        }
    }

    /// <summary>Parallel.ForEach 每个 worker 独占的非线程安全 OpenCV 对象。</summary>
    internal sealed class PatchSiftWorker : IDisposable
    {
        public SIFT Sift { get; }
        public BFMatcher Matcher { get; }
        public PatchSiftTemplateCache TemplateCache { get; }

        public PatchSiftWorker(PatchSiftTemplateCache templateCache)
        {
            TemplateCache = templateCache ?? throw new ArgumentNullException(nameof(templateCache));
            // 与原始二次配准保持一致；worker 只负责复用对象，不改变算法参数。
            Sift = SIFT.Create(100);
            Matcher = new BFMatcher(NormTypes.L2);
        }

        public void Dispose()
        {
            Matcher.Dispose();
            Sift.Dispose();
        }
    }
}
