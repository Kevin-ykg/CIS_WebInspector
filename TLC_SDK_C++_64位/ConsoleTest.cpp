// ConsoleTest.cpp : 此文件包含 "main" 函数。程序执行将在此处开始并结束。
//

#include "tlc.h"

#include <iostream>

int main()
{
    std::string cmd;
    int value = 0;
    int ret = 0;

    {
        std::string err = get_error_msg();
        std::cout << err << std::endl << std::endl;
    }

    {
        const char** p = new const char*;
        int size = 0;
        ret = enum_all_card_ports(p, &size);
        std::cout << "Recv : size = " << size << ", buffer = " << std::endl << *p << std::endl;

        if (size > 0)
        {
            std::string port;
            port.assign(*p, size);
            for (size_t i = 0, begin = 0, end = 0; i < port.size(); i++)
            {
                if (port[i] == ';')
                {
					port = port.substr(begin, i - begin);
                    break;
                }
            }
            ret = open_port(port.c_str());
            if (TLC_OK == ret)
            {
                std::cout << "Open port success." << std::endl << std::endl;
            }
            else
            {
                std::string err = get_error_msg();
                std::cout << "Open port fail.\r\n错误信息如下 : " << err << std::endl << std::endl;
            }
		}


    }

    while (1)
    {
        std::cout << "输入命令:" << std::endl;
        std::cin >> cmd;
        if ("exit" == cmd)
        {
            break;
        }
        else if ("h" == cmd)
        {
            const char** p = new const char*;
            int size = 0;
            ret = help(p, &size);
            if(TLC_OK == ret)
                std::cout << "Recv : size = " << size << ", buffer = " << std::endl << *p << std::endl;
        }
        else if ("gcp" == cmd)
        {
            const char** p = new const char*;
            int size = 0;
            ret = get_camera_parameters(p, &size);
            if (TLC_OK == ret)
                std::cout << "Recv : size = " << size << ", buffer = " << std::endl << *p << std::endl;
        }
        else if ("ffc" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_ffc_start(value);
        }
        else if ("ffm" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_ffc_mode(value);
        }
        else if ("slr" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_light_red(value);
        }
        else if ("slg" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_light_green(value);
        }
        else if ("slb" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_light_blue(value);
        }
        else if ("spf" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_pixel_format(value);
        }
        else if ("ssf" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_line_rate(value);
        }
        else if ("ssg" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_gain(float(value));
        }
        else if ("stm" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_trigger_mode(value);
        }
        else if ("stp" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_test_pattern(value);
        }
        else if ("usd" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_usd(value);
        }
        else if ("usl" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_usl(value);
        }
        else if ("uss" == cmd)
        {
            std::cout << "输入参数值:" << std::endl;
            std::cin >> value;
            ret = set_uss(value);
        }
        else
        {
            continue;
        }
        if (TLC_OK == ret)
        {
            std::cout << "参数设置正常." << std::endl << std::endl;
        }
        else
        {
            std::string err = get_error_msg();
            std::cout << "参数设置错误.\r\n错误信息如下 : " << err << std::endl << std::endl;
        }
    }
}
