// 零件局部配准的正式实现：匹配、模型估计、质量门控和结果状态集中在此文件。
#include "patch_constants.h"
#include "patch_detector.h"
#include "patch_region.h"
#include <iomanip>
#include <limits>
#include <set>
#include <sstream>

namespace cis::patch
{
namespace
{
constexpr double inf = std::numeric_limits<double>::infinity();
// 确定性求解的临界区只覆盖 RNG 状态与稳健估计；图像准备、匹配、评分仍可并行。
std::mutex ransac_mutex;

std::string number(double value, int digits = 2)
{
    std::ostringstream out;
    out << std::fixed << std::setprecision(digits) << value;
    return out.str();
}

struct EdgeScore
{
    double mean = inf;
    std::array<double, 9> cells{};
    double worst = inf;
    int occupied = 0; // TIFF 模板有边缘的固定分区数，变换前后不能换分母。
};
struct EdgeReference
{
    cv::Mat edges;
    cv::Mat distance;
};

EdgeReference prepare_edges(const cv::Mat &reference)
{
    EdgeReference prepared;
    cv::Canny(reference, prepared.edges, 80, 160);
    cv::Mat inverse;
    cv::bitwise_not(prepared.edges, inverse);
    cv::distanceTransform(inverse, prepared.distance, cv::DIST_L2, cv::DIST_MASK_3);
    return prepared;
}

// 只用于验证矩阵，不搜索轮廓平移。固定模板九区并双向统计，避免 CIS 中多出的
// 错周期结构或整块缺失被“模板到 CIS”的单向分数掩盖。
EdgeScore edge_score(const EdgeReference &reference, const cv::Mat &moving, const cv::Mat &matrix)
{
    cv::Mat warped, mov_edges, inverse, to_moving;
    if (matrix.rows == 3)
    {
        cv::warpPerspective(moving, warped, matrix, reference.edges.size(), cv::INTER_NEAREST);
    }
    else
    {
        cv::warpAffine(moving, warped, matrix, reference.edges.size(), cv::INTER_NEAREST);
    }
    cv::Canny(warped, mov_edges, 80, 160);
    EdgeScore result;
    result.cells.fill(inf);
    if (!cv::countNonZero(reference.edges) || !cv::countNonZero(mov_edges))
    {
        return result;
    }
    cv::bitwise_not(mov_edges, inverse);
    cv::distanceTransform(inverse, to_moving, cv::DIST_L2, cv::DIST_MASK_3);
    double total = 0;
    result.worst = 0;
    for (int gy = 0; gy < 3; ++gy)
    {
        for (int gx = 0; gx < 3; ++gx)
        {
            const cv::Rect cell(gx * reference.edges.cols / 3, gy * reference.edges.rows / 3,
                                (gx + 1) * reference.edges.cols / 3 - gx * reference.edges.cols / 3,
                                (gy + 1) * reference.edges.rows / 3 - gy * reference.edges.rows / 3);
            int nr = 0, nm = 0;
            double sr = 0, sm = 0;
            for (int y = cell.y; y < cell.br().y; ++y)
            {
                const auto *r = reference.edges.ptr<uint8_t>(y), *m = mov_edges.ptr<uint8_t>(y);
                const auto *dr = reference.distance.ptr<float>(y), *dm = to_moving.ptr<float>(y);
                for (int x = cell.x; x < cell.br().x; ++x)
                {
                    if (r[x])
                    {
                        sr += std::min(8., static_cast<double>(dm[x]));
                        ++nr;
                    }
                    if (m[x])
                    {
                        sm += std::min(8., static_cast<double>(dr[x]));
                        ++nm;
                    }
                }
            }
            // 评分集合由 TIFF 模板确定。CIS 独有的分区另加罚分，但不改变
            // 平均值的分母；否则矩阵把坏区移出画面后，九区均值会虚假下降。
            if (nr < 25)
            {
                if (nm >= 25)
                {
                    result.cells[gy * 3 + gx] = 8.;
                    total += 8.;
                    result.worst = std::max(result.worst, 8.);
                }
                continue;
            }
            const double value = nm >= 25 ? .5 * (sr / nr + sm / nm) : 8.;
            result.cells[gy * 3 + gx] = value;
            total += value;
            result.worst = std::max(result.worst, value);
            ++result.occupied;
        }
    }
    result.mean = result.occupied ? total / result.occupied : inf;
    return result;
}

// 工作像素中心 u=s*(x-origin)+(s-1)/2；保留仿射 2x3 / 单应 3x3 的矩阵形状。
cv::Mat original_matrix(const cv::Mat &work, cv::Rect region, double sx, double sy)
{
    const cv::Mat w = (cv::Mat_<double>(3, 3) << sx, 0, -sx * region.x + (sx - 1.) / 2., 0, sy,
                       -sy * region.y + (sy - 1.) / 2., 0, 0, 1);
    cv::Mat full = cv::Mat::eye(3, 3, CV_64F);
    if (work.rows == 3)
    {
        work.copyTo(full);
    }
    else
    {
        work.copyTo(full(cv::Rect(0, 0, 3, 2)));
    }
    const cv::Mat converted = w.inv() * full * w;
    if (work.rows == 3)
    {
        return converted / converted.at<double>(2, 2);
    }
    return converted(cv::Rect(0, 0, 3, 2)).clone();
}

cv::Point2d mapped(const cv::Mat &matrix, cv::Point2d point)
{
    const double denominator = matrix.rows == 3 ? matrix.at<double>(2, 0) * point.x +
                                                      matrix.at<double>(2, 1) * point.y + matrix.at<double>(2, 2)
                                                : 1.;
    if (!std::isfinite(denominator) || std::abs(denominator) < 1e-9)
    {
        return {inf, inf};
    }
    return {(matrix.at<double>(0, 0) * point.x + matrix.at<double>(0, 1) * point.y + matrix.at<double>(0, 2)) /
                denominator,
            (matrix.at<double>(1, 0) * point.x + matrix.at<double>(1, 1) * point.y + matrix.at<double>(1, 2)) /
                denominator};
}

// 从工作图还原到输入原图再判断物理形变，不能把工作像素阈值直接用于原图。
// 该安全检查与轮廓质量分级不同：不合法几何不能仅因平均评分下降而被接受。
bool geometry_ok(const cv::Mat &work, cv::Rect region, cv::Rect core, cv::Size source_size, double sx, double sy,
                 bool affine)
{
    const auto matrix = original_matrix(work, region, sx, sy);
    if (!cv::checkRange(matrix))
    {
        return false;
    }
    const double a = matrix.at<double>(0, 0), b = matrix.at<double>(0, 1);
    const double c = matrix.at<double>(1, 0), d = matrix.at<double>(1, 1);
    const double determinant = a * d - b * c;
    if (!std::isfinite(determinant) || determinant <= 0)
    {
        return false;
    }
    cv::Mat singular;
    cv::SVD::compute(matrix(cv::Rect(0, 0, 2, 2)), singular);
    const double maximum = singular.at<double>(0), minimum = singular.at<double>(1);
    if (!std::isfinite(minimum) || !std::isfinite(maximum) || minimum < .97 || maximum > 1.03 ||
        (affine && maximum / minimum > 1.03))
    {
        return false;
    }
    const double rotation = std::atan2(c, a) * 180. / CV_PI;
    const double cosine = (a * b + c * d) / (std::hypot(a, c) * std::hypot(b, d));
    if (!std::isfinite(rotation) || !std::isfinite(cosine) || std::abs(rotation) > 3. ||
        (affine && std::abs(std::asin(std::clamp(cosine, -1., 1.)) * 180. / CV_PI) > LocalAlignmentMaximumShearDegrees))
    {
        return false;
    }
    cv::Mat inverse;
    cv::invertAffineTransform(matrix, inverse);
    for (const auto &corner : {cv::Point2d(core.x, core.y), cv::Point2d(core.br().x - 1., core.y),
                               cv::Point2d(core.x, core.br().y - 1.), cv::Point2d(core.br().x - 1., core.br().y - 1.)})
    {
        const auto destination = mapped(matrix, corner), source = mapped(inverse, corner);
        if (!std::isfinite(destination.x) || !std::isfinite(destination.y) || !std::isfinite(source.x) ||
            !std::isfinite(source.y) || cv::norm(destination - corner) > 150. || source.x < 0 || source.y < 0 ||
            source.x >= source_size.width || source.y >= source_size.height)
        {
            return false;
        }
    }
    return true;
}

// 单应性只作为最后候选；在核心区九个位置检查局部雅可比，不能只凭四角和一个
// 低 RANSAC 残差放行。全局 H0 已去掉主要透视，剩余局部映射必须接近恒等、无折叠。
bool homography_geometry_ok(const cv::Mat &work, cv::Rect region, cv::Rect core, cv::Size source_size, double sx,
                            double sy)
{
    const cv::Mat h = original_matrix(work, region, sx, sy);
    if (h.rows != 3 || h.cols != 3 || !cv::checkRange(h))
    {
        return false;
    }
    cv::Mat inverse;
    if (!cv::invert(h, inverse, cv::DECOMP_LU) || !cv::checkRange(inverse))
    {
        return false;
    }
    for (int gy = 0; gy < 3; ++gy)
    {
        for (int gx = 0; gx < 3; ++gx)
        {
            const cv::Point2d point(core.x + (core.width - 1.) * gx / 2., core.y + (core.height - 1.) * gy / 2.);
            const double denominator = h.at<double>(2, 0) * point.x + h.at<double>(2, 1) * point.y + h.at<double>(2, 2);
            if (!std::isfinite(denominator) || denominator < .5 || denominator > 1.5)
            {
                return false;
            }
            const auto destination = mapped(h, point);
            const auto source = mapped(inverse, point);
            if (!std::isfinite(destination.x) || !std::isfinite(destination.y) || !std::isfinite(source.x) ||
                !std::isfinite(source.y) || cv::norm(destination - point) > 150. || source.x < 0 || source.y < 0 ||
                source.x >= source_size.width || source.y >= source_size.height)
            {
                return false;
            }
            const double dx = (h.at<double>(0, 0) - h.at<double>(2, 0) * destination.x) / denominator;
            const double dy = (h.at<double>(0, 1) - h.at<double>(2, 1) * destination.x) / denominator;
            const double ex = (h.at<double>(1, 0) - h.at<double>(2, 0) * destination.y) / denominator;
            const double ey = (h.at<double>(1, 1) - h.at<double>(2, 1) * destination.y) / denominator;
            const double determinant = dx * ey - dy * ex;
            if (!std::isfinite(determinant) || determinant <= 0)
            {
                return false;
            }
            const cv::Mat jacobian = (cv::Mat_<double>(2, 2) << dx, dy, ex, ey);
            cv::Mat singular;
            cv::SVD::compute(jacobian, singular);
            const double maximum = singular.at<double>(0), minimum = singular.at<double>(1);
            const double cosine = (dx * dy + ex * ey) / (std::hypot(dx, ex) * std::hypot(dy, ey));
            const double rotation = std::atan2(ex, dx) * 180. / CV_PI;
            if (minimum < .95 || maximum > 1.05 || maximum / minimum > 1.08 || !std::isfinite(cosine) ||
                !std::isfinite(rotation) || std::abs(rotation) > 3. ||
                std::abs(std::asin(std::clamp(cosine, -1., 1.)) * 180. / CV_PI) > LocalAlignmentMaximumShearDegrees)
            {
                return false;
            }
        }
    }
    return true;
}

// 同一空间邻域内的双向 Lowe ratio。所有零件只走这一条匹配路径；不在失败后改阈值。
std::pair<std::vector<cv::Point2f>, std::vector<cv::Point2f>> correspondences(
    const TemplateFeatures &reference, const std::vector<cv::KeyPoint> &moving_points, const cv::Mat &moving_desc,
    double scale)
{
    const int rows = static_cast<int>(reference.keypoints.size()), cols = static_cast<int>(moving_points.size());
    cv::Mat distances(rows, cols, CV_64F, cv::Scalar(inf));
    const double radius = 150. * scale;
    for (int r = 0; r < rows; ++r)
    {
        for (int c = 0; c < cols; ++c)
        {
            const auto delta = reference.keypoints[r].pt - moving_points[c].pt;
            if (cv::norm(delta) <= radius)
            {
                distances.at<double>(r, c) = cv::norm(reference.descriptors.row(r), moving_desc.row(c), cv::NORM_L2);
            }
        }
    }
    auto select = [&](bool reverse) {
        const int queries = reverse ? cols : rows, candidates = reverse ? rows : cols;
        std::vector<int> chosen(queries, -1);
        for (int q = 0; q < queries; ++q)
        {
            double first = inf, second = inf;
            for (int t = 0; t < candidates; ++t)
            {
                const double value = reverse ? distances.at<double>(t, q) : distances.at<double>(q, t);
                if (value < first)
                {
                    second = first;
                    first = value;
                    chosen[q] = t;
                }
                else if (value < second)
                {
                    second = value;
                }
            }
            // 局部窗口只有一个候选时，Lowe ratio 没有分母；此时要求更强的
            // 绝对描述子距离与下方双向一致性，而不是把比值当作已通过。
            const bool distinctive = std::isfinite(second) ? first < .70 * second : first < 120.;
            if (first > 200. || !distinctive)
            {
                chosen[q] = -1;
            }
        }
        return chosen;
    };
    const auto forward = select(false), backward = select(true);
    std::vector<cv::Point2f> source, destination;
    for (int r = 0; r < rows; ++r)
    {
        const int c = forward[r];
        if (c >= 0 && backward[c] == r)
        {
            source.push_back(moving_points[c].pt);
            destination.push_back(reference.keypoints[r].pt);
        }
    }
    return {source, destination};
}

cv::Mat least_squares(const std::vector<cv::Point2f> &source, const std::vector<cv::Point2f> &destination,
                      const std::vector<int> &indices, bool affine)
{
    const int unknowns = affine ? 6 : 4;
    if (static_cast<int>(indices.size()) < (affine ? 4 : 3))
    {
        return {};
    }
    cv::Mat design = cv::Mat::zeros(2 * static_cast<int>(indices.size()), unknowns, CV_64F);
    cv::Mat values = cv::Mat::zeros(design.rows, 1, CV_64F);
    for (size_t i = 0; i < indices.size(); ++i)
    {
        const auto s = source[indices[i]], d = destination[indices[i]];
        const int row = static_cast<int>(2 * i);
        design.at<double>(row, 0) = s.x;
        design.at<double>(row, 1) = affine ? s.y : -s.y;
        design.at<double>(row + 1, 0) = affine ? 0. : s.y;
        design.at<double>(row + 1, 1) = affine ? 0. : s.x;
        design.at<double>(row, 2) = 1.;
        if (affine)
        {
            design.at<double>(row + 1, 3) = s.x;
            design.at<double>(row + 1, 4) = s.y;
            design.at<double>(row + 1, 5) = 1.;
        }
        else
        {
            design.at<double>(row + 1, 3) = 1.;
        }
        values.at<double>(row) = d.x;
        values.at<double>(row + 1) = d.y;
    }
    cv::Mat solution;
    if (!cv::solve(design, values, solution, cv::DECOMP_SVD))
    {
        return {};
    }
    if (affine)
    {
        return (cv::Mat_<double>(2, 3) << solution.at<double>(0), solution.at<double>(1), solution.at<double>(2),
                solution.at<double>(3), solution.at<double>(4), solution.at<double>(5));
    }
    const double a = solution.at<double>(0), b = solution.at<double>(1);
    return (cv::Mat_<double>(2, 3) << a, -b, solution.at<double>(2), b, a, solution.at<double>(3));
}

double leave_out_change(const cv::Mat &matrix, const std::vector<cv::Point2f> &source,
                        const std::vector<cv::Point2f> &destination, const std::vector<int> &inliers,
                        cv::Size work_size, cv::Rect region, double sx, double sy, bool affine, bool homography = false,
                        int *independent_count = nullptr)
{
    // SIFT 同一角点可有多个主方向，它们会形成坐标完全相同的内点。
    // 方向描述子继续参与原有匹配/RANSAC；但稳定性复核必须按几何位置
    // 计数和重拟合，否则“底部两点的三个方向”会误触发整区留出，删掉
    // 全部底部证据，而复制一个方向也会改变最小二乘权重和检查结论。
    std::vector<int> distinct_inliers;
    for (int index : inliers)
    {
        const bool duplicate = std::any_of(distinct_inliers.begin(), distinct_inliers.end(), [&](int previous) {
            return source[index] == source[previous] && destination[index] == destination[previous];
        });
        if (!duplicate)
        {
            distinct_inliers.push_back(index);
        }
    }
    if (independent_count)
    {
        *independent_count = static_cast<int>(distinct_inliers.size());
    }
    if (distinct_inliers.size() < (homography ? 9U : affine ? 5U : 4U))
    {
        // 必须仍能留出至少一个独立位置并保持重拟合的最小点数。
        // 一处角点的多个方向不能伪装成足够的稳定性证据。
        return inf;
    }
    std::array<std::vector<int>, 9> groups;
    for (int index : distinct_inliers)
    {
        const auto point = destination[index];
        const int x = std::clamp(static_cast<int>(3 * point.x / work_size.width), 0, 2);
        const int y = std::clamp(static_cast<int>(3 * point.y / work_size.height), 0, 2);
        groups[y * 3 + x].push_back(index);
    }
    std::vector<std::vector<int>> removed;
    for (const auto &group : groups)
    {
        if (group.size() >= 3)
        {
            removed.push_back(group);
        }
    }
    if (removed.empty())
    {
        for (int index : distinct_inliers)
        {
            removed.push_back({index});
        }
    }
    const auto original = original_matrix(matrix, region, sx, sy);
    // 稳定性应在确有对应证据的位置评价，而不是核心裁框的空白四角。
    // 用所有内点的 CIS 原图坐标作为探针：留出某一区域重拟合时，该区域的
    // 点仍在探针中，因此能测到被留出结构上的预测漂移，而不会把远离图案的
    // 空白区外推误差当成真实配准误差。整个核心的物理可逆/越界约束仍由
    // geometry_ok / homography_geometry_ok 单独检查。
    std::vector<cv::Point2d> probes;
    probes.reserve(distinct_inliers.size());
    for (int index : distinct_inliers)
    {
        const auto point = source[index];
        probes.emplace_back(region.x + (point.x - (sx - 1.) / 2.) / sx, region.y + (point.y - (sy - 1.) / 2.) / sy);
    }
    double worst = 0;
    for (const auto &group : removed)
    {
        std::vector<int> retained;
        for (int index : distinct_inliers)
        {
            if (std::find(group.begin(), group.end(), index) == group.end())
            {
                retained.push_back(index);
            }
        }
        cv::Mat refit;
        if (homography)
        {
            if (retained.size() < 8)
            {
                return inf;
            }
            std::vector<cv::Point2f> retained_source, retained_destination;
            for (int index : retained)
            {
                retained_source.push_back(source[index]);
                retained_destination.push_back(destination[index]);
            }
            refit = cv::findHomography(retained_source, retained_destination, 0);
        }
        else
        {
            refit = least_squares(source, destination, retained, affine);
        }
        if (refit.empty())
        {
            return inf;
        }
        const auto adjusted = original_matrix(refit, region, sx, sy);
        for (auto probe : probes)
        {
            const double change = cv::norm(mapped(adjusted, probe) - mapped(original, probe));
            // 留区重拟合若在真实匹配位置出现无效投影，不能把 NaN 当作 0px
            // 稳定性放行（std::max(0, NaN) 可能仍返回 0）。
            if (!std::isfinite(change))
            {
                return inf;
            }
            worst = std::max(worst, change);
        }
    }
    return worst;
}

struct Candidate
{
    bool accepted = false;
    bool degraded = false; // 硬门控通过、九区均值改善，但未达到原优质标准。
    bool affine = false;
    bool homography = false;
    cv::Mat work_matrix;
    EdgeScore edge;
    int inliers = 0, matches = 0, stability_points = 0;
    double ratio = 0, rms_original = inf, stability = inf;
    int reason = CIS_PATCH_REASON_MATCHES;
    std::string edge_detail;
};

Candidate estimate(bool affine, const std::vector<cv::Point2f> &source, const std::vector<cv::Point2f> &destination,
                   cv::Rect region, cv::Rect core, cv::Size source_size, cv::Size work_size, double sx, double sy,
                   const EdgeScore &before, const EdgeReference &reference, const cv::Mat &moving)
{
    Candidate candidate;
    candidate.affine = affine;
    candidate.matches = static_cast<int>(source.size());
    if (source.size() < (affine ? 12U : 8U))
    {
        return candidate;
    }
    cv::Mat mask;
    {
        // OpenCV 全局 RNG 只在短求解区受锁保护；特征提取与质量图像可并行。
        std::lock_guard<std::mutex> lock(ransac_mutex);
        cv::theRNG().state = LocalAlignmentRansacSeed;
        // RANSAC 参数属于工作图像素；按当前真实 sx/sy 换算，不能把固定
        // 2 工作像素在小工作图上误解释为 2 原像素。
        const double reprojection = std::max(.75, LocalAlignmentRansacThresholdOriginalPx * std::sqrt(sx * sy));
        candidate.work_matrix =
            affine ? cv::estimateAffine2D(source, destination, mask, cv::RANSAC, reprojection, 5000, .995, 10)
                   : cv::estimateAffinePartial2D(source, destination, mask, cv::RANSAC, reprojection, 5000, .995, 10);
    }
    if (candidate.work_matrix.empty() || mask.empty())
    {
        candidate.reason = CIS_PATCH_REASON_GEOMETRY;
        return candidate;
    }
    std::vector<int> inliers;
    std::set<int> cells;
    double squared = 0;
    for (size_t i = 0; i < std::min(mask.total(), source.size()); ++i)
    {
        if (!mask.ptr<uint8_t>()[i])
        {
            continue;
        }
        inliers.push_back(static_cast<int>(i));
        const auto point = destination[i];
        cells.insert(std::clamp(static_cast<int>(point.y * 3 / work_size.height), 0, 2) * 3 +
                     std::clamp(static_cast<int>(point.x * 3 / work_size.width), 0, 2));
        const auto difference = mapped(candidate.work_matrix, source[i]) - cv::Point2d(point);
        squared += difference.dot(difference);
    }
    candidate.inliers = static_cast<int>(inliers.size());
    candidate.ratio = inliers.size() / static_cast<double>(source.size());
    // 重复纹理容易给出“少量彼此一致、但整体错一周期”的解。
    // 过半匹配点支持同一个模型是最基本的共识，不能仅凭低 RMS 和轮廓均值放行。
    if (inliers.size() < (affine ? 10U : 6U) || candidate.ratio < .50 || cells.size() < 3)
    {
        candidate.reason = CIS_PATCH_REASON_FEATURES;
        return candidate;
    }
    candidate.rms_original = std::sqrt(squared / inliers.size()) / std::sqrt(sx * sy);
    if (!std::isfinite(candidate.rms_original) || std::sqrt(squared / inliers.size()) > 1.5 ||
        !geometry_ok(candidate.work_matrix, region, core, source_size, sx, sy, affine))
    {
        candidate.reason = CIS_PATCH_REASON_GEOMETRY;
        return candidate;
    }
    candidate.stability = leave_out_change(candidate.work_matrix, source, destination, inliers, work_size, region, sx,
                                           sy, affine, false, &candidate.stability_points);
    // 相似模型自由度较少；SIFT 点位本身有亚像素定位波动，允许 0.25 原像素
    // 的边界余量。仿射仍保留原 6px 限制，不让额外自由度掩盖留区失稳。
    if (!std::isfinite(candidate.stability) || candidate.stability > (affine ? 6. : 6.25))
    {
        candidate.reason = CIS_PATCH_REASON_MODEL_INSTABILITY;
        return candidate;
    }
    candidate.edge = edge_score(reference, moving, candidate.work_matrix);
    // 模板分区集合固定后，均值可直接与 H0 比较。没有整体改善的矩阵不能
    // 使用；改善未达 10% 或最差区仍超限时使用局部矩阵，但标记待复核。
    if (!std::isfinite(candidate.edge.mean) || !std::isfinite(before.mean) || candidate.edge.mean >= before.mean)
    {
        candidate.reason = CIS_PATCH_REASON_EDGE_QUALITY;
        candidate.edge_detail = "mean-improvement";
        return candidate;
    }
    candidate.degraded = candidate.edge.mean > before.mean * .90 ||
                         candidate.edge.worst / std::sqrt(sx * sy) > LocalAlignmentMaximumCellChamferOriginalPx;
    candidate.edge_detail = candidate.edge.worst / std::sqrt(sx * sy) > LocalAlignmentMaximumCellChamferOriginalPx
                                ? "worst-cell"
                            : candidate.degraded ? "small-improvement"
                                                 : "";
    candidate.accepted = true;
    candidate.reason = candidate.degraded ? CIS_PATCH_REASON_EDGE_QUALITY_DEGRADED : CIS_PATCH_REASON_NONE;
    return candidate;
}

// 只有相似和仿射均不可信、且 H0 也不能判定为已对齐时才调用。
// 使用完全相同的 SIFT 对应点；更高自由度必须由更充分的二维分布、
// 留区稳定性、局部雅可比和更明显的九区改善共同支撑。
Candidate estimate_homography(const std::vector<cv::Point2f> &source, const std::vector<cv::Point2f> &destination,
                              cv::Rect region, cv::Rect core, cv::Size source_size, cv::Size work_size, double sx,
                              double sy, const EdgeScore &before, const EdgeReference &reference, const cv::Mat &moving)
{
    Candidate candidate;
    candidate.homography = true;
    candidate.matches = static_cast<int>(source.size());
    if (source.size() < 16)
    {
        return candidate;
    }
    cv::Mat mask;
    {
        std::lock_guard<std::mutex> lock(ransac_mutex);
        cv::theRNG().state = LocalAlignmentRansacSeed;
        const double reprojection = std::max(.75, LocalAlignmentRansacThresholdOriginalPx * std::sqrt(sx * sy));
        candidate.work_matrix = cv::findHomography(source, destination, cv::RANSAC, reprojection, mask, 5000, .995);
    }
    if (candidate.work_matrix.empty() || mask.empty() || !cv::checkRange(candidate.work_matrix))
    {
        candidate.reason = CIS_PATCH_REASON_GEOMETRY;
        return candidate;
    }
    std::vector<int> inliers;
    std::vector<cv::Point2f> inlier_points;
    std::set<int> cells;
    double squared = 0;
    for (size_t i = 0; i < std::min(mask.total(), source.size()); ++i)
    {
        if (!mask.ptr<uint8_t>()[i])
        {
            continue;
        }
        inliers.push_back(static_cast<int>(i));
        inlier_points.push_back(destination[i]);
        const auto point = destination[i];
        cells.insert(std::clamp(static_cast<int>(point.y * 3 / work_size.height), 0, 2) * 3 +
                     std::clamp(static_cast<int>(point.x * 3 / work_size.width), 0, 2));
        const auto difference = mapped(candidate.work_matrix, source[i]) - cv::Point2d(point);
        squared += difference.dot(difference);
    }
    candidate.inliers = static_cast<int>(inliers.size());
    candidate.ratio = inliers.size() / static_cast<double>(source.size());
    if (inliers.size() < 14 || candidate.ratio < .60 || cells.size() < 5)
    {
        candidate.reason = CIS_PATCH_REASON_FEATURES;
        return candidate;
    }
    const auto bounds = cv::boundingRect(inlier_points);
    std::vector<cv::Point2f> hull;
    cv::convexHull(inlier_points, hull);
    if (bounds.width < work_size.width * .40 || bounds.height < work_size.height * .40 ||
        cv::contourArea(hull) < work_size.area() * .10)
    {
        candidate.reason = CIS_PATCH_REASON_FEATURES;
        return candidate;
    }
    const double rms_work = std::sqrt(squared / inliers.size());
    candidate.rms_original = rms_work / std::sqrt(sx * sy);
    if (!std::isfinite(rms_work) || rms_work > 1.5 ||
        !homography_geometry_ok(candidate.work_matrix, region, core, source_size, sx, sy))
    {
        candidate.reason = CIS_PATCH_REASON_GEOMETRY;
        return candidate;
    }
    candidate.stability = leave_out_change(candidate.work_matrix, source, destination, inliers, work_size, region, sx,
                                           sy, false, true, &candidate.stability_points);
    if (!std::isfinite(candidate.stability) || candidate.stability > 6.)
    {
        candidate.reason = CIS_PATCH_REASON_MODEL_INSTABILITY;
        return candidate;
    }
    candidate.edge = edge_score(reference, moving, candidate.work_matrix);
    if (!std::isfinite(candidate.edge.mean) || !std::isfinite(before.mean) || candidate.edge.mean >= before.mean)
    {
        candidate.reason = CIS_PATCH_REASON_EDGE_QUALITY;
        candidate.edge_detail = "mean-improvement";
        return candidate;
    }
    candidate.degraded = candidate.edge.mean > before.mean * .85 ||
                         candidate.edge.worst / std::sqrt(sx * sy) > LocalAlignmentMaximumCellChamferOriginalPx;
    candidate.edge_detail = candidate.edge.worst / std::sqrt(sx * sy) > LocalAlignmentMaximumCellChamferOriginalPx
                                ? "worst-cell"
                            : candidate.degraded ? "small-improvement"
                                                 : "";
    // 单应性只有特征二维覆盖、雅可比、留区及周期检查均通过后才可用；
    // 15% 改善和最差区上限决定质量级别，不再比更差的 H0 优先级更低。
    candidate.accepted = true;
    candidate.reason = candidate.degraded ? CIS_PATCH_REASON_EDGE_QUALITY_DEGRADED : CIS_PATCH_REASON_NONE;
    return candidate;
}
} // namespace

// 唯一局部配准入口。只返回 CIS→Alpha 的矩阵及质量状态，不重采样生产输出。
// 相似/仿射共享匹配证据；都不可用时才考虑单应性。真正采样由 patch_detector
// 对检测核心执行一次，保证搜索上下文不会进入缺陷统计及最终零件坐标。
Alignment register_patch(const cv::Mat &alpha, const cv::Mat &cis, int at, int ct, double dpi, Worker &worker,
                         cv::Rect core)
{
    const auto started = Clock::now();
    Alignment result;
    result.info.status = CIS_PATCH_ALIGNMENT_REVIEW;
    try
    {
        if (core.width <= 0 || core.height <= 0 || alpha.size() != cis.size() ||
            (core & cv::Rect(0, 0, alpha.cols, alpha.rows)) != core)
        {
            throw std::invalid_argument("Invalid registration core/context");
        }
        const auto domain = registration_core(alpha, core, at);
        const auto template_content = registration_content(alpha, domain.roi, at);
        const auto content = template_content.bounds;
        if (content.width < 32 || content.height < 32)
        {
            result.info.reason = CIS_PATCH_REASON_FEATURES;
            result.diagnostic = "Review/InsufficientTemplateContent";
        }
        else
        {
            const int margin = LocalAlignmentContextPixels;
            const cv::Rect region(domain.roi.x + content.x - margin, domain.roi.y + content.y - margin,
                                  content.width + 2 * margin, content.height + 2 * margin);
            if ((region & cv::Rect(0, 0, alpha.cols, alpha.rows)) != region)
            {
                result.info.reason = CIS_PATCH_REASON_CONTEXT;
                result.diagnostic = "Review/InsufficientContext";
            }
            else
            {
                // 工作分辨率只由同一目标内容与物理 DPI 决定，绝不由成败/留白切换预处理。
                const double density = std::max(LocalAlignmentWorkPixelsPerMm / pixels_per_mm(dpi),
                                                static_cast<double>(LocalAlignmentMinimumWorkWidthPx) / region.width);
                const double scale = std::min(
                    {1., density,
                     static_cast<double>(LocalAlignmentMaximumWorkSidePx) / std::max(region.width, region.height),
                     std::sqrt(static_cast<double>(LocalAlignmentMaximumWorkPixels) /
                               (region.width * static_cast<double>(region.height)))});
                const cv::Size size(std::max(1, round_even(region.width * scale)),
                                    std::max(1, round_even(region.height * scale)));
                const double sx = static_cast<double>(size.width) / region.width;
                const double sy = static_cast<double>(size.height) / region.height;
                cv::Mat ab, cb, reference, moving;
                if (template_content.context_binary.empty())
                {
                    cv::threshold(alpha(region), ab, at, 255, cv::THRESH_BINARY);
                }
                else
                {
                    ab = template_content.context_binary(region);
                }
                cv::threshold(cis(region), cb, ct, 255, cv::THRESH_BINARY);
                cv::resize(ab, reference, size, 0, 0, cv::INTER_AREA);
                cv::resize(cb, moving, size, 0, 0, cv::INTER_AREA);
                cv::GaussianBlur(reference, reference, {3, 3}, .8);
                cv::GaussianBlur(moving, moving, {3, 3}, .8);
                const auto identity = cv::Mat::eye(2, 3, CV_64F);
                const auto prepared_edges = prepare_edges(reference);
                const auto before = edge_score(prepared_edges, moving, identity);
                result.info.edge_before_work_px = before.mean;
                worker.initialize();
                if (std::isfinite(before.mean) && before.mean / std::sqrt(sx * sy) <= .5)
                {
                    result.info.status = CIS_PATCH_ALIGNMENT_NOT_NEEDED;
                    result.info.reason = CIS_PATCH_REASON_NONE;
                    result.diagnostic = "NotNeeded/Aligned: edge=" + number(before.mean);
                }
                else
                {
                    auto template_features = worker.cache->get(reference, *worker.sift, 32);
                    std::vector<cv::KeyPoint> moving_points;
                    cv::Mat moving_desc;
                    extract_alignment_features(moving, *worker.sift, moving_points, moving_desc, nullptr, 0, 32);
                    if (template_features->descriptors.empty() || moving_desc.empty())
                    {
                        result.info.reason = CIS_PATCH_REASON_FEATURES;
                        result.diagnostic = "Review/InsufficientFeatures";
                    }
                    else
                    {
                        const auto pairs =
                            correspondences(*template_features, moving_points, moving_desc, std::sqrt(sx * sy));
                        result.info.matches = static_cast<int>(pairs.first.size());
                        auto similarity = estimate(false, pairs.first, pairs.second, region, core, alpha.size(), size,
                                                   sx, sy, before, prepared_edges, moving);
                        auto affine = estimate(true, pairs.first, pairs.second, region, core, alpha.size(), size, sx,
                                               sy, before, prepared_edges, moving);
                        Candidate *chosen = nullptr;
                        if (similarity.accepted && affine.accepted)
                        {
                            // 模型择优也看九区合计误差：仿射必须比相似明显更好，
                            // 否则优先使用自由度更低的相似变换。两者已经分别
                            // 通过匹配、几何和稳定性硬门控；局部轮廓未达优质
                            // 标准时仍可采用整体更优的候选，但状态标记为待复核。
                            chosen = affine.edge.mean < similarity.edge.mean * .90 ? &affine : &similarity;
                        }
                        else if (similarity.accepted || affine.accepted)
                        {
                            chosen = similarity.accepted ? &similarity : &affine;
                        }
                        // 错周期检查也是最终门控：首选模型若有歧义，先试另一种
                        // 已通过几何/轮廓检查的低自由度模型；两者均不可信才试单应性。
                        std::string period_rejected;
                        auto period_ok = [&](Candidate &candidate) {
                            std::string detail;
                            if (unambiguous_period(reference, moving, at, ct, candidate.work_matrix, std::sqrt(sx * sy),
                                                   worker, detail, 32))
                            {
                                return true;
                            }
                            period_rejected = detail;
                            candidate.accepted = false;
                            candidate.reason = CIS_PATCH_REASON_PERIOD_AMBIGUOUS;
                            candidate.edge_detail = "period-ambiguous";
                            return false;
                        };
                        if (chosen && !period_ok(*chosen))
                        {
                            Candidate *alternate = chosen == &similarity ? &affine : &similarity;
                            chosen = alternate->accepted && period_ok(*alternate) ? alternate : nullptr;
                        }
                        // H0 已足够好时不为追求微小评分收益尝试更自由的模型。
                        // 其他情况下，只有前两种模型都未通过才承担单应性的额外耗时。
                        const double pixels_per_work_pixel = 1. / std::sqrt(sx * sy);
                        const bool already_aligned =
                            !chosen && similarity.reason == CIS_PATCH_REASON_EDGE_QUALITY &&
                            affine.reason == CIS_PATCH_REASON_EDGE_QUALITY &&
                            similarity.edge_detail == "mean-improvement" && affine.edge_detail == "mean-improvement" &&
                            before.occupied >= LocalAlignmentGoodGlobalMinimumCells &&
                            before.mean * pixels_per_work_pixel <= LocalAlignmentGoodGlobalMeanOriginalPx &&
                            before.worst * pixels_per_work_pixel <= LocalAlignmentGoodGlobalWorstCellOriginalPx;
                        Candidate homography;
                        if (!chosen && !already_aligned)
                        {
                            homography = estimate_homography(pairs.first, pairs.second, region, core, alpha.size(),
                                                             size, sx, sy, before, prepared_edges, moving);
                            if (homography.accepted && period_ok(homography))
                            {
                                chosen = &homography;
                            }
                        }
                        if (!chosen)
                        {
                            // 即使没有可用矩阵，也把证据最多的失败候选指标带回日志，
                            // 便于区分“根本无特征”和“有内点但被几何/分区门控拒绝”。
                            const Candidate *evidence = affine.inliers > similarity.inliers ? &affine : &similarity;
                            if (!already_aligned && homography.inliers > evidence->inliers)
                            {
                                evidence = &homography;
                            }
                            result.info.inliers = evidence->inliers;
                            result.info.inlier_ratio = evidence->ratio;
                            if (std::isfinite(evidence->rms_original))
                            {
                                result.info.rms_original_px = evidence->rms_original;
                            }
                            if (std::isfinite(evidence->edge.mean))
                            {
                                result.info.edge_after_work_px = evidence->edge.mean;
                            }
                            if (std::isfinite(evidence->stability))
                            {
                                result.info.leave_out_change_original_px = evidence->stability;
                            }
                            const bool unstable = similarity.reason == CIS_PATCH_REASON_MODEL_INSTABILITY ||
                                                  affine.reason == CIS_PATCH_REASON_MODEL_INSTABILITY ||
                                                  homography.reason == CIS_PATCH_REASON_MODEL_INSTABILITY;
                            result.info.reason = !period_rejected.empty() ? CIS_PATCH_REASON_PERIOD_AMBIGUOUS
                                                 : unstable               ? CIS_PATCH_REASON_MODEL_INSTABILITY
                                                                          : evidence->reason;
                            result.diagnostic =
                                "Review/NoTrustedModel: similarity=" + std::to_string(similarity.reason) + "/" +
                                number(similarity.stability) + "px, affine=" + std::to_string(affine.reason) + "/" +
                                number(affine.stability) + "px, homography=" +
                                (already_aligned
                                     ? "skipped/H0-good"
                                     : std::to_string(homography.reason) + "/" + number(homography.stability) + "px") +
                                ", edge=" + number(before.mean) + "/worst=" + number(before.worst) +
                                ", stabilityPoints=" + std::to_string(similarity.stability_points) + "/" +
                                std::to_string(affine.stability_points) + "/" +
                                std::to_string(homography.stability_points) +
                                ", similarityEdge=" + number(similarity.edge.mean) +
                                "/worst=" + number(similarity.edge.worst) + "/" + similarity.edge_detail +
                                ", affineEdge=" + number(affine.edge.mean) + "/worst=" + number(affine.edge.worst) +
                                "/" + affine.edge_detail + ", homographyEdge=" + number(homography.edge.mean) +
                                "/worst=" + number(homography.edge.worst) + "/" + homography.edge_detail +
                                (period_rejected.empty() ? "" : ", period=" + period_rejected);
                            // 有充分的匹配证据，但两种矩阵都只因改善不足被拒绝时，
                            // 再检查 H0 自身是否已足够好。满足条件则保留原像素、
                            // 不额外 Warp，也不把“没有必要配准”误记为异常。
                            // 若任一候选被几何/稳定性拒绝，或最差分区偏差较大，
                            // 仍维持 Review，不能借平均值掩盖局部错位。
                            if (already_aligned)
                            {
                                result.info.status = CIS_PATCH_ALIGNMENT_NOT_NEEDED;
                                result.info.reason = CIS_PATCH_REASON_NONE;
                                result.info.edge_after_work_px = before.mean;
                                result.diagnostic = "NotNeeded/StrongGlobalAlignment: edge=" + number(before.mean) +
                                                    "/worst=" + number(before.worst) + " work px, " +
                                                    "candidate improvement below 10%; H0 retained";
                            }
                        }
                        else
                        {
                            result.info.matches = chosen->matches;
                            result.info.inliers = chosen->inliers;
                            result.info.inlier_ratio = chosen->ratio;
                            result.info.rms_original_px = chosen->rms_original;
                            result.info.edge_after_work_px = chosen->edge.mean;
                            result.info.leave_out_change_original_px = chosen->stability;
                            result.applied = true;
                            result.transform = original_matrix(chosen->work_matrix, region, sx, sy);
                            result.info.status =
                                chosen->degraded ? CIS_PATCH_ALIGNMENT_APPLIED_DEGRADED : CIS_PATCH_ALIGNMENT_APPLIED;
                            result.info.model = chosen->homography ? CIS_PATCH_MODEL_HOMOGRAPHY
                                                : chosen->affine   ? CIS_PATCH_MODEL_CONSTRAINED_AFFINE
                                                                   : CIS_PATCH_MODEL_SIMILARITY;
                            result.info.reason = chosen->reason;
                            result.diagnostic = std::string(chosen->degraded ? "AppliedDegraded/" : "Applied/") +
                                                (chosen->homography ? "Homography"
                                                 : chosen->affine   ? "ConstrainedAffine"
                                                                    : "Similarity") +
                                                ": matches=" + std::to_string(chosen->matches) +
                                                ", inliers=" + std::to_string(chosen->inliers) +
                                                ", rms=" + number(chosen->rms_original) +
                                                "px, edge=" + number(before.mean) + "->" + number(chosen->edge.mean) +
                                                ", worstCell=" + number(chosen->edge.worst) +
                                                ", cells=" + std::to_string(chosen->edge.occupied) +
                                                ", stability=" + number(chosen->stability) + "px" +
                                                ", stabilityPoints=" + std::to_string(chosen->stability_points) +
                                                (chosen->degraded ? ", review=" + chosen->edge_detail : "");
                        }
                    }
                }
                if (result.info.status == CIS_PATCH_ALIGNMENT_NOT_NEEDED)
                {
                    std::string period;
                    if (!unambiguous_period(reference, moving, at, ct, identity, std::sqrt(sx * sy), worker, period,
                                            32))
                    {
                        result.info.status = CIS_PATCH_ALIGNMENT_REVIEW;
                        result.info.reason = CIS_PATCH_REASON_PERIOD_AMBIGUOUS;
                        result.diagnostic = "Review/PeriodAmbiguous: " + period;
                    }
                }
                result.diagnostic += "; domain=" + std::string(domain.basis) +
                                     "; boundaryFragments=" + std::to_string(template_content.excluded_fragments) +
                                     "/" + std::to_string(template_content.excluded_core_pixels) + "px; frame=(" +
                                     std::to_string(region.x) + "," + std::to_string(region.y) + "," +
                                     std::to_string(region.width) + "," + std::to_string(region.height) +
                                     "), work=" + std::to_string(size.width) + "x" + std::to_string(size.height);
            }
        }
    }
    catch (const std::exception &error)
    {
        result.applied = false;
        result.transform.release();
        result.info.status = CIS_PATCH_ALIGNMENT_ERROR;
        result.info.reason = CIS_PATCH_REASON_EXCEPTION;
        result.diagnostic = std::string("Review/AlignmentError: ") + error.what();
    }
    result.milliseconds = elapsed(started);
    result.info.alignment_ms = result.milliseconds;
    return result;
}
} // namespace cis::patch
