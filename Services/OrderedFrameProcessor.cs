using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 有界、单消费者的帧处理队列。
    /// 采集线程只负责提交拥有稳定生命周期的帧快照，二维码检测与拼接始终按采集顺序执行。
    /// </summary>
    internal sealed class OrderedFrameProcessor : IDisposable
    {
        private readonly BlockingCollection<FrameReadyEventArgs> _queue;
        private readonly Action<FrameReadyEventArgs> _processFrame;
        private readonly Action<Exception> _onError;
        private readonly Task _worker;
        private int _disposed;
        private int _workerThreadId;

        public OrderedFrameProcessor(
            int capacity,
            Action<FrameReadyEventArgs> processFrame,
            Action<Exception> onError)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _processFrame = processFrame ?? throw new ArgumentNullException(nameof(processFrame));
            _onError = onError;
            _queue = new BlockingCollection<FrameReadyEventArgs>(capacity);
            _worker = Task.Factory.StartNew(
                Consume,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>
        /// 将事件参数规范化为队列可安全持有的帧。在线、离线数据源都必须在触发事件前
        /// 创建独立 DataArray；这里复用该数组，不再对整帧做第二次深拷贝。
        /// </summary>
        public static FrameReadyEventArgs CreateOwnedFrame(FrameReadyEventArgs source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Width <= 0 || source.Height <= 0 || source.Stride <= 0)
                throw new ArgumentException("帧尺寸或步长无效。", nameof(source));

            int byteCount = checked(source.Stride * source.Height);
            if (source.DataArray == null)
                throw new ArgumentException("帧不包含有效图像数据。", nameof(source));
            if (source.DataArray.Length < byteCount)
                throw new ArgumentException("托管帧缓冲区长度不足。", nameof(source));

            return new FrameReadyEventArgs
            {
                DataArray = source.DataArray,
                BufferIndex = source.BufferIndex,
                SourceFrameIndex = source.SourceFrameIndex,
                IsBroken = source.IsBroken,
                Width = source.Width,
                Height = source.Height,
                Stride = source.Stride,
                BitsPerPixel = source.BitsPerPixel
            };
        }

        /// <summary>在指定时间内提交一帧；队列已停止、已释放或超时均返回 false。</summary>
        public bool TryEnqueue(FrameReadyEventArgs frame, int millisecondsTimeout)
        {
            if (frame == null || Volatile.Read(ref _disposed) != 0 || _queue.IsAddingCompleted)
                return false;

            try
            {
                return _queue.TryAdd(frame, millisecondsTimeout);
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

        /// <summary>清空尚未开始处理的帧并返回数量；正在由单消费者处理的帧不受影响。</summary>
        public int DiscardPending()
        {
            int discarded = 0;
            while (_queue.TryTake(out _)) discarded++;
            return discarded;
        }

        /// <summary>
        /// 停止接收新帧。drain=false 时丢弃尚未开始的帧，但会等待当前帧处理完成。
        /// </summary>
        public bool Stop(bool drain, int waitMilliseconds)
        {
            if (!_queue.IsAddingCompleted)
                _queue.CompleteAdding();

            if (!drain)
                DiscardPending();

            if (Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref _workerThreadId))
                return false;

            try
            {
                return _worker.Wait(Math.Max(0, waitMilliseconds));
            }
            catch (AggregateException ex)
            {
                _onError?.Invoke(ex.Flatten().InnerException ?? ex);
                return true;
            }
        }

        /// <summary>唯一消费者循环，保证二维码检测和拼接严格遵守入队顺序。</summary>
        private void Consume()
        {
            Volatile.Write(ref _workerThreadId, Thread.CurrentThread.ManagedThreadId);
            try
            {
                foreach (FrameReadyEventArgs frame in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        _processFrame(frame);
                    }
                    catch (Exception ex)
                    {
                        DiscardPending();
                        _onError?.Invoke(ex);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _workerThreadId, 0);
            }
        }

        /// <summary>停止接收并尝试结束消费者；不在消费者线程自身等待，避免自锁。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop(false, 10000);
            if (_worker.IsCompleted)
                _queue.Dispose();
        }
    }
}
