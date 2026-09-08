using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using OpenCvSharp;
using LegacyDetector = PatchRegression.Legacy.PatchDefectDetector;
using LegacyCache = PatchRegression.Legacy.PatchSiftTemplateCache;
using LegacyWorker = PatchRegression.Legacy.PatchSiftWorker;

namespace PatchRegression
{
    // 独立开发验证入口，不进入正式 UI/生产流水线。Legacy 仅作为本次迁移前的冻结基准。
    internal static class Program
    {
        private sealed class Log : IAppLogger
        {
            public string Diagnostic;
            public void Write(string message) { Console.WriteLine(message); }
            public void WriteDiagnostic(string message) { Diagnostic = message; }
        }
        private sealed class Case { public string Name; public Mat Alpha, Cis; public int Threshold; }
        private sealed class Row
        {
            public string Name { get; set; }
            public bool Equal { get; set; }
            public string Error { get; set; }
            public double LegacyMs { get; set; }
            public double NativeMs { get; set; }
            public string LegacyAlignment { get; set; }
            public string NativeAlignment { get; set; }
            public object LegacyResult { get; set; }
            public object NativeResult { get; set; }
        }
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        private static int Main(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(args[0]);
                string Option(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
                string output = Path.Combine(root, "obj", "PatchNativeValidation", Option("--output") ?? "default"); Directory.CreateDirectory(output);
                AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path.Combine(root, "bin/x64/Debug/net48/app_config.json")));
                // 仅覆盖当前验证进程内的夹具位置/物理尺寸，绝不改写软件的 app_config.json。
                if (Option("--layout-log") != null) config.DebugLogPath = Option("--layout-log");
                if (Option("--tiff-dir") != null) config.TiffImageDir = Option("--tiff-dir");
                if (Option("--row-spacing") != null) config.MarkCisRowSpacingMm = double.Parse(Option("--row-spacing"), System.Globalization.CultureInfo.InvariantCulture);
                if (Option("--layout-height") != null) config.MarkTiffHeightMm = double.Parse(Option("--layout-height"), System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(output, "effective-config.json"), JsonSerializer.Serialize(config, Json));
                var cases = Synthetic();
                if (Option("--frames") != null) cases.AddRange(FromFrames(Option("--frames"), config));
                if (args.Length > 1 && args[1] == "--pairs")
                    foreach (string file in Directory.GetFiles(args[2], "*_alpha.png").OrderBy(x => x).Take(24))
                    {
                        string cis = file.Replace("_alpha.png", "_cis.jpg");if (!File.Exists(cis)) continue;
                        cases.Add(new Case { Name = Path.GetFileNameWithoutExtension(file), Alpha = Cv2.ImRead(file, ImreadModes.Grayscale), Cis = Cv2.ImRead(cis, ImreadModes.Unchanged), Threshold = 100 });
                    }
                var rows = new List<Row>();
                using (var oldCache = new LegacyCache())
                using (var cache = new PatchSiftTemplateCache())
                using (var oldWorker = new LegacyWorker(oldCache))
                using (var worker = new PatchSiftWorker(cache))
                {
                    foreach (Case item in cases)
                    {
                        // 覆盖默认尺度、独立细线尺度、关闭配准，检查输出图开关不会改变结果。
                        foreach (int variant in (Array.IndexOf(args, "--actual-only") >= 0 ? new[] { 3 } : new[] { 0, 1, 2 }))
                        {
                            AppConfig current = Clone(config);current.SaveCroppedImages = true;
                            if (variant != 3)
                            {
                                current.EnableFineLineBreakDetection = true;current.FineLineMinBreakLengthMm = .5;
                                current.FineLineMaxWidthMm = 5;
                            }
                            if (variant == 1) current.DefectDetectScale = .5;
                            if (variant == 2) current.EnableSiftLocalAlign = false;
                            string name = item.Name + "_v" + variant;
                            var row = new Row { Name = name };
                            string oldDir = Path.Combine(output, name, "legacy"), newDir = Path.Combine(output, name, "native");
                            Directory.CreateDirectory(oldDir);Directory.CreateDirectory(newDir);
                            var oldLog = new Log();var newLog = new Log();
                            using (var originalA = item.Alpha.Clone())
                            using (var originalC = item.Cis.Clone())
                            {
                                try
                                {
                                    var clock = Stopwatch.StartNew();
                                    var old = LegacyDetector.Detect(item.Alpha, item.Cis, item.Threshold, current,
                                        Path.Combine(oldDir,"defect.png"),Path.Combine(oldDir,"cis.png"),Path.Combine(oldDir,"edge.png"),oldWorker,name,oldLog);
                                    row.LegacyMs = clock.Elapsed.TotalMilliseconds;clock.Restart();
                                    var result = PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, current,
                                        Path.Combine(newDir,"defect.png"),Path.Combine(newDir,"cis.png"),Path.Combine(newDir,"edge.png"),worker,name,newLog);
                                    row.NativeMs = clock.Elapsed.TotalMilliseconds;
                                    row.LegacyResult = old;row.NativeResult = result;
                                    // 相同字段的序列化顺序来自相同模型快照；数值容许仅 1e-9 浮点表示误差。
                                    row.Equal = Compare(old, result);
                                    foreach (string image in new[] { "cis.png", "edge.png", "defect.png" })
                                        using (var a = Cv2.ImRead(Path.Combine(oldDir,image),ImreadModes.Unchanged))
                                        using (var b = Cv2.ImRead(Path.Combine(newDir,image),ImreadModes.Unchanged))
                                            if (a.Empty() || b.Empty() || a.Size()!=b.Size() || Cv2.Norm(a,b,NormTypes.L1)!=0)
                                            { row.Equal=false;row.Error += image + " pixel mismatch; "; }
                                    if(Cv2.Norm(item.Alpha,originalA,NormTypes.L1)!=0||Cv2.Norm(item.Cis,originalC,NormTypes.L1)!=0)
                                        throw new Exception("Input buffer modified");
                                    var withoutImages = PatchDefectDetector.Detect(item.Alpha,item.Cis,item.Threshold,current,null,null,null,worker,name,newLog);
                                    if (!Compare(result,withoutImages)) throw new Exception("Output flags changed detection");
                                }
                                catch(Exception ex) { row.Error=ex.ToString();row.Equal=false; }
                            }
                            row.LegacyAlignment=oldLog.Diagnostic;row.NativeAlignment=newLog.Diagnostic;rows.Add(row);
                            Console.WriteLine(name + ": " + (row.Equal?"MATCH":"MISMATCH")+" old="+row.LegacyMs.ToString("F1")+" native="+row.NativeMs.ToString("F1")+"ms "+row.Error);
                        }
                    }
                }
                File.WriteAllText(Path.Combine(output,"comparison.json"),JsonSerializer.Serialize(rows,Json));
                // 在一个共享缓存和 4 个独立 worker 上反复检测，核对并发与生命周期。
                var repeat = new List<object>();
                using(var cache=new PatchSiftTemplateCache())
                {
                    Case item=cases.Last();config.EnableFineLineBreakDetection=true;config.FineLineMinBreakLengthMm=.5;
                    using(var worker=new PatchSiftWorker(cache))
                    {
                        var expected=PatchDefectDetector.Detect(item.Alpha,item.Cis,item.Threshold,config,null,null,null,worker,item.Name,null);
                        for(int batch=0;batch<5;batch++)
                        {
                            long before=Process.GetCurrentProcess().PrivateMemorySize64;
                            Parallel.For(0,4,new ParallelOptions{MaxDegreeOfParallelism=4},i=>{
                                using(var w=new PatchSiftWorker(cache))
                                {
                                    var actual=PatchDefectDetector.Detect(item.Alpha,item.Cis,item.Threshold,config,null,null,null,w,item.Name,null);
                                    if(!Compare(expected,actual))throw new Exception("Concurrent repeat mismatch");
                                }
                            });
                            repeat.Add(new { batch,before,after=Process.GetCurrentProcess().PrivateMemorySize64 });
                        }
                    }
                }
                File.WriteAllText(Path.Combine(output,"repeat.json"),JsonSerializer.Serialize(repeat,Json));
                File.WriteAllText(Path.Combine(output,"contract.json"),JsonSerializer.Serialize(CheckContract(cases[1], config),Json));
                File.WriteAllText(Path.Combine(output,"performance.json"),JsonSerializer.Serialize(MeasureWithoutSaving(cases.Last(), config),Json));
                foreach(var item in cases){item.Alpha.Dispose();item.Cis.Dispose();}
                int mismatches=rows.Count(x=>!x.Equal);Console.WriteLine("TOTAL="+rows.Count+" MISMATCH="+mismatches);return mismatches==0?0:1;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
        }
        private static AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config));

        private static List<string> CheckContract(Case item, AppConfig config)
        {
            var passed = new List<string>();
            void Check(bool ok, string name) { if (!ok) throw new Exception("ABI contract: " + name); passed.Add(name); }
            var cache = new PatchSiftTemplateCache();
            using (var worker = new PatchSiftWorker(cache))
            {
                // 已建立的 worker 应独立保有 shared_ptr；托管缓存先 Dispose 也不形成悬空指针。
                cache.Dispose();
                var alpha = PatchNativeInterop.Image.Borrow(item.Alpha);
                var cis = PatchNativeInterop.Image.Borrow(item.Cis);
                var options = PatchNativeInterop.Config.From(config, item.Threshold, 1);
                options.Alignment = 0;
                var validImage = alpha;
                var validOptions = options;
                foreach (string name in new[] { "image layout", "short input", "stride", "channels", "null pixels", "config layout", "nonfinite scale" })
                {
                    alpha = validImage; options = validOptions;
                    if (name == "image layout") alpha.Size = 0;
                    if (name == "short input") alpha.Bytes--;
                    if (name == "stride") alpha.Stride = 1;
                    if (name == "channels") alpha.Channels = 2;
                    if (name == "null pixels") alpha.Pixels = IntPtr.Zero;
                    if (name == "config layout") options.Size = 0;
                    if (name == "nonfinite scale") options.Scale = double.NaN;
                    int status = PatchNativeInterop.cis_patch_detect(worker.Handle, ref alpha, ref cis, ref options,
                        out IntPtr invalidResult, worker.Error, (uint)worker.Error.Length);
                    Check(status == -1 && invalidResult == IntPtr.Zero, name + " rejected without result");
                }
                alpha = validImage; options = validOptions;
                int success = PatchNativeInterop.cis_patch_detect(worker.Handle, ref alpha, ref cis, ref options,
                    out IntPtr pointer, worker.Error, (uint)worker.Error.Length);
                PatchNativeInterop.Check(success, worker.Error);
                using (var result = new PatchNativeInterop.ResultHandle(pointer))
                {
                    var summary = new PatchNativeInterop.Summary { Size = 80 };
                    Check(PatchNativeInterop.cis_patch_result_summary(result, ref summary, worker.Error, (uint)worker.Error.Length) == 0,
                        "worker survives disposed cache");
                    summary.Size = 0;
                    Check(PatchNativeInterop.cis_patch_result_summary(result, ref summary, worker.Error, (uint)worker.Error.Length) == -1,
                        "summary layout rejected");
                    if (summary.DefectCount > 0)
                        Check(PatchNativeInterop.cis_patch_result_defects(result, null, 0, worker.Error, (uint)worker.Error.Length) == -3,
                            "short defect output rejected");
                    Check(cis_patch_result_log(result, null, 0, out uint needed) == -3 && needed >= 1, "log size query");
                    using (var target = new Mat(item.Alpha.Size(), MatType.CV_8UC1, Scalar.All(123)))
                    {
                        var before = Cv2.Sum(target);
                        Check(cis_patch_result_copy_image(result, 0, target.Data, 1, (ulong)target.Step(), worker.Error, (uint)worker.Error.Length) == -1 &&
                            Cv2.Sum(target) == before, "short image output rejected without writing");
                    }
                }
                GC.KeepAlive(item.Alpha); GC.KeepAlive(item.Cis);
            }
            Console.WriteLine("ABI CONTRACT: " + passed.Count + " PASS");
            return passed;
        }

        private static object MeasureWithoutSaving(Case item, AppConfig config)
        {
            var legacy = new List<double>(); var native = new List<double>();
            using (var oldCache = new LegacyCache()) using (var cache = new PatchSiftTemplateCache())
            using (var oldWorker = new LegacyWorker(oldCache)) using (var worker = new PatchSiftWorker(cache))
            {
                // 先预热，再交替调用次序；不落盘、不统计首次模型/缓存创建，不以单次耗时宣称提速。
                Action runLegacy = () => LegacyDetector.Detect(item.Alpha,item.Cis,item.Threshold,config,null,null,null,oldWorker,item.Name,null);
                Action runNative = () => PatchDefectDetector.Detect(item.Alpha,item.Cis,item.Threshold,config,null,null,null,worker,item.Name,null);
                runLegacy(); runNative();
                for (int i = 0; i < 6; i++)
                {
                    foreach (bool old in (i % 2 == 0 ? new[] { true, false } : new[] { false, true }))
                    {
                        var clock = Stopwatch.StartNew(); (old ? runLegacy : runNative)();
                        (old ? legacy : native).Add(clock.Elapsed.TotalMilliseconds);
                    }
                }
            }
            return new { sample = item.Name, note = "warm, sequential, no image saves; six interleaved calls each; not production takt", legacyMs = legacy, nativeMs = native };
        }

        [DllImport("CISVisionCore.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int cis_patch_result_log(PatchNativeInterop.ResultHandle result, [Out] byte[] text, uint capacity, out uint needed);
        [DllImport("CISVisionCore.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int cis_patch_result_copy_image(PatchNativeInterop.ResultHandle result, int kind, IntPtr pixels, ulong bytes, ulong stride, [Out] byte[] error, uint capacity);
        private static bool Compare(object a,object b)
        {
            using(var first=JsonDocument.Parse(JsonSerializer.Serialize(a,Json)))
            using(var second=JsonDocument.Parse(JsonSerializer.Serialize(b,Json))) return CompareElement(first.RootElement,second.RootElement);
        }
        private static bool CompareElement(JsonElement a,JsonElement b)
        {
            if(a.ValueKind!=b.ValueKind)return false;
            if(a.ValueKind==JsonValueKind.Number)return Math.Abs(a.GetDouble()-b.GetDouble())<=1e-9;
            if(a.ValueKind==JsonValueKind.Array){var aa=a.EnumerateArray().ToArray();var bb=b.EnumerateArray().ToArray();return aa.Length==bb.Length&&aa.Zip(bb,CompareElement).All(x=>x);}
            if(a.ValueKind==JsonValueKind.Object){foreach(var p in a.EnumerateObject())if(!b.TryGetProperty(p.Name,out var value)||!CompareElement(p.Value,value))return false;return a.EnumerateObject().Count()==b.EnumerateObject().Count();}
            return a.ToString()==b.ToString();
        }
        private static List<Case> Synthetic()
        {
            var cases=new List<Case>();
            cases.Add(new Case{Name="blank",Alpha=new Mat(600,900,MatType.CV_8UC1,Scalar.All(0)),Cis=new Mat(600,900,MatType.CV_8UC1,Scalar.All(0)),Threshold=100});
            var alpha=new Mat(800,1100,MatType.CV_8UC1,Scalar.All(0));
            Cv2.Rectangle(alpha,new Rect(100,100,380,400),Scalar.All(255),-1);
            Cv2.Rectangle(alpha,new Rect(160,160,240,220),Scalar.All(0),-1);
            Cv2.Line(alpha,new Point(120,600),new Point(900,600),Scalar.All(255),10);
            for(int i=0;i<8;i++)Cv2.Circle(alpha,new Point(580+(i%3)*140,100+(i/3)*140),35+i,Scalar.All(255),-1);
            var cis=alpha.Clone();Cv2.Rectangle(cis,new Rect(110,390,65,55),Scalar.All(0),-1);
            Cv2.Circle(cis,new Point(750,500),25,Scalar.All(255),-1);Cv2.Rectangle(cis,new Rect(470,580,16,40),Scalar.All(0),-1);
            cases.Add(new Case{Name="three_defects",Alpha=alpha,Cis=cis,Threshold=100});
            using(var matrix=Cv2.GetRotationMatrix2D(new Point2f(550,400),.7,1.005))
            {matrix.Set(0,2,matrix.At<double>(0,2)+7);matrix.Set(1,2,matrix.At<double>(1,2)-4);var shifted=new Mat();Cv2.WarpAffine(cis,shifted,matrix,alpha.Size(),InterpolationFlags.Cubic);
                cases.Add(new Case{Name="shift_rotation",Alpha=alpha.Clone(),Cis=shifted,Threshold=100});}
            // 非连续 ROI + BGR 输入覆盖跨 ABI 步长和通道转换。
            var parent=new Mat(840,1140,MatType.CV_8UC3,Scalar.All(0));using(var color=new Mat())
            {Cv2.CvtColor(cis,color,ColorConversionCodes.GRAY2BGR);using(var roi=new Mat(parent,new Rect(20,20,1100,800)))color.CopyTo(roi);}
            cases.Add(new Case{Name="strided_bgr",Alpha=alpha.Clone(),Cis=new Mat(parent,new Rect(20,20,1100,800)),Threshold=100});parent.Dispose();
            return cases;
        }
        private static unsafe List<Case> FromFrames(string directory,AppConfig config)
        {
            var result=new List<Case>();StitchedImageResult stitched=null;
            using(var stitcher=new ImageStitcher())
            {
                stitcher.StitchCompleted+=(sender,item)=>{if(stitched==null)stitched=item;};
                bool initialized=false;
                foreach(string path in Directory.GetFiles(directory).Where(x=>x.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)).OrderBy(x=>x,StringComparer.Ordinal))
                {
                    using(var image=Cv2.ImRead(path,ImreadModes.Color))
                    {
                        if(!initialized){stitcher.Configure(image.Width,image.Height,(int)image.Step(),24,config);initialized=true;}
                        var bytes=new byte[checked((int)(image.Step()*image.Height))];Marshal.Copy(image.Data,bytes,0,bytes.Length);
                        stitcher.ProcessOwnedFrame(bytes,image.Width,image.Height,(int)image.Step(),24);
                    }
                    if(stitched!=null)break;
                }
            }
            if(stitched==null)throw new Exception("No stitched segment in test input");
            Console.WriteLine("STITCHED: "+stitched.EndQrText+" "+stitched.Width+"x"+stitched.Height);
            var layout=DebugLogParser.ParseForQrCode(config.DebugLogPath,stitched.EndQrText,config.TiffImageDir);
            if(layout==null)throw new Exception("Missing layout record for "+stitched.EndQrText);
            using(var raw=Cv2.ImRead(layout.TiffFullPath,ImreadModes.Unchanged))
            using(var alpha=new Mat())
            using(var tiff=new Mat(raw.Height,raw.Width,MatType.CV_8UC3))
            using(var cis=new Mat(stitched.Height,stitched.Width,MatType.CV_8UC3))
            {
                if(raw.Channels()!=4)throw new Exception("Raw layout must contain Alpha");
                Cv2.ExtractChannel(raw,alpha,3);
                for(int y=0;y<raw.Height;y++){byte* s=(byte*)raw.Ptr(y),d=(byte*)tiff.Ptr(y);for(int x=0;x<raw.Width;x++,s+=4,d+=3){
                    float a=s[3]*(1f/255f),inv=1f-a;for(int c=0;c<3;c++)d[c]=s[3]==255?s[c]:s[3]==0?(byte)255:(byte)(s[c]*a+255f*inv);}}
                Marshal.Copy(stitched.Data,0,cis.Data,stitched.Data.Length);
                var anchor=new CisQrAnchor{CenterX=stitched.EndQrCenterX,GlobalCenterY=stitched.EndQrGlobalY,SegmentStartGlobalY=stitched.SegmentStartGlobalY,
                    PixelWidth=stitched.EndQrPixelWidth,PixelHeight=stitched.EndQrPixelHeight};
                var options=MarkAlignmentOptions.FromConfig(config);options.EnableWhiteInkInspection=false;
                using(var alignment=ImageAligner.ComputeTransform(cis,tiff,anchor,options,out int threshold,out string diagnostic))
                {
                    if(alignment?.GlobalTransform==null||alignment.GlobalTransform.Empty())throw new Exception(diagnostic);
                    using(var warped=ImageAligner.WarpToTiffSpace(cis,alignment,tiff.Size()))
                    using(var flippedCis=new Mat())using(var flippedAlpha=new Mat())
                    {
                        Cv2.Flip(warped,flippedCis,FlipMode.X);Cv2.Flip(alpha,flippedAlpha,FlipMode.X);
                        double ppm=config.LayoutDpi/25.4;int ox=(int)(config.LayoutOriginXmm*ppm),oy=(int)(config.LayoutOriginYmm*ppm);
                        foreach(var part in layout.Parts)
                        {
                            if(part.HotInkTaskID?.Contains("QRCode")==true)continue;
                            int x=Math.Max(0,(int)(ox+part.RelativeTopLeftX*ppm)),y=Math.Max(0,(int)(oy+part.RelativeTopLeftY*ppm));
                            int w=Math.Min((int)((part.RelativeBottomRightX-part.RelativeTopLeftX)*ppm),tiff.Width-x),h=Math.Min((int)((part.RelativeBottomRightY-part.RelativeTopLeftY)*ppm),tiff.Height-y);
                            if(w<=0||h<=0)continue;var roi=new Rect(x,y,w,h);
                            using(var a=new Mat(flippedAlpha,roi))using(var c=new Mat(flippedCis,roi))
                                result.Add(new Case{Name=part.HotInkTaskID,Alpha=a.Clone(),Cis=c.Clone(),Threshold=Math.Max(0,Math.Min(255,threshold+config.DefectCisThreshOffset))});
                        }
                    }
                }
            }
            return result;
        }
    }
}
