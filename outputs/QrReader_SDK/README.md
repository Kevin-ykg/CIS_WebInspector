# 通用二维码识别器：同事接入指南

## 1. 能做什么

传入一张 Gray8 / BGR24 / BGRA32 图像，返回第一个成功解出的非空二维码文本、中心、尺寸和诊断策略。不依赖 CIS 相机、AppConfig、WPF、拼接状态或某个固定横向位置。

通用默认值为**整张输入图、黑色前景优先、纵向尺度 1.0**；白色前景可配置 `invert_polarity=true`。后续仍保留定位框证据驱动的反极性、动态尺度、局部静区、透视、低对比及模糊恢复。不是不加条件地把所有候选跑一遍。

```text
同事的图像 ────────────────────→ qr::QrDetector::detect(image)
CIS 图像 → CisQrAdapter（ROI/保护带） ─→ 同一个识别核心
                                       ↓
                           WeChatQRCode 解码成功才返回文本
```

`QrReader.dll` 是独立交付库，不包含 CIS 对准、白墨或缺陷模块。正式 CIS 工程仍使用原来的 `CISVisionCore.dll / cis_qr_* v2`，无需换配置或 C# 调用方式。

## 2. 先直接运行示例

交付包中包含 `QrReader.dll`、导入库 `QrReader.lib`、C 头文件、C++ 静态核心/源码、两个例程、模型、许可、样本及检测预览。Windows x64，需要与构建工具集兼容的 VC++ x64 运行库；OpenCV 已静态链接，无须额外复制 `opencv_world.dll`。

在交付目录中执行：

```powershell
# C++ 源码/静态核心调用示例，输出额外说明栏和绿色中心点
.\QrReaderExample.exe .\models .\sample.jpg .\sample_result.png
# 稳定 C ABI / DLL 调用示例；只把原始像素传给 DLL
.\QrReaderCExample.exe .\models .\sample.jpg
# 白色前景优先时，C ABI 示例最后加 1
.\QrReaderCExample.exe .\models .\your_white_qr.jpg 1
```

2026-09-22 本机验证：用户提供的 `20260921-175708.jpg`，整图、默认配置成功解码为 `ZJ_202606302651265_RT`。两个例程文本和几何一致，走 `finder-perspective` 路径，解码尝试 2 次。未手工指定码区，未向算法输入已知文本。

该样本检测中心 `(330.00, 338.75) px`，几何尺寸约 `213.03 × 205.50 px`（本路径为平均对边长度）。预热后的本机单次检测约 100 ms，只是样本观察值，不含文件解码/模型加载，不是所有输入的速度保证。

### 新增模糊/局部变形样本（2026-09-22）

两张图片均完整输入，无手工 ROI、无预期文本参数。更新后的例程可直接执行：

```powershell
.\QrReaderExample.exe .\models 'C:\Users\EDY\Desktop\test1.png' .\test1_result.png
.\QrReaderExample.exe .\models 'C:\Users\EDY\Desktop\test2.png' .\test2_result.png
```

| 图像 | 实际解码文本 | 恢复路径 |
|---|---|---|
| test1.png | `ZJ_202607310321032_RT` | 三定位框确定局部码区，红色通道重采样，3 次总解码调用 |
| test2.png | `ZJ_202607318430843_RT` | 三定位框的 12 个外边界角点拟合平滑局部逆映射，4 次总解码调用 |

局部恢复只在原识别路径全部失败后启动：几何三点组 → 局部原图解码 → 必要时测量定位框外边界 → 拟合误差与 Jacobian 检查 → 模块结构检查 → WeChat 解码。新增分支最多 4 次解码；所有数据模块来自实拍像素，不补造码内容、不使用已知文本。三个定位框不能可靠测量或几何检查失败时返回未识别。不是 CIS 的 ROI 规则，也不更改全局/零件对准算法。

## 3. C++ 接入（同工具链源码 / 静态链接）

