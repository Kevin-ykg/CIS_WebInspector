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
        private sealed class Case { public string Name; public Mat Alpha, Cis; public int Threshold; public Rect Core; }
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
                string Option(string name)
                { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
                // 托管编排回归独立于算法新旧对照；不读取或改写现场 app_config.json。
                if (Array.IndexOf(args, "--pipeline-only") >= 0)
                {
                    return PipelineChecks.Run(root, Option("--output") ?? "pipeline", Option("--compare"));
                }
                // 0.6 mm 灵敏度是隔离实验：从配置取快照，在内存中改参数，不回写现场文件。
                if (Array.IndexOf(args, "--sensitivity-06") >= 0)
                {
                    return SensitivityChecks.Run(root, Option("--config"), Option("--output") ?? "sensitivity-06");
                }
                string output = Path.Combine(root, "obj", "PatchNativeValidation", Option("--output") ?? "default");
                Directory.CreateDirectory(output);
                // 回归使用显式快照，避免测试期间现场开关/阈值变化污染前后比较；不写回配置。
                string configPath = Option("--config") ?? Path.Combine(root, "bin/x64/Debug/net48/app_config.json");
                AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));
                // 仅覆盖当前验证进程内的夹具位置/物理尺寸，绝不改写软件的 app_config.json。
                if (Option("--layout-log") != null)
                {
                    config.DebugLogPath = Option("--layout-log");
                }

                if (Option("--tiff-dir") != null)
                {
                    config.TiffImageDir = Option("--tiff-dir");
                }

                if (Option("--row-spacing") != null)
                {
                    config.MarkCisRowSpacingMm = double.Parse(Option("--row-spacing"), System.Globalization.CultureInfo.InvariantCulture);
                }

                if (Option("--layout-height") != null)
                {
                    config.MarkTiffHeightMm = double.Parse(Option("--layout-height"), System.Globalization.CultureInfo.InvariantCulture);
                }
                if (Option("--origin-y") != null)
                {
                    config.LayoutOriginYmm = double.Parse(Option("--origin-y"), System.Globalization.CultureInfo.InvariantCulture);
                }
                if (Option("--parallelism") != null)
                {
                    config.DefectMaxParallelism = int.Parse(Option("--parallelism"), System.Globalization.CultureInfo.InvariantCulture);
                }

                File.WriteAllText(Path.Combine(output, "effective-config.json"), JsonSerializer.Serialize(config, Json));
                // 统一配准有意改变图像结果；接口契约应能独立验证，不能把旧版像素
                // 不等价的预期 MISMATCH 混进 ABI 检查的退出码。
                if (Array.IndexOf(args, "--contract-only") >= 0)
                {
                    var contractCases = Synthetic();
                    try { CheckContract(contractCases[1], config); return 0; }
                    finally { foreach (var item in contractCases) { item.Alpha.Dispose(); item.Cis.Dispose(); } }
                }
                string localProbePart = Option("--local-probe-part");
                var cases = Option("--raw-inputs") == null && localProbePart == null ? Synthetic() : new List<Case>();
                if (Option("--raw-inputs") != null)
                {
                    string directory = Option("--raw-inputs");
                    var thresholds = JsonSerializer.Deserialize<Dictionary<string, int>>(
                        File.ReadAllText(Path.Combine(directory, "thresholds.json")));
                    string corePath = Path.Combine(directory, "cores.json");
                    var cores = File.Exists(corePath)
                        ? JsonSerializer.Deserialize<Dictionary<string, Rect>>(File.ReadAllText(corePath), Json)
                        : new Dictionary<string, Rect>();
                    foreach (var entry in thresholds.OrderBy(entry => entry.Key))
                    {
                        if (localProbePart != null && entry.Key != localProbePart)
                        {
                            continue;
                        }
                        cases.Add(new Case { Name = entry.Key, Threshold = entry.Value,
                            Core = cores.TryGetValue(entry.Key, out Rect core) ? core : default,
                            Alpha = Cv2.ImRead(Path.Combine(directory, entry.Key + "_alpha.png"), ImreadModes.Unchanged),
                            Cis = Cv2.ImRead(Path.Combine(directory, entry.Key + "_raw.png"), ImreadModes.Unchanged) });
                    }
                }
                if (Option("--frames") != null)
                {
                    cases.AddRange(FromFrames(Option("--frames"), config, localProbePart,
                        Array.IndexOf(args, "--registration-context") >= 0, Option("--export-frame")));
                }

                if (Array.IndexOf(args, "--export-inputs") >= 0)
                {
                    ExportInputs(cases, output);
                }
                if (Array.IndexOf(args, "--export-only") >= 0)
                {
                    foreach (Case item in cases) { item.Alpha.Dispose(); item.Cis.Dispose(); }
                    return 0;
                }
                if (localProbePart != null)
                {
                    try
                    {
                        if (cases.Count != 1)
                        {
                            throw new Exception("Expected exactly one local registration part: " + localProbePart);
                        }
                        using (var cache = new PatchSiftTemplateCache())
                        using (var worker = new PatchSiftWorker(cache))
                        {
                            var item = cases[0];
                            var log = new Log();
                            var detected = PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, config,
                                null, null, null, worker, item.Name, log, item.Core);
                            string report = JsonSerializer.Serialize(new
                            {
                                item.Name, item.Threshold, item.Core, detected.AlignmentStatus,
                                detected.AlignmentModel, detected.AlignmentReason, detected.AlignmentMatches,
                                detected.AlignmentInliers, detected.AlignmentRmsOriginalPx,
                                detected.AlignmentEdgeBeforeWorkPx, detected.AlignmentEdgeAfterWorkPx,
                                detected.AlignmentStabilityOriginalPx, detected.IsPass,
                                detected.InnerDefectCount, detected.OuterDefectCount,
                                detected.FineLineBreakCount, Diagnostic = log.Diagnostic
                            }, Json);
                            Console.WriteLine(report);
                            File.WriteAllText(Path.Combine(output, "local-probe.json"), report);
                        }
                    }
                    finally
                    {
                        foreach (var item in cases) { item.Alpha.Dispose(); item.Cis.Dispose(); }
                    }
                    return 0;
                }

                if (args.Length > 1 && args[1] == "--pairs")
                {
                    foreach (string file in Directory.GetFiles(args[2], "*_alpha.png").OrderBy(x => x).Take(24))
                    {
                        string cis = file.Replace("_alpha.png", "_cis.jpg");
                        if (!File.Exists(cis))
                        {
                            continue;
                        }

                        cases.Add(new Case { Name = Path.GetFileNameWithoutExtension(file), Alpha = Cv2.ImRead(file, ImreadModes.Grayscale), Cis = Cv2.ImRead(cis, ImreadModes.Unchanged), Threshold = 100 });
                    }
                }

                if (Array.IndexOf(args, "--benchmark-batch") >= 0)
                {
                    // 固定原始 ROI 与现场配置，整批并发、不保存图；避免将单件耗时外推为整批节拍。
                    // 现有排版数据也含 31 位历史编号；不能因此漏掉整批中的四个真实零件。
                    var actualCases = cases.Where(item => item.Name.Length >= 31 && item.Name.Length <= 32 &&
                        item.Name.All(Uri.IsHexDigit)).ToList();
                    if (actualCases.Count == 0) { throw new ArgumentException("批次性能测试需要真实零件 ROI"); }
                    File.WriteAllText(Path.Combine(output, "batch-performance.json"),
                        JsonSerializer.Serialize(MeasureBatch(actualCases, config,
                            Array.IndexOf(args, "--cold-cache") >= 0), Json));
                    foreach (Case item in cases) { item.Alpha.Dispose(); item.Cis.Dispose(); }
                    return 0;
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
                            AppConfig current = Clone(config);
                            current.SaveCroppedImages = true;
                            if (variant != 3)
                            {
                                current.EnableFineLineBreakDetection = true;
                                current.FineLineMinBreakLengthMm = .5;
                                current.FineLineMaxWidthMm = 5;
                            }
                            if (variant == 1)
                            {
                                current.DefectDetectScale = .5;
                            }

                            if (variant == 2)
                            {
                                current.EnableSiftLocalAlign = false;
                            }

                            string name = item.Name + "_v" + variant;
                            var row = new Row { Name = name };
                            string oldDir = Path.Combine(output, name, "legacy"), newDir = Path.Combine(output, name, "native");
                            Directory.CreateDirectory(oldDir);
                            Directory.CreateDirectory(newDir);
                            var oldLog = new Log();
                            var newLog = new Log();
                            using (var originalA = item.Alpha.Clone())
                            using (var originalC = item.Cis.Clone())
                            {
                                try
                                {
                                    var clock = Stopwatch.StartNew();
                                    var old = LegacyDetector.Detect(item.Alpha, item.Cis, item.Threshold, current,
                                        Path.Combine(oldDir, "defect.png"), Path.Combine(oldDir, "cis.png"), Path.Combine(oldDir, "edge.png"), oldWorker, name, oldLog);
                                    row.LegacyMs = clock.Elapsed.TotalMilliseconds;
                                    clock.Restart();
                                    var result = PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, current,
                                        Path.Combine(newDir, "defect.png"), Path.Combine(newDir, "cis.png"), Path.Combine(newDir, "edge.png"), worker, name, newLog);
                                    row.NativeMs = clock.Elapsed.TotalMilliseconds;
                                    row.LegacyResult = old;
                                    row.NativeResult = result;
                                    // 相同字段的序列化顺序来自相同模型快照；数值容许仅 1e-9 浮点表示误差。
                                    row.Equal = Compare(old, result);
                                    foreach (string image in new[] { "cis.png", "edge.png", "defect.png" })
                                    {
                                        using (var a = Cv2.ImRead(Path.Combine(oldDir, image), ImreadModes.Unchanged))
                                        using (var b = Cv2.ImRead(Path.Combine(newDir, image), ImreadModes.Unchanged))
                                        {
                                            if (a.Empty() || b.Empty() || a.Size() != b.Size() || Cv2.Norm(a, b, NormTypes.L1) != 0)
                                            { row.Equal = false; row.Error += image + " pixel mismatch; "; }
                                        }
                                    }

                                    if (Cv2.Norm(item.Alpha, originalA, NormTypes.L1) != 0 || Cv2.Norm(item.Cis, originalC, NormTypes.L1) != 0)
                                    {
                                        throw new Exception("Input buffer modified");
                                    }

                                    var withoutImages = PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, current, null, null, null, worker, name, newLog);
                                    if (!Compare(result, withoutImages))
                                    {
                                        throw new Exception("Output flags changed detection");
                                    }
                                }
                                catch (Exception ex) { row.Error = ex.ToString(); row.Equal = false; }
                            }
                            row.LegacyAlignment = oldLog.Diagnostic;
                            row.NativeAlignment = newLog.Diagnostic;
                            rows.Add(row);
                            Console.WriteLine(name + ": " + (row.Equal ? "MATCH" : "MISMATCH") + " old=" + row.LegacyMs.ToString("F1") + " native=" + row.NativeMs.ToString("F1") + "ms " + row.Error);
                        }
                    }
                }
                File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(rows, Json));
                // 在一个共享缓存和 4 个独立 worker 上反复检测，核对并发与生命周期。
                var repeat = new List<object>();
                using (var cache = new PatchSiftTemplateCache())
                {
                    Case item = cases.Last();
                    config.EnableFineLineBreakDetection = true;
                    config.FineLineMinBreakLengthMm = .5;
                    using (var worker = new PatchSiftWorker(cache))
                    {
                        var expected = PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, config, null, null, null, worker, item.Name, null);
                        for (int batch = 0; batch < 5; batch++)
                        {
                            long before = Process.GetCurrentProcess().PrivateMemorySize64;
                            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                            {
                                using (var w = new PatchSiftWorker(cache))
                                {
                                    var actual = PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, config, null, null, null, w, item.Name, null);
                                    if (!Compare(expected, actual))
                                    {
                                        throw new Exception("Concurrent repeat mismatch");
                                    }
                                }
                            });
                            repeat.Add(new { batch, before, after = Process.GetCurrentProcess().PrivateMemorySize64 });
                        }
                    }
                }
                File.WriteAllText(Path.Combine(output, "repeat.json"), JsonSerializer.Serialize(repeat, Json));
                // 单零件定点回归也应执行接口契约检查，不能假定输入目录至少有两件。
                Case contractCase = cases[Math.Min(1, cases.Count - 1)];
                File.WriteAllText(Path.Combine(output, "contract.json"), JsonSerializer.Serialize(CheckContract(contractCase, config), Json));
                File.WriteAllText(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(MeasureWithoutSaving(cases.Last(), config), Json));
                foreach (var item in cases)
                { item.Alpha.Dispose(); item.Cis.Dispose(); }
                int mismatches = rows.Count(x => !x.Equal);
                Console.WriteLine("TOTAL=" + rows.Count + " MISMATCH=" + mismatches);
                return mismatches == 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
        }
        private static AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config));

        private static void ExportInputs(List<Case> cases, string output)
        {
            // 保存全局对准后的原始像素和真实 core；不拿已经二值化、二次变换的
            // 结果图回做配准，避免丢失原始灰度或把搜索余量当成检测区域。
            string inputs = Path.Combine(output, "inputs");
            Directory.CreateDirectory(inputs);
            foreach (Case item in cases)
            {
                Cv2.ImWrite(Path.Combine(inputs, item.Name + "_alpha.png"), item.Alpha);
                Cv2.ImWrite(Path.Combine(inputs, item.Name + "_raw.png"), item.Cis);
            }
            File.WriteAllText(Path.Combine(inputs, "thresholds.json"),
                JsonSerializer.Serialize(cases.ToDictionary(item => item.Name, item => item.Threshold), Json));
            File.WriteAllText(Path.Combine(inputs, "cores.json"),
                JsonSerializer.Serialize(cases.ToDictionary(item => item.Name, item => item.Core), Json));
        }

        private static object MeasureBatch(List<Case> cases, AppConfig config, bool coldCache = false)
        {
            var samples = new List<double>();
            string[] expected = null;
            using (var cache = new PatchSiftTemplateCache())
            {
                // 两次预热不计入统计；随后十批同时核对确定性，当前配置细线开关保持原值。
                for (int batch = 0; batch < 12; batch++)
                {
                    // 正式流水线每批创建模板缓存。显式冷缓存模式用于检验
                    // 实际批次成本及冷/热一致性，不把跨批缓存复用当作现场节拍。
                    using var batchCache = coldCache ? new PatchSiftTemplateCache() : null;
                    var activeCache = batchCache ?? cache;
                    var actual = new string[cases.Count];
                    var watch = Stopwatch.StartNew();
                    Parallel.For(0, cases.Count, new ParallelOptions { MaxDegreeOfParallelism = config.DefectMaxParallelism }, i =>
                    {
                        using (var worker = new PatchSiftWorker(activeCache))
                        {
                            Case item = cases[i];
                            var log = new Log();
                            var result = PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, config,
                                null, null, null, worker, item.Name, log, item.Core);
                            // 缺陷计数相同不代表配准相同：同时核对去掉耗时后的模型/内点/轮廓诊断。
                            string alignment = log.Diagnostic ?? "";
                            int time = alignment.LastIndexOf(", time=", StringComparison.Ordinal);
                            if (time >= 0) { alignment = alignment.Substring(0, time); }
                            actual[i] = JsonSerializer.Serialize(result, Json) + "\n" + alignment;
                        }
                    });
                    watch.Stop();
                    if (expected != null && !expected.SequenceEqual(actual))
                    { throw new InvalidOperationException("整批并发重复结果不一致"); }
                    expected = actual;
                    if (batch >= 2) { samples.Add(watch.Elapsed.TotalMilliseconds); }
                    Console.WriteLine("BATCH " + batch + ": " + watch.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
                }
            }
            var sorted = samples.OrderBy(x => x).ToArray();
            return new { Parts = cases.Count, Parallelism = config.DefectMaxParallelism, Warmup = 2,
                Runs = samples, MedianMs = (sorted[4] + sorted[5]) / 2, Repeatable = true,
                Compared = "defect results and alignment diagnostics without timing", SaveImages = false,
                CacheMode = coldCache ? "new cache per batch" : "shared across repetitions",
                Results = expected };
        }

        private static List<string> CheckContract(Case item, AppConfig config)
        {
            var passed = new List<string>();
            void Check(bool ok, string name)
            { if (!ok) { throw new Exception("ABI contract: " + name); } passed.Add(name); }
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
                foreach (string name in new[] { "image layout", "short input", "stride", "channels", "null pixels", "config layout", "nonfinite scale", "core outside" })
                {
                    alpha = validImage;
                    options = validOptions;
                    if (name == "image layout")
                    {
                        alpha.Size = 0;
                    }

                    if (name == "short input")
                    {
                        alpha.Bytes--;
                    }

                    if (name == "stride")
                    {
                        alpha.Stride = 1;
                    }

                    if (name == "channels")
                    {
                        alpha.Channels = 2;
                    }

                    if (name == "null pixels")
                    {
                        alpha.Pixels = IntPtr.Zero;
                    }

                    if (name == "config layout")
                    {
                        options.Size = 0;
                    }

                    if (name == "nonfinite scale")
                    {
                        options.Scale = double.NaN;
                    }

                    if (name == "core outside")
                    {
                        options.CoreX = item.Alpha.Width;
                        options.CoreWidth = 10;
                        options.CoreHeight = 10;
                    }

                    int status = PatchNativeInterop.cis_patch_detect(worker.Handle, ref alpha, ref cis, ref options,
                        out IntPtr invalidResult, worker.Error, (uint)worker.Error.Length);
                    Check(status == -1 && invalidResult == IntPtr.Zero, name + " rejected without result");
                }
                alpha = validImage;
                options = validOptions;
                int success = PatchNativeInterop.cis_patch_detect(worker.Handle, ref alpha, ref cis, ref options,
                    out IntPtr pointer, worker.Error, (uint)worker.Error.Length);
                PatchNativeInterop.Check(success, worker.Error);
                using (var result = new PatchNativeInterop.ResultHandle(pointer))
                {
                    var summary = new PatchNativeInterop.Summary { Size = 80 };
                    Check(PatchNativeInterop.cis_patch_result_summary(result, ref summary, worker.Error, (uint)worker.Error.Length) == 0,
                        "worker survives disposed cache");
                    var alignment = new PatchNativeInterop.AlignmentInfo { Size = 72 };
                    Check(PatchNativeInterop.cis_patch_result_alignment(result, ref alignment, worker.Error,
                        (uint)worker.Error.Length) == 0 && alignment.Status == (int)LocalAlignmentStatus.Disabled,
                        "structured alignment status");
                    alignment.Size = 0;
                    Check(PatchNativeInterop.cis_patch_result_alignment(result, ref alignment, worker.Error,
                        (uint)worker.Error.Length) == -1, "alignment layout rejected");
                    summary.Size = 0;
                    Check(PatchNativeInterop.cis_patch_result_summary(result, ref summary, worker.Error, (uint)worker.Error.Length) == -1,
                        "summary layout rejected");
                    if (summary.DefectCount > 0)
                    {
                        Check(PatchNativeInterop.cis_patch_result_defects(result, null, 0, worker.Error, (uint)worker.Error.Length) == -3,
                            "short defect output rejected");
                    }

                    Check(cis_patch_result_log(result, null, 0, out uint needed) == -3 && needed >= 1, "log size query");
                    using (var target = new Mat(item.Alpha.Size(), MatType.CV_8UC1, Scalar.All(123)))
                    {
                        var before = Cv2.Sum(target);
                        Check(cis_patch_result_copy_image(result, 0, target.Data, 1, (ulong)target.Step(), worker.Error, (uint)worker.Error.Length) == -1 &&
                            Cv2.Sum(target) == before, "short image output rejected without writing");
                    }
                }
                // ABI v2 的核心 ROI 与外围搜索区必须独立：配准可使用外圈像素，
                // 但对外的缺陷/预览图仍应严格保持原理论零件大小。
                using (var contextAlpha = new Mat())
                using (var contextCis = new Mat())
                {
                    Cv2.CopyMakeBorder(item.Alpha, contextAlpha, 100, 100, 100, 100,
                        BorderTypes.Constant, Scalar.All(0));
                    Cv2.CopyMakeBorder(item.Cis, contextCis, 100, 100, 100, 100,
                        BorderTypes.Constant, Scalar.All(0));
                    var contextA = PatchNativeInterop.Image.Borrow(contextAlpha);
                    var contextC = PatchNativeInterop.Image.Borrow(contextCis);
                    var contextOptions = validOptions;
                    contextOptions.Alignment = 1;
                    contextOptions.Outputs = 3; // 请求原尺寸的边缘屏蔽图，检查核心尺寸。
                    contextOptions.CoreX = 100;
                    contextOptions.CoreY = 100;
                    contextOptions.CoreWidth = item.Alpha.Width;
                    contextOptions.CoreHeight = item.Alpha.Height;
                    PatchNativeInterop.Check(PatchNativeInterop.cis_patch_detect(worker.Handle, ref contextA,
                        ref contextC, ref contextOptions, out IntPtr contextPointer, worker.Error,
                        (uint)worker.Error.Length), worker.Error);
                    using (var contextResult = new PatchNativeInterop.ResultHandle(contextPointer))
                    {
                        var info = new PatchNativeInterop.AlignmentInfo { Size = 72 };
                        Check(PatchNativeInterop.cis_patch_result_alignment(contextResult, ref info,
                            worker.Error, (uint)worker.Error.Length) == 0 &&
                            info.Status != (int)LocalAlignmentStatus.Disabled, "alignment result from context");
                        using (var coreImage = PatchNativeInterop.ReadImage(contextResult, 4, worker.Error))
                        {
                            Check(coreImage.Size() == item.Alpha.Size(), "output excludes search padding");
                        }
                    }
                }
                GC.KeepAlive(item.Alpha);
                GC.KeepAlive(item.Cis);
            }
            Console.WriteLine("ABI CONTRACT: " + passed.Count + " PASS");
            return passed;
        }

        private static object MeasureWithoutSaving(Case item, AppConfig config)
        {
            var legacy = new List<double>();
            var native = new List<double>();
            using (var oldCache = new LegacyCache())
            using (var cache = new PatchSiftTemplateCache())
            using (var oldWorker = new LegacyWorker(oldCache))
            using (var worker = new PatchSiftWorker(cache))
            {
                // 先预热，再交替调用次序；不落盘、不统计首次模型/缓存创建，不以单次耗时宣称提速。
                Action runLegacy = () => LegacyDetector.Detect(item.Alpha, item.Cis, item.Threshold, config, null, null, null, oldWorker, item.Name, null);
                Action runNative = () => PatchDefectDetector.Detect(item.Alpha, item.Cis, item.Threshold, config, null, null, null, worker, item.Name, null);
                runLegacy();
                runNative();
                for (int i = 0; i < 6; i++)
                {
                    foreach (bool old in (i % 2 == 0 ? new[] { true, false } : new[] { false, true }))
                    {
                        var clock = Stopwatch.StartNew();
                        (old ? runLegacy : runNative)();
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
        private static bool Compare(object a, object b)
        {
            using (var first = JsonDocument.Parse(JsonSerializer.Serialize(a, Json)))
            using (var second = JsonDocument.Parse(JsonSerializer.Serialize(b, Json)))
            {
                return CompareElement(first.RootElement, second.RootElement);
            }
        }
        private static bool CompareElement(JsonElement a, JsonElement b)
        {
            if (a.ValueKind != b.ValueKind)
            {
                return false;
            }

            if (a.ValueKind == JsonValueKind.Number)
            {
                return Math.Abs(a.GetDouble() - b.GetDouble()) <= 1e-9;
            }

            if (a.ValueKind == JsonValueKind.Array)
            { var aa = a.EnumerateArray().ToArray(); var bb = b.EnumerateArray().ToArray(); return aa.Length == bb.Length && aa.Zip(bb, CompareElement).All(x => x); }
            if (a.ValueKind == JsonValueKind.Object)
            { foreach (var p in a.EnumerateObject()) { if (!b.TryGetProperty(p.Name, out var value) || !CompareElement(p.Value, value)) { return false; } } return a.EnumerateObject().Count() == b.EnumerateObject().Count(); }
            return a.ToString() == b.ToString();
        }
        private static List<Case> Synthetic()
        {
            var cases = new List<Case>();
            cases.Add(new Case { Name = "blank", Alpha = new Mat(600, 900, MatType.CV_8UC1, Scalar.All(0)), Cis = new Mat(600, 900, MatType.CV_8UC1, Scalar.All(0)), Threshold = 100 });
            var alpha = new Mat(800, 1100, MatType.CV_8UC1, Scalar.All(0));
            Cv2.Rectangle(alpha, new Rect(100, 100, 380, 400), Scalar.All(255), -1);
            Cv2.Rectangle(alpha, new Rect(160, 160, 240, 220), Scalar.All(0), -1);
            Cv2.Line(alpha, new Point(120, 600), new Point(900, 600), Scalar.All(255), 10);
            for (int i = 0; i < 8; i++)
            {
                Cv2.Circle(alpha, new Point(580 + (i % 3) * 140, 100 + (i / 3) * 140), 35 + i, Scalar.All(255), -1);
            }

            var cis = alpha.Clone();
            Cv2.Rectangle(cis, new Rect(110, 390, 65, 55), Scalar.All(0), -1);
            Cv2.Circle(cis, new Point(750, 500), 25, Scalar.All(255), -1);
            Cv2.Rectangle(cis, new Rect(470, 580, 16, 40), Scalar.All(0), -1);
            cases.Add(new Case { Name = "three_defects", Alpha = alpha, Cis = cis, Threshold = 100 });
            using (var matrix = Cv2.GetRotationMatrix2D(new Point2f(550, 400), .7, 1.005))
            {
                matrix.Set(0, 2, matrix.At<double>(0, 2) + 7);
                matrix.Set(1, 2, matrix.At<double>(1, 2) - 4);
                var shifted = new Mat();
                Cv2.WarpAffine(cis, shifted, matrix, alpha.Size(), InterpolationFlags.Cubic);
                cases.Add(new Case { Name = "shift_rotation", Alpha = alpha.Clone(), Cis = shifted, Threshold = 100 });
            }
            // 非连续 ROI + BGR 输入覆盖跨 ABI 步长和通道转换。
            var parent = new Mat(840, 1140, MatType.CV_8UC3, Scalar.All(0));
            using (var color = new Mat())
            {
                Cv2.CvtColor(cis, color, ColorConversionCodes.GRAY2BGR);
                using (var roi = new Mat(parent, new Rect(20, 20, 1100, 800)))
                {
                    color.CopyTo(roi);
                }
            }
            cases.Add(new Case { Name = "strided_bgr", Alpha = alpha.Clone(), Cis = new Mat(parent, new Rect(20, 20, 1100, 800)), Threshold = 100 });
            parent.Dispose();
            return cases;
        }
        private static unsafe List<Case> FromFrames(string directory, AppConfig config, string localProbePart = null,
            bool registrationContext = false, string frameOutput = null)
        {
            var result = new List<Case>();
            StitchedImageResult stitched = null;
            using (var stitcher = new ImageStitcher())
            {
                stitcher.StitchCompleted += (sender, item) => { if (stitched == null) { stitched = item; } };
                bool initialized = false;
                foreach (string path in Directory.GetFiles(directory).Where(x => x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x, StringComparer.Ordinal))
                {
                    using (var image = Cv2.ImRead(path, ImreadModes.Color))
                    {
                        if (!initialized)
                        { stitcher.Configure(image.Width, image.Height, (int)image.Step(), 24, config); initialized = true; }
                        var bytes = new byte[checked((int)(image.Step() * image.Height))];
                        Marshal.Copy(image.Data, bytes, 0, bytes.Length);
                        stitcher.ProcessOwnedFrame(bytes, image.Width, image.Height, (int)image.Step(), 24);
                    }
                    if (stitched != null)
                    {
                        break;
                    }
                }
            }
            if (stitched == null)
            {
                throw new Exception("No stitched segment in test input");
            }

            Console.WriteLine("STITCHED: " + stitched.EndQrText + " " + stitched.Width + "x" + stitched.Height);
            var layout = DebugLogParser.ParseForQrCode(config.DebugLogPath, stitched.EndQrText, config.TiffImageDir);
            if (layout == null)
            {
                throw new Exception("Missing layout record for " + stitched.EndQrText);
            }

            using (var raw = Cv2.ImRead(layout.TiffFullPath, ImreadModes.Unchanged))
            using (var alpha = new Mat())
            using (var tiff = new Mat(raw.Height, raw.Width, MatType.CV_8UC3))
            using (var cis = new Mat(stitched.Height, stitched.Width, MatType.CV_8UC3))
            {
                if (raw.Channels() != 4)
                {
                    throw new Exception("Raw layout must contain Alpha");
                }

                Cv2.ExtractChannel(raw, alpha, 3);
                for (int y = 0; y < raw.Height; y++)
                {
                    byte* s = (byte*)raw.Ptr(y), d = (byte*)tiff.Ptr(y);
                    for (int x = 0; x < raw.Width; x++, s += 4, d += 3)
                    {
                        float a = s[3] * (1f / 255f), inv = 1f - a;
                        for (int c = 0; c < 3; c++)
                        {
                            d[c] = s[3] == 255 ? s[c] : s[3] == 0 ? (byte)255 : (byte)(s[c] * a + 255f * inv);
                        }
                    }
                }
                Marshal.Copy(stitched.Data, 0, cis.Data, stitched.Data.Length);
                var anchor = new CisQrAnchor
                {
                    CenterX = stitched.EndQrCenterX,
                    GlobalCenterY = stitched.EndQrGlobalY,
                    SegmentStartGlobalY = stitched.SegmentStartGlobalY,
                    PixelWidth = stitched.EndQrPixelWidth,
                    PixelHeight = stitched.EndQrPixelHeight
                };
                var options = MarkAlignmentOptions.FromConfig(config);
                options.EnableWhiteInkInspection = false;
                using (var alignment = ImageAligner.ComputeTransform(cis, tiff, anchor, options, out int threshold, out string diagnostic))
                {
                    if (alignment?.GlobalTransform == null || alignment.GlobalTransform.Empty())
                    {
                        throw new Exception(diagnostic);
                    }

                    using (var warped = ImageAligner.WarpToTiffSpace(cis, alignment, tiff.Size()))
                    using (var flippedCis = new Mat())
                    using (var flippedAlpha = new Mat())
                    {
                        Cv2.Flip(warped, flippedCis, FlipMode.X);
                        Cv2.Flip(alpha, flippedAlpha, FlipMode.X);
                        if (frameOutput != null)
                        {
                            Directory.CreateDirectory(frameOutput);
                            Cv2.ImWrite(Path.Combine(frameOutput, "frame_alpha.png"), flippedAlpha);
                            Cv2.ImWrite(Path.Combine(frameOutput, "frame_raw.png"), flippedCis);
                        }
                        double ppm = config.LayoutDpi / 25.4;
                        int ox = (int)(config.LayoutOriginXmm * ppm), oy = (int)(config.LayoutOriginYmm * ppm);
                        foreach (var part in layout.Parts)
                        {
                            if (localProbePart != null && part.HotInkTaskID != localProbePart)
                            {
                                continue;
                            }
                            if (part.HotInkTaskID?.Contains("QRCode") == true)
                            {
                                continue;
                            }

                            int x = Math.Max(0, (int)(ox + part.RelativeTopLeftX * ppm)), y = Math.Max(0, (int)(oy + part.RelativeTopLeftY * ppm));
                            int w = Math.Min((int)((part.RelativeBottomRightX - part.RelativeTopLeftX) * ppm), tiff.Width - x), h = Math.Min((int)((part.RelativeBottomRightY - part.RelativeTopLeftY) * ppm), tiff.Height - y);
                            if (w <= 0 || h <= 0)
                            {
                                continue;
                            }

                            var roi = new Rect(x, y, w, h);
                            int padding = localProbePart == null && !registrationContext ? 0 : Math.Max(16, (int)Math.Min(2000,
                                Math.Ceiling(Math.Max(0, config.LocalAlignmentPaddingMm) * ppm)));
                            int left = Math.Max(0, roi.X - padding), top = Math.Max(0, roi.Y - padding);
                            int right = Math.Min(tiff.Width, roi.Right + padding);
                            int bottom = Math.Min(tiff.Height, roi.Bottom + padding);
                            var search = new Rect(left, top, right - left, bottom - top);
                            using (var a = new Mat(flippedAlpha, search))
                            using (var c = new Mat(flippedCis, search))
                            {
                                result.Add(new Case { Name = part.HotInkTaskID, Alpha = a.Clone(), Cis = c.Clone(),
                                    Core = new Rect(roi.X - left, roi.Y - top, roi.Width, roi.Height),
                                    Threshold = Math.Max(0, Math.Min(255, threshold + config.DefectCisThreshOffset)) });
                            }
                        }
                    }
                }
            }
            return result;
        }
    }
}
