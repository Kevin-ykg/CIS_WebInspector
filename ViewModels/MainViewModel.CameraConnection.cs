using System;
using System.Threading;
using System.Windows;
using CIS_WebInspector.Services;

namespace CIS_WebInspector.ViewModels
{
    public partial class MainViewModel
    {
        private void OnCameraAcquisitionFaulted(object sender, string reason)
        {
            if (_disposed || !ReferenceEquals(sender, CameraSource))
            {
                return;
            }
            // 后台先持久化；Engine 已锁存故障并禁止后续帧回调。
            AddLog("[CameraConnection][ERROR] " + reason);
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                // 切换数据源/关窗后，旧会话的排队通知不能停止新相机或离线播放。
                if (_disposed || !ReferenceEquals(sender, CameraSource))
                {
                    return;
                }
                Interlocked.Exchange(ref _stopAfterCurrentFrame, 1);
                Volatile.Read(ref _frameProcessor)?.DiscardPending();
                CancelInspectionJob();
                try
                {
                    ((CisCameraEngine)sender).StopGrab();
                }
                catch (Exception ex)
                {
                    AddLog("[CameraConnection][ERROR] 异常后停止 SDK 采集失败：" + ex.Message);
                }
                finally
                {
                    StopFrameProcessor(false);
                    IsRunning = false;
                }
                StatusText = "相机信号或采集异常，请检查后重新加载相机";
                MessageBox.Show(reason + "\n\n已停止采集并取消当前检测作业。" +
                    "\n请检查相机供电、CameraLink 线缆和采集卡；恢复后重新加载相机配置。",
                    "相机信号/采集异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }, System.Windows.Threading.DispatcherPriority.Send);
        }
    }
}
