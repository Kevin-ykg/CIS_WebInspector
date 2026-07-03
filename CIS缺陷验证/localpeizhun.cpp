#include "opencv2/highgui.hpp"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <opencv2/core/utils/logger.hpp>
#include <opencv2/opencv.hpp>
#include <opencv2/shape/shape_transformer.hpp>
#include <opencv2\highgui\highgui.hpp>
#include <opencv2\imgproc\imgproc.hpp>

using namespace cv;
using namespace std;
vector<Point> srcpoints; // 四角坐标点集
int calmatrix(Mat dbImg, Mat testImg, std::vector<cv::Point2f> &dbImgPointsOk,
              std::vector<cv::Point2f> &testImgPointsOk);
int cal_paint(Mat dif, int arealeve, Mat &rgb_image);

int main() {

  cv::utils::logging::setLogLevel(utils::logging::LOG_LEVEL_SILENT);
  int fontFace = cv::FONT_HERSHEY_SIMPLEX; // 字体
  double fontScale = 3;                    // 字体缩放
  cv::Scalar color_green(0, 255, 0);       // 颜色 (绿色)
  cv::Scalar color_red(0, 0, 255);         // 颜色 (绿色)
  int thickness = 3;                       // 字体线条粗细

  // 以下需要开放，调试
  double scale = 0.125;   // 缩放尺寸
  int linethickness = 7;  // 轮廓宽度
  int arealevelinner = 3; // 轮廓内部刮花面积阈值
  int arealevelouter = 5; // 轮廓外部刮花面积阈值
  int dilate_size = 25;   // 膨胀系数
  vector<Point> src;
  for (int i = 1; i < 52; i++) {
    std::cout << i << endl;
    for (int j = 0; j < 6; j++) {
      std::cout << j << endl;
      string num = to_string(i);
      Mat imageorg = imread("F:\\nanjing\\code\\cutresult\\" + num + "-" +
                                to_string(j) + "-1.jpg",
                            IMREAD_GRAYSCALE); // 原图      //原始图
      Mat imagecom = imread("F:\\nanjing\\code\\cutresult\\" + num + "-" +
                                to_string(j) + "-2.jpg",
                            IMREAD_GRAYSCALE); // 扫描图 //扫描图
      if (imageorg.empty() || imagecom.empty()) {
        std::cout << "empty" << endl;
        continue;
      }
      resize(imageorg, imageorg, Size(), scale, scale,
             INTER_NEAREST); // 缩放图像
      resize(imagecom, imagecom, Size(), scale, scale, INTER_NEAREST);

      // 原图减扫描图得到轮廓内部刮花dif_inner，扫描图减原图得到轮廓外部刮花dif_outer，分别处理
      Mat dif_org, dif_covert, dif_inner, dif_outer;
      Mat dbImg, testImg;
      cv::blur(imageorg, dbImg, cv::Size(7, 7)); // 均值滤波,更好找到sift特征点
      cv::blur(imagecom, testImg, cv::Size(7, 7));

      // 根据特征点计算仿射变换矩阵
      Mat T;
      std::vector<cv::Point2f> targetPoints, sourcePoints;
      int test = calmatrix(dbImg, testImg, sourcePoints, targetPoints);
      if (test == 0) {
        cout << "sift is wrong" << endl;
        dif_covert = imageorg - imagecom; // 未较准的差分图

      } else {
        Mat imageconvert;
        T = cv::estimateAffine2D(targetPoints, sourcePoints);
        cv::warpAffine(imagecom, imageconvert, T, imageorg.size(),
                       cv::INTER_CUBIC);
        double scale_x = T.at<double>(0, 0); // 对角x
        double scale_y = T.at<double>(1, 1); // 对角y
        double delta_x = T.at<double>(0, 2); // 偏置x
        double delta_y = T.at<double>(1, 2); // 偏置y
        // 对T放射矩阵做判断，异常使用未校准的差分图
        if ((scale_x < 1.1) && (scale_x > 0.9) && (scale_y < 1.1) &&
            (delta_x > -10) && (delta_x < 10) && (delta_y > -10) &&
            (delta_y < 10)) {
          dif_covert = imageorg - imageconvert; // 校准后的差分图
        } else {
          cout << "matrixT is wrong" << endl;
          dif_covert = imageorg - imagecom; // 未较准的差分图
        }
      }

      // 二值化
      // cv::threshold(dif_org, dif_org, 20, 255, THRESH_BINARY);
      cv::threshold(dif_covert, dif_covert, 20, 255, THRESH_BINARY);
      cv::threshold(imageorg, imageorg, 20, 255, THRESH_BINARY);
      cv::threshold(imagecom, imagecom, 20, 255, THRESH_BINARY);

      // 计算原图轮廓,并加粗
      vector<vector<Point>> contours; // 轮廓
      vector<Vec4i> hierachy; // 存放轮廓结构变量RETR_EXTERNAL,RETR_TREE
      findContours(imageorg, contours, hierachy, RETR_TREE,
                   CHAIN_APPROX_SIMPLE);
      Mat contourimg = Mat::zeros(imageorg.size(), imageorg.type());
      drawContours(contourimg, contours, -1, Scalar(255), linethickness, 8);

      // cv::bitwise_and(dif_org, dif_covert, dif_inner); //两个差分图取交集
      dif_inner = dif_covert -
                  contourimg; // 校准后的差分图减去加粗轮廓,减少错位轮廓误差

      // cv::imwrite("F:\\nanjing\\code\\difcut\\" + num + "-" + to_string(j) +
      // "-1.jpg", dif_org); //保存图像
      // cv::imwrite("F:\\nanjing\\code\\difcut\\" + num + "-" + to_string(j) +
      // "-2.jpg", imageconvert); //保存图像
      // cv::imwrite("F:\\nanjing\\code\\difcut\\" + num + "-" + to_string(j) +
      // "-5.jpg", contourimg); //保存图像
      // cv::imwrite("F:\\nanjing\\code\\difcut\\" + num + "-" + to_string(j) +
      // "-4.jpg", dif_inner); //保存图像
      // cv::imwrite("F:\\nanjing\\code\\difcut2\\" + num + "-" + to_string(j) +
      // "-5.jpg", result); //保存图像

      Mat element =
          getStructuringElement(MORPH_RECT, Size(dilate_size, dilate_size));
      Mat out;
      // 进行膨胀操作
      dilate(imageorg, contourimg, element);
      dif_outer = imagecom - contourimg; // 扫描图减去膨胀原始图得到轮廓外刮花

      Mat rgbresult, orgresult, inner_rgb, outer_rgb;
      cvtColor(imageorg, orgresult, COLOR_GRAY2RGB);
      cvtColor(imagecom, rgbresult, COLOR_GRAY2RGB);
      cvtColor(dif_inner, inner_rgb, COLOR_GRAY2RGB);
      cvtColor(dif_outer, outer_rgb, COLOR_GRAY2RGB);

      // 计算最大连通域面积,绘制图像
      int maxareainner = cal_paint(dif_inner, arealevelinner, rgbresult);
      int maxareaouter = cal_paint(dif_outer, arealevelouter, rgbresult);

      cv::Rect rect(0, 0, rgbresult.cols, rgbresult.rows);
      ;
      cv::rectangle(rgbresult, rect, Scalar(0, 255, 0), 1);
      cv::putText(orgresult, "Org_image",
                  cv::Point(orgresult.cols / 3, orgresult.rows / 9), fontFace,
                  fontScale, color_green, thickness);
      if ((maxareaouter <= arealevelouter) &&
          (maxareainner <= arealevelinner)) {
        cv::putText(rgbresult, "Pass",
                    cv::Point(rgbresult.cols / 3, rgbresult.rows / 9), fontFace,
                    fontScale, color_green, thickness);
      } else {
        cv::putText(rgbresult, "Wrong",
                    cv::Point(rgbresult.cols / 3, rgbresult.rows / 9), fontFace,
                    fontScale, color_red, thickness);
      }
      cv::putText(inner_rgb, "Dif_image",
                  cv::Point(inner_rgb.cols / 3, inner_rgb.rows / 9), fontFace,
                  fontScale, color_green, thickness);

      cv::hconcat(orgresult, rgbresult, rgbresult);
      cv::hconcat(rgbresult, inner_rgb + outer_rgb, rgbresult);
      // std::cout << "max_area=" << max_area << endl;
      cv::imwrite("F:\\nanjing\\code\\finalresult\\" + num + "-" +
                      to_string(j) + "-2.jpg",
                  rgbresult); // 保存图像
      // return 0;
      // namedWindow("test", WINDOW_NORMAL);
      // imshow("test", rgbresult);
      ////setMouseCallback("test", onMouse, reinterpret_cast<void*>(&imageorg));
      /////关联图像显示窗口和onMouse函数
      // waitKey(0);
      // return 0;
    }
  }
}

