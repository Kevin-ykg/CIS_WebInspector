#pragma once
#include "cis_patch_api.h"
#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/features2d.hpp>
#include <opencv2/calib3d.hpp>
#include <opencv2/ximgproc.hpp>
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <climits>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace cis::patch
{
using Clock = std::chrono::steady_clock;
inline double elapsed(Clock::time_point start)
{
    return std::chrono::duration<double, std::milli>(Clock::now() - start).count();
}
// 保留 C# Math.Round 默认的 midpoint-to-even，不能统一换成 std::round 的远离零取整。
inline int round_even(double value)
{
    return static_cast<int>(std::nearbyint(value));
}
inline cv::Mat ellipse(int size)
{
    return cv::getStructuringElement(cv::MORPH_ELLIPSE, {size, size});
}
cv::Mat gray(const cv::Mat &image);
double pixels_per_mm(double dpi);
int length_pixels(double mm, double dpi, double scale);
int area_pixels(double mm2, double dpi, double scale);
cv::Rect expand_rect(cv::Rect rect, int margin, cv::Size bounds);
cv::Rect scale_rect(cv::Rect rect, double sx, double sy, cv::Size bounds);
cv::Mat edge_exclusion(const cv::Mat &binary, int outer, int inner);

struct TemplateFeatures
{
    cv::Mat representative, descriptors;
    std::vector<cv::KeyPoint> keypoints;
};
struct TemplateBucket
{
    std::mutex mutex;
    std::vector<std::shared_ptr<TemplateFeatures>> entries;
};
// 快速哈希只分桶，逐像素相等才复用特征；批次结束后所有 Mat 随 shared_ptr 自动回收。
struct TemplateCache
{
    std::mutex mutex;
    std::unordered_map<std::string, std::shared_ptr<TemplateBucket>> buckets;
    CisPatchCacheStats stats{sizeof(CisPatchCacheStats)};
    std::shared_ptr<TemplateFeatures> get(const cv::Mat &, cv::SIFT &);
};
struct Worker
{
    explicit Worker(std::shared_ptr<TemplateCache> value) : cache(std::move(value)) {}
    std::mutex mutex; // 同一 worker 串行，不影响其他 worker 的配准/检测。
    std::shared_ptr<TemplateCache> cache;
    cv::Ptr<cv::SIFT> sift;
    cv::Ptr<cv::BFMatcher> matcher;
    void initialize()
    {
        if (!sift)
        {
            sift = cv::SIFT::create(100);
            matcher = cv::BFMatcher::create(cv::NORM_L2);
        }
    }
};
struct Alignment
{
    bool applied = false;
    cv::Mat transform; // 2x3/CV_64F，工作图像坐标，CIS -> Alpha；applied=false 时不得 Warp。
    std::string diagnostic;
    double milliseconds = 0;
};
struct FineResult
{
    cv::Mat mask;                       // 细节尺度下已接受的断口骨架像素并集。
    std::vector<cv::Rect> boxes, tight; // 同一分组：展示框有外扩边距，tight 才是测量框。
    std::vector<int> areas;             // 每个 tight 内 mask 的非零像素数，不是面积填充后的图案缺失区域。
    double longest = 0, widest = 0;     // 毫米：骨架路径长度、距离场中位半宽×2。
};
struct PatchResult
{
    CisPatchSummary summary{sizeof(CisPatchSummary)};
    std::vector<CisPatchDefect> defects;
    std::array<cv::Mat, 6> images;
    std::string log;
};
// 输入视图只在同步调用期间借用。结果中所有图像均拥有引用计数内存，不引用调用方像素。
Alignment local_align(const cv::Mat &alpha, const cv::Mat &cis, int alpha_threshold, int cis_threshold, double scale,
                      Worker &worker);
FineResult fine_line(const cv::Mat &alpha, const cv::Mat &cis, int threshold, double scale,
                     const CisPatchConfig &config);
PatchResult detect(const cv::Mat &alpha, const cv::Mat &cis, const CisPatchConfig &config, Worker &worker);
} // namespace cis::patch
