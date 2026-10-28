using System;
using CIS_WebInspector.Services;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception(name);
        }
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static int Main()
    {
        var health = new CameraConnectionHealth();
        Func<bool, uint, int, string> observe = (read, status, ms) => health.Observe(read, status, TimeSpan.FromMilliseconds(ms));
        // 编码器停止导致 Line/Frame 变化，但只要任一相机时钟存在就不能报断连。
        foreach (uint status in new uint[] { 1, 2, 4, 7, 0x11, 0x21, 0x31 })
        {
            Check(observe(true, status, 0) == null && observe(true, status, 60000) == null,
                "clock-present/trigger-idle " + status);
        }
        health.Reset();
        Check(observe(true, 0, 0) == null, "no-clock begins confirmation");
        Check(observe(true, 0x30, 999) == null, "Line/Frame do not substitute for clock; debounce");
        Check(observe(true, 0x30, 1000) != null, "persistent clock loss alarms at boundary");
        health.Reset();
        observe(true, 0, 0);
        Check(observe(true, 1, 900) == null && observe(true, 0, 1000) == null &&
            observe(true, 0, 1900) == null, "clock recovery resets confirmation");
        health.Reset();
        Check(observe(false, 7, 0) == null && observe(false, 7, 999) == null, "read failure debounce");
        Check(observe(false, 7, 1000) != null, "persistent read failure ignores stale status");
        Check(observe(true, 1, 1100) == null && observe(false, 0, 1500) == null, "successful read resets failure streak");
        health.Reset();
        observe(true, 0, 0);
        observe(false, 0, 900);
        Check(observe(true, 0, 1000) == null, "different failure causes do not share a duration");
        observe(true, 0, 2000);
        health.Reset();
        Check(observe(true, 0, 10000) == null, "stop/restart clears old confirmation");
        Console.WriteLine(_checks + " checks passed; no hardware accessed.");
        return 0;
    }
}
