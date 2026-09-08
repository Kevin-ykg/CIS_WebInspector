#include "patch_detector.h"
#include "patch_constants.h"
#include <limits>

namespace cis::patch
{
namespace
{
constexpr double infinity = std::numeric_limits<double>::infinity();

// 对应迁移前 BuildExactBoundaryBand。
// 生成严格离散总宽度的二值边界带。宽度被拆分到轮廓外侧和内侧；奇数宽度时
// 多出的一个像素放在结构内侧，以保证 thickness=1 仍覆盖真实轮廓像素。
cv::Mat boundary_band(const cv::Mat &binary, int thickness)
{
    cv::Mat band = cv::Mat::zeros(binary.size(), CV_8U);
    if (thickness <= 0)
        return band;
    // 总宽度拆到轮廓内外；奇数多出的像素置于内侧，1 px 仍覆盖真实结构轮廓。
    int outside = thickness / 2, inside = thickness - outside;
    cv::Mat dilated, eroded;
    if (outside > 0)
        cv::dilate(binary, dilated, ellipse(outside * 2 + 1));
    else
        binary.copyTo(dilated);
    if (inside > 0)
        cv::erode(binary, eroded, ellipse(inside * 2 + 1));
    else
        binary.copyTo(eroded);
    cv::subtract(dilated, eroded, band);
    return band;
}
// 对应迁移前 CalculateFineLineLocalContrastThreshold。
// 统一计算细线局部对比度阈值，保证前景提取与恢复复核使用同一尺度。
int contrast_threshold(int absolute)
{
    return std::max(9, std::min(20, round_even(absolute * .11)));
}

// 对应迁移前 BuildFineLineForegroundEvidence。
// 构造细线前景证据：绝对亮度负责稳定识别正常白色图案，白顶帽负责保留
// 光照不均或整体偏灰、但相对局部背景仍然清晰连续的细线。
cv::Mat foreground_evidence(const cv::Mat &image, int threshold, int diameter)
{
    cv::Mat foreground, opened, top_hat, contrast;
    cv::threshold(image, foreground, threshold, 255, cv::THRESH_BINARY);
    diameter = std::max(3, diameter);
    if ((diameter & 1) == 0)
        ++diameter;
    // 白顶帽补回偏灰但相对局部背景仍清晰的线条，减少绝对阈值造成的假断线。
    cv::morphologyEx(image, opened, cv::MORPH_OPEN, ellipse(diameter));
    cv::subtract(image, opened, top_hat);
    cv::threshold(top_hat, contrast, contrast_threshold(threshold), 255, cv::THRESH_BINARY);
    cv::bitwise_or(foreground, contrast, foreground);
    return foreground;
}

// 对应迁移前 CalculateCrossSectionMissingRatio。
// 计算缺口附近模板笔画有多少宽度在 CIS 前景中确实缺失。
// 结果接近 1 表示笔画被完整截断；仅中心变暗而两侧仍连接时结果明显较低。
double cross_section_missing(const cv::Mat &pattern, const cv::Mat &foreground, const cv::Mat &gap, double half_width)
{
    cv::Mat expanded, section, inverse, missing;
    int radius = std::max(1, static_cast<int>(std::ceil(half_width)));
    cv::dilate(gap, expanded, ellipse(radius * 2 + 1));
    cv::bitwise_and(pattern, expanded, section);
    int count = cv::countNonZero(section);
    if (count <= 0)
        return 0;
    cv::bitwise_not(foreground, inverse);
    cv::bitwise_and(section, inverse, missing);
    return cv::countNonZero(missing) / static_cast<double>(count);
}

// 对应迁移前 IsGapBrightnessConsistentWithBackground。
// 判断绝对灰度恢复候选是否真的已经退回到局部背景。
// 该检查用于区分两种外观相近的情况：真实断口与仍有墨迹、但局部偏灰的连续细线。
bool background_consistent(const cv::Mat &image, const cv::Mat &pattern, const cv::Mat &gap, double half_width,
                           int ring_width, int brightness_excess)
{
    int radius = std::max(1, static_cast<int>(std::ceil(half_width)));
    int inner_radius = radius + 1, outer_radius = inner_radius + std::max(2, ring_width);
    cv::Mat expanded, sample, inner, outer, inverse, ring, inverse_pattern, background;
    // 亮度取完整横截面；周围环带排除模板应有图案，区分真实空白与偏暗的连续墨迹。
    cv::dilate(gap, expanded, ellipse(radius * 2 + 1));
    cv::bitwise_and(pattern, expanded, sample);
    cv::dilate(gap, inner, ellipse(inner_radius * 2 + 1));
    cv::dilate(gap, outer, ellipse(outer_radius * 2 + 1));
    cv::bitwise_not(inner, inverse);
    cv::bitwise_and(outer, inverse, ring);
    cv::bitwise_not(pattern, inverse_pattern);
    cv::bitwise_and(ring, inverse_pattern, background);
    if (cv::countNonZero(sample) < 3 || cv::countNonZero(background) < 8)
        return false;
    return cv::mean(image, sample)[0] <= cv::mean(image, background)[0] + brightness_excess;
}

// 对应迁移前 CollectMaskPoints。
// 把二值掩膜转换为稀疏点集，供小范围平移覆盖率搜索复用。
std::vector<cv::Point> mask_points(const cv::Mat &mask)
{
    std::vector<cv::Point> result;
    // 显式按行扫描以支持非连续 ROI，并保持原 C# 的行优先候选顺序。
    for (int y = 0; y < mask.rows; ++y)
        for (int x = 0; x < mask.cols; ++x)
            if (mask.ptr<uint8_t>(y)[x])
                result.emplace_back(x, y);
    return result;
}

// 对应迁移前 FindFarthestSkeletonPoint。
// 在八邻域骨架图上执行小规模 Dijkstra，返回起点到最远骨架点的距离。
// 候选骨架通常只有几十个像素，使用清晰的 O(N²) 实现可避免引入复杂堆结构。
double farthest(int start, const std::vector<cv::Point> &points, const cv::Mat &index_map, int &end)
{
    // 与原实现相同的 O(N²) Dijkstra：等距离时保留行优先先到点，保证路径选择可复现。
    std::vector<double> distances(points.size(), infinity);
    std::vector<bool> visited(points.size(), false);
    distances[start] = 0;
    for (size_t iteration = 0; iteration < points.size(); ++iteration)
    {
        int current = -1;
        double best = infinity;
        for (int i = 0; i < static_cast<int>(points.size()); ++i)
            if (!visited[i] && distances[i] < best)
            {
                current = i;
                best = distances[i];
            }
        if (current < 0)
            break;
        visited[current] = true;
        auto point = points[current];
        for (int dy = -1; dy <= 1; ++dy)
            for (int dx = -1; dx <= 1; ++dx)
            {
                if (dx == 0 && dy == 0)
                    continue;
                int x = point.x + dx, y = point.y + dy;
                if (x < 0 || y < 0 || x >= index_map.cols || y >= index_map.rows)
                    continue;
                int neighbor = index_map.at<int>(y, x);
                if (neighbor < 0 || visited[neighbor])
                    continue;
                double candidate = best + (dx == 0 || dy == 0 ? 1. : std::sqrt(2.));
                if (candidate < distances[neighbor])
                    distances[neighbor] = candidate;
            }
    }
    end = start;
    double result = 0;
    for (int i = 0; i < static_cast<int>(points.size()); ++i)
        if (std::isfinite(distances[i]) && distances[i] > result)
        {
            result = distances[i];
            end = i;
        }
    return result;
}

// 对应迁移前 CalculateSkeletonPathLengthPixels。
// 计算骨架主路径长度，而不是把分叉的所有支路长度相加。
// 对连通骨架执行两次最远点搜索：第一次找到远端，第二次得到主路径跨度；
// 水平/垂直相邻按 1 px、对角相邻按 √2 px。这样交叉点或 T 形结构不会虚增断口长度。
double skeleton_length(const cv::Mat &mask)
{
    auto points = mask_points(mask);
    if (points.empty())
        return 0;
    if (points.size() == 1)
        return 1;
    cv::Mat index_map(mask.size(), CV_32S, cv::Scalar(-1));
    for (int i = 0; i < static_cast<int>(points.size()); ++i)
        index_map.at<int>(points[i]) = i;
    int end = 0, ignored = 0;
    farthest(0, points, index_map, end);
    // 两次最远点搜索得到主路径，不把 T 形分支全部累加；补 1 px 恢复可见跨度。
    return farthest(end, points, index_map, ignored) + 1.;
}

// 对应迁移前 CalculateMedianDistance。
// 读取掩膜内距离变换的中位数，用于判断候选是否位于设计细线而非宽实心区域。
double median_distance(const cv::Mat &distance, const cv::Mat &mask)
{
    std::vector<float> selected;
    for (int y = 0; y < mask.rows; ++y)
        for (int x = 0; x < mask.cols; ++x)
            if (mask.ptr<uint8_t>(y)[x])
                selected.push_back(distance.ptr<float>(y)[x]);
    if (selected.empty())
        return infinity;
    std::sort(selected.begin(), selected.end());
    size_t middle = selected.size() / 2;
    // 保留 float 加法后提升 double 的计算顺序，避免临界线宽变化。
    return selected.size() % 2 == 0 ? (selected[middle - 1] + selected[middle]) * .5 : selected[middle];
}

// 对应迁移前 TryBuildEndpointAnchors。
// 从缺口外环的模板骨架中选取分居缺口两侧的两个连通分量，作为“断口前后结构”锚点。
// 两个输出 Mat 由调用方作用域内的 RAII 对象持有。
bool endpoint_anchors(const cv::Mat &skeleton, const cv::Mat &gap, int removal, int reach, cv::Mat &first,
                      cv::Mat &second)
{
    cv::Mat inner, outer, ring, pixels, labels, stats, centroids;
    cv::dilate(gap, inner, ellipse(removal * 2 + 1));
    cv::dilate(gap, outer, ellipse((removal + std::max(2, reach)) * 2 + 1));
    cv::subtract(outer, inner, ring);
    cv::bitwise_and(skeleton, ring, pixels);
    int count = cv::connectedComponentsWithStats(pixels, labels, stats, centroids);
    std::vector<int> candidates;
    for (int label = 1; label < count; ++label)
        if (stats.at<int>(label, 4) >= 2)
            candidates.push_back(label);
    if (candidates.size() < 2)
        return false;
    auto moments = cv::moments(gap, true);
    if (moments.m00 <= 0)
        return false;
    double cx = moments.m10 / moments.m00, cy = moments.m01 / moments.m00, best = infinity;
    int best_first = -1, best_second = -1;
    for (int a : candidates)
    {
        double ax = centroids.at<double>(a, 0) - cx, ay = centroids.at<double>(a, 1) - cy,
               ad = std::sqrt(ax * ax + ay * ay);
        if (ad < 1e-6)
            continue;
        for (int b : candidates)
        {
            if (b <= a)
                continue;
            double bx = centroids.at<double>(b, 0) - cx, by = centroids.at<double>(b, 1) - cy,
                   bd = std::sqrt(bx * bx + by * by);
            if (bd < 1e-6)
                continue;
            double cosine = (ax * bx + ay * by) / (ad * bd);
            if (cosine > -.15)
                continue;
            double score = ad + bd + (cosine + 1.) * reach;
            if (score < best)
            {
                best = score;
                best_first = a;
                best_second = b;
            }
        }
    }
    if (best_first < 0 || best_second < 0)
        return false;
    cv::inRange(labels, cv::Scalar(best_first), cv::Scalar(best_first), first);
    cv::inRange(labels, cv::Scalar(best_second), cv::Scalar(best_second), second);
    return true;
}

// 对应迁移前 CalculateCoverage。
// 计算模板锚点被 CIS 宽容前景覆盖的比例。
double coverage(const cv::Mat &anchor, const cv::Mat &foreground)
{
    int count = cv::countNonZero(anchor);
    if (count <= 0)
        return 0;
    cv::Mat covered;
    cv::bitwise_and(anchor, foreground, covered);
    return cv::countNonZero(covered) / static_cast<double>(count);
}
// 对应迁移前 CalculateShiftedCoverage。
// 计算点集平移 (dx,dy) 后落在 CIS 前景中的比例，越界点按未覆盖处理。
double shifted_coverage(const std::vector<cv::Point> &points, const cv::Mat &foreground, int dx, int dy)
{
    int count = 0;
    for (auto p : points)
    {
        int x = p.x + dx, y = p.y + dy;
        if (x >= 0 && y >= 0 && x < foreground.cols && y < foreground.rows && foreground.ptr<uint8_t>(y)[x])
            ++count;
    }
    return count / static_cast<double>(points.size());
}
// 对应迁移前 IsGapExplainedByMinorAlignmentOffset。
// 在限定搜索半径内寻找一个共同位移，要求缺口区域和两个端点同时被 CIS 前景解释。
// 找到时说明差分主要来自整体错位，应拒绝该断裂候选。
bool explained_by_shift(const cv::Mat &foreground, const cv::Mat &gap, const cv::Mat &first, const cv::Mat &second,
                        int radius, int tangential_limit)
{
    auto gaps = mask_points(gap), a = mask_points(first), b = mask_points(second);
    if (gaps.empty() || a.empty() || b.empty())
        return false;
    auto m1 = cv::moments(first, true), m2 = cv::moments(second, true);
    if (m1.m00 <= 0 || m2.m00 <= 0)
        return false;
    double tx = m2.m10 / m2.m00 - m1.m10 / m1.m00, ty = m2.m01 / m2.m00 - m1.m01 / m1.m00;
    double length = std::sqrt(tx * tx + ty * ty);
    if (length <= 1e-6)
        return false;
    tx /= length;
    ty /= length;
    for (int dy = -radius; dy <= radius; ++dy)
        for (int dx = -radius; dx <= radius; ++dx)
        {
            // 沿线方向严格限幅，不能把断口之后的完整笔画移来“填补”真实缺陷。
            if (std::abs(dx * tx + dy * ty) > std::max(1, tangential_limit))
                continue;
            if (shifted_coverage(gaps, foreground, dx, dy) < .60)
                continue;
            if (shifted_coverage(a, foreground, dx, dy) < .55 || shifted_coverage(b, foreground, dx, dy) < .55)
                continue;
            return true;
        }
    return false;
}

// 对应迁移前 MergeNearbyRects。
// 合并相交或间距小于 margin 的候选框，避免同一物理断口被多个骨架段重复计数。
std::vector<cv::Rect> merge_rects(const std::vector<cv::Rect> &source, int margin)
{
    std::vector<cv::Rect> merged;
    for (auto current : source)
    {
        bool combined;
        do
        {
            combined = false;
            for (int i = static_cast<int>(merged.size()) - 1; i >= 0; --i)
            {
                auto existing = merged[i];
                if (current.x > existing.br().x + margin || current.br().x + margin < existing.x ||
                    current.y > existing.br().y + margin || current.br().y + margin < existing.y)
                    continue;
                current = current | existing;
                merged.erase(merged.begin() + i);
                combined = true;
            }
        } while (combined);
        merged.push_back(current);
    }
    return merged;
}
} // namespace

// 对应迁移前 BuildEdgeExclusionMask。
// 生成原有轮廓屏蔽掩膜。掩膜与差分分开创建，使细线通道能够在清零前读取原始内部差分。
cv::Mat edge_exclusion(const cv::Mat &binary, int outer, int inner)
{
    cv::Mat mask = cv::Mat::zeros(binary.size(), CV_8U);
    if (outer <= 0 && inner <= 0)
        return mask;
    // 外轮廓先填实，避免孔洞混入外包围；内轮廓通道直接使用含孔洞的 Alpha。
    cv::Mat filled = cv::Mat::zeros(binary.size(), CV_8U);
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(binary.clone(), contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
    cv::drawContours(filled, contours, -1, cv::Scalar(255), -1);
    if (outer > 0)
        cv::bitwise_or(mask, boundary_band(filled, outer), mask);
    if (inner > 0)
        cv::bitwise_or(mask, boundary_band(binary, inner), mask);
    return mask;
}

// 对应迁移前 DetectFineLineBreaksAtDetailScale。
// 在独立细节尺度上检测细线中间断口，所有断口统一执行同一条证据链：
// 1. 建立模板/CIS 前景；2. 提取轮廓屏蔽区内的缺失候选；
// 3. 在候选 ROI 上提取骨架；4. 校验物理长度和线宽；
// 5. 确认笔画横截面被切断；6. 确认缺口前后仍有结构；
// 7. 排除可由同一微小位移解释的配准残差；8. 合并同一物理断口。
// 返回的矩形和掩膜均位于 scale 对应的检测坐标系；最长断口长度和
// 最大模板笔画宽度均使用毫米，并与用户配置的物理参数保持同一尺度。
FineResult fine_line(const cv::Mat &alpha, const cv::Mat &cis, int threshold, double scale,
                     const CisPatchConfig &config)
{
    FineResult result;
    result.mask = cv::Mat::zeros(alpha.size(), CV_8U);
    double pixels_per_millimeter = config.layout_dpi > 0 ? config.layout_dpi / 25.4 * scale : 0;
    if (pixels_per_millimeter <= 0 || config.fine_min_length_mm <= 0 || config.fine_max_width_mm <= 0 ||
        alpha.empty() || cis.empty())
        return result;
    int minimum_gap_pixels =
        std::max(2, static_cast<int>(std::ceil(std::max(.5, config.fine_min_length_mm) * pixels_per_millimeter)));
    double maximum_half_width_pixels = std::max(1., config.fine_max_width_mm * pixels_per_millimeter * .5 + 1.25);
    int contrast_window =
        std::max(3, static_cast<int>(std::ceil(FineLineLocalContrastWindowMm * pixels_per_millimeter)));
    if ((contrast_window & 1) == 0)
        ++contrast_window;
    double minimum_recovery_half_width = std::max(1.5, .30 * pixels_per_millimeter);
    // 局部配准后弯曲细线仍可能有少量法向漂移。法向允许吸收错位，沿线方向严格限制，
    // 防止把断口后方的正常线段移来填补真缺陷。DefectToleranceInner 不参与本通道。
    int tangential_tolerance = std::max(1, round_even(FineLineTangentialAlignmentToleranceMm * pixels_per_millimeter));
    int normal_tolerance =
        std::max(tangential_tolerance, round_even(FineLineNormalAlignmentToleranceMm * pixels_per_millimeter));
    int endpoint_radius =
        std::max(normal_tolerance + 1, std::max(2, round_even(FineLineEndpointSearchRadiusMm * pixels_per_millimeter)));
    int anchor_reach =
        std::max(minimum_gap_pixels * 2, round_even(FineLineEndpointAnchorLengthMm * pixels_per_millimeter));
    int relaxed_threshold = std::max(8, std::min(threshold - 1, round_even(threshold * .82)));
    // 绝对灰度恢复只针对“局部对比度把真实空档补亮”。真断口应接近周围背景；
    // 若仍明显亮于背景，更可能是偏灰但连续的墨迹，不能仅凭绝对二值化就判断裂。
    int brightness_excess = std::max(4, contrast_threshold(relaxed_threshold) / 2);
    int ring_width = std::max(3, static_cast<int>(std::ceil(.80 * pixels_per_millimeter)));

    // 1. Alpha 为应有结构；CIS 宽松前景由绝对亮度与局部对比度联合建立。
    cv::Mat template_binary, captured, absolute_foreground, near_endpoints, inverse, raw, missing_candidates, labels,
        stats, centroids;
    cv::threshold(alpha, template_binary, config.alpha_threshold, 255, cv::THRESH_BINARY);
    if (cis.size() == alpha.size())
        cis.copyTo(captured);
    else
        cv::resize(cis, captured, alpha.size(), 0, 0, cv::INTER_LINEAR);
    auto captured_foreground = foreground_evidence(captured, relaxed_threshold, contrast_window);
    cv::threshold(captured, absolute_foreground, relaxed_threshold, 255, cv::THRESH_BINARY);
    auto exclusion = edge_exclusion(template_binary, length_pixels(config.exclusion_outer_mm, config.layout_dpi, scale),
                                    length_pixels(config.exclusion_inner_mm, config.layout_dpi, scale));
    cv::dilate(captured_foreground, near_endpoints, ellipse(endpoint_radius * 2 + 1));
    // 2. 唯一候选源：设计应有、实拍宽松前景不存在、并且落在普通通道屏蔽区域。
    cv::bitwise_not(captured_foreground, inverse);
    cv::bitwise_and(template_binary, inverse, raw);
    cv::bitwise_and(raw, exclusion, missing_candidates);
    if (cv::countNonZero(missing_candidates) == 0)
        return result;
    int count = cv::connectedComponentsWithStats(missing_candidates, labels, stats, centroids);
    std::vector<cv::Rect> expanded_boxes, tight_boxes;
    for (int label = 1; label < count; ++label)
    {
        cv::Rect bounds(stats.at<int>(label, 0), stats.at<int>(label, 1), stats.at<int>(label, 2),
                        stats.at<int>(label, 3));
        if (std::sqrt(static_cast<double>(bounds.width * bounds.width + bounds.height * bounds.height)) <
            minimum_gap_pixels)
            continue;
        int border = endpoint_radius + 1;
        // 贴近 ROI 边界时无法证明断口两端仍有线，保留原来的保守拒绝策略。
        // 若以后要覆盖零件裁切边界，应从裁切 padding 与配准取样一起验证，不能只删此门控。
        if (bounds.x <= border || bounds.y <= border || bounds.br().x >= template_binary.cols - border ||
            bounds.br().y >= template_binary.rows - border)
            continue;
        auto roi = expand_rect(bounds, anchor_reach + endpoint_radius, template_binary.size());
        cv::Mat candidate, skeleton, distance, gaps, gap_labels, gap_stats, gap_centroids;
        cv::inRange(labels(roi), cv::Scalar(label), cv::Scalar(label), candidate);
        // 3. 仅候选 ROI 骨架化和距离变换。GUOHALL 与旧 C# 相同，避免整幅预计算增加耗时。
        cv::ximgproc::thinning(template_binary(roi), skeleton, cv::ximgproc::THINNING_GUOHALL);
        cv::distanceTransform(template_binary(roi), distance, cv::DIST_L2, cv::DIST_MASK_3);
        cv::bitwise_and(skeleton, candidate, gaps);
        if (cv::countNonZero(gaps) == 0)
            continue;
        int gap_count = cv::connectedComponentsWithStats(gaps, gap_labels, gap_stats, gap_centroids);
        for (int gap_label = 1; gap_label < gap_count; ++gap_label)
        {
            cv::Rect local(gap_stats.at<int>(gap_label, 0), gap_stats.at<int>(gap_label, 1),
                           gap_stats.at<int>(gap_label, 2), gap_stats.at<int>(gap_label, 3));
            cv::Mat gap, first_anchor, second_anchor;
            cv::inRange(gap_labels, cv::Scalar(gap_label), cv::Scalar(gap_label), gap);
            // 4. 最短断口按骨架主路径；最大线宽按模板骨架处距离场中位数过滤。
            double gap_length = skeleton_length(gap), line_half_width = median_distance(distance, gap);
            // 骨架路径不是外接框对角线；距离场给出模板中心到边缘的半宽。
            // 线宽门槛保留旧版 1.25 像素栅格余量，统计值不扣除它，不能误解为绝对硬上限。
            if (gap_length < minimum_gap_pixels || line_half_width > maximum_half_width_pixels)
                continue;
            // 5. 横截面应缺失至少 75%；光晕恢复还需绝对缺失 90% 且亮度接近背景。
            bool use_absolute_recovery = false;
            if (cross_section_missing(template_binary(roi), captured_foreground(roi), gap, line_half_width) < .75)
            {
                use_absolute_recovery = line_half_width >= minimum_recovery_half_width &&
                                        cross_section_missing(template_binary(roi), absolute_foreground(roi), gap,
                                                              line_half_width) >= .90 &&
                                        background_consistent(captured(roi), template_binary(roi), gap, line_half_width,
                                                              ring_width, brightness_excess);
                if (!use_absolute_recovery)
                    continue;
            }
            // 6. 缺口两侧必须都有真实线段，不能把线端、裁切边界或孤立暗点判为断口。
            // 0.40/0.75/0.90 是既有内部证据门槛，不是新增用户参数：
            // 分别用于两端存在、主要截面切断，以及光晕情况下的绝对缺失复核。
            if (!endpoint_anchors(skeleton, gap, tangential_tolerance + 1, anchor_reach, first_anchor, second_anchor))
                continue;
            if (coverage(first_anchor, near_endpoints(roi)) < .40 || coverage(second_anchor, near_endpoints(roi)) < .40)
                continue;
            // 7. 同一个微小位移若同时解释缺口和两个锚段，则判为配准残差。
            if (explained_by_shift(use_absolute_recovery ? absolute_foreground(roi) : captured_foreground(roi), gap,
                                   first_anchor, second_anchor, normal_tolerance, tangential_tolerance))
                continue;
            // 8. 所有门控通过才提交；保存的断口掩膜仍为骨架像素，沿用现有面积定义。
            auto accepted = result.mask(roi);
            cv::bitwise_or(accepted, gap, accepted);
            cv::Rect global(local.x + roi.x, local.y + roi.y, local.width, local.height);
            tight_boxes.push_back(global);
            expanded_boxes.push_back(expand_rect(global, normal_tolerance + 1, template_binary.size()));
            result.longest = std::max(result.longest, gap_length / pixels_per_millimeter);
            result.widest = std::max(result.widest, line_half_width * 2. / pixels_per_millimeter);
        }
    }
    // 9. 显示框合并后，用同一分组恢复紧致框；面积取最终掩膜并集，避免重复统计。
    result.boxes = merge_rects(expanded_boxes, endpoint_radius);
    for (auto merged : result.boxes)
    {
        bool found = false;
        cv::Rect tight;
        for (size_t i = 0; i < expanded_boxes.size(); ++i)
            if ((expanded_boxes[i] & merged) == expanded_boxes[i])
            {
                tight = found ? (tight | tight_boxes[i]) : tight_boxes[i];
                found = true;
            }
        if (!found)
            tight = merged;
        result.tight.push_back(tight);
        result.areas.push_back(cv::countNonZero(result.mask(tight)));
    }
    return result;
}
} // namespace cis::patch
