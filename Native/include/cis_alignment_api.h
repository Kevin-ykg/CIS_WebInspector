#pragma once
#include "cis_patch_api.h"

// 全局对准/白墨 ABI 单独版本化。图像沿用明确的像素描述符，不传 cv::Mat、STL 或 C++ bool。
// 所有输入仅在同步调用期间借用；结果持有独立的矩阵/控制网格，不持有输入大图。
#pragma pack(push, 8)
#ifdef __cplusplus
extern "C"
{
#endif
    typedef CisPatchImage CisAlignmentImage;
    typedef struct CisAlignmentAnchor
    {
        uint32_t struct_size, reserved;
        double CenterX;
        int64_t GlobalCenterY, SegmentStartGlobalY;
        double PixelWidth, PixelHeight;
    } CisAlignmentAnchor;

    // 名称与 MarkAlignmentOptions 一致，便于维护者逐项核对。物理量均为 mm，灰度为 0..255。
    typedef struct CisAlignmentConfig
    {
        uint32_t struct_size;
        int32_t EnableWhiteInkInspection, EnableSideMarkNonlinearAlignment, SideMarkPairCount;
        int32_t SideMarkMinValidPerColumn, NonlinearRemapStripeRows;
        double LayoutDpi, TiffHeightMm, TiffTopCenterYmm, TiffBottomOffsetMm, MarkDiameterMm;
        double CisRowSpacingMm, QrPhysicalHeightMm, QrPhysicalWidthMm;
        double InitialSearchMarginMm, ExpandedSearchMarginMm, MinCircularityTiff, MinCircularityCis;
        double WhiteInkNormalGray, WhiteInkStreakStdDevThreshold;
        double SideMarkDiameterMm, SheetWidthMm, TiffSideMarkEdgeOffsetMm, CisQrToLeftMarkMm, CisSideMarkSpanMm;
        double SideMarkInitialSearchMarginMm, SideMarkExpandedSearchMarginMm;
    } CisAlignmentConfig;

    typedef struct CisAlignmentMark
    {
        int32_t row, index; // row=0 Top/1 Bottom；index 是 TIFF 排内原编号，缺点后不重新编号。
        double tiff_x, tiff_y, cis_x, cis_y;
    } CisAlignmentMark;
    typedef struct CisAlignmentControl
    {
        int32_t row, column, flags, reserved; // flags:1 实测有效、2 孤立缺点插值、4 虚拟零残差。
        double expected_x, expected_y, detected_tiff_x, detected_tiff_y;
        double coarse_x, coarse_y, detected_cis_x, detected_cis_y, residual_x, residual_y;
    } CisAlignmentControl;
    typedef struct CisWhiteInkSample
    {
        int32_t index, detected;
        double x, y, radius, mean, variance, background, contrast;
    } CisWhiteInkSample;
    typedef struct CisAlignmentSummary
    {
        uint32_t struct_size;
        int32_t has_transform, mode, quality, optimal_threshold, white_status, streaking, stripe_rows;
        uint32_t mark_count, control_count, sample_count, grid_rows;
        double homography[9], inverse[9]; // 行优先；CIS -> TIFF 与 TIFF -> CIS，均为原分辨率像素。
        double ink_percent, mark_mean, mark_variance, background_mean, contrast;
        double detection_ms, map_ms, remap_ms, loo_median_mm, loo_max_mm;
        uint64_t temporary_bytes;
        int32_t white_x, white_y, white_width, white_height;
    } CisAlignmentSummary;

    CIS_API uint32_t __cdecl cis_alignment_abi_version(void);
    // mode=0 完整对准，mode=1 独立白墨（允许 tiff=null，不依赖排版文件）。
    // 可预期的 Mark 不足返回成功 + has_transform=0 + 诊断，白墨结果仍然可读。
    // 参数/运行库异常返回负状态码并清空 result。调用方必须检查状态，禁止静默调用旧算法。
    CIS_API int32_t __cdecl cis_alignment_compute(const CisAlignmentImage *cis, const CisAlignmentImage *tiff,
                                                  const CisAlignmentAnchor *anchor, const CisAlignmentConfig *config,
                                                  int32_t mode, void **result, char *error, uint32_t capacity);
    CIS_API int32_t __cdecl cis_alignment_summary(void *result, CisAlignmentSummary *summary);
    CIS_API int32_t __cdecl cis_alignment_marks(void *result, CisAlignmentMark *output, uint32_t count);
    CIS_API int32_t __cdecl cis_alignment_controls(void *result, CisAlignmentControl *output, uint32_t count);
    CIS_API int32_t __cdecl cis_alignment_samples(void *result, CisWhiteInkSample *output, uint32_t count);
    // kind=0 完整诊断，kind=1 白墨诊断。required 包含 UTF-8 终止零，查询长度返回 buffer-small。
    CIS_API int32_t __cdecl cis_alignment_log(void *result, int32_t kind, char *output, uint32_t capacity,
                                              uint32_t *required);
    // output 是调用方已分配的可写 Gray/BGR/BGRA 像素。写入前验证容量；不额外复制整幅输出。
    // 同一句柄的 warp/summary/destroy 不得并发。不同结果可独立工作，不共享可变算法状态。
    CIS_API int32_t __cdecl cis_alignment_warp(void *result, const CisAlignmentImage *cis,
                                               const CisAlignmentImage *output, char *error, uint32_t capacity);
    CIS_API void __cdecl cis_alignment_destroy(void *result);
#ifdef __cplusplus
}
#endif
#pragma pack(pop)
