<#
.SYNOPSIS
构建二维码、全局/局部对准、白墨与三类缺陷检测 C++ DLL。首次构建下载固定版本的 OpenCV/contrib；后续复用 obj/VisionNative。
.EXAMPLE
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -Configuration Release
.EXAMPLE
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -PrepareOnly
.EXAMPLE
powershell -ExecutionPolicy Bypass -File Tools/Build-VisionNative.ps1 -SourceArchiveDirectory C:\Downloads\OpenCV
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateRange(1, 32)][int]$Parallelism = 2,
    # 可选：预先下载的 opencv-4.10.0.zip / opencv_contrib-4.10.0.zip 所在目录。
    # 指定后不再为源码压缩包联网下载；IPP 等依赖仍可能在 CMake 配置时下载。
    [string]$SourceArchiveDirectory = '',
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root 'obj\VisionNative'
$opencvBuild = Join-Path $cache 'opencv-build'
$nativeBuild = Join-Path $cache 'core-build'
if (-not $PrepareOnly -and -not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    throw '请安装 CMake 并加入 PATH，同时安装 Visual Studio 的「使用 C++ 的桌面开发」工作负载。'
}
New-Item -ItemType Directory -Path $cache -Force | Out-Null

function Invoke-CMake([string[]]$Arguments) {
    & cmake @Arguments
    if ($LASTEXITCODE -ne 0) { throw "CMake 失败，退出码 $LASTEXITCODE" }
}
function Get-SourceArchive([string]$Repository, [string]$Archive, [string]$ExpectedHash) {
    if (Test-Path -LiteralPath $Archive) {
        if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -eq $ExpectedHash) {
            return
        }
        Write-Warning "缓存压缩包不完整或校验失败，将重新获取：$Archive"
        # 只删除本构建缓存中的失败下载，不修改用户提供的本地源文件。
        Remove-Item -LiteralPath $Archive -Force
    }
    if ($SourceArchiveDirectory) {
        $localArchive = Join-Path $SourceArchiveDirectory "$Repository-4.10.0.zip"
        if (-not (Test-Path -LiteralPath $localArchive -PathType Leaf)) {
            throw "未找到本地源码包：$localArchive"
        }
        if ((Get-FileHash -LiteralPath $localArchive -Algorithm SHA256).Hash -ne $ExpectedHash) {
            throw "本地源码包 SHA256 不匹配，未修改源文件：$localArchive"
        }
        Copy-Item -LiteralPath $localArchive -Destination $Archive
        return
    }

    # codeload 是 GitHub 官方归档下载端点，避免网页入口的额外重定向。
    # 两个地址使用同一固定标签、同一校验值；不使用第三方镜像或跳过 TLS 验证。
    $urls = @("https://codeload.github.com/opencv/$Repository/zip/refs/tags/4.10.0",
              "https://github.com/opencv/$Repository/archive/refs/tags/4.10.0.zip")
    $partial = "$Archive.partial"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $lastFailure = ''
    foreach ($url in $urls) {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            try {
                Write-Host "下载 $Repository（第 $attempt/2 次）：$url"
                if ($curl) {
                    & $curl.Source --fail --location --http1.1 --silent --show-error `
                        --connect-timeout 20 --max-time 600 --speed-time 60 --speed-limit 1024 `
                        --output $partial --url $url
                    if ($LASTEXITCODE -ne 0) { throw "curl 下载失败，退出码 $LASTEXITCODE" }
                }
                else {
                    Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -TimeoutSec 600
                }
                if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $ExpectedHash) {
                    throw '下载文件 SHA256 不匹配，不能用于构建'
                }
                # 只有完整且校验通过的文件才成为缓存；中断下载不再冒充有效 zip。
                Move-Item -LiteralPath $partial -Destination $Archive
                return
            }
            catch {
                $lastFailure = $_.Exception.Message
                Write-Warning $lastFailure
            }
            finally {
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
            }
        }
    }
    throw "下载 $Repository 失败：$lastFailure。请用浏览器下载上述官方源码包，命名为 $Repository-4.10.0.zip，再通过 -SourceArchiveDirectory 指定目录。"
}

