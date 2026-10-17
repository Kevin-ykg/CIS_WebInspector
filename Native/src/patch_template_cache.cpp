#include "patch_detector.h"

// 模板缓存仅复用像素完全一致的模板特征；哈希命中不能直接当作模板相同。
// 保留原有目录锁/分桶锁顺序和统计时点，配准求解见 patch_alignment.cpp。
namespace cis::patch
{
std::shared_ptr<TemplateFeatures> TemplateCache::get(const cv::Mat &image, cv::SIFT &sift)
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
                      std::to_string(image.type()) + ":" + std::to_string(hash);
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
    sift.detectAndCompute(image, cv::noArray(), entry->keypoints, entry->descriptors);
    bucket->entries.push_back(entry);
    {
        std::lock_guard<std::mutex> lock(mutex);
        ++stats.entries;
        ++stats.misses;
    }
    return entry;
}
} // namespace cis::patch
