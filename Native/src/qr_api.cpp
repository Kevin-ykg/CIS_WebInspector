#include "cis_qr_api.h"
#include "cis_qr_adapter.h"
#include <algorithm>
#include <Windows.h>
#include <cstring>
#include <limits>
#include <stdexcept>

#include "qr_api_support.h"
using namespace qr_api_util;

// 结构体大小是 C#/C++ 布局契约的一部分；任何字段、对齐方式变化都应在编译期失败。
static_assert(sizeof(CisQrConfig) == 32 && sizeof(CisQrResult) == 48, "C ABI layout changed");
uint32_t __cdecl cis_qr_abi_version(void)
{
    return 2;
}
int32_t __cdecl cis_qr_create(const wchar_t *directory, void **handle, char *error, uint32_t capacity)
{
    if (handle)
        *handle = nullptr;
    return guarded(error, capacity, [&]() -> int32_t {
        if (!handle)
            throw std::invalid_argument("Null handle output");
        auto context = std::make_unique<cis::CisQrAdapter>(model_path(directory));
        // 只有成功创建后才转移所有权；此前发生异常时 unique_ptr 会自动回收。
        *handle = context.release();
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_qr_configure(void *handle, const CisQrConfig *config, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() -> int32_t {
        if (!handle || !config || config->struct_size != sizeof(CisQrConfig) ||
            (config->scale_count && !config->scales_y) || config->scale_count > 4096)
            throw std::invalid_argument("Invalid QR configuration");
        auto &context = *static_cast<cis::CisQrAdapter *>(handle);
        // WeChatQRCode/DNN 实例不按线程安全使用；配置、预热、检测和释放由托管层及此锁串行化。
        std::lock_guard<std::mutex> guard(context.mutex);
        context.configure(config->roi_x, config->roi_width, config->invert_polarity != 0, config->scales_y,
                          config->scale_count);
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_qr_initialize(void *handle, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() -> int32_t {
        if (!handle)
            throw std::invalid_argument("Null QR handle");
        auto &context = *static_cast<cis::CisQrAdapter *>(handle);
        std::lock_guard<std::mutex> guard(context.mutex);
        context.initialize();
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_qr_detect(void *handle, const uint8_t *pixels, uint64_t bytes, int32_t width, int32_t height,
                              uint64_t stride, int32_t channels, CisQrResult *output, char *text,
                              uint32_t text_capacity, char *strategy, uint32_t strategy_capacity, char *error,
                              uint32_t error_capacity)
{
    message(text, text_capacity, "");
    message(strategy, strategy_capacity, "");
    return guarded(error, error_capacity, [&]() -> int32_t {
        if (!output || output->struct_size != sizeof(CisQrResult))
            throw std::invalid_argument("Invalid result structure");
        *output = {};
        output->struct_size = sizeof(CisQrResult);
        if (!handle || !pixels || width <= 0 || height <= 0 || (channels != 1 && channels != 3 && channels != 4))
            throw std::invalid_argument("Invalid image: Gray8/BGR24/BGRA32 required");
        // 先以 64 位无符号数验证步长和最后一行地址，防止乘法溢出或越界构造 cv::Mat。
        uint64_t row = static_cast<uint64_t>(width) * channels;
        if (stride < row || (height > 1 && stride > (UINT64_MAX - row) / (height - 1)) ||
            bytes < stride * (height - 1) + row)
            throw std::invalid_argument("Image length/stride overflow or insufficient input buffer");
        if (!text || !text_capacity || !strategy || !strategy_capacity)
            throw std::invalid_argument("Output buffers are required");
        auto &context = *static_cast<cis::CisQrAdapter *>(handle);
        std::lock_guard<std::mutex> guard(context.mutex);
        // 此 Mat 只是调用方像素上的非拥有型视图。context.detect 必须同步完成，且不得保存 data 指针。
        cv::Mat source(height, width, CV_MAKETYPE(CV_8U, channels), const_cast<uint8_t *>(pixels),
                       static_cast<size_t>(stride));
        cis::Result result;
        try
        {
            result = context.detect(source);
        }
        catch (...)
        {
            output->attempts = context.attempts();
            throw;
        }
        output->attempts = context.attempts();
        if (!result.found)
            return CIS_QR_NOT_FOUND;
        output->text_bytes = static_cast<uint32_t>(result.text.size() + 1);
        output->strategy_bytes = static_cast<uint32_t>(result.strategy.size() + 1);
        // 不截断二维码文本或诊断策略。调用方可根据返回长度扩大缓冲区后重试。
        if (output->text_bytes > text_capacity || output->strategy_bytes > strategy_capacity)
        {
            message(error, error_capacity, "Result buffer too small; required lengths are returned");
            return CIS_QR_BUFFER_SMALL;
        }
        output->found = 1;
        output->center_x = result.x;
        output->center_y = result.y;
        output->pixel_width = result.width;
        output->pixel_height = result.height;
        std::memcpy(text, result.text.c_str(), output->text_bytes);
        std::memcpy(strategy, result.strategy.c_str(), output->strategy_bytes);
        return CIS_QR_OK;
    });
}
void __cdecl cis_qr_destroy(void *handle)
{
    // C# SafeHandle 会把句柄寿命延长到最后一次 P/Invoke 结束；其他 C 调用者也必须遵守该顺序。
    // 析构绝不能向 C# 抛异常，因此这里保留最终防线，即使当前析构函数本身不会抛出。
    try
    {
        delete static_cast<cis::CisQrAdapter *>(handle);
    }
    catch (...)
    {
    }
}
