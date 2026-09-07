#include "qr_detector.h"
#include <cfloat>

namespace cis
{
namespace
{
// QR 定位框的标准 7x7 模块：外黑框、内白框和中心 3x3 黑块。
bool finder_dark(int row, int col)
{
    return row == 0 || row == 6 || col == 0 || col == 6 || (row >= 2 && row <= 4 && col >= 2 && col <= 4);
}
cv::Mat finder_template(int module)
{
    // 模板按不同模块宽度生成，并加入与真实失焦相近的轻度模糊。
    // 模板只用于找几何位置，最终文本仍由 WeChatQRCode 解码。
    cv::Mat ideal(module * 7, module * 7, CV_8UC1, cv::Scalar(255)), blurred;
    for (int row = 0; row < 7; ++row)
        for (int col = 0; col < 7; ++col)
            if (finder_dark(row, col))
                cv::rectangle(ideal, {col * module, row * module, module, module}, cv::Scalar(0), -1);
    cv::GaussianBlur(ideal, blurred, {0, 0}, std::max(.8, module * .22));
    return blurred;
}
void collect_peaks(const cv::Mat &response, int module, double scale, bool inverted, std::vector<BlurFinder> &output)
{
    // 在相关图上迭代提取少量局部峰，并屏蔽已取区域，避免一个定位框重复入选。
    // 正相关对应常规黑色定位框，负相关同时覆盖反极性的白色定位框。
    auto work = response.clone();
    int side = module * 7;
    for (int i = 0; i < 5; ++i)
    {
        double low, high;
        cv::Point low_at, high_at;
        cv::minMaxLoc(work, &low, &high, &low_at, &high_at);
        double score = inverted ? -low : high;
        auto location = inverted ? low_at : high_at;
        if (score < .60)
            break;
        output.push_back({{static_cast<float>((location.x + side * .5) / scale),
                           static_cast<float>((location.y + side * .5) / scale)},
                          module / scale,
                          score,
                          inverted});
        int radius = side / 2, left = std::max(0, location.x - radius), top = std::max(0, location.y - radius);
        int right = std::min(work.cols, location.x + side + radius),
            bottom = std::min(work.rows, location.y + side + radius);
        if (right <= left || bottom <= top)
            break;
        cv::rectangle(work, {left, top, right - left, bottom - top}, cv::Scalar(inverted ? 1 : -1), -1);
    }
}
Quad blur_corners(const BlurTriple &t, int modules)
{
    // 三个定位框中心分别位于二维码边界内 3.5 个模块处，据此外推完整码区四角。
    auto x = scaled(t.first.center - t.corner.center, 1.0 / (modules - 7.0));
    auto y = scaled(t.second.center - t.corner.center, 1.0 / (modules - 7.0));
    double far = modules - 3.5;
    return ordered(
        {add(t.corner.center, scaled(x, -3.5), scaled(y, -3.5)), add(t.corner.center, scaled(x, far), scaled(y, -3.5)),
         add(t.corner.center, scaled(x, far), scaled(y, far)), add(t.corner.center, scaled(x, -3.5), scaled(y, far))});
}
Quad expanded(const Quad &points, double scale)
{
    // 对四角做极小范围的中心缩放，补偿失焦条件下模板峰对真实模块边界的轻微偏差。
    float x = 0, y = 0;
    for (auto p : points)
    {
        x += p.x;
        y += p.y;
    }
    x /= 4;
    y /= 4;
    Quad result;
    for (size_t i = 0; i < 4; ++i)
        result[i] = {x + static_cast<float>((points[i].x - x) * scale),
                     y + static_cast<float>((points[i].y - y) * scale)};
    return result;
}
bool plausible(const Quad &c, int width, int height)
{
    // 只允许近似方形且最多轻微越过图像边界的码区，过滤重复纹理产生的离谱三点组。
    double w = (distance(c[0], c[1]) + distance(c[3], c[2])) * .5,
           h = (distance(c[0], c[3]) + distance(c[1], c[2])) * .5;
    if (w < 96 || h < 96 || std::max(w, h) / std::min(w, h) > 1.50)
        return false;
    double outside = std::max(width, height) * .15;
    for (auto p : c)
        if (p.x < -outside || p.y < -outside || p.x > width + outside || p.y > height + outside)
            return false;
    return true;
}
} // namespace

std::vector<BlurFinder> blurred_evidence(const cv::Mat &source)
{
    // 模板搜索最长边限制在 640 px，既控制多尺度匹配耗时，也使不同输入尺寸下的
    // 模块宽度落入有限模板集合。命中坐标随后除以 scale 恢复到原图。
    double scale = std::min(1., 640. / std::max(source.cols, source.rows));
    cv::Mat image;
    if (scale < 1 - .0001)
        cv::resize(source, image,
                   {std::max(64, round_even(source.cols * scale)), std::max(64, round_even(source.rows * scale))}, 0, 0,
                   cv::INTER_AREA);
    else
        source.copyTo(image);
    std::vector<BlurFinder> raw, distinct;
    // 模板正/负相关同时覆盖黑码与白码，不把整个识别流程执行两遍。
    for (int module : {3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 16, 18})
    {
        if (module * 7 >= image.cols || module * 7 >= image.rows)
            continue;
        cv::Mat templ = finder_template(module), response;
        cv::matchTemplate(image, templ, response, cv::TM_CCOEFF_NORMED);
        collect_peaks(response, module, scale, false, raw);
        collect_peaks(response, module, scale, true, raw);
    }
    std::sort(raw.begin(), raw.end(), [](const BlurFinder &a, const BlurFinder &b) { return a.score > b.score; });
    for (const auto &candidate : raw)
    {
        if (distinct.size() >= 16)
            break;
        bool duplicate = false;
        for (const auto &accepted : distinct)
        {
            if (candidate.inverted != accepted.inverted)
                continue;
            double x = candidate.center.x - accepted.center.x, y = candidate.center.y - accepted.center.y;
            double limit = std::max(candidate.module, accepted.module) * 7 * .45;
            if (x * x + y * y <= limit * limit)
            {
                duplicate = true;
                break;
            }
        }
        if (!duplicate)
            distinct.push_back(candidate);
    }
    return distinct;
}

std::vector<BlurTriple> blurred_triples(const std::vector<BlurFinder> &evidence)
{
    // 单个方框在工业图案中很常见；只有三个同极性、尺寸接近并形成近似等腰直角的峰
    // 才进入码区恢复。最多保留两个最佳组合，避免无码帧产生大量 DNN 调用。
    std::vector<BlurTriple> result;
    for (size_t a = 0; a < evidence.size(); ++a)
        for (size_t b = a + 1; b < evidence.size(); ++b)
            for (size_t c = b + 1; c < evidence.size(); ++c)
            {
                std::array<BlurFinder, 3> t{evidence[a], evidence[b], evidence[c]};
                if (t[0].inverted != t[1].inverted || t[0].inverted != t[2].inverted)
                    continue;
                double small = std::min({t[0].module, t[1].module, t[2].module}),
                       large = std::max({t[0].module, t[1].module, t[2].module});
                if (small <= 0 || large / small > 1.60)
                    continue;
                for (int i = 0; i < 3; ++i)
                {
                    auto v1 = t[(i + 1) % 3].center - t[i].center, v2 = t[(i + 2) % 3].center - t[i].center;
                    double l1 = std::sqrt(static_cast<double>(v1.x * v1.x + v1.y * v1.y)),
                           l2 = std::sqrt(static_cast<double>(v2.x * v2.x + v2.y * v2.y));
                    if (l1 <= large * 8 || l2 <= large * 8)
                        continue;
                    double cosine = std::abs((v1.x * v2.x + v1.y * v2.y) / (l1 * l2)),
                           ratio = std::max(l1, l2) / std::min(l1, l2);
                    if (cosine > .28 || ratio > 1.35)
                        continue;
                    std::array<double, 3> sizes{t[0].module, t[1].module, t[2].module};
                    std::sort(sizes.begin(), sizes.end());
                    // 用定位框中心距离/模块宽度估算二维码总模块数，供后续枚举合法版本。
                    double dimension = (l1 + l2) * .5 / sizes[1] + 7;
                    if (dimension < 17 || dimension > 65)
                        continue;
                    double average = (t[0].score + t[1].score + t[2].score) / 3;
                    result.push_back(
                        {t[i], t[(i + 1) % 3], t[(i + 2) % 3], t[i].inverted, cosine, dimension, average,
                         cosine + std::abs(std::log(ratio)) + std::abs(std::log(large / small)) + (1 - average)});
                }
            }
    std::sort(result.begin(), result.end(), [](const BlurTriple &a, const BlurTriple &b) { return a.score < b.score; });
    if (result.size() > 2)
        result.resize(2);
    return result;
}

bool module_structure(const cv::Mat &source, int count, double &score)
{
    // 在真正调用较慢的 DNN 前，将透视图压成 count x count 的模块网格，验证：
    // 1) 三个 7x7 定位框；2) 第 6 行/列的黑白交替时序图案。
    // 该门控只检查 QR 通用结构，不包含任何业务文本或样本编号。
    score = 0;
    if (source.empty() || source.channels() != 1 || count < 21)
        return false;
    cv::Mat grid;
    cv::resize(source, grid, {count, count}, 0, 0, cv::INTER_AREA);
    const std::array<cv::Point, 3> origins{{{0, 0}, {count - 7, 0}, {0, count - 7}}};
    double dark = 0, light = 0;
    int dark_count = 0, light_count = 0;
    for (auto origin : origins)
        for (int row = 0; row < 7; ++row)
            for (int col = 0; col < 7; ++col)
            {
                auto value = grid.at<uint8_t>(origin.y + row, origin.x + col);
                if (finder_dark(row, col))
                {
                    dark += value;
                    ++dark_count;
                }
                else
                {
                    light += value;
                    ++light_count;
                }
            }
    double dark_mean = dark / std::max(1, dark_count), light_mean = light / std::max(1, light_count);
    if (light_mean - dark_mean < 12)
        return false;
    double threshold = (dark_mean + light_mean) * .5;
    std::array<double, 3> agreements;
    for (size_t f = 0; f < 3; ++f)
    {
        int correct = 0;
        auto p = origins[f];
        for (int row = 0; row < 7; ++row)
            for (int col = 0; col < 7; ++col)
                if ((grid.at<uint8_t>(p.y + row, p.x + col) <= threshold) == finder_dark(row, col))
                    ++correct;
        agreements[f] = correct / 49.;
    }
    std::sort(agreements.begin(), agreements.end());
    if (agreements[1] < .68)
        return false; // 允许一个定位框局部缺失或反光，但至少两个必须可信。
    int end = count - 9, horizontal = 0, vertical = 0;
    if (end < 8)
        return false;
    for (int i = 8; i <= end; ++i)
    {
        bool expected = ((i - 8) & 1) == 0;
        if ((grid.at<uint8_t>(6, i) <= threshold) == expected)
            ++horizontal;
        if ((grid.at<uint8_t>(i, 6) <= threshold) == expected)
            ++vertical;
    }
    double timing = (horizontal / static_cast<double>(end - 7) + vertical / static_cast<double>(end - 7)) * .5;
    if (timing < .65)
        return false;
    score = (agreements[0] + agreements[1] + agreements[2]) / 3 * .70 + timing * .30;
    return true;
}

bool QrDetector::perspective(const cv::Mat &source, cv::Rect evidence_roi, int offset, Result &result)
{
    auto safe = evidence_roi & cv::Rect(0, 0, source.cols, source.rows);
    if (safe.width < 64 || safe.height < 64)
        return false;
    cv::Mat view = source(safe), background, flattened;
    // 大范围亮度不均会破坏 Otsu 轮廓层级。背景除法展平仅用于寻找三个定位框；
    // 真正的二维码数据模块始终从原灰度图透视展开，避免展平引入纹理伪影。
    cv::GaussianBlur(view, background, {0, 0}, 18.0);
    cv::divide(view, background, flattened, 255.0);
    auto candidates = perspective_candidates(finder_evidence(flattened), safe.x, safe.y, source.cols, source.rows);
    for (const auto &candidate : candidates)
    {
        // 每模块 12 px，并在四周补足标准 4 模块静区，让解码器获得稳定采样条件。
        int side = candidate.modules * 12, quiet = 4 * 12;
        Quad dst{{{0, 0},
                  {static_cast<float>(side - 1), 0},
                  {static_cast<float>(side - 1), static_cast<float>(side - 1)},
                  {0, static_cast<float>(side - 1)}}};
        auto transform = cv::getPerspectiveTransform(candidate.corners.data(), dst.data());
        cv::Mat straight, padded, normalized;
        cv::warpPerspective(source, straight, transform, {side, side}, cv::INTER_CUBIC, cv::BORDER_CONSTANT,
                            cv::Scalar(255));
        cv::copyMakeBorder(straight, padded, quiet, quiet, quiet, quiet, cv::BORDER_CONSTANT, cv::Scalar(255));
        cv::normalize(padded, normalized, 0, 255, cv::NORM_MINMAX);
        Hit hit;
        std::string preprocessing = "gray";
        // 先尝试仅归一化的原始灰度；失败后才进行局部照度展平，保持正常样本路径简单。
        bool decoded = decode(normalized, 1, hit);
        if (!decoded)
        {
            cv::Mat local_background, local_flattened;
            cv::GaussianBlur(normalized, local_background, {0, 0}, 12 * 1.8);
            cv::divide(normalized, local_background, local_flattened, 255.0);
            decoded = decode(local_flattened, 1, hit);
            if (decoded)
                preprocessing = "illumination-flattened";
        }
        if (!decoded)
            continue;
        result = from_corners(hit, candidate.corners, offset, true);
        result.strategy =
            "WeChatQRCode, finder-perspective, finderCount=3, moduleCount=" + std::to_string(candidate.modules) +
            ", rightAngleCosine=" + fixed(candidate.cosine, 3) + ", rectified=" + preprocessing;
        return true;
    }
    return false;
}

bool QrDetector::low_contrast(const cv::Mat &original, int offset, int evidence_count, Result &result)
{
    // 白墨不足可能令二维码前景由白变黑。该分支只在常规极性失败且已有定位框证据时运行，
    // 先用高斯 + Otsu 帮助定位局部码区，再用原始灰度的相反极性解码。
    cv::Mat alternative = original, owned;
    if (!inverted_)
    {
        cv::bitwise_not(original, owned);
        alternative = owned;
    }
    Region region;
    if (!candidate_region(alternative, region, true))
        return false;
    cv::Mat input = alternative(region.roi), padded;
    if (region.padded())
    {
        cv::copyMakeBorder(input, padded, region.top, region.bottom, region.left, region.right, cv::BORDER_CONSTANT,
                           cv::Scalar(255));
        input = padded;
    }
    double x = 224. / region.side, y = x * clamp(region.relative_y * .70, .55, 1.80);
    if (x < .20 || x > 1.75 || y < .20 || y > 1.75)
        return false;
    Hit hit;
    if (!decode_xy(input, x, y, hit))
        return false;
    result = from_region(hit, offset, region);
    result.strategy = "WeChatQRCode, low-contrast-opposite-polarity, finderEvidence=" + std::to_string(evidence_count) +
                      ", locator=gaussian5-otsu, roi=" + std::to_string(region.roi.x) + "," +
                      std::to_string(region.roi.y) + "," + std::to_string(region.roi.width) + "x" +
                      std::to_string(region.roi.height) + ", padding=" + std::to_string(region.left) + "," +
                      std::to_string(region.top) + "," + std::to_string(region.right) + "," +
                      std::to_string(region.bottom) + ", estimatedSide=" + fixed(region.side, 1) +
                      ", targetSide=224.0, geometryScaleY=" + fixed(region.relative_y, 3) + ", scaleX=" + fixed(x, 3) +
                      ", scaleY=" + fixed(y, 3);
    return true;
}

bool QrDetector::blurred_rectified(const cv::Mat &source, const Quad &corners, int modules, bool inverted,
                                   int &attempts, Hit &hit, std::string &preprocessing)
{
    int side = modules * 24, quiet = 4 * 24;
    Quad dst{{{0, 0},
              {static_cast<float>(side), 0},
              {static_cast<float>(side), static_cast<float>(side)},
              {0, static_cast<float>(side)}}};
    auto transform = cv::getPerspectiveTransform(corners.data(), dst.data());
    cv::Mat straight, polarity, padded, normalized;
    // 失焦恢复以模块边界建立变换，不使用 side-1，保留原来的采样相位。
    cv::warpPerspective(source, straight, transform, {side, side}, cv::INTER_CUBIC, cv::BORDER_REPLICATE);
    if (inverted)
        cv::bitwise_not(straight, polarity);
    else
        straight.copyTo(polarity);
    double score;
    // 结构门控放在 DNN 之前；同一模糊候选最多进行 4 次真正解码，约束最坏耗时。
    if (!module_structure(polarity, modules, score) || attempts >= 4)
        return false;
    cv::copyMakeBorder(polarity, padded, quiet, quiet, quiet, quiet, cv::BORDER_CONSTANT, cv::Scalar(255));
    cv::normalize(padded, normalized, 0, 255, cv::NORM_MINMAX);
    ++attempts;
    if (decode(normalized, 1, hit))
    {
        preprocessing = "normalized-gray, structure=" + fixed(score, 3);
        return true;
    }
    if (attempts >= 4)
        return false;
    cv::Mat background, flattened;
    cv::GaussianBlur(normalized, background, {0, 0}, 24 * 1.8);
    cv::divide(normalized, background, flattened, 255.0);
    ++attempts;
    if (!decode(flattened, 1, hit))
        return false;
    preprocessing = "illumination-flattened, structure=" + fixed(score, 3);
    return true;
}

bool QrDetector::blurred(const cv::Mat &source, cv::Rect roi, int offset, Result &result)
{
    if (roi.width < 96 || roi.height < 96)
        return false;
    cv::Mat view = source(roi), search, secondary;
    if (view.channels() == 1)
        view.copyTo(search);
    else
    {
        // 绿色通道用于模板定位，通常具有较稳定的亮度结构；红色通道保留为解码首选，
        // 可减轻部分样本中青色失焦边缘对黑白模块边界的干扰。
        cv::extractChannel(view, search, 1);
        cv::extractChannel(view, secondary, 2);
    }
    cv::Scalar mean, deviation;
    cv::meanStdDev(search, mean, deviation);
    if (deviation[0] < 8)
        return false;
    auto triples = blurred_triples(blurred_evidence(search));
    // 黑码只有通过三个定位框几何确认才尝试原极性拉伸，避免无码帧翻倍解码。
    for (const auto &triple : triples)
        if (!triple.inverted)
        {
            cv::Mat normalized;
            cv::normalize(search, normalized, 0, 255, cv::NORM_MINMAX);
            Hit hit;
            if (decode(normalized, 1, hit))
            {
                result = from_hit(hit, hit.x + offset, hit.y, 1, 1, true);
                result.strategy = "WeChatQRCode, contrast-normalized-original-polarity, templateScore=" +
                                  fixed(triple.average_score, 3) + ", rightAngleCosine=" + fixed(triple.cosine, 3);
                return true;
            }
            break; // 原实现仅使用第一个原极性三元组。
        }
    int attempts = 0;
    for (const auto &triple : triples)
    {
        // 模糊会使模板峰和模块宽度产生轻微偏差，因此围绕估算版本尝试 -1/0/+1，
        // 每个版本仍必须通过合法模块数、几何边界和模块结构三重约束。
        int version = std::max(0, std::min(9, round_even((triple.dimension - 21.0) / 4.0)));
        for (int shift : {-1, 0, 1})
        {
            int v = version + shift;
            if (v < 0 || v > 9)
                continue;
            int modules = 21 + v * 4;
            auto corners = blur_corners(triple, modules);
            if (!plausible(corners, search.cols, search.rows))
                continue;
            // 彩色输入先试红通道、再试用于定位的绿通道；灰度输入只执行一次。
            int channels = secondary.empty() ? 1 : 2;
            for (int channel = 0; channel < channels; ++channel)
            {
                const cv::Mat &input = secondary.empty() ? search : channel == 0 ? secondary : search;
                std::string name = view.channels() == 1 ? "gray" : channel == 0 ? "red" : "green";
                // 极小的边界伸缩用于修正失焦下的采样相位，不改变二维码模块内容。
                for (double expansion : {1.0, .98, 1.035, .97})
                {
                    Hit hit;
                    std::string preprocessing;
                    if (!blurred_rectified(input, expanded(corners, expansion), modules, triple.inverted, attempts, hit,
                                           preprocessing))
                        continue;
                    result = from_corners(hit, corners, offset, false);
                    result.strategy = "WeChatQRCode, blurred-finder-template, channel=" + name +
                                      ", polarity=" + (triple.inverted ? "inverted" : "original") +
                                      ", templateScore=" + fixed(triple.average_score, 3) +
                                      ", rightAngleCosine=" + fixed(triple.cosine, 3) +
                                      ", estimatedDimension=" + fixed(triple.dimension, 1) +
                                      ", moduleCount=" + std::to_string(modules) +
                                      ", expansion=" + fixed(expansion, 3) + ", rectified=" + preprocessing;
                    return true;
                }
            }
        }
    }
    return false;
}
} // namespace cis
