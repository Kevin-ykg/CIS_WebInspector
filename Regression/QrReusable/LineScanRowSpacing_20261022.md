# 2026-10-22 单帧二维码纵向行距失真回归

输入来自 `frame_20261022_155707_843_000631.jpg` 左下角。可移植的局部样本是 [line_scan_row_spacing_20261022.png](samples/line_scan_row_spacing_20261022.png)；正确文本由用户核对为 `ZJ_202610220030003_RT`，该文本仅用于回归断言，不参与识别算法。

## 失效定位

- 原始项目配置 `BaseRoiX=125`、`BaseRoiWidth=1500`，二维码完整落在搜索范围内。修改前，正式 C#→C++ 单帧入口 30 次解码仍未识别。
- 三个定位框和四角可以找到，失败发生在数据模块采样/解码阶段。对透视展开图测得上定位框纵向约 157～159 工作像素、下定位框约 189～192 工作像素；单一 Homography 无法同时把两处都校正成七模块的相同高度。
- 无法仅凭图片确定失真由线扫速度、膜片移动还是印刷过程造成；可确认的是输入码格纵向采样间距不均匀。

## 恢复边界

普通解码及既有恢复全部失败后，先用三定位框限定局部搜索区域，再取独立的四角定位结果。透视展开后的三个定位框须各自接近预期角点，并且实测上下行距确有差异；随后用四条定位框外边界建立单调的分段 Y 映射。边界测量只容许有限像素扰动，校正图最多再定位一次。只有 WeChatQRCode 实际解码成功才返回文本；没有按 `ZJ_` 格式、时间戳或已知答案猜测内容。

## 已验证

- 便携局部样本：QrReader DLL `invert=1` 返回上述正确文本。
- 完整单帧：正式 `QrRegression` 使用当时 Debug 配置回放，返回上述文本，中心约 `(963,2086)`、原图宽高约 `711×696 px`；修改前该单帧未识别。
- 同一文件夹六张单帧回放：正常首码仍在快速路径命中；目标单帧新增命中；无码帧未误检。最终收敛版本该困难码约 `2.8 s`，是单次观察值，不是批次节拍。
- 最终版本在同一进程重复回放 3 轮，共 30 次单帧/跨帧调用。目标单帧三轮均输出相同文本、中心和几何尺寸；总计 9 次命中、0 错误。该困难码三轮耗时约 `2.6～3.4 s`。
- 历史图库 `E:\南京爱速智\CIS\moreMarks\二维码无法识别`：95 次单帧/跨帧调用中 16 次命中、0 错误。与旧保存报告的前 94 个同名调用对照，Found 和文本未变化；旧报告配置哈希不同，不能将其作为严格耗时 A/B。
- 原生 Release 与 WPF Debug/Release x64 构建通过；此样本及普通/模糊样本分别验证。尚未验证连续产线采集与全部失真强度。

## 复现

```powershell
# 构建正式 DLL 后，构建并更新独立 SDK 包。
powershell -ExecutionPolicy Bypass -File Tools/Build-QrReader.ps1
& 'outputs/QrReader_SDK/QrReaderCExample.exe' 'outputs/QrReader_SDK/models' 'Regression/QrReusable/samples/line_scan_row_spacing_20261022.png' 1

# 完整单帧在原始文件夹仍可用时，通过 C# 正式适配层回放。
& 'Tools/QrRegression/bin/x64/Release/net48/QrRegression.exe' capture 'bin/x64/Debug/net48/app_config.json' 'Assets/WeChatQRCode' 'C:\Users\EDY\Desktop\2' 'obj/QrCase20261022/after-all.json' native
```
