using System;
using System.IO;
using System.Text.Json;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 应用配置的进程内单例入口。配置文件固定保存在程序目录，首次访问时延迟加载；
    /// 文件缺失或内容无效时回退到 <see cref="AppConfig"/> 默认值，避免配置故障直接阻断启动。
    /// 设置界面通过 <see cref="ApplyAndSave"/> 提交完整编辑结果；采集与检测只使用独立快照。
    /// </summary>
    public static class ConfigManager
    {
        private const string SquareMillimeterAreaUnit = "mm2";
        private const string MillimeterLengthUnit = "mm";
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

        /// <summary>
        /// 创建与全局配置完全断开的深拷贝。采集和检测作业只读取该快照，
        /// 参数窗口后续保存的新值不会在一次运行过程中改变算法条件。
        /// </summary>
        public static AppConfig CaptureSnapshot()
        {
            AppConfig current = Config;
            lock (ConfigSync)
            {
                return CloneConfig(current);
            }
        }

        /// <summary>
        /// 在同一临界区内应用设置窗口的完整编辑结果并持久化。
        /// 保持全局对象引用不变，避免已有 WPF 绑定失效；后台服务不直接持有该对象。
        /// </summary>
        public static void ApplyAndSave(AppConfig updatedConfig)
        {
            if (updatedConfig == null)
                throw new ArgumentNullException(nameof(updatedConfig));

            AppConfig globalConfig = Config;
            lock (ConfigSync)
            {
                // 先完成深拷贝和反序列化校验，再修改全局对象，避免无效输入只更新部分字段。
                AppConfig validatedSnapshot = CloneConfig(updatedConfig);
                foreach (System.Reflection.PropertyInfo property in typeof(AppConfig).GetProperties())
                {
                    if (property.CanRead && property.CanWrite)
                        property.SetValue(globalConfig, property.GetValue(validatedSnapshot));
                }

                WriteConfigFile(globalConfig);
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
                    bool requiresLegacyLengthMigration = UsesLegacyPixelLengthThresholds(json);
                    _instance = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
                    if (_instance == null)
                        _instance = new AppConfig();

                    bool configMigrated = false;
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
                        configMigrated = true;
                    }
                    else
                    {
                        // 接受 mm2/mm²/mm^2 等人工写法，保存时统一规范为机器友好的 mm2。
                        _instance.DefectAreaThresholdUnit = SquareMillimeterAreaUnit;
                    }

                    if (requiresLegacyLengthMigration)
                    {
                        // 旧版四个长度参数保存的是 TIFF 对齐空间中的原图像素数。
                        // 只在加载旧配置时除以 px/mm；算法入口再按当前 DPI 和检测缩放率换回像素，
                        // 因而升级前后的形态学核及边缘屏蔽宽度保持一致。
                        double pixelsPerMm = GetPixelsPerMm(_instance.LayoutDpi);
                        _instance.DefectToleranceInner = ConvertLegacyPixelsToMm(
                            _instance.DefectToleranceInner,
                            pixelsPerMm);
                        _instance.DefectToleranceOuter = ConvertLegacyPixelsToMm(
                            _instance.DefectToleranceOuter,
                            pixelsPerMm);
                        _instance.DefectEdgeExclusionThick = ConvertLegacyPixelsToMm(
                            _instance.DefectEdgeExclusionThick,
                            pixelsPerMm);
                        _instance.DefectEdgeExclusionSmall = ConvertLegacyPixelsToMm(
                            _instance.DefectEdgeExclusionSmall,
                            pixelsPerMm);
                        _instance.DefectLengthThresholdUnit = MillimeterLengthUnit;
                        configMigrated = true;
                    }
                    else
                    {
                        _instance.DefectLengthThresholdUnit = MillimeterLengthUnit;
                    }

                    if (configMigrated)
                    {
                        try
                        {
                            string migratedJson = JsonSerializer.Serialize(_instance, SerializerOptions);
                            File.WriteAllText(ConfigPath, migratedJson);
                            System.Diagnostics.Debug.WriteLine(
                                "缺陷面积/长度参数已由旧版像素单位自动迁移为毫米单位。");
                        }
                        catch (Exception ex)
                        {
                            // 写回失败不影响本次运行；内存中的配置已经完成正确换算。
                            System.Diagnostics.Debug.WriteLine($"缺陷参数迁移后写回失败: {ex.Message}");
                        }
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

        /// <summary>
        /// 旧配置没有长度单位字段，四个形态学/屏蔽参数的数值语义为原图 px；
        /// 新版明确保存 mm，确保不同检测缩放率下物理含义一致。
        /// </summary>
        private static bool UsesLegacyPixelLengthThresholds(string json)
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
                        nameof(AppConfig.DefectLengthThresholdUnit),
                        out JsonElement unitElement))
                {
                    return true;
                }

                string unit = unitElement.ValueKind == JsonValueKind.String
                    ? unitElement.GetString()
                    : null;
                return !string.Equals(unit, MillimeterLengthUnit, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static double ConvertLegacyPixelsToMm(double pixels, double pixelsPerMm)
        {
            if (pixels <= 0 || double.IsNaN(pixels) || double.IsInfinity(pixels))
                return 0;

            return Math.Round(
                pixels / pixelsPerMm,
                3,
                MidpointRounding.AwayFromZero);
        }

        private static double GetPixelsPerMm(double layoutDpi)
        {
            double effectiveDpi = layoutDpi > 0 && !double.IsNaN(layoutDpi) && !double.IsInfinity(layoutDpi)
                ? layoutDpi
                : DefaultLayoutDpi;
            return effectiveDpi / 25.4;
        }

        private static AppConfig CloneConfig(AppConfig source)
        {
            string json = JsonSerializer.Serialize(source, SerializerOptions);
            return JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();
        }

        private static void WriteConfigFile(AppConfig config)
        {
            string json = JsonSerializer.Serialize(config, SerializerOptions);
            File.WriteAllText(ConfigPath, json);
        }

    }
}
