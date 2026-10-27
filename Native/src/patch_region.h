#pragma once
#include <opencv2/core.hpp>

namespace cis::patch
{
// 以下数据仅在一次配准调用内使用。ROI 均是输入上下文原图像素，
// bounds 则相对核心 ROI；Mat 由值语义持有独立处理副本，不修改调用方 Alpha。
struct RegistrationContent
{
    cv::Rect bounds;        // core 内的目标范围，避免少量被边界截断的碎片改变工作框。
    cv::Mat context_binary; // 仅配准使用；为空时沿用原 Alpha 阈值化路径。
    int excluded_fragments = 0;
    int excluded_core_pixels = 0;
};

struct RegistrationCore
{
    cv::Rect roi;
    const char *basis = "layout";
};

// 根据完整图案定位稳定的工作窗口；失败时返回原布局窗口及原因。
RegistrationCore registration_core(const cv::Mat &alpha, cv::Rect core, int threshold);

// 只整理配准证据，最终检测区域及散点仍由未经修改的 Alpha 定义。
RegistrationContent registration_content(const cv::Mat &alpha, cv::Rect core, int threshold);
} // namespace cis::patch
