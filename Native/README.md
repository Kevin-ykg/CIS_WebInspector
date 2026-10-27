# C++ 视觉算法库 CISVisionCore

项目交接入口见 [docs](../docs/README.md)。本页维护 ABI、资源和原生算法模块契约；当前职责拆分与验证见 [维护记录](../docs/维护记录.md)。

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

2026-10-09：`qr_blur_local.cpp` 在 Otsu 定位框边界无法完成解码后，增加按实测模块宽度设置窗口的自适应边界测量，解决小尺寸失焦码的局部光照偏差。两种测量共用既有四次局部解码预算，保留原始灰度、几何和模块结构检查。`QrDetector::detect` 的严重失焦阶段自动进入该分支，正式 CIS DLL 与 SDK 共用实现。验证入口为 `QrReaderOutlineChecks`，样本保存在 `Regression/QrReusable/samples/tight_blurred_22.jpg`。

2026-10-25：`qr_recovery.cpp` 对存在纵向码格间距差异的困难码增加受限兜底：三定位框约束局部搜索，独立定位四角，按上/下定位框外边界作单调分段 Y 校正，再由 WeChatQRCode 解码验证。正常码仍走原快速路径；失真成因不由软件推断。样本、正确文本和回放结果见 [纵向行距失真回归](../Regression/QrReusable/LineScanRowSpacing_20261022.md)。

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
| `src/qr_recovery.cpp` | 透视、低对比度、黑码原极性、模糊恢复调度 |
| `src/qr_finder_patterns.cpp` | 模糊定位框模板匹配、三点几何和模块结构证据（从恢复文件独立，不改评分） |
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
PatchCropper（C#：核心 ROI + 真实搜索余量、并行度、结果汇总）
  → PatchDefectDetector.Detect（C#：配置快照/借用像素）
    → cis_patch_detect（每件一次同步计算）
      → cis::patch::detect
        ├─ register_patch：稳定工作坐标 → 同组匹配估计相似/仿射 → 必要时单应候选 → 质量与周期复核
        ├─ threshold / morphology / components：内部与外部缺陷
        └─ fine_line：缺口候选/骨架/截面/端点/共同位移/合并
    ← result 句柄：三类框、mm/mm²、诊断、按需输出图
  ← PatchDefectResult（C#：原结果模型/日志/原颜色/文件命名）
