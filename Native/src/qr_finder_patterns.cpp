#include "qr_detector.h"
#include <cfloat>

// 定位证据层：模板峰、三点几何与模块结构筛选。不会加载模型或直接输出业务文本。
// 恢复调用顺序及 DNN 预算仍由 qr_recovery.cpp / qr_blur_local.cpp 控制。
namespace qr
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
    {
        for (int col = 0; col < 7; ++col)
        {
            if (finder_dark(row, col))
            {
                cv::rectangle(ideal, {col * module, row * module, module, module}, cv::Scalar(0), -1);
            }
        }
    }
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
        {
            break;
        }
        output.push_back({{static_cast<float>((location.x + side * .5) / scale),
                           static_cast<float>((location.y + side * .5) / scale)},
                          module / scale,
                          score,
                          inverted});
        int radius = side / 2, left = std::max(0, location.x - radius), top = std::max(0, location.y - radius);
        int right = std::min(work.cols, location.x + side + radius),
            bottom = std::min(work.rows, location.y + side + radius);
        if (right <= left || bottom <= top)
        {
            break;
        }
        cv::rectangle(work, {left, top, right - left, bottom - top}, cv::Scalar(inverted ? 1 : -1), -1);
    }
}
} // namespace

std::vector<BlurFinder> blurred_evidence(const cv::Mat &source)
{
    // 模板搜索最长边限制在 640 px，既控制多尺度匹配耗时，也使不同输入尺寸下的
    // 模块宽度落入有限模板集合。命中坐标随后除以 scale 恢复到原图。
    double scale = std::min(1., 640. / std::max(source.cols, source.rows));
    cv::Mat image;
    if (scale < 1 - .0001)
    {
        cv::resize(source, image,
                   {std::max(64, round_even(source.cols * scale)), std::max(64, round_even(source.rows * scale))}, 0, 0,
                   cv::INTER_AREA);
    }
    else
    {
        source.copyTo(image);
    }
    std::vector<BlurFinder> raw, distinct;
    // 模板正/负相关同时覆盖黑码与白码，不把整个识别流程执行两遍。
    for (int module : {3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 16, 18})
    {
        if (module * 7 >= image.cols || module * 7 >= image.rows)
        {
            continue;
        }
        cv::Mat templ = finder_template(module), response;
        cv::matchTemplate(image, templ, response, cv::TM_CCOEFF_NORMED);
        collect_peaks(response, module, scale, false, raw);
        collect_peaks(response, module, scale, true, raw);
    }
    std::sort(raw.begin(), raw.end(), [](const BlurFinder &a, const BlurFinder &b) { return a.score > b.score; });
    for (const auto &candidate : raw)
    {
        if (distinct.size() >= 16)
        {
            break;
        }
        bool duplicate = false;
        for (const auto &accepted : distinct)
        {
            if (candidate.inverted != accepted.inverted)
            {
                continue;
            }
            double x = candidate.center.x - accepted.center.x, y = candidate.center.y - accepted.center.y;
            double limit = std::max(candidate.module, accepted.module) * 7 * .45;
            if (x * x + y * y <= limit * limit)
            {
                duplicate = true;
                break;
            }
        }
        if (!duplicate)
        {
            distinct.push_back(candidate);
        }
    }
    return distinct;
}

