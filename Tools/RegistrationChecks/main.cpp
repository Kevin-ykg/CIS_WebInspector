#include "patch_detector.h"
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <opencv2/imgcodecs.hpp>

namespace fs = std::filesystem;
int run_crop_checks(const fs::path &input, const fs::path &output);
cv::Mat read(const fs::path &path) {
  std::ifstream stream(path, std::ios::binary);
  std::vector<char> bytes((std::istreambuf_iterator<char>(stream)),
                          std::istreambuf_iterator<char>());
  // 与正式接口一致：先保留输入通道，再统一转灰度，避免 JPEG
  // 解码器的灰度近似影响比较。
  return cis::patch::gray(cv::imdecode(bytes, cv::IMREAD_UNCHANGED));
}
void write(const fs::path &path, const cv::Mat &image) {
  std::vector<unsigned char> bytes;
  cv::imencode(".png", image, bytes);
  std::ofstream stream(path, std::ios::binary);
  stream.write(reinterpret_cast<const char *>(bytes.data()), bytes.size());
}
cv::Mat homogeneous(const cv::Mat &transform) {
  if (transform.rows == 3) {
    return transform.clone();
  }
  cv::Mat full = cv::Mat::eye(3, 3, CV_64F);
  transform.copyTo(full(cv::Rect(0, 0, 3, 2)));
  return full;
}
cv::Point2d map_point(const cv::Mat &transform, cv::Point2d point) {
  const double denominator = transform.at<double>(2, 0) * point.x +
                             transform.at<double>(2, 1) * point.y + transform.at<double>(2, 2);
  return {(transform.at<double>(0, 0) * point.x + transform.at<double>(0, 1) * point.y +
           transform.at<double>(0, 2)) / denominator,
          (transform.at<double>(1, 0) * point.x + transform.at<double>(1, 1) * point.y +
           transform.at<double>(1, 2)) / denominator};
}

