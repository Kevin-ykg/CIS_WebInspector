#include "patch_constants.h"
#include "patch_detector.h"
#include <iomanip>
#include <limits>
#include <sstream>

namespace cis::patch
{
namespace
{
struct PeriodVote
{
    cv::Point2d offset;
    std::vector<cv::Point2f> anchors;
};

// 这里只找模板中可能混淆的位置差，不估计 CIS 变换。
// 至少三个不同位置的 SIFT 描述子支持同一位移才作为候选；同一位置的多个主方向只算一次。
std::vector<cv::Point2d> repeat_offsets(const TemplateFeatures &features, const cv::Mat &binary, double scale)
{
    std::vector<PeriodVote> votes;
    const double tolerance = std::max(2., LocalAlignmentPeriodToleranceOriginalPx * scale);
    for (size_t i = 0; i < features.keypoints.size(); ++i)
    {
        for (size_t j = i + 1; j < features.keypoints.size(); ++j)
        {
            const auto a = features.keypoints[i].pt, b = features.keypoints[j].pt;
            cv::Point2d delta(b.x - a.x, b.y - a.y);
            if (cv::norm(delta) < std::max(8., 10. * scale) || std::abs(delta.x) > binary.cols * .65 ||
                std::abs(delta.y) > binary.rows * .65 ||
                cv::norm(features.descriptors.row(static_cast<int>(i)), features.descriptors.row(static_cast<int>(j)),
                         cv::NORM_L2) > LocalAlignmentSpatialMaxDescriptorDistance)
            {
                continue;
            }
            if (delta.y < -tolerance || (std::abs(delta.y) <= tolerance && delta.x < 0))
            {
                delta *= -1.;
            }
            auto found = std::find_if(votes.begin(), votes.end(), [&](const PeriodVote &vote) {
                return cv::norm(vote.offset - delta) <= tolerance;
            });
            if (found == votes.end())
            {
                votes.push_back({delta, {a}});
            }
            else if (std::none_of(found->anchors.begin(), found->anchors.end(),
                                  [&](cv::Point2f p) { return cv::norm(p - a) < tolerance; }))
            {
                found->offset = (found->offset * static_cast<double>(found->anchors.size()) + delta) /
                                static_cast<double>(found->anchors.size() + 1);
                found->anchors.push_back(a);
            }
        }
    }
    std::stable_sort(votes.begin(), votes.end(),
                     [](const PeriodVote &a, const PeriodVote &b) { return a.anchors.size() > b.anchors.size(); });
    std::vector<cv::Point2d> offsets;
    for (const auto &vote : votes)
    {
        if (vote.anchors.size() < 3 || offsets.size() >= LocalAlignmentMaximumPeriodAliases)
        {
            break;
        }
        int dx = round_even(vote.offset.x), dy = round_even(vote.offset.y);
        const cv::Rect bounds(0, 0, binary.cols, binary.rows);
        const cv::Rect overlap = bounds & cv::Rect(dx, dy, binary.cols, binary.rows);
        if (overlap.empty())
        {
            continue;
        }
        cv::Mat intersection;
        auto first = binary(overlap),
             second = binary(cv::Rect(overlap.x - dx, overlap.y - dy, overlap.width, overlap.height));
        double mass = cv::countNonZero(first) + cv::countNonZero(second);
        cv::bitwise_and(first, second, intersection);
        // 描述子相似不等于整段图案真的重复；重叠区的大部分前景还必须相同。
        if (mass > 0 && 2. * cv::countNonZero(intersection) / mass >= LocalAlignmentPeriodSelfOverlap)
        {
            offsets.push_back(vote.offset);
        }
    }
    return offsets;
}
} // namespace

bool unambiguous_period(const cv::Mat &alpha, const cv::Mat &cis, int at, int ct, const cv::Mat &transform,
                        double scale, Worker &worker, std::string &diagnostic, int feature_support)
{
    worker.initialize();
    auto features = worker.cache->get(alpha, *worker.sift, feature_support);
    cv::Mat reference, moving;
    cv::threshold(alpha, reference, at, 255, cv::THRESH_BINARY);
    cv::threshold(cis, moving, ct, 255, cv::THRESH_BINARY);
    auto offsets = repeat_offsets(*features, reference, scale);
    if (offsets.empty())
    {
        return true;
    }

    double area_scale = std::abs(transform.at<double>(0, 0) * transform.at<double>(1, 1) -
                                 transform.at<double>(0, 1) * transform.at<double>(1, 0));
    if (transform.rows == 3)
    {
        // 单应性的面积倍率随位置变化；几何门控已限制其缓变，中心雅可比
        // 可为固定分母提供保守的一阶近似，不能把左上角 2x2 当成全件倍率。
        const double x = moving.cols / 2., y = moving.rows / 2.;
        const double denominator =
            transform.at<double>(2, 0) * x + transform.at<double>(2, 1) * y + transform.at<double>(2, 2);
        area_scale = std::abs(cv::determinant(transform) / (denominator * denominator * denominator));
    }
    const double mass = cv::countNonZero(reference) + cv::countNonZero(moving) * area_scale;
    if (mass == 0)
    {
        return true;
    }
    const int tolerance = std::max(1, round_even(LocalAlignmentPeriodToleranceOriginalPx * scale));
    const auto kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, {2 * tolerance + 1, 2 * tolerance + 1});
    cv::Mat reference_neighborhood;
    cv::dilate(reference, reference_neighborhood, kernel);
    auto loss = [&](cv::Point2d offset) {
        cv::Mat hypothesis = transform.clone();
        if (transform.rows == 3)
        {
            // 在输出坐标平移一个周期：T(offset)*H，而不是只改 H 的平移列。
            for (int column = 0; column < 3; ++column)
            {
                hypothesis.at<double>(0, column) += offset.x * transform.at<double>(2, column);
                hypothesis.at<double>(1, column) += offset.y * transform.at<double>(2, column);
            }
        }
        else
        {
            hypothesis.at<double>(0, 2) += offset.x;
            hypothesis.at<double>(1, 2) += offset.y;
        }
        cv::Mat warped, neighborhood, covered_reference, covered_moving;
        if (transform.rows == 3)
        {
            cv::warpPerspective(moving, warped, hypothesis, reference.size(), cv::INTER_NEAREST);
        }
        else
        {
            cv::warpAffine(moving, warped, hypothesis, reference.size(), cv::INTER_NEAREST);
        }
        cv::dilate(warped, neighborhood, kernel);
        cv::bitwise_and(reference, neighborhood, covered_reference);
        cv::bitwise_and(warped, reference_neighborhood, covered_moving);
        // 分母固定为原始前景总数。错周期把一整列图案移出画布时仍要付出代价，
        // 不能只在剩余重叠区域评分，否则“裁掉坏点”会获得虚假的高分。
        return std::max(0., 1. - (cv::countNonZero(covered_reference) + cv::countNonZero(covered_moving)) / mass);
    };
    const double selected = loss({0, 0});
    double alternative = std::numeric_limits<double>::infinity();
    cv::Point2d competitor;
    for (auto offset : offsets)
    {
        for (double sign : {-1., 1.})
        {
            double value = loss(offset * sign);
            if (value < alternative)
            {
                alternative = value;
                competitor = offset * sign;
            }
        }
    }
    // 最佳周期和另一个周期的证据接近时，应判“不确定”，而不是按距离更近强行猜选。
    // 此条件仅拒绝候选；越过全局先验的竞争位置绝不直接用于 Warp。
    // 空白/错误输入中“两个都极差”不是两个合理周期，不覆盖上游更具体的失败原因。
    bool enough_evidence = std::min(selected, alternative) < .5;
    bool unique = !enough_evidence || alternative > selected + std::max(LocalAlignmentPeriodAbsoluteGap,
                                                                        selected * LocalAlignmentPeriodRelativeGap);
    std::ostringstream out;
    out << std::fixed << std::setprecision(4) << "periodLoss=" << selected << "/" << alternative
        << ", alternativeShift=(" << competitor.x / scale << "," << competitor.y / scale
        << ")px, aliases=" << offsets.size() << (enough_evidence ? "" : ", evidence=insufficient");
    diagnostic = out.str();
    return unique;
}
} // namespace cis::patch
