using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CIS_WebInspector.Models;
using OpenCvSharp;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "compare") return Compare(args[1], args[2]);
            if (args.Length == 2 && args[0] == "contracts") return NativeContractChecks.Run(args[1]);
            if (args.Length == 4 && args[0] == "image-contracts") return NativeContractChecks.CheckImage(args[1], args[2], args[3]);
            if (args.Length >= 6 && args[0] == "capture") return Capture(args);
            Console.Error.WriteLine("capture <config> <models> <image-root> <report.json> <legacy|native> [repeat]\ncompare <baseline.json> <candidate.json>");
            return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
    }

    private static int Capture(string[] args)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(args[1]), Json);
        bool legacy = args[5] == "legacy";
        if (!legacy && args[5] != "native") throw new ArgumentException("Unknown backend");
        int repeat = args.Length > 6 ? int.Parse(args[6]) : 1;
        if (repeat < 1) throw new ArgumentException("repeat must be positive");
        string root = Path.GetFullPath(args[3]);
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("No images");
        var report = new Report { Backend = args[5], CreatedUtc = DateTime.UtcNow, ImageRoot = root,
            ConfigJson = File.ReadAllText(args[1]), ConfigHash = Hash(args[1]), OpenCvBuild = Cv2.GetBuildInformation(),
            NativeDllHash = legacy ? null : Hash(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CISVisionCore.dll")),
            Models = Directory.GetFiles(args[2]).Where(p => p.EndsWith(".prototxt") || p.EndsWith(".caffemodel")).ToDictionary(Path.GetFileName, Hash) };
        // 两个实现仅共用数据模型。Legacy 源码是迁移前快照，不编译进正式 WPF 程序。
        var old = legacy ? new QrRegression.Legacy.QrCodeDetector(args[2]) : null;
        var current = legacy ? null : new CIS_WebInspector.Services.QrCodeDetector(args[2]);
        Func<byte[], int, int, int, int, QrDetectionResult> detect = legacy ? old.Detect : current.Detect;
        Func<string> error = () => legacy ? old.LastError : current.LastError;
        Func<string> strategy = () => legacy ? old.LastDecodeStrategy : current.LastDecodeStrategy;
        Func<int> attempts = () => legacy ? old.LastDecodeAttemptCount : current.LastDecodeAttemptCount;
        using (IDisposable owner = legacy ? (IDisposable)old : current)
        {
            if (legacy) old.Configure(config); else current.Configure(config);
            if (!(legacy ? old.Initialize() : current.Initialize())) throw new InvalidOperationException(error());
            for (int iteration = 0; iteration < repeat; iteration++)
            {
                byte[] tail = null;
                string previousDirectory = null;
                int previousStride = 0;
                foreach (string file in files)
                {
                    using (var image = Cv2.ImRead(file, ImreadModes.Unchanged))
                    using (var resized = new Mat())
                    {
                        if (image.Empty() || image.Depth() != MatType.CV_8U) throw new InvalidOperationException("Invalid input: " + file);
                        int factor = Math.Max(1, config.DownscaleFactor);
                        Mat source = image;
                        if (factor > 1) { Cv2.Resize(image, resized, new Size(image.Width / factor, image.Height / factor), 0, 0, InterpolationFlags.Area); source = resized; }
                        int stride = source.Width * source.Channels();
                        var bytes = new byte[stride * source.Height];
                        for (int y = 0; y < source.Height; y++) Marshal.Copy(source.Ptr(y), bytes, y * stride, stride);
                        string relative = file.Substring(root.TrimEnd('\\').Length).TrimStart('\\');
                        string hash = Hash(file);
                        Func<byte[], string, Record> run = (data, kind) =>
                        {
                            var timer = Stopwatch.StartNew();
                            var hit = detect(data, source.Width, data.Length / stride, stride, source.Channels() * 8);
                            timer.Stop();
                            var record = new Record { File = relative, Sha256 = hash, Kind = kind, Iteration = iteration,
                                Found = hit.Found, Text = hit.DecodedText, X = hit.CenterX, Y = hit.CenterY, Width = hit.PixelWidth, Height = hit.PixelHeight,
                                Attempts = attempts(), Strategy = strategy(), Error = error(), Ms = timer.Elapsed.TotalMilliseconds };
                            report.Records.Add(record);
                            Console.WriteLine($"{iteration} {kind} {relative} found={record.Found} text={record.Text} attempts={record.Attempts} ms={record.Ms:F1} error={record.Error}");
                            return record;
                        };
                        var single = run(bytes, "single");
                        int overlap = Math.Max(0, Math.Min(source.Height, config.BaseOverlapRows / factor));
                        string directory = Path.GetDirectoryName(file);
                        if (!single.Found && tail != null && previousDirectory == directory && previousStride == stride && overlap > 0)
                        {
                            var combined = new byte[tail.Length + overlap * stride];
                            Buffer.BlockCopy(tail, 0, combined, 0, tail.Length);
                            Buffer.BlockCopy(bytes, 0, combined, tail.Length, overlap * stride);
                            run(combined, "overlap");
                        }
                        tail = new byte[overlap * stride];
                        Buffer.BlockCopy(bytes, bytes.Length - tail.Length, tail, 0, tail.Length);
                        previousDirectory = directory; previousStride = stride;
                    }
                }
                report.PrivateBytesAfterRounds.Add(Process.GetCurrentProcess().PrivateMemorySize64);
            }
        }
        report.PeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
        string output = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonSerializer.Serialize(report, Json));
        Console.WriteLine($"Saved {report.Records.Count} calls, found={report.Records.Count(x => x.Found)}, errors={report.Records.Count(x => !string.IsNullOrEmpty(x.Error))}");
        return report.Records.Any(x => !string.IsNullOrEmpty(x.Error)) ? 1 : 0;
    }

    private static int Compare(string first, string second)
    {
        var a = JsonSerializer.Deserialize<Report>(File.ReadAllText(first), Json);
        var b = JsonSerializer.Deserialize<Report>(File.ReadAllText(second), Json);
        if (a.ConfigHash != b.ConfigHash || a.Models.Count != b.Models.Count || a.Models.Any(x => !b.Models.TryGetValue(x.Key, out string v) || x.Value != v))
            throw new InvalidOperationException("Configuration/models differ");
        Func<Record, string> key = r => $"{r.Iteration}|{r.File}|{r.Kind}";
        var expected = a.Records.ToDictionary(key); var actual = b.Records.ToDictionary(key);
        int differences = 0;
        foreach (string id in expected.Keys.Union(actual.Keys))
        {
            if (!expected.TryGetValue(id, out Record x) || !actual.TryGetValue(id, out Record y)) { differences++; Console.WriteLine("MISSING " + id); continue; }
            if (x.Sha256 == y.Sha256 && x.Found == y.Found && x.Text == y.Text && x.X == y.X && x.Y == y.Y &&
                Math.Abs(x.Width - y.Width) <= 1e-6 && Math.Abs(x.Height - y.Height) <= 1e-6 && x.Attempts == y.Attempts && x.Strategy == y.Strategy && x.Error == y.Error) continue;
            differences++;
            Console.WriteLine($"DIFF {id}: found={x.Found}/{y.Found} text={x.Text}/{y.Text} center={x.X},{x.Y}/{y.X},{y.Y} size={x.Width:F6},{x.Height:F6}/{y.Width:F6},{y.Height:F6} attempts={x.Attempts}/{y.Attempts}\n{x.Strategy}\n{y.Strategy}");
        }
        Console.WriteLine($"Compared {expected.Count}/{actual.Count}, differences={differences}; total ms {a.Records.Sum(x => x.Ms):F1}/{b.Records.Sum(x => x.Ms):F1}");
        return differences == 0 ? 0 : 1;
    }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    public sealed class Report
    {
        public DateTime CreatedUtc { get; set; }
        public string Backend { get; set; }
        public string ImageRoot { get; set; }
        public string ConfigHash { get; set; }
        public string ConfigJson { get; set; }
        public string OpenCvBuild { get; set; }
        public string NativeDllHash { get; set; }
        public Dictionary<string, string> Models { get; set; }
        public long PeakWorkingSet { get; set; }
        public List<long> PrivateBytesAfterRounds { get; set; } = new List<long>();
        public List<Record> Records { get; set; } = new List<Record>();
    }
    public sealed class Record
    {
        public string File { get; set; }
        public string Sha256 { get; set; }
        public string Kind { get; set; }
        public int Iteration { get; set; }
        public bool Found { get; set; }
        public string Text { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int Attempts { get; set; }
        public string Strategy { get; set; }
        public string Error { get; set; }
        public double Ms { get; set; }
    }
}
