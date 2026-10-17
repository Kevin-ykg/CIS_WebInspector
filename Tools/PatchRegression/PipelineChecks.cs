using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using OpenCvSharp;

namespace PatchRegression
{
    /// <summary>
    /// 用确定的合成膜片验证托管编排：Alpha 白底合成、ROI 翻转、缺陷尺寸日志、保存开关与提前退出。
    /// 与算法回归互补；不把合成样本通过视为现场检出率验收。
    /// </summary>
    internal static class PipelineChecks
    {
        private sealed class Log : IAppLogger
        {
            public readonly List<string> Measurements = new List<string>();
            public void Write(string message)
            {
                if (message.StartsWith("  [缺陷尺寸]", StringComparison.Ordinal))
                {
                    Measurements.Add(message);
                }
            }
            public void WriteDiagnostic(string message) { }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static string Hash(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            }
        }

        public static int Run(string root, string outputName, string comparePath)
        {
            string output = Path.Combine(root, "obj", "PipelineValidation", outputName);
            Directory.CreateDirectory(output);
            var snapshots = new List<object>();
            int checks = 0;
            using (var layout = new Mat(1010, 600, MatType.CV_8UC4, new Scalar(255, 255, 255, 0)))
            using (var cis = new Mat(1020, 620, MatType.CV_8UC1, Scalar.All(40)))
            {
                foreach (int y in new[] { 50, 970 })
                {
                    foreach (int x in new[] { 90, 160, 190, 285, 410, 440, 535 })
                    {
                        Cv2.Circle(layout, new Point(x, y), 10, new Scalar(0, 0, 0, 255), -1);
                        Cv2.Circle(cis, new Point(x + 5, y + 4), 10, Scalar.All(210), -1);
                    }
                }
                Cv2.Rectangle(layout, new Rect(250, 480, 100, 100), new Scalar(30, 60, 90, 255), -1);
                Cv2.Rectangle(layout, new Rect(270, 490, 12, 12), new Scalar(30, 60, 90, 128), -1);
                Cv2.Rectangle(cis, new Rect(255, 484, 100, 100), Scalar.All(210), -1);
                Cv2.Rectangle(cis, new Rect(295, 524, 10, 10), Scalar.All(40), -1);
                string imagePath = Path.Combine(output, "layout.png");
                string logPath = Path.Combine(output, "Debug.log");
                Cv2.ImWrite(imagePath, layout);
                File.WriteAllText(logPath, JsonSerializer.Serialize(new[] { new
                {
                    cuttingInput = new
                    {
                        qr = "HANDOVER_FIXTURE", formattingFilename = "layout.png",
                        sourceFileLocation = new[] { new
                        {
                            hotInkTaskID = "fixture_part", relativeTopLeftX = 220, relativeTopLeftY = 380,
                            relativeBottomRightX = 400, relativeBottomRightY = 560
                        } }
                    }
                } }));
                byte[] pixels = new byte[checked((int)(cis.Step() * cis.Rows))];
                Marshal.Copy(cis.Data, pixels, 0, pixels.Length);
                var input = new StitchedImageResult
                {
                    Data = pixels,
                    Width = cis.Cols,
                    Height = cis.Rows,
                    Stride = (int)cis.Step(),
                    BitsPerPixel = 8,
                    StartQrText = "PREVIOUS",
                    EndQrText = "HANDOVER_FIXTURE",
                    SegmentStartGlobalY = 10000,
                    EndQrGlobalY = 10974,
                    EndQrCenterY = 974,
                    EndQrCenterX = 44,
                    EndQrPixelWidth = 60,
                    EndQrPixelHeight = 60
                };
                string inputHash = Hash(pixels);
                foreach (string name in new[] { "saved", "unsaved", "missing-layout", "cancelled", "no-alpha" })
                {
                    var config = new AppConfig
                    {
                        DebugLogPath = logPath,
                        TiffImageDir = output,
                        CroppedOutputDir = "results_" + name,
                        LayoutDpi = 25.4,
                        LayoutOriginXmm = 0,
                        LayoutOriginYmm = 0,
                        MarkTiffHeightMm = 1000,
                        MarkTiffTopCenterYmm = 50,
                        MarkTiffBottomOffsetMm = 30,
                        MarkCisRowSpacingMm = 920,
                        MinCircularityTiff = .75,
                        MinCircularityCis = .75,
                        EnableWhiteInkInspection = true,
                        WhiteInkNormalGray = 210,
                        WhiteInkStreakStdDevThreshold = 12,
                        EnableSideMarkNonlinearAlignment = false,
                        EnableSiftLocalAlign = false,
                        EnableFineLineBreakDetection = false,
                        DefectDetectScale = 1,
                        DefectMaxParallelism = 1,
                        DefectCisThreshOffset = 0,
                        DefectToleranceInner = 0,
                        DefectToleranceOuter = 0,
                        DefectEdgeExclusionSmall = 0,
                        DefectEdgeExclusionThick = 0,
                        SaveCroppedImages = name != "unsaved",
                        SaveDefectResultImages = true
                    };
                    if (name == "missing-layout")
                    {
                        config.DebugLogPath = "";
                    }
                    if (name == "no-alpha")
                    {
                        using (var bgr = new Mat())
                        {
                            Cv2.CvtColor(layout, bgr, ColorConversionCodes.BGRA2BGR);
                            Cv2.ImWrite(imagePath, bgr);
                        }
                    }
                    var log = new Log();
                    var runner = new InspectionJobRunner(output, log);
                    var result = runner.Run(input, config, new CancellationToken(name == "cancelled"));
                    if (name == "cancelled")
                    {
                        Require(result.Status == InspectionJobStatus.Cancelled, "Cancelled result lost");
                    }
                    else if (name == "missing-layout")
                    {
                        Require(result.Status == InspectionJobStatus.Skipped, "Missing layout must skip");
                        Require(result.WhiteInkPreviewBytes?.Length > 0, "White ink preview must survive skip");
                        checks++;
                    }
                    else
                    {
                        // 锁定已有行为，而非认可无 Alpha 为合格：当前只裁图，结果列表确实为空。
                        Require(result.Status == InspectionJobStatus.Completed, name + ": " + result.Message);
                        Require(result.TotalParts == (name == "no-alpha" ? 0 : 1), "Unexpected part count");
                        Require(result.GlobalImageBytes?.Length > 0, "Global UI preview missing");
                        if (name == "saved")
                        {
                            Require(result.FailCount == 1 && log.Measurements.Count > 0, "Fixture defect lost");
                        }
                        if (name == "unsaved")
                        {
                            Require(!Directory.Exists(result.OutputDirectory), "Save switch created image directory");
                        }
                        checks += 3;
                    }
                    Require(Hash(pixels) == inputHash, "Stitched pixels were modified");
                    checks += 2;
                    // 只比较确定的结果和编码图像；耗时、时间戳、输出绝对路径不参与一致性判断。
                    var images = result.OutputDirectory != null && Directory.Exists(result.OutputDirectory)
                        ? Directory.GetFiles(result.OutputDirectory).Where(p => p.EndsWith(".jpg") || p.EndsWith(".png"))
                            .OrderBy(p => p, StringComparer.Ordinal).ToDictionary(Path.GetFileName, p => Hash(File.ReadAllBytes(p)))
                        : new Dictionary<string, string>();
                    snapshots.Add(new
                    {
                        name,
                        result.Status,
                        result.IssueCode,
                        result.TotalParts,
                        result.PassCount,
                        result.FailCount,
                        result.ProcessingErrorCount,
                        result.AlignmentMode,
                        result.AlignmentQualityStatus,
                        white = result.WhiteInkInspection?.Status,
                        preview = Hash(result.WhiteInkPreviewBytes),
                        global = Hash(result.GlobalImageBytes),
                        measurements = log.Measurements,
                        images
                    });
                }
            }
            string json = JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true });
            string snapshotPath = Path.Combine(output, "snapshot.json");
            File.WriteAllText(snapshotPath, json);
            if (!string.IsNullOrEmpty(comparePath))
            {
                Require(File.ReadAllText(comparePath) == json, "Pipeline snapshot differs: " + snapshotPath);
                checks++;
            }
            Console.WriteLine($"PIPELINE {checks} PASS; snapshot={snapshotPath}");
            return 0;
        }
    }
}
