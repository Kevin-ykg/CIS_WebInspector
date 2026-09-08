#include "alignment_internal.h"
#include <cstring>
#include <memory>

namespace
{
void message(char *out, uint32_t capacity, const char *text) noexcept
{
    if (!out || !capacity)
        return;
    size_t n = std::min<size_t>(capacity - 1, std::strlen(text));
    std::memcpy(out, text, n);
    out[n] = 0;
}
template <class F> int32_t guarded(char *error, uint32_t capacity, F action) noexcept
{
    message(error, capacity, "");
    try
    {
        action();
        return 0;
    }
    catch (const std::invalid_argument &e)
    {
        message(error, capacity, e.what());
        return -1;
    }
    catch (const std::exception &e)
    {
        message(error, capacity, e.what());
        return -2;
    }
    catch (...)
    {
        message(error, capacity, "Unknown native alignment error");
        return -2;
    }
}
cv::Mat image_view(const CisAlignmentImage *image)
{
    if (!image || image->struct_size != sizeof(*image))
        throw std::invalid_argument("Alignment image ABI layout mismatch");
    if (!image->pixels || image->width <= 0 || image->height <= 0 ||
        (image->channels != 1 && image->channels != 3 && image->channels != 4))
        throw std::invalid_argument("Gray8/BGR24/BGRA32 image required");
    uint64_t row = static_cast<uint64_t>(image->width) * image->channels;
    if (image->stride < row || (image->height > 1 && image->stride > (UINT64_MAX - row) / (image->height - 1)) ||
        image->buffer_bytes < image->stride * (image->height - 1) + row)
        throw std::invalid_argument("Image stride/length invalid");
    if (image->buffer_bytes > PTRDIFF_MAX ||
        reinterpret_cast<uintptr_t>(image->pixels) > UINTPTR_MAX - image->buffer_bytes)
        throw std::invalid_argument("Image address range overflow");
    // 创建的 Mat 仅是视图，不取得像素所有权；离开 API 时它不会释放 C# 内存。
    return {image->height, image->width, CV_MAKETYPE(CV_8U, image->channels), const_cast<uint8_t *>(image->pixels),
            static_cast<size_t>(image->stride)};
}
template <class T>
int32_t copy_records(void *handle, const std::vector<T> alignment::Result::*member, T *output, uint32_t count) noexcept
{
    if (!handle)
        return -1;
    auto &items = static_cast<alignment::Result *>(handle)->*member;
    if (count < items.size())
        return -3;
    if (!items.empty() && !output)
        return -1;
    if (!items.empty())
        std::memcpy(output, items.data(), items.size() * sizeof(T));
    return 0;
}
} // namespace

// 双端检查 pack/大小，杜绝 CLR bool、平台位宽和字段顺序造成“看似调用成功”的内存错读。
static_assert(sizeof(CisAlignmentImage) == 40 && sizeof(CisAlignmentAnchor) == 48 &&
                  sizeof(CisAlignmentConfig) == 192 && sizeof(CisAlignmentMark) == 40 &&
                  sizeof(CisAlignmentControl) == 96 && sizeof(CisWhiteInkSample) == 64 &&
                  sizeof(CisAlignmentSummary) == 296,
              "Alignment ABI changed");
