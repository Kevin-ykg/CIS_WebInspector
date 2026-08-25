using System.Collections.Concurrent;
using System.Threading;
using System.Windows;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 主界面日志显示适配器。日志持久化由 AppLogger 负责，本类只把后台消息批量发布到 WPF UI。
    /// </summary>
    public partial class MainViewModel
    {
        // ---- 动态日志集合 ----
        public System.Collections.ObjectModel.ObservableCollection<string> LogMessages { get; } = new System.Collections.ObjectModel.ObservableCollection<string>();

        private readonly ConcurrentQueue<string> _pendingUiLogs = new ConcurrentQueue<string>();
        private int _logDispatchPending;

        /// <summary>统一日志入口；持久化、时间戳和 UI 消息发布均由独立日志服务完成。</summary>
        public void AddLog(string msg)
        {
            _appLogger.Write(msg);
        }

        /// <summary>
        /// 合并同一时间片内的后台日志，只向 Dispatcher 投递一个任务。二维码逐帧诊断较密集时，
        /// 这能避免 UI 消息队列被大量微小 InvokeAsync 操作占满，同时保留每一条日志及原顺序。
        /// </summary>
        private void QueueUiLog(string message)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _pendingUiLogs.Enqueue(message);
            ScheduleUiLogDispatch();
        }

        /// <summary>处理“清空队列后到复位标志前”到达的新日志，避免竞争窗口遗留消息。</summary>
        private void ScheduleUiLogDispatch()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null ||
                Interlocked.CompareExchange(ref _logDispatchPending, 1, 0) != 0)
                return;

            dispatcher.InvokeAsync(() =>
            {
                try
                {
                    while (_pendingUiLogs.TryDequeue(out string pending))
                    {
                        LogMessages.Insert(0, pending);
                        // 在批量排空过程中就维持上限，避免 UI 长时间阻塞后先增长到数千项再统一裁剪。
                        if (LogMessages.Count > 500)
                            LogMessages.RemoveAt(LogMessages.Count - 1);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _logDispatchPending, 0);
                    if (!_pendingUiLogs.IsEmpty)
                        ScheduleUiLogDispatch();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

    }
}
