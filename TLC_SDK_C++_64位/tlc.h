#ifndef _THREE_LETTER_COMMAND_H
#define _THREE_LETTER_COMMAND_H

#ifdef TLC_EXPORT
#define TLC_API _declspec(dllexport)
#else
#define TLC_API _declspec(dllimport)
#endif // TLC_EXPORT

#ifndef TLC_EXPORT
#pragma comment(lib, "tlc.lib")
#endif

#define TLC_OK 0
#define TLC_FAIL -1

extern "C"
{
    // 板卡选择
    TLC_API int enum_all_card_ports(const char **chports, int *size); // 以;分隔
    TLC_API int open_port(const char *chport);
    TLC_API int close_port();

    // 设置内容
    TLC_API int help(const char **pbuffer, int *size);                  // 显示帮助信息
    TLC_API int get_camera_parameters(const char **pbuffer, int *size); // 显示相机当前参数
    TLC_API int get_uid(const char **pbuffer, int *size);               // 显示相机UID
    TLC_API int set_ffc_start(int value);                               // 执行平场校准
    TLC_API int set_ffc_mode(int value);                                // 设置平场校准模式
    TLC_API int set_lpc_selector(int value);                            // 选择平场校准参数集
    TLC_API int set_ffc_algorithm(int value);                           // 设置平场校准算法
    TLC_API int set_light_red(int value);                               // 设置相机灯光红色亮度
    TLC_API int set_light_green(int value);                             // 设置相机灯光绿色亮度
    TLC_API int set_light_blue(int value);                              // 设置相机灯光蓝色亮度
    TLC_API int set_light_white(int r, int g, int b);                   // 设置相机灯光RGB亮度
    TLC_API int set_light_red_pulse(int value);                         // 设置相机灯光红色亮度
    TLC_API int set_light_green_pulse(int value);                       // 设置相机灯光绿色亮度
    TLC_API int set_light_blue_pulse(int value);                        // 设置相机灯光蓝色亮度
    TLC_API int set_pixel_format(int value);                            // 设置相机像素输出模式
    TLC_API int set_line_rate(int value);                               // 设置相机行频
    TLC_API int set_offset(int value);                                  // 设置相机偏置
    TLC_API int set_gain(float value);                                  // 设置相机增益
    TLC_API int set_trigger_mode(int value);                            // 设置相机触发模式
    TLC_API int set_test_pattern(int value);                            // 设置相机测试模板
    TLC_API int set_mirror_mode(int value);                             // 设置相机镜像输出，0：off，1：on
    TLC_API int set_usd(int value);                                     // 默认设置相机配置集
    TLC_API int set_usl(int value);                                     // 加载相机配置集
    TLC_API int set_uss(int value);                                     // 保存相机配置集
    TLC_API int set_binarization_threshold(int value);                  // 设置二值化阈值
    TLC_API const char *get_error_msg();                                // 读取当前错误信息
    TLC_API int send_string(const char *str);

    TLC_API int abc(short **pdata, int *size);  // 显示错位数据
    TLC_API int def(double **pdata, int *size); // 显示标定数据
}

#endif
