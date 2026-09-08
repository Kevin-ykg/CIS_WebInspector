#include "alignment_internal.h"

namespace alignment
{
static int previous_valid(const std::vector<bool> &valid, int row)
{
    for (; row >= 0; --row)
        if (valid[row])
            return row;
    return -1;
}
static int next_valid(const std::vector<bool> &valid, int row)
{
    for (; row < static_cast<int>(valid.size()); ++row)
        if (valid[row])
            return row;
    return -1;
}
static double distance_mm(cv::Point2d a, cv::Point2d b, double ppmX, double ppmY)
{
    double dx = (a.x - b.x) / std::max(ppmX, 1e-6), dy = (a.y - b.y) / std::max(ppmY, 1e-6);
    return std::sqrt(dx * dx + dy * dy);
}
// 只评估实测内部点；临时隐藏当前层，使用同侧上下最近的有效层预测残差。
static std::vector<std::pair<int, double>> prediction_errors(const Result &grid, int col,
                                                             const std::vector<bool> &valid, double ppmX, double ppmY)
{
    std::vector<std::pair<int, double>> errors;
    for (int row = 1; row < static_cast<int>(valid.size()) - 1; ++row)
    {
        if (!valid[row])
            continue;
        int prev = previous_valid(valid, row - 1), next = next_valid(valid, row + 1);
        if (prev < 0 || next < 0)
            continue;
        double t = (grid.GridY[row] - grid.GridY[prev]) / std::max(grid.GridY[next] - grid.GridY[prev], 1e-6);
        errors.emplace_back(row,
                            distance_mm(grid.Residuals[row][col],
                                        lerp(grid.Residuals[prev][col], grid.Residuals[next][col], t), ppmX, ppmY));
    }
    return errors;
}
static int remove_outliers(Result &grid, int col, std::vector<bool> &valid, double ppmX, double ppmY)
{
    auto errors = prediction_errors(grid, col, valid, ppmX, ppmY);
    if (errors.size() < 5)
        return 0;
    std::vector<double> values;
    for (auto e : errors)
        values.push_back(e.second);
    double center = median(values);
    for (auto &v : values)
        v = std::abs(v - center);
    double threshold = std::max(2.0, center + 3 * std::max(median(values), .25));
    int count = 0;
    // 先计算完整误差集合，再统一剔除；不能边剔除边改邻居，会改变原来的 MAD 判定。
    for (auto e : errors)
        if (e.second > threshold)
        {
            valid[e.first] = false;
            grid.Residuals[e.first][col] = {0, 0};
            ++count;
        }
    return count;
}
static int internal_count(const std::vector<bool> &valid)
{
    int count = 0;
    for (size_t i = 1; i + 1 < valid.size(); ++i)
        if (valid[i])
            ++count;
    return count;
}
static bool consecutive_missing(const std::vector<bool> &valid)
{
    for (size_t i = 1; i + 2 < valid.size(); ++i)
        if (!valid[i] && !valid[i + 1])
            return true;
    return false;
}
static void fill_missing(Result &grid, int col, const std::vector<bool> &valid)
{
    for (int row = 1; row < static_cast<int>(valid.size()) - 1; ++row)
    {
        if (valid[row])
            continue;
        int prev = previous_valid(valid, row - 1), next = next_valid(valid, row + 1);
        if (prev < 0 || next < 0)
            continue;
        double t = (grid.GridY[row] - grid.GridY[prev]) / std::max(grid.GridY[next] - grid.GridY[prev], 1e-6);
        auto residual = lerp(grid.Residuals[prev][col], grid.Residuals[next][col], t);
        grid.Residuals[row][col] = residual;
        auto &record = grid.Controls[row * 3 + col];
        record.residual_x = residual.x;
        record.residual_y = residual.y;
        record.detected_cis_x = record.coarse_x + residual.x;
        record.detected_cis_y = record.coarse_y + residual.y;
        record.flags |= 2;
        // 不把插值层标成实测有效，后续留一统计仍只评价原始测量。
    }
}
static double signed_area(const std::array<cv::Point2d, 4> &p)
{
    double twice = 0;
    for (size_t i = 0; i < 4; ++i)
    {
        auto a = p[i], b = p[(i + 1) % 4];
        twice += a.x * b.y - b.x * a.y;
    }
    return twice * .5;
}
static bool abrupt(double a, double b)
{
    double lo = std::min(std::abs(a), std::abs(b)), hi = std::max(std::abs(a), std::abs(b));
    return lo <= 1e-9 || hi / lo > MaximumAdjacentJacobianScaleRatio;
}
static bool valid_topology(const Result &grid, std::string &error)
{
    int rows = static_cast<int>(grid.GridY.size());
    std::vector<std::array<cv::Point2d, 3>> source(rows);
    for (int row = 0; row < rows; ++row)
        for (int col = 0; col < 3; ++col)
        {
            source[row][col] = project(grid.Inverse, {grid.GridX[col], grid.GridY[row]}) + grid.Residuals[row][col];
            if (!finite(source[row][col]))
            {
                error = cv::format("控制点 (%d,%d) 包含非有限数值。", row, col);
                return false;
            }
        }
    for (int row = 0; row < rows; ++row)
        if (!(source[row][0].x < source[row][1].x && source[row][1].x < source[row][2].x))
        {
            error = cv::format("第 %d 层控制点左右顺序发生翻转。", row);
            return false;
        }
    for (int col = 0; col < 3; ++col)
        for (int row = 0; row + 1 < rows; ++row)
            if (source[row + 1][col].y <= source[row][col].y)
            {
                error = cv::format("第 %d 列第 %d/%d 层控制点 Y 顺序发生翻转。", col, row, row + 1);
                return false;
            }
    std::vector<std::array<double, 2>> scales(rows - 1);
    // 与原实现相同的网格单元有向面积/相邻尺度门控，不在迁移时更换为另一套 Jacobian 阈值。
    for (int row = 0; row + 1 < rows; ++row)
        for (int col = 0; col < 2; ++col)
        {
            double x = grid.GridX[col], xx = grid.GridX[col + 1], y = grid.GridY[row], yy = grid.GridY[row + 1];
            double target = signed_area({cv::Point2d{x, y}, {xx, y}, {xx, yy}, {x, yy}});
            double area =
                signed_area({source[row][col], source[row][col + 1], source[row + 1][col + 1], source[row + 1][col]});
            double ratio = std::abs(area) / std::max(std::abs(target), 1e-6);
            if (target * area <= 0 || ratio < .2 || ratio > 5)
            {
                error = cv::format("控制网格单元 (%d,%d) 翻折或尺度异常，ratio=%.3f。", row, col, ratio);
                return false;
            }
            scales[row][col] = ratio;
        }
    for (int row = 0; row + 1 < rows; ++row)
        for (int col = 0; col < 2; ++col)
        {
            if (row > 0 && abrupt(scales[row][col], scales[row - 1][col]))
            {
                error = cv::format("控制网格单元 (%d,%d) 与上一层 Jacobian 尺度变化过大。", row, col);
                return false;
            }
            if (col > 0 && abrupt(scales[row][col], scales[row][col - 1]))
            {
                error = cv::format("控制网格单元 (%d,%d) 与左侧 Jacobian 尺度变化过大。", row, col);
                return false;
            }
        }
    return true;
}

bool build_side_grid(const cv::Mat &cis, const cv::Mat &tiff, const Anchor &anchor, const Config &config,
                     Result &output, std::string &error)
{
    // 进入像素取整前检查非有限数，避免 C ABI 的损坏配置触发 double -> int 未定义行为。
    for (double value :
         {config.SideMarkDiameterMm, config.SheetWidthMm, config.TiffSideMarkEdgeOffsetMm, config.CisQrToLeftMarkMm,
          config.CisSideMarkSpanMm, config.SideMarkInitialSearchMarginMm, config.SideMarkExpandedSearchMarginMm})
    {
        if (!std::isfinite(value))
        {
            error = "侧边 Mark 参数包含 NaN 或无穷大。";
            return false;
        }
    }
    if (anchor.CenterX < 0 || !std::isfinite(anchor.CenterX) || anchor.PixelWidth <= 1 ||
        !std::isfinite(anchor.PixelWidth))
    {
        error = "第二个二维码的 X/宽度无效。";
        return false;
    }
    if (config.SideMarkPairCount < 1 || config.SideMarkMinValidPerColumn < 1 ||
        config.SideMarkMinValidPerColumn > config.SideMarkPairCount || config.SideMarkDiameterMm <= 0 ||
        config.SheetWidthMm <= 0 || config.TiffSideMarkEdgeOffsetMm <= 0 ||
        config.TiffSideMarkEdgeOffsetMm * 2 >= config.SheetWidthMm || config.CisQrToLeftMarkMm <= 0 ||
        config.CisSideMarkSpanMm <= 0 || config.QrPhysicalWidthMm <= 0 || config.SideMarkInitialSearchMarginMm < 0 ||
        config.SideMarkExpandedSearchMarginMm < config.SideMarkInitialSearchMarginMm ||
        config.NonlinearRemapStripeRows < 1)
    {
        error = "侧边 Mark 几何参数、有效点数量或 Remap 分块参数无效。";
        return false;
    }
    // 在临时对象中建立网格，通过全部门控后才交给 output；失败不会留下半成品控制点。
    Result grid;
    grid.Inverse = output.Inverse;
    int pairs = config.SideMarkPairCount, rows = pairs + 2;
    double ppm = config.LayoutDpi / 25.4, ppmX = anchor.PixelWidth / config.QrPhysicalWidthMm,
           ppmY = anchor.PixelHeight / config.QrPhysicalHeightMm;
    double top = config.TiffTopCenterYmm, bottom = config.TiffHeightMm - config.TiffBottomOffsetMm;
    if (bottom <= top)
    {
        error = "侧边控制网格的上下边界物理位置无效。";
        return false;
    }
    grid.GridX = {config.TiffSideMarkEdgeOffsetMm * ppm, config.SheetWidthMm * .5 * ppm,
                  (config.SheetWidthMm - config.TiffSideMarkEdgeOffsetMm) * ppm};
    grid.GridY.resize(rows);
    grid.Residuals.resize(rows);
    grid.Controls.resize(rows * 3);
    double step = (bottom - top) / (pairs + 1.0);
    for (int row = 0; row < rows; ++row)
        grid.GridY[row] = (top + row * step) * ppm;
    if (grid.GridX[0] < 0 || grid.GridX[2] >= tiff.cols || grid.GridY[0] < 0 || grid.GridY.back() >= tiff.rows)
    {
        error = "侧边控制网格预测位置超出 TIFF 图像范围。";
        return false;
    }
    double left = anchor.CenterX - config.CisQrToLeftMarkMm * ppmX, right = left + config.CisSideMarkSpanMm * ppmX;
    double corrections[] = {left - project(grid.Inverse, {grid.GridX[0], grid.GridY.back()}).x,
                            right - project(grid.Inverse, {grid.GridX[2], grid.GridY.back()}).x};
    std::array<std::vector<bool>, 2> valid = {std::vector<bool>(rows), std::vector<bool>(rows)};
    for (auto &v : valid)
        v[0] = v[rows - 1] = true;
    for (int row = 0; row < rows; ++row)
        for (int col = 0; col < 3; ++col)
        {
            auto &record = grid.Controls[row * 3 + col];
            record.row = row;
            record.column = col;
            record.flags = (row == 0 || row == rows - 1 || col == 1) ? 4 : 0;
            record.expected_x = grid.GridX[col];
            record.expected_y = grid.GridY[row];
            auto coarse = project(grid.Inverse, {record.expected_x, record.expected_y});
            record.coarse_x = record.detected_cis_x = coarse.x;
            record.coarse_y = record.detected_cis_y = coarse.y;
        }
    std::string details;
    for (int row = 1; row <= pairs; ++row)
    {
        double markY = top + row * step;
        double predictedY = anchor.GlobalCenterY - (bottom - markY) * ppmY - anchor.SegmentStartGlobalY;
        for (int side = 0; side < 2; ++side)
        {
            int col = side * 2;
            auto &record = grid.Controls[row * 3 + col];
            cv::Point2d expected{record.expected_x, record.expected_y}, coarse{record.coarse_x, record.coarse_y};
            cv::Point2d physical{coarse.x + corrections[side], predictedY};
            auto tiffDetection =
                detect_side(tiff, expected, config.SideMarkDiameterMm * ppm, config.SideMarkDiameterMm * ppm, ppm, ppm,
                            config.SideMarkInitialSearchMarginMm, config.SideMarkExpandedSearchMarginMm,
                            std::min(config.MinCircularityTiff, .75));
            auto cisDetection =
                detect_side(cis, physical, config.SideMarkDiameterMm * ppmX, config.SideMarkDiameterMm * ppmY, ppmX,
                            ppmY, config.SideMarkInitialSearchMarginMm, config.SideMarkExpandedSearchMarginMm,
                            std::min(config.MinCircularityCis, .60));
            // H0 提供列的倾斜趋势；二维码仅校正底部 X 偏移，不把 CIS 两列强制竖直。
            if (!cisDetection.Found && std::abs(coarse.y - predictedY) > 1)
            {
                auto fallback =
                    detect_side(cis, {physical.x, coarse.y}, config.SideMarkDiameterMm * ppmX,
                                config.SideMarkDiameterMm * ppmY, ppmX, ppmY, config.SideMarkInitialSearchMarginMm,
                                config.SideMarkExpandedSearchMarginMm, std::min(config.MinCircularityCis, .60));
                if (fallback.Found)
                {
                    fallback.UsedHomographyFallback = true;
                    cisDetection = fallback;
                }
            }
            valid[side][row] = tiffDetection.Found && cisDetection.Found;
            record.flags = valid[side][row] ? 1 : 0;
            if (tiffDetection.Found)
            {
                record.detected_tiff_x = tiffDetection.Point.X;
                record.detected_tiff_y = tiffDetection.Point.Y;
            }
            if (cisDetection.Found)
            {
                record.detected_cis_x = cisDetection.Point.X;
                record.detected_cis_y = cisDetection.Point.Y;
            }
            if (valid[side][row])
            {
                // 残差在 CIS 源空间定义：实测 CIS - H0^-1(TIFF 理论点)，不是减去实测 TIFF 点。
                grid.Residuals[row][col] = {cisDetection.Point.X - coarse.x, cisDetection.Point.Y - coarse.y};
                record.residual_x = grid.Residuals[row][col].x;
                record.residual_y = grid.Residuals[row][col].y;
            }
            details += cv::format(
                " | %s%d: Texp=%s, Tdet=%s, Cpred=%s, Cdet=%s, R=%s, TiffROI=[%s], CisROI=[%s], "
                "expanded=%s, h0Fallback=%s",
                side == 0 ? "L" : "R", row, point_text(expected).c_str(),
                tiffDetection.Found ? point_text({tiffDetection.Point.X, tiffDetection.Point.Y}).c_str() : "MISS",
                point_text(physical).c_str(),
                cisDetection.Found ? point_text({cisDetection.Point.X, cisDetection.Point.Y}).c_str() : "MISS",
                valid[side][row] ? point_text(grid.Residuals[row][col]).c_str() : "N/A",
                rect_text(tiffDetection.SearchRect).c_str(), rect_text(cisDetection.SearchRect).c_str(),
                tiffDetection.UsedExpandedWindow || cisDetection.UsedExpandedWindow ? "True" : "False",
                cisDetection.UsedHomographyFallback ? "True" : "False");
        }
    }
    int removed[2], counts[2];
    for (int side = 0; side < 2; ++side)
    {
        removed[side] = remove_outliers(grid, side * 2, valid[side], ppmX, ppmY);
        counts[side] = internal_count(valid[side]);
    }
    if (counts[0] < config.SideMarkMinValidPerColumn || counts[1] < config.SideMarkMinValidPerColumn)
    {
        error = cv::format("侧边有效 Mark 不足：Left=%d/%d, Right=%d/%d, 要求每侧至少 %d。", counts[0], pairs,
                           counts[1], pairs, config.SideMarkMinValidPerColumn) +
                details;
        return false;
    }
    if (consecutive_missing(valid[0]) || consecutive_missing(valid[1]))
    {
        error = "侧边 Mark 某一列连续两个内部层缺失。" + details;
        return false;
    }
    for (int side = 0; side < 2; ++side)
    {
        int col = side * 2;
        fill_missing(grid, col, valid[side]);
        for (int row = 1; row <= pairs; ++row)
        {
            auto &record = grid.Controls[row * 3 + col];
            record.flags = (record.flags & ~1) | (valid[side][row] ? 1 : 0);
            record.residual_x = grid.Residuals[row][col].x;
            record.residual_y = grid.Residuals[row][col].y;
        }
    }
    if (!valid_topology(grid, error))
    {
        error = "侧边控制网格质量无效：" + error;
        return false;
    }
    std::vector<double> errors;
    for (int side = 0; side < 2; ++side)
        for (auto e : prediction_errors(grid, side * 2, valid[side], ppmX, ppmY))
            errors.push_back(e.second);
    output.LooMedian = median(errors);
    output.LooMaximum = errors.empty() ? 0 : *std::max_element(errors.begin(), errors.end());
    // 留一指标仅记录，不新增门槛。本轮保持中心列与上下边界为零，不推测中心非线性形变。
    error = cv::format("SideMarks L=%d/%d, R=%d/%d, outliers L=%d, R=%d, LOO median=%.3fmm, max=%.3fmm", counts[0],
                       pairs, counts[1], pairs, removed[0], removed[1], output.LooMedian, output.LooMaximum) +
            details + " | Final: ";
    for (int row = 1; row <= pairs; ++row)
        for (int col : {0, 2})
        {
            auto &r = grid.Controls[row * 3 + col];
            error += cv::format(" | %s%d:%s, C=%s, R=%s", col == 0 ? "L" : "R", row,
                                r.flags & 2   ? "Interpolated"
                                : r.flags & 1 ? "Detected"
                                              : "Missing",
                                point_text({r.detected_cis_x, r.detected_cis_y}).c_str(),
                                point_text({r.residual_x, r.residual_y}).c_str());
        }
    output.GridX = grid.GridX;
    output.GridY = std::move(grid.GridY);
    output.Residuals = std::move(grid.Residuals);
    output.Controls = std::move(grid.Controls);
    return true;
}
} // namespace alignment