int cal_paint(Mat dif, int arealevel, Mat &rgb_image) {
  int fontFace = cv::FONT_HERSHEY_SIMPLEX; // 字体
  Mat labels, stats, centroids;
  int nccomps = connectedComponentsWithStats(dif, labels, stats, centroids);
  int max_area = 0, max_idx = 0;
  int x0, y0, w, h;
  double center_x, center_y;
  for (int i = 1; i < stats.rows; i++) {
    int area = stats.at<int>(i, cv::CC_STAT_AREA);
    if (area > max_area) {
      max_area = stats.at<int>(i, cv::CC_STAT_AREA);
      max_idx = i;
    }
    x0 = stats.at<int>(i, cv::CC_STAT_LEFT);
    y0 = stats.at<int>(i, cv::CC_STAT_TOP);
    h = stats.at<int>(i, cv::CC_STAT_HEIGHT);
    w = stats.at<int>(i, cv::CC_STAT_WIDTH);
    cv::Rect rect(x0, y0, w, h);
    center_x = centroids.at<double>(i, 0); // 第i个连通域的质心x坐标（col）
    center_y = centroids.at<double>(i, 1);

    if (area > arealevel) {
      cv::rectangle(rgb_image, rect, Scalar(0, 255, 0), 1);
      cv::putText(
          rgb_image, to_string(area),
          cv::Point(centroids.at<double>(i, 0), centroids.at<double>(i, 1)),
          fontFace, 1, Scalar(0, 255, 0), 2);
    }
  }
  return max_area;
}

