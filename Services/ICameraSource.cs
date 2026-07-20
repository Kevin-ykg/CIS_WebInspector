using System;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 统一的相机数据源接口，支持在线（物理相机）和离线（磁盘图像）两种模式。
    /// </summary>
    public interface ICameraSource : IDisposable
    {
        /// <summary>
        /// 每帧数据就绪时触发。推荐提供独立 DataArray；若仅提供 DataPointer，订阅者必须在回调内深拷贝。
        /// </summary>
        event EventHandler<FrameReadyEventArgs> FrameReady;

        /// <summary>发生错误时触发</summary>
        event EventHandler<string> ErrorOccurred;

        /// <summary>
        /// 初始化数据源。
        /// 在线模式：configPath 为 .arcf 配置文件路径。
        /// 离线模式：configPath 为包含图像文件的目录路径。
        /// </summary>
        bool Initialize(string configPath);

        /// <summary>开始采集/播放</summary>
        void StartGrab();

        /// <summary>停止采集/播放</summary>
        void StopGrab();

        /// <summary>当前是否正在采集</summary>
        bool IsRunning { get; }

        /// <summary>图像宽度（像素）</summary>
        int ImageWidth { get; }

        /// <summary>图像高度（像素）</summary>
        int ImageHeight { get; }

        /// <summary>行步长（字节，含4字节对齐填充）</summary>
        int LineStride { get; }

        /// <summary>位深（8 = 灰度，24 = RGB）</summary>
        int BitsPerPixel { get; }
    }
}
