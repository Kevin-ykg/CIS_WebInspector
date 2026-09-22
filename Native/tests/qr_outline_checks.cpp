#include "qr/qr_detector.h"
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>

// 真实失焦/光照不均样本的开发回归；由开发者显式运行，不参与生产检测。
// 期望文本只用于核对解码输出，绝不作为参数传入识别器。
int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc != 3)
        {
            throw std::invalid_argument("QrReaderOutlineChecks <models> <22.jpg>");
        }
        std::ifstream file(std::filesystem::path(argv[2]), std::ios::binary);
        if (!file)
        {
            throw std::runtime_error("Cannot open input image");
        }
        std::vector<uchar> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        cv::Mat image = cv::imdecode(bytes, cv::IMREAD_COLOR);
        if (image.empty())
        {
            throw std::runtime_error("Cannot decode input image");
        }
        qr::QrDetector detector(std::filesystem::path(argv[1]).string());
        detector.initialize();
        auto baseline = detector.detect(image);
        if (!baseline.found || baseline.text != "ZJ_202607310321032_RT")
        {
            throw std::runtime_error("Tight blurred sample text mismatch");
        }
        int checks = 1;
        for (int format = 0; format < 4; ++format)
        {
            qr::Options options;
            options.invert_polarity = format == 3;
            detector.configure(options);
            cv::Mat input, parent;
            if (format == 0)
            {
                input = image;
            }
            else if (format == 1)
            {
                cv::cvtColor(image, input, cv::COLOR_BGR2BGRA);
            }
            else if (format == 2)
            {
                parent = cv::Mat(image.rows + 40, image.cols + 80, image.type(), cv::Scalar(23, 37, 51));
                input = parent(cv::Rect(31, 17, image.cols, image.rows));
                image.copyTo(input);
            }
            else
            {
                cv::bitwise_not(image, input);
            }
            auto original = input.clone();
            for (int repeat = 0; repeat < 3; ++repeat)
            {
                auto result = detector.detect(input);
                if (!result.found || result.text != baseline.text || std::abs(result.x - baseline.x) > 1e-6 ||
                    std::abs(result.y - baseline.y) > 1e-6 || std::abs(result.width - baseline.width) > 1e-6 ||
                    std::abs(result.height - baseline.height) > 1e-6 || cv::norm(input, original, cv::NORM_INF) != 0)
                {
                    throw std::runtime_error("Storage layout, repeated result or input ownership changed");
                }
                if (detector.attempts() > 6)
                {
                    throw std::runtime_error("Sample decode budget exceeded");
                }
                ++checks;
            }
        }
        detector.configure(qr::Options{});
        // 从同一张真实图截取单定位框、双定位框区域，防止外框恢复把不完整证据
        // 当作成功文本。它们只是负例输入，不作为生产算法的特定 ROI。
        for (const auto &roi : {cv::Rect(200, 0, 168, 180), cv::Rect(0, 225, 368, 136)})
        {
            if (detector.detect(image(roi)).found)
            {
                throw std::runtime_error("Incomplete locator false positive");
            }
            ++checks;
        }
        if (detector.detect(cv::Mat(400, 400, CV_8UC3, cv::Scalar(230, 230, 230))).found)
        {
            throw std::runtime_error("Blank false positive");
        }
        std::cout << "PASS " << ++checks << " outline recovery checks; sample=" << baseline.text << '\n';
        return 0;
    }
    catch (const std::exception &ex)
    {
        std::cerr << ex.what() << '\n';
        return 1;
    }
}
