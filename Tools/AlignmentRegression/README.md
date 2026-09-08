# 全局对准 / 白墨迁移回归工具

这是开发验证入口，不加入正式解决方案和 UI，不是生产程序的备用算法。

- `Legacy`：迁移前 8 个 C# 文件的冻结快照；只替换命名空间和 AppConfig 类型别名。
- `Program.cs`：同一原始图同时运行旧快照与当前 C++，比较 Mark 编号、坐标、矩阵、残差、白墨统计、Warp/Remap 像素及预览文件。
- `--frames`：先经过正式二维码/拼接器，再做大图对照；`--white-only` 不需要排版原图。
- `--repeat 20`：预热 3 次后重复原生对准/白墨/Warp，记录重复性与 Private Bytes；只用于开发验证。
- `--layout-log/--tiff-dir/--row-spacing/--layout-height`：只覆盖当前测试进程的参数，不修改软件配置。

运行方法、样本和结果详见 [Regression/AlignmentNative/README.md](../../Regression/AlignmentNative/README.md)。不要通过修改 Legacy 或放宽比较条件来消除迁移差异。
