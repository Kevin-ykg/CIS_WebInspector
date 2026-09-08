using System;
using System.Runtime.InteropServices;
using System.Text;
using CIS_WebInspector.Models;
using Microsoft.Win32.SafeHandles;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 零件配准/缺陷 C ABI。只传普通结构体和像素指针，不跨运行库共享 cv::Mat。
    /// 每件一次 Detect，结果句柄持有输出，读取结果不会再次求解矩阵或再次检测。
    /// </summary>
    internal static class PatchNativeInterop
    {
        private const string Dll = "CISVisionCore.dll";
        internal static void ValidateAbi()
        {
            if (cis_patch_abi_version() != 1 || Marshal.SizeOf(typeof(Image)) != 40 ||
                Marshal.SizeOf(typeof(Config)) != 112 || Marshal.SizeOf(typeof(Defect)) != 64 ||
                Marshal.SizeOf(typeof(Summary)) != 80 || Marshal.SizeOf(typeof(CacheStats)) != 48)
                throw new InvalidOperationException("CISVisionCore.dll 零件检测接口版本/布局不匹配，请重新构建并部署 DLL。");
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Image
        {
            public uint Size;
            public int Width, Height, Channels;
            public ulong Stride, Bytes;
            public IntPtr Pixels;

            /// <summary>
            /// ROI 的 stride 可以大于有效行宽。仅声明最后一行有效像素之前的长度，
            /// 不能把父图行尾误认为当前 ROI 仍可访问的整行缓冲区。
            /// </summary>
            public static Image Borrow(Mat mat)
            {
                if (mat == null || mat.Empty() || mat.Depth() != MatType.CV_8U)
                    throw new ArgumentException("零件输入必须是非空的 8 位图像。");
                ulong stride = checked((ulong)mat.Step());
                return new Image { Size = 40, Width = mat.Width, Height = mat.Height, Channels = mat.Channels(),
                    Stride = stride, Bytes = checked(stride * (ulong)(mat.Height - 1) + (ulong)(mat.Width * mat.Channels())),
                    Pixels = mat.Data };
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Config
        {
            public uint Size;
            public int Alignment, FineLine, MinWidth, AlphaThreshold, CisThreshold, Outputs, Reserved;
            public double Scale, Dpi, InnerTolerance, OuterTolerance, OuterExclusion, InnerExclusion;
            public double InnerArea, OuterArea, FineLength, FineWidth;
            public static Config From(AppConfig value, int cisThreshold, int outputs)
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                // 配置按值复制。正在运行的检测不借用设置窗口上的可变对象。
                return new Config { Size = 112, Alignment = value.EnableSiftLocalAlign ? 1 : 0,
                    FineLine = value.EnableFineLineBreakDetection ? 1 : 0, MinWidth = value.DefectMinScaledWidth,
                    AlphaThreshold = value.DefectAlphaBinaryThresh, CisThreshold = cisThreshold, Outputs = outputs,
                    Scale = value.DefectDetectScale, Dpi = value.LayoutDpi,
                    InnerTolerance = value.DefectToleranceInner, OuterTolerance = value.DefectToleranceOuter,
                    OuterExclusion = value.DefectEdgeExclusionThick, InnerExclusion = value.DefectEdgeExclusionSmall,
                    InnerArea = value.DefectAreaThreshInner, OuterArea = value.DefectAreaThreshOuter,
                    FineLength = value.FineLineMinBreakLengthMm, FineWidth = value.FineLineMaxWidthMm };
            }
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Defect
        {
            public int Kind, X, Y, Width, Height, WorkX, WorkY, WorkWidth, WorkHeight, Reserved;
            public double WidthMm, HeightMm, AreaMm2;
            public Rect OriginalRect => new Rect(X, Y, Width, Height);
            public Rect WorkRect => new Rect(WorkX, WorkY, WorkWidth, WorkHeight);
            public DefectGeometryMeasurement Measurement => new DefectGeometryMeasurement
                { WidthMm = WidthMm, HeightMm = HeightMm, AreaMm2 = AreaMm2 };
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Summary
        {
            public uint Size, DefectCount;
            public int Pass, InnerCount, OuterCount, FineCount;
            public double MaxInner, MaxOuter, MaxLength, MaxWidth, Scale, AlignmentMs, DetectionMs;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct CacheStats
        {
            public uint Size, Entries;
            public ulong Hits, Misses, Comparisons;
            public double ComparisonMs, QuickKeyMs;
        }

        /// <summary>每种原生资源都有独立 SafeHandle；由创建它的 DLL 执行释放。</summary>
        internal sealed class CacheHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal CacheHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { cis_patch_cache_destroy(handle); return true; }
        }
        internal sealed class WorkerHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal WorkerHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { cis_patch_worker_destroy(handle); return true; }
        }
        internal sealed class ResultHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal ResultHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { cis_patch_result_destroy(handle); return true; }
        }
        internal static void Check(int status, byte[] error)
        {
            if (status == 0) return;
            int end = Array.IndexOf(error, (byte)0);
            throw new InvalidOperationException("C++ 零件检测 status=" + status + ": " +
                Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end));
        }
        internal static string ReadLog(ResultHandle handle)
        {
            int status = cis_patch_result_log(handle, null, 0, out uint required);
            if (status != -3 && status != 0) throw new InvalidOperationException("读取原生配准日志长度失败。");
            var text = new byte[checked((int)required)];
            if (cis_patch_result_log(handle, text, required, out required) != 0)
                throw new InvalidOperationException("读取原生配准日志失败。");
            return Encoding.UTF8.GetString(text, 0, checked((int)required - 1));
        }
        internal static Mat ReadImage(ResultHandle result, int kind, byte[] error)
        {
            var info = new Image { Size = 40 };
            Check(cis_patch_result_image(result, kind, ref info, error, (uint)error.Length), error);
            if (info.Width <= 0 || info.Height <= 0) throw new InvalidOperationException("原生未生成所请求的诊断图。");
            var image = new Mat(info.Height, info.Width, MatType.CV_8UC(info.Channels));
            try
            {
                // C# Mat 拥有输出缓冲区。复制后即使结果句柄释放，保存/UI 使用仍然有效。
                Image target = Image.Borrow(image);
                Check(cis_patch_result_copy_image(result, kind, target.Pixels, target.Bytes, target.Stride,
                    error, (uint)error.Length), error);
                return image;
            }
            catch { image.Dispose(); throw; }
        }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern uint cis_patch_abi_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_patch_cache_create(out IntPtr handle, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_patch_cache_stats(CacheHandle handle, ref CacheStats stats, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern void cis_patch_cache_destroy(IntPtr handle);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_patch_worker_create(CacheHandle cache, out IntPtr handle, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern void cis_patch_worker_destroy(IntPtr handle);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_patch_detect(WorkerHandle worker, ref Image alpha, ref Image cis, ref Config config, out IntPtr result, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_patch_result_summary(ResultHandle result, ref Summary summary, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_patch_result_defects(ResultHandle result, [Out] Defect[] defects, uint count, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern int cis_patch_result_log(ResultHandle result, [Out] byte[] text, uint capacity, out uint required);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern int cis_patch_result_image(ResultHandle result, int kind, ref Image info, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern int cis_patch_result_copy_image(ResultHandle result, int kind, IntPtr pixels, ulong bytes, ulong stride, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern void cis_patch_result_destroy(IntPtr result);
    }
}
