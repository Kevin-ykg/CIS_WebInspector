#include "patch_detector.h"
#include <cstring>
#include <stdexcept>

namespace
{
using namespace cis::patch;
using CacheHandle = std::shared_ptr<TemplateCache>;
void message(char *output, uint32_t capacity, const char *text) noexcept
{
    if (!output || !capacity)
        return;
    size_t count = std::min<size_t>(capacity - 1, std::strlen(text));
    std::memcpy(output, text, count);
    output[count] = 0;
}
template <class Action> int32_t guarded(char *error, uint32_t capacity, Action action) noexcept
{
    message(error, capacity, "");
    try
    {
        return action();
    }
    catch (const std::invalid_argument &ex)
    {
        message(error, capacity, ex.what());
        return CIS_QR_INVALID;
    }
    catch (const std::exception &ex)
    {
        message(error, capacity, ex.what());
        return CIS_QR_ERROR;
    }
    catch (...)
    {
        message(error, capacity, "Unknown native patch error");
        return CIS_QR_ERROR;
    }
}
template <class T> T &required(void *handle)
{
    if (!handle)
        throw std::invalid_argument("Null native handle");
    return *static_cast<T *>(handle);
}
void check_layout(uint32_t actual, size_t expected)
{
    if (actual != expected)
        throw std::invalid_argument("C#/C++ patch ABI layout mismatch");
}
cv::Mat image_view(const CisPatchImage *image)
{
    if (!image)
        throw std::invalid_argument("Missing image descriptor");
    check_layout(image->struct_size, sizeof(CisPatchImage));
    if (!image->pixels || image->width <= 0 || image->height <= 0 ||
        (image->channels != 1 && image->channels != 3 && image->channels != 4))
        throw std::invalid_argument("Gray8/BGR24/BGRA32 image required");
    uint64_t row = static_cast<uint64_t>(image->width) * image->channels;
    if (image->stride < row || (image->height > 1 && image->stride > (UINT64_MAX - row) / (image->height - 1)) ||
        image->buffer_bytes < image->stride * (image->height - 1) + row)
        throw std::invalid_argument("Image stride/length invalid");
    return cv::Mat(image->height, image->width, CV_MAKETYPE(CV_8U, image->channels),
                   const_cast<uint8_t *>(image->pixels), static_cast<size_t>(image->stride));
}
void validate(const CisPatchConfig *config)
{
    if (!config)
        throw std::invalid_argument("Missing configuration");
    check_layout(config->struct_size, sizeof(CisPatchConfig));
    if (config->detection_scale <= 0 || !std::isfinite(config->detection_scale) || config->minimum_scaled_width < 0 ||
        config->alpha_threshold < 0 || config->alpha_threshold > 255 || config->cis_threshold < 0 ||
        config->cis_threshold > 255 || (config->output_flags & ~7) != 0)
        throw std::invalid_argument("Invalid patch configuration");
    for (double value : {config->layout_dpi, config->tolerance_inner_mm, config->tolerance_outer_mm,
                         config->exclusion_outer_mm, config->exclusion_inner_mm, config->area_inner_mm2,
                         config->area_outer_mm2, config->fine_min_length_mm, config->fine_max_width_mm})
        if (!std::isfinite(value))
            throw std::invalid_argument("Non-finite physical parameter");
}
cv::Mat &output_image(void *handle, int kind)
{
    if (kind < 0 || kind >= 6)
        throw std::invalid_argument("Unknown output image kind");
    return required<PatchResult>(handle).images[kind];
}
} // namespace

// x64 / pack=8：固定布局与 C# Marshal.SizeOf 双向核对，升级字段需提升 patch ABI。
static_assert(sizeof(CisPatchImage) == 40 && sizeof(CisPatchConfig) == 112 && sizeof(CisPatchDefect) == 64 &&
                  sizeof(CisPatchSummary) == 80 && sizeof(CisPatchCacheStats) == 48,
              "Patch ABI layout changed");
