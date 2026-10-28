using System;
using System.Diagnostics;
using System.Threading;
using ares;

namespace CIS_WebInspector.Services
{
    public sealed partial class CisCameraEngine
    {
        private readonly object _deviceSync = new object();
        private readonly CameraConnectionHealth _connectionHealth = new CameraConnectionHealth();
        private readonly Stopwatch _connectionClock = Stopwatch.StartNew();
        private Timer _connectionTimer;
        private int _connectionMonitoring;
        private int _connectionFaulted;
        private int _pendingGrabFailed;
        private int _lastStatusEvent;
        // 委托必须保持至 closeDevice 返回，避免非托管回调访问已被 GC 回收的委托。
        private VolansEventHandler _callbackCameraStatus;
        private VolansEventHandler _callbackGrabFinished;

        /// <summary>每个加载会话最多一次；后台线程发布，UI 必须异步调度后再停采/弹窗。</summary>
        public event EventHandler<string> AcquisitionFaulted;

        private string GetSdkError()
        {
            var code = _device.getLastError();
            return $"{code} ({(int)code}): {VolansDevice.errorText(code)}";
        }

        private void RegisterConnectionCallbacks()
        {
            _callbackCameraStatus = (status, unused) => Interlocked.Exchange(ref _lastStatusEvent, unchecked((int)status));
            _callbackGrabFinished = (success, unused) =>
            {
                if (success == 0 && Volatile.Read(ref _connectionMonitoring) != 0)
                {
                    Interlocked.Exchange(ref _pendingGrabFailed, 1);
                }
            };
            if (!_device.registerEventHandler((uint)AriEventType.AriEvent_CameraStatusChanged, _callbackCameraStatus) ||
                !_device.registerEventHandler((uint)AriEventType.AriEvent_PendingGrabFinished, _callbackGrabFinished))
            {
                throw new InvalidOperationException("注册相机状态/采集结束回调失败：" + GetSdkError());
            }
        }

        private void CheckConnection(object unused)
        {
            string fault = null;
            // Timer 可能重入；停采/关闭与状态读取使用同一锁。SDK 回调绝不获取此锁。
            lock (_deviceSync)
            {
                if (_disposed || _device == null || _connectionMonitoring == 0 || _connectionFaulted != 0)
                {
                    return;
                }
                try
                {
                    uint status = 0;
                    bool read = _device.getProperty((uint)AriPropertyType.AriProp_CameraStatus, ref status);
                    string error = read ? null : GetSdkError();
                    string reason = Interlocked.Exchange(ref _pendingGrabFailed, 0) != 0
                        ? "SDK 通知异步采集失败"
                        : _connectionHealth.Observe(read, status, _connectionClock.Elapsed);
                    if (reason != null)
                    {
                        fault = $"{reason}；状态读取成功={read}，CameraStatus=0x{status:X8}，" +
                            $"最近状态事件=0x{unchecked((uint)Volatile.Read(ref _lastStatusEvent)):X8}，" +
                            $"SDK={error ?? GetSdkError()}。可能为相机断电、线缆/采集卡异常，请检查后重新加载相机。";
                    }
                }
                catch (Exception ex)
                {
                    // SDK 调用抛异常说明本次无法安全继续；不让异常逃出 Timer 终止进程。
                    fault = "读取相机状态时发生异常：" + ex.Message + "。请检查设备并重新加载相机。";
                }
                if (fault != null)
                {
                    Interlocked.Exchange(ref _connectionFaulted, 1);
                    Volatile.Write(ref _connectionMonitoring, 0);
                }
            }
            if (fault != null)
            {
                AcquisitionFaulted?.Invoke(this, fault);
            }
        }
    }
}
