#include "qr_detector.h"
#include <Windows.h>
#include <fstream>

namespace qr
{
    namespace
    {
        // 保留既有“空/纯空白文本不算命中”的结果策略。仅有检测框不能表示成功解码。
        // 当前读码器面向非空文本标识；若需读取纯空白或任意二进制载荷，应另行定义结果策略。
        bool blank_text(const std::string &text)
        {
            if (text.empty())
                return true;
            int size = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
            if (size <= 0)
                return false;
            std::wstring wide(size, L'\0');
            MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), wide.data(), size);
            for (wchar_t c : wide)
                if (!((c >= 9 && c <= 13) || c == 32 || c == 0x85 || c == 0xa0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200a) ||
                      c == 0x2028 || c == 0x2029 || c == 0x202f || c == 0x205f || c == 0x3000))
                    return false;
            return true;
        }

        // 诊断日志使用统一的 ROI 与四向补白格式，便于复盘边缘二维码采用了哪条恢复路径。
        std::string region_description(const Region &r)
        {
            return std::to_string(r.roi.x) + "," + std::to_string(r.roi.y) + "," + std::to_string(r.roi.width) + "x" +
                   std::to_string(r.roi.height) + ", padding=" + std::to_string(r.left) + "," + std::to_string(r.top) + "," +
                   std::to_string(r.right) + "," + std::to_string(r.bottom);
        }
    } // namespace

    void QrDetector::configure(const Options &options)
    {
        // 配置值在本函数内复制，调用方后续修改 options 不影响已配置的实例。
        inverted_ = options.invert_polarity;
        scales_.clear();
        for (float value : options.scales_y)
        {
            double v = value;
            // 过滤非数值、过度缩放和近似重复项，避免同一候选重复调用耗时较高的 DNN。
            if (!std::isfinite(v) || v < .25 || v > 4)
                continue;
            if (std::none_of(scales_.begin(), scales_.end(), [v](double prior) { return std::abs(prior - v) < .005; }))
                scales_.push_back(v);
        }
        if (scales_.empty())
            scales_.push_back(1);
        // 工作尺度变化后重新执行轻量预热，但现有模型对象仍继续复用。
        warmed_ = false;
    }

    void QrDetector::ensure_detector()
    {
        if (detector_)
            return;
        std::array<std::string, 4> paths;
        const char *files[] = {"detect.prototxt", "detect.caffemodel", "sr.prototxt", "sr.caffemodel"};
        // 在构造 WeChatQRCode 前逐一检查模型，错误信息可直接指出缺少的文件。
        for (size_t i = 0; i < 4; ++i)
        {
            paths[i] = directory_ + "/" + files[i];
            if (!std::ifstream(paths[i], std::ios::binary))
                throw std::runtime_error("Missing WeChatQRCode model: " + paths[i]);
        }
        detector_ = std::make_unique<cv::wechat_qrcode::WeChatQRCode>(paths[0], paths[1], paths[2], paths[3]);
    }
    void QrDetector::initialize(cv::Size working_size)
    {
        attempts_ = 0;
        ensure_detector();
        if (warmed_)
            return;
        // 用不包含二维码的小图触发 DNN 与超分辨率网络的首次初始化。
        // 预热只消除首帧抖动，结果不参与业务判断，attempts 也在结束后清零。
        if (working_size.width <= 0 || working_size.height <= 0)
            throw std::invalid_argument("Warm-up size must be positive");
        cv::Mat blank(working_size, CV_8UC1, cv::Scalar(255));
        Hit ignored;
        for (double scale : scales_)
            decode(blank, scale, ignored);
        attempts_ = 0;
        warmed_ = true;
    }

    bool QrDetector::decode(const cv::Mat &source, double scale_y, Hit &hit)
    {
        // 可选纵向重采样用于压缩/拉伸输入；默认只有 1.0，不假定输入来自线扫设备。
        // 命中后撤销 scaleY，把坐标与尺寸恢复到本次输入图。
        cv::Mat resized, input = source;
        if (std::abs(scale_y - 1) >= .0001)
        {
            cv::resize(source, resized, {}, 1, scale_y, cv::INTER_LINEAR);
            input = resized;
        }
        std::vector<cv::Mat> boxes;
        // attempts 只统计真正进入 WeChatQRCode 的次数，用于定位无二维码帧或困难样本的耗时长尾。
        ++attempts_;
        auto texts = detector_->detectAndDecode(input, boxes);
        for (size_t i = 0; i < std::min(texts.size(), boxes.size()); ++i)
        {
            const auto &box = boxes[i];
            if (blank_text(texts[i]) || box.empty() || box.depth() != CV_32F || box.total() * box.channels() < 8)
                continue;
            cv::Mat flat = box.reshape(1, 1);
            double x[4], y[4];
            hit = {};
            for (int p = 0; p < 4; ++p)
            {
                x[p] = flat.at<float>(0, p * 2);
                y[p] = flat.at<float>(0, p * 2 + 1);
                hit.x += x[p];
                hit.y += y[p];
            }
            hit.x /= 4;
            hit.y /= 4;
            // 常规检测框使用 X/Y 投影宽高，表示输入图坐标轴上的尺寸，区别于欧氏边长。
            // 透视恢复另有几何来源，Result 的 dimensions_are_side_lengths 明确标注该差别。
            hit.width = std::max((std::abs(x[1] - x[0]) + std::abs(x[3] - x[2])) * .5,
                                 (std::abs(x[2] - x[1]) + std::abs(x[0] - x[3])) * .5);
            hit.height = std::max((std::abs(y[1] - y[0]) + std::abs(y[3] - y[2])) * .5,
                                  (std::abs(y[2] - y[1]) + std::abs(y[0] - y[3])) * .5);
            hit.scale_y = scale_y;
            hit.text = texts[i];
            return true;
        }
        return false;
    }
    bool QrDetector::decode_xy(const cv::Mat &source, double x, double y, Hit &hit)
    {
        // 该函数封装任意 X/Y 重采样，并在成功后撤销缩放。
        // 缩小时使用 Area 抑制条纹与混叠；放大时使用 Linear，避免锐化产生伪模块边缘。
        if (source.empty() || x <= 0 || y <= 0 || !std::isfinite(x) || !std::isfinite(y))
            return false;
        int width = std::max(64, round_even(source.cols * x)), height = std::max(64, round_even(source.rows * y));
        double sx = width / static_cast<double>(source.cols), sy = height / static_cast<double>(source.rows);
        cv::Mat resized;
        cv::resize(source, resized, {width, height}, 0, 0, sx <= 1 && sy <= 1 ? cv::INTER_AREA : cv::INTER_LINEAR);
        if (!decode(resized, 1, hit))
            return false;
        hit.x /= sx;
        hit.y /= sy;
        hit.width /= sx;
        hit.height /= sy;
        hit.scale_y = 1;
        return true;
    }

    Result QrDetector::detect(const cv::Mat &source)
    {
        return detect(source, DetectHints{});
    }

    Result QrDetector::detect(const cv::Mat &source, const DetectHints &hints)
    {
        // 通用主流程：完整输入图 -> 转灰度/极性归一 -> 常规 Y 尺度 ->
        // 定位框门控的自适应/局部候选 -> 透视与低对比恢复 -> 严重失焦恢复。
        // 所有分支只有在 WeChatQRCode 解出非空文本后才返回成功。
        attempts_ = 0;
        if (source.empty() || source.dims != 2 || source.depth() != CV_8U ||
            (source.channels() != 1 && source.channels() != 3 && source.channels() != 4))
            throw std::invalid_argument("Invalid image: Gray8/BGR24/BGRA32 required");
        cv::Rect evidence_region = hints.evidence_region;
        if (evidence_region == cv::Rect())
            evidence_region = {0, 0, source.cols, source.rows};
        if (evidence_region.x < 0 || evidence_region.y < 0 || evidence_region.width <= 0 ||
            evidence_region.height <= 0 || evidence_region.width > source.cols || evidence_region.height > source.rows ||
            evidence_region.x > source.cols - evidence_region.width || evidence_region.y > source.rows - evidence_region.height)
            throw std::invalid_argument("Evidence region must be inside the input image");
        ensure_detector();
        cv::Mat gray = source, gray_owned;
        if (source.channels() != 1)
        {
            // C ABI 只接受 Gray8/BGR24/BGRA32；这里统一为算法内部的 Gray8 快速主路径。
            cv::cvtColor(source, gray_owned, source.channels() == 4 ? cv::COLOR_BGRA2GRAY : cv::COLOR_BGR2GRAY);
            gray = gray_owned;
        }
        cv::Mat normalized;
        // 输入范围由调用方决定；核心不读取安装位置、不裁横向条带、不自动扩大搜索范围。
        // 优先极性只是识别提示，困难码仍由后面的定位证据决定是否尝试相反极性。
        if (inverted_)
            cv::bitwise_not(gray, normalized);
        else
            gray.copyTo(normalized);
        const std::string polarity = inverted_ ? "inverted" : "original";
        Hit hit;
        Result result;

        // 1. 快速路径：按配置的少量 Y 向候选直接解码。
        //    这条路径覆盖绝大多数正常帧，并保持输入帧坐标契约。
        for (double scale : scales_)
            if (decode(normalized, scale, hit))
            {
                result = from_hit(hit, hit.x, hit.y / hit.scale_y, 1, hit.scale_y);
                result.strategy = "WeChatQRCode, polarity=" + polarity + ", scaleY=" + fixed(hit.scale_y, 3);
                return result;
            }
        // 2. 常规解码失败后，从嵌套轮廓中提取并去重定位框证据。
        //    默认在整图取证。高级调用者可显式约束统计范围，防止无关背景影响 Otsu。
        //    提示区不用于截断最终解码图，也不隐含传感器方向或业务位置。
        std::vector<Finder> evidence;
        try
        {
            evidence = finder_evidence(normalized(evidence_region));
        }
        catch (const cv::Exception &)
        {
        }
        std::vector<Scale> candidates;
        int coherent = 0;
        double side = 0, relative = 1;
        bool reliable = adaptive_scales(evidence, candidates, coherent, side, relative);
        // 根据定位框尺寸与间距实时计算 scaleX/scaleY，不保存任何针对历史样本的固定缩放系数。
        if (reliable)
            for (const auto &candidate : candidates)
                if (decode_xy(normalized, candidate.x, candidate.y, hit))
                {
                    result = from_hit(hit, hit.x, hit.y);
                    result.strategy = "WeChatQRCode, adaptive-finder, finderCount=" + std::to_string(coherent) +
                                      ", estimatedSide=" + fixed(side, 1) + ", targetSide=" + fixed(candidate.target, 1) +
                                      ", geometryScaleY=" + fixed(relative, 3) + ", polarity=" + polarity +
                                      ", scaleX=" + fixed(candidate.x, 3) + ", scaleY=" + fixed(candidate.y, 3);
                    return result;
                }
        // 3. 自适应整条 ROI 仍失败时，再由“嵌套定位框 + 经典四角点”双重几何确认局部码区。
        //    四角点可以反推出被传感器边界截断的码区/静区；白色补边只恢复二维码规范要求的背景，
        //    不臆造任何数据模块，纠错和业务文本仍完全交给 WeChatQRCode。
        Region region;
        if (reliable && candidate_region(normalized, region))
        {
            cv::Mat input = normalized(region.roi), padded, resized;
            if (region.padded())
            {
                cv::copyMakeBorder(input, padded, region.top, region.bottom, region.left, region.right, cv::BORDER_CONSTANT,
                                   cv::Scalar(255));
                input = padded;
            }
            for (const auto &candidate : local_scales(region))
                // 局部候选使用有限的动态工作尺寸，提高二维码在 CNN 输入中的占比，
                // 同时避免引入锐化、固定阈值等容易随样本波动的预处理分支。
                if (decode_xy(input, candidate.x, candidate.y, hit))
                {
                    result = from_region(hit, region);
                    result.strategy =
                        "WeChatQRCode, " + std::string(region.padded() ? "edge-padded" : "finder-local-adaptive") +
                        ", finderCount=" + std::to_string(coherent) + ", roi=" + region_description(region) +
                        ", estimatedSide=" + fixed(region.side, 1) + ", targetSide=" + fixed(candidate.target, 1) +
                        ", geometryScaleY=" + fixed(region.relative_y, 3) + ", polarity=" + polarity +
                        ", scaleX=" + fixed(candidate.x, 3) + ", scaleY=" + fixed(candidate.y, 3);
                    return result;
                }
            double pre_scale = std::min(1., 640. / std::max(input.cols, input.rows));
            // OpenCV WeChatQRCode 会把大图压到固定面积。长条 ROI 中二维码占比过小时容易丢失定位点，
            // 因此只对已确认的局部码区限制最长边约 640 px，在降低噪声的同时保留模块边缘。
            if (pre_scale < 1 - .0001)
            {
                cv::resize(
                    input, resized,
                    {std::max(64, round_even(input.cols * pre_scale)), std::max(64, round_even(input.rows * pre_scale))}, 0,
                    0, cv::INTER_AREA);
                input = resized;
            }
            for (double scale : scales_)
                if (decode(input, scale, hit))
                {
                    result = from_region(hit, region, pre_scale, pre_scale * hit.scale_y);
                    result.strategy = "WeChatQRCode, finder-roi=" + region_description(region) +
                                      ", preScale=" + fixed(pre_scale, 3) + ", polarity=" + polarity +
                                      ", scaleY=" + fixed(hit.scale_y, 3);
                    return result;
                }
        }
        // 4. 只有存在定位框轮廓证据时才展开较昂贵的透视与反极性恢复，
        //    避免普通无二维码帧无条件把完整识别流程执行两遍。
        if (!evidence.empty())
        {
            if (perspective(normalized, evidence_region, result))
            {
                result.strategy += ", polarity=" + polarity;
                return result;
            }
            if (inverted_ && perspective(gray, evidence_region, result))
            {
                // 优先白色前景失败且已有几何证据时，回到原始灰度复用透视恢复。
                result.strategy += ", polarity=opposite-original";
                return result;
            }
            if (low_contrast(gray, static_cast<int>(evidence.size()), result))
                return result;
        }
        // 5. 严重失焦会抹掉嵌套轮廓，但三个定位框的整体明暗结构仍可能存在。
        //    最终兜底在缩小的灰度/颜色通道上寻找三个受几何约束的模板峰；
        //    找不到可信三点组时不会继续调用额外 DNN。
        if (blurred(source, result))
            return result;
        return {};
    }
} // namespace qr
