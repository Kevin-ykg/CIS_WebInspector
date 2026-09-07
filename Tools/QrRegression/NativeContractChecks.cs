using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using OpenCvSharp;

// 独立开发验证入口，不编译进正式程序；直接验证 DLL 的边界，避免只测到 C# 参数检查。
internal static class NativeContractChecks
{
    private const string Dll = "CISVisionCore.dll";
    private static int _checks;
    [StructLayout(LayoutKind.Sequential)]
    private struct Config { public uint Size; public int X, Width, Invert; public IntPtr Scales; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Result { public uint Size, Found; public int X, Y; public double Width, Height; public uint Attempts, TextBytes, StrategyBytes, Reserved; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint cis_qr_abi_version();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int cis_qr_create(string directory, out IntPtr handle, [Out] byte[] error, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int cis_qr_configure(IntPtr handle, ref Config config, [Out] byte[] error, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int cis_qr_initialize(IntPtr handle, [Out] byte[] error, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int cis_qr_detect(IntPtr handle, IntPtr data, ulong bytes, int width, int height, ulong stride, int channels,
        ref Result result, [Out] byte[] text, uint textCapacity, [Out] byte[] strategy, uint strategyCapacity, [Out] byte[] error, uint errorCapacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void cis_qr_destroy(IntPtr handle);

    public static int Run(string models)
    {
        var error = new byte[2048];
        Require(cis_qr_abi_version() == 2 && Marshal.SizeOf<Config>() == 32 && Marshal.SizeOf<Result>() == 48, "ABI and layouts");
        Require(cis_qr_create(null, out IntPtr invalid, error, (uint)error.Length) == -1 && invalid == IntPtr.Zero, "empty model path");
        Require(cis_qr_initialize(IntPtr.Zero, error, (uint)error.Length) == -1, "null context");
        Require(cis_qr_create(models + "\\__missing__", out IntPtr missing, error, (uint)error.Length) == 0, "lazy create");
        try { Require(cis_qr_initialize(missing, error, (uint)error.Length) == -2 && error[0] != 0, "missing model reported"); }
        finally { cis_qr_destroy(missing); }
        Require(cis_qr_create(models, out IntPtr handle, error, (uint)error.Length) == 0, "create");
        try
        {
            var config = new Config { Size = 32, Width = 128, Invert = 1 };
            Require(cis_qr_configure(handle, ref config, error, (uint)error.Length) == 0, "configure defaults");
            config.Size = 1;
            Require(cis_qr_configure(handle, ref config, error, (uint)error.Length) == -1, "reject wrong struct size");
            config.Size = 32; config.Count = 1;
            Require(cis_qr_configure(handle, ref config, error, (uint)error.Length) == -1, "reject null scale pointer");
            // configure 必须复制借来的尺度数组；返回后原缓冲立即改写和解除固定。
            var scales = new[] { 1f, float.NaN, -1f, 1f };
            var scalePin = GCHandle.Alloc(scales, GCHandleType.Pinned);
            try
            {
                config.Scales = scalePin.AddrOfPinnedObject(); config.Count = 4;
                Require(cis_qr_configure(handle, ref config, error, (uint)error.Length) == 0, "scale snapshot");
                scales[0] = 0.25f;
            }
            finally { scalePin.Free(); }
            Require(cis_qr_initialize(handle, error, (uint)error.Length) == 0, "initialize");
            foreach (int channels in new[] { 1, 3, 4 })
            {
                const int width = 128, height = 96;
                int stride = width * channels + 11;
                byte[] input = Enumerable.Repeat((byte)255, stride * height).ToArray();
                byte[] before = (byte[])input.Clone();
                var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
                try
                {
                    var text = new byte[256]; var strategy = new byte[256];
                    Func<ulong, ulong, int, uint, int> detect = (bytes, step, channelCount, resultSize) =>
                    {
                        var result = new Result { Size = resultSize, Found = 1, X = 123 };
                        int status = cis_qr_detect(handle, pin.AddrOfPinnedObject(), bytes, width, height, step, channelCount,
                            ref result, text, 256, strategy, 256, error, (uint)error.Length);
                        if (resultSize == 48) Require(result.Found == 0 && result.X == 0 && text[0] == 0, "reset result on blank/error");
                        if (status == 1) Require(result.Attempts == 1 && error[0] == 0, "copied scale and cleared error");
                        return status;
                    };
                    Require(detect((ulong)input.Length, (ulong)stride, channels, 48) == 1, "padded stride " + channels);
                    Require(detect(1, (ulong)stride, channels, 48) == -1, "short buffer");
                    Require(detect(ulong.MaxValue, ulong.MaxValue, channels, 48) == -1, "overflow guard");
                    Require(detect((ulong)input.Length, 1, channels, 48) == -1, "short stride");
                    Require(detect((ulong)input.Length, (ulong)stride, 2, 48) == -1, "unsupported pixel format");
                    Require(detect((ulong)input.Length, (ulong)stride, channels, 1) == -1, "wrong output layout");
                    Require(input.SequenceEqual(before), "input unchanged " + channels);
                }
                finally { pin.Free(); }
            }
        }
        finally { cis_qr_destroy(handle); }
        cis_qr_destroy(IntPtr.Zero);
        // SafeHandle + 外层锁保护同实例检测/释放；这里不引入生产中的额外并发策略。
        var snapshot = new AppConfig { DownscaleFactor = 1, BaseRoiX = 0, BaseRoiWidth = 128, QrScaleYCandidates = new[] { 1f } };
        using (var detector = new QrCodeDetector(models))
        {
            detector.Configure(snapshot);
            Require(detector.Initialize(), "managed initialize");
            Parallel.For(0, 20, i =>
            {
                var result = detector.Detect(new byte[128 * 96], 128, 96, 128, 8);
                if (result.Found || detector.LastError != null) throw new InvalidOperationException("Concurrent blank detection failed");
            });
            Require(true, "same-context concurrent calls serialized");
            detector.Dispose(); detector.Dispose();
            Require(!detector.Detect(new byte[1], 1, 1, 1, 8).Found && detector.LastError != null, "disposed context fails safely");
        }
        // 记录释放后的私有内存观察值，不把分配器保留内存等同于泄漏，也不据此宣称生产稳定性。
        for (int i = 0; i < 25; i++)
        {
            using (var detector = new QrCodeDetector(models))
            {
                detector.Configure(snapshot);
                Require(detector.Initialize(), "lifecycle " + i);
                Require(!detector.Detect(new byte[128 * 96], 128, 96, 128, 8).Found && detector.LastError == null, "lifecycle detect " + i);
            }
            if (i % 5 == 4) Console.WriteLine($"Lifecycle {i + 1}: private bytes={Process.GetCurrentProcess().PrivateMemorySize64}");
        }
        Console.WriteLine($"PASS {_checks} contract checks"); return 0;
    }
    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); _checks++; }

    public static int CheckImage(string models, string configPath, string imagePath)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));
        var error = new byte[4096];
        Require(cis_qr_create(models, out IntPtr handle, error, 4096) == 0, "create real-image context");
        var scalePin = GCHandle.Alloc(config.QrScaleYCandidates, GCHandleType.Pinned);
        try
        {
            int factor = Math.Max(1, config.DownscaleFactor);
            var settings = new Config { Size = 32, X = config.BaseRoiX / factor, Width = config.BaseRoiWidth / factor,
                Invert = config.QrInvertPolarity ? 1 : 0, Scales = scalePin.AddrOfPinnedObject(), Count = (uint)config.QrScaleYCandidates.Length };
            Require(cis_qr_configure(handle, ref settings, error, 4096) == 0, "configure real-image context");
            using (var original = Cv2.ImRead(imagePath, ImreadModes.Color))
            using (var resized = new Mat())
            {
                Cv2.Resize(original, resized, new Size(original.Width / factor, original.Height / factor), 0, 0, InterpolationFlags.Area);
                Result? expected = null;
                string expectedText = null;
                foreach (int channels in new[] { 3, 1, 4 })
                using (var converted = new Mat())
                {
                    if (channels == 1) Cv2.CvtColor(resized, converted, ColorConversionCodes.BGR2GRAY);
                    else if (channels == 4) Cv2.CvtColor(resized, converted, ColorConversionCodes.BGR2BGRA);
                    else resized.CopyTo(converted);
                    int stride = converted.Width * channels + 13;
                    var bytes = Enumerable.Repeat((byte)79, stride * converted.Height).ToArray();
                    for (int y = 0; y < converted.Height; y++) Marshal.Copy(converted.Ptr(y), bytes, y * stride, converted.Width * channels);
                    var before = (byte[])bytes.Clone();
                    var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    try
                    {
                        var result = new Result { Size = 48 };
                        var text = new byte[16384]; var strategy = new byte[4096];
                        int status = cis_qr_detect(handle, pin.AddrOfPinnedObject(), (ulong)bytes.Length, converted.Width, converted.Height,
                            (ulong)stride, channels, ref result, text, (uint)text.Length, strategy, 4096, error, 4096);
                        Require(status == 0 && result.Found == 1, "real image decoded " + channels);
                        string decoded = Encoding.UTF8.GetString(text, 0, Array.IndexOf(text, (byte)0));
                        if (expected.HasValue)
                            Require(expectedText == decoded && result.X == expected.Value.X && result.Y == expected.Value.Y &&
                                Math.Abs(result.Width - expected.Value.Width) < 1e-6 && Math.Abs(result.Height - expected.Value.Height) < 1e-6, "pixel format equivalence");
                        else { expected = result; expectedText = decoded; }
                        result.Size = 48;
                        status = cis_qr_detect(handle, pin.AddrOfPinnedObject(), (ulong)bytes.Length, converted.Width, converted.Height,
                            (ulong)stride, channels, ref result, text, 1, strategy, 1, error, 4096);
                        Require(status == -3 && result.Found == 0 && result.TextBytes > 1 && result.StrategyBytes > 1 &&
                            text[0] == 0 && strategy[0] == 0 && error[0] != 0, "short output never returns truncated success");
                        Require(bytes.SequenceEqual(before), "real pixels and padding unchanged");
                        Console.WriteLine($"Channels={channels}, decoded={decoded}, center={expected.Value.X},{expected.Value.Y}");
                    }
                    finally { pin.Free(); }
                }
            }
        }
        finally { scalePin.Free(); cis_qr_destroy(handle); }
        Console.WriteLine($"PASS {_checks} real-image contract checks"); return 0;
    }
}