// 目标像素不变，仅让相邻图案的一小部分进入裁框四边。配准应保持同一
// 工作框及矩阵；同时保留完整散点，且不能把配准筛选应用到缺陷模板上。
int boundary_checks()
{
  using namespace cis::patch;
  cv::Mat alpha = cv::Mat::zeros(1800, 1600, CV_8U);
  const cv::Rect core(160, 160, 1280, 1480);
  cv::RNG random(20261027);
  for (int i = 0; i < 80; ++i)
  {
    const cv::Point center(random.uniform(300, 1300), random.uniform(300, 1500));
    cv::circle(alpha, center, random.uniform(8, 25), cv::Scalar(255), -1);
    cv::line(alpha, center, center + cv::Point(random.uniform(-30, 30), 40), cv::Scalar(255), 4);
  }
  cv::putText(alpha, "CIS 2026", {410, 850}, cv::FONT_HERSHEY_SIMPLEX, 2., cv::Scalar(255), 5);
  // 完整且远离主体的小散点也必须决定工作框，不能只保留最大连通域。
  cv::rectangle(alpha, {200, 220, 8, 8}, cv::Scalar(255), -1);
  cv::rectangle(alpha, {1390, 1550, 8, 8}, cv::Scalar(255), -1);
  const cv::Mat shift = (cv::Mat_<double>(2, 3) << 1, 0, 4, 0, 1, -3);
  cv::Mat cis;
  cv::warpAffine(alpha, cis, shift, alpha.size(), cv::INTER_LINEAR);
  auto cache = std::make_shared<TemplateCache>();
  Worker worker(cache);
  const auto baseline = register_patch(alpha, cis, 60, 100, 300., worker, core);
  if (!baseline.applied)
  {
    std::cerr << "Boundary fixture baseline rejected: " << baseline.diagnostic << "\n";
    return 1;
  }
  const auto frame = [](const Alignment &value) {
    const auto begin = value.diagnostic.find("frame=(");
    return value.diagnostic.substr(begin, value.diagnostic.find(", time=", begin) - begin);
  };
  const auto expected = cv::boundingRect(alpha);
  const auto expected_frame = "frame=(" + std::to_string(expected.x - 16) + "," +
      std::to_string(expected.y - 16) + "," + std::to_string(expected.width + 32) + "," +
      std::to_string(expected.height + 32) + ")";
  if (frame(baseline).find(expected_frame) != 0)
  {
    std::cerr << "Complete isolated motif was dropped\n";
    return 1;
  }
  const std::vector<cv::Rect> fragments{{650, 60, 70, 112}, {650, 1628, 70, 112},
                                      {60, 650, 112, 70}, {1428, 650, 112, 70}};
  int checked = 0;
  for (int side = 0; side <= 4; ++side)
  {
    auto contaminated = alpha.clone();
    for (int index = 0; index < 4; ++index)
    {
      if (side == 4 || index == side)
      {
        cv::rectangle(contaminated, fragments[index], cv::Scalar(255), -1);
      }
    }
    cv::Mat observed;
    cv::warpAffine(contaminated, observed, shift, alpha.size(), cv::INTER_LINEAR);
    const auto original = contaminated.clone();
    const auto result = register_patch(contaminated, observed, 60, 100, 300., worker, core);
    const double difference = result.applied
        ? cv::norm(homogeneous(result.transform), homogeneous(baseline.transform), cv::NORM_INF) : 1e9;
    if (!result.applied || result.info.status != baseline.info.status ||
        result.info.model != baseline.info.model || difference > 1e-8 || frame(result) != frame(baseline) ||
        cv::norm(original, contaminated, cv::NORM_INF) != 0)
    {
      std::cerr << "Boundary variant " << side << " changed registration: " << result.diagnostic << "\n";
      return 1;
    }
    // CIS 中故意不打印边界片段。即使配准忽略它，检测仍须报告原 Alpha
    // 中这部分的缺失，证明没有用删模板的方式掩盖缺陷。
    CisPatchConfig config{};
    config.struct_size = sizeof(config);
    config.enable_alignment = 1;
    config.alpha_threshold = 60;
    config.cis_threshold = 100;
    config.detection_scale = 1.;
    config.layout_dpi = 300.;
    config.core_x = core.x;
    config.core_y = core.y;
    config.core_width = core.width;
    config.core_height = core.height;
    auto detected = detect(contaminated, cis, config, worker);
    if (detected.summary.inner_count == 0)
    {
      std::cerr << "Registration filtering leaked into defect inspection\n";
      return 1;
    }
    ++checked;
  }
  // 主体本身穿过边界时，不允许将其当成少量外围碎片排除。
  auto clipped = alpha.clone();
  cv::rectangle(clipped, {120, 360, 500, 800}, cv::Scalar(255), -1);
  const auto clipped_result = register_patch(clipped, clipped, 60, 100, 300., worker, core);
  if (clipped_result.diagnostic.find("boundaryFragments=0/0px") == std::string::npos)
  {
    std::cerr << "Substantial boundary structure was silently dropped\n";
    return 1;
  }
  auto touching = alpha.clone();
  cv::rectangle(touching, {core.x, 340, 8, 8}, cv::Scalar(255), -1);
  const auto touching_result = register_patch(touching, touching, 60, 100, 300., worker, core);
  if (touching_result.diagnostic.find("boundaryFragments=0/0px") == std::string::npos ||
      frame(touching_result).find("frame=(144,") != 0)
  {
    std::cerr << "Complete dot touching core boundary was dropped\n";
    return 1;
  }
  std::cout << "BOUNDARY checks=" << checked + 3 << " passed; complete dots retained; inspection unchanged\n";
  return 0;
}

