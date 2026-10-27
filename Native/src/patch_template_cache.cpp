#include "patch_constants.h"
#include "patch_detector.h"

// 模板缓存仅复用像素完全一致的模板特征；哈希命中不能直接当作模板相同。
// 保留原有目录锁/分桶锁顺序和统计时点，当前配准求解见 patch_alignment.cpp。
namespace cis::patch
{
namespace
{
// 用真实工作区四边的灰度中位数表示背景，不用固定 0 覆盖不同曝光的 CIS 背景。
// 这是滤波的边界条件，不是观测到的图案；关键点中心仍必须落在真实像素内。
uint8_t border_background(const cv::Mat &image)
{
    std::vector<uint8_t> samples;
    samples.reserve(2 * static_cast<size_t>(image.rows + image.cols));
    for (int y = 0; y < image.rows; ++y)
    {
        samples.push_back(image.at<uint8_t>(y, 0));
        samples.push_back(image.at<uint8_t>(y, image.cols - 1));
    }
    for (int x = 0; x < image.cols; ++x)
    {
        samples.push_back(image.at<uint8_t>(0, x));
        samples.push_back(image.at<uint8_t>(image.rows - 1, x));
    }
    auto middle = samples.begin() + samples.size() / 2;
    std::nth_element(samples.begin(), middle, samples.end());
    return *middle;
}
} // namespace

// 先检出候选，再按模板坐标的 3x3 区域轮流分配预算，最后才计算描述子。
// 比“整图最强 100 个”更不容易被一个高对比重复单元占满；相同工作图的结果仍确定。
void extract_alignment_features(const cv::Mat &image, cv::SIFT &sift, std::vector<cv::KeyPoint> &keypoints,
                                cv::Mat &descriptors, const std::vector<cv::KeyPoint> *reference, double radius,
                                int feature_support)
{
    // 工作区靠近圆角/端点时，默认边界反射会把末端结构映射回金字塔，改变稀疏极值。
    // 对稀疏模板只延拓滤波缓冲；随后把坐标移回真实工作区。Alpha/CIS 使用相同尺寸支持。
    cv::Mat feature_image = image, valid_centers;
    if (feature_support > 0)
    {
        cv::copyMakeBorder(image, feature_image, feature_support, feature_support, feature_support, feature_support,
                           cv::BORDER_CONSTANT, border_background(image));
        valid_centers = cv::Mat::zeros(feature_image.size(), CV_8U);
        valid_centers(cv::Rect(feature_support, feature_support, image.cols, image.rows)).setTo(255);
    }
    std::vector<cv::KeyPoint> candidates;
    sift.detect(feature_image, candidates, valid_centers);
    std::array<std::vector<cv::KeyPoint>, 9> cells;
    for (auto point : candidates)
    {
        point.pt -= cv::Point2f(static_cast<float>(feature_support), static_cast<float>(feature_support));
        if (point.pt.x < 0 || point.pt.y < 0 || point.pt.x >= image.cols || point.pt.y >= image.rows)
        {
            continue;
        }
        if (reference)
        {
            // 全局位置先验在特征预算分配之前生效，远离模板结构的噪声不挤掉真实角点。
            bool near = std::any_of(reference->begin(), reference->end(), [&](const cv::KeyPoint &anchor) {
                return std::abs(point.pt.x - anchor.pt.x) <= radius && std::abs(point.pt.y - anchor.pt.y) <= radius;
            });
            if (!near)
            {
                continue;
            }
        }
        const int x = std::clamp(static_cast<int>(point.pt.x * 3 / image.cols), 0, 2);
        const int y = std::clamp(static_cast<int>(point.pt.y * 3 / image.rows), 0, 2);
        cells[y * 3 + x].push_back(point);
    }
    for (auto &cell : cells)
    {
        std::stable_sort(cell.begin(), cell.end(), [](const cv::KeyPoint &a, const cv::KeyPoint &b) {
            if (a.response != b.response)
            {
                return a.response > b.response;
            }
            if (a.pt.y != b.pt.y)
            {
                return a.pt.y < b.pt.y;
            }
            if (a.pt.x != b.pt.x)
            {
                return a.pt.x < b.pt.x;
            }
            if (a.size != b.size)
            {
                return a.size < b.size;
            }
            return a.angle < b.angle;
        });
    }
    keypoints.clear();
    for (size_t rank = 0; keypoints.size() < LocalAlignmentFeatureBudget; ++rank)
    {
        bool added = false;
        for (const auto &cell : cells)
        {
            if (rank < cell.size() && keypoints.size() < LocalAlignmentFeatureBudget)
            {
                keypoints.push_back(cell[rank]);
                added = true;
            }
        }
        if (!added)
        {
            break;
        }
    }
    // 按不同几何位置统计每区已有证据：同一角点的多个方向不重复占位。
    // 只为覆盖不足的分区补足采样点，不给已有丰富特征的区域一律加点。
    std::array<std::vector<cv::Point2f>, 9> sample_positions;
    for (const auto &point : keypoints)
    {
        const int cell = std::clamp(static_cast<int>(point.pt.y * 3 / image.rows), 0, 2) * 3 +
                         std::clamp(static_cast<int>(point.pt.x * 3 / image.cols), 0, 2);
        auto &positions = sample_positions[cell];
        if (std::none_of(positions.begin(), positions.end(),
                         [&](cv::Point2f existing) { return cv::norm(existing - point.pt) < 2.; }))
        {
            positions.push_back(point.pt);
        }
    }
    // 二值轮廓中的长直边/圆角常只有少量 DoG 极值；保留原有 SIFT
    // 预算，再将九区的不同采样位置补足至 8 个（有真实角点才补）。
    // 轮廓转角仍计算 SIFT 描述子、通过原有空间
    // 匹配和 RANSAC，不从轮廓直接生成或精修变换矩阵。
    // 局部旋转已被全局对准约束为小角度，补充点使用统一图像轴方向。
    std::vector<cv::Point2f> corners;
    std::vector<float> corner_quality;
    cv::goodFeaturesToTrack(image, corners, 288, .02, 4., cv::noArray(), corner_quality, 5, 3);
    std::vector<size_t> order;
    for (size_t i = 0; i < corners.size(); ++i)
    {
        order.push_back(i);
    }
    // 复用提取时的质量分数，不再重复计算整幅特征值图；同分按坐标排序。
    std::stable_sort(order.begin(), order.end(), [&](size_t a, size_t b) {
        return corner_quality[a] != corner_quality[b] ? corner_quality[a] > corner_quality[b]
               : corners[a].y != corners[b].y         ? corners[a].y < corners[b].y
                                                      : corners[a].x < corners[b].x;
    });
    for (auto index : order)
    {
        const auto point = corners[index];
        const int cell = std::clamp(static_cast<int>(point.y * 3 / image.rows), 0, 2) * 3 +
                         std::clamp(static_cast<int>(point.x * 3 / image.cols), 0, 2);
        if (sample_positions[cell].size() >= 8 ||
            std::any_of(keypoints.begin(), keypoints.end(),
                        [&](const cv::KeyPoint &existing) { return cv::norm(existing.pt - point) < 2.; }))
        {
            continue;
        }
        if (reference && std::none_of(reference->begin(), reference->end(), [&](const cv::KeyPoint &anchor) {
                return std::abs(point.x - anchor.pt.x) <= radius && std::abs(point.y - anchor.pt.y) <= radius;
            }))
        {
            continue;
        }
        keypoints.emplace_back(point, 12.f, 0.f);
        sample_positions[cell].push_back(point);
    }
    if (!keypoints.empty())
    {
        // 描述子从有滤波支持的图取样，发布给匹配器的点却始终是原工作区坐标。
        auto sampling_points = keypoints;
        for (auto &point : sampling_points)
        {
            point.pt += cv::Point2f(static_cast<float>(feature_support), static_cast<float>(feature_support));
        }
        sift.compute(feature_image, sampling_points, descriptors);
        keypoints = std::move(sampling_points);
        for (auto &point : keypoints)
        {
            point.pt -= cv::Point2f(static_cast<float>(feature_support), static_cast<float>(feature_support));
        }
    }
}

std::shared_ptr<TemplateFeatures> TemplateCache::get(const cv::Mat &image, cv::SIFT &sift, int feature_support)
{
    auto start = Clock::now();
    uint64_t hash = 1469598103934665603ULL;
    // 16x16 规则网格 FNV 签名仅用于分桶；相同签名必须再经过 L1==0 精确像素比较。
    for (int gy = 0; gy < 16; ++gy)
    {
        for (int gx = 0; gx < 16; ++gx)
        {
            int y = std::min(image.rows - 1, static_cast<int>((2LL * gy + 1) * image.rows / 32));
            int x = std::min(image.cols - 1, static_cast<int>((2LL * gx + 1) * image.cols / 32));
            for (size_t b = 0; b < image.elemSize(); ++b)
            {
                hash ^= image.ptr<uint8_t>(y)[x * image.elemSize() + b];
                hash *= 1099511628211ULL;
            }
        }
    }
    std::string key = std::to_string(image.rows) + "x" + std::to_string(image.cols) + ":" +
                      std::to_string(image.type()) + ":support=" + std::to_string(feature_support) + ":" +
                      std::to_string(hash);
    double key_ms = elapsed(start);
    std::shared_ptr<TemplateBucket> bucket;
    {
        std::lock_guard<std::mutex> lock(mutex);
        stats.quick_key_ms += key_ms;
        auto &entry = buckets[key];
        if (!entry)
        {
            entry = std::make_shared<TemplateBucket>();
        }
        bucket = entry;
    }
    // 全局锁只维护目录/统计，同一哈希桶单独加锁以避免重复创建模板。
    // 不同模板的 SIFT 可并行；锁顺序不能改成持有全局锁等待桶锁，否则会与统计路径互锁。
    std::lock_guard<std::mutex> bucket_lock(bucket->mutex);
    for (const auto &entry : bucket->entries)
    {
        auto compare_start = Clock::now();
        bool equal = image.size() == entry->representative.size() && image.type() == entry->representative.type() &&
                     cv::norm(image, entry->representative, cv::NORM_L1) == 0;
        double compare_ms = elapsed(compare_start);
        {
            std::lock_guard<std::mutex> lock(mutex);
            ++stats.comparisons;
            stats.comparison_ms += compare_ms;
            if (equal)
            {
                ++stats.hits;
            }
        }
        if (equal)
        {
            return entry;
        }
    }
    auto entry = std::make_shared<TemplateFeatures>();
    entry->representative = image.clone();
    // 边界策略也是特征签名的一部分，同一像素不能误用另一种滤波支持的描述子。
    extract_alignment_features(image, sift, entry->keypoints, entry->descriptors, nullptr, 0, feature_support);
    bucket->entries.push_back(entry);
    {
        std::lock_guard<std::mutex> lock(mutex);
        ++stats.entries;
        ++stats.misses;
    }
    return entry;
}
} // namespace cis::patch
