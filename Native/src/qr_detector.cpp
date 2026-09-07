#include "qr_detector.h"
#include <Windows.h>
#include <fstream>

namespace cis
{
namespace
{
// WeChatQRCode 偶尔会返回只包含空白字符的字符串；业务上必须视为未识别，
// 不能仅凭检测框存在就触发图像拼接。
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

void QrDetector::configure(int x, int width, bool invert, const float *values, uint32_t count)
{
    // 配置按采集会话快照保存。C ABI 传入的 scales 数组只在本函数内借用，
    // 返回后 C# 即可解除固定；检测期间不会观察到设置窗口的中途修改。
    roi_x_ = x;
    roi_width_ = width;
    inverted_ = invert;
    scales_.clear();
    for (uint32_t i = 0; i < count; ++i)
    {
        double v = values[i];
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
void QrDetector::initialize()
{
    attempts_ = 0;
    ensure_detector();
    if (warmed_)
        return;
    // 用不包含二维码的小图触发 DNN 与超分辨率网络的首次初始化。
    // 预热只消除首帧抖动，结果不参与业务判断，attempts 也在结束后清零。
    cv::Mat blank(2500, std::max(64, roi_width_), CV_8UC1, cv::Scalar(255));
    Hit ignored;
    for (double scale : scales_)
        decode(blank, scale, ignored);
    attempts_ = 0;
    warmed_ = true;
}

bool QrDetector::decode(const cv::Mat &source, double scale_y, Hit &hit)
{
    // CIS 为沿 Y 方向连续扫描，速度波动主要表现为纵向压缩/拉伸。
    // 快速路径只枚举 scaleY，命中后由调用方把 Y 坐标和高度恢复到 source 坐标系。
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
        // WeChatQRCode 返回顺时针四边形。拼接和 Mark 比例换算使用 X/Y 投影宽高，
        // 不能改成欧氏边长，否则会把横向分辨率和轻微旋转混入 CIS 纵向尺度。
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
    // 统一主流程：转灰度 -> 横向 ROI -> 极性归一 -> 常规 Y 尺度 ->
    // 定位框门控的自适应/局部候选 -> 透视与低对比恢复 -> 严重失焦恢复。
    // 所有分支只有在 WeChatQRCode 解出非空文本后才返回成功。
    attempts_ = 0;
    ensure_detector();
    cv::Mat gray = source, gray_owned;
    if (source.channels() != 1)
    {
        // C ABI 只接受 Gray8/BGR24/BGRA32；这里统一为算法内部的 Gray8 快速主路径。
        cv::cvtColor(source, gray_owned, source.channels() == 4 ? cv::COLOR_BGRA2GRAY : cv::COLOR_BGR2GRAY);
        gray = gray_owned;
    }
    int x = std::max(0, std::min(roi_x_, gray.cols - 1));
    int width = roi_width_ > 0 ? std::min(roi_width_, gray.cols - x) : gray.cols - x;
    if (width <= 0)
    {
        x = 0;
        width = gray.cols;
    }
    int core_x = x, core_width = width;
    // 配置 ROI 表示二维码的常规安装区域。二维码贴近传感器边缘时，定位框可能略微落到
    // ROI 左侧，经典定位还需要少量右侧背景判断边界，因此两侧增加约 1/6 ROI 的保护带。
    // 保护带仍远小于整幅 CIS 宽度，避免把大面积无关图案送入二维码网络。
    int guard = std::max(64, width / 6), left = std::min(x, guard), right = std::min(gray.cols - (x + width), guard);
    x -= left;
    width = std::min(gray.cols - x, width + left + right);
    cv::Mat roi = gray(cv::Rect(x, 0, width, gray.rows)), normalized;
    // CIS 上二维码只限制横向安装区域；纵向保留全帧，以覆盖单帧和跨帧拼接后的任意 Y。
    // 正常生产的白色前景二维码通过配置反色为常规黑码；异常无白墨黑码由后续受限分支恢复。
    if (inverted_)
        cv::bitwise_not(roi, normalized);
    else
        roi.copyTo(normalized);
    const std::string polarity = inverted_ ? "inverted" : "original";
    Hit hit;
    Result result;

    // 1. 快速路径：按配置的少量 Y 向候选直接解码。
    //    这条路径覆盖绝大多数正常帧，并保持输入帧坐标契约。
    for (double scale : scales_)
        if (decode(normalized, scale, hit))
        {
            result = from_hit(hit, hit.x + x, hit.y / hit.scale_y, 1, hit.scale_y);
            result.strategy = "WeChatQRCode, polarity=" + polarity + ", scaleY=" + fixed(hit.scale_y, 3);
            return result;
        }
    // 2. 常规解码失败后，从嵌套轮廓中提取并去重定位框证据。
    //    几何取证只看用户配置的核心 ROI；保护带不参与 Otsu，避免额外背景改变阈值和轮廓层级。
    //    单帧与跨帧组合图共用完全相同的门控和候选生成逻辑。
    cv::Rect core(core_x - x, 0, core_width, gray.rows);
    std::vector<Finder> evidence;
    try
    {
        evidence = finder_evidence(normalized(core));
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
                result = from_hit(hit, hit.x + x, hit.y);
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
                result = from_region(hit, x, region);
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
                result = from_region(hit, x, region, pre_scale, pre_scale * hit.scale_y);
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
        if (perspective(normalized, core, x, result))
        {
            result.strategy += ", polarity=" + polarity;
            return result;
        }
        if (inverted_ && perspective(roi, core, x, result))
        {
            // 配置极性适合常规白色前景二维码；白墨缺失时码可能变为黑色前景。
            // 仅在标准极性失败且已有几何证据时，回到传感器原始灰度复用同一套透视恢复。
            result.strategy += ", polarity=opposite-original";
            return result;
        }
        if (low_contrast(roi, x, static_cast<int>(evidence.size()), result))
            return result;
    }
    // 5. 严重失焦会抹掉嵌套轮廓，但三个定位框的整体明暗结构仍可能存在。
    //    最终兜底在缩小的灰度/颜色通道上寻找三个受几何约束的模板峰；
    //    找不到可信三点组时不会继续调用额外 DNN。
    if (blurred(source, {x, 0, width, source.rows}, x, result))
        return result;
    return {};
}
} // namespace cis
