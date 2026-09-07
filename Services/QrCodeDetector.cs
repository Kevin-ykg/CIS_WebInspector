using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CIS_WebInspector.Models;
using Microsoft.Win32.SafeHandles;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// C++ 二维码算法的唯一托管入口，保持 ImageStitcher 原有调用协议。
    /// 灰度/ROI/极性/尺度/透视/失焦恢复全部在 CISVisionCore.dll 内执行；
    /// 本类只负责配置快照、输入有效期、错误信息及结果转换。
    /// </summary>
    public sealed class QrCodeDetector : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _modelDirectory;
        private readonly byte[] _text = new byte[16384], _strategy = new byte[4096], _error = new byte[4096];
        private QrHandle _handle;
        private float[] _scales = Array.Empty<float>();
        private bool _invert, _needsConfiguration = true, _disposed;
        public int RoiX { get; private set; }
        public int RoiWidth { get; private set; }
        public string LastError { get; private set; }
        public string LastDecodeStrategy { get; private set; }
        public int LastDecodeAttemptCount { get; private set; }

        public QrCodeDetector() : this(null) { }
        public QrCodeDetector(string modelDirectory)
        {
            _modelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "WeChatQRCode")
                : Path.GetFullPath(modelDirectory);
            Configure(new AppConfig());
        }
        /// <summary>按会话复制参数；同一把锁串行化配置、检测和释放，避免悬空原生句柄。</summary>
        public void Configure(AppConfig snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(QrCodeDetector));
                int factor = Math.Max(1, snapshot.DownscaleFactor);
                RoiX = snapshot.BaseRoiX / factor; RoiWidth = snapshot.BaseRoiWidth / factor;
                _invert = snapshot.QrInvertPolarity;
                _scales = snapshot.QrScaleYCandidates == null ? Array.Empty<float>() : (float[])snapshot.QrScaleYCandidates.Clone();
                _needsConfiguration = true;
            }
        }
        /// <summary>加载并预热模型。库缺失、ABI 或模型错误明确失败，不静默改用另一算法。</summary>
        public bool Initialize()
        {
            lock (_sync)
            {
                ResetDiagnostics();
                if (_disposed) { LastError = "二维码检测器已经释放。"; return false; }
                try
                {
                    EnsureConfigured();
                    Check(Native.cis_qr_initialize(_handle, _error, (uint)_error.Length));
                    return true;
                }
                catch (Exception ex) { LastError = "C++ 二维码初始化失败：" + ex.Message; return false; }
            }
        }
        /// <summary>
        /// 每帧一次 P/Invoke，同步借用 Gray8/BGR24/BGRA32 像素。
        /// 整个检测期固定数组，返回即解除固定；DLL 不保留、不修改输入像素。
        /// </summary>
        public QrDetectionResult Detect(byte[] data, int width, int height, int stride, int bitsPerPixel)
        {
            lock (_sync)
            {
                ResetDiagnostics();
                if (_disposed) { LastError = "二维码检测器已经释放。"; return QrDetectionResult.NotFound; }
                int channels = bitsPerPixel == 8 ? 1 : bitsPerPixel == 24 ? 3 : bitsPerPixel == 32 ? 4 : 0;
                if (width <= 0 || height <= 0 || stride <= 0 || channels == 0 || stride < (long)width * channels)
                { LastError = $"无效图像参数：{width}x{height}, stride={stride}, bpp={bitsPerPixel}。"; return QrDetectionResult.NotFound; }
                long required = (long)stride * height;
                if (data == null || data.LongLength < required)
                { LastError = $"图像缓冲区不足：需要 {required} 字节，实际 {data?.LongLength ?? 0} 字节。"; return QrDetectionResult.NotFound; }
                try
                {
                    EnsureConfigured();
                    var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        var result = new NativeResult { Size = 48 };
                        int status = Native.cis_qr_detect(_handle, pin.AddrOfPinnedObject(), (ulong)data.LongLength,
                            width, height, (ulong)stride, channels, ref result,
                            _text, (uint)_text.Length, _strategy, (uint)_strategy.Length, _error, (uint)_error.Length);
                        LastDecodeAttemptCount = checked((int)result.Attempts);
                        if (status == 1) return QrDetectionResult.NotFound;
                        Check(status);
                        if (result.Found == 0) return QrDetectionResult.NotFound;
                        // 使用 DLL 返回的字节数，而非扫描第一个 NUL；不截断合法载荷中的嵌入零字符。
                        LastDecodeStrategy = ReadUtf8Result(_strategy, result.StrategyBytes);
                        return new QrDetectionResult { Found = true, DecodedText = ReadUtf8Result(_text, result.TextBytes),
                            CenterX = result.X, CenterY = result.Y, PixelWidth = result.Width, PixelHeight = result.Height };
                    }
                    finally { pin.Free(); }
                }
                catch (Exception ex) { LastError = "C++ 二维码检测失败：" + ex.Message; return QrDetectionResult.NotFound; }
            }
        }
        private void EnsureConfigured()
        {
            if (_handle == null)
            {
                if (Native.cis_qr_abi_version() != 2)
                    throw new InvalidOperationException("CISVisionCore.dll 接口版本不匹配，请部署本版本生成的 DLL。");
                Check(Native.cis_qr_create(_modelDirectory, out IntPtr pointer, _error, (uint)_error.Length));
                if (pointer == IntPtr.Zero) throw new InvalidOperationException("原生实例为空。");
                _handle = new QrHandle(pointer); _needsConfiguration = true;
            }
            if (!_needsConfiguration) return;
            var pin = GCHandle.Alloc(_scales, GCHandleType.Pinned);
            try
            {
                var config = new NativeConfig { Size = 32, X = RoiX, Width = RoiWidth, Invert = _invert ? 1 : 0,
                    Scales = _scales.Length == 0 ? IntPtr.Zero : pin.AddrOfPinnedObject(), Count = (uint)_scales.Length };
                Check(Native.cis_qr_configure(_handle, ref config, _error, (uint)_error.Length));
                _needsConfiguration = false;
            }
            finally { pin.Free(); } // C++ 已复制尺度数组，解除固定不会留下悬空指针。
        }
        private void ResetDiagnostics() { LastError = null; LastDecodeStrategy = null; LastDecodeAttemptCount = 0; }
        private void Check(int status)
        { if (status != 0) throw new InvalidOperationException($"Native status={status}: {ReadUtf8(_error)}"); }
        private static string ReadUtf8(byte[] bytes)
        {
            int count = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, count < 0 ? bytes.Length : count);
        }
        private static string ReadUtf8Result(byte[] bytes, uint lengthIncludingNull)
        {
            if (lengthIncludingNull == 0 || lengthIncludingNull > bytes.Length || bytes[lengthIncludingNull - 1] != 0)
                throw new InvalidOperationException("原生返回的文本长度无效。");
            return Encoding.UTF8.GetString(bytes, 0, checked((int)lengthIncludingNull - 1));
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _handle?.Dispose(); _handle = null; _disposed = true;
            }
        }
        private sealed class QrHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal QrHandle(IntPtr pointer) : base(true) { SetHandle(pointer); }
            protected override bool ReleaseHandle() { Native.cis_qr_destroy(handle); return true; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeConfig
        { public uint Size; public int X, Width, Invert; public IntPtr Scales; public uint Count, Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeResult
        { public uint Size, Found; public int X, Y; public double Width, Height; public uint Attempts, TextBytes, StrategyBytes, Reserved; }
        private static class Native
        {
            private const string Dll = "CISVisionCore.dll";
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern uint cis_qr_abi_version();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
            internal static extern int cis_qr_create(string directory, out IntPtr handle, [Out] byte[] error, uint capacity);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern int cis_qr_configure(QrHandle handle, ref NativeConfig config, [Out] byte[] error, uint capacity);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern int cis_qr_initialize(QrHandle handle, [Out] byte[] error, uint capacity);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern int cis_qr_detect(QrHandle handle, IntPtr data, ulong bytes, int width, int height, ulong stride, int channels,
                ref NativeResult result, [Out] byte[] text, uint textCapacity, [Out] byte[] strategy, uint strategyCapacity, [Out] byte[] error, uint errorCapacity);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            internal static extern void cis_qr_destroy(IntPtr handle);
        }
    }
}
