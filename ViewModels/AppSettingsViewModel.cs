using System;
using System.Windows;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 全局参数设置页。界面绑定配置副本，保存时才一次性覆盖进程配置并落盘，
    /// 因此取消窗口不会把尚未确认的参数带入正在运行的主流程。
    /// </summary>
    public class AppSettingsViewModel : ViewModelBase
    {
        public AppConfig Config { get; set; }

        public string DebugLogPath { get => Config.DebugLogPath; set { Config.DebugLogPath = value; OnPropertyChanged(nameof(DebugLogPath)); } }
        public string TiffImageDir { get => Config.TiffImageDir; set { Config.TiffImageDir = value; OnPropertyChanged(nameof(TiffImageDir)); } }
        public string CroppedOutputDir { get => Config.CroppedOutputDir; set { Config.CroppedOutputDir = value; OnPropertyChanged(nameof(CroppedOutputDir)); } }

        public int DownscaleFactor
        {
            get => Config.DownscaleFactor;
            set
            {
                int oldFactor = Config.DownscaleFactor;
                if (oldFactor != value && value > 0 && oldFactor > 0)
                {
                    double ratio = (double)oldFactor / value;
                    Config.DownscaleFactor = value;

                    // Base* 参数保存原始采集尺度值。改变缩小倍数时同步调整，
                    // 使除以 DownscaleFactor 后的实际处理 ROI/偏移尽量保持不变。
                    Config.BaseQrOffsetRows = (int)Math.Round(Config.BaseQrOffsetRows / ratio);
                    Config.BaseOverlapRows = (int)Math.Round(Config.BaseOverlapRows / ratio);
                    Config.BaseRoiX = (int)Math.Round(Config.BaseRoiX / ratio);
                    Config.BaseRoiWidth = (int)Math.Round(Config.BaseRoiWidth / ratio);

                    OnPropertyChanged(nameof(DownscaleFactor));
                    OnPropertyChanged(nameof(BaseQrOffsetRows));
                    OnPropertyChanged(nameof(BaseOverlapRows));
                    OnPropertyChanged(nameof(BaseRoiX));
                    OnPropertyChanged(nameof(BaseRoiWidth));
                }
                else if (value > 0)
                {
                    Config.DownscaleFactor = value;
                    OnPropertyChanged(nameof(DownscaleFactor));
                }
            }
        }

        public int BaseQrOffsetRows { get => Config.BaseQrOffsetRows; set { Config.BaseQrOffsetRows = value; OnPropertyChanged(nameof(BaseQrOffsetRows)); } }
        public int BaseOverlapRows { get => Config.BaseOverlapRows; set { Config.BaseOverlapRows = value; OnPropertyChanged(nameof(BaseOverlapRows)); } }
        public int BaseRoiX { get => Config.BaseRoiX; set { Config.BaseRoiX = value; OnPropertyChanged(nameof(BaseRoiX)); } }
        public int BaseRoiWidth { get => Config.BaseRoiWidth; set { Config.BaseRoiWidth = value; OnPropertyChanged(nameof(BaseRoiWidth)); } }

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

            // JSON 深拷贝把编辑会话与全局单例隔离；这是“取消不生效”的关键边界。
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
                // 保持 ConfigManager.Config 对象引用不变，逐属性复制，避免已持有该引用的界面失效。
                var json = System.Text.Json.JsonSerializer.Serialize(Config);
                var globalConfig = ConfigManager.Config;

                // 先反序列化得到类型完整的快照，再复制所有可写配置项。
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
