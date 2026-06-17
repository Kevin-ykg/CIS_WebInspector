using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;

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

        public void AddLog(string msg)
        {
            string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string formattedMsg = $"[{timeStamp}] {msg}";

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

                // 如果是离线加载模式，自动暂停并弹窗提示
                if (_cameraSource is OfflineImageSource)
                {
                    ExecuteStop(null);
                    System.Windows.MessageBox.Show("当前拼接图像已完成，请在确认无误后点击界面上的【恢复采集】按钮继续提取下一段！", "拼接完成", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
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