uint32_t __cdecl cis_alignment_abi_version(void)
{
    return 1;
}
int32_t __cdecl cis_alignment_compute(const CisAlignmentImage *cis, const CisAlignmentImage *tiff,
                                      const CisAlignmentAnchor *anchor, const CisAlignmentConfig *config, int32_t mode,
                                      void **result, char *error, uint32_t capacity)
{
    if (result)
        *result = nullptr;
    return guarded(error, capacity, [&] {
        if (!result || !anchor || anchor->struct_size != sizeof(*anchor) || !config ||
            config->struct_size != sizeof(*config) || (mode != 0 && mode != 1))
            throw std::invalid_argument("Alignment ABI/config/mode invalid");
        if (anchor->GlobalCenterY < 0 || anchor->SegmentStartGlobalY < 0)
            throw std::invalid_argument("Global Y must be nonnegative");
        // 防止错误 ABI/损坏配置申请不受限的控制网格；不约束实际的毫米几何或正常参数范围。
        if (config->SideMarkPairCount > 4096 || config->NonlinearRemapStripeRows > 1048576)
            throw std::invalid_argument("Alignment allocation limit exceeded");
        auto owned = std::make_unique<alignment::Result>();
        if (mode == 1 && !config->EnableWhiteInkInspection)
        { /* 关闭时不触碰输入像素。 */
        }
        else
        {
            cv::Mat source = image_view(cis);
            if (mode == 0)
                alignment::compute(source, image_view(tiff), *anchor, *config, *owned);
            else
            {
                auto start = alignment::Clock::now();
                owned->Ink = alignment::inspect_bottom(source, *anchor, *config);
                owned->Diagnostic = owned->Ink.Diagnostic;
                owned->DetectionMs = alignment::elapsed(start);
            }
        }
        *result = owned.release(); // 全部完成后发布句柄，任何异常前都由 unique_ptr 自动释放。
    });
}
int32_t __cdecl cis_alignment_summary(void *result, CisAlignmentSummary *summary)
{
    if (!result || !summary || summary->struct_size != sizeof(*summary))
        return -1;
    auto &r = *static_cast<alignment::Result *>(result);
    *summary = {};
    summary->struct_size = sizeof(*summary);
    summary->has_transform = !r.H.empty();
    summary->mode = r.Mode;
    summary->quality = r.Quality;
    summary->optimal_threshold = r.Threshold;
    summary->white_status = r.Ink.Status;
    summary->streaking = r.Ink.HasStreaking;
    summary->stripe_rows = r.StripeRows;
    summary->mark_count = static_cast<uint32_t>(r.Marks.size());
    summary->control_count = static_cast<uint32_t>(r.Controls.size());
    summary->sample_count = static_cast<uint32_t>(r.Ink.Samples.size());
    summary->grid_rows = static_cast<uint32_t>(r.GridY.size());
    if (!r.H.empty())
        for (int y = 0; y < 3; ++y)
            for (int x = 0; x < 3; ++x)
            {
                summary->homography[y * 3 + x] = r.H.at<double>(y, x);
                summary->inverse[y * 3 + x] = r.Inverse.at<double>(y, x);
            }
    summary->ink_percent = r.Ink.InkLevelPercent;
    summary->mark_mean = r.Ink.MarkMean;
    summary->mark_variance = r.Ink.MarkVariance;
    summary->background_mean = r.Ink.BackgroundMean;
    summary->contrast = r.Ink.Contrast;
    summary->detection_ms = r.DetectionMs;
    summary->map_ms = r.MapMs;
    summary->remap_ms = r.RemapMs;
    summary->loo_median_mm = r.LooMedian;
    summary->loo_max_mm = r.LooMaximum;
    summary->temporary_bytes = r.TemporaryBytes;
    summary->white_x = r.Ink.SearchRegion.x;
    summary->white_y = r.Ink.SearchRegion.y;
    summary->white_width = r.Ink.SearchRegion.width;
    summary->white_height = r.Ink.SearchRegion.height;
    return 0;
}
int32_t __cdecl cis_alignment_marks(void *result, CisAlignmentMark *output, uint32_t count)
{
    return copy_records(result, &alignment::Result::Marks, output, count);
}
int32_t __cdecl cis_alignment_controls(void *result, CisAlignmentControl *output, uint32_t count)
{
    return copy_records(result, &alignment::Result::Controls, output, count);
}
int32_t __cdecl cis_alignment_samples(void *result, CisWhiteInkSample *output, uint32_t count)
{
    if (!result)
        return -1;
    auto &samples = static_cast<alignment::Result *>(result)->Ink.Samples;
    if (count < samples.size())
        return -3;
    if (!samples.empty() && !output)
        return -1;
    if (!samples.empty())
        std::memcpy(output, samples.data(), samples.size() * sizeof(*output));
    return 0;
}
int32_t __cdecl cis_alignment_log(void *result, int32_t kind, char *output, uint32_t capacity, uint32_t *required)
{
    if (!result || !required || (kind != 0 && kind != 1))
        return -1;
    auto &r = *static_cast<alignment::Result *>(result);
    auto &text = kind == 0 ? r.Diagnostic : r.Ink.Diagnostic;
    if (text.size() >= UINT32_MAX)
        return -2;
    *required = static_cast<uint32_t>(text.size() + 1);
    if (!output || capacity < *required)
        return -3;
    std::memcpy(output, text.c_str(), *required);
    return 0;
}
int32_t __cdecl cis_alignment_warp(void *result, const CisAlignmentImage *cis, const CisAlignmentImage *output,
                                   char *error, uint32_t capacity)
{
    return guarded(error, capacity, [&] {
        if (!result)
            throw std::invalid_argument("Null alignment result");
        cv::Mat source = image_view(cis), target = image_view(output);
        if (source.type() != target.type())
            throw std::invalid_argument("Warp input/output channel mismatch");
        auto src = reinterpret_cast<uintptr_t>(cis->pixels), dst = reinterpret_cast<uintptr_t>(output->pixels);
        if (src < dst + output->buffer_bytes && dst < src + cis->buffer_bytes)
            throw std::invalid_argument("Warp input/output must not overlap");
        alignment::warp(source, target, *static_cast<alignment::Result *>(result));
    });
}
void __cdecl cis_alignment_destroy(void *result)
{
    delete static_cast<alignment::Result *>(result);
}
