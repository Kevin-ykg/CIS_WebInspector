using System;
using System.Collections.ObjectModel;
using System.Windows;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;
using System.Linq;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// TLC 串口参数页的状态与命令适配层。耗时的 DLL 调用放到线程池，完成后由 WPF
    /// 同步上下文恢复 UI 属性；Parameters 保存“显示项—回读关键字—下发函数”的映射。
    /// </summary>
    public class TlcSettingsViewModel : ViewModelBase
    {
        public ObservableCollection<string> AvailablePorts { get; } = new ObservableCollection<string>();
        public ObservableCollection<TlcParameterModel> Parameters { get; } = new ObservableCollection<TlcParameterModel>();

        private string _selectedPort;
        public string SelectedPort
        {
            get => _selectedPort;
            set { _selectedPort = value; OnPropertyChanged(); }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set 
            { 
                _isConnected = value; 
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotConnected));
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                RefreshCommand?.RaiseCanExecuteChanged();
            }
        }

        public bool IsNotConnected => !IsConnected;

        private string _cameraParametersText;
        public string CameraParametersText
        {
            get => _cameraParametersText;
            set { _cameraParametersText = value; OnPropertyChanged(); }
        }

        public RelayCommand ConnectCommand { get; }
        public RelayCommand DisconnectCommand { get; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand ApplyParameterCommand { get; }

        public TlcSettingsViewModel()
        {
            ConnectCommand = new RelayCommand(_ => ExecuteConnect(), _ => !IsConnected && !string.IsNullOrEmpty(SelectedPort));
            DisconnectCommand = new RelayCommand(_ => ExecuteDisconnect(), _ => IsConnected);
            RefreshCommand = new RelayCommand(_ => RefreshCameraParametersText(), _ => IsConnected);
            ApplyParameterCommand = new RelayCommand(ExecuteApplyParameter);

            LoadPorts();
            InitializeParameters();
        }

        /// <summary>枚举 TLC 可用端口并默认选择第一项；异常只反馈到设置窗口。</summary>
        private void LoadPorts()
        {
            AvailablePorts.Clear();
            try
            {
                var ports = TlcSdkWrapper.GetPorts();
                foreach (var port in ports)
                {
                    AvailablePorts.Add(port);
                }
                if (AvailablePorts.Count > 0)
                {
                    SelectedPort = AvailablePorts[0];
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"枚举串口失败: {ex.Message}");
            }
        }

        /// <summary>在后台打开端口，等待硬件 DTR 复位完成后再读取参数。</summary>
        private async void ExecuteConnect()
        {
            if (string.IsNullOrEmpty(SelectedPort)) return;
            string portToOpen = SelectedPort.Trim(); // 去除可能的空白符或不可见字符
            
            IsConnected = false;
            CameraParametersText = $"正在连接 {portToOpen}，请稍候...";

            int ret = await System.Threading.Tasks.Task.Run(() => TlcSdkWrapper.open_port(portToOpen));
            if (ret == 0)
            {
                IsConnected = true;
                CameraParametersText = "连接成功，等待设备就绪...";
                
                // 延迟 1500ms 等待串口硬件彻底初始化完成 (串口 DTR 复位可能需要较长时间)
                await System.Threading.Tasks.Task.Delay(1500);
                RefreshCameraParametersText();
            }
            else
            {
                string err = TlcSdkWrapper.GetLastErrorMsg();
                MessageBox.Show($"连接端口失败!\n原因: {err}", "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>在后台关闭唯一 TLC 端口，并同步更新界面连接状态。</summary>
        private async void ExecuteDisconnect()
        {
            await System.Threading.Tasks.Task.Run(() => TlcSdkWrapper.close_port());
            IsConnected = false;
            CameraParametersText = "已断开连接。";
        }

        /// <summary>读取 SDK 的参数文本并解析回填到结构化参数列表。</summary>
        private async void RefreshCameraParametersText()
        {
            if (IsConnected)
            {
                CameraParametersText = "正在后台线程读取底层参数，请稍候...";
                string result = await System.Threading.Tasks.Task.Run(() => TlcSdkWrapper.GetCameraParameters());
                CameraParametersText = result;
                ParseAndFillValues(result);
            }
        }

        /// <summary>根据每个参数的 MatchKeys，从 SDK 人类可读文本中提取数值。</summary>
        private void ParseAndFillValues(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return;
            
            // SDK 返回面向人的多行文本而非结构化对象：允许单位/状态后缀，只取键后的首个数值。
            var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*([a-zA-Z0-9\s\.]+?)\s+([-+]?[0-9]*\.?[0-9]+)");
                if (match.Success)
                {
                    string key = match.Groups[1].Value.Trim().ToLower().Replace(" ", "").Replace("_", "");
                    string val = match.Groups[2].Value.Trim();

                    foreach (var param in Parameters)
                    {
                        if (param.MatchKeys != null)
                        {
                            foreach (var mk in param.MatchKeys)
                            {
                                string cleanMk = mk.ToLower().Replace(" ", "").Replace("_", "");
                                if (key == cleanMk)
                                {
                                    param.Value = val;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>把单项参数值交给绑定的原生下发函数，失败时显示 SDK 最近错误。</summary>
        private async void ExecuteApplyParameter(object parameter)
        {
            if (!(parameter is TlcParameterModel param)) return;

            if (!IsConnected)
            {
                MessageBox.Show("请先连接串口后再修改参数！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (param == null || string.IsNullOrEmpty(param.Value) || param.ApplyAction == null) return;

            string displayValue = param.Value;
            if (param.HasOptions)
            {
                var opt = param.Options.FirstOrDefault(o => o.Value == param.Value);
                if (opt != null) displayValue = opt.DisplayText;
            }

            CameraParametersText += $"\n\nUSER> 正在下发参数: {param.Name} -> {displayValue}";
            // ApplyAction 在初始化参数表时绑定到具体 tlc.dll 命令，UI 层无需按参数名写分支。
            bool success = await System.Threading.Tasks.Task.Run(() => param.ApplyAction(param.Value));
            if (success)
            {
                CameraParametersText += $"\n{param.MatchKeys.FirstOrDefault() ?? "cmd"} {param.Value}. {displayValue} 操作成功.";
            }
            else
            {
                string err = TlcSdkWrapper.GetLastErrorMsg();
                CameraParametersText += $"\n操作失败: {err}";
                MessageBox.Show($"设置 {param.Name} 失败！\n硬件反馈: {err}", "设置失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>建立设置页支持的参数清单；此清单也是 UI 与 tlc.dll 命令之间的唯一映射表。</summary>
        private void InitializeParameters()
        {
            // 集中声明设备参数元数据，MatchKeys 用于回读匹配，ApplyAction 用于单项下发。
            Parameters.Add(new TlcParameterModel
            {
                Name = "行频 (Line Rate)",
                Description = "设置相机扫描行频",
                MatchKeys = new[] { "linerate" },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_line_rate(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "增益 (System Gain)",
                Description = "设置相机增益 (浮点数)",
                MatchKeys = new[] { "systemgain" },
                ApplyAction = val => float.TryParse(val, out float v) && TlcSdkWrapper.set_gain(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "偏置 (Black Level)",
                Description = "设置黑电平偏置",
                MatchKeys = new[] { "blacklevel" },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_offset(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "触发模式 (Ext. Trig.)",
                Description = "0:内触发 1:外触发",
                MatchKeys = new[] { "ext.trig." },
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "内触发 (OFF)", Value = "0" },
                    new TlcParameterOption { DisplayText = "外触发 (ON)", Value = "1" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_trigger_mode(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "红灯亮度 (Light Red)",
                Description = "设置红光通道亮度 (0-255)",
                MatchKeys = new[] { "lightred" },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_light_red(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "绿灯亮度 (Light Green)",
                Description = "设置绿光通道亮度 (0-255)",
                MatchKeys = new[] { "lightgreen" },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_light_green(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "蓝灯亮度 (Light Blue)",
                Description = "设置蓝光通道亮度 (0-255)",
                MatchKeys = new[] { "lightblue" },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_light_blue(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "平场校正模式 (FlatFieldMode)",
                Description = "设置平场校正的模式",
                MatchKeys = new[] { "flatfieldmode" },
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "关闭 (OFF)", Value = "0" },
                    new TlcParameterOption { DisplayText = "开启 (ON)", Value = "1" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_ffc_mode(v) == 0
            });


            Parameters.Add(new TlcParameterModel
            {
                Name = "平场校正算法 (FlatFieldAlgo.)",
                Description = "设置平场校准的算法版本",
                MatchKeys = new[] { "flatfieldalgo." },
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "Basic", Value = "0" },
                    new TlcParameterOption { DisplayText = "Vendor", Value = "1" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_ffc_algorithm(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "平场参数集 (LPC Selector)",
                Description = "选择平场校准参数集",
                MatchKeys = new[] { "pixelcoeff." }, // 根据黑框输出盲猜是 Pixel Coeff.，若不是可为空
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_lpc_selector(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "执行平场校正 (FFC Start)",
                Description = "执行平场校准操作",
                MatchKeys = new string[0],
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "White", Value = "0" },
                    new TlcParameterOption { DisplayText = "Black", Value = "1" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_ffc_start(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "镜像输出 (Mirror Mode)",
                Description = "0:关闭 1:开启",
                MatchKeys = new[] { "mirrormode" },
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "正常 (OFF)", Value = "0" },
                    new TlcParameterOption { DisplayText = "镜像 (ON)", Value = "1" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_mirror_mode(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "二值化阈值 (Binarize Th.)",
                Description = "硬件二值化输出时的阈值",
                MatchKeys = new[] { "binarizeth." },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_binarization_threshold(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "测试模板 (Test Pat.)",
                Description = "选择硬件测试图案",
                MatchKeys = new[] { "testpat." },
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "Sensor", Value = "0" },
                    new TlcParameterOption { DisplayText = "Black", Value = "1" },
                    new TlcParameterOption { DisplayText = "White", Value = "2" },
                    new TlcParameterOption { DisplayText = "Ramp", Value = "3" },
                    new TlcParameterOption { DisplayText = "ValueStep", Value = "4" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_test_pattern(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "加载相机配置 (Load Config)",
                Description = "1:USER等，从硬件加载配置",
                MatchKeys = new string[0],
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "USER 配置 1", Value = "1" },
                    new TlcParameterOption { DisplayText = "USER 配置 2", Value = "2" },
                    new TlcParameterOption { DisplayText = "USER 配置 3", Value = "3" },
                    new TlcParameterOption { DisplayText = "USER 配置 4", Value = "4" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_usl(v) == 0
            });

            Parameters.Add(new TlcParameterModel
            {
                Name = "保存相机配置 (Save Config)",
                Description = "保存当前参数到硬件存储",
                MatchKeys = new string[0],
                Options = new[] 
                {
                    new TlcParameterOption { DisplayText = "USER 配置 1", Value = "1" },
                    new TlcParameterOption { DisplayText = "USER 配置 2", Value = "2" },
                    new TlcParameterOption { DisplayText = "USER 配置 3", Value = "3" },
                    new TlcParameterOption { DisplayText = "USER 配置 4", Value = "4" }
                },
                ApplyAction = val => int.TryParse(val, out int v) && TlcSdkWrapper.set_uss(v) == 0
            });
        }
    }
}
