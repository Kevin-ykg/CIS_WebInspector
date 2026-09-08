# 全局对准与白墨 C++ 迁移验证

日期：2026-09-08。迁移基准提交：`3a60aeaa8bbccee3d69e48636fe64d91d787d37c`。

## 本轮边界

- 迁移原 `ImageAligner` 的大 Mark 候选、倾斜行、缺点序列配对、RANSAC/质量门控、侧边残差网格、Warp/Remap、独立白墨检测。
- C# 保留公开方法、配置、结果模型、预览/保存及 UI 告警。未调整二维码、拼接、局部配准、三类缺陷规则、现场 `app_config.json`。
- 原 C# 的 8 个相关文件冻结在 `Tools/AlignmentRegression/Legacy`，仅修改命名空间及 `AppConfig` 类型别名；逐文件对照确认算法和注释未改。此目录只用于验证，不编译进生产程序，不是运行时后备。
- C++ 中保留并补充坐标系、物理单位、候选/配对意图、白墨核心/背景采样、共线约束、降级原因、结果及像素所有权注释。

## 测试输入与结果

测试从原始 JPG 通过当前正式 QR/拼接器生成 CIS 段；TIFF 按原流水线合成白底。没有使用已经二值化或保存后失真的零件图作为输入。

| 记录 | 输入 / 配置 | 覆盖结果 | 检查数 / 失败 |
|---|---|---|---|
| [batch12-16.json](batch12-16.json) | `E:/南京爱速智/CIS/Defect batch test/12-16`；当前配置，1100 mm 排版、1020 mm 排距 | 7803×12374；白墨正常；全局对准；侧边不足时两版同样降级 | 411 / 0 |
| [moremarks6.json](moremarks6.json) | `E:/南京爱速智/CIS/moreMarks/6细线断裂修改阈值`；仅测试进程覆盖 1000/920 mm，2026-06-24 排版 | 7803×11353；白墨正常；全局与侧边降级 | 465 / 0 |
| [moremarks1.json](moremarks1.json) | `E:/南京爱速智/CIS/moreMarks/1`；1000/920 mm，2026-07-09 排版 | 7803×11225；全局和实际 Nonlinear 模式；预热后 20 次重复 | 1103 / 0 |
| [white-noink.json](white-noink.json) | `E:/南京爱速智/CIS/moreMarks/白墨关闭`；不加载 TIFF | 7803×6066；NoInk、6 个采样圆，与原版相同 | 69 / 0 |
| [synthetic.json](synthetic.json) | 23 个小型合成情形，Release 原生 DLL | 正常/缺墨各档/拉丝/关闭/无法评估，倾斜、缺点、孤立补点、连续缺点降级、非连续 BGR ROI、无效参数 | 3298 / 0 |
| [synthetic-native-debug.json](synthetic-native-debug.json) | 同一组合成情形，Debug 原生 DLL | Debug 数值/输出图仍与基准一致；测试后恢复 Release DLL | 3298 / 0 |

“检查数”是多个情形中的字段、矩阵、像素和 ABI 断言总数，不代表独立生产样本数，也不代表缺陷检出率。Release 共 5346 项检查，全部通过。

核对内容：

1. H0 是否可用、最优二值阈值、最终模式和质量状态。
2. 上下排 Mark 的 TIFF 编号及 CIS/TIFF 圆心；H0 最大元素差要求 ≤1e-8。
3. 三列控制点的预期/实测坐标、插值/实测/虚拟标志、残差和留一指标，误差要求 ≤1e-7。
4. 白墨状态、拉丝标志、ROI、逐圆均值/方差/背景/对比度及相对白墨量，误差要求 ≤1e-7。
5. **全局 Warp 和非线性 Remap 输出逐像素一致（最大差值 0）**；源 CIS 不被修改。
6. 三组真实对准场景的 Mark 标注 JPEG，以及白墨预览 JPEG，均与原版字节一致。
7. ABI 配置大小、像素容量、非法模式、UTF-8 查询/短缓冲区、缺矩阵 Warp、输入输出重叠和 SafeHandle 重复释放。

