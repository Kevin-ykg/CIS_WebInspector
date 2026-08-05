using System.Windows;

namespace CIS_WebInspector
{
    /// <summary>
    /// WPF 应用入口。主窗口由 App.xaml 创建，业务对象的生命周期由 MainWindow 管理。
    /// 采集卡、二维码模型和检测服务均按用户操作延迟初始化，避免应用启动阶段占用硬件资源。
    /// </summary>
    public partial class App : Application
    {
    }
}
