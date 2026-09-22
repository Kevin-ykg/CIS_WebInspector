#include "qr_detector.h"
#include <limits>

namespace qr
{
namespace
{
// 轮廓树会为同一个定位框产生多层嵌套矩形；IoU 与中心距离共同用于去重。
double iou(cv::Rect a, cv::Rect b)
{
    const auto overlap = a & b;
    double area = static_cast<double>(overlap.width) * overlap.height;
    double total = static_cast<double>(a.width) * a.height + static_cast<double>(b.width) * b.height - area;
    return total <= 0 ? 0 : area / total;
}

// 将估算出的二维码边长归一化到有限的工作尺寸。
// 最多保留 8 组且限制缩放范围，防止困难样本把 DNN 调用次数无限放大。
void add_scale(std::vector<Scale> &list, double target, double side, double relative)
{
    if (list.size() >= 8 || side <= 0)
        return;
    double x = target / side, y = x * clamp(relative, .55, 1.80);
    if (x < .35 || x > 1.75 || y < .35 || y > 1.75)
        return;
    for (const auto &item : list)
        if (std::abs(item.x - x) < .01 && std::abs(item.y - y) < .01)
            return;
    list.push_back({target, x, y});
}
cv::Point2f center(const Finder &f, int x, int y)
{
    return {static_cast<float>(f.x + x), static_cast<float>(f.y + y)};
}
} // namespace

std::vector<Finder> finder_evidence(const cv::Mat &source)
{
    // 二维码三个定位框具有“黑-白-黑”嵌套轮廓。Otsu 仅负责形成稳定轮廓，
    // 此处不解码文本，也不会把一个看起来像方框的轮廓直接当成二维码。
    cv::Mat binary;
    cv::threshold(source, binary, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    std::vector<std::vector<cv::Point>> contours;
    std::vector<cv::Vec4i> hierarchy;
    cv::findContours(binary, contours, hierarchy, cv::RETR_TREE, cv::CHAIN_APPROX_SIMPLE);
    std::vector<Finder> raw, distinct;
    int min_side = std::max(12, std::min(source.cols, source.rows) / 120);
    int max_side = std::max(min_side + 1, round_even(std::min(source.cols, source.rows) * .45));
    for (size_t i = 0; i < contours.size(); ++i)
    {
        auto box = cv::boundingRect(contours[i]);
        double aspect = box.width / static_cast<double>(std::max(1, box.height));
        if (std::min(box.width, box.height) < min_side || std::max(box.width, box.height) > max_side || aspect < .65 ||
            aspect > 1.50)
            continue;
        // 至少向下存在两层子轮廓，才保留为定位框的弱证据。
        int depth = 0, child = hierarchy[i][2];
        while (child >= 0 && child < static_cast<int>(hierarchy.size()) && depth < 4)
        {
            ++depth;
            child = hierarchy[child][2];
        }
        if (depth >= 2)
            raw.push_back({box, box.x + box.width * .5, box.y + box.height * .5,
                           std::sqrt(static_cast<double>(box.width) * box.height)});
    }
    // 外层优先；一个定位框的多层轮廓不能当作多个独立证据。
    std::sort(raw.begin(), raw.end(),
              [](const Finder &a, const Finder &b) { return a.bounds.area() > b.bounds.area(); });
    for (const auto &f : raw)
    {
        bool duplicate = false;
        for (const auto &accepted : distinct)
        {
            double x = f.x - accepted.x, y = f.y - accepted.y, limit = std::max(f.side, accepted.side) * .45;
            if (x * x + y * y <= limit * limit || iou(f.bounds, accepted.bounds) >= .30)
            {
                duplicate = true;
                break;
            }
        }
        if (!duplicate)
            distinct.push_back(f);
    }
    return distinct;
}

bool adaptive_scales(const std::vector<Finder> &evidence, std::vector<Scale> &candidates, int &coherent_count,
                     double &side, double &relative)
{
    // 自适应缩放不依赖具体历史样本：先找尺寸接近、近似水平或垂直排列的两个定位框，
    // 再尝试寻找与其中一点形成近似直角的第三个定位框。
    candidates.clear();
    coherent_count = 0;
    side = 0;
    relative = 1;
    int first = -1, second = -1;
    double best = std::numeric_limits<double>::max();
    for (size_t i = 0; i < evidence.size(); ++i)
        for (size_t j = i + 1; j < evidence.size(); ++j)
        {
            const auto &a = evidence[i];
            const auto &b = evidence[j];
            double small = std::min(a.side, b.side), large = std::max(a.side, b.side);
            if (small <= 0 || large / small > 1.65)
                continue;
            double x = b.x - a.x, y = b.y - a.y, length = std::sqrt(x * x + y * y), average = (a.side + b.side) * .5;
            double deviation = std::min(std::abs(x), std::abs(y)) / std::max(1.0, std::max(std::abs(x), std::abs(y)));
            double separation = length / std::max(1.0, average);
            if (separation < 1.60 || separation > 8.50 || deviation > .45)
                continue;
            double score = std::abs(std::log(large / small)) + deviation * .50 + average / std::max(1.0, length) * .05;
            if (score < best)
            {
                best = score;
                first = static_cast<int>(i);
                second = static_cast<int>(j);
            }
        }
    if (first < 0)
        return false;
    std::vector<Finder> coherent{evidence[first], evidence[second]};
    double pair_side = (coherent[0].side + coherent[1].side) * .5;
    int third = -1;
    best = std::numeric_limits<double>::max();
    for (int i = 0; i < static_cast<int>(evidence.size()); ++i)
    {
        if (i == first || i == second)
            continue;
        double ratio = evidence[i].side / pair_side;
        if (ratio < .67 || ratio > 1.50)
            continue;
        for (int c = 0; c < 2; ++c)
        {
            double px = coherent[1 - c].x - coherent[c].x, py = coherent[1 - c].y - coherent[c].y;
            double tx = evidence[i].x - coherent[c].x, ty = evidence[i].y - coherent[c].y;
            double p = std::sqrt(px * px + py * py), t = std::sqrt(tx * tx + ty * ty);
            if (p <= 0 || t <= pair_side * 1.60)
                continue;
            double cosine = std::abs((px * tx + py * ty) / (p * t)), legs = std::max(p, t) / std::min(p, t);
            if (cosine > .35 || legs > 1.50)
                continue;
            double score = cosine + std::abs(std::log(legs)) + std::abs(std::log(ratio));
            if (score < best)
            {
                best = score;
                third = i;
            }
        }
    }
    // 第三个定位框必须同时满足尺寸、直角和两条边长度约束，不能只因大小相近就加入。
    if (third >= 0)
        coherent.push_back(evidence[third]);
    std::vector<double> widths, heights;
    double min_x = DBL_MAX, min_y = DBL_MAX, max_x = -DBL_MAX, max_y = -DBL_MAX;
    for (const auto &f : coherent)
    {
        widths.push_back(f.bounds.width);
        heights.push_back(f.bounds.height);
        min_x = std::min(min_x, f.x);
        max_x = std::max(max_x, f.x);
        min_y = std::min(min_y, f.y);
        max_y = std::max(max_y, f.y);
    }
    auto median = [](std::vector<double> &v) {
        std::sort(v.begin(), v.end());
        size_t n = v.size() / 2;
        return v.size() % 2 ? v[n] : (v[n - 1] + v[n]) * .5;
    };
    double width = median(widths), height = median(heights);
    if (width <= 0 || height <= 0)
        return false;
    // 定位框的宽高比给出线扫方向的相对形变；三点外包范围估算完整二维码边长。
    relative = clamp(width / height, .55, 1.80);
    side = std::max(max_x - min_x + width, (max_y - min_y + height) * relative);
    if (side < std::sqrt(width * height) * 2.20)
        return false;
    coherent_count = static_cast<int>(coherent.size());
    for (double target : {544., 512., 576.})
        add_scale(candidates, target, side, relative);
    if (coherent.size() >= 3)
    {
        add_scale(candidates, 544, side, 1);
        add_scale(candidates, 544, side, relative * .80);
        add_scale(candidates, 544, side, relative * 1.25);
        add_scale(candidates, 512, side, 1);
        add_scale(candidates, 576, side, 1);
    }
    return !candidates.empty();
}

std::vector<Scale> local_scales(const Region &r)
{
    // 局部码区已经排除了长条 ROI 中的大量无关背景，因此使用更小的目标边长即可
    // 保留模块结构。靠近图像边界、需要补静区的候选允许额外一组尺度。
    std::vector<Scale> result;
    for (double relative : {r.relative_y * (r.padded() ? 1.30 : 1.15), r.relative_y})
        for (double target : {300., 320., 280.})
        {
            add_scale(result, target, r.side, relative);
            if (result.size() >= 7)
                return result;
        }
    if (r.padded())
        add_scale(result, 576, r.side, r.relative_y * .70);
    return result;
}

bool candidate_region(const cv::Mat &source, Region &region, bool low_contrast)
{
    if (source.empty() || source.cols < 64 || source.rows < 64)
        return false;
    cv::Mat blurred, binary, input = source;
    if (low_contrast)
    {
        // 低对比模式只为经典定位器生成较稳定的几何输入；后续解码仍使用原灰度像素。
        cv::GaussianBlur(source, blurred, {5, 5}, 0);
        cv::threshold(blurred, binary, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
        input = binary;
    }
    std::vector<cv::Point2f> points;
    // 定位器只提供几何，不负责业务文本；可选定位断言按无候选处理。
    try
    {
        cv::QRCodeDetector locator;
        if (!locator.detect(input, points))
            return false;
    }
    catch (const cv::Exception &)
    {
        return false;
    }
    if (points.size() < 4)
        return false;
    double x0 = DBL_MAX, y0 = DBL_MAX, x1 = -DBL_MAX, y1 = -DBL_MAX;
    for (auto p : points)
    {
        if (!std::isfinite(p.x) || !std::isfinite(p.y))
            return false;
        x0 = std::min(x0, static_cast<double>(p.x));
        y0 = std::min(y0, static_cast<double>(p.y));
        x1 = std::max(x1, static_cast<double>(p.x));
        y1 = std::max(y1, static_cast<double>(p.y));
    }
    double width = x1 - x0, height = y1 - y0;
    if (width < 32 || height < 32)
        return false;
    // 在定位四边形外预留约 20% 静区。若理论静区越过传感器边界，记录所需补白量；
    // 不扩大原始 ROI 访问范围，也不会生成二维码的数据模块。
    int quiet = std::max(8, round_even(std::max(width, height) * .20));
    int raw_left = static_cast<int>(std::floor(x0)) - quiet, raw_top = static_cast<int>(std::floor(y0)) - quiet;
    int raw_right = static_cast<int>(std::ceil(x1)) + quiet, raw_bottom = static_cast<int>(std::ceil(y1)) + quiet;
    int raw_width = raw_right - raw_left, raw_height = raw_bottom - raw_top;
    if (raw_width < 64 || raw_height < 64 || raw_width > source.cols * 2 || raw_height > source.rows * 2)
        return false;
    int left = std::max(0, raw_left), top = std::max(0, raw_top), right = std::min(source.cols, raw_right),
        bottom = std::min(source.rows, raw_bottom);
    if (right - left < 64 || bottom - top < 64)
        return false;
    region = {{left, top, right - left, bottom - top},
              left - raw_left,
              top - raw_top,
              raw_right - right,
              raw_bottom - bottom,
              std::max(width, height),
              clamp(width / height, .55, 1.80)};
    return true;
}

Quad ordered(const Quad &points)
{
    // 统一为左上、右上、右下、左下，供透视展开和坐标恢复共同使用。
    Quad result{points[0], points[0], points[0], points[0]};
    double min_sum = DBL_MAX, max_sum = -DBL_MAX, min_diff = DBL_MAX, max_diff = -DBL_MAX;
    for (auto p : points)
    {
        double sum = p.x + p.y, diff = p.x - p.y;
        if (sum < min_sum)
        {
            min_sum = sum;
            result[0] = p;
        }
        if (sum > max_sum)
        {
            max_sum = sum;
            result[2] = p;
        }
        if (diff > max_diff)
        {
            max_diff = diff;
            result[1] = p;
        }
        if (diff < min_diff)
        {
            min_diff = diff;
            result[3] = p;
        }
    }
    return result;
}

std::vector<Perspective> perspective_candidates(const std::vector<Finder> &evidence, int ox, int oy, int width,
                                                int height)
{
    // 从最多 12 个弱证据中枚举三点组，通过“尺寸一致 + 直角 + 两腿近似等长”筛选。
    // 只保留评分最好的 3 个候选，限制误检风险和透视解码耗时。
    std::vector<Perspective> result;
    int count = std::min(static_cast<int>(evidence.size()), 12);
    for (int a = 0; a < count - 2; ++a)
        for (int b = a + 1; b < count - 1; ++b)
            for (int c = b + 1; c < count; ++c)
            {
                std::array<Finder, 3> triple{evidence[a], evidence[b], evidence[c]};
                double small = std::min({triple[0].side, triple[1].side, triple[2].side});
                double large = std::max({triple[0].side, triple[1].side, triple[2].side});
                if (small <= 0 || large / small > 1.50)
                    continue;
                int corner = -1;
                double best = DBL_MAX, leg1 = 0, leg2 = 0;
                for (int i = 0; i < 3; ++i)
                {
                    auto origin = center(triple[i], ox, oy);
                    auto v1 = center(triple[(i + 1) % 3], ox, oy) - origin,
                         v2 = center(triple[(i + 2) % 3], ox, oy) - origin;
                    double l1 = std::sqrt(static_cast<double>(v1.x * v1.x + v1.y * v1.y)),
                           l2 = std::sqrt(static_cast<double>(v2.x * v2.x + v2.y * v2.y));
                    if (l1 <= 1 || l2 <= 1)
                        continue;
                    double cosine = std::abs((v1.x * v2.x + v1.y * v2.y) / (l1 * l2));
                    if (cosine >= best)
                        continue;
                    best = cosine;
                    corner = i;
                    leg1 = l1;
                    leg2 = l2;
                }
                if (corner < 0 || best > .25 || std::max(leg1, leg2) / std::min(leg1, leg2) > 1.35)
                    continue;
                auto origin = center(triple[corner], ox, oy);
                int first = (corner + 1) % 3, second = (corner + 2) % 3;
                int h = std::abs(triple[first].x - triple[corner].x) >= std::abs(triple[second].x - triple[corner].x)
                            ? first
                            : second;
                int v = h == first ? second : first;
                std::array<double, 3> sizes{triple[0].side, triple[1].side, triple[2].side};
                std::sort(sizes.begin(), sizes.end());
                // 定位框固定占 7 个模块。由定位框中心间距反推二维码模块数，
                // 并吸附到 QR 规范中的 21 + 4 * version 合法尺寸。
                double estimate = (leg1 + leg2) * .5 / (sizes[1] / 7.0) + 7.0;
                int modules = 21 + 4 * std::max(0, round_even((estimate - 21.0) / 4.0));
                if (modules > 177 || std::abs(modules - estimate) > 2.25)
                    continue;
                // 这里除以 int，保留原 Point2f 单精度运算；不得改为提前使用 double。
                auto horizontal = center(triple[h], ox, oy), vertical = center(triple[v], ox, oy);
                float span = static_cast<float>(modules - 7);
                cv::Point2f x((origin.x - horizontal.x) / span, (origin.y - horizontal.y) / span);
                cv::Point2f y((origin.x - vertical.x) / span, (origin.y - vertical.y) / span);
                double far = modules - 3.5;
                auto corners = ordered(
                    {add(origin, scaled(x, -far), scaled(y, -far)), add(origin, scaled(x, 3.5), scaled(y, -far)),
                     add(origin, scaled(x, 3.5), scaled(y, 3.5)), add(origin, scaled(x, -far), scaled(y, 3.5))});
                double outside = std::max(width, height) * .15;
                bool invalid = false;
                for (auto p : corners)
                    if (p.x < -outside || p.y < -outside || p.x > width + outside || p.y > height + outside)
                        invalid = true;
                if (invalid)
                    continue;
                result.push_back({corners, modules, best,
                                  best + std::abs(std::log(leg1 / leg2)) + std::abs(std::log(large / small)) +
                                      std::abs(modules - estimate) * .05});
            }
    std::sort(result.begin(), result.end(),
              [](const Perspective &a, const Perspective &b) { return a.score < b.score; });
    if (result.size() > 3)
        result.resize(3);
    return result;
}

Result from_hit(const Hit &hit, double x, double y, double sx, double sy, bool positive)
{
    // 撤销工作图缩放，保留输入图浮点坐标；positive 交由业务出口在加回父图偏移后夹零。
    return {true,
            x,
            y,
            hit.width / sx,
            hit.height / sy,
            hit.text,
            {}, positive};
}
Result from_region(const Hit &hit, const Region &r, double sx, double sy)
{
    // 恢复到本次输入图；外部父图偏移由调用者加回，取整与夹零也延迟到父图空间。
    return from_hit(hit, r.roi.x + hit.x / sx - r.left, r.roi.y + hit.y / sy - r.top, sx, sy, true);
}
Result from_corners(const Hit &hit, const Quad &c, bool float_sum)
{
    // 透视展开后的检测框仅用于证明解码成功；位置和物理尺寸应由原图四角计算，
    // 否则会把标准化工作图的大小误当成输入图上真实的二维码尺寸。
    double x = 0, y = 0;
    if (float_sum)
    {
        x = (c[0].x + c[1].x + c[2].x + c[3].x) * .25;
        y = (c[0].y + c[1].y + c[2].y + c[3].y) * .25;
    }
    else
    {
        for (auto p : c)
        {
            x += p.x;
            y += p.y;
        }
        x /= 4;
        y /= 4;
    }
    return {true,
            x,
            y,
            (distance(c[0], c[1]) + distance(c[3], c[2])) * .5,
            (distance(c[0], c[3]) + distance(c[1], c[2])) * .5,
            hit.text,
            {}, true, true};
}
} // namespace qr
