#include "qr/qr_detector.h"
#include "qr_reader_api.h"
#include "cis_qr_adapter.h"
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <cstring>
#include <cmath>

namespace
{
int checks = 0;
void require(bool okay, const char *what)
{
    ++checks;
    if (!okay) throw std::runtime_error(what);
}
void same(const qr::Result &a, const qr::Result &b)
{
    require(a.found == b.found && a.text == b.text && a.strategy == b.strategy, "source/view result differs");
    require(std::abs(a.x - b.x) < 1e-8 && std::abs(a.y - b.y) < 1e-8 &&
            std::abs(a.width - b.width) < 1e-8 && std::abs(a.height - b.height) < 1e-8, "geometry differs");
}
struct Handle
{
    void *value = nullptr;
    ~Handle() { qr_reader_destroy(value); }
};
QrReaderImage image_view(const cv::Mat &image)
{
    return {sizeof(QrReaderImage), image.cols, image.rows, image.channels(), image.data,
        (image.rows - 1) * static_cast<uint64_t>(image.step) + image.cols * image.elemSize(), image.step};
}
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc != 3) throw std::invalid_argument("QrReaderChecks <models> <real-image>");
        std::ifstream file(std::filesystem::path(argv[2]), std::ios::binary);
        std::vector<uchar> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        cv::Mat image = cv::imdecode(bytes, cv::IMREAD_COLOR);
        require(!image.empty(), "input image missing");
        const auto original = image.clone();
        qr::QrDetector detector(std::filesystem::path(argv[1]).string());
        auto baseline = detector.detect(image); // 也验证未显式预热时自动初始化。
        require(baseline.found, "real image not decoded");
        // 同样像素放进有二维偏移的非连续父图 ROI，结果仍相对传入图而非父图。
        cv::Mat parent(image.rows + 47, image.cols + 83, image.type(), cv::Scalar(19, 31, 51));
        cv::Mat roi = parent(cv::Rect(31, 23, image.cols, image.rows));
        image.copyTo(roi);
        require(!roi.isContinuous(), "test ROI must have row padding");
        auto parent_copy = parent.clone();
        same(baseline, detector.detect(roi));
        qr::DetectHints full{{0, 0, image.cols, image.rows}};
        same(baseline, detector.detect(image, full));
        // 保留背景统计，只去掉窄边；取证区过紧会改变 Otsu 阈值，并不保证比整图更易解码。
        qr::DetectHints local{{1, 1, image.cols - 2, image.rows - 2}};
        auto localized = detector.detect(image, local);
        require(localized.found && localized.text == baseline.text, "two-dimensional evidence region failed");
        require(localized.x > 160 && localized.x < 500 && localized.y > 170 && localized.y < 510,
            "evidence coordinates not restored");
        bool rejected = false;
        try { detector.detect(image, qr::DetectHints{{-1, 0, 10, 10}}); }
        catch (const std::invalid_argument &) { rejected = true; }
        require(rejected && detector.attempts() == 0, "invalid evidence region not rejected before DNN");
        rejected = false;
        try { detector.detect(cv::Mat()); } catch (const std::invalid_argument &) { rejected = true; }
        require(rejected, "empty C++ input not rejected");
        cv::Mat wrong_depth(20, 20, CV_16UC1);
        rejected = false;
        try { detector.detect(wrong_depth); } catch (const std::invalid_argument &) { rejected = true; }
        require(rejected, "16-bit input not rejected");

        // midpoint-to-even 不是“先 round 再加偏移”；也不能在局部坐标提前夹零。
        qr::Result half; half.found = true; half.x = .5; half.y = 1.5;
        auto mapped = cis::to_frame_result(half, {1, 1});
        require(mapped.x == 2 && mapped.y == 2, "CIS half-pixel round order changed");
        half.x = -2.5; half.y = -3.5; half.clamp_center_to_zero = true;
        mapped = cis::to_frame_result(half, {5, 5});
        require(mapped.x == 2 && mapped.y == 2, "CIS clamp applied before parent offset");
        require(cis::to_frame_result({}, {5, 5}).found == false, "not-found became found");

        char error[4096]{}, text[16384]{}, strategy[4096]{};
        Handle handle;
        require(qr_reader_abi_version() == 1, "ABI version");
        require(qr_reader_create(argv[1], &handle.value, error, sizeof(error)) == 0, "C create");
        require(qr_reader_initialize(handle.value, error, sizeof(error)) == 0, "C initialize");
        auto source = image_view(roi);
        QrReaderResult result{};
        auto detect = [&](uint32_t capacity = 16384, const QrReaderHints *hints = nullptr) {
            result = {}; result.struct_size = sizeof(result);
            return qr_reader_detect(handle.value, &source, hints, &result, text, capacity, strategy,
                sizeof(strategy), error, sizeof(error));
        };
        for (int i = 0; i < 5; ++i)
        {
            require(detect() == 0, "C detect");
            require(std::string(text, result.text_bytes - 1) == baseline.text &&
                std::string(strategy) == baseline.strategy, "C/C++ text/strategy mismatch");
            require(std::abs(result.center_x - baseline.x) < 1e-8 && std::abs(result.center_y - baseline.y) < 1e-8,
                "C/C++ center mismatch");
        }
        require(detect(2) == -3 && !result.found && result.text_bytes == baseline.text.size() + 1 && text[0] == 0,
            "short output silently truncated");
        QrReaderHints invalid{sizeof(QrReaderHints), 0, 0, image.cols + 1, image.rows, 0};
        require(detect(sizeof(text), &invalid) == -1, "out-of-range hint accepted");
        invalid = {sizeof(QrReaderHints), 1, 1, image.cols - 2, image.rows - 2, 0};
        require(detect(sizeof(text), &invalid) == 0 && std::string(text) == baseline.text, "C hints failed");
        auto good = source;
        source.bytes = 1; require(detect() == -1, "short image accepted"); source = good;
        source.stride = 1; require(detect() == -1, "bad stride accepted"); source = good;
        source.stride = UINT64_MAX; require(detect() == -1, "stride overflow accepted"); source = good;
        source.channels = 2; require(detect() == -1, "two-channel input accepted"); source = good;
        source.pixels = nullptr; require(detect() == -1, "null pixels accepted"); source = good;
        source.struct_size = 0; require(detect() == -1, "wrong image layout accepted"); source = good;
        cv::Mat gray, bgra;
        cv::cvtColor(image, gray, cv::COLOR_BGR2GRAY); cv::cvtColor(image, bgra, cv::COLOR_BGR2BGRA);
        source = image_view(gray); require(detect() == 0 && std::string(text) == baseline.text, "Gray8 failed");
        source = image_view(bgra); require(detect() == 0 && std::string(text) == baseline.text, "BGRA32 failed");
        cv::Mat white; cv::bitwise_not(image, white);
        float scale = 1.f;
        QrReaderConfig config{sizeof(QrReaderConfig), 1, &scale, 1, 0};
        require(qr_reader_configure(handle.value, &config, error, sizeof(error)) == 0, "configure inverse");
        scale = 0; // 检测不得继续借用这个配置指针。
        source = image_view(white);
        require(detect() == 0 && std::string(text) == baseline.text, "white foreground or config snapshot failed");
        config.struct_size = 0;
        require(qr_reader_configure(handle.value, &config, error, sizeof(error)) == -1, "bad config accepted");
        cv::Mat blank(160, 160, CV_8UC1, cv::Scalar(255)); source = image_view(blank);
        require(detect() == 1 && !result.found && !text[0], "blank input not reported as not-found");
        require(cv::norm(parent, parent_copy, cv::NORM_INF) == 0 && cv::norm(image, original, cv::NORM_INF) == 0,
            "input pixels modified");
        qr_reader_destroy(nullptr);
        std::cout << "PASS " << checks << " reusable QR checks; sample=" << baseline.text << '\n';
        return 0;
    }
    catch (const std::exception &ex) { std::cerr << "FAIL after " << checks << ": " << ex.what() << '\n'; return 1; }
}
