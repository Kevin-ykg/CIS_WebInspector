// 真实整图上的裁框扰动回归：像素不改，只改变逻辑核心和真实搜索窗口。
#include "patch_detector.h"
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <opencv2/imgcodecs.hpp>

namespace
{
namespace fs = std::filesystem;
cv::Mat load_image(const fs::path &path)
{
    std::ifstream stream(path, std::ios::binary);
    std::vector<char> bytes((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    return cis::patch::gray(cv::imdecode(bytes, cv::IMREAD_UNCHANGED));
}
cv::Rect rectangle(const cv::FileNode &value)
{
    return {static_cast<int>(value["x"]), static_cast<int>(value["y"]),
            static_cast<int>(value["width"]), static_cast<int>(value["height"])};
}
cv::Mat full_matrix(const cv::Mat &value)
{
    cv::Mat result = cv::Mat::eye(3, 3, CV_64F);
    value.copyTo(result(cv::Rect(0, 0, 3, value.rows)));
    return result;
}
cv::Point2d project(const cv::Mat &matrix, cv::Point2d point)
{
    const double denominator = matrix.at<double>(2, 0) * point.x + matrix.at<double>(2, 1) * point.y +
                               matrix.at<double>(2, 2);
    return {(matrix.at<double>(0, 0) * point.x + matrix.at<double>(0, 1) * point.y +
             matrix.at<double>(0, 2)) / denominator,
            (matrix.at<double>(1, 0) * point.x + matrix.at<double>(1, 1) * point.y +
             matrix.at<double>(1, 2)) / denominator};
}
}

int run_crop_checks(const std::filesystem::path &input, const std::filesystem::path &output)
{
    using namespace cis::patch;
    auto alpha = load_image(input / "frame_alpha.png"), cis = load_image(input / "frame_raw.png");
    if (alpha.empty() || alpha.size() != cis.size())
    {
        throw std::runtime_error("Full frame Alpha/CIS missing or inconsistent");
    }
    std::ifstream stream(input / "sweep.json", std::ios::binary);
    const std::string json((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    cv::FileStorage config(json, cv::FileStorage::READ | cv::FileStorage::MEMORY | cv::FileStorage::FORMAT_JSON);
    const int padding = static_cast<int>(config["padding"]), at = static_cast<int>(config["alpha_threshold"]),
              ct = static_cast<int>(config["cis_threshold"]);
    const double dpi = static_cast<double>(config["dpi"]);
    std::filesystem::create_directories(output);
    std::ofstream report(output / "crop-sweep.tsv");
    report << "name\tdx\tdy\tapplied\tstatus\tmodel\tmatches\tinliers\tmatrix_delta_px\tsame\tms\tdiagnostic\n"
           << std::setprecision(15);
    auto cache = std::make_shared<TemplateCache>();
    Worker worker(cache);
    int checked = 0, failures = 0;
    double maximum = 0;
    for (const auto &entry : config["cases"])
    {
        const std::string name = static_cast<std::string>(entry["name"]);
        const auto nominal = rectangle(entry["core"]), target = rectangle(entry["target"]);
        std::vector<int> dx, dy;
        entry["dx"] >> dx;
        if (!entry["dy"].empty())
        {
            entry["dy"] >> dy;
        }
        else
        {
            for (int y = static_cast<int>(entry["dy_min"]); y <= static_cast<int>(entry["dy_max"]); ++y)
            {
                dy.push_back(y);
            }
        }
        const auto run = [&](cv::Rect core) {
            if ((target & core) != target)
            {
                throw std::runtime_error("Sweep would truncate labeled target: " + name);
            }
            const auto search = expand_rect(core, padding, alpha.size());
            auto result = register_patch(alpha(search), cis(search), at, ct, dpi, worker,
                {core.x - search.x, core.y - search.y, core.width, core.height});
            cv::Mat global;
            if (result.applied)
            {
                const cv::Mat origin = (cv::Mat_<double>(3, 3) << 1, 0, search.x, 0, 1, search.y, 0, 0, 1);
                global = origin * full_matrix(result.transform) * origin.inv();
            }
            return std::make_pair(std::move(result), global);
        };
        const auto baseline = run(nominal);
        if (!baseline.first.applied)
        {
            throw std::runtime_error("Baseline must apply a reliable local model: " + name + ": " +
                                     baseline.first.diagnostic);
        }
        for (int x : dx)
        {
            for (int y : dy)
            {
                const auto actual = run(nominal + cv::Point(x, y));
                double difference = std::numeric_limits<double>::infinity();
                if (actual.first.applied)
                {
                    difference = 0;
                    for (int gy = 0; gy < 3; ++gy)
                    {
                        for (int gx = 0; gx < 3; ++gx)
                        {
                            const cv::Point2d probe(target.x + (target.width - 1.) * gx / 2.,
                                                     target.y + (target.height - 1.) * gy / 2.);
                            difference = std::max(difference,
                                cv::norm(project(actual.second, probe) - project(baseline.second, probe)));
                        }
                    }
                }
                const bool same = actual.first.applied && actual.first.info.status == baseline.first.info.status &&
                    actual.first.info.model == baseline.first.info.model &&
                    actual.first.info.matches == baseline.first.info.matches &&
                    actual.first.info.inliers == baseline.first.info.inliers && difference < 1e-6;
                ++checked;
                failures += !same;
                maximum = std::max(maximum, difference);
                report << name << '\t' << x << '\t' << y << '\t' << actual.first.applied << '\t'
                       << actual.first.info.status << '\t' << actual.first.info.model << '\t'
                       << actual.first.info.matches << '\t' << actual.first.info.inliers << '\t'
                       << difference << '\t' << same << '\t' << actual.first.milliseconds << '\t'
                       << actual.first.diagnostic << '\n';
                if (checked % 25 == 0)
                {
                    std::cout << "SWEEP checked=" << checked << " failures=" << failures << "\n" << std::flush;
                }
            }
        }
    }
    std::cout << "CROP SWEEP checked=" << checked << " failures=" << failures << " max_px=" << maximum << "\n";
    return failures ? 1 : 0;
}
