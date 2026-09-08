#include "alignment_internal.h"

namespace alignment
{
std::string white_diagnostic(const White &w)
{
    return w.Status == 0 ? "" : " | WhiteInk: " + w.Diagnostic;
}

// 在原灰度图上采样，而不是在 CLAHE/二值化结果上统计；否则增强算法会抹掉缺墨的亮度差。
White inspect_white(const cv::Mat &image, const Region &region, const Row &row,
                    const std::vector<MarkerPoint> &topPoints, const Config &config)
{
    White result;
    result.Status = 7;
    result.SearchRegion = row.SearchRect;
    std::vector<std::pair<cv::Point2d, bool>> centers;
    auto bottom = row.Points, top = topPoints;
    sort_x(bottom);
    sort_x(top);
    if (top.size() >= 2)
    {
        std::vector<bool> used(bottom.size());
        for (auto &p : top)
        {
            int best = -1;
            double distance = DBL_MAX;
            for (size_t i = 0; i < bottom.size(); ++i)
                if (!used[i] && std::abs(bottom[i].X - p.X) < distance)
                {
                    distance = std::abs(bottom[i].X - p.X);
                    best = static_cast<int>(i);
                }
            // 底排轮廓消失时仍用上排同列 X + QR 底排 Y 采样，避免完全无墨反而无法判定。
            if (best >= 0 && distance <= region.CisDiameterPixels)
            {
                centers.push_back({{bottom[best].X, bottom[best].Y}, true});
                used[best] = true;
            }
            else
                centers.push_back({{p.X, region.CisCenterY}, false});
        }
    }
    else
        for (auto &p : bottom)
            centers.push_back({{p.X, p.Y}, true});
    if (centers.size() < 2)
    {
        result.Diagnostic =
            cv::format("底排/上排可用 Mark 中心仅 %d 个，无法形成可靠白墨统计。", static_cast<int>(centers.size()));
        return result;
    }
    double diameter = std::max(4.0, region.CisDiameterPixels);
    double core = diameter * .30, inner = diameter * .57, outer = diameter * .82;
    for (size_t i = 0; i < centers.size(); ++i)
    {
        auto p = centers[i].first;
        int x0 = std::max(0, static_cast<int>(std::floor(p.x - outer))),
            x1 = std::min(image.cols, static_cast<int>(std::ceil(p.x + outer + 1)));
        int y0 = std::max(0, static_cast<int>(std::floor(p.y - outer))),
            y1 = std::min(image.rows, static_cast<int>(std::ceil(p.y + outer + 1)));
        if (x1 <= x0 || y1 <= y0)
            continue;
        cv::Rect bounds{x0, y0, x1 - x0, y1 - y0};
        cv::Point center{round_even(p.x - x0), round_even(p.y - y0)};
        cv::Mat roi = image(bounds), markMask = cv::Mat::zeros(bounds.size(), CV_8UC1),
                backgroundMask = cv::Mat::zeros(bounds.size(), CV_8UC1);
        // 核心圆避开边缘/小量错位，环形背景负责消除局部底材亮度差。半径口径保持原版。
        cv::circle(markMask, center, std::max(2, round_even(core)), 255, -1, cv::LINE_8);
        cv::circle(backgroundMask, center, std::max(3, round_even(outer)), 255, -1, cv::LINE_8);
        cv::circle(backgroundMask, center, std::max(2, round_even(inner)), 0, -1, cv::LINE_8);
        for (size_t j = 0; j < centers.size(); ++j)
        {
            if (j == i)
                continue;
            auto other = centers[j].first;
            if (other.x < x0 - diameter || other.x > x1 + diameter || other.y < y0 - diameter ||
                other.y > y1 + diameter)
                continue;
            // 双圆紧邻时不把邻圆当背景，防止白墨对比度被人为压低。
            cv::circle(backgroundMask, {round_even(other.x - x0), round_even(other.y - y0)},
                       std::max(2, round_even(inner)), 0, -1, cv::LINE_8);
        }
        if (cv::countNonZero(markMask) < 20 || cv::countNonZero(backgroundMask) < 20)
            continue;
        cv::Scalar mean, sd, bg, bgSd;
        cv::meanStdDev(roi, mean, sd, markMask);
        cv::meanStdDev(roi, bg, bgSd, backgroundMask);
        result.Samples.push_back({static_cast<int>(result.Samples.size()) + 1, centers[i].second ? 1 : 0, p.x, p.y,
                                  diameter * .5, mean[0], sd[0] * sd[0], bg[0], mean[0] - bg[0]});
    }
    if (result.Samples.size() < 2)
    {
        result.Diagnostic =
            cv::format("有效灰度样本仅 %d 个，无法形成可靠白墨统计。", static_cast<int>(result.Samples.size()));
        return result;
    }
    std::vector<double> means, vars, backgrounds, contrasts;
    for (auto &s : result.Samples)
    {
        means.push_back(s.mean);
        vars.push_back(s.variance);
        backgrounds.push_back(s.background);
        contrasts.push_back(s.contrast);
    }
    // 每项独立中位数抑制单个污点/反光。这里的百分比是相对灰度指标，不是真实墨水体积计量。
    result.MarkMean = median(means);
    result.MarkVariance = median(vars);
    result.BackgroundMean = median(backgrounds);
    result.Contrast = median(contrasts);
    double normalContrast = std::max(1.0, config.WhiteInkNormalGray - result.BackgroundMean);
    result.InkLevelPercent = std::clamp(result.Contrast * 100.0 / normalContrast, 0.0, 100.0);
    double sd = std::sqrt(std::max(0.0, result.MarkVariance));
    result.HasStreaking = sd >= config.WhiteInkStreakStdDevThreshold;
    double level = result.InkLevelPercent;
    result.Status = level < 20 ? 6 : level < 40 ? 5 : level < 60 ? 4 : level < 80 ? 3 : result.HasStreaking ? 2 : 1;
    const char *names[] = {"未启用", "正常", "白墨拉丝", "轻度缺墨", "中度缺墨", "严重缺墨", "基本无白墨", "无法判定"};
    std::string name = names[result.Status];
    if (result.HasStreaking && result.Status >= 3 && result.Status <= 5)
        name = result.Status == 3 ? "缺墨并伴拉丝" : name + "并伴拉丝";
    result.Diagnostic = cv::format(
        "状态=%s, 相对白墨=%.1f%%, Mark均值=%.1f, 背景均值=%.1f, 对比度=%.1f, 标准差=%.1f, 方差=%.1f, 拉丝=%s, 样本=%d",
        name.c_str(), level, result.MarkMean, result.BackgroundMean, result.Contrast, sd, result.MarkVariance,
        result.HasStreaking ? "是" : "否", static_cast<int>(result.Samples.size()));
    return result;
}

// Theil-Sen 中位斜率避免低对比圆 D4 一类偏心点拉偏整排；保留 X，仅将 Y 校正回共线约束。
static bool robust_line(const std::vector<MarkerPoint> &points, double diameter, double &slope, double &intercept)
{
    if (points.size() < 3)
        return false;
    std::vector<double> slopes;
    for (size_t i = 0; i + 1 < points.size(); ++i)
        for (size_t j = i + 1; j < points.size(); ++j)
        {
            double dx = points[j].X - points[i].X;
            if (std::abs(dx) >= std::max(1.0, diameter))
                slopes.push_back((points[j].Y - points[i].Y) / dx);
        }
    if (slopes.empty())
        return false;
    slope = std::clamp(median(slopes), -.05, .05);
    intercept = median_of(points, [&](auto &p) { return p.Y - slope * p.X; });
    return true;
}
static std::vector<MarkerPoint> constrain_row(std::vector<MarkerPoint> points, double expectedDiameter)
{
    sort_x(points);
    double diameter = std::max(4.0, expectedDiameter), area = CV_PI * diameter * diameter * .25;
    for (auto &p : points)
    {
        p.Width = p.Height = diameter;
        p.Area = area;
        p.Circularity = 1;
    }
    double slope = 0, intercept = 0;
    if (!robust_line(points, diameter, slope, intercept))
        return points;
    double tolerance = std::max(4.0, diameter * .35);
    std::vector<MarkerPoint> inliers;
    for (auto &p : points)
        if (std::abs(p.Y - (slope * p.X + intercept)) <= tolerance)
            inliers.push_back(p);
    double refinedSlope = 0, refinedIntercept = 0;
    if (robust_line(inliers, diameter, refinedSlope, refinedIntercept))
    {
        slope = refinedSlope;
        intercept = refinedIntercept;
    }
    std::vector<MarkerPoint> result;
    for (auto p : points)
        if (std::abs(p.Y - (slope * p.X + intercept)) <= tolerance)
        {
            p.Y = slope * p.X + intercept;
            result.push_back(p);
        }
    return result;
}
static std::vector<MarkerPoint> hough_markers(const cv::Mat &image, cv::Rect roi, const Region &region,
                                              const Anchor &anchor)
{
    cv::Mat blurred;
    int blur = std::min(21, std::max(5, round_even(region.CisDiameterPixels * .05) | 1));
    cv::GaussianBlur(image(roi), blurred, {blur, blur}, 2);
    std::vector<cv::Vec3f> circles;
    cv::HoughCircles(blurred, circles, cv::HOUGH_GRADIENT, 1.2, std::max(20.0, region.CisDiameterPixels * .48), 40, 18,
                     std::max(3, round_even(region.CisDiameterPixels * .28)),
                     std::max(4, round_even(region.CisDiameterPixels * .72)));
    double exclude = std::max(anchor.PixelWidth * .65, region.CisDiameterPixels);
    std::vector<MarkerPoint> candidates;
    for (auto circle : circles)
    {
        // 原 Hough Center 为 float；与 ROI int 相加仍先按 float 计算，再提升 double。
        double x = circle[0] + roi.x, y = circle[1] + roi.y, r = circle[2];
        if (std::abs(y - region.CisCenterY) > region.CisDiameterPixels * .65 || std::abs(x - anchor.CenterX) <= exclude)
            continue;
        candidates.push_back({x, y, CV_PI * r * r, 1, r * 2, r * 2,
                              std::abs(y - region.CisCenterY) + std::abs(r - region.CisDiameterPixels * .5) * .5});
    }
    std::stable_sort(candidates.begin(), candidates.end(), [](auto &a, auto &b) { return a.Score < b.Score; });
    std::vector<MarkerPoint> unique;
    double duplicate = region.CisDiameterPixels * .70;
    for (auto &c : candidates)
    {
        bool found = false;
        for (auto &p : unique)
        {
            double dx = p.X - c.X, dy = p.Y - c.Y;
            if (dx * dx + dy * dy < duplicate * duplicate)
            {
                found = true;
                break;
            }
        }
        if (!found)
            unique.push_back(c);
    }
    auto result = constrain_row(unique, region.CisDiameterPixels);
    sort_x(result);
    if (result.size() > 12)
        result.resize(12);
    return result;
}

White inspect_bottom(const cv::Mat &cis, const Anchor &anchor, const Config &config)
{
    if (!config.EnableWhiteInkInspection)
        return {};
    White unable;
    unable.Status = 7;
    if (cis.empty())
    {
        unable.Diagnostic = "CIS 拼接图为空。";
        return unable;
    }
    if (anchor.PixelHeight <= 1 || !std::isfinite(anchor.PixelHeight))
    {
        unable.Diagnostic = "第二个二维码高度无效，无法定位 Bottom 条带。";
        return unable;
    }
    if (config.QrPhysicalHeightMm <= 0 || config.MarkDiameterMm <= 0 || config.WhiteInkNormalGray <= 0 ||
        config.WhiteInkNormalGray > 255 || config.WhiteInkStreakStdDevThreshold <= 0)
    {
        unable.Diagnostic = "白墨检测物理参数或灰度标定参数无效。";
        return unable;
    }
    double y = static_cast<double>(anchor.GlobalCenterY - anchor.SegmentStartGlobalY);
    if (y < 0 || y >= cis.rows)
    {
        unable.Diagnostic = cv::format("Bottom 圆心 Y=%.1f 超出 CIS 高度 %d。", y, cis.rows);
        return unable;
    }
    Region region;
    region.Name = "Bottom";
    region.CisCenterY = y;
    region.CisPixelsPerMm = anchor.PixelHeight / config.QrPhysicalHeightMm;
    region.CisDiameterPixels = config.MarkDiameterMm * region.CisPixelsPerMm;
    try
    {
        cv::Mat g = gray(cis);
        auto row = detect_cis_row(g, region, config, 0);
        // 无墨纹理破坏二值轮廓时才启用受限 Hough 后备；正常批次不承担这部分额外成本。
        if (row.Points.size() < 2)
        {
            auto points = hough_markers(g, row.SearchRect, region, anchor);
            if (points.size() > row.Points.size())
                row.Points = std::move(points);
        }
        return inspect_white(g, region, row, {}, config);
    }
    catch (const std::exception &e)
    {
        unable.Diagnostic = std::string("Bottom 白墨检查异常：") + e.what();
        return unable;
    }
}
} // namespace alignment
