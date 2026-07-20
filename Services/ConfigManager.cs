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
                    LoadOrCreateConfig();
                }
                return _instance;
            }
        }

        private static void LoadOrCreateConfig()
        {
            // 允许人工维护的 JSON 含注释和尾随逗号，同时保留中文路径/文本，便于现场调参。
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    _instance = JsonSerializer.Deserialize<AppConfig>(json, options);
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
                    string json = JsonSerializer.Serialize(_instance, options);
                    File.WriteAllText(ConfigPath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"创建默认配置失败: {ex.Message}");
                }
            }
        }

        /// <summary>把当前内存配置写回 app_config.json；失败仅记录调试信息。</summary>
        public static void SaveConfig()
        {
            if (_instance == null) return;
            
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            try
            {
                // 保存失败只记录诊断信息，不替换当前内存配置，避免运行中的服务突然失去参数。
                string json = JsonSerializer.Serialize(_instance, options);
                File.WriteAllText(ConfigPath, json);
                System.Diagnostics.Debug.WriteLine("配置已成功保存到本地。");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            }
        }
    }
}
