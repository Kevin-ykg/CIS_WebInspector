#include "alignment_internal.h"
#include <ppl.h>

namespace alignment
{
void warp(const cv::Mat &source, cv::Mat &destination, Result &result)
{
    if (result.H.empty())
        throw std::invalid_argument("无有效全局矩阵，不能 Warp。");
    result.MapMs = 0;
    result.RemapMs = 0;
    if (result.Mode == 0 || result.GridY.empty() || result.Residuals.empty())
    {
        auto start = Clock::now();
        cv::warpPerspective(source, destination, result.H, destination.size(), cv::INTER_LINEAR, cv::BORDER_CONSTANT,
                            0);
        result.RemapMs = elapsed(start);
        return;
    }
    int width = destination.cols, maxRows = std::max(1, result.StripeRows);
    std::vector<float> left(width), right(width);
    // float 权重是原 C# 的刻意数值口径；提升为 double 会使临界插值像素发生变化。
    for (int x = 0; x < width; ++x)
    {
        if (x <= result.GridX[0])
            left[x] = 1;
        else if (x < result.GridX[1])
            left[x] = static_cast<float>((result.GridX[1] - x) / std::max(result.GridX[1] - result.GridX[0], 1e-6));
        if (x >= result.GridX[2])
            right[x] = 1;
        else if (x > result.GridX[1])
            right[x] = static_cast<float>((x - result.GridX[1]) / std::max(result.GridX[2] - result.GridX[1], 1e-6));
    }
    cv::Mat mapX(maxRows, width, CV_32FC1), mapY(maxRows, width, CV_32FC1), stripe;
    const cv::Mat &h = result.Inverse;
    // 缓存九个系数，避免每像素通过 Mat::at 做形状/行地址处理。
    double h00 = h.at<double>(0, 0), h01 = h.at<double>(0, 1), h02 = h.at<double>(0, 2), h10 = h.at<double>(1, 0),
           h11 = h.at<double>(1, 1), h12 = h.at<double>(1, 2), h20 = h.at<double>(2, 0), h21 = h.at<double>(2, 1),
           h22 = h.at<double>(2, 2);
    for (int startY = 0; startY < destination.rows; startY += maxRows)
    {
        int rows = std::min(maxRows, destination.rows - startY);
        cv::Mat mx = mapX.rowRange(0, rows), my = mapY.rowRange(0, rows);
        result.TemporaryBytes = std::max(result.TemporaryBytes,
                                         static_cast<uint64_t>(rows) * width * (sizeof(float) * 2 + source.elemSize()));
        auto start = Clock::now();
        // 每行独立，只读 H/网格，不共享写入；对应原 Parallel.For。仅分块 map，不建立全幅浮点图。
        concurrency::parallel_for(0, rows, [&](int localY) {
            int y = startY + localY, interval = -1;
            double t = 0;
            if (y >= result.GridY.front() && y <= result.GridY.back())
            {
                if (y >= result.GridY.back())
                {
                    interval = static_cast<int>(result.GridY.size()) - 2;
                    t = 1;
                }
                else
                    for (size_t i = 0; i + 1 < result.GridY.size(); ++i)
                        if (y >= result.GridY[i] && y < result.GridY[i + 1])
                        {
                            interval = static_cast<int>(i);
                            t = (y - result.GridY[i]) / std::max(result.GridY[i + 1] - result.GridY[i], 1e-6);
                            break;
                        }
            }
            cv::Point2d l{0, 0}, r{0, 0};
            if (interval >= 0)
            {
                l = lerp(result.Residuals[interval][0], result.Residuals[interval + 1][0], t);
                r = lerp(result.Residuals[interval][2], result.Residuals[interval + 1][2], t);
            }
            float *px = mx.ptr<float>(localY);
            float *py = my.ptr<float>(localY);
            for (int x = 0; x < width; ++x)
            {
                double d = h20 * x + h21 * y + h22;
                double sx = (h00 * x + h01 * y + h02) / d, sy = (h10 * x + h11 * y + h12) / d;
                if (interval >= 0)
                {
                    sx += l.x * left[x] + r.x * right[x];
                    sy += l.y * left[x] + r.y * right[x];
                }
                px[x] = static_cast<float>(sx);
                py[x] = static_cast<float>(sy);
            }
        });
        result.MapMs += elapsed(start);
        start = Clock::now();
        // 单次逆向采样：先 H^-1 加残差，再 Remap；不先 Warp 再 Remap，避免两次插值损伤边缘。
        cv::remap(source, stripe, mx, my, cv::INTER_LINEAR, cv::BORDER_CONSTANT, 0);
        stripe.copyTo(destination.rowRange(startY, startY + rows));
        result.RemapMs += elapsed(start);
    }
}
} // namespace alignment
