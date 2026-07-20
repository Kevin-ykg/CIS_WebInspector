using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// tlc.dll 的最薄 P/Invoke 边界。原生函数返回 0 表示成功；返回的字符串指针由 SDK 管理，
    /// 托管侧只立即复制为 string，不缓存或释放该指针。上层负责串行化设备操作和展示错误。
    /// </summary>
    public static class TlcSdkWrapper
    {
        private const string DllName = "tlc.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int enum_all_card_ports(ref IntPtr chports, ref int size);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int open_port(string chport);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int close_port();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int get_camera_parameters(ref IntPtr pbuffer, ref int size);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int help(ref IntPtr pbuffer, ref int size);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr get_error_msg();

        // ---- 参数设置接口 ----
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_ffc_start(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_ffc_mode(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_lpc_selector(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_ffc_algorithm(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_mirror_mode(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_usl(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_uss(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_light_red(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_light_green(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_light_blue(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_light_white(int r, int g, int b);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_pixel_format(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_line_rate(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_offset(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_gain(float value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_trigger_mode(int value);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_test_pattern(int value);
        

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int set_binarization_threshold(int value);


        // ---- 安全封装的 C# 方法 ----

        /// <summary>
        /// 获取所有串口，以列表形式返回
        /// </summary>
        public static List<string> GetPorts()
        {
            List<string> portsList = new List<string>();
            IntPtr ptr = IntPtr.Zero;
            int size = 0;
            if (enum_all_card_ports(ref ptr, ref size) == 0 && size > 0 && ptr != IntPtr.Zero)
            {
                // 立即复制 SDK 返回的 ANSI 缓冲区；不把原生指针暴露给 ViewModel。
                string portsStr = Marshal.PtrToStringAnsi(ptr, size);
                if (!string.IsNullOrEmpty(portsStr))
                {
                    var splitted = portsStr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    portsList.AddRange(splitted);
                }
            }
            return portsList;
        }

        /// <summary>
        /// 获取底层相机的完整参数状态文本
        /// </summary>
        public static string GetCameraParameters()
        {
            IntPtr ptr = IntPtr.Zero;
            int size = 0;
            int ret = get_camera_parameters(ref ptr, ref size);
            if (ret == 0)
            {
                if (ptr != IntPtr.Zero)
                {
                    return size > 0 ? Marshal.PtrToStringAnsi(ptr, size) : Marshal.PtrToStringAnsi(ptr);
                }
                return "获取成功，但未返回具体数据。";
            }
            return $"读取失败 (Code: {ret})\n底层反馈: {GetLastErrorMsg()}\n\n可能原因:\n1. 硬件未响应或串口被独占\n2. 需要等待几百毫秒后再读取";
        }

        /// <summary>
        /// 获取最新的错误信息
        /// </summary>
        public static string GetLastErrorMsg()
        {
            IntPtr ptr = get_error_msg();
            if (ptr != IntPtr.Zero)
            {
                return Marshal.PtrToStringAnsi(ptr);
            }
            return "Unknown error";
        }
    }
}
