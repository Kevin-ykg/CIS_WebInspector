using System;
using System.Windows.Media.Imaging;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 主界面业务协调器：连接相机/离线源、单线程帧处理、图像拼接、后台检测和 WPF 展示。
    /// 算法不在 UI 线程执行；Dispatcher 只发布轻量状态和预览，确保 WriteableBitmap 线程安全。
    /// </summary>
    public partial class MainViewModel : ViewModelBase, IDisposable
    {
        // ---- 服务层 ----
        // 帧处理队列保证采集顺序；保存队列是可丢弃的诊断支路；检测作业一次只允许最新任务发布结果。
        private readonly AcquisitionSession _acquisitionSession = new AcquisitionSession();
        private readonly ImageStitcher _stitcher = new ImageStitcher();
        private readonly AppLogger _appLogger;
        private readonly InspectionJobCoordinator _inspectionJobCoordinator;
        private readonly object _frameProcessorSync = new object();
        private OrderedFrameProcessor _frameProcessor;
        private readonly BoundedImageSaveQueue _imageSaveQueue;
        // 只读快捷入口；数据源及其配置快照的所有权统一属于 AcquisitionSession。
        private ICameraSource CameraSource => _acquisitionSession.Source;
        private AppConfig ActiveAcquisitionConfig => _acquisitionSession.ConfigSnapshot;
        private long _receivedFrameCount;
        private long _brokenFrameCount;
        private int _latestBufferIndex;
        private int _processingStopSignaled;
        private int _stopAfterCurrentFrame;
        private long _saveSequence;
        private long _skippedFrameSaveCount;
        private FrameReadyEventArgs _latestPreviewFrame;
        private int _previewDispatchPending;
        // 单消费者当前正在执行的离线文件序号；OnStitchCompleted 同步回调据此建立恢复检查点。
        private int _activeOfflineFrameIndex = -1;
        private bool _disposed;

        public MainViewModel()
        {
            // 日志服务先于其他后台服务创建；后续组件统一写入同一文件，并由事件桥接到 UI。
            _appLogger = new AppLogger();
            _appLogger.MessageWritten += QueueUiLog;
            var inspectionRunner = new InspectionJobRunner(logger: _appLogger);
            _inspectionJobCoordinator = new InspectionJobCoordinator(inspectionRunner, _appLogger);
            AppConfig startupConfig = ConfigManager.CaptureSnapshot();
            int saveQueueCapacity = Math.Max(1, startupConfig.ImageSaveQueueCapacity);
            _imageSaveQueue = new BoundedImageSaveQueue(saveQueueCapacity, AddLog);

            // 自动保存属于跨会话设置：启动时从 app_config.json 的配置快照恢复，
            // 不再依赖 MainViewModel 字段的默认值。
            _isAutoSaveEnabled = startupConfig.EnableAutoSave;
            _autoSaveDirectory = startupConfig.AutoSaveDirectory ?? string.Empty;
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
    }
}
