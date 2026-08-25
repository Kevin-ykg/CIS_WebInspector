using System.Threading;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 单个拼接段的同步检测入口。接口隔离的是作业调度与视觉算法，便于在不加载
    /// OpenCV/TIFF 的情况下验证取消、串行和“最新结果优先”等并发规则。
    /// </summary>
    public interface IInspectionJobRunner
    {
        InspectionJobResult Run(
            StitchedImageResult stitchedResult,
            AppConfig config,
            CancellationToken cancellationToken);
    }
}
