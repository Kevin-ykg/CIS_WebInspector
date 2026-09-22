# C++ 视觉算法库 CISVisionCore

## 二维码：边界与调用关系

```text
ImageStitcher（单帧 / 跨帧组合，仍为 C#）
  → QrCodeDetector.Detect（C#：参数快照 / 固定像素数组 / SafeHandle）
    → cis_qr_detect（稳定 C ABI，单次同步调用）
      → cis::CisQrAdapter::detect（CIS ROI / 保护带 / 坐标映射）
        → qr::QrDetector::detect（通用完整 C++ 识别流程）
        → cv::wechat_qrcode::WeChatQRCode::detectAndDecode
    ← 文本、输入图中的中心 / 既有宽高、尝试次数、策略、错误
  ← QrDetectionResult（原数据模型，拼接和 Mark 尺度计算无须改动）
```

灰度转换、ROI 与保护带、极性处理、定位框证据、自适应尺度、局部静区、透视展开、低对比度和模糊恢复均在 C++ 内。原有候选顺序、阈值、坐标还原和取整规则沿用迁移前实现；2026-09-22 在旧恢复全部失败之后新增了三定位框门控的局部模糊/形变恢复，见 `qr_blur_local.cpp`。局部配准/缺陷、全局/侧边对准和白墨算法也已迁入同一 DLL；拼接状态机、界面、告警调度和可视化仍在 C#。

2026-09-22 拆分：通用核心默认整图、黑色前景优先；CIS 适配层仍使用原固定 ROI/保护带、配置极性及预热尺寸。可选 `DetectHints.evidence_region` 仅用于保留原 core 与工作图的统计边界，默认整图，不是隐藏的裁图规则。`CISVisionCore` 和独立 `QrReader` 链接同一个静态核心；正式程序不依赖 `QrReader.dll`。对外使用说明见 [QR_READER_README.md](QR_READER_README.md)。

| 文件 | 职责 |
|---|---|
| `include/cis_qr_api.h` | 二维码 C 接口，固定宽度字段、状态码、调用约定 |
| `src/qr_api.cpp` | 参数/长度检查、上下文锁、输出缓冲区、异常屏障 |
| `src/cis_qr_adapter.*` | CIS 横向 ROI/保护带、配置映射、帧坐标还原与最终取整 |
| `include/qr/qr_detector.h` | 通用 C++ 源码/静态链接接口，浮点输入图坐标 |
| `include/qr_reader_api.h`、`src/qr_reader_api.cpp` | 对外独立通用 C ABI v1，不带 CIS ROI 字段 |
| `src/qr_detector.h` | 内部模型实例、候选数据结构、坐标计算约定 |
| `src/qr_detector.cpp` | 主流程、模型加载与复用、WeChatQRCode 调用、成功结果恢复 |
| `src/qr_geometry.cpp` | 嵌套定位框、几何证据、自适应尺度、局部/透视候选 |
| `src/qr_recovery.cpp` | 透视、低对比度、黑码原极性、模糊模板与模块结构校验 |
| `src/qr_blur_local.cpp` | 原路径失败后的局部红通道解码、三定位框外边界几何恢复；至多 4 次额外解码 |
| `../Services/QrCodeDetector.cs` | C# 唯一入口；没有第二套算法或静默回退 |

## 二维码：内存与接口契约

- Windows x64，C ABI **版本 2**，`__cdecl`，默认 8 字节对齐；Config 为 32 字节，Result 为 48 字节。先查 `cis_qr_abi_version`，不兼容时明确报错。
- `create → configure → initialize → detect（多次）→ destroy`。模型按实例持有；初始化/预热可以重复调用。改变配置时复制尺度数组，外部数组不长期借用。
- 输入为 Gray8 / BGR24 / BGRA32，正 stride 可包含行填充；缓冲区同步只读借用。C# 在检测期间固定 byte[]，返回后立即解除。DLL 不保留像素指针，不跨 DLL 传递 `cv::Mat`。
- C++ 的 `cv::Mat` 和容器作用域结束自动释放，模型由 `unique_ptr` 持有；C# 通过 `SafeHandle` 释放原生上下文，调用/配置/Dispose 串行化。直接 C 调用者不得与正在执行的调用并发 destroy，也不得使用已经销毁的句柄。
- 坐标相对传入图像；跨帧组合仍由 ImageStitcher 加回全局坐标。常规路径宽高为 X/Y 投影，透视恢复路径为原四角平均对边长度，这是既有分支差异，本轮不更改。通用结果用 `dimensions_are_side_lengths/size_kind` 标明；旧 CIS ABI 保持原字段和数值。适配层先加回 ROI 偏移，再按 .NET midpoint-to-even 取整，保留原先 float/double 运算边界。
- 输出文本为 UTF-8，模型目录参数为 Windows UTF-16；OpenCV 模型文件打开仍要求目录可由 Windows 系统代码页表示，不支持的路径明确失败。部署到同一中文 Windows 代码页或纯英文目录最稳妥。
- 调用方分配结果及字符缓冲区，不导出 STL、不要求跨运行库释放内存。缓冲区不足返回 `-3` 和需要的字节数（含 NUL），不会把截断文本作为成功结果。重新调用需要重新检测，当前没有结果缓存句柄。
- 状态：`0` 成功，`1` 正常未检出，`-1` 参数错误，`-2` 模型/算法异常，`-3` 输出空间不足。C++ 异常不越过 C 边界；原生非法指针属于调用方违反契约，不能靠异常屏障保证安全。
- 本轮不设置 OpenCV 全局线程数，不改变正式帧队列；同一个 QR 实例只做一帧检测。独立 DLL 和 OpenCvSharp 不共享 Mat、分配器或全局设置。

