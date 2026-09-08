#include "patch_detector.h"
#include "patch_constants.h"
#include <stdexcept>

namespace cis::patch
{
// 单通道输入仅返回引用计数视图，多通道才分配灰度图。所有调用点均只读原始输入。
cv::Mat gray(const cv::Mat &source)
{
    if (source.channels() == 1)
        return source;
    cv::Mat converted;
    cv::cvtColor(source, converted, source.channels() == 4 ? cv::COLOR_BGRA2GRAY : cv::COLOR_BGR2GRAY);
    return converted;
}
double pixels_per_mm(double dpi)
{
    return (dpi > 0 && std::isfinite(dpi) ? dpi : 300.) / 25.4;
}
// 对应迁移前 ConvertLengthMmToScaledPixels。
// 把用户配置的物理长度换算为当前检测尺度的像素长度。
// 形态学核和边缘屏蔽均在 TIFF 对齐空间中执行，因此统一使用 LayoutDpi；
// 返回 0 表示配置关闭，正长度即使缩小后不足 1 px 也至少保留 1 px。
int length_pixels(double mm, double dpi, double scale)
{
    if (mm <= 0 || !std::isfinite(mm))
        return 0;
    double value = mm * pixels_per_mm(dpi) * (scale > 0 && std::isfinite(scale) ? scale : 1.);
    // 配置物理长度转换沿用 AwayFromZero；与几何坐标的 midpoint-to-even 不同。
    return value >= INT_MAX ? INT_MAX : std::max(1, static_cast<int>(std::round(value)));
}
// 对应迁移前 ConvertAreaMm2ToScaledPixels。
// 把用户配置的物理面积阈值换算到当前检测尺度的像素面积。
// LayoutDpi 定义 TIFF/对齐目标空间的像素密度，线性缩放后面积再乘 scale²。
int area_pixels(double mm2, double dpi, double scale)
{
    double ppm = pixels_per_mm(dpi), s = scale > 0 && std::isfinite(scale) ? scale : 1.;
    double value = std::max(0., mm2) * ppm * ppm * s * s;
    return value >= INT_MAX ? INT_MAX : std::max(1, static_cast<int>(std::round(value)));
}
// 对应原 ExpandRect：候选证据 ROI 扩大后仍必须落在零件内部。
cv::Rect expand_rect(cv::Rect r, int margin, cv::Size size)
{
    int x = std::max(0, r.x - margin), y = std::max(0, r.y - margin);
    int right = std::min(size.width, r.br().x + margin), bottom = std::min(size.height, r.br().y + margin);
    return {x, y, std::max(1, right - x), std::max(1, bottom - y)};
}
// 对应原 ScaleRect：起点向下取整、终点向上取整，防止细小断口在坐标转换时被截掉。
cv::Rect scale_rect(cv::Rect r, double sx, double sy, cv::Size size)
{
    int x = std::max(0, static_cast<int>(std::floor(r.x * sx))),
        y = std::max(0, static_cast<int>(std::floor(r.y * sy)));
    int right = std::min(size.width, static_cast<int>(std::ceil(r.br().x * sx))),
        bottom = std::min(size.height, static_cast<int>(std::ceil(r.br().y * sy)));
    return {x, y, std::max(1, right - x), std::max(1, bottom - y)};
}
namespace
{
struct Components
{
    std::vector<cv::Rect> boxes;
    std::vector<int> areas;
    int maximum = 0;
};
// 对应迁移前 AnalyzeConnectedComponentsPreservingOriginalArea。
// 对屏蔽前的原始差分执行连通域分析，并用边缘屏蔽掩膜决定整个连通域是否放行。
// 完全位于屏蔽区内的连通域被忽略；只要有任意像素越过屏蔽区，就恢复该连通域
// 的完整轮廓，并以屏蔽前的完整面积参与阈值判断。方法返回前会把 binaryImg
// 更新为放行后的完整连通域掩膜，供结果图和后续处理使用。
Components components(cv::Mat &difference, const cv::Mat &exclusion, int threshold)
{
    cv::Mat labels, stats, centroids;
    int count = cv::connectedComponentsWithStats(difference, labels, stats, centroids);
    std::vector<bool> outside(count, false);
    // 屏蔽用于整块放行：先标记有没有像素越过屏蔽带，再按原标签恢复完整连通域。
    // 如果先做 difference &= ~mask，缺陷会被切碎，面积阈值和数量都会改变。
    for (int y = 0; y < labels.rows; ++y)
        for (int x = 0; x < labels.cols; ++x)
        {
            int label = labels.ptr<int>(y)[x];
            if (label > 0 && !exclusion.ptr<uint8_t>(y)[x])
                outside[label] = true;
        }
    for (int y = 0; y < labels.rows; ++y)
        for (int x = 0; x < labels.cols; ++x)
        {
            int label = labels.ptr<int>(y)[x];
            difference.ptr<uint8_t>(y)[x] = label > 0 && outside[label] ? 255 : 0;
        }
    Components result;
    for (int label = 1; label < count; ++label)
        if (outside[label])
        {
            int area = stats.at<int>(label, cv::CC_STAT_AREA);
            result.maximum = std::max(result.maximum, area);
            // 保留严格 > 阈值，等于阈值不计数。最大面积仍记录放行但未过面积阈值的候选。
            if (area > threshold)
            {
                result.boxes.emplace_back(stats.at<int>(label, 0), stats.at<int>(label, 1), stats.at<int>(label, 2),
                                          stats.at<int>(label, 3));
                result.areas.push_back(area);
            }
        }
    return result;
}
void append_defect(PatchResult &result, int kind, cv::Rect original, cv::Rect work, cv::Rect tight, int pixels,
                   double ppm)
{
    result.defects.push_back({kind, original.x, original.y, original.width, original.height, work.x, work.y, work.width,
                              work.height, 0, tight.width / ppm, tight.height / ppm, pixels / (ppm * ppm)});
}
} // namespace

// 原 DetectCore 的完整处理链。alpha/cis 为已裁出的原分辨率零件，方向与原 PatchCropper 相同。
// 配准失败不等于缺陷：未得到可信矩阵时，继续使用全局对准的 CIS 裁图做三类检测。
// 此处只返回数据，不写磁盘、不调用 UI；C# 保留现有颜色、文件命名、日志与结果汇总。
PatchResult detect(const cv::Mat &alpha, const cv::Mat &cis, const CisPatchConfig &config, Worker &worker)
{
    auto started = Clock::now();
    PatchResult result;
    cv::Mat alpha_gray = gray(alpha), cis_gray = gray(cis);
    double scale = config.detection_scale;
    if (scale <= 0 || !std::isfinite(scale))
        throw std::invalid_argument("DefectDetectScale must be positive and finite");
    int scaled_width = static_cast<int>(alpha_gray.cols * scale);
    if (scaled_width < config.minimum_scaled_width && alpha_gray.cols > config.minimum_scaled_width)
        scale = static_cast<double>(config.minimum_scaled_width) / alpha_gray.cols;
    result.summary.detection_scale = scale;

    // 普通差分使用 Nearest；细线和配准各有独立尺度，不将所有缩放合并成一种插值。
    cv::Mat alpha_scaled, cis_scaled, cis_aligned, cis_original;
    cv::resize(alpha_gray, alpha_scaled, {}, scale, scale, cv::INTER_NEAREST);
    cv::resize(cis_gray, cis_scaled, alpha_scaled.size(), 0, 0, cv::INTER_NEAREST);
    cis_aligned = cis_scaled;
    if (config.enable_alignment)
    {
        // 配准使用独立的约 700 px 工作宽度，不能复用普通差分的 scale。
        // 这样调整 DefectDetectScale 只改变检测采样，不改变 SIFT 的特征图和几何容差。
        double alignment_scale =
            std::min(1., LocalAlignmentTargetWidthPx / static_cast<double>(std::max(1, alpha_gray.cols)));
        cv::Size size(std::max(1, round_even(alpha_gray.cols * alignment_scale)),
                      std::max(1, round_even(alpha_gray.rows * alignment_scale)));
        cv::Mat template_work, captured_work, template_blurred, captured_blurred;
        cv::resize(alpha_gray, template_work, size, 0, 0, cv::INTER_AREA);
        cv::resize(cis_gray, captured_work, size, 0, 0, cv::INTER_AREA);
        cv::blur(template_work, template_blurred, {3, 3});
        cv::blur(captured_work, captured_blurred, {3, 3});
        auto alignment = local_align(template_blurred, captured_blurred, config.alpha_threshold, config.cis_threshold,
                                     alignment_scale, worker);
        result.summary.alignment_ms = alignment.milliseconds;
        result.log =
            alignment.diagnostic + ", time=" + std::to_string(static_cast<int64_t>(alignment.milliseconds)) + "ms";
        if (alignment.applied)
        {
            // 对应原 CreateAlignedOutputs：2x2 旋转/缩放项无量纲，只有平移项需要换尺度。
            // 目标检测图只 Warp 一次；若细线通道需要原图，再由原像素独立 Warp，
            // 不能把低分辨率配准图放大，否则会丢失细线灰度并改变二值化边缘。
            cv::Mat work = alignment.transform.clone();
            double ratio = scale / alignment_scale;
            work.at<double>(0, 2) *= ratio;
            work.at<double>(1, 2) *= ratio;
            cv::warpAffine(cis_scaled, cis_aligned, work, alpha_scaled.size(), cv::INTER_CUBIC);
            if (config.enable_fine_line || (config.output_flags & CIS_PATCH_CIS_BINARY))
            {
                cv::Mat original = alignment.transform.clone();
                original.at<double>(0, 2) /= alignment_scale;
                original.at<double>(1, 2) /= alignment_scale;
                // 原图可能有 3/4 通道，先 Warp 彩色再灰度，与旧细线通道保持相同舍入顺序。
                cv::warpAffine(cis, cis_original, original, cis.size(), cv::INTER_CUBIC);
            }
        }
    }
    if (config.output_flags & CIS_PATCH_CIS_BINARY)
    {
        auto saved = gray(cis_original.empty() ? cis : cis_original);
        cv::threshold(saved, result.images[CIS_PATCH_CIS_ORIGINAL], config.cis_threshold, 255, cv::THRESH_BINARY);
    }
    cv::Mat alpha_binary, cis_binary, inner_dilated, outer_dilated, difference_inner, difference_outer;
    cv::threshold(alpha_scaled, alpha_binary, config.alpha_threshold, 255, cv::THRESH_BINARY);
    cv::threshold(cis_aligned, cis_binary, config.cis_threshold, 255, cv::THRESH_BINARY);
    int inner_kernel_size = std::max(1, length_pixels(config.tolerance_inner_mm, config.layout_dpi, scale));
    int outer_kernel_size = std::max(1, length_pixels(config.tolerance_outer_mm, config.layout_dpi, scale));
    int inner_area_threshold = area_pixels(config.area_inner_mm2, config.layout_dpi, scale),
        outer_area_threshold = area_pixels(config.area_outer_mm2, config.layout_dpi, scale);
    auto exclusion = edge_exclusion(alpha_binary, length_pixels(config.exclusion_outer_mm, config.layout_dpi, scale),
                                    length_pixels(config.exclusion_inner_mm, config.layout_dpi, scale));
    if (config.output_flags & CIS_PATCH_EDGE_MASK)
        cv::resize(exclusion, result.images[CIS_PATCH_EDGE], alpha_gray.size(), 0, 0, cv::INTER_NEAREST);
    // 内部：模板有、实拍没有；外部：实拍有、模板没有。膨胀提供原有的物理错位容差。
    cv::dilate(cis_binary, inner_dilated, ellipse(inner_kernel_size));
    cv::subtract(alpha_binary, inner_dilated, difference_inner);
    cv::dilate(alpha_binary, outer_dilated, ellipse(outer_kernel_size));
    cv::subtract(cis_binary, outer_dilated, difference_outer);
    FineResult fine;
    // 细线至少使用 0.5 倍细节尺度，且不放大超过原图；这不是普通面积通道的 scale。
    // 两条通道共享已求出的局部矩阵，不会各自独立配准产生矛盾坐标。
    double fine_scale = std::min(1., std::max(.5, scale));
    cv::Size fine_size;
    if (config.enable_fine_line && config.fine_min_length_mm > 0 && cv::countNonZero(exclusion) > 0)
    {
        fine_size = {std::max(1, round_even(alpha_gray.cols * fine_scale)),
                     std::max(1, round_even(alpha_gray.rows * fine_scale))};
        cv::Mat fine_template, fine_captured;
        auto captured = gray(cis_original.empty() ? cis_gray : cis_original);
        cv::resize(alpha_gray, fine_template, fine_size, 0, 0, cv::INTER_AREA);
        cv::resize(captured, fine_captured, fine_size, 0, 0, cv::INTER_AREA);
        fine = fine_line(fine_template, fine_captured, config.cis_threshold, fine_scale, config);
    }
    auto inner = components(difference_inner, exclusion, inner_area_threshold),
         outer = components(difference_outer, exclusion, outer_area_threshold);
    // 细线仅在可视化差分上叠入内部颜色底图；分类、计数和测量保持第三类独立。
    if (!fine.boxes.empty())
    {
        cv::Mat mask;
        cv::resize(fine.mask, mask, alpha_binary.size(), 0, 0, cv::INTER_NEAREST);
        cv::bitwise_or(difference_inner, mask, difference_inner);
    }
    double ppm = pixels_per_mm(config.layout_dpi) * scale;
    // 最大面积换算保留旧 C# 乘法次序，避免边界附近额外浮点差异。
    double base_ppm = pixels_per_mm(config.layout_dpi), area_denominator = base_ppm * base_ppm * scale * scale;
    result.summary.max_inner_mm2 = inner.maximum / area_denominator;
    result.summary.max_outer_mm2 = outer.maximum / area_denominator;
    result.summary.inner_count = static_cast<int>(inner.boxes.size());
    result.summary.outer_count = static_cast<int>(outer.boxes.size());
    result.summary.fine_count = static_cast<int>(fine.boxes.size());
    result.summary.max_fine_length_mm = fine.longest;
    result.summary.max_fine_width_mm = fine.widest;
    result.summary.is_pass =
        inner.maximum <= inner_area_threshold && outer.maximum <= outer_area_threshold && fine.boxes.empty();
    for (int kind = 0; kind < 2; ++kind)
    {
        const auto &group = kind == 0 ? inner : outer;
        for (size_t i = 0; i < group.boxes.size(); ++i)
        {
            auto r = group.boxes[i];
            cv::Rect original(static_cast<int>(r.x / scale), static_cast<int>(r.y / scale),
                              std::max(1, static_cast<int>(r.width / scale)),
                              std::max(1, static_cast<int>(r.height / scale)));
            append_defect(result, kind, original, r, r, group.areas[i], ppm);
            result.defects.back().area_mm2 = group.areas[i] / area_denominator;
        }
    }
    for (size_t i = 0; i < fine.boxes.size(); ++i)
    {
        auto original = scale_rect(fine.boxes[i], alpha_gray.cols / static_cast<double>(fine_size.width),
                                   alpha_gray.rows / static_cast<double>(fine_size.height), alpha_gray.size());
        auto work = scale_rect(fine.boxes[i], alpha_binary.cols / static_cast<double>(fine_size.width),
                               alpha_binary.rows / static_cast<double>(fine_size.height), alpha_binary.size());
        append_defect(result, 2, original, work, fine.tight[i], fine.areas[i], base_ppm * fine_scale);
        result.defects.back().area_mm2 = fine.areas[i] / (base_ppm * base_ppm * fine_scale * fine_scale);
    }
    if (config.output_flags & CIS_PATCH_VISUALIZATION)
    {
        // 所有输出均是本次分配的 Mat，不引用输入裸指针。结果句柄接管引用计数，
        // C# 只有确实保存图像时才复制这些像素；纯结果统计不会跨边界搬运整张图。
        result.images[CIS_PATCH_ALPHA] = alpha_binary;
        result.images[CIS_PATCH_CIS] = cis_binary;
        result.images[CIS_PATCH_INNER] = difference_inner;
        result.images[CIS_PATCH_OUTER] = difference_outer;
    }
    result.summary.defect_count = static_cast<uint32_t>(result.defects.size());
    result.summary.detection_ms = elapsed(started);
    return result;
}
} // namespace cis::patch
