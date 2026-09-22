# 二维码通用化验证（2026-09-22）

## 变更范围

把安装 ROI、横向保护带、CIS 预热尺寸和最终帧坐标取整从 `QrDetector` 移到 `CisQrAdapter`。`qr::QrDetector::detect` 默认整图输入；旧 `cis_qr_*` v2 不变，新增独立 `QrReader.dll / qr_reader_*` v1。两者链接同一套静态核心，未改变识别候选顺序、门槛或恢复算法，也没有根据测试图片硬编码内容。

## 已确认结果

| 项目 | 结果 |
|---|---|
| 指定样本 | `C:\Users\EDY\Desktop\20260921-175708.jpg`，原图完整输入，默认黑色前景 / Y 尺度 1.0 |
| 识别文本 | `ZJ_202606302651265_RT`；C++ 静态核心和独立 DLL 的结果一致 |
| 几何 | 中心 `(330.00, 338.75)`；宽高 `213.033 × 205.500 px`，此透视分支为平均对边长度 |
| 策略 | `finder-perspective`；三个定位框、21 模块、`rectified=gray`、`polarity=original`，2 次解码调用 |
| 单样本观察 | 交付包预热后 C++ 例程 106.836 ms、C ABI 例程 124.361 ms；不包含模型初始化和文件解码 |
| CIS 原图库 | `E:\南京爱速智\CIS\moreMarks\二维码无法识别`，54 张输入，单帧与跨帧组合共 95 次调用 |
| 等价性 | 16 次命中、0 个运行错误；重构前后 Found、文本、整数中心、宽高（容差 1e-6）、策略、尝试次数和错误逐项零差异 |
| 通用接口 | Release / Debug 均通过 49 项检查：非连续 ROI、二维取证区、父图坐标恢复、灰/彩色、反色、配置复制、输入只读和非法参数等 |
| 原 CIS 接口 | Release 103 项契约检查通过，包含缓冲、模型缺失、布局、步长/溢出、重复生命周期等 |
| 构建 | 原生 Debug/Release、QR-only Release、WPF x64 Debug/Release 通过；最终 WPF 构建均 0 警告 / 0 错误 |
| 部署一致性 | Debug/Release 正式 WPF 输出目录中的 DLL 均与最终 Release 算法库 SHA-256 一致 |

95 次调用包含无码帧及跨帧组合，**16/95 不是二维码识别率**。本轮关注复用边界和识别行为保持，不宣称新提高了识别率。

### 性能口径

最终一次回放重构前累计 Detect 14.392 s、重构后 15.655 s；开发中另一次重放为 17.711 s（与 Debug 链接并发）。这是不同时间、未隔离整机后台负载的单轮记录，不能据此确认提速或稳定回退。没有降低恢复预算、修改线程数或改变生产队列来换取耗时。需要评估生产性能时应在固定负载下交替多轮比较；本轮不承诺提速。

### 坐标与提示区专项

- 通用结果保留浮点；CIS 适配层先加输入窗口偏移，再 midpoint-to-even 取整，并按原路径的规则夹零。测试 `.5 + 1` 和负坐标加正偏移，防止“先取整再平移”的一像素差异。
- `evidence_region` 只约束 Otsu/定位取证，不是最终搜索区域限制。开发时在该模糊样本上把提示区收紧至 `(160,170,340,340)` 会未命中，而默认整图成功；因此不把紧框设为通用默认值。二维偏移测试使用保留背景统计的窄边内缩区域。
- 普通 WeChat 框的 X/Y 投影宽高和透视恢复的平均对边长度原本不同，本轮保留 CIS 数值；新通用接口明确返回 `size_kind`，避免同事把它当作统一精密测量宽高。

## 复现

```powershell
# 仅编译开发验证，不加入正式程序的自动启动/运行流程
cmake -S Native -B obj/QrChecks -A x64 "-DOpenCV_DIR=<本机 OpenCV 4.10.0 构建目录>" -DQR_READER_ONLY=ON -DQR_BUILD_TESTS=ON
cmake --build obj/QrChecks --config Release --parallel 2
.\obj\QrChecks\Release\QrReaderChecks.exe .\Assets\WeChatQRCode 'C:\Users\EDY\Desktop\20260921-175708.jpg'

# CIS 结果回归；本目录的 before.json/final.json 为本轮保存记录
dotnet build Tools/QrRegression/QrRegression.csproj -c Release -p:Platform=x64
.\Tools\QrRegression\bin\x64\Release\net48\QrRegression.exe contracts .\Assets\WeChatQRCode
.\Tools\QrRegression\bin\x64\Release\net48\QrRegression.exe compare .\Regression\QrReusable\before.json .\Regression\QrReusable\final.json

# 生成同事使用包，附带指定图片及结果预览
powershell -ExecutionPolicy Bypass -File Tools/Build-QrReader.ps1 -SampleImage 'C:\Users\EDY\Desktop\20260921-175708.jpg'
```

`before.json` 是修改前实际 C++ 版本的输出（不是旧 C# 冻结快照）；`final.json` 是最终 Release DLL 的输出。两份记录包含配置、模型和输入哈希。JSON 中 `OpenCvBuild` 来自回放读图端 OpenCvSharp；原生算法构建为本项目 OpenCV 4.10.0 静态库 + IPP，不能把该字段误当作 DLL 的构建信息。

样本 SHA-256：`57A5D83AA95194C6263265529D935F5B7A2290BCC126B7EF57C2F92A1F0582B6`。

## 尚未覆盖

没有验证全部 QR 版本/密度、多码全量读取、纯空白/二进制载荷、跨平台或在线 CameraLink 全天运行。通用默认入口和现有 CIS 适配入口不能混用配置结构体。图像裁剪、透明背景合成及输入生命周期属于调用方职责；异常屏障不能补救伪造指针或并发销毁句柄。