uint32_t __cdecl cis_patch_abi_version(void)
{
    return 1;
}
int32_t __cdecl cis_patch_cache_create(void **handle, char *error, uint32_t capacity)
{
    if (handle)
        *handle = nullptr;
    return guarded(error, capacity, [&]() {
        if (!handle)
            throw std::invalid_argument("Null output handle");
        auto cache = std::make_unique<CacheHandle>(std::make_shared<TemplateCache>());
        *handle = cache.release();
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_patch_cache_stats(void *handle, CisPatchCacheStats *stats, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() {
        if (!stats)
            throw std::invalid_argument("Null stats");
        check_layout(stats->struct_size, sizeof(*stats));
        auto cache = required<CacheHandle>(handle);
        std::lock_guard<std::mutex> lock(cache->mutex);
        *stats = cache->stats;
        return CIS_QR_OK;
    });
}
void __cdecl cis_patch_cache_destroy(void *handle)
{
    delete static_cast<CacheHandle *>(handle);
}
int32_t __cdecl cis_patch_worker_create(void *cache, void **handle, char *error, uint32_t capacity)
{
    if (handle)
        *handle = nullptr;
    return guarded(error, capacity, [&]() {
        if (!handle)
            throw std::invalid_argument("Null output handle");
        auto worker = std::make_unique<Worker>(required<CacheHandle>(cache));
        *handle = worker.release();
        return CIS_QR_OK;
    });
}
void __cdecl cis_patch_worker_destroy(void *handle)
{
    delete static_cast<Worker *>(handle);
}
int32_t __cdecl cis_patch_detect(void *handle, const CisPatchImage *alpha, const CisPatchImage *cis,
                                 const CisPatchConfig *config, void **output, char *error, uint32_t capacity)
{
    if (output)
        *output = nullptr;
    return guarded(error, capacity, [&]() {
        if (!output)
            throw std::invalid_argument("Null result output");
        validate(config);
        auto a = image_view(alpha), c = image_view(cis);
        auto &worker = required<Worker>(handle);
        // 防止调用者在同一 worker 上重入；其他 worker 的计算不被这把锁阻塞。
        std::lock_guard<std::mutex> lock(worker.mutex);
        auto result = std::make_unique<PatchResult>(detect(a, c, *config, worker));
        *output = result.release();
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_patch_result_summary(void *handle, CisPatchSummary *summary, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() {
        if (!summary)
            throw std::invalid_argument("Null summary");
        check_layout(summary->struct_size, sizeof(*summary));
        *summary = required<PatchResult>(handle).summary;
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_patch_result_defects(void *handle, CisPatchDefect *defects, uint32_t count, char *error,
                                         uint32_t capacity)
{
    return guarded(error, capacity, [&]() {
        const auto &source = required<PatchResult>(handle).defects;
        if (count < source.size())
            return CIS_QR_BUFFER_SMALL;
        if (!source.empty() && !defects)
            throw std::invalid_argument("Null defect buffer");
        if (!source.empty())
            std::memcpy(defects, source.data(), source.size() * sizeof(CisPatchDefect));
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_patch_result_log(void *handle, char *text, uint32_t capacity, uint32_t *needed)
{
    return guarded(nullptr, 0, [&]() {
        if (!needed)
            throw std::invalid_argument("Null required length");
        const auto &log = required<PatchResult>(handle).log;
        *needed = static_cast<uint32_t>(log.size() + 1);
        if (capacity < *needed)
            return CIS_QR_BUFFER_SMALL;
        if (!text)
            throw std::invalid_argument("Null log buffer");
        std::memcpy(text, log.c_str(), *needed);
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_patch_result_image(void *handle, int32_t kind, CisPatchImage *info, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() {
        if (!info)
            throw std::invalid_argument("Null image metadata");
        check_layout(info->struct_size, sizeof(*info));
        auto &image = output_image(handle, kind);
        *info = {sizeof(CisPatchImage)};
        if (!image.empty())
        {
            info->width = image.cols;
            info->height = image.rows;
            info->channels = image.channels();
            info->stride = image.cols * image.elemSize();
            info->buffer_bytes = info->stride * image.rows;
        }
        // 不把原生像素地址暴露给 C#；结果释放与 UI 保存不会竞争同一块内存。
        return CIS_QR_OK;
    });
}
int32_t __cdecl cis_patch_result_copy_image(void *handle, int32_t kind, uint8_t *pixels, uint64_t bytes,
                                            uint64_t stride, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() {
        auto &image = output_image(handle, kind);
        if (image.empty())
            throw std::invalid_argument("Output image was not requested");
        uint64_t row = image.cols * image.elemSize();
        if (!pixels || stride < row || (image.rows > 1 && stride > (UINT64_MAX - row) / (image.rows - 1)) ||
            bytes < stride * (image.rows - 1) + row)
            throw std::invalid_argument("Output image buffer invalid");
        for (int y = 0; y < image.rows; ++y)
            std::memcpy(pixels + y * stride, image.ptr(y), static_cast<size_t>(row));
        return CIS_QR_OK;
    });
}
void __cdecl cis_patch_result_destroy(void *handle)
{
    delete static_cast<PatchResult *>(handle);
}
