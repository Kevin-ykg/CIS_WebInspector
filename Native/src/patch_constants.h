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

// 配准采样只取决于模板物理尺寸，不取决于裁图空白、原点或 DefectDetectScale。
// 3 px/mm 是工作图密度目标，不是最终检测分辨率/精度承诺；小模板保留至少约 500 px
// 的宽度预算（不放大原图），长边和总像素上限约束极大、极细长零件的成本。
inline constexpr double LocalAlignmentWorkPixelsPerMm = 3.0;
inline constexpr int LocalAlignmentMinimumWorkWidthPx = 500;
inline constexpr int LocalAlignmentMaximumWorkSidePx = 1200;
inline constexpr int LocalAlignmentMaximumWorkPixels = 1000000;
// 九区中最差区域的平均双向轮廓距离优质标准。超过 8 原像素只把已改善
// 整体均值的候选标为 AppliedDegraded/需复核，不单独拒绝局部矩阵；
// 该量不是人工同名角点误差或 0.6 mm 缺陷检出保证。
inline constexpr double LocalAlignmentMaximumCellChamferOriginalPx = 8.0;
// 如果全局对准在有足够图案分布的区域已达到较低轮廓误差，而两个合法
// 候选的九区均值都没有进一步改善，可记为 NotNeeded，不额外采样。
// 这些量仅参与该状态判断，不是同名角点精度承诺。
inline constexpr double LocalAlignmentGoodGlobalMeanOriginalPx = 2.0;
inline constexpr double LocalAlignmentGoodGlobalWorstCellOriginalPx = 4.0;
inline constexpr int LocalAlignmentGoodGlobalMinimumCells = 6;
// 估计窗口保留 16 原像素的真实上下文。缺少时明确拒绝，不填充背景伪造配准证据。
inline constexpr int LocalAlignmentContextPixels = 16;
inline constexpr int LocalAlignmentFeatureBudget = 100;
// 全局校正后仍可能残留轻微剪切。相似/仿射与单应候选使用同一 2° 剪切边界，
// 避免较低自由度的仿射仅因 1° 旧限值被拒绝、反而要求更自由的单应性。
// 这不是非线性矫正；缩放、非等比、位移、匹配共识、稳定性和错周期检查不变。
inline constexpr double LocalAlignmentMaximumShearDegrees = 2.0;
// RANSAC 的候选内点允许图上测量/采样噪声，不是最终对准误差上限；
// 最终仍须检查 RMS、稳定性及九区轮廓是否整体改善。按原图物理尺度换算，
// 避免同一值的“工作图像素”在不同零件上变成不同原图容差。
inline constexpr double LocalAlignmentRansacThresholdOriginalPx = 8.0;
inline constexpr double LocalAlignmentPeriodToleranceOriginalPx = 3.0;
// 统一空间约束 SIFT 配对的绝对描述子距离上限；OpenCV SIFT 描述子模长约 512。
inline constexpr double LocalAlignmentSpatialMaxDescriptorDistance = 200.0;
// 重复位置必须由多处描述子投票且模板自重叠率足够高；只用于质量复核，不用于选周期。
inline constexpr int LocalAlignmentMaximumPeriodAliases = 12;
inline constexpr double LocalAlignmentPeriodSelfOverlap = .85;
inline constexpr double LocalAlignmentPeriodAbsoluteGap = .01;
inline constexpr double LocalAlignmentPeriodRelativeGap = .05;
inline constexpr uint64_t LocalAlignmentRansacSeed = 0x5EED2026UL;

} // namespace cis::patch