int wmain(int argc, wchar_t **argv) {
  if (argc == 4 && std::wstring(argv[1]) == L"--roi-sweep")
  {
    return run_crop_checks(argv[2], argv[3]);
  }
  if (argc == 4 && std::wstring(argv[3]) == L"--roi-sweep")
  {
    return run_crop_checks(argv[1], argv[2]);
  }
  if (argc == 2 && std::wstring(argv[1]) == L"--boundary-checks")
  {
    return boundary_checks();
  }
  if (argc < 3) {
    return 2;
  }
  fs::path input(argv[1]), output(argv[2]);
  fs::create_directories(output);
  auto cache = std::make_shared<cis::patch::TemplateCache>();
  cis::patch::Worker worker(cache);
  std::ofstream report(output / "alignment.tsv");
  report << "name\tapplied\tmodel\tms\ta\tb\ttx\tc\td\tty\th20\th21\th22\tdiagnostic\n"
         << std::setprecision(15);
  const bool invariance = argc > 3 && std::wstring(argv[3]) == L"--invariance";
  const bool synthetic_context = argc > 3 && std::wstring(argv[3]) == L"--synthetic-context";
  // 正式导出的 ROI 附带实际二值化阈值。读取它，不从零件编号长度猜阈值。
  cv::FileStorage thresholds;
  cv::FileStorage cores;
  if (fs::exists(input / "cores.json")) {
    std::ifstream stream(input / "cores.json", std::ios::binary);
    std::string json((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    cores.open(json, cv::FileStorage::READ | cv::FileStorage::MEMORY | cv::FileStorage::FORMAT_JSON);
  }
  if (fs::exists(input / "thresholds.json")) {
    std::ifstream stream(input / "thresholds.json", std::ios::binary);
    std::string json((std::istreambuf_iterator<char>(stream)),
                     std::istreambuf_iterator<char>());
    thresholds.open(json, cv::FileStorage::READ | cv::FileStorage::MEMORY |
                              cv::FileStorage::FORMAT_JSON);
  }
  int checked = 0, failures = 0;
  double maximum_error = 0;
  std::ofstream invariant_report(output / "invariance.tsv");
  invariant_report << "name\tdx\tdy\tstate_same\tmatrix_max_error_px\n"
                   << std::setprecision(15);
  std::vector<fs::path> files;
  for (const auto &entry : fs::directory_iterator(input)) {
    auto name = entry.path().filename().string();
    if (name.size() >= 10 && name.substr(name.size() - 10) == "_alpha.png") {
      files.push_back(entry.path());
    }
  }
  std::sort(files.begin(), files.end());
  for (const auto &file : files) {
    auto name = file.stem().string();
    name.resize(name.size() - 6);
    auto alpha = read(file), cis = read(input / (name + "_raw.png"));
    if (alpha.empty() || cis.empty()) {
      return 3;
    }
    const int ct = thresholds.isOpened() && !thresholds[name].empty()
                       ? static_cast<int>(thresholds[name])
                       : (name.size() == 32 ? 120 : 100);
    cv::Mat registration_alpha = alpha, registration_cis = cis;
    cv::Rect core(0, 0, alpha.cols, alpha.rows);
    if (cores.isOpened() && !cores[name].empty()) {
      const auto entry = cores[name];
      const cv::Rect declared(static_cast<int>(entry["X"]), static_cast<int>(entry["Y"]),
                              static_cast<int>(entry["Width"]), static_cast<int>(entry["Height"]));
      if (!declared.empty()) {
        core = declared;
      }
    }
    if (synthetic_context) {
      // 只供算法诊断：复制边缘常量不是相机实际拍到的真实搜索上下文，
      // 因此此模式不能用于生产精度/正确接受率验收。
      cv::copyMakeBorder(alpha, registration_alpha, 160, 160, 160, 160,
                         cv::BORDER_CONSTANT, cv::Scalar(0));
      cv::copyMakeBorder(cis, registration_cis, 160, 160, 160, 160,
                         cv::BORDER_CONSTANT, cv::Scalar(0));
      core = cv::Rect(160, 160, alpha.cols, alpha.rows);
    }
    auto result = cis::patch::register_patch(registration_alpha, registration_cis, 60, ct, 300., worker, core);
    cv::Mat transform = homogeneous(result.applied ? result.transform : cv::Mat::eye(2, 3, CV_64F));
    if (synthetic_context && result.applied) {
      cv::Mat shift = (cv::Mat_<double>(3, 3) << 1, 0, 160, 0, 1, 160, 0, 0, 1);
      transform = shift.inv() * transform * shift;
    }
    report << name << '\t' << result.applied << '\t' << result.info.model << '\t' << result.milliseconds;
    for (int y = 0; y < 3; ++y) {
      for (int x = 0; x < 3; ++x) {
        report << '\t' << transform.at<double>(y, x);
      }
    }
    report << '\t' << result.diagnostic << '\n';
    cv::Mat aligned, binary;
    cv::warpPerspective(cis, aligned, transform, cis.size(), cv::INTER_CUBIC);
    cv::threshold(aligned, binary, ct, 255, cv::THRESH_BINARY);
    write(output / (name + "_aligned.png"), binary);
    std::cout << name << " " << result.diagnostic << "\n";
    if (synthetic_context && result.info.model == CIS_PATCH_MODEL_HOMOGRAPHY) {
      // 同时走正式 detect 路径，验证 3x3 矩阵能对核心 ROI 一次透视采样，
      // 而不是仅在 register_patch 的候选阶段通过。
      CisPatchConfig config{};
      config.struct_size = sizeof(config);
      config.enable_alignment = 1;
      config.alpha_threshold = 60;
      config.cis_threshold = ct;
      config.detection_scale = .3;
      config.layout_dpi = 300.;
      config.core_x = core.x;
      config.core_y = core.y;
      config.core_width = core.width;
      config.core_height = core.height;
      auto detected = cis::patch::detect(registration_alpha, registration_cis, config, worker);
      if ((detected.alignment.status != CIS_PATCH_ALIGNMENT_APPLIED &&
           detected.alignment.status != CIS_PATCH_ALIGNMENT_APPLIED_DEGRADED) ||
          detected.alignment.model != CIS_PATCH_MODEL_HOMOGRAPHY) {
        ++failures;
        std::cerr << name << " production homography path failed: " << detected.log << "\n";
      }
    }
    if (invariance) {
      // 原始 ROI 没有真实搜索外围，不能与人为加边后的 ROI 比状态。
      // 基准和变体都给相同且足够大的 256px 像素上下文，再共同整数平移；
      // 这只证明坐标等变性，不替代带真实外围内容的准确率测试。
      cv::Mat base_a, base_c;
      cv::copyMakeBorder(alpha, base_a, 256, 256, 256, 256,
                         cv::BORDER_CONSTANT, cv::Scalar(0));
      cv::copyMakeBorder(cis, base_c, 256, 256, 256, 256,
                         cv::BORDER_CONSTANT, cv::Scalar(0));
      const auto baseline = cis::patch::register_patch(
          base_a, base_c, 60, ct, 300., worker,
          cv::Rect(256, 256, alpha.cols, alpha.rows));
      cv::Mat base_full = homogeneous(baseline.applied ? baseline.transform : cv::Mat::eye(2, 3, CV_64F));
      const cv::Mat base_shift =
          (cv::Mat_<double>(3, 3) << 1, 0, 256, 0, 1, 256, 0, 0, 1);
      const cv::Mat base_local = base_shift.inv() * base_full * base_shift;
      const std::vector<cv::Point> offsets{
          {0, 0},  {1, 0},  {-1, 0},    {0, 1},   {0, -1},  {8, -8}, {-8, 8},
          {24, 0}, {0, 24}, {-24, -24}, {48, 48}, {-48, 0}, {0, -48}};
      for (size_t index = 0; index < offsets.size(); ++index) {
        auto offset = offsets[index] + cv::Point(256, 256);
        cv::Mat translated_a(alpha.rows + 512 + static_cast<int>(index),
                             alpha.cols + 512, CV_8U, cv::Scalar(0));
        cv::Mat translated_c(translated_a.size(), CV_8U, cv::Scalar(0));
        alpha.copyTo(translated_a(cv::Rect(offset, alpha.size())));
        cis.copyTo(translated_c(cv::Rect(offset, cis.size())));
        auto shifted = cis::patch::register_patch(translated_a, translated_c,
                                                  60, ct, 300., worker,
                                                  cv::Rect(offset, alpha.size()));
        cv::Mat local = homogeneous(shifted.applied ? shifted.transform : cv::Mat::eye(2, 3, CV_64F));
        cv::Mat change =
            (cv::Mat_<double>(3, 3) << 1, 0, offset.x, 0, 1, offset.y, 0, 0, 1);
        cv::Mat restored = change.inv() * local * change;
        double error = 0;
        for (auto p :
             std::vector<cv::Point2d>{{0, 0},
                                      {alpha.cols - 1., 0},
                                      {0, alpha.rows - 1.},
                                      {alpha.cols - 1., alpha.rows - 1.},
                                      {alpha.cols / 2., alpha.rows / 2.}}) {
          error = std::max(error, cv::norm(map_point(restored, p) - map_point(base_local, p)));
        }
        const bool same = baseline.info.status == shifted.info.status &&
                          baseline.info.reason == shifted.info.reason &&
                          baseline.info.model == shifted.info.model;
        ++checked;
        if (!same || error > 1e-6) {
          ++failures;
        }
        maximum_error = std::max(error, maximum_error);
        invariant_report << name << '\t' << offsets[index].x << '\t'
                         << offsets[index].y << '\t' << same << '\t' << error
                         << '\n';
      }
    }
  }
  std::cout << "INVARIANCE checked=" << checked << " failures=" << failures
            << " max_px=" << maximum_error << "\n";
  return failures == 0 ? 0 : 1;
}
