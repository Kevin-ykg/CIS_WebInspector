using System;
using System.Threading;
using System.Windows;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 采集与有序帧处理，负责数据源生命周期、背压队列、预览抽样和拼接输入。
    /// </summary>
    public partial class MainViewModel
    {
        // ---- 初始化在线相机 ----
        private void ExecuteLoadConfig(object _)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Volans 配置文件(*.arcf)|*.arcf|所有文件(*.*)|*.*",
                Title = "选择相机配置文件"
            };
            if (ofd.ShowDialog() != true) return;

            InitializeDataSource(
                config => new CisCameraEngine(config),
                ofd.FileName,
                "相机已加载",
                "相机加载失败");
        }

        // ---- 加载离线数据 ----
        private void ExecuteLoadOffline(object _)
        {
            CancelInspectionJob();

            // 使用 OpenFileDialog 选择目录中的任意一张图片，然后取其所在目录
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图像文件(*.bmp;*.png;*.tif;*.jpg)|*.bmp;*.png;*.tif;*.tiff;*.jpg|所有文件(*.*)|*.*",
                Title = "选择图像目录中的任意一张图片"
            };
            if (ofd.ShowDialog() != true) return;

            string selectedDir = System.IO.Path.GetDirectoryName(ofd.FileName);
            if (string.IsNullOrEmpty(selectedDir)) return;

            InitializeDataSource(
                config => new OfflineImageSource(config),
                selectedDir,
                "离线模式已加载",
                "离线数据加载失败");
        }

        /// <summary>
        /// 在线相机和离线图库共用的数据源初始化边界。调用顺序固定为：捕获配置快照 → 停止旧源 →
        /// 创建并订阅新源 → 初始化 → 配置拼接处理几何。失败源在离开本方法前立即释放。
        /// </summary>
        private void InitializeDataSource(
            Func<AppConfig, ICameraSource> sourceFactory,
            string initializationPath,
            string successStatusPrefix,
            string failureStatus)
        {
            if (sourceFactory == null)
                throw new ArgumentNullException(nameof(sourceFactory));

            AppConfig acquisitionConfig = ConfigManager.CaptureSnapshot();
            CleanupSource();

            if (!_acquisitionSession.TryOpen(
                    sourceFactory,
                    acquisitionConfig,
                    initializationPath,
                    OnFrameReady,
                    OnError,
                    out string initializationError))
            {
                StatusText = failureStatus;
                if (!string.IsNullOrWhiteSpace(initializationError))
                    AddLog($"[ERROR] {failureStatus}: {initializationError}");
                return;
            }

            ICameraSource source = CameraSource;

            // 数据源公开原始几何，二维码、拼接和预览统一使用 DownscaleFactor 后的处理坐标系。
            int downscaleFactor = Math.Max(1, acquisitionConfig.DownscaleFactor);
            int processingWidth = Math.Max(1, source.ImageWidth / downscaleFactor);
            int processingHeight = Math.Max(1, source.ImageHeight / downscaleFactor);
            int processingLineBytes = source.BitsPerPixel == 8
                ? processingWidth
                : 3 * processingWidth;
            int processingStride = (processingLineBytes + 3) / 4 * 4;

            _stitcher.Configure(
                processingWidth,
                processingHeight,
                processingStride,
                source.BitsPerPixel,
                acquisitionConfig);
            InitializeLivePreview(processingWidth, processingHeight, source.BitsPerPixel);
            StatusText = $"{successStatusPrefix} ({processingWidth}x{processingHeight}, {source.BitsPerPixel}bpp)";
        }

        // ---- 开始 / 停止 ----
        private void ExecuteStart(object _)
        {
            if (CameraSource == null)
            {
                StatusText = "请先加载相机或离线数据";
                return;
            }
            if (ActiveAcquisitionConfig == null)
            {
                StatusText = "当前数据源缺少运行配置，请重新加载相机或离线图库";
                return;
            }

            // 开始采集前同步完成模型加载和预热，避免首帧在处理队列中承受初始化延迟。
            if (!_stitcher.InitializeQrDetector(out string qrInitError))
            {
                StatusText = "WeChatQRCode 初始化失败";
                AddLog($"[QR] WeChatQRCode 初始化失败：{qrInitError}");
                return;
            }

            FrameCount = 0;
            BrokenCount = 0;
            BufferIndex = 0;
            Interlocked.Exchange(ref _receivedFrameCount, 0);
            Interlocked.Exchange(ref _brokenFrameCount, 0);
            Volatile.Write(ref _latestBufferIndex, 0);
            Interlocked.Exchange(ref _processingStopSignaled, 0);
            Interlocked.Exchange(ref _stopAfterCurrentFrame, 0);
            Interlocked.Exchange(ref _latestPreviewFrame, null);
            Volatile.Write(ref _activeOfflineFrameIndex, -1);
            _stitcher.Reset();

            // 防止重复订阅
            _stitcher.StitchCompleted -= OnStitchCompleted;
            _stitcher.QrTimeoutWarning -= OnQrTimeoutWarning;
            _stitcher.LogMessageEvent -= OnLogMessageEvent;

            _stitcher.StitchCompleted += OnStitchCompleted;
            _stitcher.QrTimeoutWarning += OnQrTimeoutWarning;
            _stitcher.LogMessageEvent += OnLogMessageEvent;

            // 必须先启动消费者再启动相机生产者，防止首帧到达时队列尚未创建。
            StartFrameProcessor();
            try
            {
                CameraSource.StartGrab();
                IsRunning = true;
                StatusText = "采集中...";
                AddLog($"▶ 开始采集（帧处理队列容量: {ActiveAcquisitionConfig.FrameProcessingQueueCapacity}）");
            }
            catch (Exception ex)
            {
                StopFrameProcessor(false);
                IsRunning = false;
                StatusText = $"启动失败: {ex.Message}";
                AddLog($"[ERROR] 启动采集失败: {ex.Message}");
            }
        }

        private void ExecuteStop(object _)
        {
            // 主动停止不继续排空旧帧：先停生产者，再让当前帧结束并丢弃尚未处理的队列项。
            CameraSource?.StopGrab();
            Interlocked.Exchange(ref _stopAfterCurrentFrame, 1);
            StopFrameProcessor(false);
            IsRunning = false;
            StatusText = "已停止";
            AddLog("■ 停止采集");
        }

        private void ExecuteResume(object _)
        {
            if (CameraSource is OfflineImageSource offlineSource)
            {
                Interlocked.Exchange(ref _processingStopSignaled, 0);
                Interlocked.Exchange(ref _stopAfterCurrentFrame, 0);
                StartFrameProcessor();
                if (offlineSource.ResumeGrab())
                {
                    IsRunning = true;
                    StatusText = "恢复采集中...";
                }
                else
                {
                    StopFrameProcessor(false);
                    System.Windows.MessageBox.Show("当前文件夹中的所有图像已处理完毕！", "采集完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
            }
        }

        // ---- 保存拼接图像 ----
        private void ExecuteSaveImage(object _)
        {
            if (_lastStitchedResult == null || _lastStitchedResult.Data == null)
            {
                StatusText = "没有可保存的拼接图像";
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "BMP 图像(*.bmp)|*.bmp|PNG 图像(*.png)|*.png|所有文件(*.*)|*.*",
                Title = "保存拼接图像",
                FileName = $"stitched_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            if (sfd.ShowDialog() != true) return;

            try
            {
                _lastStitchedResult.SaveToFile(sfd.FileName);
                StatusText = $"图像已保存: {sfd.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = $"保存失败: {ex.Message}";
            }
        }


        // ---- 帧就绪回调（来自后台线程） ----
        private void OnFrameReady(object sender, FrameReadyEventArgs e)
        {
            OrderedFrameProcessor processor = Volatile.Read(ref _frameProcessor);
            // 同一回调内固定数据源与配置引用。关闭会话会先摘除处理器，再清空这些属性；
            // 已经进入的回调仍可安全完成本次判定，不会在中途读到半更新状态。
            ICameraSource source = CameraSource;
            AppConfig acquisitionConfig = ActiveAcquisitionConfig;
            if (processor == null || source == null || acquisitionConfig == null ||
                Volatile.Read(ref _stopAfterCurrentFrame) != 0)
                return;

            FrameReadyEventArgs ownedFrame;
            try
            {
                // 相机 SDK 指针的有效期可能只到回调结束；队列只能接收拥有独立托管缓冲区的帧。
                ownedFrame = OrderedFrameProcessor.CreateOwnedFrame(e);
            }
            catch (Exception ex)
            {
                SignalProcessingStop($"帧缓冲无效: {ex.Message}");
                return;
            }

            Interlocked.Increment(ref _receivedFrameCount);
            if (ownedFrame.IsBroken) Interlocked.Increment(ref _brokenFrameCount);
            Volatile.Write(ref _latestBufferIndex, ownedFrame.BufferIndex);

            // 离线源可等待消费者腾出空间以保证逐帧完整；在线源仅短暂等待，超时后安全停采。
            int timeout = source is OfflineImageSource
                ? Timeout.Infinite
                : Math.Max(0, acquisitionConfig.FrameProcessingEnqueueTimeoutMs);

            if (!processor.TryEnqueue(ownedFrame, timeout))
            {
                // 正常停止会关闭队列并唤醒正在等待的离线生产者，此时不应误报为过载。
                if (Volatile.Read(ref _stopAfterCurrentFrame) == 0)
                {
                    SignalProcessingStop(
                        $"帧处理队列已满（容量 {acquisitionConfig.FrameProcessingQueueCapacity}），系统已安全停采，未静默丢帧。");
                }
            }
        }

        private void ProcessQueuedFrame(FrameReadyEventArgs frame)
        {
            // 拼接/二维码是不可丢帧的主链；实时预览和单帧保存只是旁路，不得反向阻塞检测语义。
            QueueLivePreview(frame);
            Volatile.Write(ref _activeOfflineFrameIndex, frame.SourceFrameIndex);
            try
            {
                // StitchCompleted 在本调用栈内同步触发，因此回调读取到的就是当前真实消费帧序号。
                _stitcher.ProcessOwnedFrame(frame.DataArray, frame.Width, frame.Height, frame.Stride, frame.BitsPerPixel);
            }
            finally
            {
                Volatile.Write(ref _activeOfflineFrameIndex, -1);
            }

            if (IsAutoSaveEnabled && !string.IsNullOrWhiteSpace(AutoSaveDirectory))
            {
                long sequence = Interlocked.Increment(ref _saveSequence);
                string fileName = $"frame_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{sequence:D6}.jpg";
                string filePath = System.IO.Path.Combine(AutoSaveDirectory, fileName);
                bool queued = _imageSaveQueue.TryEnqueue(
                    frame.DataArray,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    frame.BitsPerPixel,
                    filePath,
                    0);

                if (!queued)
                {
                    long skipped = Interlocked.Increment(ref _skippedFrameSaveCount);
                    if (skipped == 1 || skipped % 50 == 0)
                    {
                        AddLog($"[WARN] 自动保存队列已满，已跳过 {skipped} 张诊断单帧；检测与拼接不受影响。");
                    }
                }
            }

            if (Volatile.Read(ref _stopAfterCurrentFrame) != 0)
                Volatile.Read(ref _frameProcessor)?.DiscardPending();
        }

        private void QueueLivePreview(FrameReadyEventArgs frame)
        {
            // “latest wins”：高帧率下覆盖尚未显示的旧预览，不积压 UI 消息，也不影响拼接处理的每一帧。
            Interlocked.Exchange(ref _latestPreviewFrame, frame);
            SchedulePreviewUpdate();
        }

        /// <summary>合并多个预览请求为一个 Render 优先级 UI 任务，并在完成后检查是否出现更新帧。</summary>
        private void SchedulePreviewUpdate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || Interlocked.CompareExchange(ref _previewDispatchPending, 1, 0) != 0)
                return;

            dispatcher.InvokeAsync(() =>
            {
                FrameReadyEventArgs latest = Interlocked.Exchange(ref _latestPreviewFrame, null);
                if (latest != null) UpdateLivePreview(latest);

                FrameCount = (ulong)Math.Max(0, Interlocked.Read(ref _receivedFrameCount));
                BrokenCount = (ulong)Math.Max(0, Interlocked.Read(ref _brokenFrameCount));
                BufferIndex = Volatile.Read(ref _latestBufferIndex);

                Interlocked.Exchange(ref _previewDispatchPending, 0);
                if (Volatile.Read(ref _latestPreviewFrame) != null)
                    SchedulePreviewUpdate();
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private void StartFrameProcessor()
        {
            StopFrameProcessor(false);

            // 容量是允许的瞬时生产/消费抖动，不是长期缓存；满队列时在线采集会安全停机而非静默丢帧。
            int capacity = Math.Max(1, ActiveAcquisitionConfig?.FrameProcessingQueueCapacity ?? 3);
            var processor = new OrderedFrameProcessor(capacity, ProcessQueuedFrame, ex =>
            {
                SignalProcessingStop($"帧处理异常: {ex.Message}");
            });

            lock (_frameProcessorSync)
            {
                _frameProcessor = processor;
            }
        }

        /// <summary>
        /// 原子摘除当前处理器后停止消费者；drain=false 丢弃等待帧，但仍等待正在处理的帧安全返回。
        /// </summary>
        private void StopFrameProcessor(bool drain)
        {
            OrderedFrameProcessor processor;
            lock (_frameProcessorSync)
            {
                processor = _frameProcessor;
                _frameProcessor = null;
            }

            if (processor == null) return;

            bool stopped = processor.Stop(drain, 10000);
            if (!stopped)
                AddLog("[WARN] 帧处理线程未在 10 秒内退出；已禁止继续入队，请检查算法耗时或非托管调用。");
            processor.Dispose();
        }

        /// <summary>确保过载/算法异常只触发一次安全停机，并把最终状态切回 UI 线程。</summary>
        private void SignalProcessingStop(string reason)
        {
            if (Interlocked.Exchange(ref _processingStopSignaled, 1) != 0) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.InvokeAsync(() =>
                {
                    AddLog($"[ERROR] {reason}");
                    ExecuteStop(null);
                    StatusText = reason;
                }, System.Windows.Threading.DispatcherPriority.Send);
            }
            else
            {
                CameraSource?.StopGrab();
                Volatile.Read(ref _frameProcessor)?.DiscardPending();
            }
        }

        /// <summary>
        /// 在算法消费者线程仍持有当前帧序号时暂停离线源，并把恢复位置固定到当前帧之后。
        /// Timer 生产者可能已经预读到文件末尾，因此恢复检查点必须来自真实消费进度。
        /// </summary>
        private void PauseOfflineAfterCurrentFrame(
            OfflineImageSource offlineSource,
            string reason)
        {
            if (offlineSource == null)
                return;

            Interlocked.Exchange(ref _stopAfterCurrentFrame, 1);
            offlineSource.StopGrab();
            int discarded = Volatile.Read(ref _frameProcessor)?.DiscardPending() ?? 0;

            int completedFrameIndex = Volatile.Read(ref _activeOfflineFrameIndex);
            if (completedFrameIndex < 0)
            {
                AddLog(
                    $"[Offline] {reason}：未取得当前消费帧序号；" +
                    $"已丢弃 {discarded} 张预读帧。");
                return;
            }

            offlineSource.SetResumeFromFrame(completedFrameIndex + 1);
            AddLog(
                $"[Offline] {reason}发生于第 {completedFrameIndex + 1} 张；" +
                $"已丢弃 {discarded} 张预读帧，恢复时将从第 {completedFrameIndex + 2} 张继续。");
        }
    }
}