完整例程：`examples/qr_reader.cpp`。

```cpp
#include "qr/qr_detector.h"
qr::QrDetector detector("models");
qr::Options options;                  // 常规黑码默认不反色
options.invert_polarity = false;
detector.configure(options);          // 配置复制，不借用 options
detector.initialize();               // 循环外预热，可选但推荐
qr::Result result = detector.detect(image); // cv::Mat，Gray/BGR/BGRA 8 bit
if (result.found) {
    // result.text、x/y、width/height、strategy
}
```

模型对象应长期复用，不要每帧重新创建。一个 C++ 实例由调用者串行使用；多个并行 worker 各建自己的实例。不要通过 DLL 导出本 C++ 类或跨 DLL 传 `cv::Mat`、STL 对象。

如果调用者先裁 ROI：返回坐标相对该 ROI；**先加回 ROI 左上角，再取整**。图像仅同步只读借用，不保留指针、不修改输入。BGR/BGRA 不是 RGB/RGBA，Alpha 不参与识别；透明图片需调用者按需要先合成背景。

`DetectHints.evidence_region` 是高级可选项，默认整图。它改变定位框/背景统计区域，但不裁掉解码输入，不会自动扩区，也不保证结果一定出自该区域。取证区变窄会改变 Otsu 统计，可能降低识别效果；普通使用不需要设置它。必须限制搜索范围时，应由调用者裁图。

## 4. DLL / C# 接入（推荐对外使用）

接口定义：`include/qr_reader_api.h`；完整调用和出错处理：`examples/qr_reader_c_api.cpp`。此例用 OpenCV 读取 JPEG，但 DLL 接口本身只收像素，调用端也可以用自有相机 SDK 或其他图像库。

```text
qr_reader_abi_version() == 1
  → create(UTF-16 模型目录)
  → configure(可选：极性、纵向候选尺度)
  → initialize(预热)
  → detect(image, hints=null, result, text, strategy, error) × N
  → destroy
```

| 接入项 | 契约 |
|---|---|
| 平台与约定 | Windows x64，`__cdecl`，pack=8；Config/Image/Hints/Result 大小分别为 24/40/24/56 字节 |
| 输入 | 8 bit、1/3/4 通道，正 stride；支持非连续 ROI，bytes 至少为 `(height-1)*stride + width*channels` |
| 结果 | 输入图坐标系的浮点中心；`size_kind=0` 为 X/Y 投影宽高，`1` 为透视恢复平均对边长度；不是精密计量接口 |
| 字符串 | 文本、策略、错误为 UTF-8；返回 byte 数含末尾 NUL，应按字节数解码，不能用系统 ANSI 编码 |
| 状态码 | 0 命中；1 未识别（正常情况）；-1 参数错误；-2 运行异常；-3 输出缓冲不足 |
| 不足处理 | -3 返回所需 text/strategy 字节数，扩大后重试；重试会重新识别，不返回截断文本 |
| 资源 | 调用方拥有像素与输出缓冲；DLL 拥有模型；同句柄内部串行，但不能并发 destroy、伪造/复用失效句柄 |

C# 使用 `DllImport("QrReader.dll", CallingConvention = CallingConvention.Cdecl)`，结构体使用 `[StructLayout(LayoutKind.Sequential, Pack = 8)]`；`uint32_t/int32_t/uint64_t` 分别对应 `uint/int/ulong`，指针对应 `IntPtr`，不要把字段 `found` 映射为默认 C# `bool`。模型目录按 `LPWStr` 传入。

托管像素数组调用期间用 `fixed` 或 `GCHandle.Alloc(..., Pinned)` 固定；已有原生 Mat 可传数据地址，但保持 Mat 生命周期。句柄用 `SafeHandle` 包装，释放调用 `qr_reader_destroy`；按该 SafeHandle 参数声明 P/Invoke，避免 Dispose 与正在执行的调用竞争。首次核对 ABI/结构大小，不要拿现有 CIS v2 的 Config/Result 套用到通用 v1。

