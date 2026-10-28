# CIS_WebInspector

CIS_WebInspector 是面向连续烫画膜/CIS 线扫图像的 WPF 工业视觉检测程序。在线相机与离线图库共用同一条正式处理主线：

```text
采集/回放 → WeChatQRCode → 相邻二维码分段拼接 → 白墨质量检查
→ 上下大 Mark 全局对准 → 可选侧边小 Mark 非线性补偿
→ 排版零件裁切 → 零件级局部配准 → 内部/外部/细线断裂检测
→ 结果图、逐缺陷尺寸日志和批次汇总
```

在线与离线数据源共用 `MainViewModel.Acquisition.InitializeDataSource`；数据源生命周期由 `AcquisitionSession` 管理，有序帧消费由 `OrderedFrameProcessor` 保证。拼接后的作业由 `InspectionJobCoordinator` 串行协调，视觉主线位于 `InspectionJobRunner`，WPF 对象只在 UI Dispatcher 中创建和发布。

## 开发交接入口

第一次接手先读 [docs/README.md](docs/README.md)；已有完整算法说明继续保留在 [项目全局导航与开发指南](项目全局导航与开发指南.md)。

- [职责边界与调用链](docs/职责边界与调用链.md)：从输入帧到作业、零件和结果，明确各文件该做/不该做什么。
- [数据接口与资源约定](docs/数据接口与资源约定.md)：坐标、mm/mm²、三种工作尺度、Mat/句柄所有权、取消语义。
- [开发调试与验证](docs/开发调试与验证.md)：构建、C#→C++ 断点、故障定位及结构修改前后比较。
- [维护记录](docs/维护记录.md)：本轮职责拆分、验证结果与未覆盖范围。

