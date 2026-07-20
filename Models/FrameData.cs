using System;

namespace CIS_WebInspector.Models
{
    /// <summary>
    /// 帧就绪事件参数，由相机源在每帧采集完成时触发。
    /// </summary>
    public class FrameReadyEventArgs : EventArgs
    {
        /// <summary>指向非托管帧数据的指针（兼容旧式输入；进入异步队列前必须深拷贝）</summary>
        public IntPtr DataPointer { get; set; }

        /// <summary>帧数据的托管字节数组（当前在线、离线输入均使用独立数组）</summary>
        public byte[] DataArray { get; set; }

        /// <summary>环形缓存区索引</summary>
        public int BufferIndex { get; set; }

        /// <summary>是否为残帧（数据不完整）</summary>
        public bool IsBroken { get; set; }

        /// <summary>图像宽度（像素）</summary>
        public int Width { get; set; }

        /// <summary>图像高度（像素）</summary>
        public int Height { get; set; }

        /// <summary>行步长（字节，含4字节对齐填充）</summary>
        public int Stride { get; set; }

        /// <summary>位深（8 = 灰度，24 = RGB）</summary>
        public int BitsPerPixel { get; set; }
    }

    /// <summary>
    /// 二维码检测结果。
    /// </summary>
    public class QrDetectionResult
    {
        /// <summary>是否检测到二维码</summary>
        public bool Found { get; set; }

        /// <summary>二维码中心X坐标（像素）</summary>
        public int CenterX { get; set; }

        /// <summary>二维码中心Y坐标（像素）</summary>
        public int CenterY { get; set; }

        /// <summary>
        /// 二维码在输入图像 Y 方向上的像素高度。
        /// 检测器内部使用过 Y 轴缩放补偿时，该值已还原到输入图像坐标系。
        /// </summary>
        public double PixelHeight { get; set; }

        /// <summary>二维码在输入图像 X 方向上的像素宽度。</summary>
        public double PixelWidth { get; set; }

        /// <summary>二维码解码文本内容</summary>
        public string DecodedText { get; set; }

        public static QrDetectionResult NotFound => new QrDetectionResult { Found = false };
    }

    /// <summary>
    /// 拼接完成的独立膜片图案结果。
    /// </summary>
    public class StitchedImageResult
    {
        /// <summary>拼接后的独立像素缓冲区，由结果对象持有，可安全跨线程用于预览、保存和检测。</summary>
        public byte[] Data { get; set; }

        /// <summary>图像宽度（像素）</summary>
        public int Width { get; set; }

        /// <summary>拼接后的图像总高度（像素）</summary>
        public int Height { get; set; }

        /// <summary>行步长（字节）</summary>
        public int Stride { get; set; }

        /// <summary>起始二维码解码文本</summary>
        public string StartQrText { get; set; }

        /// <summary>结束二维码解码文本</summary>
        public string EndQrText { get; set; }

        /// <summary>当前拼接段第 0 行在连续采集数据中的全局 Y 坐标。</summary>
        public long SegmentStartGlobalY { get; set; }

        /// <summary>第二个二维码中心在连续采集数据中的全局 Y 坐标。</summary>
        public long EndQrGlobalY { get; set; }

        /// <summary>第二个二维码中心在当前拼接图中的局部 Y 坐标。</summary>
        public double EndQrCenterY { get; set; }

        /// <summary>第二个二维码中心在当前拼接图中的 X 坐标。</summary>
        public double EndQrCenterX { get; set; }

        /// <summary>第二个二维码在当前处理分辨率下的 Y 方向像素高度。</summary>
        public double EndQrPixelHeight { get; set; }

        /// <summary>第二个二维码在当前处理分辨率下的 X 方向像素宽度。</summary>
        public double EndQrPixelWidth { get; set; }

        /// <summary>位深（8 或 24）</summary>
        public int BitsPerPixel { get; set; }

        /// <summary>
        /// 将拼接图像保存到硬盘指定路径。
        /// </summary>
        /// <param name="filePath">目标文件完整路径（如 D:\output\segment_001.bmp）</param>
        public void SaveToFile(string filePath)
        {
            if (Data == null || Data.Length == 0) return;

            using (var mat = BitsPerPixel == 8
                ? new OpenCvSharp.Mat(Height, Width, OpenCvSharp.MatType.CV_8UC1)
                : new OpenCvSharp.Mat(Height, Width, OpenCvSharp.MatType.CV_8UC3))
            {
                int lineBytes = BitsPerPixel == 8 ? Width : 3 * Width;
                if (lineBytes == Stride)
                {
                    System.Runtime.InteropServices.Marshal.Copy(Data, 0, mat.Data, Data.Length);
                }
                else
                {
                    for (int i = 0; i < Height; i++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(Data, i * Stride, mat.Data + i * (int)mat.Step(), lineBytes);
                    }
                }
                OpenCvSharp.Cv2.ImWrite(filePath, mat);
            }
        }
    }
}
