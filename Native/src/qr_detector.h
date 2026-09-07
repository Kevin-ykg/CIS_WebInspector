#pragma once
#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/objdetect.hpp>
#include <opencv2/wechat_qrcode.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <iomanip>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

namespace cis
{
// WeChatQRCode 返回的一个有效文本及其几何信息。
// x/y/width/height 所属坐标系由调用阶段决定，离开阶段前必须恢复到输入帧坐标系。
using Quad = std::array<cv::Point2f, 4>;
struct Hit
{
    std::string text;
    double x = 0, y = 0, width = 0, height = 0, scale_y = 1;
};
struct Result
{
    bool found = false;
    int x = 0, y = 0;
    double width = 0, height = 0;
    std::string text, strategy;
};

// 从轮廓树中提取的二维码定位框证据。它只用于几何门控，不代表已经识别成功。
struct Finder
{
    cv::Rect bounds;
    double x, y, side;
};

// 将候选二维码边长归一化到稳定工作尺寸时使用的二维缩放参数。
// 线扫速度主要改变 Y 方向，因此 x/y 可以不同，但候选数量受到严格限制。
struct Scale
{
    double target, x, y;
};

// 经典二维码定位器推导出的局部码区。
// roi 是传感器中真实存在的像素；四个 padding 是理论静区超出画面的部分。
struct Region
{
    cv::Rect roi;
    int left = 0, top = 0, right = 0, bottom = 0;
    double side = 0, relative_y = 1;
    bool padded() const
    {
        return left || top || right || bottom;
    }
};

// 三个清晰定位框推导出的透视候选。corners 始终按左上、右上、右下、左下排序。
struct Perspective
{
    Quad corners;
    int modules;
    double cosine, score;
};

// 严重失焦时通过 7x7 定位框模板得到的峰值证据及三点几何组合。
struct BlurFinder
{
    cv::Point2f center;
    double module, score;
    bool inverted;
};
struct BlurTriple
{
    BlurFinder corner, first, second;
    bool inverted;
    double cosine, dimension, average_score, score;
};

// 保持 C# Math.Round 的 midpoint-to-even，而不是 std::round 的远离零规则。
inline int round_even(double value)
{
    double low = std::floor(value), part = value - low;
    return static_cast<int>(part < .5 ? low : part > .5 ? low + 1 : (std::fmod(low, 2) == 0 ? low : low + 1));
}
inline double clamp(double v, double lo, double hi)
{
    return std::max(lo, std::min(hi, v));
}
inline std::string fixed(double v, int n)
{
    std::ostringstream s;
    s.imbue(std::locale::classic());
    s << std::fixed << std::setprecision(n) << v;
    return s.str();
}
inline double distance(cv::Point2f a, cv::Point2f b)
{
    double x = a.x - b.x, y = a.y - b.y;
    return std::sqrt(x * x + y * y);
}
inline cv::Point2f scaled(cv::Point2f p, double v)
{
    return {static_cast<float>(p.x * v), static_cast<float>(p.y * v)};
}
inline cv::Point2f add(cv::Point2f p, cv::Point2f a, cv::Point2f b)
{
    return {p.x + a.x + b.x, p.y + a.y + b.y};
}

// 以下辅助函数共同维护一个重要契约：任何局部裁切、补白、缩放或透视展开
// 都只能服务于解码，最终 Result 必须回到原始输入帧的像素坐标系。
Quad ordered(const Quad &points);
Result from_hit(const Hit &, double x, double y, double sx = 1, double sy = 1, bool clamp_zero = false);
Result from_corners(const Hit &, const Quad &, int offset_x, bool float_sum);
Result from_region(const Hit &, int offset_x, const Region &, double sx = 1, double sy = 1);
bool candidate_region(const cv::Mat &, Region &, bool low_contrast = false);
std::vector<Finder> finder_evidence(const cv::Mat &);
bool adaptive_scales(const std::vector<Finder> &, std::vector<Scale> &, int &coherent, double &side,
                     double &relative_y);
std::vector<Scale> local_scales(const Region &);
std::vector<Perspective> perspective_candidates(const std::vector<Finder> &, int, int, int, int);
std::vector<BlurFinder> blurred_evidence(const cv::Mat &);
std::vector<BlurTriple> blurred_triples(const std::vector<BlurFinder> &);
bool module_structure(const cv::Mat &, int, double &);

// 面向 CIS 线扫图像的 WeChatQRCode 检测器。
// 每个候选最终只使用同一个 CNN 检测/增强/解码后端，不依赖其他解码器回退。
// 所有视觉策略均在 C++ 内完成：C# 每帧只调用 detect 一次，不回调托管图像算子。
// 同一实例复用 WeChat 模型；临时 Mat 依靠 RAII 按作用域释放，不保留借用的帧像素。
class QrDetector
{
  public:
    explicit QrDetector(std::string directory) : directory_(std::move(directory)) {}
    void configure(int x, int width, bool invert, const float *scales, uint32_t count);
    void initialize();
    Result detect(const cv::Mat &source);
    uint32_t attempts() const
    {
        return attempts_;
    }
    std::mutex mutex;

  private:
    std::string directory_;
    std::unique_ptr<cv::wechat_qrcode::WeChatQRCode> detector_;
    int roi_x_ = 0, roi_width_ = 0;
    bool inverted_ = true, warmed_ = false;
    std::vector<double> scales_{1.0};
    uint32_t attempts_ = 0;
    void ensure_detector();
    bool decode(const cv::Mat &, double, Hit &);
    bool decode_xy(const cv::Mat &, double, double, Hit &);
    bool perspective(const cv::Mat &, cv::Rect, int, Result &);
    bool low_contrast(const cv::Mat &, int, int, Result &);
    bool blurred(const cv::Mat &, cv::Rect, int, Result &);
    bool blurred_rectified(const cv::Mat &, const Quad &, int, bool, int &, Hit &, std::string &);
};
} // namespace cis
