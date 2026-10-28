using System;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 只判断 SDK 可观察的异常，不用“无帧”推断断电。Clock 位与行/帧触发位分开，
    /// 编码器停转时 Line/Frame 消失不会触发本判断。调用方串行访问并传入单调时间。
    /// </summary>
    internal sealed class CameraConnectionHealth
    {
        internal const uint ClockMask = 0x07; // SDK ARI_CAMERA_STATUS_CLOCK_SIGNAL1/2/3
        private readonly TimeSpan _confirmation = TimeSpan.FromSeconds(1);
        private TimeSpan? _missingClockSince;
        private TimeSpan? _readFailureSince;

        internal void Reset()
        {
            _missingClockSince = null;
            _readFailureSince = null;
        }

        internal string Observe(bool readSucceeded, uint status, TimeSpan now)
        {
            if (!readSucceeded)
            {
                _missingClockSince = null;
                _readFailureSince = _readFailureSince ?? now;
                return now - _readFailureSince.Value >= _confirmation ? "连续至少 1 秒无法读取相机信号状态" : null;
            }
            _readFailureSince = null;
            if ((status & ClockMask) != 0)
            {
                _missingClockSince = null;
                return null;
            }
            _missingClockSince = _missingClockSince ?? now;
            return now - _missingClockSince.Value >= _confirmation ? "相机 CLK1/CLK2/CLK3 均持续至少 1 秒无信号" : null;
        }
    }
}
