# 局部配准统一化：人工真值核对包

此目录保留独立于 `obj` 的八件原尺寸输入、用户确认的人工点与可复用离线核对入口。当前 C++ 正式实现已经接入；实际效果和未完成验收项见 [阶段进展](Progress_20261024.md)。早期流程复盘与首版标注保留为历史依据，不能替代确认版真值。

[历史流程复盘](../UnifiedRegistrationBaseline_20261023.md)、[首版标注核验](AnnotationAudit_20261023.md)及 [首版标注 JSON](annotations-20261023.json) 仅供追溯；正式复核请使用下方确认版标注。

2026-10-24 收到 [同名结构确认版](annotations-confirmed-20261024.json)：八件均确认结构归属，原 87 个点的坐标未改变；用户估计单侧点击误差约 3 原像素。已在[独立离线试验](OfflineTrial_20261024.md)中使用；当前标注精度不足以认证原计划的 2px 几何目标，**没有替换生产算法**。本目录的 `run_offline.py` 只供开发/诊断，不参与正式流水线。

用户随后将目标调整为约 5～6 原像素几何错位、约 0.6mm 缺陷检出；[验收修订说明](AcceptanceRevision_20261024.md)把两者分开，并核对了当前运行配置尚不能证明 0.6mm 能力的原因。

已开始第一轮隔离优化：双向九区轮廓评分与空间留区重拟合稳定性门控拦下一个原先误接受的仿射候选。八件样本的更新结果、0.6mm 合成缺陷链路测试和未达标项见[阶段进展](Progress_20261024.md)。当前仍不满足生产替换条件。

## 图像怎么看

每件有两种可重建的预览图。运行下方脚本后，图像位于 `obj/PatchNativeValidation/UnifiedRegistrationReview`，不进入 Git：

- `*_overview.png`：整件并列显示 Alpha 模板、未做局部配准的 CIS 灰度图，以及原位置二值叠加图。
- `*_grid.png`：将同一张原尺寸 ROI 均分为九区。每格同时显示模板、CIS 和叠加；标题给出**原尺寸零件 ROI 坐标**。绿色只在模板有，洋红色只在 CIS 有，白色为两者共有。Alpha 区的橙色编号仅是自动建议核对的**模板侧角点**，未找到或确认 CIS 对应点，不能算真值。此图仅是 H0 裁图下的观察材料，**没有应用候选局部矩阵**。

请特别核对：三处以上分散的同名结构是否能够对应、是否有真实缺陷、以及零件矩形是否混入邻居。不要根据单张叠加图的颜色直接认定缺陷或给出配准真值。

## 待填写的人工确认

### 推荐：直接在图上标注

双击 [annotate.html](annotate.html) 在 Edge/Chrome 中打开。它会加载本目录 `inputs` 下的原尺寸 Alpha 和 CIS 图，不需要安装软件或启动服务。按以下步骤操作：

1. 选择零件，先以 25%～50% 总览寻找独特结构，再切换至 100%～200% 放大定位。
2. 保持“对应点”模式，在左图点击一个清晰的模板角点，再在右图点击**同一实际结构**，得到同编号点对。每件尽量标出分布于至少三个区域的六组以上对应点；对周期图案，不要把相邻重复单元误当成同名结构。
3. 选择缺陷类型，在右图的真实缺陷位置拖出矩形；不确定是否为缺陷时选“待确认区域”，并在备注里解释。随后填写“目标是否完整”“变形形态”“三个分散区域能否对应”。
4. 点击“导出标注 JSON”，把下载的 `UnifiedRegistrationAnnotations.json` 发回。浏览器会尝试暂存草稿，但**不要只依赖浏览器暂存**；中途暂停也请导出备份。

修订时点击“导入已有 JSON 继续修改”，选择上一次下载的 JSON，即可保留原有点和缺陷框，仅调整需要复核的零件；完成后再次导出。

点位坐标自动按原尺寸像素保存，不受页面显示比例影响。JSON 是人工核对数据，收到后再用于评价；标注页不会修改生产算法。若图片加载失败，请确认 `annotate.html` 与 `inputs` 目录保持原有相对位置。

### 备用：手工填写 CSV

在 [review-labels.csv](review-labels.csv) 每行填写：

1. `target_complete`：目标主体是否完整且可确认归属（yes/no/uncertain）。
2. `geometry_class`：预计可由相似变换、轻微仿射解释，或明显局部非线性，或无法判定。
3. `same_structure_confirmed`：是否确认至少上、中、下或左、中、右三个互不相邻区域的同名结构（yes/no/uncertain）。
4. `real_defect_notes`：已知真实缺陷与位置；没有把握时填 uncertain。
5. `reviewer_notes`：需要排除的相邻对象、失焦、局部缺失等。

预览脚本会在 `obj/PatchNativeValidation/UnifiedRegistrationReview/candidate-landmarks.csv` 生成模板侧建议点；它们只从 Alpha 的角点产生，可能包含重复、邻居或无法确认的结构。将核对过的独立对应点填写到 [landmarks.csv](landmarks.csv)：一行一个结构，`alpha_x/y` 与 `cis_x/y` 均为本零件**原尺寸 ROI**坐标。建议每件至少六点，分布在不相邻的上/中/下或左/中/右区域，避开真实缺陷与模糊处。空白表示尚未核对，不能用当前 SIFT 的自动配对充当真值。

## 数据来历

- `inputs/img2`：`纹理重复图库/img/2` 经现有完整流程导出的五件原始 ROI；CIS 是局部变换之前的图。
- `inputs/img3_origin62`：同一排版 img/3、LayoutOriginYmm=62 的稀疏零件。
- `inputs/batch171315`：此前受约束仿射诊断的两个复杂纹理件。
- `manifest.json` 记录输入 SHA256、尺寸、阈值及来源；哈希是同一输入的复现实证，不是质量标签。

## 生成预览

```powershell
& E:/Software/Miniconda/python.exe Regression/PatchNative/UnifiedRegistration/make_review.py
```

预览脚本仅做裁块、缩图和叠加，不执行或替代配准算法。人工核对后才可写入 `review-labels.csv` / `landmarks.csv` 作为验收真值。
