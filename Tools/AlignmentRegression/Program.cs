using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using OpenCvSharp;
using Old = AlignmentRegression.Legacy.ImageAligner;
using Native = CIS_WebInspector.Services.ImageAligner;

namespace AlignmentRegression
{
    // 仅用于迁移验证，不进入生产项目。Legacy 逐文件冻结，避免对照组被新实现间接替换。
    internal static class Program
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        private static readonly List<object> Results = new List<object>();
        private static readonly List<string> Failures = new List<string>();
        private static string Output;
        private static int Checks;
        private static int RepeatCount;
        private static void Check(bool condition, string text) { Checks++; if (!condition) { Failures.Add(text); Console.WriteLine("FAIL " + text); } }
        private static void Near(double a, double b, string text, double tolerance = 1e-7) => Check((double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a-b) <= tolerance, text + " old=" + a + " new=" + b);
        private static T Convert<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        private static Legacy.CisQrAnchor OldAnchor(CisQrAnchor a) => Convert<Legacy.CisQrAnchor>(a);
        private static Legacy.MarkAlignmentOptions OldOptions(MarkAlignmentOptions o) => Convert<Legacy.MarkAlignmentOptions>(o);

        private static int Main(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(args[0]);
                string Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i+1] : null; }
                Output = Path.Combine(root, "obj", "AlignmentNativeValidation", Option("--output") ?? "default"); Directory.CreateDirectory(Output);
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path.Combine(root, "bin/x64/Debug/net48/app_config.json")));
                if (Option("--layout-log") != null) config.DebugLogPath = Option("--layout-log");
                if (Option("--tiff-dir") != null) config.TiffImageDir = Option("--tiff-dir");
                if (Option("--row-spacing") != null) config.MarkCisRowSpacingMm = double.Parse(Option("--row-spacing"));
                if (Option("--layout-height") != null) config.MarkTiffHeightMm = double.Parse(Option("--layout-height"));
                File.WriteAllText(Path.Combine(Output,"effective-config.json"),JsonSerializer.Serialize(config,Json));
                AlignmentNativeInterop.ValidateAbi();
                RepeatCount = Option("--repeat") == null ? 0 : int.Parse(Option("--repeat"));
                Contracts();
                if (Option("--frames") != null) Frames(Option("--frames"),config,Array.IndexOf(args,"--white-only")>=0);
                else Synthetic();
                File.WriteAllText(Path.Combine(Output,"summary.json"),JsonSerializer.Serialize(new { Checks, Failures, Results },Json));
                Console.WriteLine("CHECKS " + Checks + ", FAILURES " + Failures.Count + ", OUTPUT " + Output);
                return Failures.Count == 0 ? 0 : 1;
            }
            catch(Exception e) {Console.WriteLine(e);return 2;}
        }
        private static void WhiteEqual(Legacy.WhiteInkInspectionResult a,WhiteInkInspectionResult b,string name)
        {
            Check((int)a.Status==(int)b.Status,name+" status");Check(a.HasStreaking==b.HasStreaking,name+" streak");Check(a.SearchRegion==b.SearchRegion,name+" ROI");
            Near(a.InkLevelPercent,b.InkLevelPercent,name+" level");Near(a.MarkMean,b.MarkMean,name+" mean");Near(a.MarkVariance,b.MarkVariance,name+" variance");
            Near(a.BackgroundMean,b.BackgroundMean,name+" background");Near(a.Contrast,b.Contrast,name+" contrast");Check(a.Samples.Count==b.Samples.Count,name+" samples");
            for(int i=0;i<Math.Min(a.Samples.Count,b.Samples.Count);i++)
            {
                var x=a.Samples[i];var y=b.Samples[i];Near(x.Center.X,y.Center.X,name+" centerX");Near(x.Center.Y,y.Center.Y,name+" centerY");
                Check(x.UsedDetectedCenter==y.UsedDetectedCenter,name+" measured/predicted");Near(x.MarkMean,y.MarkMean,name+" sample mean");Near(x.MarkVariance,y.MarkVariance,name+" sample variance");
                Near(x.BackgroundMean,y.BackgroundMean,name+" sample background");Near(x.Contrast,y.Contrast,name+" sample contrast");Near(x.DisplayRadius,y.DisplayRadius,name+" sample radius");
            }
        }
        private static void CompareWhite(string name,Mat cis,CisQrAnchor anchor,MarkAlignmentOptions options)
        {
            int before=Failures.Count;var watch=Stopwatch.StartNew();var a=Old.InspectBottomWhiteInk(cis,OldAnchor(anchor),OldOptions(options),out string ad);double oldMs=watch.Elapsed.TotalMilliseconds;
            watch.Restart();var b=Native.InspectBottomWhiteInk(cis,anchor,options,out string bd);double newMs=watch.Elapsed.TotalMilliseconds;
            WhiteEqual(a,b,name);Results.Add(new {Name=name,WhiteOnly=true,Equal=Failures.Count==before,OldMs=oldMs,NewMs=newMs,OldDiagnostic=ad,NewDiagnostic=bd});
            byte[] oldPreview=Old.CreateWhiteInkInspectionPreview(cis,a),newPreview=Native.CreateWhiteInkInspectionPreview(cis,b);
            Check(oldPreview==null&&newPreview==null||oldPreview!=null&&newPreview!=null&&oldPreview.SequenceEqual(newPreview),name+" preview bytes");
            if(name.StartsWith("real")&&newPreview!=null) File.WriteAllBytes(Path.Combine(Output,"WhiteInk_BottomMarks_Preview.jpg"),newPreview);
            Console.WriteLine(name+" "+b.Status+" samples="+b.Samples.Count+" old="+oldMs.ToString("F1")+"ms new="+newMs.ToString("F1")+"ms");
        }
        private static void Compare(string name,Mat cis,Mat tiff,CisQrAnchor anchor,MarkAlignmentOptions options)
        {
            int before=Failures.Count;using(var original=cis.Clone())
            {
                var watch=Stopwatch.StartNew();
                using(var a=Old.ComputeTransform(cis,tiff,OldAnchor(anchor),OldOptions(options),out int at,out string ad,out var aw))
                {
                    double oldMs=watch.Elapsed.TotalMilliseconds;watch.Restart();
                    using(var b=Native.ComputeTransform(cis,tiff,anchor,options,out int bt,out string bd,out var bw))
                    {
                        double newMs=watch.Elapsed.TotalMilliseconds;Check((a==null)==(b==null),name+" transform availability");Check(at==bt,name+" threshold");WhiteEqual(aw,bw,name+" white");
                        double warpDifference=-1,oldWarpMs=0,newWarpMs=0;
                        if(a!=null&&b!=null)
                        {
                            Check((int)a.Mode==(int)b.Mode,name+" mode");Check((int)a.QualityStatus==(int)b.QualityStatus,name+" quality");
                            Near(Cv2.Norm(a.GlobalTransform,b.GlobalTransform,NormTypes.INF),0,name+" H",1e-8);
                            Check(a.GlobalMarkPoints.Count==b.GlobalMarkPoints.Count,name+" mark count");Check(a.ControlPoints.Count==b.ControlPoints.Count,name+" control count");
                            for(int i=0;i<Math.Min(a.GlobalMarkPoints.Count,b.GlobalMarkPoints.Count);i++)
                            {var x=a.GlobalMarkPoints[i];var y=b.GlobalMarkPoints[i];Check(x.RowName==y.RowName&&x.Index==y.Index,name+" numbering");Near(x.CisPoint.X,y.CisPoint.X,name+" markX");Near(x.CisPoint.Y,y.CisPoint.Y,name+" markY");Near(x.TiffPoint.X,y.TiffPoint.X,name+" tiffX");Near(x.TiffPoint.Y,y.TiffPoint.Y,name+" tiffY");}
                            for(int i=0;i<Math.Min(a.ControlPoints.Count,b.ControlPoints.Count);i++)
                            {
                                var x=a.ControlPoints[i];var y=b.ControlPoints[i];Check(x.RowIndex==y.RowIndex&&(int)x.Column==(int)y.Column&&x.IsDetected==y.IsDetected&&x.IsInterpolated==y.IsInterpolated&&x.IsVirtual==y.IsVirtual,name+" grid flags");
                                Near(x.ExpectedTiffPoint.X,y.ExpectedTiffPoint.X,name+" grid targetX");Near(x.ExpectedTiffPoint.Y,y.ExpectedTiffPoint.Y,name+" grid targetY");
                                Near(x.DetectedTiffPoint.X,y.DetectedTiffPoint.X,name+" grid tiffX");Near(x.DetectedTiffPoint.Y,y.DetectedTiffPoint.Y,name+" grid tiffY");
                                Near(x.DetectedCisPoint.X,y.DetectedCisPoint.X,name+" grid sourceX");Near(x.DetectedCisPoint.Y,y.DetectedCisPoint.Y,name+" grid sourceY");
                                Near(x.Residual.X,y.Residual.X,name+" residualX");Near(x.Residual.Y,y.Residual.Y,name+" residualY");
                            }
                            Near(a.LeaveOneOutMedianMm,b.LeaveOneOutMedianMm,name+" LOOmedian");Near(a.LeaveOneOutMaximumMm,b.LeaveOneOutMaximumMm,name+" LOOmax");
                            watch.Restart();using(var wa=Old.WarpToTiffSpace(cis,a,tiff.Size()))
                            {
                                oldWarpMs=watch.Elapsed.TotalMilliseconds;watch.Restart();using(var wb=Native.WarpToTiffSpace(cis,b,tiff.Size()))
                                {newWarpMs=watch.Elapsed.TotalMilliseconds;warpDifference=Cv2.Norm(wa,wb,NormTypes.INF);Near(warpDifference,0,name+" warp pixels",0);}
                            }
                            if(name.StartsWith("real"))
                            {
                                var oldPaths=Old.SaveAlignmentMarkPreviews(cis,tiff,a,Path.Combine(Output,name,"legacy"));
                                var newPaths=Native.SaveAlignmentMarkPreviews(cis,tiff,b,Path.Combine(Output,name,"native"));
                                for(int i=0;i<oldPaths.Count;i++) Check(File.ReadAllBytes(oldPaths[i]).SequenceEqual(File.ReadAllBytes(newPaths[i])),name+" Mark preview bytes "+i);
                            }
                        }
                        Near(Cv2.Norm(cis,original,NormTypes.INF),0,name+" input immutable",0);
                        Results.Add(new {Name=name,Equal=Failures.Count==before,OldMs=oldMs,NewMs=newMs,OldWarpMs=oldWarpMs,NewWarpMs=newWarpMs,WarpMaxPixelDifference=warpDifference,
                            OldDiagnostic=ad,NewDiagnostic=bd,Mode=b?.Mode.ToString(),WhiteStatus=bw.Status.ToString()});
                        Console.WriteLine(name+" mode="+b?.Mode+" old="+oldMs.ToString("F1")+"ms new="+newMs.ToString("F1")+"ms warpMax="+warpDifference);
                    }
                }
            }
        }
        private static void Synthetic()
        {
            var c=new AppConfig();c.LayoutDpi=25.4;c.MarkTiffHeightMm=1000;c.MarkTiffTopCenterYmm=50;c.MarkTiffBottomOffsetMm=30;c.MarkDiameterMm=20;
            c.MarkCisRowSpacingMm=920;c.MarkQrPhysicalHeightMm=c.MarkQrPhysicalWidthMm=60;c.EnableWhiteInkInspection=true;c.WhiteInkNormalGray=210;c.WhiteInkStreakStdDevThreshold=12;
            // 合成圆只有 20px 直径，栅格圆度低于真实 236px Mark；仅夹具使用 0.75，不改生产参数。
            c.MinCircularityTiff=c.MinCircularityCis=.75;
            var options=MarkAlignmentOptions.FromConfig(c);options.NonlinearRemapStripeRows=73;
            var anchor=new CisQrAnchor{CenterX=44,GlobalCenterY=10974,SegmentStartGlobalY=10000,PixelWidth=60,PixelHeight=60};
            using(var tiff=new Mat(1010,600,MatType.CV_8UC3,Scalar.All(255)))
            using(var cis=new Mat(1020,620,MatType.CV_8UC1,Scalar.All(40)))
            {
                int[] xs={90,160,190,285,410,440,535};
                foreach(int y in new[]{50,970}) foreach(int x in xs) {Cv2.Circle(tiff,new Point(x,y),10,Scalar.All(0),-1);Cv2.Circle(cis,new Point(x+5,y+4),10,Scalar.All(210),-1);}
                for(int i=1;i<=9;i++) foreach(double x in new[]{2.5,583.5})
                {int y=50+i*92;Cv2.Circle(tiff,new Point((int)Math.Round(x),y),2,Scalar.All(0),-1);Cv2.Circle(cis,new Point((int)Math.Round(x+5),y+4),2,Scalar.All(210),-1);}
                options.EnableSideMarkNonlinearAlignment=false;Compare("synthetic_global",cis,tiff,anchor,options);
                options.EnableSideMarkNonlinearAlignment=true;Compare("synthetic_side",cis,tiff,anchor,options);
                using(var missing=cis.Clone()) {Cv2.Rectangle(missing,new Rect(0,410,20,22),Scalar.All(40),-1);Compare("synthetic_isolated_side",missing,tiff,anchor,options);}
                using(var missing=cis.Clone()) {Cv2.Rectangle(missing,new Rect(0,315,20,125),Scalar.All(40),-1);Compare("synthetic_consecutive_side",missing,tiff,anchor,options);}
                using(var tilt=Mat.Eye(2,3,MatType.CV_64FC1).ToMat())using(var tilted=new Mat())
                {tilt.Set(1,0,.02);tilt.Set(1,2,-6.0);Cv2.WarpAffine(cis,tilted,tilt,cis.Size(),InterpolationFlags.Linear,BorderTypes.Constant,Scalar.All(40));Compare("synthetic_tilt",tilted,tiff,anchor,options);}
                CompareWhite("synthetic_normal",cis,anchor,options);
                // 参数、缺点与非连续 ROI，不更改正式 app_config.json。
                options.SideMarkMinValidPerColumn=10;Compare("synthetic_side_invalid",cis,tiff,anchor,options);options.SideMarkMinValidPerColumn=7;
                using(var missing=cis.Clone()) {Cv2.Rectangle(missing,new Rect(140,30,70,45),Scalar.All(40),-1);Compare("synthetic_missing_top",missing,tiff,anchor,options);}
                using(var blank=new Mat(cis.Size(),cis.Type(),Scalar.All(40))) {Compare("synthetic_blank",blank,tiff,anchor,options);CompareWhite("synthetic_blank_white",blank,anchor,options);}
                using(var parent=new Mat(cis.Height+20,cis.Width+20,MatType.CV_8UC3))using(var roi=new Mat(parent,new Rect(10,10,cis.Width,cis.Height)))
                {Cv2.CvtColor(cis,roi,ColorConversionCodes.GRAY2BGR);Compare("synthetic_strided",roi,tiff,anchor,options);}
                foreach(int value in new[]{180,160,140,105,70,10})
                using(var shortage=cis.Clone())
                {foreach(int x in xs) Cv2.Circle(shortage,new Point(x+5,974),10,Scalar.All(value),-1);CompareWhite("white_level_"+value,shortage,anchor,options);}
                using(var streak=cis.Clone())
                {foreach(int x in xs) for(int y=970;y<=978;y+=2) Cv2.Line(streak,new Point(x,y),new Point(x+10,y),Scalar.All(50),1);CompareWhite("white_streak",streak,anchor,options);}
                using(var streak=cis.Clone())
                {foreach(int x in xs) for(int y=970;y<=978;y+=2) Cv2.Line(streak,new Point(x,y),new Point(x+10,y),Scalar.All(180),1);CompareWhite("white_streak_only",streak,anchor,options);}
                options.EnableWhiteInkInspection=false;CompareWhite("white_disabled",cis,anchor,options);
                options.EnableWhiteInkInspection=true;anchor.GlobalCenterY=9999;Compare("invalid_anchor",cis,tiff,anchor,options);CompareWhite("white_invalid_anchor",cis,anchor,options);
                anchor.GlobalCenterY=10974;options.MinCircularityTiff=double.NaN;Compare("invalid_nan",cis,tiff,anchor,options);
            }
        }
        private static unsafe void Frames(string path,AppConfig config,bool whiteOnly)
        {
            StitchedImageResult segment=null;
            using(var stitcher=new ImageStitcher())
            {
                stitcher.StitchCompleted+=(sender,value)=>{if(segment==null) segment=value;};bool initialized=false;
                foreach(string file in Directory.GetFiles(path).Where(x=>x.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)).OrderBy(x=>x,StringComparer.Ordinal))
                using(var image=Cv2.ImRead(file,ImreadModes.Color))
                {
                    if(!initialized) {stitcher.Configure(image.Width,image.Height,(int)image.Step(),24,config);initialized=true;}
                    var bytes=new byte[checked((int)(image.Step()*image.Height))];Marshal.Copy(image.Data,bytes,0,bytes.Length);stitcher.ProcessOwnedFrame(bytes,image.Width,image.Height,(int)image.Step(),24);
                    if(segment!=null) break;
                }
            }
            if(segment==null) throw new Exception("图库未产生完整拼接段。");
            Console.WriteLine("STITCHED "+segment.EndQrText+" "+segment.Width+"x"+segment.Height);
            var anchor=new CisQrAnchor{CenterX=segment.EndQrCenterX,GlobalCenterY=segment.EndQrGlobalY,SegmentStartGlobalY=segment.SegmentStartGlobalY,PixelWidth=segment.EndQrPixelWidth,PixelHeight=segment.EndQrPixelHeight};
            var options=MarkAlignmentOptions.FromConfig(config);options.EnableWhiteInkInspection=true;
            using(var cis=new Mat(segment.Height,segment.Width,MatType.CV_8UC3))
            {
                Marshal.Copy(segment.Data,0,cis.Data,segment.Data.Length);CompareWhite("real_white",cis,anchor,options);if(whiteOnly) return;
                var layout=DebugLogParser.ParseForQrCode(config.DebugLogPath,segment.EndQrText,config.TiffImageDir);
                if(layout==null) throw new Exception("缺少测试排版信息："+segment.EndQrText);
                using(var raw=Cv2.ImRead(layout.TiffFullPath,ImreadModes.Unchanged))using(var tiff=new Mat(raw.Height,raw.Width,MatType.CV_8UC3))
                {
                    if(raw.Channels()!=4) throw new Exception("测试 TIFF 需包含 Alpha");
                    for(int y=0;y<raw.Height;y++) {byte* s=(byte*)raw.Ptr(y),d=(byte*)tiff.Ptr(y);for(int x=0;x<raw.Width;x++,s+=4,d+=3) {float a=s[3]*(1f/255f),inv=1f-a;for(int k=0;k<3;k++)d[k]=s[3]==255?s[k]:s[3]==0?(byte)255:(byte)(s[k]*a+255f*inv);}}
                    options.EnableSideMarkNonlinearAlignment=false;Compare("real_global",cis,tiff,anchor,options);
                    options.EnableSideMarkNonlinearAlignment=true;Compare("real_side",cis,tiff,anchor,options);
                    options.EnableWhiteInkInspection=false;Compare("real_white_disabled",cis,tiff,anchor,options);
                    if(RepeatCount>0) Stability(cis,tiff,anchor,options);
                }
            }
        }
        private static void Contracts()
        {
            using(var mat=new Mat(40,40,MatType.CV_8UC1,Scalar.All(0)))
            {
                var image=PatchNativeInterop.Image.Borrow(mat);var anchor=new AlignmentNativeInterop.Anchor{Size=48,PixelHeight=60,PixelWidth=60};
                var config=AlignmentNativeInterop.Config.From(MarkAlignmentOptions.FromConfig(new AppConfig()));var error=new byte[1024];
                image.Bytes=1;
                int status=AlignmentNativeInterop.cis_alignment_compute(ref image,ref image,ref anchor,ref config,0,out IntPtr handle,error,(uint)error.Length);
                Check(status==-1&&handle==IntPtr.Zero,"ABI short image rejected");
                image=PatchNativeInterop.Image.Borrow(mat);config.Size=1;
                status=AlignmentNativeInterop.cis_alignment_compute(ref image,ref image,ref anchor,ref config,0,out handle,error,(uint)error.Length);
                Check(status==-1&&handle==IntPtr.Zero,"ABI wrong config layout rejected");
                config.Size=192;status=AlignmentNativeInterop.cis_alignment_compute(ref image,ref image,ref anchor,ref config,99,out handle,error,(uint)error.Length);
                Check(status==-1&&handle==IntPtr.Zero,"ABI mode rejected");
                // 关闭白墨时仍可取得结果并验证两段式 UTF-8、数组边界、重复 Dispose。
                config.EnableWhiteInkInspection=0;
                status=AlignmentNativeInterop.cis_alignment_compute(ref image,ref image,ref anchor,ref config,1,out handle,error,(uint)error.Length);
                Check(status==0&&handle!=IntPtr.Zero,"ABI disabled white handle");
                var result=new AlignmentNativeInterop.ResultHandle(handle);
                using(result)
                {
                    Check(AlignmentNativeInterop.cis_alignment_log(result,1,null,0,out uint required)==-3&&required>1,"ABI query UTF8 size");
                    Check(AlignmentNativeInterop.cis_alignment_log(result,1,new byte[1],1,out required)==-3,"ABI short UTF8 rejected");
                    Check(AlignmentNativeInterop.cis_alignment_log(result,9,null,0,out required)==-1,"ABI log kind rejected");
                    var summary=new AlignmentNativeInterop.Summary{Size=1};Check(AlignmentNativeInterop.cis_alignment_summary(result,ref summary)==-1,"ABI summary layout rejected");
                    status=AlignmentNativeInterop.cis_alignment_warp(result,ref image,ref image,error,(uint)error.Length);Check(status==-1,"ABI overlapping warp rejected");
                    using(var other=new Mat(40,40,MatType.CV_8UC1))
                    {var target=PatchNativeInterop.Image.Borrow(other);status=AlignmentNativeInterop.cis_alignment_warp(result,ref image,ref target,error,(uint)error.Length);Check(status==-1,"ABI missing transform rejected");}
                }
                result.Dispose();Check(result.IsClosed,"SafeHandle idempotent Dispose");
            }
        }

        private static void Stability(Mat cis,Mat tiff,CisQrAnchor anchor,MarkAlignmentOptions options)
        {
            options.EnableWhiteInkInspection=true;
            var samples=new List<object>();double baselinePixels=double.NaN;Mat baselineH=null;
            try
            {
                for(int i=-3;i<RepeatCount;i++)
                {
                    var watch=Stopwatch.StartNew();
                    using(var result=Native.ComputeTransform(cis,tiff,anchor,options,out _,out string diagnostic))
                    {
                        if(result==null) throw new Exception(diagnostic);
                        using(var warped=Native.WarpToTiffSpace(cis,result,tiff.Size()))
                        {
                            double pixels=Cv2.Norm(warped,NormTypes.L1);
                            if(baselineH==null) {baselineH=result.GlobalTransform.Clone();baselinePixels=pixels;}
                            else {Near(Cv2.Norm(result.GlobalTransform,baselineH,NormTypes.INF),0,"repeat matrix",0);Near(pixels,baselinePixels,"repeat warped checksum",0);}
                        }
                    }
                    double ms=watch.Elapsed.TotalMilliseconds;
                    if(i>=0) {using(var process=Process.GetCurrentProcess()) samples.Add(new {Iteration=i+1,Milliseconds=ms,PrivateBytes=process.PrivateMemorySize64});}
                    if(i>=0&&(i+1)%5==0) Console.WriteLine("STABILITY "+(i+1)+"/"+RepeatCount);
                }
            }
            finally {baselineH?.Dispose();}
            Results.Add(new {Name="native_repeat",Warmup=3,Iterations=RepeatCount,Samples=samples});
        }
    }
}