## 全局对准与白墨：边界与调用关系

```text
InspectionJobRunner
  ├─ ImageAligner.InspectBottomWhiteInk（不依赖 TIFF）
  │   → cis_alignment_compute(mode=1) → inspect_bottom / inspect_white
  │   ← 白墨状态、均值/方差/背景、相对墨量、采样圆及诊断
  └─ ImageAligner.ComputeTransform
      → cis_alignment_compute(mode=0)
        → 大 Mark 提取 → 倾斜行 → 缺点序列配对 → RANSAC + 质量门控
        → 可选 side_grid（通过则非线性，不通过则 GlobalOnly/Degraded）
      ← AlignmentResult：SafeHandle + 小型诊断副本
      → ImageAligner.WarpToTiffSpace → cis_alignment_warp
        → WarpPerspective 或 256 行逆向 Remap（一次采样）
```

| 文件 | 职责 / 原实现定位 |
|---|---|
| `include/cis_alignment_api.h` | 独立 alignment ABI v1，原图描述、参数、结果读取与释放 |
| `src/alignment_api.cpp` | C 异常屏障、步长/容量/重叠检查、结果句柄、UTF-8 诊断 |
| `src/alignment_internal.h` | 私有 RAII 数据、坐标约定、取中位数/舍入等通用函数 |
| `src/alignment_global.cpp` | 原 `ComputeTransform`、`ComputeRobustTransform`、Homography 门控 |
| `src/alignment_marks.cpp` | 原 `DetectTiff/DetectJpg`、倾斜行、动态规划缺点配对、小圆评分 |
| `src/alignment_side_grid.cpp` | 原 `TryBuildSideGrid`、MAD、孤立补点、拓扑/尺度门控及留一统计 |
| `src/alignment_white_ink.cpp` | 原 `InspectBottomWhiteInk/InspectWhiteInk`、Hough 共线约束及灰度分级 |
| `src/alignment_warp.cpp` | 原 `WarpToTiffSpace/FillRemapStripe`，PPL 独立行建图与分块 Remap |
| `../Services/AlignmentNativeInterop.cs` | P/Invoke、布局校验、SafeHandle、小型结果复制 |
| `../Services/ImageAligner.*.cs` | 保留公共入口/统计/原预览，不包含第二套运行算法 |

### 接口与资源约定

- Windows x64，`__cdecl`、pack=8；Image=40、Anchor=48、Config=192、Summary=296、Mark=40、Control=96、Sample=64 字节。两端同时验证 ABI；二维码/零件 ABI 版本不变。
- 配置为作业快照，长度用 mm，图像坐标用原分辨率 px，全局行号用 `int64_t`。第二码全局 Y 减去拼接段起点后才用于图内定位；X/Y 分别由二维码宽/高标定。
- `compute → summary/marks/controls/samples/log → warp → destroy`。结果拥有原生 H、逆矩阵及控制网格，不持有 CIS/TIFF 大图。可预期的点数/质量不足返回 `0 + has_transform=0 + diagnostic`，已完成的白墨结果仍可读取。
- C# `AlignmentResult.Dispose` 释放原生 SafeHandle 和两份诊断 Mat；`GlobalTransform` 是只读使用的诊断副本，不能通过改写该副本控制原生 Warp。
- `warp` 的目标 Mat 由 C# 分配，DLL 直接写像素，不跨 CRT 释放；原图与输出禁止重叠。任何失败时上层丢弃输出 Mat。不同结果可独立使用，同一结果的 Warp/读取/释放不得并发。
- 算法关闭/正常未满足条件与 ABI 错误分开：负值 `-1/-2/-3` 为参数/运行异常/缓冲区不足；不通过隐藏 C# 回退掩盖 DLL 缺失或版本错误。
- 独立白墨的 Hough 后备和共线圆心约束保留；百分比是相对白墨灰度指标，不是真实墨水体积计量。拉丝仍按原标准差门槛，不在迁移时换算法。
- C# 的 Mark 叠加图、白墨预览、文件名、颜色、保存开关和 WPF 告警逻辑不变。可视化仅修改副本，不能污染后续差分输入。
- 回归工具及迁移前快照：`Tools/AlignmentRegression`；报告：`Regression/AlignmentNative`。对照快照不参与生产构建。

