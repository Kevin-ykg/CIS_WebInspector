#include "qr_reader_api.h"
#include "qr/qr_detector.h"
#include "qr_api_support.h"
#include <mutex>

namespace
{
struct Reader
{
    explicit Reader(std::string models) : detector(std::move(models)) {}
    qr::QrDetector detector;
    std::mutex mutex;
};
Reader &reader(void *handle)
{
    if (!handle) throw std::invalid_argument("Null QR reader handle");
    return *static_cast<Reader *>(handle);
}
}
using namespace qr_api_util;
static_assert(sizeof(QrReaderConfig) == 24 && sizeof(QrReaderImage) == 40 &&
              sizeof(QrReaderHints) == 24 && sizeof(QrReaderResult) == 56, "QR reader ABI layout changed");

uint32_t __cdecl qr_reader_abi_version(void) { return 1; }
int32_t __cdecl qr_reader_create(const wchar_t *models, void **handle, char *error, uint32_t capacity)
{
    if (handle) *handle = nullptr;
    return guarded(error, capacity, [&]() -> int32_t {
        if (!handle) throw std::invalid_argument("Null handle output");
        auto owned = std::make_unique<Reader>(model_path(models));
        *handle = owned.release();
        return 0;
    });
}
int32_t __cdecl qr_reader_configure(void *handle, const QrReaderConfig *config, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() -> int32_t {
        if (!config || config->struct_size != sizeof(QrReaderConfig) || config->reserved ||
            config->scale_count > 4096 || (config->scale_count && !config->scales_y))
            throw std::invalid_argument("Invalid QR reader configuration");
        auto &context = reader(handle);
        qr::Options options;
        options.invert_polarity = config->invert_polarity != 0;
        options.scales_y.clear();
        if (config->scale_count) options.scales_y.assign(config->scales_y, config->scales_y + config->scale_count);
        std::lock_guard<std::mutex> lock(context.mutex);
        context.detector.configure(options);
        return 0;
    });
}
int32_t __cdecl qr_reader_initialize(void *handle, char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&]() -> int32_t {
        auto &context = reader(handle);
        std::lock_guard<std::mutex> lock(context.mutex);
        context.detector.initialize();
        return 0;
    });
}
int32_t __cdecl qr_reader_detect(void *handle, const QrReaderImage *image, const QrReaderHints *hints,
    QrReaderResult *output, char *text, uint32_t text_capacity, char *strategy, uint32_t strategy_capacity,
    char *error, uint32_t error_capacity)
{
    message(text, text_capacity, ""); message(strategy, strategy_capacity, "");
    return guarded(error, error_capacity, [&]() -> int32_t {
        if (!output || output->struct_size != sizeof(QrReaderResult))
            throw std::invalid_argument("Invalid result structure");
        *output = {}; output->struct_size = sizeof(QrReaderResult);
        if (!image || image->struct_size != sizeof(QrReaderImage) || !image->pixels || image->width <= 0 ||
            image->height <= 0 || (image->channels != 1 && image->channels != 3 && image->channels != 4))
            throw std::invalid_argument("Invalid image: Gray8/BGR24/BGRA32 required");
        uint64_t row = static_cast<uint64_t>(image->width) * image->channels;
        if (image->stride < row ||
            (image->height > 1 && image->stride > (UINT64_MAX - row) / (image->height - 1)) ||
            image->bytes < image->stride * (image->height - 1) + row)
            throw std::invalid_argument("Image length/stride overflow or insufficient input buffer");
        if (!text || !text_capacity || !strategy || !strategy_capacity)
            throw std::invalid_argument("Output buffers are required");
        qr::DetectHints hints_value;
        if (hints)
        {
            if (hints->struct_size != sizeof(QrReaderHints) || hints->reserved)
                throw std::invalid_argument("Invalid hints structure");
            hints_value.evidence_region = {hints->evidence_x, hints->evidence_y, hints->evidence_width, hints->evidence_height};
        }
        auto &context = reader(handle);
        std::lock_guard<std::mutex> lock(context.mutex);
        cv::Mat source(image->height, image->width, CV_MAKETYPE(CV_8U, image->channels),
            const_cast<uint8_t *>(image->pixels), static_cast<size_t>(image->stride));
        qr::Result result;
        try { result = context.detector.detect(source, hints_value); }
        catch (...) { output->attempts = context.detector.attempts(); throw; }
        output->attempts = context.detector.attempts();
        if (!result.found) return 1;
        output->text_bytes = static_cast<uint32_t>(result.text.size() + 1);
        output->strategy_bytes = static_cast<uint32_t>(result.strategy.size() + 1);
        if (text_capacity < output->text_bytes || strategy_capacity < output->strategy_bytes)
        {
            message(error, error_capacity, "Result buffer too small; required lengths are returned");
            return -3;
        }
        output->found = 1;
        output->center_x = result.x; output->center_y = result.y;
        output->pixel_width = result.width; output->pixel_height = result.height;
        output->size_kind = result.dimensions_are_side_lengths ? 1 : 0;
        std::memcpy(text, result.text.c_str(), output->text_bytes);
        std::memcpy(strategy, result.strategy.c_str(), output->strategy_bytes);
        return 0;
    });
}
void __cdecl qr_reader_destroy(void *handle)
{
    try { delete static_cast<Reader *>(handle); } catch (...) { }
}
