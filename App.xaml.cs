using System;
using System.IO;
using System.Windows;
using OpenCvSharp;
using CIS_WebInspector.Services;

namespace CIS_WebInspector
{
    /// <summary>
    /// WPF 应用入口。主窗口由 App.xaml 创建，业务对象的生命周期由 MainWindow 管理；
    /// 此处只保留应用级启动扩展点，避免把采集或算法初始化放到 UI 框架入口中。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
        }
    }
}
