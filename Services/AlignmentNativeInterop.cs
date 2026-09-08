using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CIS_WebInspector.Models;
using Microsoft.Win32.SafeHandles;
using OpenCvSharp;
using Image = CIS_WebInspector.Services.PatchNativeInterop.Image;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 全局对准/白墨 C ABI 适配层：只复制配置和小型结果，不跨 DLL 传递 Mat 头。
    /// 原图同步借用；输出 Mat 由 C# 分配，C++ 按行步长直接写入，避免整幅结果的额外复制。
    /// </summary>
    internal static class AlignmentNativeInterop
    {
        private const string Dll = "CISVisionCore.dll";
        internal static void ValidateAbi()
        {
            if (cis_alignment_abi_version() != 1 || Marshal.SizeOf(typeof(Image)) != 40 ||
                Marshal.SizeOf(typeof(Anchor)) != 48 || Marshal.SizeOf(typeof(Config)) != 192 ||
                Marshal.SizeOf(typeof(Summary)) != 296 || Marshal.SizeOf(typeof(Mark)) != 40 ||
                Marshal.SizeOf(typeof(Control)) != 96 || Marshal.SizeOf(typeof(Sample)) != 64)
                throw new InvalidOperationException("CISVisionCore.dll 全局对准接口版本/布局不匹配，请重新构建并部署 DLL。");
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Anchor
        {
            public uint Size, Reserved;
            public double CenterX;
            public long GlobalCenterY, SegmentStartGlobalY;
            public double PixelWidth, PixelHeight;
            public static Anchor From(CisQrAnchor a) => new Anchor { Size = 48, CenterX = a.CenterX,
                GlobalCenterY = a.GlobalCenterY, SegmentStartGlobalY = a.SegmentStartGlobalY,
                PixelWidth = a.PixelWidth, PixelHeight = a.PixelHeight };
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Config
        {
            public uint Size;
            public int EnableWhiteInkInspection, EnableSideMarkNonlinearAlignment, SideMarkPairCount;
            public int SideMarkMinValidPerColumn, NonlinearRemapStripeRows;
            public double LayoutDpi, TiffHeightMm, TiffTopCenterYmm, TiffBottomOffsetMm, MarkDiameterMm;
            public double CisRowSpacingMm, QrPhysicalHeightMm, QrPhysicalWidthMm;
            public double InitialSearchMarginMm, ExpandedSearchMarginMm, MinCircularityTiff, MinCircularityCis;
            public double WhiteInkNormalGray, WhiteInkStreakStdDevThreshold;
            public double SideMarkDiameterMm, SheetWidthMm, TiffSideMarkEdgeOffsetMm, CisQrToLeftMarkMm, CisSideMarkSpanMm;
            public double SideMarkInitialSearchMarginMm, SideMarkExpandedSearchMarginMm;

            // 按值生成一次配置快照；线程中不反复读取可变的界面配置对象。
            public static Config From(MarkAlignmentOptions c) => new Config
            {
                Size = 192,
                EnableWhiteInkInspection = c.EnableWhiteInkInspection ? 1 : 0,
                EnableSideMarkNonlinearAlignment = c.EnableSideMarkNonlinearAlignment ? 1 : 0,
                SideMarkPairCount = c.SideMarkPairCount, SideMarkMinValidPerColumn = c.SideMarkMinValidPerColumn,
                NonlinearRemapStripeRows = c.NonlinearRemapStripeRows,
                LayoutDpi = c.LayoutDpi, TiffHeightMm = c.TiffHeightMm, TiffTopCenterYmm = c.TiffTopCenterYmm,
                TiffBottomOffsetMm = c.TiffBottomOffsetMm, MarkDiameterMm = c.MarkDiameterMm,
                CisRowSpacingMm = c.CisRowSpacingMm, QrPhysicalHeightMm = c.QrPhysicalHeightMm, QrPhysicalWidthMm = c.QrPhysicalWidthMm,
                InitialSearchMarginMm = c.InitialSearchMarginMm, ExpandedSearchMarginMm = c.ExpandedSearchMarginMm,
                MinCircularityTiff = c.MinCircularityTiff, MinCircularityCis = c.MinCircularityCis,
                WhiteInkNormalGray = c.WhiteInkNormalGray, WhiteInkStreakStdDevThreshold = c.WhiteInkStreakStdDevThreshold,
                SideMarkDiameterMm = c.SideMarkDiameterMm, SheetWidthMm = c.SheetWidthMm,
                TiffSideMarkEdgeOffsetMm = c.TiffSideMarkEdgeOffsetMm, CisQrToLeftMarkMm = c.CisQrToLeftMarkMm,
                CisSideMarkSpanMm = c.CisSideMarkSpanMm, SideMarkInitialSearchMarginMm = c.SideMarkInitialSearchMarginMm,
                SideMarkExpandedSearchMarginMm = c.SideMarkExpandedSearchMarginMm
            };
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Summary
        {
            public uint Size;
            public int HasTransform, Mode, Quality, Threshold, WhiteStatus, Streaking, StripeRows;
            public uint MarkCount, ControlCount, SampleCount, GridRows;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public double[] Homography;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public double[] Inverse;
            public double InkPercent, MarkMean, MarkVariance, BackgroundMean, Contrast;
            public double DetectionMs, MapMs, RemapMs, LooMedian, LooMaximum;
            public ulong TemporaryBytes;
            public int WhiteX, WhiteY, WhiteWidth, WhiteHeight;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Mark { public int Row, Index; public double TiffX, TiffY, CisX, CisY; }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Control
        {
            public int Row, Column, Flags, Reserved;
            public double ExpectedX, ExpectedY, DetectedTiffX, DetectedTiffY;
            public double CoarseX, CoarseY, DetectedCisX, DetectedCisY, ResidualX, ResidualY;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Sample { public int Index, Detected; public double X, Y, Radius, Mean, Variance, Background, Contrast; }

        /// <summary>
        /// 结果独占原生矩阵与网格。P/Invoke 传 SafeHandle 期间 CLR 会保持句柄有效；
        /// 正常使用仍应 using AlignmentResult，不允许业务线程同时 Warp 和 Dispose 同一结果。
        /// </summary>
        internal sealed class ResultHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal ResultHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { cis_alignment_destroy(handle); return true; }
        }
        internal static void Check(int status, byte[] error = null)
        {
            if (status == 0) return;
            string detail = "";
            if (error != null) { int end = Array.IndexOf(error, (byte)0); detail = Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end); }
            throw new InvalidOperationException("C++ 全局对准/白墨检测 status=" + status + ": " + detail);
        }
        internal static ResultHandle Compute(Mat cis, Mat tiff, CisQrAnchor anchor, MarkAlignmentOptions options, bool whiteOnly)
        {
            ValidateAbi();
            Image source = Image.Borrow(cis), layout = whiteOnly ? default(Image) : Image.Borrow(tiff);
            Anchor a = Anchor.From(anchor); Config c = Config.From(options); var error = new byte[4096];
            IntPtr pointer;
            int status = cis_alignment_compute(ref source, ref layout, ref a, ref c, whiteOnly ? 1 : 0, out pointer, error, (uint)error.Length);
            // 输入 Mat 在整个同步原生调用结束后才能释放；不让 GC 因只剩 Data 指针而提前终结对象。
            GC.KeepAlive(cis); GC.KeepAlive(tiff);
            var result = new ResultHandle(pointer);
            try { Check(status, error); return result; }
            catch { result.Dispose(); throw; }
        }
        internal static Summary ReadSummary(ResultHandle handle)
        {
            var s = new Summary { Size = 296, Homography = new double[9], Inverse = new double[9] };
            Check(cis_alignment_summary(handle, ref s)); return s;
        }
        internal static string ReadLog(ResultHandle result, bool white = false)
        {
            int status = cis_alignment_log(result, white ? 1 : 0, null, 0, out uint required);
            if (status != -3 && status != 0) Check(status);
            var bytes = new byte[checked((int)required)];
            Check(cis_alignment_log(result, white ? 1 : 0, bytes, required, out required));
            return Encoding.UTF8.GetString(bytes, 0, checked((int)required - 1));
        }
        internal static WhiteInkInspectionResult ReadWhite(ResultHandle handle, Summary summary)
        {
            var samples = new Sample[checked((int)summary.SampleCount)];
            Check(cis_alignment_samples(handle, samples, summary.SampleCount));
            var list = new List<WhiteInkMarkSample>(samples.Length);
            foreach (var s in samples) list.Add(new WhiteInkMarkSample { Index = s.Index, Center = new Point2d(s.X, s.Y),
                DisplayRadius = s.Radius, UsedDetectedCenter = s.Detected != 0, MarkMean = s.Mean,
                MarkVariance = s.Variance, BackgroundMean = s.Background, Contrast = s.Contrast });
            return new WhiteInkInspectionResult { Status = (WhiteInkInspectionStatus)summary.WhiteStatus,
                InkLevelPercent = summary.InkPercent, MarkMean = summary.MarkMean, MarkVariance = summary.MarkVariance,
                BackgroundMean = summary.BackgroundMean, Contrast = summary.Contrast, HasStreaking = summary.Streaking != 0,
                SearchRegion = new Rect(summary.WhiteX, summary.WhiteY, summary.WhiteWidth, summary.WhiteHeight),
                Samples = list.AsReadOnly(), Diagnostic = ReadLog(handle, true) };
        }
        internal static AlignmentResult ReadAlignment(ResultHandle handle, Summary summary)
        {
            var marks = new Mark[checked((int)summary.MarkCount)];
            var controls = new Control[checked((int)summary.ControlCount)];
            Check(cis_alignment_marks(handle, marks, summary.MarkCount));
            Check(cis_alignment_controls(handle, controls, summary.ControlCount));
            var globalList = new List<AlignmentGlobalMarkPoint>();
            foreach (var m in marks) globalList.Add(new AlignmentGlobalMarkPoint { RowName = m.Row == 0 ? "Top" : "Bottom", Index = m.Index,
                TiffPoint = new Point2d(m.TiffX, m.TiffY), CisPoint = new Point2d(m.CisX, m.CisY) });
            var controlList = new List<AlignmentControlPoint>();
            double[] gridX = summary.GridRows == 0 ? null : new double[3];
            double[] gridY = summary.GridRows == 0 ? null : new double[checked((int)summary.GridRows)];
            Point2d[,] residuals = summary.GridRows == 0 ? null : new Point2d[checked((int)summary.GridRows), 3];
            foreach (var c in controls)
            {
                var point = new AlignmentControlPoint { RowIndex = c.Row, Column = (AlignmentControlColumn)c.Column,
                    ExpectedTiffPoint = new Point2d(c.ExpectedX, c.ExpectedY), DetectedTiffPoint = new Point2d(c.DetectedTiffX, c.DetectedTiffY),
                    CoarseCisPoint = new Point2d(c.CoarseX, c.CoarseY), DetectedCisPoint = new Point2d(c.DetectedCisX, c.DetectedCisY),
                    Residual = new Point2d(c.ResidualX, c.ResidualY), IsDetected = (c.Flags & 1) != 0,
                    IsInterpolated = (c.Flags & 2) != 0, IsVirtual = (c.Flags & 4) != 0 };
                controlList.Add(point); gridX[c.Column] = c.ExpectedX; gridY[c.Row] = c.ExpectedY; residuals[c.Row, c.Column] = point.Residual;
            }
            // 小型矩阵复制给原公共模型供诊断；实际 Warp 使用 SafeHandle 中的原生矩阵/网格。
            Mat h = null, inverse = null;
            try
            {
                h = new Mat(3, 3, MatType.CV_64FC1); inverse = new Mat(3, 3, MatType.CV_64FC1);
                Marshal.Copy(summary.Homography, 0, h.Data, 9); Marshal.Copy(summary.Inverse, 0, inverse.Data, 9);
                var result = new AlignmentResult(h, inverse, (AlignmentMode)summary.Mode, (AlignmentQualityStatus)summary.Quality,
                    gridX, gridY, residuals, controlList, globalList, summary.StripeRows);
                h = inverse = null; return result;
            }
            finally { h?.Dispose(); inverse?.Dispose(); }
        }
        internal static void Warp(ResultHandle handle, Mat source, Mat target)
        {
            var src = Image.Borrow(source); var dst = Image.Borrow(target); var error = new byte[4096];
            int status = cis_alignment_warp(handle, ref src, ref dst, error, (uint)error.Length);
            GC.KeepAlive(source); GC.KeepAlive(target); Check(status, error);
        }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern uint cis_alignment_abi_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_alignment_compute(ref Image cis, ref Image tiff, ref Anchor anchor, ref Config config, int mode, out IntPtr result, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_alignment_summary(ResultHandle result, ref Summary summary);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_alignment_marks(ResultHandle result, [Out] Mark[] output, uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_alignment_controls(ResultHandle result, [Out] Control[] output, uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_alignment_samples(ResultHandle result, [Out] Sample[] output, uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_alignment_log(ResultHandle result, int kind, [Out] byte[] output, uint capacity, out uint required);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int cis_alignment_warp(ResultHandle result, ref Image cis, ref Image output, [Out] byte[] error, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern void cis_alignment_destroy(IntPtr result);
    }
}
