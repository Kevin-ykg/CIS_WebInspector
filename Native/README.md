# C++ 二维码算法库 CISVisionCore

## 边界与调用关系

```text
ImageStitcher（单帧 / 跨帧组合，仍为 C#）
  → QrCodeDetector.Detect（C#：参数快照 / 固定像素数组 / SafeHandle）
    → cis_qr_detect（稳定 C ABI，单次同步调用）
      → cis::QrDetector::detect（完整 C++ 识别流程）
        → cv::wechat_qrcode::WeChatQRCode::detectAndDecode
    ← 文本、输入图中的中心 / X、Y 投影宽高、尝试次数、策略、错误
  ← QrDetectionResult（原数据模型，拼接和 Mark 尺度计算无须改动）
```

**不是只把 detectAndDecode 包一层 DLL**：灰度转换、ROI 与保护带、极性处理、定位框证据、自适应尺度、局部静区、透视展开、低对比度和模糊恢复均在 C++ 内。候选顺序、阈值、坐标还原和取整规则沿用迁移前实现。拼接状态机、对准、白墨与缺陷算法仍在 C#，不在本轮修改范围。

| 文件 | 职责 |
|---|---|
| `include/cis_qr_api.h` | 对外唯一 C 头文件，固定宽度字段、状态码、调用约定 |
| `src/qr_api.cpp` | 参数/长度检查、上下文锁、输出缓冲区、异常屏障 |
| `src/qr_detector.h` | 内部模型实例、候选数据结构、坐标计算约定 |
| `src/qr_detector.cpp` | 主流程、模型加载与复用、WeChatQRCode 调用、成功结果恢复 |
| `src/qr_geometry.cpp` | 嵌套定位框、几何证据、自适应尺度、局部/透视候选 |
| `src/qr_recovery.cpp` | 透视、低对比度、黑码原极性、模糊模板与模块结构校验 |
| `../Services/QrCodeDetector.cs` | C# 唯一入口；没有第二套算法或静默回退 |

## 内存与接口契约

- Windows x64，C ABI **版本 2**，`__cdecl`，默认 8 字节对齐；Config 为 32 字节，Result 为 48 字节。先查 `cis_qr_abi_version`，不兼容时明确报错。
- `create → configure → initialize → detect（多次）→ destroy`。模型按实例持有；初始化/预热可以重复调用。改变配置时复制尺度数组，外部数组不长期借用。
- 输入为 Gray8 / BGR24 / BGRA32，正 stride 可包含行填充；缓冲区同步只读借用。C# 在检测期间固定 byte[]，返回后立即解除。DLL 不保留像素指针，不跨 DLL 传递 `cv::Mat`。
- C++ 的 `cv::Mat` 和容器作用域结束自动释放，模型由 `unique_ptr` 持有；C# 通过 `SafeHandle` 释放原生上下文，调用/配置/Dispose 串行化。直接 C 调用者不得与正在执行的调用并发 destroy，也不得使用已经销毁的句柄。
- 坐标相对传入图像；跨帧组合仍由 ImageStitcher 加回全局坐标。宽高是 X/Y 方向投影，不是旋转边长。保留 .NET 的 midpoint-to-even 取整以及原先 float/double 运算边界。
- 输出文本为 UTF-8，模型目录参数为 Windows UTF-16；OpenCV 模型文件打开仍要求目录可由 Windows 系统代码页表示，不支持的路径明确失败。部署到同一中文 Windows 代码页或纯英文目录最稳妥。
- 调用方分配结果及字符缓冲区，不导出 STL、不要求跨运行库释放内存。缓冲区不足返回 `-3` 和需要的字节数（含 NUL），不会把截断文本作为成功结果。重新调用需要重新检测，当前没有结果缓存句柄。
- 状态：`0` 成功，`1` 正常未检出，`-1` 参数错误，`-2` 模型/算法异常，`-3` 输出空间不足。C++ 异常不越过 C 边界；原生非法指针属于调用方违反契约，不能靠异常屏障保证安全。
- 本轮不设置 OpenCV 全局线程数，不改变正式帧队列；同一个 QR 实例只做一帧检测。独立 DLL 和 OpenCvSharp 不共享 Mat、分配器或全局设置。

## 构建与部署

前置：Visual Studio「使用 C++ 的桌面开发」、Windows SDK、CMake、.NET Framework 4.8 开发环境。当前机器使用 MSVC 14.51（VS 18 Community），目标 x64。

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -Configuration Release
dotnet build CIS_WebInspector.sln -c Debug -p:Platform=x64
dotnet build CIS_WebInspector.sln -c Release -p:Platform=x64
```

首次下载并编译 OpenCV / opencv_contrib **4.10.0**，脚本校验下载包 SHA-256；保留 IPP，关闭不需要的测试、Python、视频与 OpenCL。缓存位于 `obj/VisionNative`，可删除但删除后要重新执行脚本。首次构建可能需较长时间和较多磁盘空间；增量构建复用已编译依赖。源代码在 `Native`，不能把 obj 中生成的 vcxproj 当作维护入口。

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

迁移目的首先是封装与生命周期清晰，**不承诺改语言即可提速或提高识别率**。原 OpenCvSharp 的主要算子本来就在 C++ 执行；收益/代价需分别观察识别耗时、跨边界调用、私有内存和部署体积。
