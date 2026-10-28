# 相机连接判断检查

此工具直接编译正式 `Services/CameraConnectionHealth.cs`，不打开采集卡、不调用相机、不弹窗，不依赖图像算法 DLL。

```powershell
dotnet run --project Tools/CameraConnectionChecks/CameraConnectionChecks.csproj -c Release
```

16 项检查覆盖：单/多时钟、编码器停止时无 Line/Frame、短暂丢时钟、1 秒确认边界、持续状态读取失败、信号/读取恢复、不同异常不混算持续时间、停采重启后清空旧状态。

这仅验证判断逻辑，不验证驱动回调时机、Timer 调度、弹窗去重、实际停采或物理断电。实机步骤和限制见 `docs/开发调试与验证.md` 的“在线相机信号与采集异常告警”。
