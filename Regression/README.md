# 检测行为回归基线

本目录用于保护现有检测行为。第一轮重构只允许整理结构、注释和命名，不应改变二维码、拼接、对准、SIFT、差分或细线判定结果。

## 基线对象

每个有效案例必须同时记录：

- 输入图库、排版 JSON/Debug.log、TIFF/PNG 原图的明确版本和路径；
- 实际使用的 `app_config.json`；
- 二维码文本、位置、所在帧和拼接图尺寸；
- 白墨等级、Mark 数量、对准模式及降级原因；
- 每个 PartId 的通过状态，以及内部、外部、细线断裂三类结果；
- 运行日志、结果图、阶段耗时和进程内存数据。

`Export-RegressionBaseline.ps1` 不替代人工标注。它负责把一次已经确认有效的运行结果固化为可比较快照；真实缺陷是否正确仍由测试人员维护的案例清单决定。

## 推荐案例分组

| 分组 | 至少覆盖的场景 |
|---|---|
| QR | 正常白码、无白墨黑码、低对比度、模糊、形变、贴边、跨帧 |
| Stitch | 正常双二维码、二维码超时、恢复采集、文件夹未处理完 |
| WhiteInk | 正常、轻/中/重度缺墨、无墨、拉丝 |
| Alignment | 上下大 Mark、侧边 Mark 开/关、侧边网格降级 |
| Defect | 内部、外部、细线断裂、正常件、重复纹理和局部偏暗 |

## 使用方法

```powershell
# 修改前：对已经完成的一次运行建立快照
.\Tools\Export-RegressionBaseline.ps1 `
  -RunDirectory '.\bin\x64\Debug\net48\裁切结果' `
  -ConfigPath '.\bin\x64\Debug\net48\app_config.json' `
  -LogPath '.\bin\x64\Debug\net48\日志\SysRunLog_YYYYMMDD.txt' `
  -OutputPath '.\Regression\Baselines\before.json'

# 修改后：用同一配置和输入重新运行，再导出快照
.\Tools\Export-RegressionBaseline.ps1 `
  -RunDirectory '.\bin\x64\Debug\net48\裁切结果' `
  -ConfigPath '.\bin\x64\Debug\net48\app_config.json' `
  -LogPath '.\bin\x64\Debug\net48\日志\SysRunLog_YYYYMMDD.txt' `
  -OutputPath '.\Regression\Baselines\after.json'

.\Tools\Compare-RegressionBaseline.ps1 `
  -ExpectedPath '.\Regression\Baselines\before.json' `
  -ActualPath '.\Regression\Baselines\after.json'
```

## 判定原则

- P0～P3 重构：关键结果日志、输出文件集合、图像尺寸和内容哈希应保持一致。
- 日志时间戳和基线生成时间不参与比较。
- 如果可视化图片含有运行时间等非确定内容，应把它列入人工检查项，不能简单放宽所有图片比较。
- 并行执行造成日志行顺序变化时，比较脚本按规范化后的文本排序，不依赖行号。
- 任何差异都要记录为“预期变化”或“回归问题”，不得静默接受。