std::vector<BlurTriple> blurred_triples(const std::vector<BlurFinder> &evidence)
{
    // 单个方框在工业图案中很常见；只有三个同极性、尺寸接近并形成近似等腰直角的峰
    // 才进入码区恢复。最多保留两个最佳组合，避免无码帧产生大量 DNN 调用。
    std::vector<BlurTriple> result;
    for (size_t a = 0; a < evidence.size(); ++a)
    {
        for (size_t b = a + 1; b < evidence.size(); ++b)
        {
            for (size_t c = b + 1; c < evidence.size(); ++c)
            {
                std::array<BlurFinder, 3> t{evidence[a], evidence[b], evidence[c]};
                if (t[0].inverted != t[1].inverted || t[0].inverted != t[2].inverted)
                {
                    continue;
                }
                double small = std::min({t[0].module, t[1].module, t[2].module}),
                       large = std::max({t[0].module, t[1].module, t[2].module});
                if (small <= 0 || large / small > 1.60)
                {
                    continue;
                }
                for (int i = 0; i < 3; ++i)
                {
                    auto v1 = t[(i + 1) % 3].center - t[i].center, v2 = t[(i + 2) % 3].center - t[i].center;
                    double l1 = std::sqrt(static_cast<double>(v1.x * v1.x + v1.y * v1.y)),
                           l2 = std::sqrt(static_cast<double>(v2.x * v2.x + v2.y * v2.y));
                    if (l1 <= large * 8 || l2 <= large * 8)
                    {
                        continue;
                    }
                    double cosine = std::abs((v1.x * v2.x + v1.y * v2.y) / (l1 * l2)),
                           ratio = std::max(l1, l2) / std::min(l1, l2);
                    if (cosine > .28 || ratio > 1.35)
                    {
                        continue;
                    }
                    std::array<double, 3> sizes{t[0].module, t[1].module, t[2].module};
                    std::sort(sizes.begin(), sizes.end());
                    // 用定位框中心距离/模块宽度估算二维码总模块数，供后续枚举合法版本。
                    double dimension = (l1 + l2) * .5 / sizes[1] + 7;
                    if (dimension < 17 || dimension > 65)
                    {
                        continue;
                    }
                    double average = (t[0].score + t[1].score + t[2].score) / 3;
                    result.push_back(
                        {t[i], t[(i + 1) % 3], t[(i + 2) % 3], t[i].inverted, cosine, dimension, average,
                         cosine + std::abs(std::log(ratio)) + std::abs(std::log(large / small)) + (1 - average)});
                }
            }
        }
    }
    std::sort(result.begin(), result.end(), [](const BlurTriple &a, const BlurTriple &b) { return a.score < b.score; });
    if (result.size() > 2)
    {
        result.resize(2);
    }
    return result;
}

bool module_structure(const cv::Mat &source, int count, double &score)
{
    // 在真正调用较慢的 DNN 前，将透视图压成 count x count 的模块网格，验证：
    // 1) 三个 7x7 定位框；2) 第 6 行/列的黑白交替时序图案。
    // 该门控只检查 QR 通用结构，不包含任何业务文本或样本编号。
    score = 0;
    if (source.empty() || source.channels() != 1 || count < 21)
    {
        return false;
    }
    cv::Mat grid;
    cv::resize(source, grid, {count, count}, 0, 0, cv::INTER_AREA);
    const std::array<cv::Point, 3> origins{{{0, 0}, {count - 7, 0}, {0, count - 7}}};
    double dark = 0, light = 0;
    int dark_count = 0, light_count = 0;
    for (auto origin : origins)
    {
        for (int row = 0; row < 7; ++row)
        {
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
        }
    }
    double dark_mean = dark / std::max(1, dark_count), light_mean = light / std::max(1, light_count);
    if (light_mean - dark_mean < 12)
    {
        return false;
    }
    double threshold = (dark_mean + light_mean) * .5;
    std::array<double, 3> agreements;
    for (size_t f = 0; f < 3; ++f)
    {
        int correct = 0;
        auto p = origins[f];
        for (int row = 0; row < 7; ++row)
        {
            for (int col = 0; col < 7; ++col)
            {
                if ((grid.at<uint8_t>(p.y + row, p.x + col) <= threshold) == finder_dark(row, col))
                {
                    ++correct;
                }
            }
        }
        agreements[f] = correct / 49.;
    }
    std::sort(agreements.begin(), agreements.end());
    if (agreements[1] < .68)
    {
        return false; // 允许一个定位框局部缺失或反光，但至少两个必须可信。
    }
    int end = count - 9, horizontal = 0, vertical = 0;
    if (end < 8)
    {
        return false;
    }
    for (int i = 8; i <= end; ++i)
    {
        bool expected = ((i - 8) & 1) == 0;
        if ((grid.at<uint8_t>(6, i) <= threshold) == expected)
        {
            ++horizontal;
        }
        if ((grid.at<uint8_t>(i, 6) <= threshold) == expected)
        {
            ++vertical;
        }
    }
    double timing = (horizontal / static_cast<double>(end - 7) + vertical / static_cast<double>(end - 7)) * .5;
    if (timing < .65)
    {
        return false;
    }
    score = (agreements[0] + agreements[1] + agreements[2]) / 3 * .70 + timing * .30;
    return true;
}
} // namespace qr