int calmatrix(Mat dbImg, Mat testImg, std::vector<cv::Point2f> &dbImgPointsOk,
              std::vector<cv::Point2f> &testImgPointsOk) {
  cv::Ptr<cv::SIFT> detector = cv::SIFT::create(250);
  std::vector<cv::KeyPoint> keypoints1, keypoints2;
  cv::Mat descriptor1, descriptor2;

  detector->detectAndCompute(dbImg, cv::Mat(), keypoints1, descriptor1);
  detector->detectAndCompute(testImg, cv::Mat(), keypoints2, descriptor2);
  if (keypoints1.empty() || keypoints2.empty()) {
    std::cout << "sift key points empty" << endl;
    return 0;
  }

  cv::Ptr<cv::DescriptorMatcher> matcher =
      cv::DescriptorMatcher::create(cv::DescriptorMatcher::FLANNBASED);
  std::vector<std::vector<cv::DMatch>> matches;
  // 使用KNN-matching算法，令K=2。则每个match得到两个最接近的descriptor，然后计算最接近距离和次接近距离之间的比值，当比值大于既定值时，才作为最终match。
  matcher->knnMatch(descriptor1, descriptor2, matches, 2);

  if (matches.empty()) {
    std::cout << "sift matched points is empty" << endl;
    return 0;
  }
  if (matches.size() < 4) {
    std::cout << "sift matched points is less than 4" << endl;
    return 0;
  }
  const float ratio_thresh = 0.6f;
  std::vector<cv::DMatch> good_matches;
  for (size_t i = 0; i < matches.size(); ++i) {
    if (matches[i][0].distance < ratio_thresh * matches[i][1].distance) {
      good_matches.push_back(matches[i][0]);
    }
  }
  cv::Mat img_good_matches;
  cv::drawMatches(dbImg, keypoints1, testImg, keypoints2, good_matches,
                  img_good_matches, cv::Scalar::all(-1), cv::Scalar::all(-1),
                  std::vector<char>(),
                  cv::DrawMatchesFlags::NOT_DRAW_SINGLE_POINTS);

  // cv::imwrite("img_good_matches.jpg", img_good_matches); //保存图像

  // 获取匹配特征点对的坐标值
  std::vector<cv::Point2f> points_dbImg, points_testImg;
  for (size_t i = 0; i < good_matches.size(); i++) {
    points_dbImg.push_back(
        keypoints1[good_matches[i].queryIdx].pt); //.pt对应坐标
    points_testImg.push_back(keypoints2[good_matches[i].trainIdx].pt);
  }

  // RANSAC算法进一步剔除误匹配点对
  std::vector<uchar> inliers;
  cv::findFundamentalMat(points_dbImg, points_testImg, inliers, cv::FM_RANSAC,
                         3); // p1 p2必须为float型
  // cv::Mat h2 = cv::findHomography(points_dbImg, points_testImg, inliers,
  // cv::RANSAC);
  std::vector<cv::DMatch> good_matches_ransac;
  for (size_t i = 0; i < inliers.size(); ++i) {
    if (inliers[i]) {
      good_matches_ransac.push_back(good_matches[i]);
      dbImgPointsOk.push_back(points_dbImg[i]);     // 图1的点
      testImgPointsOk.push_back(points_testImg[i]); // 图2的对应点
    }
  }
  if (testImgPointsOk.size() < 4 || dbImgPointsOk.size() < 4) {
    return 0;
  }
  return 1;
}
