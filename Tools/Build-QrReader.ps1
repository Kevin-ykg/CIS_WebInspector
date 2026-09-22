<#
.SYNOPSIS
使用本项目已验证的 OpenCV 缓存，构建不包含 CIS 对准/缺陷业务的通用二维码 DLL，并生成同事交付目录。
.EXAMPLE
powershell -ExecutionPolicy Bypass -File Tools/Build-QrReader.ps1 -SampleImage 'C:\samples\qr.jpg'
#>
[CmdletBinding()]
param(
    [string]$OpenCvDirectory = '',
    [string]$OutputDirectory = '',
    [string]$SampleImage = '',
    [ValidateRange(1, 32)][int]$Parallelism = 2
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OpenCvDirectory) { $OpenCvDirectory = Join-Path $root 'obj\VisionNative\opencv-build' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'outputs\QrReader_SDK' }
if (-not (Test-Path -LiteralPath (Join-Path $OpenCvDirectory 'OpenCVConfig.cmake'))) {
    throw '缺少 OpenCV 4.10.0 构建缓存。先运行 Tools/Build-VisionNative.ps1，或指定 -OpenCvDirectory。'
}
$build = Join-Path $root 'obj\QrReaderBuild'
& cmake -S (Join-Path $root 'Native') -B $build -A x64 "-DOpenCV_DIR=$OpenCvDirectory" -DQR_READER_ONLY=ON -DQR_BUILD_EXAMPLES=ON -DQR_BUILD_TESTS=OFF
if ($LASTEXITCODE -ne 0) { throw '通用二维码工程配置失败' }
& cmake --build $build --config Release --parallel $Parallelism -- /p:UseStructuredOutput=false
if ($LASTEXITCODE -ne 0) { throw '通用二维码工程编译失败' }

# 交付目录只包含 QR 相关内容，不复制 WPF、设备 SDK、对准或缺陷检测源码。
# 不清理调用方指定目录；只更新下面明确列出的本工具产物。
foreach ($directory in @('', 'include', 'include\qr', 'src', 'examples', 'models', 'licenses')) {
    New-Item -ItemType Directory -Path (Join-Path $OutputDirectory $directory) -Force | Out-Null
}
foreach ($name in @('QrReader.dll', 'QrReader.lib', 'QrRecognition.lib', 'QrReaderExample.exe', 'QrReaderCExample.exe')) {
    Copy-Item -LiteralPath (Join-Path $build "Release\$name") -Destination $OutputDirectory -Force
}
foreach ($name in @('qr_detector.cpp', 'qr_detector.h', 'qr_geometry.cpp', 'qr_recovery.cpp', 'qr_blur_local.cpp', 'qr_reader_api.cpp', 'qr_api_support.h')) {
    Copy-Item -LiteralPath (Join-Path $root "Native\src\$name") -Destination (Join-Path $OutputDirectory 'src') -Force
}
Copy-Item -LiteralPath (Join-Path $root 'Native\include\qr\qr_detector.h') -Destination (Join-Path $OutputDirectory 'include\qr') -Force
Copy-Item -LiteralPath (Join-Path $root 'Native\include\qr_reader_api.h') -Destination (Join-Path $OutputDirectory 'include') -Force
Copy-Item -LiteralPath (Join-Path $root 'Native\CMakeLists.txt') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $root 'Native\QR_READER_README.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
Get-ChildItem -LiteralPath (Join-Path $root 'Native\examples') -File | Copy-Item -Destination (Join-Path $OutputDirectory 'examples') -Force
Get-ChildItem -LiteralPath (Join-Path $root 'Assets\WeChatQRCode') -File | Copy-Item -Destination (Join-Path $OutputDirectory 'models') -Force
Get-ChildItem -LiteralPath (Join-Path $root 'Native\licenses') -File | Copy-Item -Destination (Join-Path $OutputDirectory 'licenses') -Force

if ($SampleImage) {
    # 原样复制用户指定的验证图片，不将样本文本/路径写入算法。
    $sampleName = 'sample' + [IO.Path]::GetExtension($SampleImage)
    $sampleDestination = Join-Path $OutputDirectory $sampleName
    Copy-Item -LiteralPath $SampleImage -Destination $sampleDestination -Force
    & (Join-Path $OutputDirectory 'QrReaderExample.exe') (Join-Path $OutputDirectory 'models') $sampleDestination (Join-Path $OutputDirectory 'sample_result.png')
    if ($LASTEXITCODE -ne 0) { throw '交付包 C++ 整图示例未命中，请检查输入/模型/运行库。' }
    & (Join-Path $OutputDirectory 'QrReaderCExample.exe') (Join-Path $OutputDirectory 'models') $sampleDestination
    if ($LASTEXITCODE -ne 0) { throw '交付包 DLL C ABI 示例未命中，请检查输入/模型/运行库。' }
}
Write-Host "通用二维码交付目录：$OutputDirectory"
