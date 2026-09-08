#pragma once
#include <cstdint>
namespace cis::patch
{
// 局部对比窗口只用于建立“相对周围背景仍然可见”的细线前景证据。
// 固定为物理尺寸可避免分析缩放或 DPI 改变时窗口语义漂移；它不再由最大允许线宽控制。
// 10 mm 与此前 FineLineMaxWidthMm=5 mm 时约 61 px 的默认窗口基本一致，降低算法改动风险。
inline constexpr double FineLineLocalContrastWindowMm = 10.0;

// 细线通道的几何容差使用独立物理尺寸，不能复用普通面积缺陷的 DefectToleranceInner。
// 以下默认值等价于 LayoutDpi=300、analysisScale=0.5、旧版 DefectToleranceInner=6 px 时
// 已通过真实断线样本验证的 1/2/6/12 px，保证解耦前后基准结果保持一致。
inline constexpr double FineLineTangentialAlignmentToleranceMm = 0.17;
inline constexpr double FineLineNormalAlignmentToleranceMm = 0.40;
inline constexpr double FineLineEndpointSearchRadiusMm = 1.00;
inline constexpr double FineLineEndpointAnchorLengthMm = 2.00;

// 局部配准使用独立工作分辨率，避免 DefectDetectScale 同时改变缺陷检测精度、
// SIFT 特征分布、RANSAC 物理容差和最大平移范围。当前常见零件宽约 2220 px，
// 目标宽 700 px 与原 0.3 倍路径接近，可在基本不增加耗时的前提下稳定参数语义。
inline constexpr int LocalAlignmentTargetWidthPx = 700;
inline constexpr double LocalAlignmentRansacThresholdOriginalPx = 3.0;
inline constexpr double LocalAlignmentMaxTranslationOriginalPx = 40.0;
inline constexpr double LocalAlignmentMaxMatchDisplacementOriginalPx = 60.0;
inline constexpr double LocalAlignmentMaxResidualRmsOriginalPx = 3.0;
// 常规情况下要求 RANSAC 内点率达到 50%。复杂彩色/文字图案可能同时产生大量
// 合法但不够唯一的 SIFT 候选，使“绝对内点充分”的正确矩阵被比例阈值误拒绝。
// 条件通道只把候选送往后续覆盖率、RMS、矩阵和轮廓四重门控，并不直接接受矩阵。
inline constexpr double LocalAlignmentMinimumInlierRatio = 0.50;
inline constexpr double LocalAlignmentConditionalMinimumInlierRatio = 0.35;
inline constexpr int LocalAlignmentConditionalMinimumInlierCount = 12;
inline constexpr double LocalAlignmentConditionalMinimumBoundingCoverage = 0.08;
inline constexpr double LocalAlignmentConditionalMaxResidualRmsOriginalPx = 2.5;
inline constexpr double LocalAlignmentTranslationRefineRadiusOriginalPx = 10.0;
// SIFT/RANSAC 或内点中位数已经给出可靠初值后，只允许距离场再修正 1 px；
// 防止轮廓评分被真实缺陷牵引，覆盖掉特征点提供的几何共识。
inline constexpr double LocalAlignmentCandidateRefineRadiusOriginalPx = 1.0;
// 距离场先在工作图上做整数搜索，再以 0.25 px 步长搜索整数最优点周围的 2 px 邻域。
// 700 px 固定工作宽度下，这已经能把最终平移细化到原图约 1 px 以内；最终 Warp 仍只执行一次。
inline constexpr double LocalAlignmentSubpixelStepWorkPx = 0.25;
inline constexpr double LocalAlignmentSubpixelRadiusWorkPx = 1.0;
// 距离超过该值后不再继续增大惩罚，避免真实缺口、飞墨等少量异常轮廓牵引配准结果。
inline constexpr double LocalAlignmentChamferDistanceCapWorkPx = 4.0;
inline constexpr int LocalAlignmentMaxEdgeSamplesPerDirection = 6000;
// 未配准轮廓平均误差低于此原图像素值时，继续重采样的收益很小，直接保留全局对齐结果。
inline constexpr double LocalAlignmentNotNeededScoreOriginalPx = 0.50;
inline constexpr double LocalAlignmentNotNeededShiftOriginalPx = 0.75;
// 小于 10% 的全局轮廓收益很容易来自阈值噪声、JPEG 纹理或真实缺陷，而不是稳定错位。
// 宁可保留 H0 结果，也不为小幅评分收益承担新增细线误检的风险。
inline constexpr double LocalAlignmentMinEdgeImprovementRatio = 0.10;
// 纯轮廓平移缺少特征几何共识，周期图案可能在错误的小位移上取得有限收益；
// 因此快速分支使用更严格的 20%，不足时交给 SIFT 分支继续判断。
inline constexpr double LocalAlignmentFastTranslationMinImprovementRatio = 0.20;
// 快速搜索的最优点若贴近搜索边界，说明距离场仍想继续向外移动，当前结果不是可信极小值。
// 这类候选不得直接应用，应交给具有特征几何约束的 SIFT 分支继续判断。
inline constexpr double LocalAlignmentFastTranslationBoundaryRatio = 0.85;
inline constexpr double LocalAlignmentStrongEdgeImprovementRatio = 0.20;
inline constexpr double LocalAlignmentStrongCaseMaxLocalRegressionPixels = 0.75;
inline constexpr double LocalAlignmentTranslationConsensusP80OriginalPx = 5.0;
// 对明显整体平移，若 SIFT 内点位移高度一致，可直接信任纯平移模型；此时逐格轮廓
// 可能被真实缺陷干扰而出现假退化。小位移仍必须通过局部稳定性，避免新增细线误检。
inline constexpr double LocalAlignmentStrongTranslationConsensusP80OriginalPx = 2.0;
inline constexpr double LocalAlignmentStrongTranslationMinMagnitudeOriginalPx = 5.0;
inline constexpr uint64_t LocalAlignmentRansacSeed = 0x5EED2026UL;
inline constexpr int LocalAlignmentValidationGridSize = 3;
inline constexpr int LocalAlignmentMinReferenceEdgesPerCell = 30;
inline constexpr double LocalAlignmentMaxLocalRegressionPixels = 0.25;
// 局部稳定门控只允许绝对 0.25 个工作像素的轻微数值波动；不再按当前误差比例放宽，
// 否则原本较差的局部区域反而会获得更大的退化额度，容易新增细线误检。

} // namespace cis::patch
