using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 离线图像数据源：从磁盘目录加载已保存的相机小图，通过定时器模拟相机帧率逐帧输出。
    /// 用于在无物理相机环境下进行算法验证和调试。
    /// </summary>
    public sealed class OfflineImageSource : ICameraSource
    {
        public event EventHandler<FrameReadyEventArgs> FrameReady;
        public event EventHandler<string> ErrorOccurred;

        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }
        public int LineStride { get; private set; }
        public int BitsPerPixel { get; private set; }
        public bool IsRunning { get; private set; }

        private List<string> _imageFiles;
        private int _currentIndex;
        private Timer _timer;
        private int _intervalMs = 10; // 模拟帧间隔(ms)，设置得很小以最大化吞吐量，防重入锁会保证安全

        /// <summary>
        /// 初始化离线数据源。
        /// </summary>
        /// <param name="directoryPath">包含图像文件（BMP/PNG/TIFF）的目录路径</param>
        public bool Initialize(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    ErrorOccurred?.Invoke(this, $"Directory not found: {directoryPath}");
                    return false;
                }

                // 获取所有支持的图像文件并按文件名排序
                var extensions = new[] { "*.bmp", "*.png", "*.tif", "*.tiff", "*.jpg" };
                _imageFiles = extensions
                    .SelectMany(ext => Directory.GetFiles(directoryPath, ext))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (_imageFiles.Count == 0)
                {
                    ErrorOccurred?.Invoke(this, "No image files found in directory.");
                    return false;
                }

                // 读取第一张图像以获取尺寸参数
                using (var firstMat = OpenCvSharp.Cv2.ImRead(_imageFiles[0], OpenCvSharp.ImreadModes.Unchanged))
                {
                    ImageWidth = firstMat.Width;
                    ImageHeight = firstMat.Height;
                    BitsPerPixel = firstMat.Channels() == 1 ? 8 : 24;

                    int lineBytes = BitsPerPixel == 8 ? ImageWidth : 3 * ImageWidth;
                    LineStride = (lineBytes + 3) / 4 * 4;
                }

                _currentIndex = 0;
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Offline init failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>从第一张图重新开始定时播放；每次 Start 都重置当前文件索引。</summary>
        public void StartGrab()
        {
            if (_imageFiles == null || _imageFiles.Count == 0 || IsRunning) return;

            _currentIndex = 0;
            IsRunning = true;

            // 使用 Timer 模拟相机帧率
            _timer = new Timer(OnTimerTick, null, 0, _intervalMs);
        }

        /// <summary>
        /// 恢复采集（断点续传）：不清零当前索引，直接恢复 Timer。
        /// 返回 false 表示已经读取完所有图像。
        /// </summary>
        public bool ResumeGrab()
        {
            if (_imageFiles == null || _imageFiles.Count == 0 || IsRunning) return false;
            
            if (_currentIndex >= _imageFiles.Count)
            {
                // 如果已经读完了，返回 false 阻止恢复
                return false;
            }

            IsRunning = true;
            _timer = new Timer(OnTimerTick, null, 0, _intervalMs);
            return true;
        }

        private int _isProcessing = 0;

        /// <summary>读取下一张文件、统一通道/尺寸并复制为独立帧缓冲；文件顺序就是模拟采集顺序。</summary>
        private void OnTimerTick(object state)
        {
            // 防重入只跳过计时器 Tick，不跳过图像：_currentIndex 仅在当前处理成功后递增。
            if (System.Threading.Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
                return;

            try
            {
                if (!IsRunning || _currentIndex >= _imageFiles.Count)
                {
                    StopGrab();
                    return;
                }

                string filePath = _imageFiles[_currentIndex];

                // 按灰度或彩色读取
                var readMode = BitsPerPixel == 8
                    ? OpenCvSharp.ImreadModes.Grayscale
                    : OpenCvSharp.ImreadModes.Color;

                using (var originalMat = OpenCvSharp.Cv2.ImRead(filePath, readMode))
                {
                    if (originalMat.Empty()) return;

                    using (var mat = new OpenCvSharp.Mat())
                    {
                        int df = ConfigManager.Config.DownscaleFactor;
                        OpenCvSharp.Cv2.Resize(originalMat, mat, new OpenCvSharp.Size(originalMat.Width / df, originalMat.Height / df), 0, 0, OpenCvSharp.InterpolationFlags.Area);

                        int lineBytes = BitsPerPixel == 8 ? mat.Width : 3 * mat.Width;
                        int stride = (lineBytes + 3) / 4 * 4;
                        int totalBytes = stride * mat.Height;
                        byte[] data = new byte[totalBytes];

                        // 逐行拷贝（处理stride对齐）
                        if (lineBytes == stride && mat.IsContinuous())
                        {
                            Marshal.Copy(mat.Data, data, 0, totalBytes);
                        }
                        else
                        {
                            for (int row = 0; row < mat.Height; row++)
                            {
                                IntPtr srcRow = mat.Data + row * (int)mat.Step();
                                Marshal.Copy(srcRow, data, row * stride, lineBytes);
                            }
                        }

                        FrameReady?.Invoke(this, new FrameReadyEventArgs
                        {
                            DataArray = data,
                            BufferIndex = _currentIndex % 5,
                            IsBroken = false,
                            Width = mat.Width,
                            Height = mat.Height,
                            Stride = stride,
                            BitsPerPixel = BitsPerPixel
                        });
                    }
                }

                _currentIndex++;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Error reading frame {_currentIndex}: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isProcessing, 0);
            }
        }

        /// <summary>停止并释放 Timer，但保留当前索引，供 ResumeGrab 断点继续。</summary>
        public void StopGrab()
        {
            IsRunning = false;
            _timer?.Dispose();
            _timer = null;
        }

        /// <summary>设置模拟帧间隔（毫秒）</summary>
        /// <summary>设置模拟帧间隔；仅影响之后创建的 Timer。</summary>
        public void SetInterval(int intervalMs)
        {
            _intervalMs = Math.Max(10, intervalMs);
        }

        /// <summary>停止模拟播放并释放 Timer；磁盘图像均在单个 Tick 内打开和释放。</summary>
        public void Dispose()
        {
            StopGrab();
            _imageFiles?.Clear();
        }
    }
}
