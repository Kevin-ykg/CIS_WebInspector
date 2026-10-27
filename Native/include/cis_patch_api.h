#pragma once
#include "cis_qr_api.h"

// 不依赖调用方此前的 #pragma pack 状态；退出本头文件时恢复原状态。
#pragma pack(push, 8)

// 零件检测 ABI 独立版本化，不改变已部署二维码接口的版本和结构体。
// 所有图像只传像素/尺寸/步长；所有内存由分配方释放，禁止跨 DLL 传 cv::Mat/STL。
#ifdef __cplusplus
extern "C"
{
#endif
    typedef struct CisPatchImage
    {
        uint32_t struct_size;
        int32_t width, height, channels; // Gray8/BGR24/BGRA32，正向 stride，可为父图 ROI。
        uint64_t stride, buffer_bytes;   // 可访问长度至少为 (height-1)*stride + width*channels。
        const uint8_t *pixels;           // 同步只读借用，detect 返回后不再持有。
    } CisPatchImage;

    // 长度/面积参数沿用 AppConfig 的 mm/mm²；阈值为 Gray8 值。显式 int32 代替 ABI bool。
    typedef struct CisPatchConfig
    {
        uint32_t struct_size;
        int32_t enable_alignment, enable_fine_line, minimum_scaled_width;
        int32_t alpha_threshold, cis_threshold, output_flags, reserved;
        double detection_scale, layout_dpi;
        double tolerance_inner_mm, tolerance_outer_mm, exclusion_outer_mm, exclusion_inner_mm;
        double area_inner_mm2, area_outer_mm2, fine_min_length_mm, fine_max_width_mm;
        // 传入图像可含真实搜索余量；以下矩形指定最终缺陷判定的核心 ROI。
        // width/height 为 0 时兼容“整张输入就是核心 ROI”的独立测试入口。
        int32_t core_x, core_y, core_width, core_height;
    } CisPatchConfig;

    // 配准与产品缺陷结果分离。Review 时使用全局图，AppliedDegraded 时
    // 使用已改善整体误差的局部图；两种情况均继续检测并独立提示复核。
    enum CisPatchAlignmentStatus
    {
        CIS_PATCH_ALIGNMENT_DISABLED = 0,
        CIS_PATCH_ALIGNMENT_NOT_NEEDED = 1,
        CIS_PATCH_ALIGNMENT_APPLIED = 2,
        CIS_PATCH_ALIGNMENT_REVIEW = 3,
        CIS_PATCH_ALIGNMENT_ERROR = 4,
        // 已使用局部矩阵，整体九区误差优于全局，但局部质量未达优质标准。
        // 保留既有枚举值和 ABI 布局；调用方须继续检测并提示人工复核。
        CIS_PATCH_ALIGNMENT_APPLIED_DEGRADED = 5
    };
    enum CisPatchAlignmentModel
    {
        CIS_PATCH_MODEL_NONE = 0,
        CIS_PATCH_MODEL_SIMILARITY = 1,
        CIS_PATCH_MODEL_CONSTRAINED_AFFINE = 2,
        CIS_PATCH_MODEL_HOMOGRAPHY = 3
    };
    enum CisPatchAlignmentReason
    {
        CIS_PATCH_REASON_NONE = 0,
        CIS_PATCH_REASON_CONTEXT = 1,
        CIS_PATCH_REASON_FEATURES = 2,
        CIS_PATCH_REASON_MATCHES = 3,
        CIS_PATCH_REASON_GEOMETRY = 4,
        CIS_PATCH_REASON_EDGE_QUALITY = 5,
        CIS_PATCH_REASON_PERIOD_AMBIGUOUS = 6,
        CIS_PATCH_REASON_MODEL_INSTABILITY = 7,
        CIS_PATCH_REASON_EXCEPTION = 8,
        CIS_PATCH_REASON_EDGE_QUALITY_DEGRADED = 9
    };
    typedef struct CisPatchAlignmentInfo
    {
        uint32_t struct_size;
        int32_t status, model, reason;
        int32_t matches, inliers;
        double inlier_ratio, rms_original_px, edge_before_work_px, edge_after_work_px;
        double leave_out_change_original_px, alignment_ms;
    } CisPatchAlignmentInfo;

    // 每条记录对应一个最终过门限的缺陷。显示框与紧致框的物理宽高分别存储。
    // area_mm2 来自连通域/断口掩膜真实前景像素数，绝不是外接框宽*高。
    typedef struct CisPatchDefect
    {
        int32_t kind;                                    // 0 内部、1 外部、2 细线断裂。
        int32_t x, y, width, height;                     // 原始零件分辨率下的显示框。
        int32_t work_x, work_y, work_width, work_height; // 普通检测尺度，用于现有三联图。
        int32_t reserved;
        double width_mm, height_mm, area_mm2;
    } CisPatchDefect;

    typedef struct CisPatchSummary
    {
        uint32_t struct_size, defect_count;
        int32_t is_pass, inner_count, outer_count, fine_count;
        double max_inner_mm2, max_outer_mm2, max_fine_length_mm, max_fine_width_mm;
        double detection_scale, alignment_ms, detection_ms; // detection_ms 为整件原生总时长，包含 alignment_ms。
    } CisPatchSummary;

    typedef struct CisPatchCacheStats
    {
        uint32_t struct_size, entries;
        uint64_t hits, misses, comparisons;
        double comparison_ms, quick_key_ms;
    } CisPatchCacheStats;

    enum CisPatchOutputFlags
    {
        CIS_PATCH_VISUALIZATION = 1,
        CIS_PATCH_EDGE_MASK = 2,
        CIS_PATCH_CIS_BINARY = 4
    };
    // 输出图类型：前四张在普通检测尺度，后两张在原始零件尺度；均为 Gray8。
    enum CisPatchImageKind
    {
        CIS_PATCH_ALPHA = 0,
        CIS_PATCH_CIS = 1,
        CIS_PATCH_INNER = 2,
        CIS_PATCH_OUTER = 3,
        CIS_PATCH_EDGE = 4,
        CIS_PATCH_CIS_ORIGINAL = 5
    };

    CIS_API uint32_t __cdecl cis_patch_abi_version(void);
    // 一个 cache 对应一批零件；worker 共享其只读条目，但各自独占 SIFT。
    // 当前空间约束匹配直接比较描述子距离，不创建 BFMatcher。
    // 创建顺序 cache -> worker -> result，常规按相反顺序释放。worker 内部共享拥有 cache。
    // destroy 不能与使用同一句柄的调用并发；重复/伪造句柄或悬空像素指针不属于可恢复参数错误。
    CIS_API int32_t __cdecl cis_patch_cache_create(void **cache, char *error, uint32_t capacity);
    CIS_API int32_t __cdecl cis_patch_cache_stats(void *cache, CisPatchCacheStats *stats, char *error,
                                                  uint32_t capacity);
    CIS_API void __cdecl cis_patch_cache_destroy(void *cache);
    CIS_API int32_t __cdecl cis_patch_worker_create(void *cache, void **worker, char *error, uint32_t capacity);
    CIS_API void __cdecl cis_patch_worker_destroy(void *worker);
    // 结果拥有独立内存；两步读取不会重新运行算法。失败时 *result=null，异常转状态码和 UTF-8。
    CIS_API int32_t __cdecl cis_patch_detect(void *worker, const CisPatchImage *alpha, const CisPatchImage *cis,
                                             const CisPatchConfig *config, void **result, char *error,
                                             uint32_t capacity);
    CIS_API int32_t __cdecl cis_patch_result_summary(void *result, CisPatchSummary *summary, char *error,
                                                     uint32_t capacity);
    CIS_API int32_t __cdecl cis_patch_result_alignment(void *result, CisPatchAlignmentInfo *info, char *error,
                                                       uint32_t capacity);
    CIS_API int32_t __cdecl cis_patch_result_defects(void *result, CisPatchDefect *defects, uint32_t count, char *error,
                                                     uint32_t capacity);
    // 先查询 UTF-8 字节数（含 NUL）再读取日志；文本/缺陷数组容量不足返回 BUFFER_SMALL。
    CIS_API int32_t __cdecl cis_patch_result_log(void *result, char *text, uint32_t capacity, uint32_t *required);
    CIS_API int32_t __cdecl cis_patch_result_image(void *result, int32_t kind, CisPatchImage *info, char *error,
                                                   uint32_t capacity);
    // 图像先查尺寸/步长再复制；未申请的图像、无效像素指针描述或容量不足返回 INVALID。
    // 复制前检查整个范围，不会先写一半图像再报告空间不足。info 不暴露结果内部 pixels。
    CIS_API int32_t __cdecl cis_patch_result_copy_image(void *result, int32_t kind, uint8_t *pixels, uint64_t bytes,
                                                        uint64_t stride, char *error, uint32_t capacity);
    CIS_API void __cdecl cis_patch_result_destroy(void *result);
#ifdef __cplusplus
}
#endif
#pragma pack(pop)