```

| 文件 | 职责与原方法对应 |
|---|---|
| `include/cis_patch_api.h` | 零件检测 C ABI v2；核心 ROI、结构化配准状态、输入/配置/结果生命周期 |
| `src/patch_api.cpp` | 长度/步长检查、异常屏障、worker 锁、结果复制；不包含阈值算法 |
| `src/patch_detector.h` | 私有 RAII 类型、模板缓存、worker、结果所有权 |
| `src/patch_constants.h` | 正式局部配准/细线常量；已删除无引用的历史常量，不是另一份用户配置 |
| `src/patch_detector.cpp` | 原 `DetectCore`、物理单位换算、最终 Warp、普通连通域判定与测量 |
| `src/patch_alignment.cpp` | 当前唯一编入 DLL 的局部配准实现 |
| `src/patch_region.h/.cpp` | 配准窗口与边界内容整理；只读输入，不求矩阵、不改变检测核心 |
| `src/patch_periodicity.cpp` | 模板重复位移投票、全前景覆盖比较；只拒绝歧义，不自动换周期 |
| `src/patch_template_cache.cpp` | `TemplateCache::get`：签名分桶、逐像素核对、SIFT 特征复用和锁顺序 |
| `src/patch_fine_line.cpp` | 原 `DetectFineLineBreaksAtDetailScale`、边界带、骨架/端点/抗错位证据 |
| `../Services/PatchNativeInterop.cs` | P/Invoke 布局、SafeHandle、只读输入与输出图复制 |
| `../Services/PatchDefectDetector.cs` | 保留原 `Detect` 调用接口，转换结果，不重复检测 |
| `../Services/PatchDefectDetector.Visualization.cs` | 只负责原有结果图/屏蔽图保存；不再包含算法 |

原 `PatchDefectDetector.Alignment.cs`、`.FineLine.cs` 不再存在于正式 Services；冻结快照只在开发比较工具中。注释按流程迁入对应 C++ 文件，并补充坐标/尺度、截面与锚段含义、面积口径、RAII/缓存锁等说明。C++ helper 上保留原 C# 方法名，便于查找迁移位置。

### 当前统一实现（2026-10-27 核对）

- `PatchCropper` 在理论零件核心 ROI 外取默认 15 mm 的真实像素作为配准搜索上下文；缺陷坐标、三类判定和保存图只使用核心。采样预算与 DPI、目标尺寸有关，独立于 `DefectDetectScale`。裁框一致性有完整目标和真实搜索余量前提，不是任意外围内容下的保证。
- `registration_core` 从 TIFF Alpha 完整连通结构中共同定位至少 95% 前景的中心，保留排版给定的逻辑窗口尺寸；同面积结构共同纳入，中心取整使用对整数平移等变的 floor。要求新窗口在真实输入内且不丢失原核心任何前景，失败则记录 `domain=layout/no-context|empty|incomplete-support|insufficient-context|retain-all-content` 并保留原窗口；成功为 `domain=content-anchored`。不需要独立 PNG，不改检测核心或缺陷模板。孔洞和孔洞内独立散点按真实 8 邻域像素统计，不以矩形面积代替。完整结构仅在自身包围框内 floodFill，边界结构复用访问掩膜，避免对每个散点反复分配全图缓冲。
- `registration_content` 先检查核心边界是否接触前景。没有接触时保持原像素路径；有真实上下文且边界截断片段合计不超过核心前景 5% 时，在独立配准二值副本上通过 8 邻域填充去掉这些片段，再确定内容框。完整框内的所有散点保留，不取最大连通域；主体大面积穿过边界时沿用原图，避免把被裁坏的目标悄悄缩小。原 Alpha 和缺陷模板从不修改，C ABI 不变。诊断 `boundaryFragments=N/Mpx` 表示忽略的连通片段数和它们进入核心的前景像素数，不是缺陷统计。
- TIFF/CIS 统一二值化、Area 缩小和轻度模糊；SIFT 特征图统一使用 32 工作像素的滤波边界，关键点中心仍限真实区域，工作密度不按特征数临界值切换。特征为最多 100 个空间均衡 DoG 点，仅对不同位置不足 8 个的分区补充转角 SIFT 描述子；有真实转角才补，不保证每区一定有 8 个点。转角点使用固定轴向、12 工作像素尺度，不生成独立轮廓矩阵。全部证据执行原局部约束、双向 ratio 与互相一致匹配。空间窗内只有一个候选时不用虚假的 ratio，要求其描述子距离 <120。同一组对应点分别估计相似和受约束仿射，RANSAC 固定随机种子。新增采样会改变匹配证据，须单独回归精度、缺陷保留及耗时，不能声明检测效果完全不变。
- 相似、受约束仿射统一检查内点、几何、跨区留出稳定性、九区双向轮廓及周期歧义。九区平均值使用固定的 TIFF 有图案分区作为分母，CIS 独有结构另加罚分，防止候选矩阵改变评分分区集合。通过安全检查且整体均值优于全局的候选可以应用；改善不足 10% 或最差区超过 8 原像素时标记 `AppliedDegraded` 待复核，而非直接回退。效果接近选相似，仿射整体至少再改善 10% 才选。两者均不可用且全局并非已足够好时，才以同组点补估局部单应性；其二维内点分布、九点雅可比、留区稳定性和周期检查保持硬门控，15% 改善及最差区上限只决定是否标记降级。轮廓只检查质量，不生成或精修矩阵；最终原尺寸核心 ROI 仅采样一次。
- 留区稳定性在所有 RANSAC 内点的实际 CIS 图案位置检查重拟合位移（包括被留出的点），不再把核心裁框的空白四角当作特征证据；整个核心的物理变换范围和越界仍由几何门控独立检查。相似变换的稳定性上限为 6.25 原像素（包含 0.25px 特征定位余量），仿射及单应性仍为 6 原像素；失败日志分别记录各模型的稳定性数值。
- 轻微剪切范围：仿射与单应局部雅可比共用 `LocalAlignmentMaximumShearDegrees=2°`（2026-10-27）。仿射仍限制主伸缩在 0.97–1.03、非等比比值 ≤1.03、旋转 ≤3°，不是允许任意 2° 剪切与其他形变叠加。匹配共识、留区稳定性、真实搜索范围和错周期检查保留。此调整允许 img/2 的 2b…使用整体更好的降级仿射；不把弯曲形变变成已解决能力，也不强制应用不稳定候选。
- `leave_out_change` 先合并源坐标、目标坐标均完全相同的内点，仅在稳定性复核中按几何位置分组/留出/重拟合/设置探针。相同角点的多个 SIFT 方向不会把“两处结构”变成“三点整区留出”，也不会重复加权最小二乘。相似/仿射/单应至少需 4/5/9 个不同坐标对，才能留出一点并保持原重拟合最小点数。没有容差聚类，不把相近但不同的真实角点合并；原匹配、Lowe 比值、RANSAC 矩阵、8/12 候选点及 6/10 内点门槛不变。`stabilityPoints` 为不同坐标对数；拒绝诊断按相似/仿射/单应顺序显示三项，0 表示该候选未走到稳定性检查。
- 如果相似与仿射的九区均值均未优于全局对准，而全局已有至少六个模板有结构分区、平均 ≤2 原像素、最差区 ≤4 原像素，则保留全局图并记录 `NotNeeded/StrongGlobalAlignment`，不额外重采样；周期检查照常执行。只要有候选通过安全检查且整体均值确有改善，即使改善不足 10%，也进入 `AppliedDegraded`，不走这一 `NotNeeded` 分支。
- 无可信局部矩阵时使用全局对准裁图正常检测并返回结构化 `Review/Error` 警示；已采用但质量降级的局部矩阵返回 `AppliedDegraded` 警示（原因码 `EdgeQualityDegraded=9`）。两者的三类缺陷均照常计数、显示和写尺寸日志，建议人工核对对准质量。`cis_patch_result_alignment` 与 `cis_patch_abi_version()==2` 读取状态；旧 DLL 不可与新 C# 混用，新增状态/原因码只追加枚举值，不修改 ABI 结构布局。
- 这是算法更改，不能引用旧版迁移等价性或下方历史性能数据作为新版本验收。当前 [独立验收口径](../Regression/PatchNative/UnifiedRegistration/AcceptanceRevision_20261024.md) 与后续实图库测试应分别评估正确接受、错误接受、拒绝率、缺陷保留和耗时。

旧配准分支和专用常量已清理；历史实验记录见 [维护记录](../docs/维护记录.md)。改算法请修改 `Native/src`，不要修改 SDK 发布副本或生成工程。

### 零件 C ABI 与内存契约

- Windows x64、`__cdecl`、显式 pack=8、`cis_patch_abi_version()==2`；二维码 ABI v2 不变。配置增加核心 ROI，结果增加 `cis_patch_result_alignment`；输入/配置/汇总/配准/缓存统计的 `struct_size` 必须正确。
- `cache_create → worker_create → detect → result读取 → result_destroy → worker_destroy → cache_destroy`。每批一个缓存，每并行 worker 一个上下文；SIFT 按需创建并复用，空间匹配直接比较受约束候选，不再保留 BFMatcher。worker 通过 `shared_ptr` 持有缓存。
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
