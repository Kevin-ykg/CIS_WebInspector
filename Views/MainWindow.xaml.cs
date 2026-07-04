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

        // ---- 图像缩放与拖动逻辑 ----
        private bool _isDragging = false;
        private Point _lastMousePosition;

        private void Image_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var grid = sender as System.Windows.Controls.Grid;
            if (grid == null || grid.Children.Count == 0) return;

            var image = grid.Children[0] as System.Windows.Controls.Image;
            if (image == null || image.RenderTransform == null) return;

            var transformGroup = image.RenderTransform as System.Windows.Media.TransformGroup;
            if (transformGroup == null || transformGroup.Children.Count < 2) return;

            var scaleTransform = transformGroup.Children[0] as System.Windows.Media.ScaleTransform;
            var translateTransform = transformGroup.Children[1] as System.Windows.Media.TranslateTransform;

            if (scaleTransform == null || translateTransform == null) return;

            double zoom = e.Delta > 0 ? 1.2 : 1 / 1.2;
            
            // 获取鼠标相对于图片的坐标
            Point relativePos = e.GetPosition(image);

            double absoluteX = relativePos.X * scaleTransform.ScaleX + translateTransform.X;
            double absoluteY = relativePos.Y * scaleTransform.ScaleY + translateTransform.Y;

            scaleTransform.ScaleX *= zoom;
            scaleTransform.ScaleY *= zoom;

            // 限制缩放级别
            if (scaleTransform.ScaleX < 0.1)
            {
                scaleTransform.ScaleX = 0.1;
                scaleTransform.ScaleY = 0.1;
            }
            if (scaleTransform.ScaleX > 50)
            {
                scaleTransform.ScaleX = 50;
                scaleTransform.ScaleY = 50;
            }

            // 保持鼠标所在的像素点在屏幕上位置不变
            translateTransform.X = absoluteX - relativePos.X * scaleTransform.ScaleX;
            translateTransform.Y = absoluteY - relativePos.Y * scaleTransform.ScaleY;
        }

        private void Image_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var grid = sender as System.Windows.Controls.Grid;
            if (grid == null) return;

            _isDragging = true;
            _lastMousePosition = e.GetPosition(grid);
            grid.CaptureMouse();
        }

        private void Image_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var grid = sender as System.Windows.Controls.Grid;
            if (grid == null) return;

            _isDragging = false;
            grid.ReleaseMouseCapture();
        }

        private void Image_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDragging) return;

            var grid = sender as System.Windows.Controls.Grid;
            if (grid == null || grid.Children.Count == 0) return;

            var image = grid.Children[0] as System.Windows.Controls.Image;
            if (image == null || image.RenderTransform == null) return;

            var transformGroup = image.RenderTransform as System.Windows.Media.TransformGroup;
            if (transformGroup == null || transformGroup.Children.Count < 2) return;

            var translateTransform = transformGroup.Children[1] as System.Windows.Media.TranslateTransform;
            if (translateTransform == null) return;

            Point currentPosition = e.GetPosition(grid);
            double deltaX = currentPosition.X - _lastMousePosition.X;
            double deltaY = currentPosition.Y - _lastMousePosition.Y;

            translateTransform.X += deltaX;
            translateTransform.Y += deltaY;

            _lastMousePosition = currentPosition;
        }
    }
}
