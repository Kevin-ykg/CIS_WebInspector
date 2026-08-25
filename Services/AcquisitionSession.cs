using System;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 管理一个在线相机或离线图库数据源的完整生命周期。该类只负责创建、事件订阅、
    /// 初始化失败清理和确定性释放；帧队列、拼接及 WPF 状态仍由上层协调。
    /// </summary>
    public sealed class AcquisitionSession : IDisposable
    {
        private ICameraSource _source;
        private EventHandler<FrameReadyEventArgs> _frameReadyHandler;
        private EventHandler<string> _errorHandler;
        private bool _disposed;

        /// <summary>当前已经成功初始化的数据源；初始化失败或关闭后为 null。</summary>
        public ICameraSource Source => _source;

        /// <summary>
        /// 当前数据源加载时使用的配置快照。整个采集会话固定使用该版本，避免设置窗口
        /// 在运行过程中改变队列、缩放、二维码或拼接参数。
        /// </summary>
        public AppConfig ConfigSnapshot { get; private set; }

        /// <summary>
        /// 用新数据源替换当前会话。ErrorOccurred 在 Initialize 前订阅，以保留初始化诊断；
        /// FrameReady 仅在初始化成功后订阅，防止失败对象继续向主流程推送帧。
        /// </summary>
        public bool TryOpen(
            Func<AppConfig, ICameraSource> sourceFactory,
            AppConfig configSnapshot,
            string initializationPath,
            EventHandler<FrameReadyEventArgs> frameReadyHandler,
            EventHandler<string> errorHandler,
            out string errorMessage)
        {
            if (sourceFactory == null)
                throw new ArgumentNullException(nameof(sourceFactory));
            if (configSnapshot == null)
                throw new ArgumentNullException(nameof(configSnapshot));
            if (_disposed)
                throw new ObjectDisposedException(nameof(AcquisitionSession));

            Close();

            ICameraSource candidate = null;
            bool errorSubscribed = false;
            bool frameSubscribed = false;

            try
            {
                candidate = sourceFactory(configSnapshot);
                if (candidate == null)
                {
                    errorMessage = "数据源工厂返回了空对象。";
                    return false;
                }

                if (errorHandler != null)
                {
                    candidate.ErrorOccurred += errorHandler;
                    errorSubscribed = true;
                }

                if (!candidate.Initialize(initializationPath))
                {
                    errorMessage = "数据源初始化返回失败。";
                    return false;
                }

                if (frameReadyHandler != null)
                {
                    candidate.FrameReady += frameReadyHandler;
                    frameSubscribed = true;
                }

                _source = candidate;
                _frameReadyHandler = frameReadyHandler;
                _errorHandler = errorHandler;
                ConfigSnapshot = configSnapshot;
                candidate = null; // 所有权已转移到当前会话。
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            finally
            {
                if (candidate != null)
                {
                    if (frameSubscribed)
                        candidate.FrameReady -= frameReadyHandler;
                    if (errorSubscribed)
                        candidate.ErrorOccurred -= errorHandler;

                    // 构造或初始化到一半的设备也可能已经持有 Timer、SDK 句柄或原生缓冲区。
                    try { candidate.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// 摘除事件后释放当前数据源。调用方应先停止帧消费者，保证已经进入回调的帧不会
        /// 在数据源释放后继续访问采集状态。
        /// </summary>
        public void Close()
        {
            ICameraSource source = _source;
            EventHandler<FrameReadyEventArgs> frameReadyHandler = _frameReadyHandler;
            EventHandler<string> errorHandler = _errorHandler;

            _source = null;
            _frameReadyHandler = null;
            _errorHandler = null;
            ConfigSnapshot = null;

            if (source == null)
                return;

            if (frameReadyHandler != null)
                source.FrameReady -= frameReadyHandler;
            if (errorHandler != null)
                source.ErrorOccurred -= errorHandler;

            source.Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Close();
        }
    }
}
