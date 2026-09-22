# 22.jpg：失焦与局部照度变化的定位框外边界恢复

验证日期：2026-10-09。范围：共享 `qr::QrDetector::detect`、正式 `CISVisionCore.dll`、独立 `QrReader.dll` 及 SDK 示例。

## 样本与失败原因

- 原始输入：`outputs/QrReader_SDK/22.jpg`，368×361，BGR8，完整输入默认黑码优先入口。
- 原图 SHA-256：`9812EFC90076DD9B039D748FF7DCF35FD8ADB70663152EE833FB592A2AB835EE`。
- 供开发回归保留的同字节副本：`samples/tight_blurred_22.jpg`。没有修改用户原图。
- 用户命令修改前返回 `found=0`，总尝试4次，单次检测观察275.093 ms。模型能够加载，不是文件路径或依赖缺失问题。
- 原始Otsu未找到可靠嵌套轮廓，失焦模板找到3个定位框：中心约(283,89)、(281,286)、(82,286)，模块宽14/12/12 px，相关分数0.794/0.718/0.712。
- 原有局部Otsu边界在亮度变化处切斜，导致局部几何模型采样不准确。静区补白、直接缩放、简单灰度增强均未解码。诊断实验中Otsu边界的模型拟合RMS约4.14 px，自适应测量约1.95 px。

## 最小有效改动

`Native/src/qr_blur_local.cpp` 保持已有局部解码/Otsu模型优先；均失败后才用自适应高斯阈值重新测量三个定位框外边界。

1. 窗口宽取约4个实测模块，转为奇数，随分辨率调整，不使用22.jpg专属坐标或固定缩放比例。
2. 仍用12个实测边界角点建立原有局部逆映射。
3. 保留拟合误差、采样范围、Jacobian和模块结构检查。
4. 待解码数据始终来自实拍灰度；二值化仅用于几何测量，不替换码字。
5. 两种测量共享原来最多4次局部解码预算，不增加API字段/业务文本规则。

`QrDetector::detect → blurred → blurred_local` 自动调用此分支。新成功策略追加 `outline=adaptive-gaussian`；原有成功分支的策略保持原样。

## 已确认结果

| 验证项 | 实际结果 |
|---|---|
| C++静态核心示例 | 解出 `ZJ_202607310321032_RT`，5次总解码调用 |
| 独立DLL示例 | 同样文本与几何，5次总解码调用 |
| 中心与尺寸 | (183.841,181.599) px，约301.855×294.026 px，几何为平均对边长度 |
| 单次识别观察 | C++262.564 ms、DLL264.310 ms，预热后检测时间，不含文件读取与模型初始化 |
| 成功策略 | `blurred-finder-local-warp`，red，21模块，结构分数0.818，`outline=adaptive-gaussian` |
| 新样本回归 | 16项：首次原图、BGR/BGRA/非连续ROI/反色配置各重复3次，三种负例；结果稳定且输入未被修改 |
| 历史test1/test2 | SDK中原图哈希与历史记录一致，15项原图检查通过 |
| 通用C ABI | 原49项检查通过 |
| 原CIS C ABI | 原103项契约检查通过 |
| 真实CIS图库 | 54张原图与跨帧组合共95次调用，16次命中、0异常；与`blur-after.json`的状态/文本/坐标/尺寸/策略/尝试次数逐项零差异 |
| CIS累计Detect观察 | 历史单轮14.416 s，本轮14.362 s；不是受控多轮性能基准，不宣称提速 |
| 构建与正式部署 | WPF Debug/Release x64，0警告、0错误；两者使用的新Release DLL哈希一致 |

命中次数包含无码帧和跨帧调用，不等于识别率。文本为后端实际解码输出；算法没有接收预期文本参数。

## 复现与防回退

在SDK目录执行用户原命令：

```powershell
.\QrReaderExample.exe .\models .\22.jpg .\sample_result.png
.\QrReaderCExample.exe .\models .\22.jpg
```

项目开发者按需构建和运行回归，不加入生产程序自动流程：

```powershell
cmake -S Native -B obj/QrReaderBuild -A x64 "-DOpenCV_DIR=<OpenCV 4.10.0 构建目录>" -DQR_READER_ONLY=ON -DQR_BUILD_EXAMPLES=ON -DQR_BUILD_TESTS=ON
cmake --build obj/QrReaderBuild --config Release --parallel 2
.\obj\QrReaderBuild\Release\QrReaderOutlineChecks.exe .\Assets\WeChatQRCode .\Regression\QrReusable\samples\tight_blurred_22.jpg
```

测试文件中的期望文本只用于断言，不传入算法。同步交付请运行`Tools/Build-QrReader.ps1`，同时更新独立DLL和静态链接的示例EXE；只替换DLL不足以更新`QrReaderExample.exe`。

## 尚未解决的边界

对同一原图的诊断变体：BGRA、配置反色、180°旋转、0.85倍缩小和轻度亮度降低均成功；灰度转换、90°/270°旋转及1.15倍放大仍有失败。失焦样本的轮廓离散化与颜色通道信息变化仍会影响结果，不宣称任意变体可读。

本次保留强几何约束和解码校验，解决的是有足够三个定位框证据、且Otsu外边界受局部光照偏差影响的情况。强过曝丢失数据、严重失焦或大面积破损仍需改善采集或进一步验证。
