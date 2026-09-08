#include "alignment_internal.h"

namespace alignment
{
cv::Mat gray(const cv::Mat &image)
{
    cv::Mat result;
    if (image.channels() == 1)
        image.copyTo(result);
    else
        cv::cvtColor(image, result, image.channels() == 4 ? cv::COLOR_BGRA2GRAY : cv::COLOR_BGR2GRAY);
    return result;
}

// 顶/底排靠近图像边界时平移整个搜索条带，保持允许的误差余量；不直接截短条带。
cv::Rect search_rect(cv::Size size, double center, double diameter, double margin, double ppm)
{
    if (!std::isfinite(center) || center < 0 || center >= size.height)
        throw std::invalid_argument("预测圆心超出图像高度。");
    double half = diameter * .5 + margin * ppm;
    if (!std::isfinite(half) || half < 0 || half > INT_MAX / 4)
        throw std::invalid_argument("Mark 搜索高度无效。");
    int start = static_cast<int>(std::floor(center - half)), end = static_cast<int>(std::ceil(center + half));
    int height = std::min(size.height, std::max(1, end - start));
    start = std::clamp(start, 0, size.height - height);
    return {0, start, size.width, height};
}
static double coverage(const std::vector<MarkerPoint> &points, int width)
{
    if (points.size() < 2 || width <= 1)
        return 0;
    auto limits = std::minmax_element(points.begin(), points.end(), [](auto &a, auto &b) { return a.X < b.X; });
    return std::clamp((limits.second->X - limits.first->X) / (width - 1.0), 0.0, 1.0);
}
static std::pair<double, double> fit_line(const std::vector<MarkerPoint> &p)
{
    if (p.empty())
        return {0, 0};
    double x = 0, y = 0;
    for (auto &v : p)
    {
        x += v.X;
        y += v.Y;
    }
    x /= p.size();
    y /= p.size();
    double numerator = 0, denominator = 0;
    for (auto &v : p)
    {
        numerator += (v.X - x) * (v.Y - y);
        denominator += (v.X - x) * (v.X - x);
    }
    double slope = denominator <= 1e-6 ? 0 : numerator / denominator;
    return {slope, y - slope * x};
}
static double line_distance(const MarkerPoint &p, double slope, double intercept)
{
    return std::abs(slope * p.X - p.Y + intercept) / std::sqrt(slope * slope + 1);
}
void update_geometry(Row &row)
{
    sort_x(row.Points);
    auto line = fit_line(row.Points);
    row.Slope = line.first;
    row.EndToEndYDrift = line.first * std::max(0, row.SearchRect.width - 1);
    row.HorizontalCoverage = coverage(row.Points, row.SearchRect.width);
    row.MedianLineResidual =
        row.Points.size() < 2
            ? 0
            : median_of(row.Points, [&](auto &p) { return line_distance(p, line.first, line.second); });
}

// 枚举候选点对拟合倾斜排，再以全部内点最小二乘细化；不强制同排圆心 Y 相同。
// 多条候选排依次比较数量、横向覆盖、行残差、物理预测位置，保留原 C# 的平局顺序。
static std::vector<MarkerPoint> select_row(const std::vector<MarkerPoint> &points, double diameter, double expectedY,
                                           int width)
{
    if (points.size() <= 1)
    {
        auto result = points;
        sort_x(result);
        return result;
    }
    std::vector<MarkerPoint> best;
    double bestCoverage = -1, bestResidual = DBL_MAX, bestCenter = DBL_MAX;
    double tolerance = std::max(3.0, diameter * RowLineInlierDiameterRatio);
    for (size_t i = 0; i + 1 < points.size(); ++i)
        for (size_t j = i + 1; j < points.size(); ++j)
        {
            double dx = points[j].X - points[i].X;
            if (std::abs(dx) < std::max(2.0, diameter * .5))
                continue;
            double slope = (points[j].Y - points[i].Y) / dx, intercept = points[i].Y - slope * points[i].X;
            std::vector<MarkerPoint> inliers;
            for (auto &p : points)
                if (line_distance(p, slope, intercept) <= tolerance)
                    inliers.push_back(p);
            if (inliers.size() < 2)
                continue;
            auto refined = fit_line(inliers);
            slope = refined.first;
            intercept = refined.second;
            inliers.clear();
            for (auto &p : points)
                if (line_distance(p, slope, intercept) <= tolerance)
                    inliers.push_back(p);
            double cover = coverage(inliers, width);
            double residual = median_of(inliers, [&](auto &p) { return line_distance(p, slope, intercept); });
            double center = std::abs(slope * (width * .5) + intercept - expectedY);
            bool better = inliers.size() > best.size() ||
                          (inliers.size() == best.size() &&
                           (cover > bestCoverage + 1e-6 ||
                            (std::abs(cover - bestCoverage) <= 1e-6 &&
                             (residual < bestResidual - 1e-6 ||
                              (std::abs(residual - bestResidual) <= 1e-6 && center < bestCenter)))));
            if (better)
            {
                best = inliers;
                bestCoverage = cover;
                bestResidual = residual;
                bestCenter = center;
            }
        }
    if (best.empty())
        best.push_back(*std::min_element(points.begin(), points.end(), [&](auto &a, auto &b) {
            return std::abs(a.Y - expectedY) < std::abs(b.Y - expectedY);
        }));
    sort_x(best);
    return best;
}

// 不用外接框面积替代轮廓面积；CIS 允许更宽的纵向形变范围，底排可复用顶排面积参考。
static std::vector<MarkerPoint> valid_markers(const cv::Mat &binary, int yOffset, double circularity, double diameter,
                                              bool cis, double referenceArea = 0)
{
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(binary, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
    std::vector<MarkerPoint> result;
    double stripArea = std::max(1.0, static_cast<double>(binary.cols) * binary.rows);
    for (auto &contour : contours)
    {
        double area = cv::contourArea(contour), perimeter = cv::arcLength(contour, true);
        if (perimeter <= 0 || area / stripArea < .0001 || area / stripArea > .20)
            continue;
        double circular = 4 * CV_PI * area / (perimeter * perimeter);
        if (circular < circularity)
            continue;
        cv::Rect bounds = cv::boundingRect(contour);
        if (diameter > 0 &&
            (bounds.height < diameter * (cis ? .35 : .55) || bounds.height > diameter * (cis ? 2.20 : 1.60)))
            continue;
        if (referenceArea > 0 && (area < referenceArea * .3 || area > referenceArea * 3))
            continue;
        auto m = cv::moments(contour);
        if (std::abs(m.m00) < std::numeric_limits<double>::denorm_min())
            continue;
        result.push_back({m.m10 / m.m00, m.m01 / m.m00 + yOffset, area, circular, static_cast<double>(bounds.width),
                          static_cast<double>(bounds.height), 0});
    }
    return result;
}
static void clear_border(cv::Mat &binary, int border)
{
    int x = std::min(border, binary.cols), y = std::min(border, binary.rows);
    binary(cv::Rect(0, 0, binary.cols, y)).setTo(0);
    binary(cv::Rect(0, binary.rows - y, binary.cols, y)).setTo(0);
    binary(cv::Rect(0, 0, x, binary.rows)).setTo(0);
    binary(cv::Rect(binary.cols - x, 0, x, binary.rows)).setTo(0);
}
Row detect_tiff_row(const cv::Mat &image, const Region &region, const Config &c)
{
    Row row;
    row.SearchRect = search_rect(image.size(), region.TiffCenterY, region.TiffDiameterPixels, c.InitialSearchMarginMm,
                                 region.TiffPixelsPerMm);
    cv::Mat strip = image(row.SearchRect), bgr = strip;
    if (strip.channels() == 1)
        cv::cvtColor(strip, bgr, cv::COLOR_GRAY2BGR);
    else if (strip.channels() == 4)
        cv::cvtColor(strip, bgr, cv::COLOR_BGRA2BGR);
    cv::Mat f, distSq, dist, u8, binary;
    bgr.convertTo(f, CV_32FC3);
    std::vector<cv::Mat> channels;
    cv::split(f, channels);
    // 使用 B/G/R 到白色的欧氏距离保留彩色 Mark。逐通道运算顺序与托管实现一致。
    for (auto &channel : channels)
    {
        cv::subtract(channel, cv::Scalar::all(255), channel);
        cv::multiply(channel, channel, channel);
    }
    cv::add(channels[0], channels[1], distSq);
    cv::add(distSq, channels[2], distSq);
    cv::sqrt(distSq, dist);
    dist.convertTo(u8, CV_8UC1, 255.0 / 441.7);
    cv::threshold(u8, binary, 25, 255, cv::THRESH_BINARY);
    row.Points =
        select_row(valid_markers(binary, row.SearchRect.y, c.MinCircularityTiff, region.TiffDiameterPixels, false),
                   region.TiffDiameterPixels, region.TiffCenterY, image.cols);
    update_geometry(row);
    return row;
}
Row detect_cis_row(const cv::Mat &image, const Region &region, const Config &c, double referenceArea)
{
    Row row;
    // 单次条带包含额外半径余量应对倾斜；不再执行重复的全宽扩展搜索。
    row.SearchRect = search_rect(image.size(), region.CisCenterY, region.CisDiameterPixels,
                                 c.InitialSearchMarginMm + c.MarkDiameterMm * .5, region.CisPixelsPerMm);
    cv::Mat contrast, blurred;
    cv::createCLAHE(2.0, {4, 4})->apply(image(row.SearchRect), contrast);
    cv::GaussianBlur(contrast, blurred, {3, 3}, 0);
    cv::Mat kernel = cv::getStructuringElement(cv::MORPH_ELLIPSE, {5, 5});
    std::vector<MarkerPoint> best, previous;
    int bestThreshold = 120, stable = 0;
    // 正常批次先试 120..160 附近；严重缺墨仍覆盖完整低灰度阈值表。
    const int thresholds[] = {140, 120, 160, 100, 180, 80, 60, 40, 20, 30, 50, 70};
    auto distance = [&](auto &points) {
        if (points.empty())
            return DBL_MAX;
        double sum = 0;
        for (auto &p : points)
            sum += p.Y;
        return std::abs(sum / points.size() - region.CisCenterY);
    };
    for (int threshold : thresholds)
    {
        cv::Mat binary;
        cv::threshold(blurred, binary, threshold, 255, cv::THRESH_BINARY);
        if (static_cast<double>(cv::countNonZero(binary)) / std::max(1, binary.cols * binary.rows) > .5)
            cv::bitwise_not(binary, binary);
        clear_border(binary, std::min(15, std::max(2, binary.rows / 20)));
        cv::morphologyEx(binary, binary, cv::MORPH_OPEN, kernel, {-1, -1}, 2);
        auto circles = select_row(
            valid_markers(binary, row.SearchRect.y, c.MinCircularityCis, region.CisDiameterPixels, true, referenceArea),
            region.CisDiameterPixels, region.CisCenterY, image.cols);
        bool equivalent = !circles.empty() && circles.size() == previous.size();
        if (equivalent)
            for (size_t i = 0; i < circles.size(); ++i)
            {
                double dx = circles[i].X - previous[i].X, dy = circles[i].Y - previous[i].Y;
                if (std::sqrt(dx * dx + dy * dy) > std::max(3.0, region.CisDiameterPixels * .15))
                {
                    equivalent = false;
                    break;
                }
            }
        if (equivalent)
            ++stable;
        else
        {
            stable = 1;
            previous = circles;
        }
        double cover = coverage(circles, image.cols), bestCover = coverage(best, image.cols);
        if (circles.size() > best.size() ||
            (circles.size() == best.size() &&
             (cover > bestCover + 1e-6 || (std::abs(cover - bestCover) <= 1e-6 && distance(circles) < distance(best)))))
        {
            best = circles;
            bestThreshold = threshold;
        }
        // 连续三个阈值得到稳定且覆盖充分的排可提前结束；缺失数量不固定，不强求七点齐全。
        if (best.size() >= 7 ||
            (stable >= 3 && circles.size() >= MinimumPointsPerRow && cover >= MinimumStrongRowCoverage))
            break;
    }
    row.Points = best;
    row.Threshold = bestThreshold;
    update_geometry(row);
    return row;
}

// 固定尺度/偏移后动态规划一一配对。跳过缺失点，不压缩编号，不伪造对应点。
static Match monotonic_match(const std::vector<MarkerPoint> &t, const std::vector<MarkerPoint> &c, double scale,
                             double offset, double gate, int width)
{
    size_t rows = t.size() + 1, cols = c.size() + 1;
    std::vector<int> counts(rows * cols);
    std::vector<double> sums(rows * cols);
    std::vector<uint8_t> actions(rows * cols);
    for (size_t i = 1; i < rows; ++i)
        actions[i * cols] = 1;
    for (size_t j = 1; j < cols; ++j)
        actions[j] = 2;
    auto better = [](int a, double ar, int b, double br) { return a > b || (a == b && ar < br - 1e-9); };
    for (size_t i = 1; i < rows; ++i)
        for (size_t j = 1; j < cols; ++j)
        {
            size_t k = i * cols + j, up = k - cols, left = k - 1, diagonal = up - 1;
            counts[k] = counts[up];
            sums[k] = sums[up];
            actions[k] = 1;
            if (better(counts[left], sums[left], counts[k], sums[k]))
            {
                counts[k] = counts[left];
                sums[k] = sums[left];
                actions[k] = 2;
            }
            double residual = std::abs(t[i - 1].X - (scale * c[j - 1].X + offset));
            if (residual <= gate && better(counts[diagonal] + 1, sums[diagonal] + residual, counts[k], sums[k]))
            {
                counts[k] = counts[diagonal] + 1;
                sums[k] = sums[diagonal] + residual;
                actions[k] = 3;
            }
        }
    std::vector<std::pair<size_t, size_t>> pairs;
    size_t i = t.size(), j = c.size();
    while (i > 0 || j > 0)
    {
        auto action = actions[i * cols + j];
        if (action == 3)
        {
            pairs.emplace_back(i - 1, j - 1);
            --i;
            --j;
        }
        else if (action == 1 && i > 0)
            --i;
        else if (j > 0)
            --j;
        else
            break;
    }
    std::reverse(pairs.begin(), pairs.end());
    Match result;
    result.Scale = scale;
    result.Offset = offset;
    std::vector<double> errors;
    for (auto pair : pairs)
    {
        result.TiffPoints.push_back(t[pair.first]);
        result.CisPoints.push_back(c[pair.second]);
        result.TemplateIndices.push_back(static_cast<int>(pair.first) + 1);
        errors.push_back(std::abs(t[pair.first].X - (scale * c[pair.second].X + offset)));
    }
    if (!errors.empty())
        result.MedianResidual = median(errors);
    result.Coverage = coverage(result.TiffPoints, width);
    return result;
}
Match match_rows(const Row &tiffRow, const Row &cisRow, double expected)
{
    Match best;
    bool haveBest = false;
    if (tiffRow.Points.size() < 2 || cisRow.Points.size() < 2)
        return best;
    auto t = tiffRow.Points, c = cisRow.Points;
    sort_x(t);
    sort_x(c);
    double gate =
        std::max(4.0, RowMatchGateDiameterRatio * median_of(t, [](auto &p) { return std::max(p.Width, p.Height); }));
    double centered = tiffRow.SearchRect.width * .5 - expected * cisRow.SearchRect.width * .5;
    auto better = [&](const Match &a) {
        if (!haveBest)
            return true;
        if (a.TiffPoints.size() != best.TiffPoints.size())
            return a.TiffPoints.size() > best.TiffPoints.size();
        if (std::abs(a.Coverage - best.Coverage) > 1e-6)
            return a.Coverage > best.Coverage;
        double ae = std::abs(a.Scale - expected), be = std::abs(best.Scale - expected);
        if (std::abs(ae - be) > 1e-6)
            return ae < be;
        double width = std::max(1.0, static_cast<double>(tiffRow.SearchRect.width));
        ae = std::abs(a.Offset - centered) / width;
        be = std::abs(best.Offset - centered) / width;
        if (std::abs(ae - be) > 1e-6)
            return ae < be;
        return a.MedianResidual < best.MedianResidual - 1e-6;
    };
    // QR 宽度只提供尺度先验，矩阵最终仍由实测 Mark 求解。所有点对保持原枚举顺序。
    for (size_t ti = 0; ti + 1 < t.size(); ++ti)
        for (size_t tj = ti + 1; tj < t.size(); ++tj)
        {
            double tspan = t[tj].X - t[ti].X;
            if (tspan <= 1e-6)
                continue;
            for (size_t ci = 0; ci + 1 < c.size(); ++ci)
                for (size_t cj = ci + 1; cj < c.size(); ++cj)
                {
                    double cspan = c[cj].X - c[ci].X;
                    if (cspan <= 1e-6)
                        continue;
                    double scale = tspan / cspan;
                    if (scale <= 0 || !std::isfinite(scale) ||
                        std::abs(scale - expected) / std::max(std::abs(expected), 1e-6) >
                            MaximumHorizontalScaleDeviationFromQr)
                        continue;
                    auto candidate =
                        monotonic_match(t, c, scale, t[ti].X - scale * c[ci].X, gate, tiffRow.SearchRect.width);
                    if (better(candidate))
                    {
                        best = std::move(candidate);
                        haveBest = true;
                    }
                }
        }
    return best;
}

// 侧边小圆每层独立小 ROI：位置/面积/横纵直径/圆度联合评分，允许轻微椭圆，不跨层串点。
static SideDetection side_in_window(const cv::Mat &image, cv::Point2d center, double dx, double dy, double ppmX,
                                    double ppmY, double margin, double circularity)
{
    SideDetection result;
    double halfX = dx * .5 + margin * ppmX, halfY = dy * .5 + margin * ppmY;
    if (!std::isfinite(halfX) || !std::isfinite(halfY) || halfX < 0 || halfY < 0 || halfX > INT_MAX / 4 ||
        halfY > INT_MAX / 4)
        throw std::invalid_argument("侧边 Mark 搜索尺寸无效。");
    int x0 = std::max(0, static_cast<int>(std::floor(center.x - halfX))),
        y0 = std::max(0, static_cast<int>(std::floor(center.y - halfY)));
    int x1 = std::min(image.cols, static_cast<int>(std::ceil(center.x + halfX))),
        y1 = std::min(image.rows, static_cast<int>(std::ceil(center.y + halfY)));
    if (x1 <= x0 || y1 <= y0)
        throw std::invalid_argument("侧边 Mark 搜索区域为空。");
    result.SearchRect = {x0, y0, x1 - x0, y1 - y0};
    cv::Mat contrast, blurred;
    cv::createCLAHE(2.0, {4, 4})->apply(gray(image(result.SearchRect)), contrast);
    cv::GaussianBlur(contrast, blurred, {3, 3}, 0);
    double expectedArea = CV_PI * dx * dy * .25;
    for (int polarity : {cv::THRESH_BINARY, cv::THRESH_BINARY_INV})
    {
        cv::Mat binary;
        cv::threshold(blurred, binary, 0, 255, polarity | cv::THRESH_OTSU);
        clear_border(binary, std::min(3, std::max(1, std::min(binary.cols, binary.rows) / 20)));
        cv::morphologyEx(binary, binary, cv::MORPH_OPEN, cv::getStructuringElement(cv::MORPH_ELLIPSE, {3, 3}));
        std::vector<std::vector<cv::Point>> contours;
        cv::findContours(binary, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
        for (auto &contour : contours)
        {
            double area = cv::contourArea(contour), perimeter = cv::arcLength(contour, true);
            if (area <= 0 || perimeter <= 0 || area < expectedArea * .2 || area > expectedArea * 3)
                continue;
            auto bounds = cv::boundingRect(contour);
            if (bounds.width < dx * .35 || bounds.width > dx * 2.2 || bounds.height < dy * .35 ||
                bounds.height > dy * 2.2)
                continue;
            double circular = 4 * CV_PI * area / (perimeter * perimeter);
            if (circular < circularity)
                continue;
            auto m = cv::moments(contour);
            if (std::abs(m.m00) < std::numeric_limits<double>::denorm_min())
                continue;
            double x = m.m10 / m.m00 + x0, y = m.m01 / m.m00 + y0;
            double nx = (x - center.x) / std::max(result.SearchRect.width * .5, 1.0),
                   ny = (y - center.y) / std::max(result.SearchRect.height * .5, 1.0);
            double score = 4 * std::sqrt(nx * nx + ny * ny) +
                           1.5 * std::abs(std::log(std::max(area / expectedArea, 1e-6))) +
                           std::abs(bounds.width / std::max(dx, 1e-6) - 1) +
                           std::abs(bounds.height / std::max(dy, 1e-6) - 1) + (1 - circular);
            if (result.Found && score >= result.Point.Score)
                continue;
            result.Found = true;
            result.Point = {
                x, y, area, circular, static_cast<double>(bounds.width), static_cast<double>(bounds.height), score};
        }
    }
    return result;
}
SideDetection detect_side(const cv::Mat &image, cv::Point2d center, double dx, double dy, double ppmX, double ppmY,
                          double initial, double expanded, double circularity)
{
    if (!finite(center) || center.x < 0 || center.y < 0 || center.x >= image.cols || center.y >= image.rows)
        return {};
    auto result = side_in_window(image, center, dx, dy, ppmX, ppmY, initial, circularity);
    if (!result.Found && expanded > initial)
    {
        result = side_in_window(image, center, dx, dy, ppmX, ppmY, expanded, circularity);
        result.UsedExpandedWindow = true;
    }
    return result;
}
} // namespace alignment
