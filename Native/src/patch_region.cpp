// 配准目标与真实搜索上下文的整理，不估计矩阵、不判定缺陷。
#include "patch_region.h"
#include <opencv2/imgproc.hpp>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <utility>
#include <vector>

namespace cis::patch
{
// 检测裁框只规定最终输出范围，不应同时决定 SIFT 图像的采样相位。
// 对有真实上下文的完整目标，用覆盖至少 95% 前景的完整连通结构共同
// 确定几何中心，窗口尺寸仍取排版给出的逻辑零件尺寸。不是只取最大
// 连通域，也不把未参与定位的小散点从模板/缺陷图中删除。
//
// 同一主体在裁框中上下移动时，完整结构的边界在原图中不变，因而这个
// 窗口也不变；少量边缘碎片不会再改变 resize 的大小和像素中心位置。
// 必须同时满足：窗口有真实像素上下文，且包含原裁框中的每个前景像素。
// 不满足时保留原窗口，不能通过丢弃真实散点或补造背景获得“稳定”。
RegistrationCore registration_core(const cv::Mat &alpha, cv::Rect core, int threshold)
{
    RegistrationCore result{core, "layout/no-context"};
    const cv::Rect image(0, 0, alpha.cols, alpha.rows);
    if (core == image)
    {
        return result;
    }
    cv::Mat binary;
    cv::threshold(alpha, binary, threshold, 255, cv::THRESH_BINARY);
    const int foreground = cv::countNonZero(binary(core));
    if (!foreground)
    {
        result.basis = "layout/empty";
        return result;
    }
    struct Component
    {
        cv::Rect bounds;
        int pixels;
    };
    std::vector<Component> complete;
    std::vector<std::vector<cv::Point>> contours;
    std::vector<cv::Vec4i> hierarchy;
    cv::findContours(binary, contours, hierarchy, cv::RETR_TREE, cv::CHAIN_APPROX_SIMPLE);
    for (size_t index = 0; index < contours.size(); ++index)
    {
        int depth = 0;
        for (int parent = hierarchy[index][3]; parent >= 0; parent = hierarchy[parent][3])
        {
            ++depth;
        }
        if (depth % 2 || contours[index].empty())
        {
            continue; // 奇数层是背景孔洞，偶数层仍可能有独立前景散点。
        }
        const auto bounds = cv::boundingRect(contours[index]);
        if ((bounds & core) != bounds)
        {
            continue; // 只统计完整位于核心内的结构，穿边结构不参与中心定位。
        }
        const auto seed = contours[index].front();
        if (binary.at<uint8_t>(seed) != 255)
        {
            continue;
        }
        // 返回真实 8 邻域前景像素数，而不是轮廓外接矩形面积；127 仅为
        // 本地副本上的访问标记。轮廓已给出该连通结构的精确包围框，
        // 在框内填充与全图填充像素集合相同，却不必为每个小散点反复
        // 分配/清零整幅大图大小的隐式 floodFill mask。
        auto component = binary(bounds);
        const int pixels =
            cv::floodFill(component, seed - bounds.tl(), cv::Scalar(127), nullptr, cv::Scalar(0), cv::Scalar(0), 8);
        complete.push_back({bounds, pixels});
    }
    std::sort(complete.begin(), complete.end(),
              [](const Component &a, const Component &b) { return a.pixels > b.pixels; });
    const int required = static_cast<int>(std::ceil(.95 * foreground));
    int covered = 0, last_area = 0;
    cv::Rect support;
    for (const auto &component : complete)
    {
        // 面积相同的结构全部纳入，避免连通域编号/扫描顺序决定中心。
        if (covered >= required && component.pixels < last_area)
        {
            break;
        }
        support |= component.bounds;
        covered += component.pixels;
        last_area = component.pixels;
    }
    if (covered < required || support.empty())
    {
        result.basis = "layout/incomplete-support";
        return result;
    }
    // floor 对整数平移等变；不能使用 midpoint-to-even，否则半像素中心
    // 在奇偶行移动后可能让窗口额外跳动 1px。
    const cv::Rect canonical(support.x + static_cast<int>(std::floor((support.width - core.width) * .5)),
                             support.y + static_cast<int>(std::floor((support.height - core.height) * .5)), core.width,
                             core.height);
    if ((canonical & image) != canonical)
    {
        result.basis = "layout/insufficient-context";
        return result;
    }
    const auto overlap = canonical & core;
    if (overlap.empty() || cv::countNonZero(binary(overlap)) != foreground)
    {
        result.basis = "layout/retain-all-content";
        return result;
    }
    result.roi = canonical;
    result.basis = "content-anchored";
    return result;
}

// 裁框位置稍变时，相邻图案可能只进入边缘几十行。直接对 core 中所有
// 前景取 boundingRect，会让工作图尺寸、采样相位和 SIFT 预算一起变化。
// 这里检查真实上下文中的连通结构，而不是取最大连通域猜目标：
// 1. 完全位于 core 内的结构全部保留，包括微小、离散的真实图案；
// 2. 仅当完整结构已占 core 前景至少 95% 时，才排除余下的边界截断片段；
// 3. 该筛选只影响配准证据，不改变缺陷检测、保存图和结果坐标使用的 Alpha。
// 这不是按面积丢弃小图案：哪怕只有一个像素，完整位于 core 内也保留。
// 若边界结构占比大或与主体相连，可能是目标自身被截断，不能静默删除；
// 则沿用原图，交给后续匹配、几何、稳定性及错周期检查。
RegistrationContent registration_content(const cv::Mat &alpha, cv::Rect core, int threshold)
{
    RegistrationContent result;
    cv::Mat core_binary;
    cv::threshold(alpha(core), core_binary, threshold, 255, cv::THRESH_BINARY);
    result.bounds = cv::boundingRect(core_binary);
    const cv::Rect image_bounds(0, 0, alpha.cols, alpha.rows);
    const bool touches_boundary =
        cv::countNonZero(core_binary.row(0)) || cv::countNonZero(core_binary.row(core.height - 1)) ||
        cv::countNonZero(core_binary.col(0)) || cv::countNonZero(core_binary.col(core.width - 1));
    if (!touches_boundary || core == image_bounds)
    {
        // 正常留白裁图不做整图连通域分析，也不改变原有工作图的任何像素。
        return result;
    }

    cv::Mat binary;
    cv::threshold(alpha, binary, threshold, 255, cv::THRESH_BINARY);
    // 边界组件没有预先给定包围框，复用一个访问掩膜。每个组件仍只
    // 填充一次，避免 floodFill 每次隐式分配和清零整幅大图缓冲。
    cv::Mat visited = cv::Mat::zeros(binary.rows + 2, binary.cols + 2, CV_8U);
    int boundary_components = 0;
    std::vector<cv::Rect> complete_boundary_components;
    const auto clear_boundary_component = [&](int x, int y) {
        if (binary.at<uint8_t>(y, x) == 255)
        {
            // 只访问与边界相连的前景，不为整幅大图分配 CV_32S 标签矩阵。
            // floodFill 在局部二值副本上按 8 邻域清除；完整散点、孔洞内
            // 不相连的其他结构保持不变，同一连通片段也不会重复访问。
            cv::Rect component;
            cv::floodFill(binary, visited, {x, y}, cv::Scalar(127), &component, cv::Scalar(0), cv::Scalar(0), 8);
            if ((component & core) == component)
            {
                // 刚好接触边界、但实际完整位于框内的散点不是截断片段。
                // 完整结构单独标记为 128，防止其位于另一片段外接框/孔洞内时，
                // 被该片段的 127 临时标记一并清除；结束后原样恢复为 255。
                binary(component).setTo(128, binary(component) == 127);
                complete_boundary_components.push_back(component);
            }
            else
            {
                binary(component).setTo(0, binary(component) == 127);
                ++boundary_components;
            }
        }
    };
    for (int x = core.x; x < core.br().x; ++x)
    {
        clear_boundary_component(x, core.y);
        clear_boundary_component(x, core.br().y - 1);
    }
    for (int y = core.y; y < core.br().y; ++y)
    {
        clear_boundary_component(core.x, y);
        clear_boundary_component(core.br().x - 1, y);
    }
    for (const auto component : complete_boundary_components)
    {
        binary(component).setTo(255, binary(component) == 128);
    }
    const int foreground_pixels = cv::countNonZero(core_binary);
    const int boundary_pixels = foreground_pixels - cv::countNonZero(binary(core));
    constexpr double maximum_boundary_fraction = .05;
    if (boundary_pixels > maximum_boundary_fraction * foreground_pixels)
    {
        // 试清除发生在独立副本上，证据不足时整个副本直接丢弃，恢复原路径。
        return result;
    }
    if (boundary_components)
    {
        result.excluded_fragments = boundary_components;
        result.excluded_core_pixels = boundary_pixels;
        result.bounds = cv::boundingRect(binary(core));
        result.context_binary = std::move(binary);
    }
    return result;
}

} // namespace cis::patch
