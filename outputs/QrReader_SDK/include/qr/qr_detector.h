#pragma once
#include <opencv2/core.hpp>
#include <opencv2/wechat_qrcode.hpp>
#include <cstdint>
#include <array>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace qr
{
struct Hit;
struct BlurTriple;

/// 首个成功解码结果。中心为传入图像中的浮点坐标，不提前取整或裁到零边界。
/// 若调用方传入父图的 ROI，应先加回 ROI 偏移，再按业务需要取整。
struct Result
{
    bool found = false;
    double x = 0, y = 0, width = 0, height = 0;
    std::string text, strategy;
    // 历史恢复路径具有零边界保护。保留此标志供 CIS 兼容出口在原图空间执行，
    // 避免对 ROI 局部坐标过早截断。普通调用者可直接使用浮点中心。
    bool clamp_center_to_zero = false;
    // 普通解码为 X/Y 投影宽高；透视恢复为原图四角的平均对边长度。
    // 明确区分两种几何语义，不在结构迁移时悄悄改变已有物理尺度计算。
    bool dimensions_are_side_lengths = false;
};

struct Options
{
    bool invert_polarity = false;          ///< 默认按常规黑色前景二维码识别。
    std::vector<float> scales_y{1.0f};     ///< 可选纵向形变候选；不是相机安装参数。
};

struct DetectHints
{
    // 可选的定位证据区域，坐标相对本次输入图。空 Rect 表示整图。
    // 仅影响轮廓取证/透视候选；解码仍可使用完整输入。不会自动添加保护带。
    cv::Rect evidence_region;
};

/// 可复用的单码识别核心：不读取 AppConfig、不知道 CIS 安装位置或拼接状态。
/// 同一实例复用模型，调用期间只读借用图像；一个实例须由调用方串行使用。
/// 跨 DLL 请使用 qr_reader_api.h 的 C 接口；此 C++ 接口用于同工具链源码/静态链接。
class QrDetector
{
  public:
    explicit QrDetector(std::string model_directory) : directory_(std::move(model_directory)) {}
    void configure(const Options &options);
    void initialize(cv::Size working_size = {640, 640});
    Result detect(const cv::Mat &image);
    Result detect(const cv::Mat &image, const DetectHints &hints);
    uint32_t attempts() const { return attempts_; }

  private:
    std::string directory_;
    std::unique_ptr<cv::wechat_qrcode::WeChatQRCode> detector_;
    bool inverted_ = false, warmed_ = false;
    std::vector<double> scales_{1.0};
    uint32_t attempts_ = 0;
    void ensure_detector();
    bool decode(const cv::Mat &, double, Hit &);
    bool decode_xy(const cv::Mat &, double, double, Hit &);
    bool perspective(const cv::Mat &, cv::Rect, Result &);
    bool low_contrast(const cv::Mat &, int, Result &);
    bool blurred(const cv::Mat &, Result &);
    bool blurred_local(const cv::Mat &, const cv::Mat &, const std::vector<BlurTriple> &, Result &);
    bool blurred_rectified(const cv::Mat &, const std::array<cv::Point2f, 4> &, int, bool, int &, Hit &, std::string &);
};
} // namespace qr
