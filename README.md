# CIS_WebInspector

CIS_WebInspector 是面向连续烫画膜/CIS 线扫图像的 WPF 工业视觉检测程序。在线相机与离线图库共用同一条正式处理主线：

```text
采集/回放 → WeChatQRCode → 相邻二维码分段拼接 → 白墨质量检查
→ 上下大 Mark 全局对准 → 可选侧边小 Mark 非线性补偿
→ 排版零件裁切 → 零件级局部配准 → 内部/外部/细线断裂检测
→ 结果图、逐缺陷尺寸日志和批次汇总
```

在线与离线数据源共用 `MainViewModel.Acquisition.InitializeDataSource`；数据源生命周期由 `AcquisitionSession` 管理，有序帧消费由 `OrderedFrameProcessor` 保证。拼接后的作业由 `InspectionJobCoordinator` 串行协调，视觉主线位于 `InspectionJobRunner`，WPF 对象只在 UI Dispatcher 中创建和发布。

## 当前正式能力与关键口径

- 二维码：WeChatQRCode 常规识别结合定位框、自适应尺度、透视、反极性和模糊恢复；单帧和跨帧组合共用同一检测器。对于白墨完全缺失后形成的低对比度黑码，只有在模板确认三个定位框满足直角、尺寸和间距约束后，才保留原始极性并做一次灰度动态范围拉伸解码，不会在所有无二维码帧上无条件执行双极性全流程。
- 拼接保护：收集第二个二维码期间连续 `MaxFramesWithoutQr` 帧未命中，会放弃旧首码和累计段，防止拼接高度与内存无限增长。
- 全局对准：上下两排 20 mm Mark 使用单遍条带候选、倾斜行拟合、允许缺点的序列配对和 RANSAC Homography 质量门控。
- 侧边增强：左右 9 对 4 mm Mark 仅描述全局变换无法解释的边缘残差；由开关控制，失败时明确降级为全局对准。
- 局部配准：固定约 700 px 工作宽度，先尝试轮廓距离场平移，困难样本再使用双向 SIFT 匹配、RANSAC 相似变换和小范围边缘精修；失败继续使用全局对准裁图。
- 缺陷检测：内部缺陷、外部缺陷和细线断裂保持独立计数、颜色和结果集合。
- 面积口径：内部/外部面积门槛和日志面积均使用真实连通域像素数换算的 mm²；日志中的外接矩形宽高用于定位，`宽×高` 不作为缺陷面积。
- 结果语义：产品 Pass/Fail 与工程处理异常分开。`PatchProcessingStatus` 表示零件处理异常，`InspectionJobStatus/InspectionJobIssueCode` 表示段级执行状态。

## 接手代码时先看什么

1. 阅读 [项目全局导航与开发指南.md](项目全局导航与开发指南.md)，了解入口、数据流、坐标系、单位和降级规则。
2. 从 `App.xaml`、`Views/MainWindow.xaml`、`ViewModels/MainViewModel.*.cs` 追踪启动和 UI 调度。
3. 再按功能进入核心代码：

| 需求 | 流程协调 | 核心实现 | 主要结果 |
|---|---|---|---|
| 在线/离线输入 | `MainViewModel.Acquisition` | `AcquisitionSession`、`ICameraSource`、`OrderedFrameProcessor` | 独立帧、采集快照 |
| 二维码与拼接 | `ImageStitcher` | `QrCodeDetector*.cs` | `QrDetectionResult`、`StitchedImageResult` |
| 白墨墨量与拉丝 | `InspectionJobRunner` | `ImageAligner.WhiteInk.cs` | `WhiteInkInspectionResult` |
| 全局/侧边 Mark 对准 | `InspectionJobRunner` | `ImageAligner.*.cs` | `AlignmentResult`、Mark 诊断图 |
| 排版解析与零件裁切 | `InspectionJobRunner` | `DebugLogParser`、`PatchCropper` | `LayoutInfo`、零件 ROI |
| 零件局部配准 | `PatchDefectDetector` | `PatchDefectDetector.Alignment.cs` | 最终局部变换或安全回退 |
| 三类缺陷与尺寸日志 | `PatchCropper` | `PatchDefectDetector*.cs`、`InspectionJobRunner` | `PatchDefectResult`、`DefectGeometryMeasurement` |
| 作业取消与串行 | `MainViewModel.Inspection` | `InspectionJobCoordinator`、`InspectionJobRunner` | 最新作业结果、异常原因码 |
| 日志与 UI 追溯 | `AppLogger` | `MainViewModel.Logging/Preview` | 每日日志、冻结后的预览图 |

## 配置、输出与单位

- 正式运行参数保存在可执行文件目录的 `app_config.json`；数据源加载和检测作业分别使用不可变快照。
- 长度、边缘容差和线宽配置使用 mm；内部/外部面积阈值使用 mm²；进入算法后按 `LayoutDpi` 和当前检测尺度换算像素。
- `SaveCroppedImages` 是检测作业图像落盘总开关；Mark 诊断图、白墨预览、零件图、屏蔽图和全局结果图均受它控制。
- 常用诊断包括 `AlignmentMarks_CIS_Source.jpg`、`AlignmentMarks_TIFF_Layout.jpg`、`WhiteInk_BottomMarks_Preview.jpg`、`GlobalTiffReference.jpg` 和 `GlobalCisDefectResult.jpg`。
- 逐缺陷日志只记录最终通过门槛的结果，并输出外接矩形宽、高和真实缺陷面积（mm/mm/mm²）。

## 构建

```powershell
dotnet restore CIS_WebInspector.sln
dotnet build CIS_WebInspector.sln --no-restore -c Debug -p:Platform=x64
dotnet build CIS_WebInspector.sln --no-restore -c Release -p:Platform=x64
```

项目目标为 .NET Framework 4.8、WPF、x64，依赖 OpenCvSharp 4.10、WeChatQRCode 模型、Volans CameraLink SDK 和 TLC 原生组件。源码可编译不代表现场相机、采集卡、模型、排版数据和检测样本均已具备。

## 修改前后的最低动作

1. 固定测试图库、`app_config.json`、排版日志、排版原图和并行度。
2. 修改前执行 `Tools/Export-RegressionBaseline.ps1` 导出基线。
3. 修改后用同一输入重新运行，再执行 `Tools/Compare-RegressionBaseline.ps1`。
4. 同时核对缺陷计数、三类矩形、实际面积、局部配准结果、总耗时/P95 和 Private Bytes。
5. 不以“编译通过”代替算法回归，不用单个困难样本的改善代表整体效果。

详细方法见 [Regression/README.md](Regression/README.md)。
