#pragma once
#include <Windows.h>
#include <algorithm>
#include <cstring>
#include <cstdint>
#include <stdexcept>
#include <string>

namespace qr_api_util
{
// 所有跨 ABI 的字符串都写入调用方缓冲区，并保证在容量允许时以 NUL 结尾。
// 这里不返回 std::string，避免调用方使用不同 CRT 释放 C++ 内存。
inline void message(char *output, uint32_t capacity, const char *text) noexcept
{
    if (!output || !capacity)
        return;
    size_t count = std::min(std::strlen(text), static_cast<size_t>(capacity - 1));
    std::memcpy(output, text, count);
    output[count] = 0;
}

// C# 以 UTF-16 传入模型目录；当前 OpenCV Windows 文件接口使用本机代码页路径。
// 无法无损转换时直接报错，避免模型路径被静默替换后表现为“模型文件缺失”。
inline std::string model_path(const wchar_t *value)
{
    if (!value || !*value)
        throw std::invalid_argument("Model directory is empty");
    UINT codepage = GetACP();
    BOOL substituted = FALSE;
    BOOL *used = codepage == CP_UTF8 ? nullptr : &substituted;
    DWORD flags = codepage == CP_UTF8 ? WC_ERR_INVALID_CHARS : WC_NO_BEST_FIT_CHARS;
    int count = WideCharToMultiByte(codepage, flags, value, -1, nullptr, 0, nullptr, used);
    if (count <= 0)
        throw std::invalid_argument("Cannot encode model directory");
    std::string result(count, '\0');
    if (!WideCharToMultiByte(codepage, flags, value, -1, result.data(), count, nullptr, used) || substituted)
        throw std::invalid_argument("Model path cannot be represented in the Windows system code page");
    result.pop_back();
    return result;
}

// C++ 异常不得越过 C ABI。参数错误与运行时错误使用不同状态码，详细原因写入 error。
template <class Function> int32_t guarded(char *error, uint32_t capacity, Function action) noexcept
{
    message(error, capacity, "");
    try
    {
        return action();
    }
    catch (const std::invalid_argument &ex)
    {
        message(error, capacity, ex.what());
        return -1;
    }
    catch (const std::exception &ex)
    {
        message(error, capacity, ex.what());
        return -2;
    }
    catch (...)
    {
        message(error, capacity, "Unknown native failure");
        return -2;
    }
}
} // namespace
