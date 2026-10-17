using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    // 排版像素准备。保持逐像素浮点计算、Alpha 提取和取消检查与原流程一致。
    public sealed partial class InspectionJobRunner
    {
        /// <summary>
        /// 保留排版 Alpha 作为差分模板，再将显示/Mark 检测用的 BGRA 合成到白底。
        /// tiffMat/alphaMask 的所有权仍由 Run 的 finally 持有；即使中途取消，已分配资源也可释放。
        /// </summary>
        private static void PrepareLayoutImage(
            ref Mat tiffMat, ref Mat alphaMask, IAppLogger log, CancellationToken cancellationToken)
        {
            // Parallel.For 只读这个局部引用；所有行结束后才释放旧 Mat 并替换调用方引用。
            Mat source = tiffMat;
            if (source.Channels() == 4)
            {
                int h = source.Height;
                int w = source.Width;
                // 只提取后续真正需要的 Alpha 通道。Cv2.Split 会额外创建 B/G/R/A 四张
                // 全尺寸 Mat，对约 300 MB TIFF 会显著抬高峰值内存。
                alphaMask = new Mat();
                Cv2.ExtractChannel(source, alphaMask, 3);

                // Alpha 是后续零件设计轮廓的判定依据，必须在 TIFF 合成白底前独立保留。
                int nonZero = Cv2.CountNonZero(alphaMask);
                Log(log, $"  提取Alpha通道: 非零像素={nonZero}, 覆盖率={nonZero * 100.0 / (h * w):F1}%");

                // 设计图的透明区域按白底合成，保证其灰度语义与白色膜片背景一致。
                Mat composited = new Mat(h, w, MatType.CV_8UC3);
                try
                {
                    var parallelOptions = new ParallelOptions
                    {
                        CancellationToken = cancellationToken
                    };

                    unsafe
                    {
                        Parallel.For(0, h, parallelOptions, row =>
                        {
                            byte* srcRow = (byte*)source.Ptr(row);
                            byte* dstRow = (byte*)composited.Ptr(row);

                            for (int col = 0; col < w; col++)
                            {
                                byte sb = srcRow[0];
                                byte sg = srcRow[1];
                                byte sr = srcRow[2];
                                byte sa = srcRow[3];

                                if (sa == 255)
                                {
                                    dstRow[0] = sb;
                                    dstRow[1] = sg;
                                    dstRow[2] = sr;
                                }
                                else if (sa == 0)
                                {
                                    dstRow[0] = 255;
                                    dstRow[1] = 255;
                                    dstRow[2] = 255;
                                }
                                else
                                {
                                    float a = sa * (1f / 255f);
                                    float inverseAlpha = 1f - a;
                                    dstRow[0] = (byte)(sb * a + 255f * inverseAlpha);
                                    dstRow[1] = (byte)(sg * a + 255f * inverseAlpha);
                                    dstRow[2] = (byte)(sr * a + 255f * inverseAlpha);
                                }

                                srcRow += 4;
                                dstRow += 3;
                            }
                        });
                    }

                    source.Dispose();
                    tiffMat = composited;
                    composited = null;
                }
                finally
                {
                    composited?.Dispose();
                }
            }
            else
            {
                // 保留既有日志文案和执行行为。本分支没有构造替代 Alpha；PatchCropper 会跳过
                // 零件差分，不能把流水线 Completed 或零缺陷理解为该图已通过检测。详见 docs 数据契约。
                Log(log, "  [WARN] TIFF无Alpha通道，将使用统一阈值检测。");
            }
        }
    }
}
