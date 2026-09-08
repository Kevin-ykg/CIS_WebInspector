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

## C++ 迁移状态与职责边界

核心视觉计算已集中到同一个 x64 原生库 `CISVisionCore.dll`，但按业务域拆成三套独立版本的 C ABI。正式程序不存在“C++ 失败后静默执行旧 C# 算法”的双路径；迁移前代码只保留在独立回归工具的 `Legacy` 目录中。

| 业务域 | C# 入口与保留职责 | C++ 正式实现 | C ABI |
|---|---|---|---|
| 二维码识别 | `QrCodeDetector`：配置快照、固定输入、结果转换、SafeHandle | `Native/src/qr_*`：预处理、定位框、自适应尺度、透视/黑码/模糊恢复、WeChatQRCode | `cis_qr_api.h` v2 |
| 全局/侧边对准 | `ImageAligner.GlobalTransform/Warp`：参数映射、诊断结果、预览保存 | `alignment_global.cpp`、`alignment_marks.cpp`、`alignment_side_grid.cpp`、`alignment_warp.cpp`：Mark 检测、RANSAC Homography、残差网格、Warp/Remap | `cis_alignment_api.h` v1 |
| 白墨检查 | `ImageAligner.WhiteInk`：开关、结果模型、日志/告警/预览 | `Native/src/alignment_white_ink.cpp`：底排 Mark、灰度/背景/对比度、墨量分档与拉丝判断 | `cis_alignment_api.h` v1 |
| 零件局部配准与缺陷 | `PatchCropper/PatchDefectDetector`：ROI、并行调度、结果汇总和图像保存 | `patch_alignment.cpp`、`patch_detector.cpp`、`patch_fine_line.cpp`：局部配准、内部/外部/细线断裂检测和物理测量 | `cis_patch_api.h` v1 |
| 帧拼接与应用流程 | `ImageStitcher`、`InspectionJobRunner`、`MainViewModel` | 不迁移：仍由 C# 负责状态机、作业编排和 WPF 线程边界 | — |

跨语言边界只传固定宽度 POD、像素地址/stride 和调用方分配的输出缓冲区，不传递 `cv::Mat`、STL 或 C++ 异常。输入像素只在同步调用期间借用；C++ 资源由 RAII 管理，C# 句柄由 `SafeHandle` 确定性释放。详细契约见 [Native/README.md](Native/README.md)。

## 接手代码时先看什么

1. 阅读 [项目全局导航与开发指南.md](项目全局导航与开发指南.md)，了解入口、数据流、坐标系、单位和降级规则。
2. 从 `App.xaml`、`Views/MainWindow.xaml`、`ViewModels/MainViewModel.*.cs` 追踪启动和 UI 调度。
3. 再按功能进入核心代码：

| 需求 | 流程协调 | 核心实现 | 主要结果 |
|---|---|---|---|
| 在线/离线输入 | `MainViewModel.Acquisition` | `AcquisitionSession`、`ICameraSource`、`OrderedFrameProcessor` | 独立帧、采集快照 |
| 二维码与拼接 | `ImageStitcher` | `QrCodeDetector.cs`（P/Invoke）→ `Native/src/qr_detector.cpp` | `QrDetectionResult`、`StitchedImageResult` |
| 白墨墨量与拉丝 | `InspectionJobRunner` → `ImageAligner.WhiteInk.cs` | `Native/src/alignment_white_ink.cpp` | `WhiteInkInspectionResult` |
| 全局/侧边 Mark 对准 | `InspectionJobRunner` → `ImageAligner`（P/Invoke） | `Native/src/alignment_global.cpp`、`alignment_marks.cpp`、`alignment_side_grid.cpp`、`alignment_warp.cpp` | `AlignmentResult`、Mark 诊断图 |
| 排版解析与零件裁切 | `InspectionJobRunner` | `DebugLogParser`、`PatchCropper` | `LayoutInfo`、零件 ROI |
| 零件局部配准 | `PatchDefectDetector`（P/Invoke） | `Native/src/patch_alignment.cpp` | 最终局部变换或安全回退 |
| 三类缺陷与尺寸日志 | `PatchCropper` | `Native/src/patch_detector.cpp`、`patch_fine_line.cpp`；C# 汇总/保存 | `PatchDefectResult`、`DefectGeometryMeasurement` |
| 作业取消与串行 | `MainViewModel.Inspection` | `InspectionJobCoordinator`、`InspectionJobRunner` | 最新作业结果、异常原因码 |
| 日志与 UI 追溯 | `AppLogger` | `MainViewModel.Logging/Preview` | 每日日志、冻结后的预览图 |

