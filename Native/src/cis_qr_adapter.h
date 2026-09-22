#pragma once
#include "qr/qr_detector.h"
#include <mutex>

namespace cis
{
/// 当前 CIS 应用需要的整数几何结果；公共 C ABI v2 的布局保持不变。
struct Result
{
    bool found = false;
    int x = 0, y = 0;
    double width = 0, height = 0;
    std::string text, strategy;
};

// 将通用输入图结果映射到 CIS 帧坐标。先加偏移再取整/夹零，供回归单独验证。
Result to_frame_result(const qr::Result &result, cv::Point origin);

/// 安装位置、横向保护带、原 core 区及 CIS 预热尺寸只在本适配层维护。
/// 与其他项目共享算法时不需要携带此文件或 AppConfig。
class CisQrAdapter
{
  public:
    explicit CisQrAdapter(std::string directory) : detector_(std::move(directory))
    {
        qr::Options options;
        options.invert_polarity = true; // 保留旧 cis_qr_create 未 configure 时的默认极性。
        detector_.configure(options);
    }
    void configure(int x, int width, bool invert, const float *scales, uint32_t count);
    void initialize();
    Result detect(const cv::Mat &source);
    uint32_t attempts() const { return detector_.attempts(); }
    std::mutex mutex;

  private:
    qr::QrDetector detector_;
    int roi_x_ = 0, roi_width_ = 0;
};
} // namespace cis
