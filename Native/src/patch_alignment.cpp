#include "patch_detector.h"
#include "patch_constants.h"
#include <set>
#include <sstream>
#include <iomanip>
#include <limits>

namespace cis::patch
{
namespace
{
constexpr double infinity = std::numeric_limits<double>::infinity();
// 保留原固定随机种子与短临界区策略；不能把特征提取、Warp 和差分也锁在这里。
std::mutex ransac_mutex;
std::string number(double value, int digits = 3)
{
    std::ostringstream out;
    out << std::fixed << std::setprecision(digits) << value;
    return out.str();
}

// 对应迁移前 BilinearSample。
double sample(const cv::Mat &values, double x, double y)
{
    int x0 = static_cast<int>(std::floor(x)), y0 = static_cast<int>(std::floor(y)), x1 = x0 + 1, y1 = y0 + 1;
    if (x0 < 0 || y0 < 0 || x1 >= values.cols || y1 >= values.rows)
        return LocalAlignmentChamferDistanceCapWorkPx;
    double fx = x - x0, fy = y - y0;
    double top = values.at<float>(y0, x0) * (1. - fx) + values.at<float>(y0, x1) * fx;
    double bottom = values.at<float>(y1, x0) * (1. - fx) + values.at<float>(y1, x1) * fx;
    return top * (1. - fy) + bottom * fy;
}
struct Shift
{
    double score = infinity, x = 0, y = 0, magnitude = infinity;
};
// 对应迁移前 FindBestRobustChamferShift。
// 在两组二值轮廓之间搜索双向 Chamfer 最优平移。整数搜索负责覆盖范围，
// 亚像素搜索通过双线性采样距离场细化结果；每个方向最多固定采样 6000 个点，
// 保证不同轮廓复杂度下耗时可控且结果可复现。
Shift best_shift(const cv::Mat &reference, const cv::Mat &moving, int radius, int margin_radius, double maximum,
                 bool subpixel)
{
    cv::Mat inverse_reference, inverse_moving, reference_distance, moving_distance;
    cv::bitwise_not(reference, inverse_reference);
    cv::bitwise_not(moving, inverse_moving);
    cv::distanceTransform(inverse_reference, reference_distance, cv::DIST_L2, cv::DIST_MASK_3);
    cv::distanceTransform(inverse_moving, moving_distance, cv::DIST_L2, cv::DIST_MASK_3);
    int margin = std::max(0, margin_radius) +
                 (subpixel ? static_cast<int>(std::ceil(LocalAlignmentSubpixelRadiusWorkPx)) + 1 : 1);
    std::vector<cv::Point> references, movings;
    for (int y = margin; y < reference.rows - margin; ++y)
        for (int x = margin; x < reference.cols - margin; ++x)
        {
            if (reference.ptr<uint8_t>(y)[x])
                references.emplace_back(x, y);
            if (moving.ptr<uint8_t>(y)[x])
                movings.emplace_back(x, y);
        }
    Shift best;
    if (references.empty() || movings.empty())
        return best;
    int reference_stride = std::max(
        1,
        static_cast<int>(std::ceil(references.size() / static_cast<double>(LocalAlignmentMaxEdgeSamplesPerDirection))));
    int moving_stride = std::max(
        1, static_cast<int>(std::ceil(movings.size() / static_cast<double>(LocalAlignmentMaxEdgeSamplesPerDirection))));
    auto evaluate = [&](double dx, double dy) {
        double forward = 0, reverse = 0;
        int nf = 0, nr = 0;
        for (size_t i = 0; i < movings.size(); i += moving_stride)
        {
            auto p = movings[i];
            forward += std::min(sample(reference_distance, p.x + dx, p.y + dy), LocalAlignmentChamferDistanceCapWorkPx);
            ++nf;
        }
        for (size_t i = 0; i < references.size(); i += reference_stride)
        {
            auto p = references[i];
            reverse += std::min(sample(moving_distance, p.x - dx, p.y - dy), LocalAlignmentChamferDistanceCapWorkPx);
            ++nr;
        }
        double score = .5 * (forward / nf + reverse / nr), magnitude = dx * dx + dy * dy;
        if (score < best.score - 1e-9 || (std::abs(score - best.score) <= 1e-9 && magnitude < best.magnitude))
            best = {score, dx, dy, magnitude};
    };
    // 先整数网格，再仅在整数最优点邻域以 0.25 工作像素精修；相同评分优先更小位移。
    for (int dy = -radius; dy <= radius; ++dy)
        for (int dx = -radius; dx <= radius; ++dx)
            if (std::sqrt(static_cast<double>(dx * dx + dy * dy)) <= maximum + 1e-9)
                evaluate(dx, dy);
    if (subpixel && radius > 0 && std::isfinite(best.score))
    {
        double bx = best.x, by = best.y;
        for (double oy = -LocalAlignmentSubpixelRadiusWorkPx; oy <= LocalAlignmentSubpixelRadiusWorkPx + 1e-9;
             oy += LocalAlignmentSubpixelStepWorkPx)
            for (double ox = -LocalAlignmentSubpixelRadiusWorkPx; ox <= LocalAlignmentSubpixelRadiusWorkPx + 1e-9;
                 ox += LocalAlignmentSubpixelStepWorkPx)
            {
                double dx = bx + ox, dy = by + oy;
                if (std::abs(dx) > radius + 1e-9 || std::abs(dy) > radius + 1e-9 ||
                    std::sqrt(dx * dx + dy * dy) > maximum + 1e-9)
                    continue;
                evaluate(dx, dy);
            }
    }
    return best;
}

// 对应迁移前 HasNoSignificantLocalEdgeRegression。
// 对固定的模板边缘点，比较局部配准前后到 CIS 最近边缘的平均距离。
// 使用固定模板点集而不是比较两幅差分图，可避免某个区域因 CIS 偏暗、边缘点变少而
// 获得虚假的“分数改善”。任一有足够模板结构的区域明显退化，整张局部矩阵即拒绝。
bool locally_stable(const cv::Mat &reference, const cv::Mat &before, const cv::Mat &after, double scale,
                    double improvement, double &worst, int &cells)
{
    worst = -infinity;
    cells = 0;
    cv::Mat inverse_before, inverse_after, distance_before, distance_after;
    cv::bitwise_not(before, inverse_before);
    cv::bitwise_not(after, inverse_after);
    cv::distanceTransform(inverse_before, distance_before, cv::DIST_L2, cv::DIST_MASK_3);
    cv::distanceTransform(inverse_after, distance_after, cv::DIST_L2, cv::DIST_MASK_3);
    int border = std::max(1, static_cast<int>(std::ceil(LocalAlignmentMaxTranslationOriginalPx * scale))),
        regressed = 0;
    // 固定模板边缘分成 3x3 区，比较同一组点；忽略 Warp 可能越界的安全边框。
    for (int gy = 0; gy < LocalAlignmentValidationGridSize; ++gy)
        for (int gx = 0; gx < LocalAlignmentValidationGridSize; ++gx)
        {
            int count = 0;
            double sum_before = 0, sum_after = 0;
            for (int y = gy * reference.rows / 3; y < (gy + 1) * reference.rows / 3; ++y)
                for (int x = gx * reference.cols / 3; x < (gx + 1) * reference.cols / 3; ++x)
                {
                    if (x < border || x >= reference.cols - border || y < border || y >= reference.rows - border ||
                        !reference.ptr<uint8_t>(y)[x])
                        continue;
                    ++count;
                    sum_before += std::min(static_cast<double>(distance_before.at<float>(y, x)),
                                           LocalAlignmentChamferDistanceCapWorkPx);
                    sum_after += std::min(static_cast<double>(distance_after.at<float>(y, x)),
                                          LocalAlignmentChamferDistanceCapWorkPx);
                }
            if (count < LocalAlignmentMinReferenceEdgesPerCell)
                continue;
            ++cells;
            double regression = sum_after / count - sum_before / count;
            worst = std::max(worst, regression);
            if (regression > LocalAlignmentMaxLocalRegressionPixels)
                ++regressed;
        }
    if (cells == 0)
    {
        worst = infinity;
        return false;
    }
    if (worst == -infinity)
        worst = 0;
    return regressed == 0 || (improvement >= LocalAlignmentStrongEdgeImprovementRatio && regressed == 1 &&
                              worst <= LocalAlignmentStrongCaseMaxLocalRegressionPixels);
}
struct EdgeGate
{
    double before = infinity, after = infinity, dx = 0, dy = 0, worst = infinity;
    int cells = 0;
};
// 对应迁移前 TryRefineAffineTranslationByEdges。
// 在候选变换附近，以模板/CIS 二值轮廓的双向距离场为目标做小范围平移精修。
// 先搜索工作图整像素，再在最优点附近进行亚像素搜索；距离采用截断损失，
// 使少量真实缺陷不会为了降低配准分数而牵引整张零件图。
// 若最终评分没有优于未做局部配准的输入，则拒绝矩阵，避免错误匹配使结果变差。
bool refine(const cv::Mat &alpha, const cv::Mat &cis, int at, int ct, double scale, cv::Mat &transform, bool stability,
            double minimum_improvement, double max_original, EdgeGate &gate)
{
    gate = {};
    double maximum = std::max(LocalAlignmentSubpixelStepWorkPx, max_original * scale);
    int radius = std::max(1, static_cast<int>(std::ceil(maximum)));
    cv::Mat ab, cb, warped, ae, before, after, final_warp, final_edges;
    auto kernel = cv::getStructuringElement(cv::MORPH_RECT, {3, 3});
    cv::threshold(alpha, ab, at, 255, cv::THRESH_BINARY);
    cv::threshold(cis, cb, ct, 255, cv::THRESH_BINARY);
    cv::morphologyEx(ab, ae, cv::MORPH_GRADIENT, kernel);
    cv::morphologyEx(cb, before, cv::MORPH_GRADIENT, kernel);
    gate.before = best_shift(ae, before, 0, radius, 0, true).score;
    cv::warpAffine(cb, warped, transform, ab.size(), cv::INTER_NEAREST);
    cv::morphologyEx(warped, after, cv::MORPH_GRADIENT, kernel);
    auto shift = best_shift(ae, after, radius, radius, maximum, true);
    gate.after = shift.score;
    gate.dx = shift.x;
    gate.dy = shift.y;
    double improvement = (gate.before - gate.after) / std::max(gate.before, 1e-6);
    if (!std::isfinite(gate.before) || !std::isfinite(gate.after) || improvement < minimum_improvement)
        return false;
    // 精修矩阵先保存在临时副本；后续拒绝时不污染原始 SIFT 或平移候选。
    cv::Mat refined = transform.clone();
    refined.at<double>(0, 2) += gate.dx;
    refined.at<double>(1, 2) += gate.dy;
    if (stability)
    {
        cv::warpAffine(cb, final_warp, refined, ab.size(), cv::INTER_NEAREST);
        cv::morphologyEx(final_warp, final_edges, cv::MORPH_GRADIENT, kernel);
        if (!locally_stable(ae, before, final_edges, scale, improvement, gate.worst, gate.cells))
            return false;
    }
    else
    {
        gate.worst = 0;
        gate.cells = 0;
    }
    refined.copyTo(transform);
    return true;
}

// 对应迁移前 TryValidateLocalAffine。
// 检查完整 2x2 线性部分，防止镜像、过大旋转/缩放或剪切仅靠对角元素漏检。
bool valid_transform(const cv::Mat &transform, double scale, std::string &diagnostic)
{
    double a = transform.at<double>(0, 0), b = transform.at<double>(0, 1), tx = transform.at<double>(0, 2);
    double c = transform.at<double>(1, 0), d = transform.at<double>(1, 1), ty = transform.at<double>(1, 2);
    double determinant = a * d - b * c, s1 = std::sqrt(a * a + c * c), s2 = std::sqrt(b * b + d * d);
    double shear = s1 > 0 && s2 > 0 ? std::abs((a * b + c * d) / (s1 * s2)) : infinity,
           rotation = std::atan2(c, a) * 180. / CV_PI;
    bool finite = true;
    for (double v : {a, b, c, d, tx, ty, determinant, s1, s2, shear, rotation})
        finite = finite && std::isfinite(v);
    bool accepted = finite && determinant > 0 && s1 >= .90 && s1 <= 1.10 && s2 >= .90 && s2 <= 1.10 && shear <= .15 &&
                    std::abs(rotation) <= 5. && std::abs(tx / scale) <= LocalAlignmentMaxTranslationOriginalPx &&
                    std::abs(ty / scale) <= LocalAlignmentMaxTranslationOriginalPx;
    diagnostic = accepted ? ""
                          : "仿射矩阵越界: scale=(" + number(s1, 4) + "," + number(s2, 4) +
                                "), rot=" + number(rotation, 2) + "deg, shear=" + number(shear) + ", move=(" +
                                number(tx / scale, 2) + "," + number(ty / scale, 2) + ")px";
    return accepted;
}
// 对应迁移前 HasSufficientInlierCoverage。
// 内点不能全部挤在一个局部重复纹理块内。这里要求至少跨越三个 3x3 网格，
// 并在 X/Y 至少一个方向覆盖图像 20%，兼容细长图案而不强制二维铺满。
bool sufficient_coverage(const std::vector<cv::Point2f> &points, cv::Size size, double &coverage)
{
    coverage = 0;
    if (points.empty() || size.width <= 0 || size.height <= 0)
        return false;
    float minx = points[0].x, maxx = minx, miny = points[0].y, maxy = miny;
    std::set<int> cells;
    for (auto p : points)
    {
        minx = std::min(minx, p.x);
        maxx = std::max(maxx, p.x);
        miny = std::min(miny, p.y);
        maxy = std::max(maxy, p.y);
        int x = std::max(0, std::min(2, static_cast<int>(p.x * 3 / size.width))),
            y = std::max(0, std::min(2, static_cast<int>(p.y * 3 / size.height)));
        cells.insert(y * 3 + x);
    }
    // C# 的跨度先执行 float 运算再赋值 double，保留边界判断精度。
    double sx = std::max(0.f, maxx - minx) / size.width, sy = std::max(0.f, maxy - miny) / size.height;
    coverage = sx * sy;
    return cells.size() >= 3 && std::max(sx, sy) >= .20;
}
// 对应迁移前 CalculateAffineRmsOriginalPixels。
double rms(const cv::Mat &t, const std::vector<cv::Point2f> &source, const std::vector<cv::Point2f> &target,
           double scale)
{
    double squared = 0;
    for (size_t i = 0; i < source.size(); ++i)
    {
        auto s = source[i], d = target[i];
        double x = t.at<double>(0, 0) * s.x + t.at<double>(0, 1) * s.y + t.at<double>(0, 2) - d.x;
        double y = t.at<double>(1, 0) * s.x + t.at<double>(1, 1) * s.y + t.at<double>(1, 2) - d.y;
        squared += x * x + y * y;
    }
    return source.empty() ? infinity : std::sqrt(squared / source.size()) / scale;
}
double median(std::vector<double> &values)
{
    std::sort(values.begin(), values.end());
    size_t m = values.size() / 2;
    return values.size() % 2 ? values[m] : .5 * (values[m - 1] + values[m]);
}
// 对应迁移前 TryBuildTranslationFallback。
// 当相似变换对局部结构产生不利影响时，使用 RANSAC 内点位移的中位数构造纯平移候选。
// P80 残差限制保证大多数内点确实支持同一个平移；存在真实旋转或缩放时不会误走该分支。
// 返回矩阵由调用方 cv::Mat 自动管理；不跨 C ABI 共享。
bool translation_fallback(const std::vector<cv::Point2f> &source, const std::vector<cv::Point2f> &target, double scale,
                          cv::Mat &transform, double &p80)
{
    p80 = infinity;
    if (source.size() < 3 || scale <= 0)
        return false;
    std::vector<double> dx, dy, residuals;
    for (size_t i = 0; i < source.size(); ++i)
    {
        dx.push_back(target[i].x - source[i].x);
        dy.push_back(target[i].y - source[i].y);
    }
    double mx = median(dx), my = median(dy);
    for (size_t i = 0; i < source.size(); ++i)
    {
        double x = target[i].x - source[i].x - mx, y = target[i].y - source[i].y - my;
        residuals.push_back(std::sqrt(x * x + y * y) / scale);
    }
    std::sort(residuals.begin(), residuals.end());
    p80 = residuals[std::max(0, static_cast<int>(std::ceil(source.size() * .80)) - 1)];
    if (p80 > LocalAlignmentTranslationConsensusP80OriginalPx)
        return false;
    transform = cv::Mat::eye(2, 3, CV_64F);
    transform.at<double>(0, 2) = mx;
    transform.at<double>(1, 2) = my;
    return true;
}
// 对应迁移前 SelectRatioMatches。
std::vector<cv::DMatch> ratio_matches(cv::BFMatcher &matcher, const cv::Mat &a, const cv::Mat &b)
{
    std::vector<std::vector<cv::DMatch>> knn;
    matcher.knnMatch(a, b, knn, 2);
    std::vector<cv::DMatch> result;
    for (const auto &matches : knn)
        if (matches.size() >= 2 && matches[0].distance < .70f * matches[1].distance)
            result.push_back(matches[0]);
    return result;
}
std::string edge_diagnostic(const EdgeGate &gate)
{
    return "edge=" + number(gate.before) + "->" + number(gate.after) + ", refine=(" + number(gate.dx, 2) + "," +
           number(gate.dy, 2) + ")workPx, localWorst=" + number(gate.worst) + "px/" + std::to_string(gate.cells);
}
} // namespace

std::shared_ptr<TemplateFeatures> TemplateCache::get(const cv::Mat &image, cv::SIFT &sift)
{
    auto start = Clock::now();
    uint64_t hash = 1469598103934665603ULL;
    // 16x16 规则网格 FNV 签名仅用于分桶；相同签名必须再经过 L1==0 精确像素比较。
    for (int gy = 0; gy < 16; ++gy)
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
    std::string key = std::to_string(image.rows) + "x" + std::to_string(image.cols) + ":" +
                      std::to_string(image.type()) + ":" + std::to_string(hash);
    double key_ms = elapsed(start);
    std::shared_ptr<TemplateBucket> bucket;
    {
        std::lock_guard<std::mutex> lock(mutex);
        stats.quick_key_ms += key_ms;
        auto &entry = buckets[key];
        if (!entry)
            entry = std::make_shared<TemplateBucket>();
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
                ++stats.hits;
        }
        if (equal)
            return entry;
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

// 对应原 TryLocalAlign：输入是相同尺寸、已轻度平滑的工作图，scale=工作像素/原像素。
// 矩阵方向始终 CIS -> TIFF Alpha；结果仅含矩阵与诊断，最终取样集中在 detect 中完成。
// 沿用当前的分级顺序，不因迁入 C++ 改为另一套配准模型：
// 轮廓平移/已对齐跳过 -> 双向 SIFT + RANSAC 相似变换 -> 小范围精修与局部稳定门控。
// 拒绝矩阵只意味着继续用全局裁图，不直接把该零件判 NG。
Alignment local_align(const cv::Mat &alpha, const cv::Mat &cis, int at, int ct, double scale, Worker &worker)
{
    auto start = Clock::now();
    Alignment result;
    auto reject = [&](const std::string &reason) {
        result.diagnostic = "skipped: " + reason;
        result.milliseconds = elapsed(start);
        return result;
    };
    auto accept = [&](const cv::Mat &transform, const std::string &model, const EdgeGate &gate,
                      const std::string &details) {
        result.applied = true;
        result.transform = transform.clone();
        result.milliseconds = elapsed(start);
        result.diagnostic = "Applied/" + model + ": workScale=" + number(scale) + ", " + details + ", move=(" +
                            number(transform.at<double>(0, 2) / scale, 2) + "," +
                            number(transform.at<double>(1, 2) / scale, 2) + ")px, " + edge_diagnostic(gate);
        return result;
    };
    try
    {
        // 1. 延续当前生产代码：先验证低自由度的轮廓平移，足够对齐时保留全局裁图。
        cv::Mat fast = cv::Mat::eye(2, 3, CV_64F);
        EdgeGate gate;
        bool accepted = refine(alpha, cis, at, ct, scale, fast, true, LocalAlignmentFastTranslationMinImprovementRatio,
                               LocalAlignmentTranslationRefineRadiusOriginalPx, gate);
        double score_original = gate.before / std::max(scale, 1e-6),
               shift_original = std::sqrt(gate.dx * gate.dx + gate.dy * gate.dy) / std::max(scale, 1e-6);
        double improvement = (gate.before - gate.after) / std::max(gate.before, 1e-6);
        if (std::isfinite(score_original) &&
            (score_original <= LocalAlignmentNotNeededScoreOriginalPx ||
             (std::isfinite(gate.after) && shift_original <= LocalAlignmentNotNeededShiftOriginalPx &&
              improvement < LocalAlignmentFastTranslationMinImprovementRatio)))
        {
            result.diagnostic = "NotNeeded: " + edge_diagnostic(gate);
            result.milliseconds = elapsed(start);
            return result;
        }
        std::string diagnostic;
        bool boundary =
            std::sqrt(gate.dx * gate.dx + gate.dy * gate.dy) >=
            LocalAlignmentTranslationRefineRadiusOriginalPx * scale * LocalAlignmentFastTranslationBoundaryRatio;
        if (accepted && !boundary && valid_transform(fast, scale, diagnostic))
            return accept(fast, "FastTranslation", gate, "");

        // 2. 每 worker 独占 SIFT/BFMatcher，模板特征按批次复用。输入已统一到独立 700 px 工作宽度。
        worker.initialize();
        auto pattern = worker.cache->get(alpha, *worker.sift);
        if (pattern->keypoints.empty() || pattern->descriptors.empty())
            return reject("模板特征为空");
        cv::Mat descriptors;
        std::vector<cv::KeyPoint> keypoints;
        worker.sift->detectAndCompute(cis, cv::noArray(), keypoints, descriptors);
        if (keypoints.empty() || descriptors.empty())
            return reject("CIS 特征为空");
        auto forward = ratio_matches(*worker.matcher, pattern->descriptors, descriptors),
             reverse = ratio_matches(*worker.matcher, descriptors, pattern->descriptors);
        // 周期纹理容易一对多误配。正反方向都把对方选为最佳才保留，
        // 然后再按原图尺度限制位移；增加特征数不能替代这两层几何约束。
        std::unordered_map<int, int> reverse_map;
        for (auto m : reverse)
            reverse_map[m.queryIdx] = m.trainIdx;
        std::vector<cv::Point2f> targets, sources;
        for (auto m : forward)
        {
            auto it = reverse_map.find(m.trainIdx);
            if (it == reverse_map.end() || it->second != m.queryIdx)
                continue;
            auto a = pattern->keypoints[m.queryIdx].pt, c = keypoints[m.trainIdx].pt;
            if (std::abs((a.x - c.x) / scale) <= LocalAlignmentMaxMatchDisplacementOriginalPx &&
                std::abs((a.y - c.y) / scale) <= LocalAlignmentMaxMatchDisplacementOriginalPx)
            {
                targets.push_back(a);
                sources.push_back(c);
            }
        }
        if (targets.size() < 6)
            return reject("有效匹配不足(" + std::to_string(targets.size()) + "/6)");
        cv::Mat inlier_mask, transform;
        // 相似变换只允许平移/旋转/统一缩放，不引入完整仿射的剪切和非等比拉伸。
        // 固定随机种子，只对 RANSAC 的短求解加锁；矩阵方向始终为 CIS -> TIFF。
        {
            std::lock_guard<std::mutex> lock(ransac_mutex);
            cv::theRNG().state = LocalAlignmentRansacSeed;
            transform = cv::estimateAffinePartial2D(sources, targets, inlier_mask, cv::RANSAC,
                                                    std::max(.5, LocalAlignmentRansacThresholdOriginalPx * scale), 2000,
                                                    .99, 10);
        }
        if (transform.empty() || inlier_mask.empty())
            return reject("Similarity RANSAC 未得到矩阵或内点");
        std::vector<cv::Point2f> ti, si;
        for (size_t i = 0; i < std::min(inlier_mask.total(), targets.size()); ++i)
            if (inlier_mask.ptr<uint8_t>()[i])
            {
                ti.push_back(targets[i]);
                si.push_back(sources[i]);
            }
        double ratio = ti.size() / static_cast<double>(targets.size()), coverage = 0;
        if (ti.size() < 6)
            return reject("Similarity 内点不足");
        if (!sufficient_coverage(ti, alpha.size(), coverage))
            return reject("内点空间覆盖不足: " + number(coverage));
        double residual = rms(transform, si, ti, scale);
        if (residual > LocalAlignmentMaxResidualRmsOriginalPx)
            return reject("内点重投影 RMS 过大: " + number(residual));
        bool standard = ratio >= LocalAlignmentMinimumInlierRatio;
        // 大量重复文字可能拉低比例但仍有足够可靠内点。条件通道同时要求 >=12 内点、
        // >=35% 比例、>=8% 覆盖、<=2.5 原像素 RMS；通过后仍须检查矩阵范围和轮廓，
        // 不是绕过质量门控直接接受。此例外原已存在，本次迁移不放宽任何门槛。
        bool conditional = ti.size() >= LocalAlignmentConditionalMinimumInlierCount &&
                           ratio >= LocalAlignmentConditionalMinimumInlierRatio &&
                           coverage >= LocalAlignmentConditionalMinimumBoundingCoverage &&
                           residual <= LocalAlignmentConditionalMaxResidualRmsOriginalPx;
        if (!standard && !conditional)
            return reject("Similarity 内点置信度不足");
        if (!valid_transform(transform, scale, diagnostic))
            return reject(diagnostic);
        std::string model = standard ? "Similarity" : "SimilarityConditional";
        // 3. 原有相似变换质量门控；必要时使用同一组 RANSAC 内点位移中位数给出平移候选。
        if (!refine(alpha, cis, at, ct, scale, transform, true, LocalAlignmentMinEdgeImprovementRatio,
                    LocalAlignmentCandidateRefineRadiusOriginalPx, gate))
        {
            std::string similarity = edge_diagnostic(gate);
            cv::Mat translation;
            double p80;
            bool built = translation_fallback(si, ti, scale, translation, p80), refined = false;
            if (built)
            {
                double tx = translation.at<double>(0, 2), ty = translation.at<double>(1, 2);
                bool strong =
                    p80 <= LocalAlignmentStrongTranslationConsensusP80OriginalPx &&
                    std::sqrt(tx * tx + ty * ty) / scale >= LocalAlignmentStrongTranslationMinMagnitudeOriginalPx;
                refined = refine(alpha, cis, at, ct, scale, translation, !strong, LocalAlignmentMinEdgeImprovementRatio,
                                 LocalAlignmentCandidateRefineRadiusOriginalPx, gate);
            }
            if (!refined)
                return reject("相似变换与纯平移均未通过轮廓质量门控: similarity(" + similarity + "), translation(" +
                              edge_diagnostic(gate) + "), P80=" + number(p80));
            translation.copyTo(transform);
            model = "Translation";
        }
        if (!valid_transform(transform, scale, diagnostic))
            return reject("边缘精修后" + diagnostic);
        return accept(transform, model, gate,
                      "kp=" + std::to_string(pattern->keypoints.size()) + "/" + std::to_string(keypoints.size()) +
                          ", mutual=" + std::to_string(targets.size()) + ", inliers=" + std::to_string(ti.size()) +
                          ", ratio=" + number(ratio) + ", coverage=" + number(coverage) + ", rms=" + number(residual));
    }
    catch (const std::exception &ex)
    {
        return reject(std::string("异常: ") + ex.what());
    }
}
} // namespace cis::patch
