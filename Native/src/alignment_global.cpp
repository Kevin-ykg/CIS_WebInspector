#include "alignment_internal.h"

namespace alignment
{
static bool validate(const cv::Mat &cis, const cv::Mat &tiff, const Anchor &a, const Config &c, std::string &error)
{
    if (cis.empty())
    {
        error = "CIS 图像为空。";
        return false;
    }
    if (tiff.empty())
    {
        error = "TIFF 图像为空。";
        return false;
    }
    const double numbers[] = {c.LayoutDpi,
                              c.TiffHeightMm,
                              c.TiffTopCenterYmm,
                              c.TiffBottomOffsetMm,
                              c.MarkDiameterMm,
                              c.CisRowSpacingMm,
                              c.QrPhysicalHeightMm,
                              c.QrPhysicalWidthMm,
                              c.InitialSearchMarginMm,
                              c.ExpandedSearchMarginMm,
                              c.MinCircularityTiff,
                              c.MinCircularityCis};
    for (double n : numbers)
        if (!std::isfinite(n))
        {
            error = "Mark 配准参数包含 NaN 或无穷大。";
            return false;
        }
    if (a.GlobalCenterY < 0 || a.SegmentStartGlobalY < 0)
    {
        error = "二维码全局 Y 或拼接段起始全局 Y 无效。";
        return false;
    }
    if (a.PixelHeight <= 1 || !std::isfinite(a.PixelHeight))
    {
        error = cv::format("第二个二维码像素高度无效：%.3f。", a.PixelHeight);
        return false;
    }
    if (a.PixelWidth <= 1 || !std::isfinite(a.PixelWidth))
    {
        error = cv::format("第二个二维码像素宽度无效：%.3f。", a.PixelWidth);
        return false;
    }
    if (c.LayoutDpi <= 0 || c.TiffHeightMm <= 0 || c.TiffTopCenterYmm < 0 || c.TiffBottomOffsetMm < 0 ||
        c.TiffBottomOffsetMm >= c.TiffHeightMm || c.MarkDiameterMm <= 0 || c.CisRowSpacingMm <= 0 ||
        c.QrPhysicalHeightMm <= 0 || c.QrPhysicalWidthMm <= 0 || c.InitialSearchMarginMm < 0 ||
        c.ExpandedSearchMarginMm < c.InitialSearchMarginMm || c.MinCircularityTiff <= 0 || c.MinCircularityTiff > 1 ||
        c.MinCircularityCis <= 0 || c.MinCircularityCis > 1 ||
        (c.EnableWhiteInkInspection &&
         (!std::isfinite(c.WhiteInkNormalGray) || !std::isfinite(c.WhiteInkStreakStdDevThreshold) ||
          c.WhiteInkNormalGray <= 0 || c.WhiteInkNormalGray > 255 || c.WhiteInkStreakStdDevThreshold <= 0)))
    {
        error = "Mark 配准物理参数或圆度阈值无效。";
        return false;
    }
    double y = static_cast<double>(a.GlobalCenterY - a.SegmentStartGlobalY);
    if (y < 0 || y >= cis.rows)
    {
        error = cv::format("第二个二维码全局 Y 转换后的图内坐标 %.1f 超出 CIS 高度 %d。", y, cis.rows);
        return false;
    }
    return true;
}
static bool finite_transform(const cv::Mat &h)
{
    if (h.rows != 3 || h.cols != 3 || h.type() != CV_64FC1)
        return false;
    for (int y = 0; y < 3; ++y)
        for (int x = 0; x < 3; ++x)
            if (!std::isfinite(h.at<double>(y, x)))
                return false;
    return true;
}
static cv::Mat robust_transform(const std::vector<cv::Point2f> &source, const std::vector<cv::Point2f> &target,
                                double threshold)
{
    // 点顺序和 Point2f 精度与原 C# 输入一致。保留 Homography 失败后的完整仿射后备，
    // 并升为 3×3，避免在语言迁移中擅自改成相似变换而改变现场对准效果。
    if (source.size() >= 6)
    {
        auto h = cv::findHomography(source, target, cv::RANSAC, threshold);
        if (!h.empty())
            return h;
    }
    cv::Mat inliers;
    auto affine = cv::estimateAffine2D(source, target, inliers, cv::RANSAC, threshold);
    if (affine.empty())
        return {};
    cv::Mat h = cv::Mat::eye(3, 3, CV_64FC1);
    affine.copyTo(h(cv::Rect(0, 0, 3, 2)));
    return h;
}
static bool project_checked(const cv::Mat &h, cv::Point2f p, cv::Point2d &output)
{
    double d = h.at<double>(2, 0) * p.x + h.at<double>(2, 1) * p.y + h.at<double>(2, 2);
    if (std::abs(d) <= 1e-9 || !std::isfinite(d))
        return false;
    output = project(h, cv::Point2d(p));
    return finite(output);
}
static bool quality(const cv::Mat &h, const std::vector<cv::Point2f> &source, const std::vector<cv::Point2f> &target,
                    cv::Size size, double ppm, double threshold, std::string &diagnostic)
{
    std::vector<double> inliers;
    for (size_t i = 0; i < source.size(); ++i)
    {
        cv::Point2d p;
        if (!project_checked(h, source[i], p))
        {
            diagnostic = cv::format("第 %d 个对应点投影结果无效。", static_cast<int>(i) + 1);
            return false;
        }
        double dx = p.x - target[i].x, dy = p.y - target[i].y, error = std::sqrt(dx * dx + dy * dy);
        if (error <= threshold)
            inliers.push_back(error);
    }
    double ratio = static_cast<double>(inliers.size()) / std::max(size_t(1), source.size());
    if (inliers.size() <
            static_cast<size_t>(std::max(4, static_cast<int>(std::ceil(source.size() * MinimumGlobalInlierRatio)))) ||
        ratio < MinimumGlobalInlierRatio)
    {
        diagnostic = cv::format("RANSAC 有效内点 %d/%d (%.1f%%)，低于最低要求 60%%。", static_cast<int>(inliers.size()),
                                static_cast<int>(source.size()), ratio * 100);
        return false;
    }
    double errorMm = median(inliers) / std::max(ppm, 1e-6);
    if (errorMm > MaximumMedianReprojectionErrorMm)
    {
        diagnostic = cv::format("内点重投影误差中位数 %.3fmm，超过允许值 0.500mm。", errorMm);
        return false;
    }
    // RANSAC 不保证全图方向。用源图两个坐标轴的叉积排除镜像/方向退化。
    cv::Point2d origin, x, y;
    if (!project_checked(h, {0, 0}, origin) ||
        !project_checked(h, {static_cast<float>(std::max(1, size.width - 1)), 0}, x) ||
        !project_checked(h, {0, static_cast<float>(std::max(1, size.height - 1))}, y))
    {
        diagnostic = "无法验证 Homography 的全图方向。";
        return false;
    }
    double cross = (x.x - origin.x) * (y.y - origin.y) - (x.y - origin.y) * (y.x - origin.x);
    if (cross <= 0 || !std::isfinite(cross))
    {
        diagnostic = "Homography 发生镜像、翻折或方向退化。";
        return false;
    }
    diagnostic = cv::format("HQuality=Passed(inliers=%d/%d, ratio=%.1f%%, median=%.3fmm)",
                            static_cast<int>(inliers.size()), static_cast<int>(source.size()), ratio * 100, errorMm);
    return true;
}

void compute(const cv::Mat &cis, const cv::Mat &tiff, const Anchor &anchor, const Config &config, Result &output)
{
    if (!validate(cis, tiff, anchor, config, output.Diagnostic))
        return;
    auto start = Clock::now();
    double ppm = config.LayoutDpi / 25.4, ppmY = anchor.PixelHeight / config.QrPhysicalHeightMm,
           ppmX = anchor.PixelWidth / config.QrPhysicalWidthMm;
    // 必须先用 int64 全局 Y 减去拼接段起点，才得到当前源图坐标；不可把帧内 Y 当段内 Y。
    double bottomY = static_cast<double>(anchor.GlobalCenterY - anchor.SegmentStartGlobalY),
           topY = bottomY - config.CisRowSpacingMm * ppmY;
    std::array<Region, 2> regions = {Region{"Top", config.TiffTopCenterYmm * ppm, topY, config.MarkDiameterMm * ppm,
                                            config.MarkDiameterMm * ppmY, ppm, ppmY},
                                     Region{"Bottom", (config.TiffHeightMm - config.TiffBottomOffsetMm) * ppm, bottomY,
                                            config.MarkDiameterMm * ppm, config.MarkDiameterMm * ppmY, ppm, ppmY}};
    for (auto &r : regions)
    {
        if (r.TiffCenterY < 0 || r.TiffCenterY >= tiff.rows)
        {
            output.Diagnostic =
                cv::format("TIFF %s 预测圆心 Y=%.1f 超出图像高度 %d。", r.Name.c_str(), r.TiffCenterY, tiff.rows);
            return;
        }
        if (r.CisCenterY < 0 || r.CisCenterY >= cis.rows)
        {
            output.Diagnostic =
                cv::format("CIS %s 预测圆心 Y=%.1f 超出图像高度 %d。", r.Name.c_str(), r.CisCenterY, cis.rows);
            return;
        }
    }
    cv::Mat cisGray = gray(cis);
    std::array<Row, 2> tiffRows, cisRows;
    double referenceArea = 0;
    std::vector<MarkerPoint> topPoints;
    for (int row = 0; row < 2; ++row)
    {
        tiffRows[row] = detect_tiff_row(tiff, regions[row], config);
        cisRows[row] = detect_cis_row(cisGray, regions[row], config, referenceArea);
        if (row == 0 && cisRows[row].Points.size() >= 3)
        {
            double area = median_of(cisRows[row].Points, [](auto &p) { return p.Area; });
            auto &points = cisRows[row].Points;
            points.erase(std::remove_if(points.begin(), points.end(), [&](auto &p) { return p.Area >= area * 2.5; }),
                         points.end());
            update_geometry(cisRows[row]);
        }
        if (row == 0 && !cisRows[row].Points.empty())
        {
            referenceArea = median_of(cisRows[row].Points, [](auto &p) { return p.Area; });
            output.Threshold = cisRows[row].Threshold;
            topPoints = cisRows[row].Points;
            sort_x(topPoints);
        }
        if (row == 1 && config.EnableWhiteInkInspection)
            output.Ink = inspect_white(cisGray, regions[row], cisRows[row], topPoints, config);
    }
    std::vector<cv::Point2f> source, target;
    std::vector<CisAlignmentMark> marks;
    std::array<Match, 2> matches;
    std::string rowsDiagnostic, failure;
    for (int row = 0; row < 2; ++row)
    {
        auto &m = matches[row];
        m = match_rows(tiffRows[row], cisRows[row], ppm / ppmX);
        if (row)
            rowsDiagnostic += " | ";
        rowsDiagnostic += cv::format(
            "%s: TIFF=%d, CIS=%d, Matched=%d, TIFF-ROI=%s, CIS-ROI=%s, SinglePass=True, Tilt=%.2fdeg, YDrift=%.1fpx, "
            "LineResidual=%.2fpx, MatchResidual=%.2fpx, Coverage=%.1f%%",
            regions[row].Name.c_str(), static_cast<int>(tiffRows[row].Points.size()),
            static_cast<int>(cisRows[row].Points.size()), static_cast<int>(m.TiffPoints.size()),
            rect_text(tiffRows[row].SearchRect).c_str(), rect_text(cisRows[row].SearchRect).c_str(),
            std::atan(cisRows[row].Slope) * 180 / CV_PI, cisRows[row].EndToEndYDrift, cisRows[row].MedianLineResidual,
            m.MedianResidual, m.Coverage * 100);
        if (m.TiffPoints.size() < MinimumPointsPerRow)
        {
            if (failure.empty())
                failure = regions[row].Name + " 排有效对应 Mark 少于 2 个。";
            continue;
        }
        for (size_t i = 0; i < m.TiffPoints.size(); ++i)
        {
            auto t = m.TiffPoints[i], c = m.CisPoints[i];
            target.emplace_back(static_cast<float>(t.X), static_cast<float>(t.Y));
            source.emplace_back(static_cast<float>(c.X), static_cast<float>(c.Y));
            marks.push_back({row, m.TemplateIndices[i], t.X, t.Y, c.X, c.Y});
        }
    }
    if (!failure.empty())
    {
        output.Diagnostic = failure + rowsDiagnostic + white_diagnostic(output.Ink);
        return;
    }
    if (source.size() < MinimumGlobalCorrespondenceCount)
    {
        output.Diagnostic =
            "上下排合计有效对应点少于 6 个，拒绝使用少量点生成不稳定的二维变换。" + white_diagnostic(output.Ink);
        return;
    }
    if (std::max(matches[0].Coverage, matches[1].Coverage) < MinimumStrongRowCoverage ||
        (matches[0].Coverage + matches[1].Coverage) * .5 < MinimumAverageRowCoverage)
    {
        output.Diagnostic = "上下排 Mark 的横向覆盖不足；至少一排需达到 50%，平均需达到 28%。" + rowsDiagnostic;
        return;
    }
    double scaleDifference = std::abs(matches[0].Scale - matches[1].Scale) / std::max(std::abs(matches[0].Scale), 1e-6);
    if (std::isfinite(scaleDifference) && scaleDifference > MaximumRowScaleDifference)
    {
        output.Diagnostic =
            cv::format("上下排同编号 Mark 的横向尺度差异 %.1f%% 超过允许值 15%%。", scaleDifference * 100) +
            rowsDiagnostic;
        return;
    }
    double threshold = std::max(3.0, GlobalRansacThresholdMm * ppm);
    cv::Mat h = robust_transform(source, target, threshold);
    if (!finite_transform(h))
    {
        output.Diagnostic = "RANSAC 未能计算出有效的 CIS→TIFF 变换矩阵。" + rowsDiagnostic;
        return;
    }
    std::string qualityText;
    if (!quality(h, source, target, cis.size(), ppm, threshold, qualityText))
    {
        output.Diagnostic = "全局 Homography 质量门控未通过：" + qualityText + " | " + rowsDiagnostic;
        return;
    }
    cv::Mat inverse = h.inv();
    if (!finite_transform(inverse))
    {
        output.Diagnostic = "无法计算有效的 TIFF→CIS 逆变换矩阵。";
        return;
    }
    output.H = h;
    output.Inverse = inverse;
    output.Marks = std::move(marks);
    output.StripeRows = std::max(1, config.NonlinearRemapStripeRows);
    std::string sideText = "Nonlinear=DisabledByConfig: 侧边 4 mm Mark 功能已关闭，仅使用上下两排 20 mm Mark 计算 H0。";
    if (config.EnableSideMarkNonlinearAlignment)
    {
        std::string detail;
        bool valid = false;
        try
        {
            valid = build_side_grid(cisGray, tiff, anchor, config, output, detail);
        }
        catch (const std::exception &e)
        {
            detail = std::string("侧边非线性网格构建异常：") + e.what();
        }
        if (valid)
        {
            output.Mode = 1;
            sideText = "Nonlinear=Enabled: " + detail;
        }
        else
        {
            output.Quality = 1;
            sideText = "Nonlinear=GlobalOnly: " + detail;
        }
    }
    output.DetectionMs = elapsed(start);
    output.Diagnostic = "QR(globalY=" + std::to_string(anchor.GlobalCenterY) +
                        ", segmentStart=" + std::to_string(anchor.SegmentStartGlobalY) +
                        cv::format(", localY=%.1f, height=%.1f, cisPxPerMmY=%.4f, cisPxPerMmX=%.4f) | ", bottomY,
                                   anchor.PixelHeight, ppmY, ppmX) +
                        rowsDiagnostic + " | " + qualityText + " | " + sideText + white_diagnostic(output.Ink);
}
} // namespace alignment
