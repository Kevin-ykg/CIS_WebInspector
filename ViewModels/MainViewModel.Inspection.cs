using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CIS_WebInspector.Models;
using CIS_WebInspector.Services;

namespace CIS_WebInspector.ViewModels
{
    /// <summary>
    /// 拼接完成后的 UI 编排：提交后台检测、发布有效结果并分发界面告警。
    /// 作业互斥、取消和过期结果屏障由 InspectionJobCoordinator 统一维护。
    /// </summary>
    public partial class MainViewModel
    {
        // ---- 拼接完成回调 ----
        /// <summary>
        /// 接收拥有独立缓冲区的拼接结果；在线、离线模式都异步启动检测作业，
        /// 只有离线模式需要立即截断后续帧并等待用户恢复。
        /// </summary>
        private void OnStitchCompleted(object sender, StitchedImageResult result)
        {
            // 保留在内存中供后续缺陷检测使用
            _lastStitchedResult = result;

            // 离线模式在处理线程内立即停源并清空后续排队帧，避免跨越第二个二维码继续消费。
            OfflineImageSource offlineSource = CameraSource as OfflineImageSource;
            bool isOffline = offlineSource != null;
            if (isOffline)
            {
                PauseOfflineAfterCurrentFrame(offlineSource, "拼接完成");
            }

            // 拼接图保存也进入同一个有界后台队列，不再为每张图创建无界 Task。
            if (IsAutoSaveEnabled)
            {
                string saveDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "拼接后图像");
                string fileName = $"stitched_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                string filePath = System.IO.Path.Combine(saveDir, fileName);
                bool queued = _imageSaveQueue.TryEnqueue(
                    result.Data,
                    result.Width,
                    result.Height,
                    result.Stride,
                    result.BitsPerPixel,
                    filePath,
                    1000,
                    $"已自动保存拼接图像: {fileName}");
                if (!queued)
                    AddLog("[WARN] 拼接图保存队列持续繁忙，本次拼接图未自动保存，请使用手动保存。检测结果不受影响。");
            }

            // 不等待 UI 预览，直接把独立的拼接缓冲区交给后台作业。
            // 在线模式因此可以继续接收后续帧；离线模式仍由上方检查点逻辑负责暂停。
            AddLog($"[缺陷流水线] 已提交{(isOffline ? "离线" : "在线")}拼接段，后台开始处理。");
            StartInspectionJob(result);

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                UpdateStitchedPreview(result);
                string msg = $"拼接完成: {result.Width}x{result.Height}, QR: [{result.StartQrText}] → [{result.EndQrText}]";
                LastStitchInfo = msg;
                AddLog(msg);

                // 离线模式在段结束后仍暂停，用户可查看结果后主动恢复；
                // 拼接成功属于正常流程，只写界面状态和日志，不再弹出阻塞式提示框。
                if (isOffline)
                {
                    ExecuteStop(null);
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        // ---- 在线/离线缺陷检测作业提交与结果发布 ----
        /// <summary>以 fire-and-forget 方式提交作业；取消、串行和异常记录由独立协调器负责。</summary>
        private void StartInspectionJob(StitchedImageResult result)
        {
            // 必须在提交作业的同步调用栈内取快照。离线模式随后会停止采集并重新开放设置窗口，
            // 但已经提交的作业仍应完整使用拼接完成瞬间的参数版本。
            AppConfig jobConfigSnapshot = ConfigManager.CaptureSnapshot();
            _inspectionJobCoordinator.Submit(
                result,
                jobConfigSnapshot,
                PublishInspectionJobResultAsync);
        }

        /// <summary>
        /// 在 UI Dispatcher 上发布检测结果。进入 Dispatcher 后再次检查作业身份，防止等待排队期间
        /// 新拼接段已经替换当前作业，而旧结果仍覆盖最新预览或弹出过期告警。
        /// </summary>
        private async Task PublishInspectionJobResultAsync(
            InspectionJobResult jobResult,
            long jobId)
        {
            Action publish = () =>
            {
                if (!_inspectionJobCoordinator.IsCurrent(jobId))
                    return;

                if (jobResult.Succeeded &&
                    jobResult.GlobalImageBytes != null &&
                    jobResult.GlobalImageBytes.Length > 0)
                {
                    PublishGlobalDefectPreview(jobResult.GlobalImageBytes);
                }

                if (jobResult.WhiteInkPreviewBytes != null &&
                    jobResult.WhiteInkPreviewBytes.Length > 0)
                {
                    // 用带 Bottom Mark 轮廓的副本替换拼接预览；_lastStitchedResult 原始像素保持不变。
                    PublishWhiteInkPreview(jobResult.WhiteInkPreviewBytes);
                }

                if (jobResult.WhiteInkInspection?.RequiresWarning == true)
                    ShowWhiteInkWarning(jobResult.WhiteInkInspection);
            };

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                publish();
                return;
            }

            await dispatcher.InvokeAsync(
                publish,
                System.Windows.Threading.DispatcherPriority.Normal);
        }

