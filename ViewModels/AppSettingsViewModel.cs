using System;
using System.Windows;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;

namespace CIS_WebInspector.ViewModels
{
    public class AppSettingsViewModel : ViewModelBase
    {
        public AppConfig Config { get; set; }
        
        public string DebugLogPath { get => Config.DebugLogPath; set { Config.DebugLogPath = value; OnPropertyChanged(nameof(DebugLogPath)); } }
        public string TiffImageDir { get => Config.TiffImageDir; set { Config.TiffImageDir = value; OnPropertyChanged(nameof(TiffImageDir)); } }
        public string CroppedOutputDir { get => Config.CroppedOutputDir; set { Config.CroppedOutputDir = value; OnPropertyChanged(nameof(CroppedOutputDir)); } }

        public RelayCommand SaveCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand SelectDebugLogCommand { get; }
        public RelayCommand SelectTiffDirCommand { get; }
        public RelayCommand SelectCroppedDirCommand { get; }

        public MainViewModel MainVm { get; }

        private readonly Window _window;

        public AppSettingsViewModel(Window window, MainViewModel mainVm)
        {
            _window = window;
            MainVm = mainVm;
            
            // 为了防止取消修改时影响全局配置，我们创建一个副本用于绑定
            // 但是为了简单起见，且由于多数应用场景可以接受直接修改单例，
            // 这里直接引用。如果希望取消能回滚，可以做深度拷贝。
            // 这里我们采用直接引用的方式，因为保存时直接序列化。
            // 提示：由于用户希望修改后能保存或取消，如果直接绑定会立即生效。
            // 为了实现真正的“取消”，我们可以利用 JSON 序列化做一个深拷贝。
            var json = System.Text.Json.JsonSerializer.Serialize(ConfigManager.Config);
            Config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);

            SaveCommand = new RelayCommand(ExecuteSave);
            CancelCommand = new RelayCommand(ExecuteCancel);
            
            SelectDebugLogCommand = new RelayCommand(ExecuteSelectDebugLog);
            SelectTiffDirCommand = new RelayCommand(ExecuteSelectTiffDir);
            SelectCroppedDirCommand = new RelayCommand(ExecuteSelectCroppedDir);
        }

        private void ExecuteSelectDebugLog(object parameter)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "选择 Debug.log 保存路径",
                Filter = "日志文件|*.log|文本文件|*.txt|所有文件|*.*",
                FileName = System.IO.Path.GetFileName(DebugLogPath),
                InitialDirectory = System.IO.Path.GetDirectoryName(DebugLogPath)
            };
            if (dialog.ShowDialog() == true)
            {
                DebugLogPath = dialog.FileName;
            }
        }

        private void ExecuteSelectTiffDir(object parameter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 TIFF 原图目录",
                Filter = "文件夹|*.none",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "选择此文件夹"
            };
            if (dialog.ShowDialog() == true)
            {
                TiffImageDir = System.IO.Path.GetDirectoryName(dialog.FileName);
            }
        }

        private void ExecuteSelectCroppedDir(object parameter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择裁切小图输出目录",
                Filter = "文件夹|*.none",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "选择此文件夹"
            };
            if (dialog.ShowDialog() == true)
            {
                CroppedOutputDir = System.IO.Path.GetDirectoryName(dialog.FileName);
            }
        }

        private void ExecuteSave(object parameter)
        {
            try
            {
                // 将修改后的副本覆盖到全局实例
                var json = System.Text.Json.JsonSerializer.Serialize(Config);
                var globalConfig = ConfigManager.Config;
                
                // 将所有属性通过反射或再序列化赋值回去
                var updatedConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
                
                foreach (var prop in typeof(AppConfig).GetProperties())
                {
                    if (prop.CanWrite)
                    {
                        prop.SetValue(globalConfig, prop.GetValue(updatedConfig));
                    }
                }

                ConfigManager.SaveConfig();
                _window.DialogResult = true;
                _window.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteCancel(object parameter)
        {
            _window.DialogResult = false;
            _window.Close();
        }
    }
}
