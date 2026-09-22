#include "qr_reader_api.h"
#include <opencv2/imgcodecs.hpp>
#include <Windows.h>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <vector>

// 本例只通过 C ABI 调用 QrReader.dll。OpenCV 仅用于调用端读取 JPEG：
// DLL 收到的是像素地址/长度/步长，而不是 cv::Mat 或 STL 对象。
struct ReaderHandle
{
    void *value = nullptr;
    ReaderHandle() = default;
    ReaderHandle(const ReaderHandle &) = delete;
    ReaderHandle &operator=(const ReaderHandle &) = delete;
    ~ReaderHandle() { qr_reader_destroy(value); }
};

int wmain(int argc, wchar_t **argv)
{
    if (argc < 3)
    {
        std::cerr << "QrReaderCExample <model-directory> <image> [invert:0|1]\n";
        return 2;
    }
    try
    {
        SetConsoleOutputCP(CP_UTF8);
        if (qr_reader_abi_version() != 1) throw std::runtime_error("QR reader ABI version mismatch");
        std::ifstream file(std::filesystem::path(argv[2]), std::ios::binary);
        if (!file) throw std::runtime_error("Cannot open input image");
        std::vector<uchar> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        cv::Mat image = cv::imdecode(bytes, cv::IMREAD_UNCHANGED);
        if (image.empty() || image.depth() != CV_8U) throw std::runtime_error("An 8-bit image is required");

        char error[4096]{};
        ReaderHandle handle;
        if (qr_reader_create(argv[1], &handle.value, error, sizeof(error)) < 0)
            throw std::runtime_error(error);
        QrReaderConfig config{};
        config.struct_size = sizeof(config);
        config.invert_polarity = argc > 3 && std::wstring(argv[3]) == L"1";
        // 无候选数组时使用 1.0；普通黑色前景默认不反色。
        if (qr_reader_configure(handle.value, &config, error, sizeof(error)) < 0 ||
            qr_reader_initialize(handle.value, error, sizeof(error)) < 0)
            throw std::runtime_error(error);

        QrReaderImage input{};
        input.struct_size = sizeof(input);
        input.width = image.cols; input.height = image.rows; input.channels = image.channels();
        input.pixels = image.data;
        input.stride = image.step[0];
        input.bytes = (image.rows - 1) * input.stride + image.cols * image.elemSize();
        QrReaderResult result{};
        result.struct_size = sizeof(result);
        // UTF-8 最坏字节数大于汉字数量；绝不能把字节容量当成字符数量。
        std::vector<char> text(16384), strategy(4096);
        auto start = std::chrono::steady_clock::now();
        int status = qr_reader_detect(handle.value, &input, nullptr, &result,
            text.data(), static_cast<uint32_t>(text.size()), strategy.data(), static_cast<uint32_t>(strategy.size()),
            error, sizeof(error));
        if (status == -3)
        {
            // 扩大输出空间后重新检测。结果必须是完整文本，不能接受截断后的“成功”。
            text.resize(result.text_bytes); strategy.resize(result.strategy_bytes);
            status = qr_reader_detect(handle.value, &input, nullptr, &result,
                text.data(), static_cast<uint32_t>(text.size()), strategy.data(), static_cast<uint32_t>(strategy.size()),
                error, sizeof(error));
        }
        if (status < 0) throw std::runtime_error(error);
        double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        std::cout << "found=" << result.found << "\ntext=" << text.data()
            << "\ncenter=" << result.center_x << "," << result.center_y
            << "\nsize=" << result.pixel_width << "," << result.pixel_height
            << "\nsize_kind=" << result.size_kind << "\nattempts=" << result.attempts
            << "\nms=" << ms << "\nstrategy=" << strategy.data() << '\n';
        return status; // 0 命中，1 正常未识别；均不是进程异常。
    }
    catch (const std::exception &ex) { std::cerr << ex.what() << '\n'; return 2; }
}
