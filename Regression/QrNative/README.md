# QR 完整 C++ 迁移验证（2026-09-07）

## 已确认结果

| 验证项 | 结果 / 口径 |
|---|---|
| 真实输入 | `E:\南京爱速智\CIS\moreMarks\二维码无法识别` 下 5 个子目录、53 张图 |
| 配置 | 使用本机 `bin/x64/Debug/net48/app_config.json`，未改用户参数 |
| 首轮 | 单帧及跨帧组合共 94 次调用；新旧均 15 次成功、0 个运行错误，逐项零差异 |
| 连续三轮 | 各 282 次调用，均 45 次成功、0 个运行错误；新旧结果逐项零差异 |
| 比较字段 | Found、文本、整数中心坐标、像素宽高（容差 1e-6）、成功策略、尝试次数、错误信息 |
| Release 契约 | 103 项通过；Gray/BGR/BGRA、行填充、输入只读、结构大小、长度/溢出、模型缺失、配置复制、同实例调用串行化、释放后拒绝调用等 |
| Debug 契约 | 同样 103 项通过；仅开发调试使用，正式 Debug 应用默认加载 Release 原生库 |
| 真实白码格式/缓冲区 | Release/Debug 各 13 项通过；Gray/BGR/BGRA 返回相同文本和几何；输出空间不足返回 -3，不返回截断成功 |
| 真实黑码格式/缓冲区 | Release 13 项通过；三种格式同样识别 `ZJ_202609020070007_RT`，中心 (965,1315)；原图和行填充未被修改 |
| 生命周期 | 每个配置执行 25 次 create/initialize/detect/dispose，未发生运行异常；内存观察不等同于泄漏证明 |
| 构建 | C++ Debug/Release 脚本完成；C# x64 Debug/Release 编译通过 |

53 张并非 53 个二维码：其中包含无二维码帧；跨帧也可能覆盖已在整帧中出现的对象。**15/94 不能当成识别率**。本轮证明的是这批输入的新旧行为一致，不代表所有未来图像均等价。

## 性能观察（不是生产节拍承诺）

三轮测试先运行 C++、再运行旧版，均使用 Release 程序、同一配置/模型/图片；计时只包围 Detect，不包含文件读取与输入缩放。

| 指标 | 旧 C# 调度 + OpenCvSharp | 完整 C++ |
|---|---:|---:|
| 282 次 Detect 累计 | 67.06 s | 63.96 s |
| 单次中位耗时 | 178.39 ms | 168.64 ms |
| 单次 P95 | 671.18 ms | 637.15 ms |
| 进程峰值工作集（含回放读图） | 318.15 MiB | 303.97 MiB |
| 各轮结束私有内存 | 162.95 / 132.69 / 151.78 MiB | 200.07 / 215.36 / 124.69 MiB |

本轮累计减少约 4.6%，但首轮单次回放曾出现反向波动（旧版 18.83 s、C++ 21.72 s）。因此**尚不能认定稳定提速**，后续需隔离后台负载、交替顺序、多批次测试。内存数据包含 GC、图像读取及分配器缓存；未证明全天运行无泄漏，也不能用峰值工作集替代非托管内存剖析。

## 数据与复现

- `legacy.json` / `native.json`：首轮原始记录。
- `legacy-repeat.json` / `native-repeat.json`：连续三轮记录；包含配置原文/哈希、模型哈希、图像哈希、调用信息和内存观察值。
- `native-final.json`：最终重建 DLL 回放；记录 DLL 哈希，需与同目录部署文件一致。
- C++ 构建日志与契约日志在 `obj/qr-native-build-*.log`、`obj/qr-contracts*.log`，属于可再生本机构建产物。
- 旧算法快照位于 `Tools/QrRegression/Legacy`，只改 namespace；从正式工程编译中排除。它不是可切换的生产后端，也不是识别失败后的备选。

```powershell
dotnet build Tools/QrRegression/QrRegression.csproj -c Release -p:Platform=x64
$exe = '.\Tools\QrRegression\bin\x64\Release\net48\QrRegression.exe'
& $exe contracts '.\Assets\WeChatQRCode'
& $exe capture '.\bin\x64\Debug\net48\app_config.json' '.\Assets\WeChatQRCode' 'E:\南京爱速智\CIS\moreMarks\二维码无法识别' '.\obj\qr-legacy.json' legacy 3
& $exe capture '.\bin\x64\Debug\net48\app_config.json' '.\Assets\WeChatQRCode' 'E:\南京爱速智\CIS\moreMarks\二维码无法识别' '.\obj\qr-native.json' native 3
& $exe compare '.\obj\qr-legacy.json' '.\obj\qr-native.json'
```

命令失败返回非零退出码，检查构建成功后再执行，避免误运行以前的 exe。C++ 库先完成构建，再编译复制 C# 输出，不要同时链接同一个 DLL 和复制它。Debug 原生库的 OpenCV 调试日志可能提示未找到可选 TBB/OpenMP 插件；实际构建使用 Windows 并行后端，并非必须部署这些可选插件。

## 仍需用户现场确认

1. 重启软件，用日常离线图库确认开始采集、跨帧识别、拼接段边界和下游 Mark 流程。
2. CameraLink 在线采集、长时间运行、连续多段与其他任务并行的节拍仍需现场验证。
3. 未重新修改/优化缺陷检测和图像对准；不能把 QR 独立回放当成整条视觉流水线验收。
4. 模型升级、OpenCV/IPP 选项、编译器或候选规则改变后重新回归；本轮不以潜在提速为由改变识别顺序或降低恢复能力。
