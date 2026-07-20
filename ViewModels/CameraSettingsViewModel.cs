using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using Microsoft.Win32;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// Volans 采集卡原生属性的通用编辑页。属性值变化会立即写入当前设备；
    /// 写入失败时重新读取硬件值，并临时退订事件以避免回滚再次触发写入。
    /// </summary>
    public class CameraSettingsViewModel : ViewModelBase
    {
        private readonly CisCameraEngine _cameraEngine;

        public ObservableCollection<CameraPropertyModel> Properties { get; } = new ObservableCollection<CameraPropertyModel>();

        public RelayCommand SaveConfigCommand { get; }

        public CameraSettingsViewModel(CisCameraEngine cameraEngine)
        {
            _cameraEngine = cameraEngine ?? throw new ArgumentNullException(nameof(cameraEngine));
            SaveConfigCommand = new RelayCommand(_ => ExecuteSaveConfig());

            LoadProperties();
        }

        /// <summary>枚举当前设备支持的属性，并订阅 Value 变化实现即时写入。</summary>
        private void LoadProperties()
        {
            var props = _cameraEngine.GetAllProperties();
            foreach (var kvp in props)
            {
                var model = new CameraPropertyModel { Name = kvp.Key, Value = kvp.Value };
                // 监听用户修改事件，实时写入底层设备
                model.PropertyChanged += Model_PropertyChanged;
                Properties.Add(model);
            }
        }

        /// <summary>转发单项属性写入；失败时以硬件实际值回滚绑定模型。</summary>
        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CameraPropertyModel.Value) && sender is CameraPropertyModel model)
            {
                bool success = _cameraEngine.SetProperty(model.Name, model.Value);
                if (!success)
                {
                    MessageBox.Show($"修改属性 {model.Name} 失败！可能是该参数在当前模式下为只读，或者超出了允许的范围。", 
                        "设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    
                    // 恢复旧值，这里简单处理，重新读取底层值覆盖
                    var props = _cameraEngine.GetAllProperties();
                    if (props.TryGetValue(model.Name, out uint oldVal))
                    {
                        // 临时注销事件以避免循环触发
                        model.PropertyChanged -= Model_PropertyChanged;
                        model.Value = oldVal;
                        model.PropertyChanged += Model_PropertyChanged;
                    }
                }
            }
        }

        private void ExecuteSaveConfig()
        {
            var sfd = new SaveFileDialog
            {
                Title = "保存采集卡配置文件",
                Filter = "Ares Configuration File (*.arcf)|*.arcf|All Files (*.*)|*.*",
                FileName = $"config_{DateTime.Now:yyyyMMdd_HHmmss}.arcf"
            };

            if (sfd.ShowDialog() == true)
            {
                bool success = _cameraEngine.SaveConfiguration(sfd.FileName);
                if (success)
                {
                    MessageBox.Show($"配置已成功保存至:\n{sfd.FileName}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("配置保存失败，请检查底层设备状态或目标路径权限。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
