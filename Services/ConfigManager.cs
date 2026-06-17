using System;
using System.IO;
using System.Text.Json;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    public static class ConfigManager
    {
        private static AppConfig _instance;
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_config.json");

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
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
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
                    string json = JsonSerializer.Serialize(_instance, options);
                    File.WriteAllText(ConfigPath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"创建默认配置失败: {ex.Message}");
                }
            }
        }
    }
}
