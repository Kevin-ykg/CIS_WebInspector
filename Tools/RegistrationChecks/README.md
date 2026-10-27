# 局部配准开发检查

这是可重复执行的调试工具，不是生产程序的启动依赖。正式算法源头是 `Native/src/patch_alignment.cpp`，区域准备在 `patch_region.cpp`；工具直接编译这些源文件，不维护第二套候选算法。

从项目根目录执行：

```powershell
$root = (Get-Location).Path
cmake -S Tools/RegistrationChecks -B obj/RegistrationChecks -A x64 -DOpenCV_DIR="$root/obj/VisionNative/opencv-build"
cmake --build obj/RegistrationChecks --config Release --parallel 2
& '.\obj\RegistrationChecks\Release\StabilityChecks.exe'
& '.\obj\RegistrationChecks\Release\RegistrationChecks.exe' --boundary-checks
```

- `StabilityChecks`：独立已知变换、几何边界、稀疏证据、重复方向、错周期等 15 项检查。
- `--boundary-checks`：边缘碎片、完整散点、输入只读与检测区域保持等 8 项检查。
- `--roi-sweep <输入目录> <输出目录>`：在同一真实整图上改变裁框，比较矩阵与状态。输入格式见 [开发调试与验证](../../docs/开发调试与验证.md)。

输出应放在 `obj/`，完成核对后可删除。人工确认标注和固定回归规约放在 `Regression/`，不要和临时导出图混放。合成检查通过不代表实物缺陷精度或召回率已达标。
