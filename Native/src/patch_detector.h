#pragma once
#include "cis_patch_api.h"
#include <algorithm>
#include <array>
#include <chrono>
#include <climits>
#include <cmath>
#include <memory>
#include <mutex>
#include <opencv2/calib3d.hpp>
#include <opencv2/core.hpp>
#include <opencv2/features2d.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/ximgproc.hpp>
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
    // representative 拥有模板副本；发布到缓存后只读。描述子/关键点属于同一份像素，不能单独更新。
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
    // 借用当前 worker 的 SIFT 做首次提取；返回 shared_ptr 保证配准期间缓存项仍存活。
    // 哈希碰撞验证和锁顺序的实现独立放在 patch_template_cache.cpp。
    std::shared_ptr<TemplateFeatures> get(const cv::Mat &, cv::SIFT &, int feature_support = 0);
};
struct Worker
{
    explicit Worker(std::shared_ptr<TemplateCache> value) : cache(std::move(value)) {}
    std::mutex mutex; // 同一 worker 串行，不影响其他 worker 的配准/检测。
    std::shared_ptr<TemplateCache> cache;
    cv::Ptr<cv::SIFT> sift;
    void initialize()
    {
        if (!sift)
        {
            // 不在 SIFT 内部按整图响应强度先截断；统一在模板坐标中做空间均衡筛选。
            // DoG 极值按九区均衡取最多 100 个；仅对不同采样位置不足 8 个的
            // 分区补充转角 SIFT 描述子（最多额外 72 个），不是每区固定加 8 个。
            // 两类采样点一起进入同一套匹配/稳健估计流程。
            sift = cv::SIFT::create(0, 3, .04, 10., 1.6, true);
        }
    }
};
struct Alignment
{
    bool applied = false;
    // 相似/仿射为 2x3，单应为 3x3；均是原始输入像素坐标 CIS -> Alpha。
    // applied=true 包含 Applied 与 AppliedDegraded，均须用此矩阵采样核心图；
    // applied=false 时不得 Warp，调用方不能读取拒绝候选留下的临时矩阵。
    cv::Mat transform;
    std::string diagnostic;
    double milliseconds = 0;
    CisPatchAlignmentInfo info{sizeof(CisPatchAlignmentInfo)};
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
    // C ABI 返回句柄持有本对象。C# 必须在销毁句柄前复制所需字段、数组和图像。
    CisPatchSummary summary{sizeof(CisPatchSummary)};
    std::vector<CisPatchDefect> defects;
    std::array<cv::Mat, 6> images;
    std::string log;
    CisPatchAlignmentInfo alignment{sizeof(CisPatchAlignmentInfo)};
};
// 输入视图只在同步调用期间借用。结果中所有图像均拥有引用计数内存，不引用调用方像素。
// 统一配准入口：原分辨率输入，输出矩阵也在原分辨率 CIS -> Alpha 坐标中。
// 工作区与模板内容绑定；调用方不再负责工作图缩放或猜测矩阵所处的尺度。
// 少量边界截断片段仅在配准副本上排除，完整散点保留；detect 的原始 Alpha 不变。
Alignment register_patch(const cv::Mat &alpha, const cv::Mat &cis, int alpha_threshold, int cis_threshold, double dpi,
                         Worker &worker, cv::Rect core);
void extract_alignment_features(const cv::Mat &image, cv::SIFT &sift, std::vector<cv::KeyPoint> &keypoints,
                                cv::Mat &descriptors, const std::vector<cv::KeyPoint> *reference = nullptr,
                                double radius = 0, int feature_support = 0);
bool unambiguous_period(const cv::Mat &alpha, const cv::Mat &cis, int alpha_threshold, int cis_threshold,
                        const cv::Mat &transform, double scale, Worker &worker, std::string &diagnostic,
                        int feature_support = 0);
FineResult fine_line(const cv::Mat &alpha, const cv::Mat &cis, int threshold, double scale,
                     const CisPatchConfig &config);
PatchResult detect(const cv::Mat &alpha, const cv::Mat &cis, const CisPatchConfig &config, Worker &worker);
} // namespace cis::patch