## 零件配准与三类缺陷：边界与调用关系

```text
PatchCropper（C#：统一 ROI、并行度、每 worker 上下文、结果汇总）
  → PatchDefectDetector.Detect（C#：配置快照/借用像素）
    → cis_patch_detect（每件一次同步计算）
      → cis::patch::detect
        ├─ local_align：既有分级局部配准，返回矩阵/诊断
        ├─ threshold / morphology / components：内部与外部缺陷
        └─ fine_line：缺口候选/骨架/截面/端点/共同位移/合并
    ← result 句柄：三类框、mm/mm²、诊断、按需输出图
  ← PatchDefectResult（C#：原结果模型/日志/原颜色/文件命名）
```

| 文件 | 职责与原方法对应 |
|---|---|
| `include/cis_patch_api.h` | 零件检测 C ABI v1；输入/配置/结果结构体、资源生命周期 |
| `src/patch_api.cpp` | 长度/步长检查、异常屏障、worker 锁、结果复制；不包含阈值算法 |
| `src/patch_detector.h` | 私有 RAII 类型、模板缓存、worker、结果所有权 |
| `src/patch_constants.h` | 原局部配准/细线常量与原说明；不是另一份用户配置 |
| `src/patch_detector.cpp` | 原 `DetectCore`、物理单位换算、最终 Warp、普通连通域判定与测量 |
| `src/patch_alignment.cpp` | 原 `TryLocalAlign`、轮廓评分/精修、SIFT/RANSAC、模板缓存 |
| `src/patch_fine_line.cpp` | 原 `DetectFineLineBreaksAtDetailScale`、边界带、骨架/端点/抗错位证据 |
| `../Services/PatchNativeInterop.cs` | P/Invoke 布局、SafeHandle、只读输入与输出图复制 |
| `../Services/PatchDefectDetector.cs` | 保留原 `Detect` 调用接口，转换结果，不重复检测 |
| `../Services/PatchDefectDetector.Visualization.cs` | 只负责原有结果图/屏蔽图保存；不再包含算法 |

原 `PatchDefectDetector.Alignment.cs`、`.FineLine.cs` 不再存在于正式 Services；冻结快照只在开发比较工具中。注释按流程迁入对应 C++ 文件，并补充坐标/尺度、截面与锚段含义、面积口径、RAII/缓存锁等说明。C++ helper 上保留原 C# 方法名，便于查找迁移位置。

### 行为保持约定

- 本次不调整配准分支、SIFT 数量、RANSAC 门槛、插值、随机种子或缺陷门槛。当前实际主线仍为轮廓平移 → SIFT 相似变换 → 小范围精修/质量门控，失败继续使用全局裁图。
- 配准约 700 px 工作宽度，普通差分用 `DefectDetectScale`，细线用至少 0.5 且不超过 1 的细节尺度。尺度转换只修改矩阵平移项；不得把低分辨率对齐图放大作为细线输入。
- 普通缺陷先在原始差分中标记连通域；完全在屏蔽区才忽略，一旦越过屏蔽区保留完整连通域，以 `CC_STAT_AREA` 严格大于面积阈值判定。
- 细线从边缘屏蔽区内的缺失证据出发，统一校验骨架长度、模板线宽、横截面、相反方向两个锚段、共同小位移与合并。既有 0.5 mm 最小支持长度和线宽栅格余量保持不变。
- 细线计数与内部/外部独立；细线面积仍按最终断口骨架掩膜非零像素数换算，不擅自改成填充截面面积。扩展标注框也不参与面积计算。

### 零件 C ABI 与内存契约

