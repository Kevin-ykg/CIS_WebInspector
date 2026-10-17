#include "qr_detector.h"
#include <cfloat>

// 恢复编排：透视、低对比、失焦路径。定位证据的计算见 qr_finder_patterns.cpp。

namespace qr
{
namespace
{
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
    {
        result[i] = {x + static_cast<float>((points[i].x - x) * scale),
                     y + static_cast<float>((points[i].y - y) * scale)};
    }
    return result;
}
bool plausible(const Quad &c, int width, int height)
{
    // 只允许近似方形且最多轻微越过图像边界的码区，过滤重复纹理产生的离谱三点组。
    double w = (distance(c[0], c[1]) + distance(c[3], c[2])) * .5,
           h = (distance(c[0], c[3]) + distance(c[1], c[2])) * .5;
    if (w < 96 || h < 96 || std::max(w, h) / std::min(w, h) > 1.50)
    {
        return false;
    }
    double outside = std::max(width, height) * .15;
    for (auto p : c)
    {
        if (p.x < -outside || p.y < -outside || p.x > width + outside || p.y > height + outside)
        {
            return false;
        }
    }
    return true;
}
} // namespace

bool QrDetector::perspective(const cv::Mat &source, cv::Rect evidence_roi, Result &result)
{
    auto safe = evidence_roi & cv::Rect(0, 0, source.cols, source.rows);
    if (safe.width < 64 || safe.height < 64)
    {
        return false;
    }
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
            {
                preprocessing = "illumination-flattened";
            }
        }
        if (!decoded)
        {
            continue;
        }
        result = from_corners(hit, candidate.corners, true);
        result.strategy =
            "WeChatQRCode, finder-perspective, finderCount=3, moduleCount=" + std::to_string(candidate.modules) +
            ", rightAngleCosine=" + fixed(candidate.cosine, 3) + ", rectified=" + preprocessing;
        return true;
    }
    return false;
}

bool QrDetector::low_contrast(const cv::Mat &original, int evidence_count, Result &result)
{
    // 实际前景可能与调用方优先极性相反。该分支只在常规解码失败且已有定位框证据时运行，
    // 先用高斯 + Otsu 帮助定位局部码区，再用原始灰度的相反极性解码。
    cv::Mat alternative = original, owned;
    if (!inverted_)
    {
        cv::bitwise_not(original, owned);
        alternative = owned;
    }
    Region region;
    if (!candidate_region(alternative, region, true))
    {
        return false;
    }
    cv::Mat input = alternative(region.roi), padded;
    if (region.padded())
    {
        cv::copyMakeBorder(input, padded, region.top, region.bottom, region.left, region.right, cv::BORDER_CONSTANT,
                           cv::Scalar(255));
        input = padded;
    }
    double x = 224. / region.side, y = x * clamp(region.relative_y * .70, .55, 1.80);
    if (x < .20 || x > 1.75 || y < .20 || y > 1.75)
    {
        return false;
    }
    Hit hit;
    if (!decode_xy(input, x, y, hit))
    {
        return false;
    }
    result = from_region(hit, region);
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
    {
        cv::bitwise_not(straight, polarity);
    }
    else
    {
        straight.copyTo(polarity);
    }
    double score;
    // 结构门控放在 DNN 之前；同一模糊候选最多进行 4 次真正解码，约束最坏耗时。
    if (!module_structure(polarity, modules, score) || attempts >= 4)
    {
        return false;
    }
    cv::copyMakeBorder(polarity, padded, quiet, quiet, quiet, quiet, cv::BORDER_CONSTANT, cv::Scalar(255));
    cv::normalize(padded, normalized, 0, 255, cv::NORM_MINMAX);
    ++attempts;
    if (decode(normalized, 1, hit))
    {
        preprocessing = "normalized-gray, structure=" + fixed(score, 3);
        return true;
    }
    if (attempts >= 4)
    {
        return false;
    }
    cv::Mat background, flattened;
    cv::GaussianBlur(normalized, background, {0, 0}, 24 * 1.8);
    cv::divide(normalized, background, flattened, 255.0);
    ++attempts;
    if (!decode(flattened, 1, hit))
    {
        return false;
    }
    preprocessing = "illumination-flattened, structure=" + fixed(score, 3);
    return true;
}

bool QrDetector::blurred(const cv::Mat &source, Result &result)
{
    if (source.cols < 96 || source.rows < 96)
    {
        return false;
    }
    cv::Mat view = source, search, secondary;
    if (view.channels() == 1)
    {
        view.copyTo(search);
    }
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
    {
        return false;
    }
    auto triples = blurred_triples(blurred_evidence(search));
    // 黑码只有通过三个定位框几何确认才尝试原极性拉伸，避免无码帧翻倍解码。
    for (const auto &triple : triples)
    {
        if (!triple.inverted)
        {
            cv::Mat normalized;
            cv::normalize(search, normalized, 0, 255, cv::NORM_MINMAX);
            Hit hit;
            if (decode(normalized, 1, hit))
            {
                result = from_hit(hit, hit.x, hit.y, 1, 1, true);
                result.strategy = "WeChatQRCode, contrast-normalized-original-polarity, templateScore=" +
                                  fixed(triple.average_score, 3) + ", rightAngleCosine=" + fixed(triple.cosine, 3);
                return true;
            }
            break; // 原实现仅使用第一个原极性三元组。
        }
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
            {
                continue;
            }
            int modules = 21 + v * 4;
            auto corners = blur_corners(triple, modules);
            if (!plausible(corners, search.cols, search.rows))
            {
                continue;
            }
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
                    {
                        continue;
                    }
                    result = from_corners(hit, corners, false);
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
    // 保持已有成功路径和输出几何不变；全部失败后，才使用定位框局部边界恢复。
    return blurred_local(search, secondary, triples, result);
}
} // namespace qr
