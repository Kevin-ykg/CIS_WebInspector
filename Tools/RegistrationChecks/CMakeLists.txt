cmake_minimum_required(VERSION 3.20)
project(RegistrationChecks LANGUAGES CXX)
find_package(OpenCV 4.10.0 EXACT REQUIRED COMPONENTS core imgproc imgcodecs features2d calib3d ximgproc)
get_filename_component(ROOT "../.." ABSOLUTE)
add_executable(RegistrationChecks main.cpp crop_checks.cpp
  "${ROOT}/Native/src/patch_alignment.cpp" "${ROOT}/Native/src/patch_region.cpp"
  "${ROOT}/Native/src/patch_periodicity.cpp" "${ROOT}/Native/src/patch_template_cache.cpp" "${ROOT}/Native/src/patch_detector.cpp"
  "${ROOT}/Native/src/patch_fine_line.cpp")
add_executable(StabilityChecks stability_checks.cpp
  "${ROOT}/Native/src/patch_region.cpp"
  "${ROOT}/Native/src/patch_periodicity.cpp" "${ROOT}/Native/src/patch_template_cache.cpp"
  "${ROOT}/Native/src/patch_detector.cpp" "${ROOT}/Native/src/patch_fine_line.cpp")
foreach(TARGET_NAME RegistrationChecks StabilityChecks)
  target_include_directories(${TARGET_NAME} PRIVATE "${ROOT}/Native/src" "${ROOT}/Native/include" ${OpenCV_INCLUDE_DIRS})
  target_compile_features(${TARGET_NAME} PRIVATE cxx_std_17)
  target_compile_definitions(${TARGET_NAME} PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
  target_compile_options(${TARGET_NAME} PRIVATE /W4 /utf-8 /EHsc /fp:precise)
  target_link_libraries(${TARGET_NAME} PRIVATE ${OpenCV_LIBS})
endforeach()
