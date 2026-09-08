cmake_minimum_required(VERSION 3.20)
project(CISVisionCore VERSION 2.0 LANGUAGES CXX)
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Windows x64 required")
endif()
find_package(OpenCV 4.10.0 EXACT REQUIRED COMPONENTS core imgproc objdetect wechat_qrcode features2d calib3d ximgproc)
add_library(CISVisionCore SHARED src/qr_api.cpp src/qr_detector.cpp src/qr_geometry.cpp src/qr_recovery.cpp
  src/patch_api.cpp src/patch_detector.cpp src/patch_alignment.cpp src/patch_fine_line.cpp
  src/alignment_api.cpp src/alignment_global.cpp src/alignment_marks.cpp
  src/alignment_side_grid.cpp src/alignment_white_ink.cpp src/alignment_warp.cpp)
target_compile_features(CISVisionCore PRIVATE cxx_std_17)
target_compile_definitions(CISVisionCore PRIVATE CIS_VISION_EXPORTS NOMINMAX WIN32_LEAN_AND_MEAN)
target_include_directories(CISVisionCore PUBLIC include PRIVATE ${OpenCV_INCLUDE_DIRS})
target_link_libraries(CISVisionCore PRIVATE ${OpenCV_LIBS})
target_compile_options(CISVisionCore PRIVATE /W4 /utf-8 /EHsc /fp:precise)
