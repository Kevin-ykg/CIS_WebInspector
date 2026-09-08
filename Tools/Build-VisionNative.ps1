<#
.SYNOPSIS
构建二维码、全局/局部对准、白墨与三类缺陷检测 C++ DLL。首次构建下载固定版本的 OpenCV/contrib；后续复用 obj/VisionNative。
.EXAMPLE
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateRange(1, 32)][int]$Parallelism = 2
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root 'obj\VisionNative'
$opencvBuild = Join-Path $cache 'opencv-build'
$nativeBuild = Join-Path $cache 'core-build'
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    throw '请安装 CMake 并加入 PATH，同时安装 Visual Studio 的「使用 C++ 的桌面开发」工作负载。'
}
New-Item -ItemType Directory -Path $cache -Force | Out-Null

function Invoke-CMake([string[]]$Arguments) {
    & cmake @Arguments
    if ($LASTEXITCODE -ne 0) { throw "CMake 失败，退出码 $LASTEXITCODE" }
}
function Get-Source([string]$Repository, [string]$ExpectedHash) {
    $directory = Join-Path $cache "$Repository-4.10.0"
    $archive = Join-Path $cache "$Repository-4.10.0.zip"
    # 使用固定标签及校验值，避免上游变化使检测结果在重建后发生漂移。
    if (-not (Test-Path -LiteralPath $directory)) {
        if (-not (Test-Path -LiteralPath $archive)) {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri "https://github.com/opencv/$Repository/archive/refs/tags/4.10.0.zip" -OutFile $archive -UseBasicParsing
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $ExpectedHash) {
            throw "源码压缩包校验失败，请核对文件：$archive"
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $cache
    }
    return $directory
}
$opencv = Get-Source 'opencv' '3810BCA2B1D1C572912DF0AC3888126341F3762DFD28E91068C805FB656D0E51'
$contrib = Get-Source 'opencv_contrib' '15B1DECFA4D2EAF3B39148EFF4F311739FDB32F0DCCE58E7EDB3E95A560CCBD3'

# 算法与原 OpenCvSharp 同为 OpenCV 4.10.0，并保留 IPP 优化。
# 不关闭 IPP：插值/卷积的浮点差异可能影响模糊二维码的临界识别与几何坐标。
Invoke-CMake @('-S', $opencv, '-B', $opencvBuild, '-A', 'x64',
    '-DCMAKE_POLICY_VERSION_MINIMUM=3.5', '-DCMAKE_CXX_FLAGS=/utf-8', '-DCMAKE_C_FLAGS=/utf-8',
    "-DOPENCV_EXTRA_MODULES_PATH=$contrib/modules", '-DBUILD_LIST=core,imgproc,dnn,wechat_qrcode,features2d,calib3d,ximgproc',
    '-DBUILD_SHARED_LIBS=OFF', '-DBUILD_WITH_STATIC_CRT=OFF', '-DBUILD_TESTS=OFF', '-DBUILD_PERF_TESTS=OFF',
    '-DBUILD_EXAMPLES=OFF', '-DBUILD_opencv_apps=OFF', '-DBUILD_JAVA=OFF', '-DENABLE_PRECOMPILED_HEADERS=OFF',
    '-DWITH_EIGEN=OFF', '-DBUILD_opencv_python2=OFF', '-DBUILD_opencv_python3=OFF',
    '-DWITH_IPP=ON', '-DWITH_OPENCL=OFF', '-DWITH_ITT=OFF', '-DWITH_FFMPEG=OFF', '-DWITH_MSMF=OFF', '-DOPENCV_DNN_OPENCL=OFF')
Invoke-CMake @('--build', $opencvBuild, '--config', $Configuration, '--parallel', "$Parallelism", '--', '/p:UseStructuredOutput=false')
Invoke-CMake @('-S', (Join-Path $root 'Native'), '-B', $nativeBuild, '-A', 'x64', "-DOpenCV_DIR=$opencvBuild")
Invoke-CMake @('--build', $nativeBuild, '--config', $Configuration, '--parallel', "$Parallelism", '--', '/p:UseStructuredOutput=false')
Write-Host "已生成：$nativeBuild\$Configuration\CISVisionCore.dll"
