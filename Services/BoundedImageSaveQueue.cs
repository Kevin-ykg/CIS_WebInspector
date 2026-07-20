using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 有界、串行的图像保存队列，避免每帧 Task.Run 导致线程池与内存无上限增长。
    /// 调用方必须保证 Data 在请求完成前不被修改；当前采集帧和拼接结果均满足该约束。
    /// </summary>
    internal sealed class BoundedImageSaveQueue : IDisposable
    {
        private sealed class SaveRequest
        {
            public byte[] Data { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Stride { get; set; }
            public int BitsPerPixel { get; set; }
            public string FilePath { get; set; }
            public string SuccessMessage { get; set; }
        }

        private readonly BlockingCollection<SaveRequest> _queue;
        private readonly Action<string> _log;
        private readonly Task _worker;
        private int _disposed;

        public BoundedImageSaveQueue(int capacity, Action<string> log)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _queue = new BlockingCollection<SaveRequest>(capacity);
            _log = log;
            _worker = Task.Factory.StartNew(
                Consume,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public int PendingCount => _queue.Count;

        /// <summary>
        /// 提交一个不复制像素的保存请求。队列满时返回 false，由调用方决定跳过诊断图或提示手动保存。
        /// </summary>
        public bool TryEnqueue(
            byte[] data,
            int width,
            int height,
            int stride,
            int bitsPerPixel,
            string filePath,
            int millisecondsTimeout,
            string successMessage = null)
        {
            if (data == null || string.IsNullOrWhiteSpace(filePath) ||
                Volatile.Read(ref _disposed) != 0 || _queue.IsAddingCompleted)
                return false;

            int requiredBytes;
            try { requiredBytes = checked(stride * height); }
            catch (OverflowException) { return false; }

            if (width <= 0 || height <= 0 || stride <= 0 || data.Length < requiredBytes)
                return false;

            var request = new SaveRequest
            {
                Data = data,
                Width = width,
                Height = height,
                Stride = stride,
                BitsPerPixel = bitsPerPixel,
                FilePath = filePath,
                SuccessMessage = successMessage
            };

            try
            {
                return _queue.TryAdd(request, millisecondsTimeout);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>单线程顺序编码和写盘，避免多个 JPEG 编码任务同时占用大块非托管内存。</summary>
        private void Consume()
        {
            foreach (SaveRequest request in _queue.GetConsumingEnumerable())
            {
                try
                {
                    string directory = Path.GetDirectoryName(request.FilePath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    MatType matType = request.BitsPerPixel == 8 ? MatType.CV_8UC1 : MatType.CV_8UC3;
                    GCHandle handle = GCHandle.Alloc(request.Data, GCHandleType.Pinned);
                    try
                    {
                        // Mat 借用固定数组，只在 ImWrite 同步执行期间有效；保存队列不持有原生视图。
                        using (Mat mat = Mat.FromPixelData(
                            request.Height,
                            request.Width,
                            matType,
                            handle.AddrOfPinnedObject(),
                            request.Stride))
                        {
                            var parameters = new[]
                            {
                                new ImageEncodingParam(ImwriteFlags.JpegQuality, 90)
                            };
                            Cv2.ImWrite(request.FilePath, mat, parameters);
                        }
                    }
                    finally
                    {
                        handle.Free();
                    }

                    if (!string.IsNullOrWhiteSpace(request.SuccessMessage))
                        _log?.Invoke(request.SuccessMessage);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[ERROR] 后台保存图像失败 ({Path.GetFileName(request.FilePath)}): {ex.Message}");
                }
            }
        }

        /// <summary>停止接收新请求并最多等待 10 秒写完已入队任务；完成后释放队列资源。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.CompleteAdding();
            try
            {
                // 退出程序时尽量写完已接收的少量有界任务，超时后不再阻塞 UI 关闭。
                _worker.Wait(10000);
            }
            catch (AggregateException ex)
            {
                _log?.Invoke($"[ERROR] 图像保存队列退出异常: {ex.Flatten().InnerException?.Message ?? ex.Message}");
            }

            if (_worker.IsCompleted)
                _queue.Dispose();
        }
    }
}
