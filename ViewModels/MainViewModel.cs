using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using OpenCvSharp;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 主界面业务协调器：连接相机/离线源、单线程帧处理、图像拼接、后台检测和 WPF 展示。
    /// 算法不在 UI 线程执行；Dispatcher 只发布轻量状态和预览，确保 WriteableBitmap 线程安全。
    /// </summary>
    public class MainViewModel : ViewModelBase, IDisposable
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dst, IntPtr src, int size);

        // ---- 服务层 ----
        // 帧处理队列保证采集顺序；保存队列是可丢弃的诊断支路；检测作业一次只允许最新任务发布结果。
        private ICameraSource _cameraSource;
        private readonly ImageStitcher _stitcher = new ImageStitcher();
        private readonly InspectionJobRunner _inspectionJobRunner = new InspectionJobRunner();
        private readonly object _inspectionJobSync = new object();
        private CancellationTokenSource _inspectionCancellation;
        private readonly object _frameProcessorSync = new object();
        private OrderedFrameProcessor _frameProcessor;
        private readonly BoundedImageSaveQueue _imageSaveQueue;
        private long _receivedFrameCount;
        private long _brokenFrameCount;
        private int _latestBufferIndex;
        private int _processingStopSignaled;
        private int _stopAfterCurrentFrame;
        private long _saveSequence;
        private long _skippedFrameSaveCount;
        private FrameReadyEventArgs _latestPreviewFrame;
        private int _previewDispatchPending;
        private bool _disposed;

        public MainViewModel()
        {
            int saveQueueCapacity = Math.Max(1, ConfigManager.Config?.ImageSaveQueueCapacity ?? 4);
            _imageSaveQueue = new BoundedImageSaveQueue(saveQueueCapacity, AddLog);
        }

        // ---- UI 绑定属性 ----
        private WriteableBitmap _livePreview;
        public WriteableBitmap LivePreview
        {
            get => _livePreview;
            set => SetProperty(ref _livePreview, value);
        }

        private WriteableBitmap _stitchedPreview;
        public WriteableBitmap StitchedPreview
        {
            get => _stitchedPreview;
            set => SetProperty(ref _stitchedPreview, value);
        }

        private BitmapImage _globalDefectPreview;
        public BitmapImage GlobalDefectPreview
        {
            get => _globalDefectPreview;
            set => SetProperty(ref _globalDefectPreview, value);
        }

        private ulong _frameCount;
        public ulong FrameCount
        {
            get => _frameCount;
            set => SetProperty(ref _frameCount, value);
        }

        private ulong _brokenCount;
        public ulong BrokenCount
        {
            get => _brokenCount;
            set => SetProperty(ref _brokenCount, value);
        }

        private int _bufferIndex;
        public int BufferIndex
        {
            get => _bufferIndex;
            set => SetProperty(ref _bufferIndex, value);
        }

        private string _statusText = "就绪";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    _startCommand?.RaiseCanExecuteChanged();
                    _stopCommand?.RaiseCanExecuteChanged();
                    _resumeCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        private string _lastStitchInfo = "";
        public string LastStitchInfo
        {
            get => _lastStitchInfo;
            set => SetProperty(ref _lastStitchInfo, value);
        }

        // ---- 最新拼接结果（内存中保留，供后续缺陷检测使用） ----
        private StitchedImageResult _lastStitchedResult;

        // ---- 自动保存设置 ----
        private bool _isAutoSaveEnabled;
        public bool IsAutoSaveEnabled
        {
            get => _isAutoSaveEnabled;
            set => SetProperty(ref _isAutoSaveEnabled, value);
        }

        private string _autoSaveDirectory;
        public string AutoSaveDirectory
        {
            get => _autoSaveDirectory;
            set => SetProperty(ref _autoSaveDirectory, value);
        }

        // ---- 动态日志集合 ----
        public System.Collections.ObjectModel.ObservableCollection<string> LogMessages { get; } = new System.Collections.ObjectModel.ObservableCollection<string>();

        private static readonly object _logLock = new object();

        /// <summary>把任意后台线程的日志安全投递到 UI，并限制终端文本长度避免长期运行无限增长。</summary>
        public void AddLog(string msg)
        {
            string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string formattedMsg = $"[{timeStamp}] {msg}";

            try
            {
                string logDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "日志");
                if (!System.IO.Directory.Exists(logDir))
                    System.IO.Directory.CreateDirectory(logDir);
                
                string dateStr = DateTime.Now.ToString("yyyyMMdd");
                string dbgLog = System.IO.Path.Combine(logDir, $"SysRunLog_{dateStr}.txt");
                
                lock (_logLock)
                {
                    System.IO.File.AppendAllText(dbgLog, formattedMsg + "\n");
                }
            }
            catch { }

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                LogMessages.Insert(0, formattedMsg);
                if (LogMessages.Count > 500)
                {
                    LogMessages.RemoveAt(LogMessages.Count - 1);
                }
            });
        }

        // ---- Commands ----
        private RelayCommand _startCommand;
        public RelayCommand StartCommand =>
            _startCommand ?? (_startCommand = new RelayCommand(_ => ExecuteStart(_), _ => !IsRunning));

        private RelayCommand _selectAutoSaveDirCommand;
        public RelayCommand SelectAutoSaveDirCommand =>
            _selectAutoSaveDirCommand ?? (_selectAutoSaveDirCommand = new RelayCommand(_ => ExecuteSelectAutoSaveDir(_)));

        private RelayCommand _stopCommand;
        public RelayCommand StopCommand =>
            _stopCommand ?? (_stopCommand = new RelayCommand(_ => ExecuteStop(_), _ => IsRunning));

        private RelayCommand _resumeCommand;
        public RelayCommand ResumeCommand =>
            _resumeCommand ?? (_resumeCommand = new RelayCommand(_ => ExecuteResume(_), _ => !IsRunning && _cameraSource is OfflineImageSource));

        private RelayCommand _loadOfflineCommand;
        public RelayCommand LoadOfflineCommand =>
            _loadOfflineCommand ?? (_loadOfflineCommand = new RelayCommand(_ => ExecuteLoadOffline(_), _ => !IsRunning));

        private RelayCommand _saveImageCommand;
        public RelayCommand SaveImageCommand =>
            _saveImageCommand ?? (_saveImageCommand = new RelayCommand(_ => ExecuteSaveImage(_)));

        private RelayCommand _loadConfigCommand;
        public RelayCommand LoadConfigCommand =>
            _loadConfigCommand ?? (_loadConfigCommand = new RelayCommand(_ => ExecuteLoadConfig(_), _ => !IsRunning));

        private RelayCommand _openCameraSettingsCommand;
        public RelayCommand OpenCameraSettingsCommand =>
            _openCameraSettingsCommand ?? (_openCameraSettingsCommand = new RelayCommand(_ => ExecuteOpenCameraSettings(_), _ => !IsRunning));

        private RelayCommand _openTlcSettingsCommand;
        public RelayCommand OpenTlcSettingsCommand =>
            _openTlcSettingsCommand ?? (_openTlcSettingsCommand = new RelayCommand(_ => ExecuteOpenTlcSettings(_), _ => !IsRunning));

        private RelayCommand _openAppSettingsCommand;
        public RelayCommand OpenAppSettingsCommand =>
            _openAppSettingsCommand ?? (_openAppSettingsCommand = new RelayCommand(_ => ExecuteOpenAppSettings(_), _ => !IsRunning));


        // ---- 选择自动保存目录 ----
        private void ExecuteSelectAutoSaveDir(object _)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择单帧图像批量保存文件夹",
                Filter = "文件夹|*.none",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "选择此文件夹" // 占位符
            };

            if (dialog.ShowDialog() == true)
            {
                AutoSaveDirectory = System.IO.Path.GetDirectoryName(dialog.FileName);
                AddLog($"自动保存目录已设置为: {AutoSaveDirectory}");
            }
        }

        // ---- 打开采集卡设置弹窗 ----
        private void ExecuteOpenCameraSettings(object _)
        {
            if (_cameraSource is CisCameraEngine onlineCamera)
            {
                var vm = new CameraSettingsViewModel(onlineCamera);
                var win = new Views.CameraSettingsWindow { DataContext = vm };
                win.ShowDialog();
            }
            else
            {
                System.Windows.MessageBox.Show("当前不在在线采集卡模式，无法配置底层硬件参数！", 
                    "不可用", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        // ---- 打开 TLC 相机设置弹窗 ----
        private void ExecuteOpenTlcSettings(object _)
        {
            var vm = new TlcSettingsViewModel();
            var win = new Views.TlcSettingsWindow { DataContext = vm };
            win.ShowDialog();
        }

        // ---- 打开全局参数设置弹窗 ----
        private void ExecuteOpenAppSettings(object _)
        {
            var win = new Views.AppSettingsWindow();
            var vm = new AppSettingsViewModel(win, this);
            win.DataContext = vm;
            
            // 如果用户点了保存，可以在这里加一些处理，例如通知部分服务重载参数
            if (win.ShowDialog() == true)
            {
                AddLog("全局系统参数已更新并保存。");
            }
        }


        // ---- 初始化在线相机 ----
        private void ExecuteLoadConfig(object _)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Volans 配置文件(*.arcf)|*.arcf|所有文件(*.*)|*.*",
                Title = "选择相机配置文件"
            };
            if (ofd.ShowDialog() != true) return;

            CleanupSource();
            var engine = new CisCameraEngine();
            _cameraSource = engine;
            _cameraSource.ErrorOccurred += OnError;

            if (_cameraSource.Initialize(ofd.FileName))
            {
                _cameraSource.FrameReady += OnFrameReady;

                // 相机源报告原始尺寸，后续二维码/拼接统一在缩小后的处理坐标系中运行。
                int df = ConfigManager.Config.DownscaleFactor;
                int outWidth = _cameraSource.ImageWidth / df;
                int outHeight = _cameraSource.ImageHeight / df;
                int outLineBytes = _cameraSource.BitsPerPixel == 8 ? outWidth : 3 * outWidth;
                int outStride = (outLineBytes + 3) / 4 * 4;

                _stitcher.Configure(outWidth, outHeight, outStride, _cameraSource.BitsPerPixel);

                // 在 UI 线程预分配 WriteableBitmap
                InitializeLivePreview(outWidth, outHeight, _cameraSource.BitsPerPixel);
                StatusText = $"相机已加载 ({outWidth}x{outHeight}, {_cameraSource.BitsPerPixel}bpp)";
            }
            else
            {
                StatusText = "相机加载失败";
            }
        }

        // ---- 加载离线数据 ----
        private void ExecuteLoadOffline(object param)
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

            CleanupSource();
            var offlineSource = new OfflineImageSource();
            _cameraSource = offlineSource;
            _cameraSource.ErrorOccurred += OnError;

            if (_cameraSource.Initialize(selectedDir))
            {
                _cameraSource.FrameReady += OnFrameReady;

                // 离线源和在线源使用同一输出几何，保证同一套 ROI/Mark 参数可复现现场流程。
                int df = ConfigManager.Config.DownscaleFactor;
                int outWidth = _cameraSource.ImageWidth / df;
                int outHeight = _cameraSource.ImageHeight / df;
                int outLineBytes = _cameraSource.BitsPerPixel == 8 ? outWidth : 3 * outWidth;
                int outStride = (outLineBytes + 3) / 4 * 4;

                _stitcher.Configure(outWidth, outHeight, outStride, _cameraSource.BitsPerPixel);
                InitializeLivePreview(outWidth, outHeight, _cameraSource.BitsPerPixel);
                StatusText = $"离线模式已加载 ({outWidth}x{outHeight}, {_cameraSource.BitsPerPixel}bpp)";
            }
            else
            {
                StatusText = "离线数据加载失败";
            }
        }

        // ---- 开始 / 停止 ----
        private void ExecuteStart(object _)
        {
            if (_cameraSource == null)
            {
                StatusText = "请先加载相机或离线数据";
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
                _cameraSource.StartGrab();
                IsRunning = true;
                StatusText = "采集中...";
                AddLog($"▶ 开始采集（帧处理队列容量: {ConfigManager.Config.FrameProcessingQueueCapacity}）");
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
            _cameraSource?.StopGrab();
            Interlocked.Exchange(ref _stopAfterCurrentFrame, 1);
            StopFrameProcessor(false);
            IsRunning = false;
            StatusText = "已停止";
            AddLog("■ 停止采集");
        }

        private void ExecuteResume(object _)
        {
            if (_cameraSource is OfflineImageSource offlineSource)
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
            if (processor == null || Volatile.Read(ref _stopAfterCurrentFrame) != 0)
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
            int timeout = _cameraSource is OfflineImageSource
                ? Timeout.Infinite
                : Math.Max(0, ConfigManager.Config.FrameProcessingEnqueueTimeoutMs);

            if (!processor.TryEnqueue(ownedFrame, timeout))
            {
                // 正常停止会关闭队列并唤醒正在等待的离线生产者，此时不应误报为过载。
                if (Volatile.Read(ref _stopAfterCurrentFrame) == 0)
                {
                    SignalProcessingStop(
                        $"帧处理队列已满（容量 {ConfigManager.Config.FrameProcessingQueueCapacity}），系统已安全停采，未静默丢帧。");
                }
            }
        }

        private void ProcessQueuedFrame(FrameReadyEventArgs frame)
        {
            // 拼接/二维码是不可丢帧的主链；实时预览和单帧保存只是旁路，不得反向阻塞检测语义。
            QueueLivePreview(frame);
            _stitcher.ProcessOwnedFrame(frame.DataArray, frame.Width, frame.Height, frame.Stride, frame.BitsPerPixel);

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
            int capacity = Math.Max(1, ConfigManager.Config?.FrameProcessingQueueCapacity ?? 3);
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
                _cameraSource?.StopGrab();
                Volatile.Read(ref _frameProcessor)?.DiscardPending();
            }
        }

        // ---- 拼接完成回调 ----
        /// <summary>
        /// 接收拥有独立缓冲区的拼接结果；离线模式立即截断后续帧，并异步启动一次完整检测作业。
        /// </summary>
        private void OnStitchCompleted(object sender, StitchedImageResult result)
        {
            // 保留在内存中供后续缺陷检测使用
            _lastStitchedResult = result;

            // 离线模式在处理线程内立即停源并清空后续排队帧，避免跨越第二个二维码继续消费。
            bool isOffline = _cameraSource is OfflineImageSource;
            if (isOffline)
            {
                Interlocked.Exchange(ref _stopAfterCurrentFrame, 1);
                _cameraSource.StopGrab();
                Volatile.Read(ref _frameProcessor)?.DiscardPending();
            }

            // 拼接图保存也进入同一个有界后台队列，不再为每张图创建无界 Task。
            if (IsAutoSaveEnabled)
            {
                string saveDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "拼接后图像");
                string fileName = $"stitched_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                string filePath = System.IO.Path.Combine(saveDir, fileName);
                bool queued = _imageSaveQueue.TryEnqueue(
                    result.Data,
                    result.Width,
                    result.Height,
                    result.Stride,
                    result.BitsPerPixel,
                    filePath,
                    1000,
                    $"已自动保存拼接图像: {fileName}");
                if (!queued)
                    AddLog("[WARN] 拼接图保存队列持续繁忙，本次拼接图未自动保存，请使用手动保存。检测结果不受影响。");
            }

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                UpdateStitchedPreview(result);
                string msg = $"拼接完成: {result.Width}x{result.Height}, QR: [{result.StartQrText}] → [{result.EndQrText}]";
                LastStitchInfo = msg;
                AddLog(msg);

                // 如果是离线加载模式，自动暂停并弹窗提示，同时启动离线缺陷检测流水线
                if (isOffline)
                {
                    ExecuteStop(null);
                    System.Windows.MessageBox.Show("当前拼接图像已完成，系统将在后台开始执行排版解析和离线缺陷检测...", "拼接完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    
                    StartInspectionJob(result);
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        // ---- 离线缺陷检测作业协调 ----
        /// <summary>以 fire-and-forget 方式启动作业，所有异常都由 RunInspectionJobAsync 内部记录。</summary>
        private void StartInspectionJob(StitchedImageResult result)
        {
            _ = RunInspectionJobAsync(result);
        }

        /// <summary>协调“最新作业优先”的取消与结果发布，重计算在线程池执行。</summary>
        private async Task RunInspectionJobAsync(StitchedImageResult result)
        {
            var cancellation = new CancellationTokenSource();
            CancellationTokenSource previous;

            lock (_inspectionJobSync)
            {
                previous = _inspectionCancellation;
                _inspectionCancellation = cancellation;
            }

            // 新拼接段到来时取消旧作业；旧作业即使稍后结束，也会被下方身份检查阻止覆盖新结果。
            previous?.Cancel();

            try
            {
                AppConfig config = ConfigManager.Config;
                InspectionJobResult jobResult = await Task.Run(
                    () => _inspectionJobRunner.Run(result, config, AddLog, cancellation.Token),
                    cancellation.Token);

                lock (_inspectionJobSync)
                {
                    // Cancellation 不能保证本机 OpenCV 调用立即退出，引用身份是最终的“过期结果”屏障。
                    if (!ReferenceEquals(_inspectionCancellation, cancellation))
                        return;
                }

                if (jobResult.Succeeded &&
                    jobResult.GlobalImageBytes != null &&
                    jobResult.GlobalImageBytes.Length > 0)
                {
                    PublishGlobalDefectPreview(jobResult.GlobalImageBytes);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("[缺陷流水线] 作业已取消。");
            }
            catch (Exception ex)
            {
                AddLog($"[缺陷流水线] 作业协调异常: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                lock (_inspectionJobSync)
                {
                    if (ReferenceEquals(_inspectionCancellation, cancellation))
                        _inspectionCancellation = null;
                }

                cancellation.Dispose();
            }
        }

        /// <summary>从内存 JPEG 创建 OnLoad/Freeze 的 BitmapImage，使流关闭后仍可跨线程安全绑定。</summary>
        private void PublishGlobalDefectPreview(byte[] imageBytes)
        {
            try
            {
                using (var stream = new System.IO.MemoryStream(imageBytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    GlobalDefectPreview = bitmap;
                }
            }
            catch (Exception ex)
            {
                AddLog($"[缺陷流水线] 无法加载全局缺陷图到界面: {ex.Message}");
            }
        }

        /// <summary>向当前检测作业发出取消请求；作业 finally 负责摘除并释放自己的 CancellationTokenSource。</summary>
        private void CancelInspectionJob()
        {
            CancellationTokenSource cancellation;
            lock (_inspectionJobSync)
            {
                cancellation = _inspectionCancellation;
            }

            cancellation?.Cancel();
        }

        // ---- 二维码超时警告回调 ----
        private void OnQrTimeoutWarning(object sender, string message)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                LastStitchInfo = message;

                // 如果是在离线模式下，建议暂停以便排查
                if (_cameraSource is OfflineImageSource)
                {
                    ExecuteStop(null);
                    System.Windows.MessageBox.Show(message + "\n请检查打印质量！", "识别异常", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
                else
                {
                    // 在线模式：向 PLC 写入告警信号
                    // 例如：往 PLC 的 M100 寄存器（或者线圈区）写入 True
                    //PlcManager.Instance.WriteCoil("M100", true); 
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        // ---- 错误回调 ----
        private void OnError(object sender, string message)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                StatusText = $"错误: {message}";
                AddLog($"[ERROR] {message}");
            });
        }

        // ---- 底层日志回调 ----
        private void OnLogMessageEvent(object sender, string message)
        {
            AddLog(message);
        }

        // ---- WriteableBitmap 操作 ----
        private void InitializeLivePreview(int width, int height, int bpp)
        {
            var pixelFormat = bpp == 8 ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            var palette = bpp == 8 ? BitmapPalettes.Gray256 : null;
            LivePreview = new WriteableBitmap(width, height, 96, 96, pixelFormat, palette);
        }

        /// <summary>在 UI 线程把最新托管帧复制到预分配 WriteableBitmap，不保留输入缓冲区引用。</summary>
        private void UpdateLivePreview(FrameReadyEventArgs e)
        {
            if (LivePreview == null) return;

            try
            {
                LivePreview.Lock();

                int copyBytes = e.Stride * e.Height;

                if (e.DataArray != null)
                {
                    Marshal.Copy(e.DataArray, 0, LivePreview.BackBuffer, copyBytes);
                }
                else if (e.DataPointer != IntPtr.Zero)
                {
                    CopyMemory(LivePreview.BackBuffer, e.DataPointer, copyBytes);
                }

                LivePreview.AddDirtyRect(new Int32Rect(0, 0, e.Width, e.Height));
                LivePreview.Unlock();
            }
            catch
            {
                try { LivePreview.Unlock(); } catch { }
            }
        }

        /// <summary>为超大拼接图生成有限宽度预览；原始分辨率数据仍保留在 StitchedImageResult 中供检测。</summary>
        private void UpdateStitchedPreview(StitchedImageResult result)
        {
            var pixelFormat = result.BitsPerPixel == 8 ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            var palette = result.BitsPerPixel == 8 ? BitmapPalettes.Gray256 : null;

            try
            {
                // WPF WriteableBitmap 无法承受 31212x33500 (1GB) 级别的超大纹理，会导致 ArgumentException 或 OOM。
                // 因此我们只为 UI 预览生成一个按比例缩小的预览图（例如限制最大宽度为 2000 像素）
                int maxWidth = 2000;
                float scale = (float)maxWidth / result.Width;
                if (scale >= 1.0f) scale = 1.0f;

                int previewWidth = (int)(result.Width * scale);
                int previewHeight = (int)(result.Height * scale);

                var matType = result.BitsPerPixel == 8 ? OpenCvSharp.MatType.CV_8UC1 : OpenCvSharp.MatType.CV_8UC3;
                GCHandle handle = GCHandle.Alloc(result.Data, GCHandleType.Pinned);
                byte[] previewData = null;
                int previewStride = 0;

                try
                {
                    using (var srcMat = OpenCvSharp.Mat.FromPixelData(result.Height, result.Width, matType, handle.AddrOfPinnedObject(), result.Stride))
                    using (var dstMat = new OpenCvSharp.Mat())
                    {
                        OpenCvSharp.Cv2.Resize(srcMat, dstMat, new OpenCvSharp.Size(previewWidth, previewHeight), 0, 0, OpenCvSharp.InterpolationFlags.Area);
                        previewStride = (int)dstMat.Step();
                        previewData = new byte[previewStride * previewHeight];
                        System.Runtime.InteropServices.Marshal.Copy(dstMat.Data, previewData, 0, previewData.Length);
                    }
                }
                finally
                {
                    handle.Free();
                }

                var wb = new WriteableBitmap(previewWidth, previewHeight, 96, 96, pixelFormat, palette);
                wb.Lock();
                System.Runtime.InteropServices.Marshal.Copy(previewData, 0, wb.BackBuffer, previewData.Length);
                wb.AddDirtyRect(new Int32Rect(0, 0, previewWidth, previewHeight));
                wb.Unlock();

                StitchedPreview = wb;
            }
            catch (Exception ex)
            {
                LastStitchInfo = $"预览生成失败: {ex.Message} (尺寸 {result.Width}x{result.Height})";
            }
        }

        // ---- 资源清理 ----
        private void CleanupSource()
        {
            // 顺序很重要：先取消检测和停止消费者，再退订/释放生产者，避免释放后仍有回调进入。
            CancelInspectionJob();
            StopFrameProcessor(false);
            if (_cameraSource != null)
            {
                _cameraSource.FrameReady -= OnFrameReady;
                _cameraSource.ErrorOccurred -= OnError;
                _cameraSource.Dispose();
                _cameraSource = null;
            }
        }

        /// <summary>窗体关闭时停止生产/消费链并确定性释放相机、二维码模型和后台保存线程。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ExecuteStop(null);
            CleanupSource();
            _stitcher?.Dispose();
            _imageSaveQueue?.Dispose();
        }
    }
}
