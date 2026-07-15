using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using OpenCvSharp;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 主界面 ViewModel：协调相机引擎、拼接引擎与 WPF UI 之间的交互。
    /// 负责 Dispatcher 线程切换，确保 WriteableBitmap 的所有操作在 UI 线程执行。
    /// </summary>
    public class MainViewModel : ViewModelBase, IDisposable
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dst, IntPtr src, int size);

        // ---- 服务层 ----
        private ICameraSource _cameraSource;
        private readonly ImageStitcher _stitcher = new ImageStitcher();

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

            if (!_stitcher.InitializeQrDetector(out string qrInitError))
            {
                StatusText = "WeChatQRCode 初始化失败";
                AddLog($"[QR] WeChatQRCode 初始化失败：{qrInitError}");
                return;
            }

            FrameCount = 0;
            BrokenCount = 0;
            BufferIndex = 0;
            _stitcher.Reset();

            // 防止重复订阅
            _stitcher.StitchCompleted -= OnStitchCompleted;
            _stitcher.QrTimeoutWarning -= OnQrTimeoutWarning;
            _stitcher.LogMessageEvent -= OnLogMessageEvent;

            _stitcher.StitchCompleted += OnStitchCompleted;
            _stitcher.QrTimeoutWarning += OnQrTimeoutWarning;
            _stitcher.LogMessageEvent += OnLogMessageEvent;

            _cameraSource.StartGrab();
            IsRunning = true;
            StatusText = "采集中...";
            AddLog("▶ 开始采集");
        }

        private void ExecuteStop(object _)
        {
            _cameraSource?.StopGrab();
            IsRunning = false;
            StatusText = "已停止";
            AddLog("■ 停止采集");
        }

        private void ExecuteResume(object _)
        {
            if (_cameraSource is OfflineImageSource offlineSource)
            {
                if (offlineSource.ResumeGrab())
                {
                    IsRunning = true;
                    StatusText = "恢复采集中...";
                }
                else
                {
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
            // 更新统计
            FrameCount++;
            if (e.IsBroken) BrokenCount++;
            BufferIndex = e.BufferIndex;

            // 刷新实时预览（切到 UI 线程）
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                UpdateLivePreview(e);
            }, System.Windows.Threading.DispatcherPriority.Render);

            // 送入拼接引擎（在当前线程执行）
            _stitcher.ProcessFrame(e.DataPointer, e.DataArray, e.Width, e.Height, e.Stride, e.BitsPerPixel);

            // ---- 单帧自动保存逻辑 ----
            if (IsAutoSaveEnabled && !string.IsNullOrEmpty(AutoSaveDirectory))
            {
                // 1. 深拷贝图像数据，防止被底层的 DMA 环形缓存覆盖
                byte[] arrayToSave = null;
                if (e.DataArray != null)
                {
                    arrayToSave = new byte[e.DataArray.Length];
                    Buffer.BlockCopy(e.DataArray, 0, arrayToSave, 0, e.DataArray.Length);
                }
                else if (e.DataPointer != IntPtr.Zero)
                {
                    int bytesCount = e.Stride * e.Height;
                    arrayToSave = new byte[bytesCount];
                    System.Runtime.InteropServices.Marshal.Copy(e.DataPointer, arrayToSave, 0, bytesCount);
                }

                // 2. 扔到后台线程执行缓慢的磁盘 I/O
                if (arrayToSave != null)
                {
                    int w = e.Width;
                    int h = e.Height;
                    int s = e.Stride;
                    int bpp = e.BitsPerPixel;
                    string dir = AutoSaveDirectory;
                    string fileName = $"frame_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                    string filePath = System.IO.Path.Combine(dir, fileName);

                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var matType = bpp == 8 ? OpenCvSharp.MatType.CV_8UC1 : OpenCvSharp.MatType.CV_8UC3;

                            // 必须通过指针来构造带有 Stride (步长) 的 Mat
                            var handle = System.Runtime.InteropServices.GCHandle.Alloc(arrayToSave, System.Runtime.InteropServices.GCHandleType.Pinned);
                            try
                            {
                                using (var mat = OpenCvSharp.Mat.FromPixelData(h, w, matType, handle.AddrOfPinnedObject(), s))
                                {
                                    // 使用 OpenCV 保存为 JPG，指定压缩质量
                                    var prms = new OpenCvSharp.ImageEncodingParam[] {
                                        new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.JpegQuality, 90)
                                    };
                                    OpenCvSharp.Cv2.ImWrite(filePath, mat, prms);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[ERROR] 自动保存单帧失败: {ex.Message}");
                        }
                    });
                }
            }
        }

        // ---- 拼接完成回调 ----
        private void OnStitchCompleted(object sender, StitchedImageResult result)
        {
            // 保留在内存中供后续缺陷检测使用
            _lastStitchedResult = result;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                UpdateStitchedPreview(result);
                string msg = $"拼接完成: {result.Width}x{result.Height}, QR: [{result.StartQrText}] → [{result.EndQrText}]";
                LastStitchInfo = msg;
                AddLog(msg);

                // ---- 新增：自动保存拼接后的图像 ----
                if (IsAutoSaveEnabled)
                {
                    string saveDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "拼接后图像");
                    if (!System.IO.Directory.Exists(saveDir))
                    {
                        System.IO.Directory.CreateDirectory(saveDir);
                    }
                    string fileName = $"stitched_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                    string filePath = System.IO.Path.Combine(saveDir, fileName);

                    byte[] dataToSave = result.Data;
                    int w = result.Width;
                    int h = result.Height;
                    int s = result.Stride;
                    int bpp = result.BitsPerPixel;

                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var matType = bpp == 8 ? OpenCvSharp.MatType.CV_8UC1 : OpenCvSharp.MatType.CV_8UC3;
                            var handle = System.Runtime.InteropServices.GCHandle.Alloc(dataToSave, System.Runtime.InteropServices.GCHandleType.Pinned);
                            try
                            {
                                using (var mat = OpenCvSharp.Mat.FromPixelData(h, w, matType, handle.AddrOfPinnedObject(), s))
                                {
                                    var prms = new OpenCvSharp.ImageEncodingParam[] {
                                        new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.JpegQuality, 90)
                                    };
                                    OpenCvSharp.Cv2.ImWrite(filePath, mat, prms);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                            AddLog($"已自动保存拼接图像: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[ERROR] 自动保存拼接图失败: {ex.Message}");
                        }
                    });
                }

                // 如果是离线加载模式，自动暂停并弹窗提示，同时启动离线缺陷检测流水线
                if (_cameraSource is OfflineImageSource)
                {
                    ExecuteStop(null);
                    System.Windows.MessageBox.Show("当前拼接图像已完成，系统将在后台开始执行排版解析和离线缺陷检测...", "拼接完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    
                    // 启动缺陷检测流水线
                    System.Threading.Tasks.Task.Run(() => RunDefectPipeline(result));
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        // ---- 离线缺陷检测流水线 ----
        private void RunDefectPipeline(StitchedImageResult result)
        {
            Mat tiffMat = null;
            Mat alphaMask = null;
            Mat cisMat = null;
            try
            {
                AddLog("开始执行离线缺陷检测流水线...");
                
                var config = ConfigManager.Config;
                if (config == null) return;

                // 1. 解析 Debug.log 获取排版信息
                string qrCode = result.EndQrText;
                if (string.IsNullOrEmpty(qrCode))
                {
                    AddLog("[缺陷流水线] 未找到有效的结束二维码，终止流水线。");
                    return;
                }

                AddLog($"正在解析 Debug.log，目标二维码: {qrCode} ...");
                var layoutInfo = DebugLogParser.ParseForQrCode(config.DebugLogPath, qrCode, config.TiffImageDir);
                if (layoutInfo == null)
                {
                    AddLog("[缺陷流水线] 解析失败或未找到对应的排版日志。");
                    return;
                }

                AddLog($"成功解析排版日志，原图: {layoutInfo.TiffFileName}，共 {layoutInfo.Parts.Count} 个有效零件。");

                // 2. 加载 TIFF 原图
                AddLog($"正在加载 TIFF 原图...");
                if (System.IO.File.Exists(layoutInfo.TiffFullPath))
                {
                    tiffMat = OpenCvSharp.Cv2.ImRead(layoutInfo.TiffFullPath, OpenCvSharp.ImreadModes.Unchanged);
                }
                else
                {
                    AddLog($"[缺陷流水线] 无法找到 TIFF 原图文件: {layoutInfo.TiffFullPath}");
                    return;
                }

                if (tiffMat.Empty())
                {
                    AddLog($"[缺陷流水线] TIFF 图像加载失败。");
                    return;
                }

                // 如果 TIFF 是 4 通道，分离 Alpha 通道作为原始设计二值掩膜，然后 alpha 融合到白底
                // 参照 align_diff.py: alpha_mask = channels[3] 作为完美设计二值图像
                // 注意：TIFF 原图可能非常大，不能创建多个全尺寸 float32 Mat，改用逐行处理
                if (tiffMat.Channels() == 4)
                {
                    int h = tiffMat.Height;
                    int w = tiffMat.Width;

                    // 提取 Alpha 通道作为独立掩膜
                    Mat[] tiffChannels = Cv2.Split(tiffMat);
                    alphaMask = tiffChannels[3].Clone();
                    foreach (var ch in tiffChannels) ch.Dispose();

                    AddLog($"  提取Alpha通道: 非零像素={Cv2.CountNonZero(alphaMask)}, " +
                           $"覆盖率={Cv2.CountNonZero(alphaMask) * 100.0 / (h * w):F1}%");

                    // Alpha 混合到白底 → BGR 3通道
                    // 使用 unsafe 指针 + Parallel.For 多线程并行，避免逐像素 P/Invoke 开销
                    Mat result8u = new Mat(h, w, MatType.CV_8UC3);
                    unsafe
                    {
                        System.Threading.Tasks.Parallel.For(0, h, row =>
                        {
                            byte* srcRow = (byte*)tiffMat.Ptr(row);
                            byte* dstRow = (byte*)result8u.Ptr(row);

                            for (int col = 0; col < w; col++)
                            {
                                byte sb = srcRow[0];
                                byte sg = srcRow[1];
                                byte sr = srcRow[2];
                                byte sa = srcRow[3];

                                if (sa == 255)
                                {
                                    dstRow[0] = sb;
                                    dstRow[1] = sg;
                                    dstRow[2] = sr;
                                }
                                else if (sa == 0)
                                {
                                    dstRow[0] = 255;
                                    dstRow[1] = 255;
                                    dstRow[2] = 255;
                                }
                                else
                                {
                                    float a = sa * (1f / 255f);
                                    float inv = 1f - a;
                                    dstRow[0] = (byte)(sb * a + 255f * inv);
                                    dstRow[1] = (byte)(sg * a + 255f * inv);
                                    dstRow[2] = (byte)(sr * a + 255f * inv);
                                }

                                srcRow += 4;
                                dstRow += 3;
                            }
                        });
                    }

                    tiffMat.Dispose();
                    tiffMat = result8u;
                }
                else
                {
                    AddLog("  [WARN] TIFF无Alpha通道，将使用统一阈值检测");
                }

                // 3. 构建 CIS 图像的 Mat（保持原始通道，不做强制转换）
                AddLog($"正在计算图像对齐变换矩阵...");
                var matType = result.BitsPerPixel == 8 ? OpenCvSharp.MatType.CV_8UC1 : OpenCvSharp.MatType.CV_8UC3;
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(result.Data, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    cisMat = OpenCvSharp.Mat.FromPixelData(result.Height, result.Width, matType, handle.AddrOfPinnedObject(), result.Stride).Clone();
                }
                finally
                {
                    handle.Free();
                }

                // 4. 计算变换矩阵，并获取自动计算的最佳二值化阈值
                int optimalThresh = 127;
                var qrAnchor = new CisQrAnchor
                {
                    CenterX = result.EndQrCenterX,
                    GlobalCenterY = result.EndQrGlobalY,
                    SegmentStartGlobalY = result.SegmentStartGlobalY,
                    PixelWidth = result.EndQrPixelWidth,
                    PixelHeight = result.EndQrPixelHeight
                };
                var alignmentOptions = MarkAlignmentOptions.FromConfig(config);
                using (AlignmentResult alignment = ImageAligner.ComputeTransform(
                           cisMat, tiffMat, qrAnchor, alignmentOptions,
                           out optimalThresh, out string alignmentDiagnostic))
                {
                    if (alignment?.GlobalTransform == null || alignment.GlobalTransform.Empty())
                    {
                        AddLog($"[缺陷流水线] 图像对齐失败：{alignmentDiagnostic}");
                        return;
                    }
                    AddLog(
                        $"变换矩阵计算成功！模式={alignment.Mode}, 质量={alignment.QualityStatus}, " +
                        $"自动最佳二值化阈值={optimalThresh}；{alignmentDiagnostic}");

                    // 5. 图像变换
                    AddLog("正在将 CIS 图像变换到 TIFF 空间...");
                    using (Mat cisWarped = ImageAligner.WarpToTiffSpace(cisMat, alignment, tiffMat.Size()))
                    {

                // 6. 裁切小图 + 缺陷检测
                int finalCisThresh = optimalThresh + config.DefectCisThreshOffset;
                finalCisThresh = Math.Max(0, Math.Min(255, finalCisThresh));
                AddLog($"正在按排版坐标裁切零件小图并执行缺陷检测 (应用 CIS 阈值 {finalCisThresh})...");
                double scale = config.LayoutDpi / 25.4;
                string outDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.CroppedOutputDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                var defectTaskResult = PatchCropper.CropAndSave(cisWarped, tiffMat, alphaMask, layoutInfo.Parts, outDir, scale, config.LayoutOriginXmm, config.LayoutOriginYmm, finalCisThresh, config);
                var defectResults = defectTaskResult.Results;

                // 汇总检测结果
                int totalParts = defectResults.Count;
                int passCount = 0;
                int failCount = 0;
                foreach (var dr in defectResults)
                {
                    if (dr.IsPass)
                        passCount++;
                    else
                        failCount++;

                    string status = dr.IsPass ? "✓ Pass" : "✗ FAIL";
                    AddLog($"  [{status}] {dr.PartId} — 内部缺陷: {dr.InnerDefectCount}个(最大{dr.MaxAreaInner}px²) | 外部缺陷: {dr.OuterDefectCount}个(最大{dr.MaxAreaOuter}px²)");
                }

                // 加载全局缺陷大图到 UI (通过内存字节流加载，不依赖本地硬盘文件)
                if (defectTaskResult.GlobalImageBytes != null && defectTaskResult.GlobalImageBytes.Length > 0)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        try
                        {
                            using (var stream = new System.IO.MemoryStream(defectTaskResult.GlobalImageBytes))
                            {
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.StreamSource = stream;
                                bmp.EndInit();
                                bmp.Freeze();
                                GlobalDefectPreview = bmp;
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[缺陷流水线] 无法加载全局缺陷图到界面: {ex.Message}");
                        }
                    });
                }

                        AddLog(
                            $"[缺陷流水线] 全部完成！共 {totalParts} 个零件 | 合格 {passCount} | " +
                            $"不合格 {failCount} | 全局对准={alignment.Mode}/{alignment.QualityStatus} | " +
                            $"检测={alignment.DetectionMilliseconds:F1}ms, 建图={alignment.MapGenerationMilliseconds:F1}ms, " +
                            $"变换={alignment.RemapMilliseconds:F1}ms | 结果保存在: {outDir}");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"[缺陷流水线] 执行发生严重异常: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                cisMat?.Dispose();
                alphaMask?.Dispose();
                tiffMat?.Dispose();
            }
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
            if (_cameraSource != null)
            {
                _cameraSource.FrameReady -= OnFrameReady;
                _cameraSource.ErrorOccurred -= OnError;
                _cameraSource.Dispose();
                _cameraSource = null;
            }
        }

        public void Dispose()
        {
            ExecuteStop(null);
            CleanupSource();
            _stitcher?.Dispose();
        }
    }
}
