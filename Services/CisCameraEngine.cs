using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ares;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 在线相机数据源：封装 VolansDevice SDK，管理非托管内存池与异步采集回调。
    /// 实现 IDisposable 确保相机句柄与非托管内存的确定性释放。
    /// </summary>
    public sealed class CisCameraEngine : ICameraSource
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dst, IntPtr src, int size);

        // ---- 事件 ----
        public event EventHandler<FrameReadyEventArgs> FrameReady;
        public event EventHandler<string> ErrorOccurred;

        // ---- 设备 ----
        private VolansDevice _device;
        private bool _disposed;

        // ---- 图像参数 ----
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }
        public int LineStride { get; private set; }
        public int BitsPerPixel { get; private set; }
        public bool IsRunning { get; private set; }

        private int _lineBytes;
        private int _bufferSize;
        private bool _useUserBuffer;

        // ---- 非托管内存池 ----
        private int BufferCount => ConfigManager.Config.BufferCount;
        private readonly List<IntPtr> _imgBuffers = new List<IntPtr>();
        private int _imgIndex;

        // ---- 回调句柄（防止GC回收委托） ----
        private VolansEventHandler _callbackImageReady;

        // ---- 统计 ----
        public ulong FrameCount { get; private set; }
        public ulong BrokenCount { get; private set; }

        public bool Initialize(string configPath)
        {
            try
            {
                // 枚举采集卡
                VolansDeviceInfo[] infos = VolansDevice.getAllDeviceInfos();
                if (infos == null || infos.GetLength(0) == 0)
                {
                    ErrorOccurred?.Invoke(this, "Device NOT detected!");
                    return false;
                }

                // 打开设备（索引0）
                _device = VolansDevice.openDevice(0);
                if (!_device.isValid())
                {
                    ErrorOccurred?.Invoke(this, "Device can NOT be opened!");
                    return false;
                }

                bool b = true;
                uint width = 0, height = 0, cbLo = 0, cbHi = 0;
                uint databitsWidth = 0, imageFormat = 0, scanType = 0;

                // 加载配置文件
                b = b && _device.loadConfigurationFile(configPath);

                // 获取图像参数
                b = b && _device.getProperty((uint)AriPropertyType.AriProp_ImageWidth, ref width);
                b = b && _device.getProperty((uint)AriPropertyType.AriProp_ImageHeight, ref height);
                b = b && _device.getProperty((uint)AriPropertyType.AriProp_FrameSize_Low, ref cbLo);
                b = b && _device.getProperty((uint)AriPropertyType.AriProp_FrameSize_High, ref cbHi);

                if (b)
                {
                    ulong cFrameBytes = ((ulong)cbLo & 0xFFFFFFFF) | ((ulong)cbHi << 32);
                    if (cFrameBytes > int.MaxValue) b = false;
                }

                b = b && _device.getProperty((uint)AriPropertyType.AriProp_DataBitsWidth, ref databitsWidth);
                b = b && _device.getProperty((uint)AriPropertyType.AriProp_ImageType, ref imageFormat);
                b = b && _device.getProperty((uint)AriPropertyType.AriProp_ScanType, ref scanType);

                if (!b)
                {
                    ErrorOccurred?.Invoke(this, "Failed to read device properties.");
                    return false;
                }

                // 设置公开属性
                ImageWidth = (int)width;
                ImageHeight = (int)height;
                BitsPerPixel = (int)databitsWidth;

                // 计算行字节数与步长（4字节对齐）
                _lineBytes = imageFormat == (uint)AriDef_ImageType.ARI_IMAGETYPE_MONOCHROME
                    ? (int)width
                    : 3 * (int)width;
                LineStride = (_lineBytes + 3) / 4 * 4;
                _bufferSize = LineStride * (int)height;

                // 非阻塞采集模式
                b = b && _device.setProperty((uint)AriPropertyType.AriProp_GrabBlockMode,
                    (uint)AriDef_GrabBlockMode.ARI_GRAB_NON_BLOCK);

                // 接收残余帧（线阵相机）
                b = b && _device.setProperty((uint)AriPropertyType.AriProp_EnableRemainingFrames, 1);

                // 分配非托管内存池
                for (int i = 0; i < BufferCount; ++i)
                {
                    IntPtr ptr = Marshal.AllocHGlobal(_bufferSize);
                    _imgBuffers.Add(ptr);
                }

                // 根据行对齐情况选择缓存模式
                if (_lineBytes == LineStride)
                {
                    _useUserBuffer = false;
                    b = b && _device.setProperty((uint)AriPropertyType.AriProp_FrameBufferMode,
                        (uint)AriDef_FrameBufferMode.ARI_FRAMEBUFFER_INTERNEL);
                }
                else
                {
                    _useUserBuffer = true;
                    b = b && _device.setProperty((uint)AriPropertyType.AriProp_FrameBufferMode,
                        (uint)AriDef_FrameBufferMode.ARI_FRAMEBUFFER_USER);

                    if (b)
                    {
                        IntPtr[] addrs = _imgBuffers.ToArray();
                        _device.setUserFrameBuffers(addrs, (ulong)_bufferSize);
                    }
                }

                if (!b)
                {
                    ErrorOccurred?.Invoke(this, "Failed to configure device buffer mode.");
                    return false;
                }

                // 注册采集回调
                RegisterCallback();
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Initialize failed: {ex.Message}");
                return false;
            }
        }

        private void RegisterCallback()
        {
            _callbackImageReady = new VolansEventHandler(
                delegate (uint arg0, uint arg1)
                {
                    if (!_useUserBuffer)
                    {
                        // 内部缓存模式：手动拷贝数据到用户内存池
                        IntPtr ptr = _device.getInternalBufferAddress(arg0);
                        if (ptr == IntPtr.Zero) return;

                        IntPtr src = ptr;
                        IntPtr dst = _imgBuffers[_imgIndex];

                        for (int i = 0; i < ImageHeight; ++i)
                        {
                            CopyMemory(dst, src, _lineBytes);
                            src += _lineBytes;
                            dst += LineStride;
                        }

                        arg0 = (uint)_imgIndex;
                        _imgIndex = (_imgIndex + 1) % _imgBuffers.Count;
                    }
                    else
                    {
                        if (arg0 >= BufferCount) return;
                    }

                    IntPtr originalData = _imgBuffers[(int)arg0];
                    var matType = BitsPerPixel == 8 ? OpenCvSharp.MatType.CV_8UC1 : OpenCvSharp.MatType.CV_8UC3;
                    
                    using (var originalMat = OpenCvSharp.Mat.FromPixelData((int)ImageHeight, (int)ImageWidth, matType, originalData, (int)LineStride))
                    using (var resizedMat = new OpenCvSharp.Mat())
                    {
                        int df = ConfigManager.Config.DownscaleFactor;
                        OpenCvSharp.Cv2.Resize(originalMat, resizedMat, new OpenCvSharp.Size(ImageWidth / df, ImageHeight / df), 0, 0, OpenCvSharp.InterpolationFlags.Area);
                        
                        int newWidth = resizedMat.Width;
                        int newHeight = resizedMat.Height;
                        int newLineBytes = BitsPerPixel == 8 ? newWidth : 3 * newWidth;
                        int newStride = (newLineBytes + 3) / 4 * 4;
                        int totalBytes = newStride * newHeight;
                        byte[] data = new byte[totalBytes];

                        if (newLineBytes == newStride && resizedMat.IsContinuous())
                        {
                            Marshal.Copy(resizedMat.Data, data, 0, totalBytes);
                        }
                        else
                        {
                            for (int i = 0; i < newHeight; i++)
                            {
                                IntPtr srcRow = resizedMat.Data + i * (int)resizedMat.Step();
                                Marshal.Copy(srcRow, data, i * newStride, newLineBytes);
                            }
                        }

                        // 触发帧就绪事件
                        FrameCount++;
                        bool isBroken = arg1 != 0;
                        if (isBroken) BrokenCount++;

                        FrameReady?.Invoke(this, new FrameReadyEventArgs
                        {
                            DataArray = data,
                            BufferIndex = (int)arg0,
                            IsBroken = isBroken,
                            Width = newWidth,
                            Height = newHeight,
                            Stride = newStride,
                            BitsPerPixel = BitsPerPixel
                        });
                    }
                });

            _device.registerEventHandler(
                (uint)AriEventType.AriEvent_FrameReady,
                _callbackImageReady);
        }

        public void StartGrab()
        {
            if (_device == null || IsRunning) return;
            FrameCount = 0;
            BrokenCount = 0;
            _imgIndex = 0;
            IsRunning = true;
            _device.startGrab(0);
        }

        public void StopGrab()
        {
            if (_device == null || !IsRunning) return;
            _device.stopGrab();
            IsRunning = false;
        }

        // ---- 动态硬件属性配置支持 ----
        
        /// <summary>
        /// 获取所有受支持的底层硬件参数
        /// </summary>
        public Dictionary<string, uint> GetAllProperties()
        {
            var dict = new Dictionary<string, uint>();
            if (_device == null) return dict;

            foreach (AriPropertyType prop in Enum.GetValues(typeof(AriPropertyType)))
            {
                uint val = 0;
                if (_device.getProperty((uint)prop, ref val))
                {
                    dict[prop.ToString()] = val;
                }
            }
            return dict;
        }

        /// <summary>
        /// 动态设置硬件参数
        /// </summary>
        public bool SetProperty(string propName, uint value)
        {
            if (_device == null) return false;
            if (Enum.TryParse<AriPropertyType>(propName, out var prop))
            {
                return _device.setProperty((uint)prop, value);
            }
            return false;
        }

        /// <summary>
        /// 将当前硬件参数保存到配置文件
        /// </summary>
        public bool SaveConfiguration(string configPath)
        {
            if (_device == null) return false;
            return _device.saveConfigurationFile(configPath);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 确定性释放：先停止采集
            try { StopGrab(); } catch { }

            // 关闭设备句柄
            try { _device?.closeDevice(); } catch { }
            _device = null;

            // 释放所有非托管内存
            for (int i = 0; i < _imgBuffers.Count; ++i)
            {
                if (_imgBuffers[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_imgBuffers[i]);
                    _imgBuffers[i] = IntPtr.Zero;
                }
            }
            _imgBuffers.Clear();
        }
    }
}
