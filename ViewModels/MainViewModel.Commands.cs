using CIS_WebInspector.Services;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 主界面命令与数据源选择，集中维护采集控制、文件选择和参数窗口入口。
    /// </summary>
    public partial class MainViewModel
    {
        // ---- Commands ----
        private RelayCommand _startCommand;
        public RelayCommand StartCommand =>
            _startCommand ?? (_startCommand = new RelayCommand(_ => ExecuteStart(_), _ => !IsRunning));

        private RelayCommand _selectAutoSaveDirCommand;
        public RelayCommand SelectAutoSaveDirCommand =>
            _selectAutoSaveDirCommand ?? (_selectAutoSaveDirCommand = new RelayCommand(_ => ExecuteSelectAutoSaveDir(_)));

        private RelayCommand _stopCommand;
        public RelayCommand StopCommand =>
            _stopCommand ?? (_stopCommand = new RelayCommand(_ => ExecuteStop(_), _ => IsRunning));

        private RelayCommand _resumeCommand;
        public RelayCommand ResumeCommand =>
            _resumeCommand ?? (_resumeCommand = new RelayCommand(_ => ExecuteResume(_), _ => !IsRunning && CameraSource is OfflineImageSource));

        private RelayCommand _loadOfflineCommand;
        public RelayCommand LoadOfflineCommand =>
            _loadOfflineCommand ?? (_loadOfflineCommand = new RelayCommand(_ => ExecuteLoadOffline(_), _ => !IsRunning));

        private RelayCommand _saveImageCommand;
        public RelayCommand SaveImageCommand =>
            _saveImageCommand ?? (_saveImageCommand = new RelayCommand(_ => ExecuteSaveImage(_)));

        private RelayCommand _loadConfigCommand;
        public RelayCommand LoadConfigCommand =>
            _loadConfigCommand ?? (_loadConfigCommand = new RelayCommand(_ => ExecuteLoadConfig(_), _ => !IsRunning));

        private RelayCommand _openCameraSettingsCommand;
        public RelayCommand OpenCameraSettingsCommand =>
            _openCameraSettingsCommand ?? (_openCameraSettingsCommand = new RelayCommand(_ => ExecuteOpenCameraSettings(_), _ => !IsRunning));

        private RelayCommand _openTlcSettingsCommand;
        public RelayCommand OpenTlcSettingsCommand =>
            _openTlcSettingsCommand ?? (_openTlcSettingsCommand = new RelayCommand(_ => ExecuteOpenTlcSettings(_), _ => !IsRunning));

        private RelayCommand _openAppSettingsCommand;
        public RelayCommand OpenAppSettingsCommand =>
            _openAppSettingsCommand ?? (_openAppSettingsCommand = new RelayCommand(_ => ExecuteOpenAppSettings(_), _ => !IsRunning));


        // ---- 选择自动保存目录 ----
        private void ExecuteSelectAutoSaveDir(object _)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择单帧图像批量保存文件夹",
                Filter = "文件夹|*.none",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "选择此文件夹" // 占位符
            };

            if (dialog.ShowDialog() == true)
            {
                AutoSaveDirectory = System.IO.Path.GetDirectoryName(dialog.FileName);
                AddLog($"自动保存目录已设置为: {AutoSaveDirectory}");
            }
        }

        // ---- 打开采集卡设置弹窗 ----
        private void ExecuteOpenCameraSettings(object _)
        {
            if (CameraSource is CisCameraEngine onlineCamera)
            {
                var vm = new CameraSettingsViewModel(onlineCamera);
                var win = new Views.CameraSettingsWindow { DataContext = vm };
                win.ShowDialog();
            }
            else
            {
                System.Windows.MessageBox.Show("当前不在在线采集卡模式，无法配置底层硬件参数！", 
                    "不可用", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        // ---- 打开 TLC 相机设置弹窗 ----
        private void ExecuteOpenTlcSettings(object _)
        {
            var vm = new TlcSettingsViewModel();
            var win = new Views.TlcSettingsWindow { DataContext = vm };
            win.ShowDialog();
        }

        // ---- 打开全局参数设置弹窗 ----
        private void ExecuteOpenAppSettings(object _)
        {
            var win = new Views.AppSettingsWindow();
            var vm = new AppSettingsViewModel(win, this);
            win.DataContext = vm;
            
            if (win.ShowDialog() == true)
            {
                AddLog(
                    "全局系统参数已更新并保存。当前数据源仍使用加载时的配置快照；" +
                    "重新加载相机/离线图库后，采集、二维码和拼接参数才会应用新值。");
            }
        }
    }
}
