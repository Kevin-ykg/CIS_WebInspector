using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 预览更新与资源清理，只在 UI 线程创建 WPF 图像并统一释放后台服务。
    /// </summary>
    public partial class MainViewModel
    {
        // ---- WriteableBitmap 操作 ----
        private void InitializeLivePreview(int width, int height, int bpp)
        {
            var pixelFormat = bpp == 8 ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            var palette = bpp == 8 ? BitmapPalettes.Gray256 : null;
            LivePreview = new WriteableBitmap(width, height, 96, 96, pixelFormat, palette);
        }

        /// <summary>在 UI 线程把最新托管帧复制到预分配 WriteableBitmap，不保留输入缓冲区引用。</summary>
        private void UpdateLivePreview(FrameReadyEventArgs e)
        {
            if (LivePreview == null || e?.DataArray == null) return;

            bool isLocked = false;
            try
            {
                LivePreview.Lock();
                isLocked = true;

                int copyBytes = checked(e.Stride * e.Height);
                Marshal.Copy(e.DataArray, 0, LivePreview.BackBuffer, copyBytes);

                LivePreview.AddDirtyRect(new Int32Rect(0, 0, e.Width, e.Height));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"实时预览更新失败: {ex.Message}");
            }
            finally
            {
                if (isLocked)
                {
                    try { LivePreview.Unlock(); } catch { }
                }
            }
        }

        /// <summary>为超大拼接图生成有限宽度预览；原始分辨率数据仍保留在 StitchedImageResult 中供检测。</summary>
        private void UpdateStitchedPreview(StitchedImageResult result)
        {
            if (result?.Data == null || result.Data.Length == 0 || result.Width <= 0 || result.Height <= 0)
                return;

            var pixelFormat = result.BitsPerPixel == 8 ? PixelFormats.Gray8 : PixelFormats.Bgr24;
            var palette = result.BitsPerPixel == 8 ? BitmapPalettes.Gray256 : null;

            try
            {
                // WPF WriteableBitmap 无法承受 31212x33500 (1GB) 级别的超大纹理，会导致 ArgumentException 或 OOM。
                // 因此我们只为 UI 预览生成一个按比例缩小的预览图（例如限制最大宽度为 2000 像素）
                int maxWidth = 2000;
                float scale = (float)maxWidth / result.Width;
                if (scale >= 1.0f) scale = 1.0f;

                int previewWidth = Math.Max(1, (int)(result.Width * scale));
                int previewHeight = Math.Max(1, (int)(result.Height * scale));

                var matType = result.BitsPerPixel == 8 ? OpenCvSharp.MatType.CV_8UC1 : OpenCvSharp.MatType.CV_8UC3;
                GCHandle handle = GCHandle.Alloc(result.Data, GCHandleType.Pinned);
                WriteableBitmap preview = null;

                try
                {
                    using (var srcMat = OpenCvSharp.Mat.FromPixelData(result.Height, result.Width, matType, handle.AddrOfPinnedObject(), result.Stride))
                    using (var dstMat = new OpenCvSharp.Mat())
                    {
                        OpenCvSharp.Cv2.Resize(srcMat, dstMat, new OpenCvSharp.Size(previewWidth, previewHeight), 0, 0, OpenCvSharp.InterpolationFlags.Area);
                        int previewStride = checked((int)dstMat.Step());
                        int previewBytes = checked(previewStride * previewHeight);
                        preview = new WriteableBitmap(previewWidth, previewHeight, 96, 96, pixelFormat, palette);

                        // WritePixels 直接从缩放后的 Mat 拷入 WPF 缓冲区，省去一份中间 byte[] 和一次托管复制。
                        preview.WritePixels(
                            new Int32Rect(0, 0, previewWidth, previewHeight),
                            dstMat.Data,
                            previewBytes,
                            previewStride);
                    }
                }
                finally
                {
                    handle.Free();
                }

                StitchedPreview = preview;
            }
            catch (Exception ex)
            {
                LastStitchInfo = $"预览生成失败: {ex.Message} (尺寸 {result.Width}x{result.Height})";
            }
        }

        // ---- 资源清理 ----
        private void CleanupSource()
        {
            // 顺序很重要：先取消检测和停止消费者，再退订/释放生产者，避免释放后仍有回调进入。
            CancelInspectionJob();
            StopFrameProcessor(false);
            _acquisitionSession.Close();
        }

        /// <summary>窗体关闭时停止生产/消费链并确定性释放相机、二维码模型和后台保存线程。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ExecuteStop(null);
            CleanupSource();
            _acquisitionSession.Dispose();
            _inspectionJobCoordinator.Dispose();
            _stitcher?.Dispose();
            _imageSaveQueue?.Dispose();
            _appLogger.MessageWritten -= QueueUiLog;
            _appLogger.Dispose();
        }
    }
}
