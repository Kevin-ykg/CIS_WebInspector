#include "opencv2/highgui.hpp"
#include <algorithm>
#include <cmath>
#include <fstream>
#include <iostream>
#include <nlohmann/json.hpp>
#include <opencv2/core/utils/logger.hpp>
#include <opencv2/opencv.hpp>
#include <opencv2\highgui\highgui.hpp>
#include <opencv2\imgproc\imgproc.hpp>
#include <string>

using namespace cv;
using namespace std;
using json = nlohmann::json;

int main() {
  cv::utils::logging::setLogLevel(utils::logging::LOG_LEVEL_SILENT);
  double scale = 300 / 25.4;
  int x_cols = 5 * scale;  // 原点x坐标
  int y_rows = 65 * scale; // 原点y坐标
  std::string baseFilePath = "E:\\Software\\feishudocs\\test\\";

  ifstream file(baseFilePath + "log\\Debug.log");
  if (!file.is_open()) {
    std::cout << "wrong" << endl;
    return 0;
  }
  std::string keyword1 = "ZJ_202606240060006_RT";
  std::string keyword2 = "formattingFilename";
  std::string line;
  std::string lastMatchLine;
  int lastLineNumber = -1;
  int currentLine = 0;

  while (std::getline(file, line)) {
    currentLine++;

    if (line.find(keyword1) != std::string::npos &&
        line.find(keyword2) != std::string::npos) {
      lastMatchLine = line;
      lastLineNumber = currentLine;
    }
  }

  if (lastLineNumber != -1) {
    std::cout << "最后一次出现关键字的位置： " << lastLineNumber << std::endl;
  } else {
    std::cout << "未找到关键字： " << keyword1 << std::endl;
    return 0;
  }
  size_t pos = lastMatchLine.find('['); //"["以后的文字json格式化
  std::string jsonStr;
  if (pos != std::string::npos) {
    jsonStr = lastMatchLine.substr(pos);

  } else {
    std::cout << "未找到 JSON 数据 " << std::endl;
    return 0;
  }
  json j = json::parse(jsonStr);
  auto sourceFileLocation = j[0]["cuttingInput"]["sourceFileLocation"];
  std::string formattingFilenameStr =
      j[0]["cuttingInput"]["formattingFilename"].get<std::string>();

  // 提取纯文件名 (例如: 2026-06-24-15-02-33.tiff)
  std::string baseFilename = "";
  size_t lastSlashPos = formattingFilenameStr.find_last_of("\\/");
  if (lastSlashPos != std::string::npos) {
    baseFilename = formattingFilenameStr.substr(lastSlashPos + 1);
  } else {
    baseFilename = formattingFilenameStr;
  }
  std::cout << "提取到的原图文件名: " << baseFilename << std::endl;

  // 恢复为正常的可读中文路径。
  std::string fullImagePath = baseFilePath + "tiff原图\\" + baseFilename;
  Mat image;
  std::ifstream ifs(fullImagePath, std::ios::binary);
  if (!ifs.is_open()) {
    std::cout << "无法打开原图文件 (请检查路径或中文编码): " << fullImagePath
              << std::endl;
    return 0;
  }
  ifs.seekg(0, std::ios::end);
  size_t length = ifs.tellg();
  ifs.seekg(0, std::ios::beg);
  std::vector<char> buffer(length);
  ifs.read(buffer.data(), length);
  ifs.close();

  image = imdecode(buffer, IMREAD_UNCHANGED);
  if (image.empty()) {
    std::cout << "图像解码失败！ " << std::endl;
    return 0;
  }
  vector<Mat> channels;
  split(image, channels);
  Mat BW_org = channels[3]; // Alpha通道获取原图二值图像
  cvtColor(BW_org, BW_org, COLOR_GRAY2RGB);
  flip(BW_org, BW_org, 0);

  // 提取排版信息坐标
  std::cout << sourceFileLocation.dump(4) << std::endl;
  for (const auto &item : sourceFileLocation) {
    std::string hotInkTaskID = item["hotInkTaskID"];
    if (hotInkTaskID.find("QRCode") != std::string::npos) {
      continue;
    }
    double relativeCenterX = item["relativeCenterX"];
    double relativeCenterY = item["relativeCenterY"];
    double relativeTopLeftX = item["relativeTopLeftX"];
    double relativeTopLeftY = item["relativeTopLeftY"];
    double relativeBottomRightX = item["relativeBottomRightX"];
    double relativeBottomRightY = item["relativeBottomRightY"];
    cv::circle(BW_org,
               Point(relativeCenterX * scale, y_rows + relativeCenterY * scale),
               70, Scalar(0, 255, 0), -1);
    cv::Rect rect2(x_cols + relativeTopLeftX * scale,
                   y_rows + relativeTopLeftY * scale,
                   (relativeBottomRightX - relativeTopLeftX) * scale,
                   (relativeBottomRightY - relativeTopLeftY) * scale);
    cv::rectangle(BW_org, rect2, Scalar(0, 255, 0), 40);
  }
  file.close();

  namedWindow("test", WINDOW_NORMAL);
  imshow("test", BW_org);
  waitKey(0);
  imwrite("test.jpg", BW_org);
  return 0;
}
