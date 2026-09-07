#pragma once
#include <stdint.h>
#include <stddef.h>
#ifdef CIS_VISION_EXPORTS
#define CIS_API __declspec(dllexport)
#else
#define CIS_API __declspec(dllimport)
#endif
#ifdef __cplusplus
extern "C"
{
#endif

    // C ABI v2：接口中不暴露 cv::Mat、STL、C++ 异常或需要跨模块释放的内存。
    // C# 通过 P/Invoke 传入并持有全部缓冲区，DLL 只在当前同步调用期间借用。
    // 这样可以避免不同 CRT、编译器版本或 Debug/Release 组合造成的 ABI 与释放问题。
    enum CisQrStatus
    {
        CIS_QR_OK = 0,
        CIS_QR_NOT_FOUND = 1,
        CIS_QR_INVALID = -1,
        CIS_QR_ERROR = -2,
        CIS_QR_BUFFER_SMALL = -3
    };

    // 一次采集会话使用的二维码参数快照。
    // struct_size 用于发现 C#/C++ 结构体布局不一致；新增字段时必须同步提升 ABI 版本。
    typedef struct CisQrConfig
    {
        uint32_t struct_size;       ///< 调用方填写 sizeof(CisQrConfig)。
        int32_t roi_x;              ///< 横向检测 ROI 起点；纵向始终覆盖完整帧。
        int32_t roi_width;          ///< 横向检测 ROI 宽度；小于等于 0 表示使用剩余全宽。
        int32_t invert_polarity;    ///< 非 0 时先反色，适配正常生产中的白色前景二维码。
        const float *scales_y;      ///< 仅在 configure 期间借用；内部立即复制、校验并去重。
        uint32_t scale_count;       ///< scales_y 的元素数量。
        uint32_t reserved;          ///< 保留字段，当前必须按 0 传入。
    } CisQrConfig;

    /// 单次检测结果。坐标和宽高均已从所有中间缩放、局部裁切和补白图还原到输入帧坐标系。
    typedef struct CisQrResult
    {
        uint32_t struct_size;       ///< 调用方填写 sizeof(CisQrResult)。
        uint32_t found;             ///< 1 表示已解出非空文本；0 表示未命中。
        int32_t center_x;           ///< 二维码中心 X，输入帧像素坐标。
        int32_t center_y;           ///< 二维码中心 Y，输入帧像素坐标。
        double pixel_width;         ///< 四边形两组对边的 X 投影宽度，用于拼接与物理尺度换算。
        double pixel_height;        ///< 四边形两组对边的 Y 投影高度，用于线扫方向尺度换算。
        uint32_t attempts;          ///< 本次实际调用 WeChatQRCode 的次数，用于分析耗时长尾。
        uint32_t text_bytes;        ///< UTF-8 文本字节数，包含末尾 NUL；空间不足时不截断。
        uint32_t strategy_bytes;    ///< UTF-8 诊断策略字节数，包含末尾 NUL。
        uint32_t reserved;          ///< 保留字段。
    } CisQrResult;

    /// 返回当前 DLL 的 C ABI 版本。C# 必须在创建实例前核对该值。
    CIS_API uint32_t __cdecl cis_qr_abi_version(void);

    /// 创建检测器上下文。model_directory 指向四个 WeChatQRCode 模型所在目录。
    /// 成功后句柄所有权交给调用方，并必须最终传给 cis_qr_destroy。
    CIS_API int32_t __cdecl cis_qr_create(const wchar_t *model_directory, void **handle, char *error,
                                          uint32_t error_capacity);

    /// 复制会话配置。scales_y 不会在函数返回后被保存或继续借用。
    CIS_API int32_t __cdecl cis_qr_configure(void *handle, const CisQrConfig *config, char *error,
                                             uint32_t error_capacity);

    /// 延迟加载并预热模型，避免首个生产帧承担 DNN 初始化开销。
    CIS_API int32_t __cdecl cis_qr_initialize(void *handle, char *error, uint32_t error_capacity);

    /// 同步检测且只读借用输入像素。支持 Gray8、BGR24、BGRA32，stride 可大于有效行宽。
    /// text/strategy 由调用方分配；不足时返回 CIS_QR_BUFFER_SMALL，并在结果中给出所需长度。
    /// configure/detect/initialize 在同一实例内串行；调用者不得在调用期间 destroy。
    CIS_API int32_t __cdecl cis_qr_detect(void *handle, const uint8_t *pixels, uint64_t buffer_bytes, int32_t width,
                                          int32_t height, uint64_t stride, int32_t channels, CisQrResult *result,
                                          char *text, uint32_t text_capacity, char *strategy,
                                          uint32_t strategy_capacity, char *error, uint32_t error_capacity);

    /// 释放检测器上下文。允许传入空指针；不得与同一句柄上的其他调用并发执行。
    CIS_API void __cdecl cis_qr_destroy(void *handle);
#ifdef __cplusplus
}
#endif
