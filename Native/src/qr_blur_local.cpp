#include "qr_detector.h"

namespace qr
{
namespace
{
// 只供严重失焦恢复使用。三个定位框先通过模板和三角形几何确认，
// 再测量每个定位框外边界；不会根据预期文本补点、填黑块或修改数据模块。
bool finder_outline(const cv::Mat &source, const BlurFinder &finder, Quad &outline)
{
    const int radius = std::max(12, round_even(finder.module * 6));
    cv::Rect roi(round_even(finder.center.x) - radius, round_even(finder.center.y) - radius,
                 radius * 2, radius * 2);
    roi &= cv::Rect(0, 0, source.cols, source.rows);
    if (roi.width < finder.module * 8 || roi.height < finder.module * 8)
        return false;
    cv::Mat binary;
    cv::threshold(source(roi), binary, 0, 255, cv::THRESH_BINARY | cv::THRESH_OTSU);
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(binary, contours, cv::RETR_TREE, cv::CHAIN_APPROX_SIMPLE);
    double best_area = 0;
    for (const auto &contour : contours)
    {
        auto box = cv::boundingRect(contour);
        double module = finder.module;
        cv::Point2f center(roi.x + box.x + box.width * .5f, roi.y + box.y + box.height * .5f);
        // 外定位框约为 7 模块，既排除内层小方框，也排除与周边图案粘连的大轮廓。
        if (box.width <= module * 4 || box.height <= module * 4 ||
            box.width >= module * 10 || box.height >= module * 10 ||
            std::abs(center.x - finder.center.x) >= module || std::abs(center.y - finder.center.y) >= module)
            continue;
        std::vector<cv::Point> polygon;
        cv::approxPolyDP(contour, polygon, cv::arcLength(contour, true) * .03, true);
        double area = cv::contourArea(contour);
        if (polygon.size() != 4 || !cv::isContourConvex(polygon) || area <= best_area)
            continue;
        best_area = area;
        for (size_t i = 0; i < 4; ++i)
            outline[i] = cv::Point2f(static_cast<float>(polygon[i].x + roi.x),
                                     static_cast<float>(polygon[i].y + roi.y));
    }
    return best_area > 0;
}

// 逆映射：逻辑码区的 (u,v) -> 原图像素坐标。坐标归一到 0..1 后再拟合，
// 避免不同分辨率引起矩阵病态；六项只描述平滑几何变形，不学习图案灰度。
using Coefficients = cv::Matx<double, 6, 2>;
cv::Point2f map_point(const Coefficients &c, double u, double v)
{
    const double basis[] = {1, u, v, u * v, u * u, v * v};
    double x = 0, y = 0;
    for (int i = 0; i < 6; ++i) { x += basis[i] * c(i, 0); y += basis[i] * c(i, 1); }
    return {static_cast<float>(x), static_cast<float>(y)};
}

bool fit_code_map(const BlurTriple &triple, const std::array<Quad, 3> &outlines,
                  int modules, cv::Size image_size, Coefficients &coefficients, Quad &corners)
{
    const auto axis_x = triple.first.center - triple.corner.center;
    const auto axis_y = triple.second.center - triple.corner.center;
    const double determinant = axis_x.x * axis_y.y - axis_x.y * axis_y.x;
    if (std::abs(determinant) < 1)
        return false;
    const std::array<BlurFinder, 3> finders{{triple.corner, triple.first, triple.second}};
    const std::array<cv::Point2d, 3> origins{{{0, 0}, {double(modules - 7), 0}, {0, double(modules - 7)}}};
    cv::Mat design(12, 6, CV_64F), observed(12, 2, CV_64F);
    for (int f = 0; f < 3; ++f)
    {
        int quadrants = 0;
        for (int p = 0; p < 4; ++p)
        {
            auto delta = outlines[f][p] - finders[f].center;
            // 以定位框三角形的两条轴分配四个角，而非按画面“左上”排序。
            // 因此旋转 90/180 度、镜像时仍落在相同的 QR 逻辑坐标。
            bool right = (delta.x * axis_y.y - delta.y * axis_y.x) / determinant > 0;
            bool bottom = (axis_x.x * delta.y - axis_x.y * delta.x) / determinant > 0;
            int quadrant = (right ? 1 : 0) + (bottom ? 2 : 0);
            if (quadrants & (1 << quadrant)) return false;
            quadrants |= 1 << quadrant;
            double u = (origins[f].x + (right ? 7 : 0)) / modules;
            double v = (origins[f].y + (bottom ? 7 : 0)) / modules;
            const double basis[] = {1, u, v, u * v, u * u, v * v};
            int row = f * 4 + p;
            for (int i = 0; i < 6; ++i) design.at<double>(row, i) = basis[i];
            observed.at<double>(row, 0) = outlines[f][p].x;
            observed.at<double>(row, 1) = outlines[f][p].y;
        }
    }
    cv::Mat solution;
    if (!cv::solve(design, observed, solution, cv::DECOMP_SVD)) return false;
    for (int i = 0; i < 6; ++i)
        for (int j = 0; j < 2; ++j) coefficients(i, j) = solution.at<double>(i, j);
    cv::Mat errors = design * solution - observed;
    double module = (triple.corner.module + triple.first.module + triple.second.module) / 3;
    if (cv::norm(errors, cv::NORM_L2) / std::sqrt(12.) > module * .5 ||
        cv::norm(errors, cv::NORM_INF) > module)
        return false;

    // 第四角没有定位框，属于有限外推。检查整个码区的 Jacobian，禁止翻折、
    // 非合理的尺度变化或越界采样；没有足够几何证据时宁可返回未识别。
    double base = determinant * modules * modules / ((modules - 7.) * (modules - 7.));
    for (int row = 0; row <= 8; ++row)
        for (int col = 0; col <= 8; ++col)
        {
            double u = col / 8., v = row / 8.;
            auto point = map_point(coefficients, u, v);
            if (!std::isfinite(point.x) || !std::isfinite(point.y) || point.x < 0 || point.y < 0 ||
                point.x >= image_size.width || point.y >= image_size.height) return false;
            double xu = coefficients(1, 0) + coefficients(3, 0) * v + 2 * coefficients(4, 0) * u;
            double yu = coefficients(1, 1) + coefficients(3, 1) * v + 2 * coefficients(4, 1) * u;
            double xv = coefficients(2, 0) + coefficients(3, 0) * u + 2 * coefficients(5, 0) * v;
            double yv = coefficients(2, 1) + coefficients(3, 1) * u + 2 * coefficients(5, 1) * v;
            double ratio = (xu * yv - xv * yu) / base;
            if (ratio < .35 || ratio > 2.5) return false;
        }
    corners = ordered({map_point(coefficients, 0, 0), map_point(coefficients, 1, 0),
                       map_point(coefficients, 1, 1), map_point(coefficients, 0, 1)});
    return true;
}

cv::Rect local_code_region(const BlurTriple &triple, int modules, cv::Size size)
{
    auto x = scaled(triple.first.center - triple.corner.center, 1. / (modules - 7.));
    auto y = scaled(triple.second.center - triple.corner.center, 1. / (modules - 7.));
    double far = modules - 3.5;
    std::vector<cv::Point2f> corners{
        add(triple.corner.center, scaled(x, -3.5), scaled(y, -3.5)),
        add(triple.corner.center, scaled(x, far), scaled(y, -3.5)),
        add(triple.corner.center, scaled(x, far), scaled(y, far)),
        add(triple.corner.center, scaled(x, -3.5), scaled(y, far))};
    auto roi = cv::boundingRect(corners);
    int margin = std::max(roi.width, roi.height) / 5;
    return cv::Rect(roi.x - margin, roi.y - margin, roi.width + 2 * margin, roi.height + 2 * margin) &
           cv::Rect(0, 0, size.width, size.height);
}
} // namespace

bool QrDetector::blurred_local(const cv::Mat &search, const cv::Mat &red,
                              const std::vector<BlurTriple> &triples, Result &result)
{
    // 新恢复路径只接收前面已确认的至多两个三定位框候选。正常码和无码图不会
    // 执行额外的全图缩放枚举；整个分支最多增加 4 次 WeChatQRCode 调用。
    int attempts = 0;
    const cv::Mat &channel = red.empty() ? search : red;
    for (const auto &triple : triples)
    {
        if (attempts >= 4) break;
        cv::Mat input = channel, polarity;
        if (triple.inverted) { cv::bitwise_not(channel, polarity); input = polarity; }
        int version = std::max(0, std::min(9, round_even((triple.dimension - 21.) / 4.)));
        // 失焦常高估模块宽度/版本。与既有恢复一致，按相邻的三个合法版本核验。
        int first_modules = 21 + std::max(0, version - 1) * 4;
        auto roi = local_code_region(triple, first_modules, input.size());
        if (roi.width < 64 || roi.height < 64) continue;
        Hit hit;
        ++attempts;
        // 先保留原始局部像素，让 WeChat 自己定位；544 是有限工作尺寸而非样本
        // 专属缩放比例。从局部图解出后，严格撤销重采样及 ROI 偏移。
        if (decode_xy(input(roi), 544. / roi.width, 544. / roi.height, hit))
        {
            result = from_hit(hit, hit.x + roi.x, hit.y + roi.y);
            result.strategy = "WeChatQRCode, blurred-local-crop, channel=" + std::string(red.empty() ? "gray" : "red") +
                              ", targetSide=544, finderCount=3";
            return true;
        }

        std::array<Quad, 3> outlines;
        if (!finder_outline(input, triple.corner, outlines[0]) || !finder_outline(input, triple.first, outlines[1]) ||
            !finder_outline(input, triple.second, outlines[2])) continue;
        for (int shift : {-1, 0, 1})
        {
            int v = version + shift;
            if (v < 0 || v > 9 || attempts >= 4) continue;
            int modules = 21 + v * 4, side = modules * 24;
            Coefficients coefficients;
            Quad corners;
            if (!fit_code_map(triple, outlines, modules, input.size(), coefficients, corners)) continue;
            cv::Mat maps(side, side, CV_32FC2);
            for (int y = 0; y < side; ++y)
            {
                auto row = maps.ptr<cv::Vec2f>(y);
                for (int x = 0; x < side; ++x)
                {
                    auto p = map_point(coefficients, x / double(side), y / double(side));
                    row[x] = {p.x, p.y};
                }
            }
            cv::Mat straight, padded;
            cv::remap(input, straight, maps, cv::Mat(), cv::INTER_CUBIC, cv::BORDER_REPLICATE);
            double structure = 0;
            // 逻辑坐标中定位框固定在 TL/TR/BL，因此不再依赖输入画面的朝向。
            // 这里只查定位与时序结构；仍必须由解码器通过格式/纠错校验并输出文本。
            if (!module_structure(straight, modules, structure)) continue;
            cv::copyMakeBorder(straight, padded, 96, 96, 96, 96, cv::BORDER_CONSTANT, cv::Scalar(255));
            ++attempts;
            if (!decode(padded, 1, hit)) continue;
            result = from_corners(hit, corners, false);
            result.strategy = "WeChatQRCode, blurred-finder-local-warp, channel=" + std::string(red.empty() ? "gray" : "red") +
                              ", finderCount=3, moduleCount=" + std::to_string(modules) +
                              ", structure=" + fixed(structure, 3);
            return true;
        }
    }
    return false;
}
} // namespace qr
