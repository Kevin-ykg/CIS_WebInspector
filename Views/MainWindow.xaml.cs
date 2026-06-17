using System.Windows;
using CIS_WebInspector.ViewModels;

namespace CIS_WebInspector.Views
{
    /// <summary>
    /// MainWindow code-behind：仅负责 ViewModel 生命周期管理，不包含业务逻辑。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
        }

        private void Window_Closed(object sender, System.EventArgs e)
        {
            // 确定性释放：窗体关闭时立即释放相机和非托管资源
            _viewModel?.Dispose();
        }
    }
}