        /// <summary>从内存 JPEG 创建 OnLoad/Freeze 的 BitmapImage，使流关闭后仍可跨线程安全绑定。</summary>
        private void PublishGlobalDefectPreview(byte[] imageBytes)
        {
            try
            {
                using (var stream = new System.IO.MemoryStream(imageBytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    GlobalDefectPreview = bitmap;
                }
            }
            catch (Exception ex)
            {
                AddLog($"[缺陷流水线] 无法加载全局缺陷图到界面: {ex.Message}");
            }
        }

        /// <summary>把算法服务生成的有限尺寸标注 JPEG 发布到“拼接结果”，不触碰原始拼接缓存。</summary>
        private void PublishWhiteInkPreview(byte[] imageBytes)
        {
            try
            {
                using (var stream = new System.IO.MemoryStream(imageBytes))
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    StitchedPreview = new WriteableBitmap(decoder.Frames[0]);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[白墨检测] 无法加载 Bottom Mark 标注预览：{ex.Message}");
            }
        }

        /// <summary>白墨告警只在 UI 层显示，后台 OpenCV 线程不直接操作 WPF。</summary>
        private void ShowWhiteInkWarning(WhiteInkInspectionResult result)
        {
            string details = result.CanEvaluate
                ? $"检测状态：{result.StatusDisplayName}\n" +
                  $"相对白墨：{result.InkLevelPercent:F1}%（估算缺少 {result.EstimatedMissingPercent:F1}%）\n" +
                  $"Mark 灰度均值：{result.MarkMean:F1}，标准差：{result.MarkStandardDeviation:F1}\n" +
                  $"拉丝提示：{(result.HasStreaking ? "是" : "否")}"
                : "底部 Mark 有效样本不足，当前批次无法完成白墨质量判定。";

            System.Windows.MessageBox.Show(
                details +
                "\n\n请检查白墨输送管路和打印喷头。百分比为现场灰度分级，不是墨层厚度实测值。",
                "白墨出墨异常",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }

        /// <summary>向当前检测作业发出取消请求；协调器管理令牌和最终资源释放。</summary>
        private void CancelInspectionJob()
        {
            _inspectionJobCoordinator.CancelCurrent();
        }

        // ---- 二维码超时警告回调 ----
        private void OnQrTimeoutWarning(object sender, string message)
        {
            // 该事件由 ImageStitcher 在当前帧的算法调用栈内同步触发。必须在切回 UI 前
            // 立即记录消费帧检查点，否则 Timer 可能已经把剩余文件全部预读完毕。
            OfflineImageSource offlineSource = CameraSource as OfflineImageSource;
            bool isOffline = offlineSource != null;
            if (isOffline)
                PauseOfflineAfterCurrentFrame(offlineSource, "连续无二维码告警");

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                LastStitchInfo = message;

                // 离线回放暂停在告警帧，便于检查输入图像并从准确帧序号恢复。
                if (isOffline)
                {
                    ExecuteStop(null);
                    System.Windows.MessageBox.Show(message + "\n请检查打印质量！", "识别异常", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        // ---- 错误回调 ----
        private void OnError(object sender, string message)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                StatusText = $"错误: {message}";
                AddLog($"[ERROR] {message}");
            });
        }

        // ---- 底层日志回调 ----
        private void OnLogMessageEvent(object sender, string message)
        {
            AddLog(message);
        }
    }
}
