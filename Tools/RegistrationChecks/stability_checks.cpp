// 检查正式内部稳定性门控；不向生产 C ABI 暴露测试入口。
#include "patch_alignment.cpp"
#include <iostream>

int main()
{
    using namespace cis::patch;
    int checks = 0, failures = 0;
    const auto check = [&](bool passed, const char *name) {
        ++checks;
        failures += !passed;
        std::cout << name << ": " << (passed ? "PASS" : "FAIL") << "\n";
    };
    const cv::Size size(757, 929);
    const cv::Rect region(0, 0, 2981, 3656);
    std::vector<cv::Point2f> source{{20, 10}, {665, 90}, {725, 50}, {663, 838}, {725, 892}};
    std::vector<cv::Point2f> destination{{18.2f, 12.1f}, {663.1f, 92.3f}, {723.3f, 52.1f},
                                        {661.2f, 840.2f}, {723.4f, 894.1f}};
    std::vector<int> inliers{0, 1, 2, 3, 4};
    const auto matrix = least_squares(source, destination, inliers, false);
    const double sx = double(size.width) / region.width, sy = double(size.height) / region.height;
    const double baseline = leave_out_change(matrix, source, destination, inliers, size, region, sx, sy, false);
    check(std::isfinite(baseline) && baseline < 6.25, "independent sparse support is stable");

    // 原本底部只有两个位置；增加同一位置的方向描述子不能改变分区留出
    // 的统计单位，也不能改变最小二乘的权重或最终稳定性结论。
    auto duplicates_source = source, duplicates_destination = destination;
    auto duplicates = inliers;
    for (int index : {0, 4, 4, 0})
    {
        duplicates.push_back(static_cast<int>(duplicates_source.size()));
        duplicates_source.push_back(source[index]);
        duplicates_destination.push_back(destination[index]);
    }
    const double repeated = leave_out_change(matrix, duplicates_source, duplicates_destination, duplicates,
                                             size, region, sx, sy, false);
    check(std::abs(repeated - baseline) < 1e-8, "duplicated directions do not change stability");
    std::reverse(duplicates.begin(), duplicates.end());
    const double reversed = leave_out_change(matrix, duplicates_source, duplicates_destination, duplicates,
                                             size, region, sx, sy, false);
    check(std::abs(reversed - baseline) < 1e-6, "inlier order does not change stability");
    std::vector<int> single_location(20, 0);
    check(!std::isfinite(leave_out_change(matrix, source, destination, single_location, size, region, sx, sy, false)),
          "one corner cannot inflate independent support");
    auto unstable_destination = destination;
    unstable_destination[4].x += 15;
    const auto unstable_matrix = least_squares(source, unstable_destination, inliers, false);
    check(leave_out_change(unstable_matrix, source, unstable_destination, inliers, size, region, sx, sy, false) > 6.25,
          "genuinely unstable support is still rejected");

    cv::Mat periodic = cv::Mat::zeros(700, 900, CV_8U);
    for (int y = 0; y < 6; ++y)
    {
        for (int x = 0; x < 12; ++x)
        {
            const cv::Point p(60 + x * 60, 60 + y * 90);
            cv::rectangle(periodic, {p.x, p.y, 27, 43}, cv::Scalar(255), -1);
            cv::rectangle(periodic, {p.x + 10, p.y, 17, 17}, cv::Scalar(0), -1);
        }
    }
    auto cache = std::make_shared<TemplateCache>();
    Worker worker(cache);
    std::string diagnostic;
    const cv::Mat wrong_period = (cv::Mat_<double>(2, 3) << 1, 0, 60, 0, 1, 0);
    check(!unambiguous_period(periodic, periodic, 60, 100, wrong_period, 1., worker, diagnostic, 32),
          "wrong-period candidate remains rejected");

    // 孔洞中的独立前景仍是合法图案。定位统计用真实像素数，不用轮廓
    // 外接框面积；局部副本的 floodFill 访问标记不能改变调用方 Alpha。
    cv::Mat nested = cv::Mat::zeros(800, 800, CV_8U);
    cv::rectangle(nested, {220, 220, 360, 360}, cv::Scalar(255), -1);
    cv::rectangle(nested, {260, 260, 280, 280}, cv::Scalar(0), -1);
    cv::rectangle(nested, {330, 330, 140, 140}, cv::Scalar(255), -1);
    cv::rectangle(nested, {370, 370, 60, 60}, cv::Scalar(0), -1);
    cv::rectangle(nested, {390, 390, 20, 20}, cv::Scalar(255), -1);
    const auto original_nested = nested.clone();
    const cv::Rect nested_core(140, 140, 520, 520);
    const auto nested_domain = registration_core(nested, nested_core, 60);
    check(nested_domain.roi == nested_core && std::string(nested_domain.basis) == "content-anchored",
          "nested foreground islands do not bias canonical center");
    check(cv::norm(nested, original_nested, cv::NORM_INF) == 0,
          "canonical-domain analysis never mutates input Alpha");

    // 独立已知变换，不以算法自己找到的内点充当准确性真值。
    cv::Mat pattern = cv::Mat::zeros(1000, 1000, CV_8U);
    const cv::Rect core(120, 120, 760, 760);
    cv::RNG random(20261027);
    for (int i = 0; i < 65; ++i)
    {
        const cv::Point center(random.uniform(200, 800), random.uniform(200, 800));
        cv::circle(pattern, center, random.uniform(6, 23), cv::Scalar(255), -1);
        cv::rectangle(pattern, {center.x, center.y, 28, 11}, cv::Scalar(255), -1);
    }
    cv::putText(pattern, "SIFT", {260, 470}, cv::FONT_HERSHEY_SIMPLEX, 2., cv::Scalar(255), 5);
    std::vector<cv::Mat> transforms;
    transforms.push_back((cv::Mat_<double>(2, 3) << 1, 0, 4, 0, 1, -6));
    auto rotated = cv::getRotationMatrix2D({500.f, 500.f}, 1.5, 1.005);
    rotated.at<double>(0, 2) += 4;
    rotated.at<double>(1, 2) -= 3;
    transforms.push_back(rotated);
    transforms.push_back((cv::Mat_<double>(2, 3) << 1.01, .003, -8, -.002, .99, 6));
    for (size_t index = 0; index < transforms.size(); ++index)
    {
        cv::Mat observed, truth;
        cv::warpAffine(pattern, observed, transforms[index], pattern.size(), cv::INTER_LINEAR);
        cv::invertAffineTransform(transforms[index], truth);
        const auto aligned = register_patch(pattern, observed, 60, 100, 300., worker, core);
        double maximum_error = std::numeric_limits<double>::infinity();
        if (aligned.applied)
        {
            maximum_error = 0;
            for (int y = 0; y < 3; ++y)
            {
                for (int x = 0; x < 3; ++x)
                {
                    const cv::Point2d point(200 + x * 300., 200 + y * 300.);
                    maximum_error = std::max(maximum_error,
                        cv::norm(mapped(aligned.transform, point) - mapped(truth, point)));
                }
            }
        }
        std::cout << "known transform " << index << " max_error_px=" << maximum_error << " "
                  << aligned.diagnostic << "\n";
        check(aligned.applied && maximum_error < 3., "known transform recovery within 3 original pixels");
    }
    // 轻微剪切范围与单应候选一致，但不放开非等比、镜像和搜索越界保护。
    const cv::Rect geometry_core(200, 200, 600, 600);
    const cv::Size geometry_source(1000, 1000);
    const double shear = std::tan(1.44 * CV_PI / 180.);
    cv::Mat mild_shear = (cv::Mat_<double>(2, 3) << 1, shear, -500 * shear, 0, 1, 0);
    check(geometry_ok(mild_shear, {0, 0, 1000, 1000}, geometry_core, geometry_source, 1, 1, true),
          "mild 1.44 degree shear retains constrained affine option");
    cv::Mat excessive_shear = (cv::Mat_<double>(2, 3) << 1, .06, -30, 0, 1, 0);
    check(!geometry_ok(excessive_shear, {0, 0, 1000, 1000}, geometry_core, geometry_source, 1, 1, true),
          "excessive shear remains rejected");
    cv::Mat stretch = (cv::Mat_<double>(2, 3) << 1.04, 0, -20, 0, 1, 0);
    check(!geometry_ok(stretch, {0, 0, 1000, 1000}, geometry_core, geometry_source, 1, 1, true),
          "excessive scale remains rejected");
    cv::Mat mirror = (cv::Mat_<double>(2, 3) << -1, 0, 1000, 0, 1, 0);
    check(!geometry_ok(mirror, {0, 0, 1000, 1000}, geometry_core, geometry_source, 1, 1, true),
          "mirror remains rejected");
    std::cout << "STABILITY checks=" << checks << " failures=" << failures << "\n";
    return failures ? 1 : 0;
}
