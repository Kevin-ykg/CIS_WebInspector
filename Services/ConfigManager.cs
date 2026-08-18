using System;
using System.IO;
using System.Text.Json;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 应用配置的进程内单例入口。配置文件固定保存在程序目录，首次访问时延迟加载；
    /// 文件缺失或内容无效时回退到 <see cref="AppConfig"/> 默认值，避免配置故障直接阻断启动。
    /// 设置界面通常在设备空闲时调用 <see cref="SaveConfig"/>，本类不承担运行中并发写入协调。
    /// </summary>
    public static class ConfigManager
    {
        private const string SquareMillimeterAreaUnit = "mm2";
        private const double DefaultLayoutDpi = 300.0;
        private static readonly object ConfigSync = new object();
        // JsonSerializerOptions 首次使用后即按只读方式并发复用，避免每次保存重新构建元数据选项。
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static AppConfig _instance;
        // 跟随可执行文件部署，现场复制整套目录即可携带相同参数。
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_config.json");

        /// <summary>首次访问时加载配置；返回的实例在进程内保持同一引用，供界面绑定和服务读取。</summary>
        public static AppConfig Config
        {
            get
            {
                if (_instance == null)
                {
                    lock (ConfigSync)
                    {
                        if (_instance == null)
                            LoadOrCreateConfig();
                    }
                }
                return _instance;
            }
        }

        private static void LoadOrCreateConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    bool requiresLegacyAreaMigration = UsesLegacyPixelAreaThresholds(json);
                    _instance = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
                    if (_instance == null)
                        _instance = new AppConfig();

                    if (requiresLegacyAreaMigration)
                    {
                        // 旧版配置用原图 px² 表示面积。升级时按 LayoutDpi 换算为 mm²，
                        // 保证检测灵敏度不因单位切换而变化；单位标识随后持久化，迁移只执行一次。
                        double pixelsPerMm = GetPixelsPerMm(_instance.LayoutDpi);
                        _instance.DefectAreaThreshInner = Math.Round(
                            _instance.DefectAreaThreshInner / (pixelsPerMm * pixelsPerMm),
                            3,
                            MidpointRounding.AwayFromZero);
                        _instance.DefectAreaThreshOuter = Math.Round(
                            _instance.DefectAreaThreshOuter / (pixelsPerMm * pixelsPerMm),
                            3,
                            MidpointRounding.AwayFromZero);
                        _instance.DefectAreaThresholdUnit = SquareMillimeterAreaUnit;

                        try
                        {
                            string migratedJson = JsonSerializer.Serialize(_instance, SerializerOptions);
                            File.WriteAllText(ConfigPath, migratedJson);
                            System.Diagnostics.Debug.WriteLine(
                                "缺陷面积阈值已由旧版 px² 自动迁移为 mm²。");
                        }
                        catch (Exception ex)
                        {
                            // 写回失败不影响本次运行；内存中的配置已经完成正确换算。
                            System.Diagnostics.Debug.WriteLine($"面积阈值迁移后写回失败: {ex.Message}");
                        }
                    }
                    else
                    {
                        // 接受 mm2/mm²/mm^2 等人工写法，保存时统一规范为机器友好的 mm2。
                        _instance.DefectAreaThresholdUnit = SquareMillimeterAreaUnit;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"读取配置失败: {ex.Message}，将使用默认配置。");
                    _instance = new AppConfig();
                }
            }
            else
            {
                _instance = new AppConfig();
                try
                {
                    // 首次运行把代码默认值落盘，后续设置界面与人工检查共享同一份基线。
                    string json = JsonSerializer.Serialize(_instance, SerializerOptions);
                    File.WriteAllText(ConfigPath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"创建默认配置失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 旧配置没有面积单位字段，数值语义为原图 px²；新版明确保存 mm2，避免重复换算。
        /// </summary>
        private static bool UsesLegacyPixelAreaThresholds(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(
                       json,
                       new JsonDocumentOptions
                       {
                           CommentHandling = JsonCommentHandling.Skip,
                           AllowTrailingCommas = true
                       }))
            {
                if (!document.RootElement.TryGetProperty(
                        nameof(AppConfig.DefectAreaThresholdUnit),
                        out JsonElement unitElement))
                {
                    return true;
                }

                string unit = unitElement.ValueKind == JsonValueKind.String
                    ? unitElement.GetString()
                    : null;
                return !string.Equals(unit, "mm2", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(unit, "mm²", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(unit, "mm^2", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static double GetPixelsPerMm(double layoutDpi)
        {
            double effectiveDpi = layoutDpi > 0 && !double.IsNaN(layoutDpi) && !double.IsInfinity(layoutDpi)
                ? layoutDpi
                : DefaultLayoutDpi;
            return effectiveDpi / 25.4;
        }

        /// <summary>把当前内存配置写回 app_config.json；失败仅记录调试信息。</summary>
        public static void SaveConfig()
        {
            if (_instance == null) return;
            
            try
            {
                // 保存失败只记录诊断信息，不替换当前内存配置，避免运行中的服务突然失去参数。
                lock (ConfigSync)
                {
                    string json = JsonSerializer.Serialize(_instance, SerializerOptions);
                    File.WriteAllText(ConfigPath, json);
                }
                System.Diagnostics.Debug.WriteLine("配置已成功保存到本地。");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            }
        }
    }
}