模型由四个文件组成：`detect.prototxt`、`detect.caffemodel`、`sr.prototxt`、`sr.caffemodel`。C ABI 接受 UTF-16 路径，但当前 OpenCV 文件接口要求能以 Windows 系统代码页无损表示；无法表示时明确报错。跨机器部署建议使用英文模型目录。

## 5. 构建与维护

项目维护者：运行 `Tools/Build-QrReader.ps1 -SampleImage <图片路径>`，复用 `obj/VisionNative/opencv-build`，生成 `outputs/QrReader_SDK`。缓存不存在时先运行 `Tools/Build-VisionNative.ps1`。

源码包接收方：安装 Visual Studio C++ 工作负载、CMake；准备 **OpenCV/opencv_contrib 4.10.0**，保持 IPP，包含 core/imgproc/objdetect/dnn/wechat_qrcode；例程还需 imgcodecs。用 x64、同一运行库配置构建：

```powershell
cmake -S . -B build -A x64 -DQR_READER_ONLY=ON -DQR_BUILD_EXAMPLES=ON -DQR_BUILD_TESTS=OFF "-DOpenCV_DIR=C:/opencv-build"
cmake --build build --config Release --parallel 2
```

| 修改目标 | 源码入口 |
|---|---|
| 通用公开类型 / 结果含义 | `include/qr/qr_detector.h` |
| 识别流程 / 模型复用 / WeChat 调用 | `src/qr_detector.cpp` |
| 定位框几何 / 动态尺度 / 坐标还原 | `src/qr_geometry.cpp` |
| 透视 / 低对比 / 黑码 / 严重模糊恢复 | `src/qr_recovery.cpp` |
| 模糊码局部取景 / 三定位框边界局部形变恢复 | `src/qr_blur_local.cpp` |
| 稳定 DLL 接口 / 验证 / 锁 / 异常边界 | `include/qr_reader_api.h`、`src/qr_reader_api.cpp` |

生产 CIS 的 ROI、保护带和最终整数坐标适配留在主工程 `src/cis_qr_adapter.*`，不放进同事的通用交付包。修改共享核心需要同时回归通用样本和 CIS 的单帧/跨帧图库。

## 6. 已验证与边界

- 通用接口通过 49 项检查：重复调用、Gray/BGR/BGRA、反色配置、非连续 ROI、二维取证区、坐标、输入只读、非法尺寸/步长、输出容量等。
- 新增两张原图的 15 项检查通过：重复识别、BGR/BGRA/非连续 ROI、输入不修改、几何范围，以及网格背景/纯色图不误报；C++ 和独立 C ABI 均读出正确文本。困难图片并非任意旋转/缩放都可读，额外实验中 test1 旋转 180° 仍未解出；不将两张原图成功扩大为全场景保证。
- CIS 54 张原图及跨帧组合，共 95 次调用，重构前后识别状态/文本/坐标/尺寸/策略/尝试次数逐项一致；旧 C ABI 103 项契约检查通过。命中次数不是识别率，图库包含大量无码帧。
- 当前是**单码、非空文本**识别入口：不承诺多码全量输出，不排序多个码，不按 `ZJ_` 等业务前缀过滤；纯空白文本不算命中，任意二进制载荷不在此版支持目标中。
- 通用不等于所有 QR 都可读。原有恢复阶段有定位几何、模块规模和预算约束，复杂新图、没有定位框的严重破损、多码场景仍需单独验证。
- 同事更换编译器、OpenCV/IPP、模型或输入缩放方式后，需重新验证；现有测试不代表跨平台、CameraLink 在线或全天稳定性验收。
- 请随包保留 `licenses` 和 `models/LICENSE.txt`；不要只拷 DLL 丢弃模型/第三方许可。
- 示例的图像读写包含第三方 JPEG/PNG 等编解码器，许可已随包提供。This software is based in part on the work of the Independent JPEG Group.
