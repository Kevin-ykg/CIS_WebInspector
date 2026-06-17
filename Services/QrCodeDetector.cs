using System;
using System.Runtime.InteropServices;
using OpenCvSharp;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 基于 ZXing.Net 的高级二维码检测器。
    /// 完全兼容旧版 API，并能极好地处理工业相机线扫拉伸畸变和黑底白码。
    /// </summary>
    public sealed class QrCodeDetector : IDisposable
    {
        private readonly ZXing.BarcodeReaderGeneric _detector;
        private bool _disposed;

        /// <summary>横向感兴趣区域的起始 X 坐标（自动换算后）</summary>
        public int RoiX => ConfigManager.Config.BaseRoiX / ConfigManager.Config.DownscaleFactor;

        /// <summary>横向感兴趣区域的宽度（自动换算后）</summary>
        public int RoiWidth => ConfigManager.Config.BaseRoiWidth / ConfigManager.Config.DownscaleFactor;

        /// <summary>X轴降采样比例（输入已根据 DownscaleFactor 缩小，无需再缩放）</summary>
        public float DownscaleFactorX { get; set; } = 1.0f;

        /// <summary>Y轴降采样比例（未使用，通过 scaleY 动态覆盖）</summary>
        public float DownscaleFactorY { get; set; } = 1.0f;

        public QrCodeDetector()
        {
            _detector = new ZXing.BarcodeReaderGeneric
            {
                AutoRotate = false,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new System.Collections.Generic.List<ZXing.BarcodeFormat> 
                    { 
                        ZXing.BarcodeFormat.QR_CODE, 
                        ZXing.BarcodeFormat.DATA_MATRIX 
                    }
                }
            };
        }

        public QrDetectionResult Detect(byte[] data, int width, int height, int stride, int bitsPerPixel)
        {
            if (data == null || data.Length == 0) return QrDetectionResult.NotFound;
            var matType = bitsPerPixel == 8 ? MatType.CV_8UC1 : MatType.CV_8UC3;
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                using (var mat = Mat.FromPixelData(height, width, matType, handle.AddrOfPinnedObject(), stride))
                {
                    return DetectCore(mat);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        public QrDetectionResult Detect(IntPtr dataPtr, int width, int height, int stride, int bitsPerPixel)
        {
            if (dataPtr == IntPtr.Zero) return QrDetectionResult.NotFound;
            var matType = bitsPerPixel == 8 ? MatType.CV_8UC1 : MatType.CV_8UC3;
            using (var mat = Mat.FromPixelData(height, width, matType, dataPtr, stride))
            {
                return DetectCore(mat);
            }
        }

        private QrDetectionResult DetectCore(Mat mat)
        {
            try
            {
                Mat grayMat = mat;
                bool needDispose = false;
                if (mat.Channels() == 3)
                {
                    grayMat = new Mat();
                    Cv2.CvtColor(mat, grayMat, ColorConversionCodes.BGR2GRAY);
                    needDispose = true;
                }

                Mat roiMat = null;
                Mat smallMat = null;
                try
                {
                    // 1. 裁剪感兴趣区域 (ROI)
                    int safeX = Math.Max(0, Math.Min(RoiX, grayMat.Width - 1));
                    int safeWidth = Math.Min(RoiWidth, grayMat.Width - safeX);
                    if (safeWidth <= 0) 
                    {
                        safeX = 0;
                        safeWidth = grayMat.Width;
                    }
                    roiMat = new Mat(grayMat, new Rect(safeX, 0, safeWidth, grayMat.Height));

                    // 2. 动态差速拉伸补偿扫描 (解决工业产线料带张力波动或编码器打滑导致的变比畸变)
                    // 从配置中读取基础补偿系数，然后乘以当前的缩放比例，自动抵消由于总体缩放造成的Y轴形变
                    float[] baseScaleYs = ConfigManager.Config.BaseScaleYs;
                    float[] scaleYs = new float[baseScaleYs.Length];
                    for (int i = 0; i < baseScaleYs.Length; i++)
                    {
                        scaleYs[i] = baseScaleYs[i] * ConfigManager.Config.DownscaleFactor;
                    }
                    
                    foreach (var scaleY in scaleYs)
                    {
                        smallMat = new Mat();
                        Cv2.Resize(roiMat, smallMat, new OpenCvSharp.Size(0, 0), DownscaleFactorX, scaleY, InterpolationFlags.Area);
                        // 3. 极性反转 (黑底白码 -> 白底黑码)
                        Cv2.BitwiseNot(smallMat, smallMat);

                        // 4. 将 OpenCV Mat 内存无缝对接给 ZXing
                        int smallW = smallMat.Width;
                        int smallH = smallMat.Height;
                        int smallStride = (int)smallMat.Step();
                        byte[] rawData = new byte[smallStride * smallH];
                        Marshal.Copy(smallMat.Data, rawData, 0, rawData.Length);

                        var source = new ZXing.RGBLuminanceSource(rawData, smallW, smallH, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
                        
                        ZXing.Result result = null;
                        int rotationPass = 0;
                        var currentSource = (ZXing.LuminanceSource)source;

                        // 手动尝试 4 个方向，完全掌控坐标系映射，避免 AutoRotate 的毒瘤坐标错乱Bug
                        for (int r = 0; r < 4; r++)
                        {
                            result = _detector.Decode(currentSource);
                            if (result != null && result.ResultPoints != null && result.ResultPoints.Length > 0)
                            {
                                rotationPass = r;
                                break;
                            }
                            currentSource = currentSource.rotateCounterClockwise();
                        }

                        if (result != null)
                        {
                            // 5. 将发现的坐标映射回原始的 0 度坐标系
                            float cx = 0, cy = 0;
                            for (int i = 0; i < result.ResultPoints.Length; i++)
                            {
                                float px = result.ResultPoints[i].X;
                                float py = result.ResultPoints[i].Y;
                                float origX = px;
                                float origY = py;

                                // 逆时针旋转的坐标逆映射
                                if (rotationPass == 1) // 90度逆时针
                                {
                                    origX = source.Width - 1 - py;
                                    origY = px;
                                }
                                else if (rotationPass == 2) // 180度
                                {
                                    origX = source.Width - 1 - px;
                                    origY = source.Height - 1 - py;
                                }
                                else if (rotationPass == 3) // 270度逆时针
                                {
                                    origX = py;
                                    origY = source.Height - 1 - px;
                                }

                                cx += (origX / DownscaleFactorX) + safeX;
                                cy += (origY / scaleY);
                            }
                            cx /= result.ResultPoints.Length;
                            cy /= result.ResultPoints.Length;

                            smallMat.Dispose();

                            return new QrDetectionResult
                            {
                                Found = true,
                                CenterX = (int)Math.Round(cx),
                                CenterY = (int)Math.Round(cy),
                                DecodedText = result.Text
                            };
                        }

                        smallMat.Dispose();
                        smallMat = null;
                    }

                    return QrDetectionResult.NotFound;
                }
                finally
                {
                    smallMat?.Dispose();
                    roiMat?.Dispose();
                    if (needDispose) grayMat.Dispose();
                }
            }
            catch
            {
                return QrDetectionResult.NotFound;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
