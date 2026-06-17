using System.Windows;
using CIS_WebInspector.ViewModels;

namespace CIS_WebInspector.Views
{
    public partial class TlcSettingsWindow : Window
    {
        public TlcSettingsWindow()
        {
            InitializeComponent();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            // 确保窗口关闭时断开连接
            if (DataContext is TlcSettingsViewModel vm && vm.IsConnected)
            {
                if (vm.DisconnectCommand.CanExecute(null))
                {
                    vm.DisconnectCommand.Execute(null);
                }
            }
            base.OnClosed(e);
        }

        private void LogTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            LogScrollViewer?.ScrollToBottom();
        }
    }
}
