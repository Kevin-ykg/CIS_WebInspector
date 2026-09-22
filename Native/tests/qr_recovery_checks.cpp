#include "qr/qr_detector.h"
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <iostream>
#include <stdexcept>

// 开发者按需运行，不随生产程序启动。已知文本仅作测试断言，绝不传入识别器。
// 输入为用户保留的两张真实原图，禁止拿算法自己生成的二维码代替困难样本。
int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc != 4) throw std::invalid_argument("QrReaderRecoveryChecks <models> <test1.png> <test2.png>");
        qr::QrDetector detector(std::filesystem::path(argv[1]).string());
        detector.initialize();
        const char *expected[] = {"ZJ_202607310321032_RT", "ZJ_202607318430843_RT"};
        int checks = 0;
        for (int file_index = 0; file_index < 2; ++file_index)
        {
            std::ifstream file(std::filesystem::path(argv[file_index + 2]), std::ios::binary);
            std::vector<uchar> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
            cv::Mat image = cv::imdecode(bytes, cv::IMREAD_COLOR);
            if (image.empty()) throw std::runtime_error("image missing");
            // 验证不同像素存储形式不改变本次两张原图的文本结果；这不代表所有
            // 旋转/缩放后的困难图都可解码，DNN 和模板取证仍有各自的检测边界。
            for (int format = 0; format < 3; ++format)
            {
                cv::Mat input, parent;
                if (format == 0) input = image;
                else if (format == 1) cv::cvtColor(image, input, cv::COLOR_BGR2BGRA);
                else
                {
                    parent = cv::Mat(image.rows + 40, image.cols + 80, image.type(), cv::Scalar(0, 0, 0));
                    input = parent(cv::Rect(31, 17, image.cols, image.rows));
                    image.copyTo(input);
                }
                auto copy = input.clone();
                for (int repeat = 0; repeat < 2; ++repeat)
                {
                    auto result = detector.detect(input);
                    std::cout << "sample=" << file_index + 1 << " format=" << format << " repeat=" << repeat
                              << " found=" << result.found << " attempts=" << detector.attempts()
                              << " text=" << result.text << std::endl;
                    if (!result.found || result.text != expected[file_index])
                        throw std::runtime_error("real sample text mismatch");
                    if (result.x < 0 || result.y < 0 || result.x >= input.cols || result.y >= input.rows ||
                        result.width <= 0 || result.height <= 0 || cv::norm(input, copy, cv::NORM_INF) != 0)
                        throw std::runtime_error("invalid geometry or modified input");
                    ++checks;
                }
            }
            // 原图左侧只有网格背景，无二维码。验证复杂背景不会因恢复步骤变成命中。
            if (detector.detect(image(cv::Rect(0, 0, image.cols / 5, image.rows))).found)
                throw std::runtime_error("background false positive");
            ++checks;
        }
        if (detector.detect(cv::Mat(640, 640, CV_8UC3, cv::Scalar(230, 230, 230))).found)
            throw std::runtime_error("blank false positive");
        std::cout << "Recovery checks passed: " << ++checks << std::endl;
        return 0;
    }
    catch (const std::exception &e) { std::cerr << e.what() << std::endl; return 1; }
}