function Get-Source([string]$Repository, [string]$ExpectedHash) {
    $directory = Join-Path $cache "$Repository-4.10.0"
    $archive = Join-Path $cache "$Repository-4.10.0.zip"
    # contrib 不是独立的 CMake 顶层工程；主库通过 OPENCV_EXTRA_MODULES_PATH
    # 加载它的 modules。分别检查实际参与构建的入口，不能套用主库根目录检查。
    $requiredFiles = if ($Repository -eq 'opencv_contrib') {
        @('modules/wechat_qrcode/CMakeLists.txt', 'modules/ximgproc/CMakeLists.txt')
    }
    else {
        @('CMakeLists.txt', 'modules/core/CMakeLists.txt')
    }
    # 使用固定标签及校验值，避免上游变化使检测结果在重建后发生漂移。
    if (-not (Test-Path -LiteralPath $directory)) {
        Get-SourceArchive $Repository $archive $ExpectedHash
        # 先解压到暂存目录。解压失败/被中断时，不留下貌似已准备好的源码目录。
        $staging = Join-Path $cache ('_' + $Repository + '.extracting')
        if (Test-Path -LiteralPath $staging) {
            throw "上次解压未完成，请先删除临时目录后重试：$staging"
        }
        try {
            New-Item -ItemType Directory -Path $staging | Out-Null
            $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
            if ($tar) {
                # Windows 自带 bsdtar 可处理源码包内的长路径；Windows PowerShell 5.1
                # 的 Expand-Archive 在本项目较长的中文路径下可能失败。
                & $tar.Source -xf $archive -C $staging
                if ($LASTEXITCODE -ne 0) { throw "源码解压失败，tar 退出码 $LASTEXITCODE" }
            }
            else {
                Expand-Archive -LiteralPath $archive -DestinationPath $staging
            }
            $extracted = Join-Path $staging "$Repository-4.10.0"
            foreach ($required in $requiredFiles) {
                if (-not (Test-Path -LiteralPath (Join-Path $extracted $required) -PathType Leaf)) {
                    throw "源码包未包含预期文件 $required ：$archive"
                }
            }
            Move-Item -LiteralPath $extracted -Destination $directory
        }
        finally {
            # staging 是本次在固定 cache 下创建的解压目录，不删除已有源码或用户目录。
            if (Test-Path -LiteralPath $staging) {
                $resolved = (Resolve-Path -LiteralPath $staging).Path
                $cacheRoot = (Resolve-Path -LiteralPath $cache).Path
                if (-not $resolved.StartsWith($cacheRoot + [IO.Path]::DirectorySeparatorChar,
                        [StringComparison]::OrdinalIgnoreCase)) { throw '暂存路径超出构建缓存目录' }
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
    foreach ($required in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $required) -PathType Leaf)) {
            throw "已有源码目录不完整，缺少 $required ：$directory；请核对后删除该源码目录并重试。"
        }
    }
    return $directory
}
$opencv = Get-Source 'opencv' '3810BCA2B1D1C572912DF0AC3888126341F3762DFD28E91068C805FB656D0E51'
$contrib = Get-Source 'opencv_contrib' '15B1DECFA4D2EAF3B39148EFF4F311739FDB32F0DCCE58E7EDB3E95A560CCBD3'

if ($PrepareOnly) {
    Write-Host "源码已准备并校验完成：$cache；本次不编译、不更新运行 DLL。"
    return
}

# 算法与原 OpenCvSharp 同为 OpenCV 4.10.0，并保留 IPP 优化。
# 不关闭 IPP：插值/卷积的浮点差异可能影响模糊二维码的临界识别与几何坐标。
Invoke-CMake @('-S', $opencv, '-B', $opencvBuild, '-A', 'x64',
    '-DCMAKE_POLICY_VERSION_MINIMUM=3.5', '-DCMAKE_CXX_FLAGS=/utf-8', '-DCMAKE_C_FLAGS=/utf-8',
    "-DOPENCV_EXTRA_MODULES_PATH=$contrib/modules", '-DBUILD_LIST=core,imgproc,imgcodecs,objdetect,dnn,wechat_qrcode,features2d,calib3d,ximgproc',
    '-DBUILD_SHARED_LIBS=OFF', '-DBUILD_WITH_STATIC_CRT=OFF', '-DBUILD_TESTS=OFF', '-DBUILD_PERF_TESTS=OFF',
    '-DBUILD_EXAMPLES=OFF', '-DBUILD_opencv_apps=OFF', '-DBUILD_JAVA=OFF', '-DENABLE_PRECOMPILED_HEADERS=OFF',
    '-DWITH_EIGEN=OFF', '-DBUILD_opencv_python2=OFF', '-DBUILD_opencv_python3=OFF',
    '-DWITH_IPP=ON', '-DWITH_OPENCL=OFF', '-DWITH_ITT=OFF', '-DWITH_FFMPEG=OFF', '-DWITH_MSMF=OFF', '-DOPENCV_DNN_OPENCL=OFF')
Invoke-CMake @('--build', $opencvBuild, '--config', $Configuration, '--parallel', "$Parallelism", '--', '/p:UseStructuredOutput=false')
Invoke-CMake @('-S', (Join-Path $root 'Native'), '-B', $nativeBuild, '-A', 'x64', "-DOpenCV_DIR=$opencvBuild",
    '-DQR_READER_ONLY=OFF', '-DQR_BUILD_EXAMPLES=OFF', '-DQR_BUILD_TESTS=OFF')
Invoke-CMake @('--build', $nativeBuild, '--config', $Configuration, '--parallel', "$Parallelism", '--', '/p:UseStructuredOutput=false')
Write-Host "已生成：$nativeBuild\$Configuration\CISVisionCore.dll"
