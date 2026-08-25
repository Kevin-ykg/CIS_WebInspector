using System;
using System.Threading;
using System.Threading.Tasks;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 负责段级检测作业的生命周期，不依赖 WPF：新任务取消旧任务、完整检测串行执行，
    /// 并通过递增作业号阻止已过期结果发布。视觉算法和结果展示分别留在 Runner 与 UI 层。
    /// </summary>
    public sealed class InspectionJobCoordinator : IDisposable
    {
        private readonly IInspectionJobRunner _runner;
        private readonly IAppLogger _logger;
        private readonly object _sync = new object();

        // OpenCV 原生调用不能被 CancellationToken 强制中断。该门避免新旧作业同时加载
        // 大幅 TIFF/PNG 并争抢 CPU；等待执行的旧作业可以立即响应取消。
        private readonly SemaphoreSlim _executionGate = new SemaphoreSlim(1, 1);

        private CancellationTokenSource _currentCancellation;
        private long _nextJobId;
        private long _currentJobId;
        private bool _disposed;

        public InspectionJobCoordinator(IInspectionJobRunner runner, IAppLogger logger = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _logger = logger ?? NullAppLogger.Instance;
        }

        /// <summary>
        /// 提交一个拼接段并立即返回作业号。该方法不会阻塞调用线程；新作业会请求取消旧作业，
        /// 但旧作业正在执行的单次原生调用仍可能运行到下一个取消检查点。
        /// </summary>
        public long Submit(
            StitchedImageResult stitchedResult,
            AppConfig configSnapshot,
            Func<InspectionJobResult, long, Task> publishAsync)
        {
            if (stitchedResult == null)
                throw new ArgumentNullException(nameof(stitchedResult));
            if (configSnapshot == null)
                throw new ArgumentNullException(nameof(configSnapshot));
            if (publishAsync == null)
                throw new ArgumentNullException(nameof(publishAsync));

            var cancellation = new CancellationTokenSource();
            CancellationTokenSource previous;
            long jobId;

            lock (_sync)
            {
                if (_disposed)
                {
                    cancellation.Dispose();
                    throw new ObjectDisposedException(nameof(InspectionJobCoordinator));
                }

                previous = _currentCancellation;
                _currentCancellation = cancellation;
                jobId = ++_nextJobId;
                _currentJobId = jobId;
            }

            // 放在锁外执行，避免取消回调反向进入协调器造成锁竞争。
            TryCancel(previous);
            _ = RunAsync(jobId, stitchedResult, configSnapshot, cancellation, publishAsync);
            return jobId;
        }

        /// <summary>
        /// 判断作业是否仍是当前最新任务。UI 在 Dispatcher 真正执行发布动作时应再检查一次，
        /// 以覆盖“结果已排队、此时新拼接段到达”的竞争窗口。
        /// </summary>
        public bool IsCurrent(long jobId)
        {
            lock (_sync)
            {
                return !_disposed &&
                       _currentCancellation != null &&
                       _currentJobId == jobId;
            }
        }

        /// <summary>向当前作业发出取消请求；资源由作业自己的 finally 负责释放。</summary>
        public void CancelCurrent()
        {
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                cancellation = _currentCancellation;
            }

            TryCancel(cancellation);
        }

        private async Task RunAsync(
            long jobId,
            StitchedImageResult stitchedResult,
            AppConfig configSnapshot,
            CancellationTokenSource cancellation,
            Func<InspectionJobResult, long, Task> publishAsync)
        {
            bool gateEntered = false;

            try
            {
                await _executionGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                gateEntered = true;
                cancellation.Token.ThrowIfCancellationRequested();

                if (!IsCurrent(jobId))
                    return;

                InspectionJobResult result = await Task.Run(
                    () => _runner.Run(stitchedResult, configSnapshot, cancellation.Token),
                    cancellation.Token).ConfigureAwait(false);

                // 取消只能在算法检查点生效，因此 Runner 返回后仍必须用作业号阻止旧结果发布。
                if (!IsCurrent(jobId))
                    return;

                await publishAsync(result, jobId).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Write("[缺陷流水线] 作业已取消。");
            }
            catch (Exception ex)
            {
                _logger.Write($"[缺陷流水线] 作业协调异常: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (gateEntered)
                    _executionGate.Release();

                lock (_sync)
                {
                    if (_currentJobId == jobId && ReferenceEquals(_currentCancellation, cancellation))
                        _currentCancellation = null;
                }

                cancellation.Dispose();
            }
        }

        private static void TryCancel(CancellationTokenSource cancellation)
        {
            if (cancellation == null)
                return;

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 作业可能恰好在引用读取后结束并释放；此时已经不需要再次取消。
            }
        }

        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                cancellation = _currentCancellation;
            }

            TryCancel(cancellation);

            // 不在此处释放 _executionGate：正在运行的 OpenCV 调用无法同步停止，稍后仍会在
            // finally 中 Release。协调器随 MainViewModel 一同退出，由进程回收这个托管句柄。
        }
    }
}