- Windows x64、`__cdecl`、显式 pack=8、`cis_patch_abi_version()==1`；二维码 ABI v2 不变。输入/配置/汇总/缓存统计描述的 `struct_size` 必须填正确值，C# 与 C++ 均检查布局；缺陷数组记录为固定布局输出，不含该字段。
- `cache_create → worker_create → detect → result读取 → result_destroy → worker_destroy → cache_destroy`。每批一个缓存，每并行 worker 一个上下文；SIFT/Matcher 按需创建并复用。worker 通过 `shared_ptr` 持有缓存。
- 输入仅借用 Gray8/BGR24/BGRA32 的像素，支持有行间隔的 ROI；有效长度至少 `(height-1)*stride+width*channels`。调用期间不能修改/释放输入；函数返回后 DLL 不持有输入地址。
- 输出不跨库共享 Mat/STL。`detect` 成功后返回结果句柄，读取 summary/defects/log/images 不重复检测；C# 分配图像后按行复制，结果句柄释放不影响保存/UI。
- `output_flags` 的 1/2/4 分别请求缺陷三联图素材、原尺寸屏蔽图、原尺寸 CIS 二值图。关闭图像保存仍执行同一算法，只省略不需要的输出拷贝。
- `alignment_ms` 为局部配准时长；`detection_ms` 为整个原生单件调用时长（**包含**配准，二者不可相加），不包含 C# PNG/JPEG 编码与日志。
- `0` 成功（也可能没有缺陷），`-1` 参数错误，`-2` 原生异常，`-3` 日志/缺陷数组不足。图像复制的无效容量返回 `-1`，复制前整体检查，不部分写入。错误和日志为 UTF-8，长度含结尾 NUL。
- 同一个 worker 内部串行；其他 worker 可并行。共享模板分桶锁与短 RANSAC 锁保留原调度语义，不新增整批全局锁。
- 句柄只能由创建它的 DLL 销毁。直接 C 调用者不能并发销毁在用句柄、重复销毁或传伪造指针；异常屏障不等于内存越界隔离。C# 使用 SafeHandle 和调用/Dispose 同步约束。

## 构建与部署

前置：Visual Studio「使用 C++ 的桌面开发」、Windows SDK、CMake、.NET Framework 4.8 开发环境。当前机器使用 MSVC 14.51（VS 18 Community），目标 x64。

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -Configuration Release
dotnet build CIS_WebInspector.sln -c Debug -p:Platform=x64
dotnet build CIS_WebInspector.sln -c Release -p:Platform=x64
```

首次下载并编译 OpenCV / opencv_contrib **4.10.0**，脚本校验下载包 SHA-256；保留 IPP，关闭不需要的测试、Python、视频与 OpenCL。缓存位于 `obj/VisionNative`，可删除但删除后要重新执行脚本。首次构建可能需较长时间和较多磁盘空间；增量构建复用已编译依赖。源代码在 `Native`，不能把 obj 中生成的 vcxproj 当作维护入口。

零件检测新增 `features2d`、`calib3d`、`ximgproc`，构建系统还会选入它们所需的 `flann`/`video` 等模块；关闭的是不需要的视频采集后端，不是依赖中的 `video` 算法模块。部署仍只增加同一个 `CISVisionCore.dll`，无需另装 OpenCV DLL。补充的 FLANN/BSD 许可也随构建复制。

默认 C# Debug/Release 均复制 **Release C++ DLL**，避免 Debug 算法库影响正常测试耗时。需要原生逐行调试时：

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -Configuration Debug
dotnet build CIS_WebInspector.sln -c Debug -p:Platform=x64 -p:VisionNativeConfiguration=Debug
```

Visual Studio 启用本机代码调试即可混合调试。调试完成后用默认命令重建，恢复 Release 算法库。

部署需要新增 `CISVisionCore.dll`，保留四个 WeChatQRCode 模型和项目其他已有 DLL。C++ OpenCV 为静态链接，不再额外部署一套 opencv_world DLL；仍需匹配编译工具集的 VC++ x64 运行库。Debug DLL 依赖开发机调试运行库，不用于生产部署。C# 项目检测 DLL 缺失，并在构建/发布时复制 DLL。

依赖来源与许可：[OpenCV 4.10.0](https://github.com/opencv/opencv/tree/4.10.0)、[opencv_contrib 4.10.0](https://github.com/opencv/opencv_contrib/tree/4.10.0)。OpenCV/contrib 及 IPP、Protobuf、zlib 等静态依赖的原始许可收录于 `Native/licenses`，构建/发布时复制到 `ThirdPartyLicenses/CISVisionCore`；模型许可继续使用 `Assets/WeChatQRCode` 内现有文件。升级依赖时同时核对许可和重新回归，不能仅验证可编译。

## 如何验证

独立开发工具在 `Tools/QrRegression`，迁移前 C# 快照只在该工具中编译，不会成为正式程序的运行分支。用相同配置、模型和样本对比，详见 [验证记录](../Regression/QrNative/README.md)。

零件迁移验证工具为 `Tools/PatchRegression`，冻结 C# 基准与正式 C++ 对比结果见 [零件迁移验证记录](../Regression/PatchNative/README.md)。它覆盖真实 H0 裁图、独立尺度、三类结果、逐像素保存图、无输出图开关、共享缓存/并发 worker 和错误接口参数，不替代现场长时间压力测试。

迁移目的首先是封装与生命周期清晰，**不承诺改语言即可提速或提高识别率**。原 OpenCvSharp 的主要算子本来就在 C++ 执行；收益/代价需分别观察识别耗时、跨边界调用、私有内存和部署体积。
