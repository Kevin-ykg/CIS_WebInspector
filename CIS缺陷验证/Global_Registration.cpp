#include "opencv2/highgui.hpp"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <opencv2/core/utils/logger.hpp>
#include <opencv2/opencv.hpp>
#include <opencv2\highgui\highgui.hpp>
#include <opencv2\imgproc\imgproc.hpp>

using namespace cv;
using namespace std;

// setMouseCallback("test", onMouse, reinterpret_cast<void*>(&gray1));
// //关联图像显示窗口和onMouse函数
void calcircle(Mat gray, Rect roi_rect, Point &leftpoint, Point &rightpoint,
               int param1, int param2, int minRadius, int maxRadius);

int main() {

  cv::utils::logging::setLogLevel(utils::logging::LOG_LEVEL_SILENT);
  for (int i = 1; i < 2; i++) {
    string num = to_string(28);
    Mat image = imread("E:\\nanjing\\code\\save\\" + num + ".tiff",
                       IMREAD_UNCHANGED);                              // 原始图
    Mat imagecom = imread("E:\\nanjing\\code\\save\\" + num + ".jpg"); // 扫描图
    vector<Mat> channels;
    split(image, channels);
    Mat BW_org = channels[3]; // Alpha通道获取原图二值图像

    vector<Point2f> srcpoints, dstpoints; // 四角坐标点集
    // 原图四角点，已知固定可以直接输入
    Point leftuporg(2004, 588);
    Point rightuporg(5584, 588);
    Point leftdownorg(2004, 12634);
    Point rightdownorg(5584, 12634);
    srcpoints.push_back(leftuporg);
    srcpoints.push_back(rightuporg);
    srcpoints.push_back(leftdownorg);
    srcpoints.push_back(rightdownorg);

    // 扫描图四角点获取
    Mat gray, BW_com;
    cvtColor(imagecom, gray, COLOR_BGR2GRAY); // 扫描图灰度化
    resize(gray, gray, Size(), 1, 1.5,
           INTER_LINEAR); // 纵向放大1.5倍，因为扫描图圆不圆
    cv::threshold(gray, BW_com, 0, 255,
                  cv::THRESH_BINARY |
                      cv::THRESH_OTSU);          // 扫描图二值化,大津阈值法
    cv::Rect rectup2(0, 0, imagecom.cols, 1000); // 上方搜索圆区域
    Mat grayup = gray(rectup2);
    cv::blur(grayup, grayup, cv::Size(5, 5));
    Point leftupcom, rightupcom; // 识别扫描图左上，右上角
    calcircle(grayup, rectup2, leftupcom, rightupcom, 30, 30, 115, 125);

    cv::Rect rectdown2(2000, 12000, gray.cols - 2000,
                       gray.rows - 12000); // 下方搜索圆区域
    Mat graydown = gray(rectdown2);
    cv::blur(graydown, graydown, cv::Size(5, 5));
    Point leftdowncom, rightdowncom; // 识别扫描图左下，右下角
    calcircle(graydown, rectdown2, leftdowncom, rightdowncom, 40, 40, 110, 125);
    dstpoints.push_back(leftupcom);
    dstpoints.push_back(rightupcom);
    dstpoints.push_back(leftdowncom);
    dstpoints.push_back(rightdowncom);

    cv::Mat image1_to_image2 = cv::getPerspectiveTransform(
        dstpoints, srcpoints); // 计算扫描图到原图变换矩阵
    std::cout << "扫描图四角坐标" << endl;
    std::cout << dstpoints << endl;
    std::cout << "原图四角坐标" << endl;
    std::cout << srcpoints << endl;
    std::cout << "扫描图到原图变换矩阵" << endl;
    std::cout << image1_to_image2 << endl;
    Mat result;
    warpPerspective(BW_com, BW_com, image1_to_image2,
                    BW_org.size()); // 扫描图变换

    namedWindow("test", WINDOW_NORMAL);
    imshow("test", BW_com);
    setMouseCallback(
        "test", onMouse,
        reinterpret_cast<void *>(&BW_org)); // 关联图像显示窗口和onMouse函数
    waitKey(0);
    // 保存BW_org和BW_com，根据传入坐标切割，再进行下一步局部配准
  }

  return 0;
}

void calcircle(Mat gray, Rect roi_rect, Point &leftpoint, Point &rightpoint,
               int param1, int param2, int minRadius, int maxRadius) {
  int minDist = 100; // 最小距离

  vector<Vec3f> circles;
  HoughCircles(gray, circles, HOUGH_GRADIENT, 1, minDist, param1, param2,
               minRadius, maxRadius);
  // std::cout << circles.size() << endl;
  int xmin = gray.cols;
  int xmax = 0;
  for (size_t i = 0; i < circles.size(); i++) {
    Vec3i c = circles[i];
    if (c[0] < xmin) {
      xmin = c[0];
      leftpoint = Point(Point(c[0] + roi_rect.x, c[1] + roi_rect.y));
    }
    if (c[0] > xmax) {
      xmax = c[0];
      rightpoint = Point(Point(c[0] + roi_rect.x, c[1] + roi_rect.y));
      // std::cout << c[2] << endl;
    }
  }
}