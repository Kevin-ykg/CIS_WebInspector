#pragma once
#include <stdint.h>
#include <stddef.h>

#ifdef QR_READER_EXPORTS
#define QR_API __declspec(dllexport)
#else
#define QR_API __declspec(dllimport)
#endif

// 通用读码 C ABI v1；与 CIS 的 cis_qr_* v2 独立，不传 OpenCV/STL 对象。
// x64、cdecl、pack=8。状态：0 命中，1 未命中，-1 参数错误，-2 运行异常，-3 输出不足。
#pragma pack(push, 8)
#ifdef __cplusplus
extern "C" {
#endif
typedef struct QrReaderConfig
{
    uint32_t struct_size;
    int32_t invert_polarity;
    const float *scales_y;
    uint32_t scale_count;
    uint32_t reserved;
} QrReaderConfig;

typedef struct QrReaderImage
{
    uint32_t struct_size;
    int32_t width, height, channels; // Gray8/BGR24/BGRA32
    const uint8_t *pixels;
    uint64_t bytes, stride; // 正步长；允许父图 ROI 的非连续行。
} QrReaderImage;

typedef struct QrReaderHints
{
    uint32_t struct_size;
    int32_t evidence_x, evidence_y, evidence_width, evidence_height;
    uint32_t reserved; // 不使用提示可传 null；全零矩形也表示整图。
} QrReaderHints;

typedef struct QrReaderResult
{
    uint32_t struct_size, found;
    double center_x, center_y, pixel_width, pixel_height;
    uint32_t attempts, text_bytes, strategy_bytes, size_kind;
    // size_kind=0 为 X/Y 投影宽高，1 为透视候选原四角的平均对边长度。
    // 中心保持浮点输入图坐标。text_bytes/strategy_bytes 包含 UTF-8 末尾 NUL。
} QrReaderResult;

QR_API uint32_t __cdecl qr_reader_abi_version(void);
QR_API int32_t __cdecl qr_reader_create(const wchar_t *model_directory, void **handle, char *error, uint32_t capacity);
QR_API int32_t __cdecl qr_reader_configure(void *handle, const QrReaderConfig *config, char *error, uint32_t capacity);
QR_API int32_t __cdecl qr_reader_initialize(void *handle, char *error, uint32_t capacity);
// create 后可直接 detect（首次自动加载模型）；建议在循环前 initialize 预热。
// 同句柄配置/初始化/识别内部串行。调用期间不得 destroy、修改或释放输入像素。
// 不保存输入指针，不截断成功文本，输出不足返回 -3 + required lengths（重试会重新识别）。
QR_API int32_t __cdecl qr_reader_detect(void *handle, const QrReaderImage *image, const QrReaderHints *hints,
    QrReaderResult *result, char *text, uint32_t text_capacity, char *strategy, uint32_t strategy_capacity,
    char *error, uint32_t error_capacity);
QR_API void __cdecl qr_reader_destroy(void *handle);
#ifdef __cplusplus
}
#endif
#pragma pack(pop)
