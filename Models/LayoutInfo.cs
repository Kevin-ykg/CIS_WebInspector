using System.Collections.Generic;

namespace CIS_WebInspector.Models
{
    /// <summary>
    /// 表示从 Debug.log 中解析出的单张大图的排版信息。
    /// </summary>
    public class LayoutInfo
    {
        /// <summary>
        /// 提取到的原始排版 TIFF 文件名（例如：2026-06-24-15-02-33.tiff）
        /// </summary>
        public string TiffFileName { get; set; }

        /// <summary>
        /// 拼接后的完整 TIFF 文件路径（结合 TiffImageDir 目录）
        /// </summary>
        public string TiffFullPath { get; set; }

        /// <summary>
        /// 排版中的每个小零件的坐标信息集合
        /// </summary>
        public List<PartLocation> Parts { get; set; } = new List<PartLocation>();
    }

    /// <summary>
    /// 单个小零件在排版空间（TIFF空间）中的相对坐标信息（单位：mm 或物理坐标系相关值）。
    /// </summary>
    public class PartLocation
    {
        public string HotInkTaskID { get; set; }
        public double RelativeCenterX { get; set; }
        public double RelativeCenterY { get; set; }
        public double RelativeTopLeftX { get; set; }
        public double RelativeTopLeftY { get; set; }
        public double RelativeBottomRightX { get; set; }
        public double RelativeBottomRightY { get; set; }
    }
}