构建状态提醒（2026-10-28）：清理后的 `obj/VisionNative` 已出现恢复的 Release 产物，但本次实际原生增量构建因缺少 Windows SDK `10.0.26100.0` 失败，原生 Debug DLL/PDB 仍缺失，不能认定完整恢复成功。详见 [构建核查记录](docs/维护记录.md#2026-10-28原生-detect-断点交接与实际构建核查)；需要在 `patch_detector.cpp::detect` 调试时，按 [完整断点步骤](docs/开发调试与验证.md#在-patch_detectorcpp-的-detect-中打断点) 操作。

## 当前正式能力与关键口径

- 二维码：WeChatQRCode 常规识别结合定位框、自适应尺度、透视、反极性和模糊恢复；单帧和跨帧组合共用同一检测器。对于白墨完全缺失后形成的低对比度黑码，只有在模板确认三个定位框满足直角、尺寸和间距约束后，才保留原始极性并做一次灰度动态范围拉伸解码，不会在所有无二维码帧上无条件执行双极性全流程。
- 拼接保护：收集第二个二维码期间连续 `MaxFramesWithoutQr` 帧未命中，会放弃旧首码和累计段，防止拼接高度与内存无限增长。
- 全局对准：上下两排 20 mm Mark 使用单遍条带候选、倾斜行拟合、允许缺点的序列配对和 RANSAC Homography 质量门控。
- 侧边增强：左右 9 对 4 mm Mark 仅描述全局变换无法解释的边缘残差；由开关控制，失败时明确降级为全局对准。
- 局部配准（当前统一实现）：开启后，`PatchCropper` 在理论零件 ROI 外读取默认 15 mm 真实像素余量；`patch_alignment.cpp` 按 Alpha 内容与 DPI 建立稳定工作坐标，统一预处理并进行空间约束的双向 SIFT 匹配。同一组匹配点分别估计相似和受约束仿射，按固定模板分区的九区平均轮廓误差择优；两者均不可用且全局并非已足够好时才尝试局部单应性。匹配、几何、留区稳定性和错周期检查仍是安全条件；轮廓只评价候选，不生成或精修矩阵。最终缺陷始终只检查理论核心 ROI。
- 局部配准状态：通过安全检查且九区平均误差优于全局时使用局部矩阵；改善不足原优质标准或最差区超过 8 原图像素时标记 `AppliedDegraded`，继续使用该局部图检测并提示复核。当前已足够好可记 `NotNeeded`，无可信局部矩阵则记 `Review/Error` 并用全局图继续检测；关闭开关则记 `Disabled`。`NotNeeded` 和回退分支不额外执行局部 Warp。轮廓量只是筛选指标，不等同人工同名点误差。
- 裁框边缘干扰：若完整框内结构占 Alpha 前景至少 95%，少量被裁框截断的连通片段不再参与配准工作框/模板的生成；完整散点全部保留，不按最大连通域选目标。该处理只作用于配准副本，三类缺陷仍使用原始核心 Alpha。边界结构占比大或与主体相连时不擅自删除，仍可能提示复核；不是对任意外围内容或目标截断的一致性保证。
- 稳定性复核按不同的匹配坐标对分组、留出和重拟合，避免 SIFT 同角点多方向描述子重复计票。匹配、RANSAC 估计及其点数门槛不变；日志 `stabilityPoints` 单独表示稳定性复核的不同位置数，原匹配/内点数量仍按描述子匹配口径记录。
- 裁框一致性（2026-10-27）：仍直接使用 TIFF Alpha，不新增独立 PNG 依赖。先以覆盖至少 95% 核心前景的完整连通结构共同确定中心，保持排版逻辑尺寸建立固定配准窗口；必须有真实上下文，且保留原核心的全部前景，否则沿用原窗口并记录 `domain=layout/原因`。不按最大连通域删除散点，最终检测 ROI 不变。SIFT 保留最多 100 个空间均衡 DoG 点，仅对不同采样位置不足 8 个的分区补充转角 SIFT 描述子；不是每区固定追加 8 个。全部走同一匹配/稳健估计与安全检查，没有新增矩阵生成路径。已完成的回归、部署状态和耗时取舍见 [维护记录](docs/维护记录.md)，不能用一致性通过代替独立精度及缺陷保留验收。
- 先前大位移、仿射补充和稀疏模板方案已由当前统一实现替代；单次试验材料不作为现行效果依据。当前离线基准与 87 组人工对应点见 [统一配准验收修订](Regression/PatchNative/UnifiedRegistration/AcceptanceRevision_20261024.md)。
- 降级与失败语义：`AppliedDegraded` 使用局部图；没有可信矩阵的 `Review/Error` 使用全局图。两者均继续正常进行三类缺陷检测，照常计入 Pass/NG、绘制缺陷框和尺寸日志，同时独立提示“配准待复核”。全局预览保留正常红/绿零件框及类别框，用黄色 `ALIGN?` 提示核对；警示不改变产品结论，但残余错位可能影响缺陷结果。
- 缺陷检测：内部缺陷、外部缺陷和细线断裂保持独立计数、颜色和结果集合。
- 面积口径：内部/外部面积门槛和日志面积均使用真实连通域像素数换算的 mm²；日志中的外接矩形宽高用于定位，`宽×高` 不作为缺陷面积。
- 结果语义：产品 Pass/NG 与配准警示、工程处理异常分开。`PatchProcessingStatus` 为零件状态，`InspectionJobStatus/InspectionJobIssueCode` 为段级执行状态，`ProductInspectionStatus` 为整段产品结论；配准待复核不覆盖正常 Pass/NG，真正未检测或处理异常才使产品结论待复核。0.6 mm 是后续缺陷灵敏度目标；当前现场配置与 87 组约 3px/图的人工点不能证明已经达标。

## C++ 迁移状态与职责边界

核心视觉计算已集中到同一个 x64 原生库 `CISVisionCore.dll`，但按业务域拆成三套独立版本的 C ABI。正式程序不存在“C++ 失败后静默执行旧 C# 算法”的双路径；迁移前代码只保留在独立回归工具的 `Legacy` 目录中。

| 业务域 | C# 入口与保留职责 | C++ 正式实现 | C ABI |
|---|---|---|---|
| 二维码识别 | `QrCodeDetector`：配置快照、固定输入、结果转换、SafeHandle | `cis_qr_adapter`：安装 ROI/保护带；`qr_*`：通用识别、定位框、尺度与恢复 | `cis_qr_api.h` v2 |
| 全局/侧边对准 | `ImageAligner.GlobalTransform/Warp`：参数映射、诊断结果、预览保存 | `alignment_global.cpp`、`alignment_marks.cpp`、`alignment_side_grid.cpp`、`alignment_warp.cpp`：Mark 检测、RANSAC Homography、残差网格、Warp/Remap | `cis_alignment_api.h` v1 |
| 白墨检查 | `ImageAligner.WhiteInk`：开关、结果模型、日志/告警/预览 | `Native/src/alignment_white_ink.cpp`：底排 Mark、灰度/背景/对比度、墨量分档与拉丝判断 | `cis_alignment_api.h` v1 |
| 零件局部配准与缺陷 | `PatchCropper/PatchDefectDetector`：搜索余量与核心 ROI、并行调度、结果汇总和图像保存 | `patch_alignment.cpp`、`patch_periodicity.cpp`、`patch_detector.cpp`、`patch_fine_line.cpp` | `cis_patch_api.h` v2 |
| 帧拼接与应用流程 | `ImageStitcher`、`InspectionJobRunner`、`MainViewModel` | 不迁移：仍由 C# 负责状态机、作业编排和 WPF 线程边界 | — |

跨语言边界只传固定宽度 POD、像素地址/stride 和调用方分配的输出缓冲区，不传递 `cv::Mat`、STL 或 C++ 异常。输入像素只在同步调用期间借用；C++ 资源由 RAII 管理，C# 句柄由 `SafeHandle` 确定性释放。详细契约见 [Native/README.md](Native/README.md)。

### 二维码独立复用（2026-09-22）

`qr::QrDetector::detect(image)` 默认处理整张输入图，不再包含 CIS 横向 ROI、保护带或帧坐标取整；这些规则移至 `cis::CisQrAdapter`，旧 C ABI v2 与 C# 接口不变。两个入口链接同一份 `QrRecognition` 静态核心，不维护两套识别算法。

对外使用独立 `QrReader.dll / qr_reader_* v1`；C++ 源码和 DLL 两种例程、内存/坐标约定及构建交付方式见 [通用二维码接入指南](Native/QR_READER_README.md)。构建脚本为 `Tools/Build-QrReader.ps1`。

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
| 零件局部配准 | `PatchCropper`（核心 ROI+真实搜索余量）→ `PatchDefectDetector`（P/Invoke） | `Native/src/patch_alignment.cpp` → `patch_periodicity.cpp`；`patch_detector.cpp` 只采样核心 ROI | 结构化 `Applied/AppliedDegraded/NotNeeded/Review/Error/Disabled`、相似/受约束仿射/局部单应性及原因；逐件日志和裁切图标明实际方式 |
| 三类缺陷与尺寸日志 | `PatchCropper` | `Native/src/patch_detector.cpp`、`patch_fine_line.cpp`；C# 汇总/保存 | `PatchDefectResult`、`DefectGeometryMeasurement` |
| 作业取消与串行 | `MainViewModel.Inspection` | `InspectionJobCoordinator`、`InspectionJobRunner` | 最新作业结果、异常原因码 |
| 日志与 UI 追溯 | `AppLogger` | `MainViewModel.Logging/Preview` | 每日日志、冻结后的预览图 |

## 配置、输出与单位

- 正式运行参数保存在可执行文件目录的 `app_config.json`；数据源加载和检测作业分别使用独立深拷贝，运行期间按只读快照使用。
- 长度、边缘容差和线宽配置使用 mm；内部/外部面积阈值使用 mm²；进入算法后按 `LayoutDpi` 和当前检测尺度换算像素。
- `SaveCroppedImages` 是检测作业图像落盘总开关；Mark 诊断图、白墨预览、零件图、屏蔽图和全局结果图均受它控制。
- 常用诊断包括 `AlignmentMarks_CIS_Source.jpg`、`AlignmentMarks_TIFF_Layout.jpg`、`WhiteInk_BottomMarks_Preview.jpg`、`GlobalTiffReference.jpg` 和 `GlobalCisDefectResult.jpg`。
- 逐缺陷日志只记录最终通过门槛的结果，并输出外接矩形宽、高和真实缺陷面积（mm/mm/mm²）。

## 构建

Volans x64 SDK 的 `VolansGrabberCL_net.dll`、`VolansGrabberCL.dll` 和 `CamExpRemote.dll` 放在项目内 `lib/x64`，工程引用与构建复制均使用该相对路径，不再依赖仓库外的 `../../lib/x64`。这三个依赖文件需随源码一起提交，避免独立克隆后出现 `ares`、`VolansDevice` 或 `VolansEventHandler` 找不到的编译错误；在线采集仍需安装厂商驱动。

二维码、全局/侧边对准、白墨检查、零件局部配准与三类缺陷检测已迁入 C++。C# 保留拼接状态机、配置快照、同步借用像素、结果转换、批次调度和图像保存；C++ 通过稳定 C 接口封装模型、矩阵/控制网格、SIFT/模板缓存及临时图像。迁移时的等价性与后续局部配准算法调整属于不同阶段；不能拿迁移对照结果证明当前统一流程的精度或耗时。

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -Configuration Release
dotnet restore CIS_WebInspector.sln
dotnet build CIS_WebInspector.sln --no-restore -c Debug -p:Platform=x64
dotnet build CIS_WebInspector.sln --no-restore -c Release -p:Platform=x64
```

项目目标为 .NET Framework 4.8、WPF、x64，依赖 OpenCvSharp 4.10、CISVisionCore.dll、WeChatQRCode 模型、Volans CameraLink SDK 和 TLC 原生组件。C++ 首次构建需要 Visual Studio C++ 工作负载、CMake 和网络；之后复用 `obj/VisionNative` 的 OpenCV 4.10.0 + IPP 静态构建缓存。C# Debug/Release 默认都使用 Release 算法库，保证正常测试不受原生 Debug 性能影响。源码可编译不代表现场硬件和输入数据均已具备。

三项原生迁移原有独立对照工具；其中零件配准的旧版等价性数据只用于迁移历史，不能作为当前统一算法的效果证明：

| 验证域 | 工具 | 已记录的迁移等价性结果 | 详细记录 |
|---|---|---|---|
| 二维码 | `Tools/QrRegression` | 53 张输入的新旧结果逐项一致；QR C ABI 103 项契约检查通过 | [Regression/QrNative/README.md](Regression/QrNative/README.md) |
| 零件配准/三类缺陷 | `Tools/PatchRegression`、`Tools/RegistrationChecks` | 旧版迁移曾有 76 组等价结果；当前算法需重新做真实图库、缺陷保留、性能和重复性验收 | [统一配准进度](Regression/PatchNative/UnifiedRegistration/Progress_20261024.md) |
| 全局/侧边对准与白墨 | `Tools/AlignmentRegression` | Release 合计 5346 项字段、矩阵、像素和 ABI 检查通过；真实 Warp/Remap 输出逐像素一致 | [Regression/AlignmentNative/README.md](Regression/AlignmentNative/README.md) |

这些结果证明的是迁移前后等价性和接口边界，不等于所有生产样本的检出率、长期稳定性或节拍已经完成验收。

## 本轮职责整理（2026-10-17）

`InspectionJobRunner` 保留阶段编排，排版像素准备与结果日志分别进入 `.LayoutImage.cs`、`.Reporting.cs`；`PatchCropper` 保留 ROI/并行调度，全局预览与性能记录分别进入 `.Preview.cs`、`.Performance.cs`。原生定位框证据移至 `qr_finder_patterns.cpp`，模板缓存移至 `patch_template_cache.cpp`。公有入口、C ABI、配置字段、阈值与算法顺序不变。

注意：当前作业采用“最新优先”而非每段 FIFO；无 Alpha 原图不会执行零件差分。上述现状已补注释/说明，本轮没有修改业务策略。核验范围见 [维护记录](docs/维护记录.md)。

## 修改前后的最低动作

1. 固定测试图库、`app_config.json`、排版日志、排版原图和并行度。
2. 修改前执行 `Tools/Export-RegressionBaseline.ps1` 导出基线。
3. 修改后用同一输入重新运行，再执行 `Tools/Compare-RegressionBaseline.ps1`。
4. 同时核对缺陷计数、三类矩形、实际面积、局部配准结果、总耗时/P95 和 Private Bytes。
5. 不以“编译通过”代替算法回归，不用单个困难样本的改善代表整体效果。

详细方法见 [Regression/README.md](Regression/README.md)。