## 配置、输出与单位

- 正式运行参数保存在可执行文件目录的 `app_config.json`；数据源加载和检测作业分别使用不可变快照。
- 长度、边缘容差和线宽配置使用 mm；内部/外部面积阈值使用 mm²；进入算法后按 `LayoutDpi` 和当前检测尺度换算像素。
- `SaveCroppedImages` 是检测作业图像落盘总开关；Mark 诊断图、白墨预览、零件图、屏蔽图和全局结果图均受它控制。
- 常用诊断包括 `AlignmentMarks_CIS_Source.jpg`、`AlignmentMarks_TIFF_Layout.jpg`、`WhiteInk_BottomMarks_Preview.jpg`、`GlobalTiffReference.jpg` 和 `GlobalCisDefectResult.jpg`。
- 逐缺陷日志只记录最终通过门槛的结果，并输出外接矩形宽、高和真实缺陷面积（mm/mm/mm²）。

## 构建

二维码、全局/侧边对准、白墨检查、零件局部配准与三类缺陷检测已迁入 C++。C# 保留拼接状态机、配置快照、同步借用像素、结果转换、批次调度和图像保存；C++ 通过稳定 C 接口封装模型、矩阵/控制网格、SIFT/模板缓存及临时图像。算法顺序与参数口径不因迁移而改变。

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -Configuration Release
dotnet restore CIS_WebInspector.sln
dotnet build CIS_WebInspector.sln --no-restore -c Debug -p:Platform=x64
dotnet build CIS_WebInspector.sln --no-restore -c Release -p:Platform=x64
```

项目目标为 .NET Framework 4.8、WPF、x64，依赖 OpenCvSharp 4.10、CISVisionCore.dll、WeChatQRCode 模型、Volans CameraLink SDK 和 TLC 原生组件。C++ 首次构建需要 Visual Studio C++ 工作负载、CMake 和网络；之后复用 `obj/VisionNative` 的 OpenCV 4.10.0 + IPP 静态构建缓存。C# Debug/Release 默认都使用 Release 算法库，保证正常测试不受原生 Debug 性能影响。源码可编译不代表现场硬件和输入数据均已具备。

三项原生迁移都有独立的新旧实现对照工具，迁移前快照只供离线比较，不编译进正式程序：

| 验证域 | 工具 | 已记录的迁移等价性结果 | 详细记录 |
|---|---|---|---|
| 二维码 | `Tools/QrRegression` | 53 张输入的新旧结果逐项一致；QR C ABI 103 项契约检查通过 | [Regression/QrNative/README.md](Regression/QrNative/README.md) |
| 零件配准/三类缺陷 | `Tools/PatchRegression` | 76 组不同输入/配置的新旧结果一致；输出 PNG 逐像素一致 | [Regression/PatchNative/README.md](Regression/PatchNative/README.md) |
| 全局/侧边对准与白墨 | `Tools/AlignmentRegression` | Release 合计 5346 项字段、矩阵、像素和 ABI 检查通过；真实 Warp/Remap 输出逐像素一致 | [Regression/AlignmentNative/README.md](Regression/AlignmentNative/README.md) |

这些结果证明的是迁移前后等价性和接口边界，不等于所有生产样本的检出率、长期稳定性或节拍已经完成验收。

## 修改前后的最低动作

1. 固定测试图库、`app_config.json`、排版日志、排版原图和并行度。
2. 修改前执行 `Tools/Export-RegressionBaseline.ps1` 导出基线。
3. 修改后用同一输入重新运行，再执行 `Tools/Compare-RegressionBaseline.ps1`。
4. 同时核对缺陷计数、三类矩形、实际面积、局部配准结果、总耗时/P95 和 Private Bytes。
5. 不以“编译通过”代替算法回归，不用单个困难样本的改善代表整体效果。

详细方法见 [Regression/README.md](Regression/README.md)。
