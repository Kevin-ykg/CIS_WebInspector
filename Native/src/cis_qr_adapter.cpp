#include "cis_qr_adapter.h"
#include "qr_detector.h"

namespace cis
{
// CIS 持续采集的常规码为白色前景，白墨缺失可能变成黑码；configure 保留现场的优先极性。
// 检测输出的 X/Y 尺寸继续用于拼接/Mark 尺度换算，不能在本次拆分中统一改成欧氏边长。
void CisQrAdapter::configure(int x, int width, bool invert, const float *scales, uint32_t count)
{
    qr::Options options;
    options.invert_polarity = invert;
    options.scales_y.clear();
    if (count)
        options.scales_y.assign(scales, scales + count); // 会话快照，不长期借用托管数组。
    detector_.configure(options);
    roi_x_ = x;
    roi_width_ = width;
}

void CisQrAdapter::initialize()
{
    // 保持原 CIS 预热形状和尺度次序；通用核心自己的默认预热为 640×640。
    detector_.initialize({std::max(64, roi_width_), 2500});
}

Result to_frame_result(const qr::Result &source, cv::Point origin)
{
    if (!source.found)
        return {};
    int x = qr::round_even(source.x + origin.x), y = qr::round_even(source.y + origin.y);
    if (source.clamp_center_to_zero)
    {
        x = std::max(0, x);
        y = std::max(0, y);
    }
    return {true, x, y, source.width, source.height, source.text, source.strategy};
}

Result CisQrAdapter::detect(const cv::Mat &source)
{
    if (source.empty() || source.depth() != CV_8U ||
        (source.channels() != 1 && source.channels() != 3 && source.channels() != 4))
        throw std::invalid_argument("Invalid image: Gray8/BGR24/BGRA32 required");

    int x = std::max(0, std::min(roi_x_, source.cols - 1));
    int width = roi_width_ > 0 ? std::min(roi_width_, source.cols - x) : source.cols - x;
    if (width <= 0) { x = 0; width = source.cols; }
    int core_x = x, core_width = width;
    // CIS 二维码通常出现在固定横向安装区域；左右约 1/6 的保护带保留边缘定位框。
    // Y 不裁切，单帧和跨帧组合使用相同规则，避免改变既有拼接位置。
    int guard = std::max(64, width / 6);
    int left = std::min(x, guard), right = std::min(source.cols - (x + width), guard);
    x -= left;
    width = std::min(source.cols - x, width + left + right);
    cv::Rect window(x, 0, width, source.rows);
    qr::DetectHints hints;
    // 解码使用带保护带工作图，Otsu/定位证据仍限定于原 core；两者不能混为同一区域。
    hints.evidence_region = {core_x - x, 0, core_width, source.rows};
    // 只创建只读视图，不 clone 整帧，也保留彩色通道用于失焦恢复。
    return to_frame_result(detector_.detect(source(window), hints), window.tl());
}
} // namespace cis
