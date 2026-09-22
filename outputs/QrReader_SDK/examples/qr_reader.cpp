#include "qr/qr_detector.h"
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <Windows.h>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>

// 文件读取在调用端完成；核心仅接收像素，不绑定相机、UI 或图像文件路径。
// filesystem::path + imdecode 支持示例图片的中文 Windows 路径。
int wmain(int argc, wchar_t **argv)
{
    if (argc < 3)
    {
        std::cerr << "QrReaderExample <model-directory> <image> [output-preview.png] [invert:0|1]\n";
        return 2;
    }
    try
    {
        SetConsoleOutputCP(CP_UTF8);
        // OpenCV 模型文件接口使用当前 Windows 代码页；不能表示的路径应移到英文目录。
        std::filesystem::path models(argv[1]), image_path(argv[2]);
        std::ifstream file(image_path, std::ios::binary);
        if (!file) throw std::runtime_error("Cannot open input image");
        std::vector<uchar> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        cv::Mat image = cv::imdecode(bytes, cv::IMREAD_UNCHANGED);
        if (image.empty()) throw std::runtime_error("Cannot decode input image");
        qr::QrDetector detector(models.string());
        qr::Options options;
        options.invert_polarity = argc > 4 && std::wstring(argv[4]) == L"1";
        detector.configure(options);
        detector.initialize();
        auto start = std::chrono::steady_clock::now();
        // 默认整图输入，没有 CIS ROI、保护带、二维码物理尺寸或预先已知文本。
        qr::Result result = detector.detect(image);
        auto ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        std::cout << "found=" << result.found << "\ntext=" << result.text
                  << "\ncenter=" << result.x << "," << result.y << "\nsize=" << result.width << "," << result.height
                  << "\nsize_kind=" << (result.dimensions_are_side_lengths ? "mean-edge-lengths" : "xy-projections")
                  << "\nattempts=" << detector.attempts() << "\nms=" << ms << "\nstrategy=" << result.strategy << "\n";
        if (argc > 3 && result.found)
        {
            cv::Mat preview;
            if (image.channels() == 1) cv::cvtColor(image, preview, cv::COLOR_GRAY2BGR);
            else if (image.channels() == 4) cv::cvtColor(image, preview, cv::COLOR_BGRA2BGR);
            else preview = image.clone();
            cv::circle(preview, {cvRound(result.x), cvRound(result.y)}, 8, {0, 255, 0}, 2);
            // 单独顶部说明栏，避免遮住二维码。绿色点只表示检测中心，不冒充精确四角。
            cv::copyMakeBorder(preview, preview, 60, 0, 0, 0, cv::BORDER_CONSTANT, {25, 25, 25});
            cv::putText(preview, result.text, {12, 38}, cv::FONT_HERSHEY_SIMPLEX, .65, {0, 255, 0}, 1, cv::LINE_AA);
            std::vector<uchar> encoded;
            cv::imencode(".png", preview, encoded);
            std::ofstream output(std::filesystem::path(argv[3]), std::ios::binary);
            output.write(reinterpret_cast<const char *>(encoded.data()), static_cast<std::streamsize>(encoded.size()));
            if (!output) throw std::runtime_error("Cannot write preview");
        }
        return result.found ? 0 : 1;
    }
    catch (const std::exception &ex) { std::cerr << ex.what() << '\n'; return 2; }
}
