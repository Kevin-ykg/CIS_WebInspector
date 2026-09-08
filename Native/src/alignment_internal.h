#pragma once
#include "cis_alignment_api.h"
#include <opencv2/opencv.hpp>
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <iomanip>
#include <limits>
#include <numeric>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

// 由原 ImageAligner 各 partial 文件逐段迁移。仅改变语言/所有权边界，不调整门限或模型。
namespace alignment
{
using Config = CisAlignmentConfig;
using Anchor = CisAlignmentAnchor;
using Clock = std::chrono::steady_clock;
// 沿用原 ImageAligner 的工程门槛。迁移时保留名称/数值，方便维护者与历史日志对照。
constexpr int MinimumPointsPerRow = 2;
constexpr int MinimumGlobalCorrespondenceCount = 6;
constexpr double MaximumRowScaleDifference = 0.15;
constexpr double MaximumHorizontalScaleDeviationFromQr = 0.45;
constexpr double RowLineInlierDiameterRatio = 0.40;
constexpr double RowMatchGateDiameterRatio = 0.65;
constexpr double MinimumStrongRowCoverage = 0.50;
constexpr double MinimumAverageRowCoverage = 0.28;
constexpr double GlobalRansacThresholdMm = 0.75;
constexpr double MaximumMedianReprojectionErrorMm = 0.50;
constexpr double MinimumGlobalInlierRatio = 0.60;
constexpr double MaximumAdjacentJacobianScaleRatio = 2.0;
struct MarkerPoint
{
    double X = 0, Y = 0, Area = 0, Circularity = 0, Width = 0, Height = 0, Score = 0;
};
struct Region
{
    std::string Name;
    double TiffCenterY = 0, CisCenterY = 0, TiffDiameterPixels = 0, CisDiameterPixels = 0, TiffPixelsPerMm = 0,
           CisPixelsPerMm = 0;
};
struct Row
{
    std::vector<MarkerPoint> Points;
    int Threshold = 127;
    cv::Rect SearchRect;
    double Slope = 0, MedianLineResidual = 0, HorizontalCoverage = 0, EndToEndYDrift = 0;
};
struct Match
{
    std::vector<MarkerPoint> TiffPoints, CisPoints;
    std::vector<int> TemplateIndices;
    double Scale = NAN, Offset = NAN, MedianResidual = NAN, Coverage = 0;
};
struct SideDetection
{
    MarkerPoint Point;
    cv::Rect SearchRect;
    bool Found = false, UsedExpandedWindow = false, UsedHomographyFallback = false;
};
struct White
{
    int Status = 0; // 与 WhiteInkInspectionStatus 保持一致，0 关闭、7 无法评估。
    double InkLevelPercent = 0, MarkMean = 0, MarkVariance = 0, BackgroundMean = 0, Contrast = 0;
    bool HasStreaking = false;
    cv::Rect SearchRegion;
    std::vector<CisWhiteInkSample> Samples;
    std::string Diagnostic = "白墨出墨检测已关闭。";
};
struct Result
{
    cv::Mat H, Inverse; // RAII 持有，不与 C#/OpenCvSharp 共享 Mat 头或分配器。
    int Mode = 0, Quality = 0, Threshold = 127, StripeRows = 256;
    std::array<double, 3> GridX{};
    std::vector<double> GridY;
    std::vector<std::array<cv::Point2d, 3>> Residuals;
    std::vector<CisAlignmentControl> Controls;
    std::vector<CisAlignmentMark> Marks;
    White Ink;
    std::string Diagnostic;
    double DetectionMs = 0, MapMs = 0, RemapMs = 0, LooMedian = 0, LooMaximum = 0;
    uint64_t TemporaryBytes = 0;
};
inline double elapsed(Clock::time_point start)
{
    return std::chrono::duration<double, std::milli>(Clock::now() - start).count();
}
inline int round_even(double x)
{
    return static_cast<int>(std::nearbyint(x));
} // 对应 Math.Round 默认银行家舍入。
inline double median(std::vector<double> v)
{
    if (v.empty())
        return 0;
    std::stable_sort(v.begin(), v.end());
    size_t m = v.size() / 2;
    return v.size() % 2 ? v[m] : (v[m - 1] + v[m]) * .5;
}
template <class F> double median_of(const std::vector<MarkerPoint> &v, F f)
{
    std::vector<double> a;
    for (auto &p : v)
        a.push_back(f(p));
    return median(a);
}
inline bool finite(cv::Point2d p)
{
    return std::isfinite(p.x) && std::isfinite(p.y);
}
inline cv::Point2d lerp(cv::Point2d a, cv::Point2d b, double t)
{
    return {a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t};
}
inline cv::Point2d project(const cv::Mat &h, cv::Point2d p)
{
    double d = h.at<double>(2, 0) * p.x + h.at<double>(2, 1) * p.y + h.at<double>(2, 2);
    if (std::abs(d) < 1e-12)
        return {NAN, NAN};
    return {(h.at<double>(0, 0) * p.x + h.at<double>(0, 1) * p.y + h.at<double>(0, 2)) / d,
            (h.at<double>(1, 0) * p.x + h.at<double>(1, 1) * p.y + h.at<double>(1, 2)) / d};
}
inline std::string rect_text(cv::Rect r)
{
    return cv::format("(%d,%d,%d,%d)", r.x, r.y, r.width, r.height);
}
inline std::string point_text(cv::Point2d p)
{
    return cv::format("(%.1f,%.1f)", p.x, p.y);
}
inline void sort_x(std::vector<MarkerPoint> &p)
{
    std::stable_sort(p.begin(), p.end(), [](auto &a, auto &b) { return a.X < b.X; });
}
cv::Mat gray(const cv::Mat &source);
cv::Rect search_rect(cv::Size size, double center, double diameter, double margin, double ppm);
Row detect_tiff_row(const cv::Mat &, const Region &, const Config &);
Row detect_cis_row(const cv::Mat &, const Region &, const Config &, double reference_area);
void update_geometry(Row &);
Match match_rows(const Row &, const Row &, double expected_scale);
SideDetection detect_side(const cv::Mat &, cv::Point2d, double, double, double, double, double, double, double);
White inspect_white(const cv::Mat &, const Region &, const Row &, const std::vector<MarkerPoint> &, const Config &);
White inspect_bottom(const cv::Mat &, const Anchor &, const Config &);
std::string white_diagnostic(const White &);
void compute(const cv::Mat &, const cv::Mat &, const Anchor &, const Config &, Result &);
bool build_side_grid(const cv::Mat &, const cv::Mat &, const Anchor &, const Config &, Result &, std::string &);
void warp(const cv::Mat &, cv::Mat &, Result &);
} // namespace alignment
