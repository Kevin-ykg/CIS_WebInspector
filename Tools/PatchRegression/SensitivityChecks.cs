using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using OpenCvSharp;

namespace PatchRegression
{
    /// <summary>
    /// 0.6 mm 缺陷的隔离灵敏度实验。完美对准的合成图只能说明阈值链路是否可能放行
    /// 该尺寸，不能代表真实膜片的检出率、误检率或局部配准后的能力。
    /// </summary>
    internal static class SensitivityChecks
    {
        private sealed class Variant
        {
            public string Name { get; set; }
            public AppConfig Config { get; set; }
        }

        private sealed class ResultRow
        {
            public string Variant { get; set; }
            public string Sample { get; set; }
            public int Inner { get; set; }
            public int Outer { get; set; }
            public int Fine { get; set; }
            public bool IsPass { get; set; }
            public string Error { get; set; }
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true
        };

        public static int Run(string root, string configPath, string outputName)
        {
            string source = configPath ?? Path.Combine(root, "bin", "x64", "Debug", "net48", "app_config.json");
            var baseline = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(source));
            if (baseline == null || baseline.LayoutDpi <= 0)
            {
                throw new InvalidOperationException("配置缺失或 LayoutDpi 无效。");
            }

            // 不改动生产配置。逐层解除面积、形态学与采样约束，便于定位哪一道门槛吞掉小缺陷。
            var variants = new List<Variant> { new Variant { Name = "configured", Config = Copy(baseline) } };
            var areaOnly = Copy(baseline);
            areaOnly.DefectAreaThreshInner = .15;
            areaOnly.DefectAreaThreshOuter = .15;
            variants.Add(new Variant { Name = "area_only", Config = areaOnly });
            var detail = Copy(areaOnly);
            detail.DefectToleranceInner = 0;
            detail.DefectToleranceOuter = 0;
            detail.DefectEdgeExclusionSmall = 0;
            detail.DefectEdgeExclusionThick = 0;
            variants.Add(new Variant { Name = "relaxed_at_03", Config = Copy(detail) });
            detail.DefectDetectScale = .5;
            variants.Add(new Variant { Name = "relaxed_at_05", Config = Copy(detail) });
            detail.DefectDetectScale = 1;
            variants.Add(new Variant { Name = "isolated_detail", Config = detail });
            var fine = Copy(detail);
            fine.EnableFineLineBreakDetection = true;
            fine.FineLineMinBreakLengthMm = .5;
            fine.FineLineMaxWidthMm = 1;
            fine.DefectEdgeExclusionSmall = .25;
            fine.DefectEdgeExclusionThick = .25;
            variants.Add(new Variant { Name = "isolated_fine", Config = fine });

            int side = Math.Max(1, (int)Math.Round(.6 * baseline.LayoutDpi / 25.4));
            var rows = new List<ResultRow>();
            using (var alpha = new Mat(600, 900, MatType.CV_8UC1, Scalar.All(0)))
            {
                Cv2.Rectangle(alpha, new Rect(100, 90, 400, 360), Scalar.All(255), -1);
                // 细线与主实心区域隔开；断口两侧有真实结构，避免把线端误当断裂。
                Cv2.Line(alpha, new Point(130, 520), new Point(750, 520), Scalar.All(255), 5);
                using (var cache = new PatchSiftTemplateCache())
                using (var worker = new PatchSiftWorker(cache))
                {
                    foreach (Variant variant in variants)
                    {
                        variant.Config.EnableSiftLocalAlign = false;
                        variant.Config.SaveCroppedImages = false;
                        foreach (string sample in new[] { "clean", "inner_square", "outer_square", "fine_gap" })
                        {
                            using (var cis = alpha.Clone())
                            {
                                switch (sample)
                                {
                                    case "inner_square":
                                        Cv2.Rectangle(cis, new Rect(280, 260, side, side), Scalar.All(0), -1);
                                        break;
                                    case "outer_square":
                                        Cv2.Rectangle(cis, new Rect(620, 260, side, side), Scalar.All(255), -1);
                                        break;
                                    case "fine_gap":
                                        Cv2.Rectangle(cis, new Rect(440, 515, side, 11), Scalar.All(0), -1);
                                        break;
                                }

                                var row = new ResultRow { Variant = variant.Name, Sample = sample };
                                try
                                {
                                    PatchDefectResult result = PatchDefectDetector.Detect(alpha, cis, 128,
                                        variant.Config, null, null, null, worker, sample, null);
                                    row.Inner = result.InnerDefectCount;
                                    row.Outer = result.OuterDefectCount;
                                    row.Fine = result.FineLineBreakCount;
                                    row.IsPass = result.IsPass;
                                }
                                catch (Exception ex)
                                {
                                    row.Error = ex.ToString();
                                }
                                rows.Add(row);
                                Console.WriteLine($"{row.Variant,-17} {row.Sample,-13} inner={row.Inner} outer={row.Outer} fine={row.Fine} pass={row.IsPass} {row.Error}");
                            }
                        }
                    }
                }
            }

            string output = Path.Combine(root, "obj", "PatchNativeValidation", outputName);
            Directory.CreateDirectory(output);
            // 只断言隔离配置在理想输入上的代码路径，不断言现场参数必然能检出。
            bool cleanControl = rows.Where(r => r.Sample == "clean").All(r =>
                r.Error == null && r.IsPass && r.Inner == 0 && r.Outer == 0 && r.Fine == 0);
            bool isolatedInterior = rows.Any(r => r.Variant == "isolated_detail" &&
                r.Sample == "inner_square" && r.Error == null && r.Inner > 0 && !r.IsPass);
            bool isolatedExterior = rows.Any(r => r.Variant == "isolated_detail" &&
                r.Sample == "outer_square" && r.Error == null && r.Outer > 0 && !r.IsPass);
            File.WriteAllText(Path.Combine(output, "sensitivity.json"), JsonSerializer.Serialize(new
            {
                SourceConfig = source,
                SyntheticOnly = true,
                NominalDefectSideMm = .6,
                RasterizedSideOriginalPx = side,
                Note = "理想二值图与零位移夹具；不能推断真实产线检出率或误检率。",
                SyntheticChecks = new { CleanControl = cleanControl, IsolatedInterior = isolatedInterior,
                    IsolatedExterior = isolatedExterior },
                Variants = variants.Select(v => new
                {
                    v.Name,
                    v.Config.DefectDetectScale,
                    v.Config.DefectAreaThreshInner,
                    v.Config.DefectAreaThreshOuter,
                    v.Config.DefectToleranceInner,
                    v.Config.DefectToleranceOuter,
                    v.Config.DefectEdgeExclusionSmall,
                    v.Config.DefectEdgeExclusionThick,
                    v.Config.EnableFineLineBreakDetection,
                    v.Config.FineLineMinBreakLengthMm,
                    v.Config.FineLineMaxWidthMm
                }),
                Results = rows
            }, Json));
            return rows.Any(r => r.Error != null) || !cleanControl || !isolatedInterior || !isolatedExterior ? 1 : 0;
        }

        private static AppConfig Copy(AppConfig config) =>
            JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, Json));
    }
}