另回跑现有 QR 的 103 项接口检查，以及零件检测 4 个合成对照/12 项接口检查，均通过。此次没有修改它们的算法源码。

## 重复性、内存和耗时

`moremarks1`：原生白墨 + 全局/侧边对准 + Remap，预热 3 次后执行 20 次。每次矩阵与首轮一致，完整输出图的像素总和一致。预热后单次创建、读取、Warp、释放的进程私有内存在 **1324462080～1327435776 B** 间波动，最后一次低于第一次，未观察到持续增长。该范围包含测试进程保留的 CIS/TIFF 和 CLR；不是“纯算法内存”。短程稳定不等于证明任意运行时长都无泄漏。

同机记录的阶段耗时示例（ms，来自 `moremarks1.json`）：

| 阶段 | 原 C# | 新 C++ |
|---|---:|---:|
| 独立白墨 | 82.8 | 32.0 |
| 大 Mark 全局矩阵，含白墨 | 298.6 | 159.8 |
| 同轮纯 WarpPerspective | 106.1 | 126.1 |
| 大/小 Mark 对准与网格，含白墨 | 216.3 | 172.0 |
| 同轮非线性 Remap | 450.0 | 210.4 |

这些是验证进程的阶段样本，受 JIT、缓存、CPU 调度及构建配置影响；**不是正式生产节拍基准，也不宣称每个阶段都变快**。保留完整耗时供同机后续复测。硬件在线采集、整班稳定性和完整缺陷流水线节拍仍需现场验证。

## 构建与部署记录

- C# Debug/Release x64：0 错误、0 警告。
- C++ Debug/Release x64：通过；Debug 合成对照也通过。首次 Debug 缺少 ximgproc 调试依赖，补编固定版本缓存后完成；编译器内存不足时用 64 位编译器/单项目并发重试，不改变优化算子选项。
- 正式 C# Debug/Release 输出均部署 Release DLL（原策略保持）。生成库与两处输出库 SHA-256 一致：`2E770628E6444170B569C05189DB5E6C14A772316968F79555247C23B74D60EB`。
- 运行配置 SHA-256：`2FDC7EBC6FDCD98B6CC35116F3C1CE333EC031861BF9CE2DC32325D064118287`。测试只在进程内覆盖其他图库的物理参数/排版地址，不回写配置。
- 诊断文本保留含义和关键字段；C++ 的 NaN 等数字格式可能与 .NET 大小写不同，不参与检测判定。

## 复现

```powershell
dotnet restore Tools/AlignmentRegression/AlignmentRegression.csproj --configfile Tools/AlignmentRegression/NuGet.Config -p:Platform=x64
dotnet build Tools/AlignmentRegression/AlignmentRegression.csproj -c Debug -p:Platform=x64 --no-restore
& Tools/AlignmentRegression/bin/x64/Debug/net48/AlignmentRegression.exe . --output synthetic
& Tools/AlignmentRegression/bin/x64/Debug/net48/AlignmentRegression.exe . --frames 'E:\南京爱速智\CIS\Defect batch test\12-16' --output batch12-16
& Tools/AlignmentRegression/bin/x64/Debug/net48/AlignmentRegression.exe . --frames 'E:\南京爱速智\CIS\moreMarks\1' --layout-log 'E:\南京爱速智\CIS\moreMarks\log\2026-07-09\Debug.log' --tiff-dir 'E:\南京爱速智\CIS\moreMarks\tiff' --row-spacing 920 --layout-height 1000 --output moremarks1 --repeat 20
```

离线 NuGet 配置使用本机已还原的包缓存；新电脑先按主 README 还原应用依赖。输出在 `obj/AlignmentNativeValidation/<output>`，包含本次生效配置、完整报告、真实 Mark/白墨预览。长期保留的是本目录报告和测试源码；生成图像/构建缓存可按项目现有清理规则处理。
