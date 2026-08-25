[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$ConfigPath,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

$canReadImageGeometry = $true
try {
    Add-Type -AssemblyName System.Drawing
}
catch {
    $canReadImageGeometry = $false
}

function Resolve-ExistingPath([string]$Path, [string]$DisplayName) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        throw "$DisplayName 不存在：$Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-RelativePath([string]$Root, [string]$Path) {
    $rootUri = New-Object System.Uri(($Root.TrimEnd('\') + '\'))
    $pathUri = New-Object System.Uri($Path)
    return [System.Uri]::UnescapeDataString(
        $rootUri.MakeRelativeUri($pathUri).ToString().Replace('/', '\'))
}

function Get-ImageGeometry([string]$Path) {
    $supported = @('.bmp', '.jpg', '.jpeg', '.png', '.tif', '.tiff')
    if ($supported -notcontains [System.IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        return $null
    }
    if (-not $canReadImageGeometry) {
        return [ordered]@{ width = $null; height = $null; readError = 'System.Drawing 不可用。' }
    }

    try {
        $image = [System.Drawing.Image]::FromFile($Path)
        try {
            return [ordered]@{ width = $image.Width; height = $image.Height }
        }
        finally {
            $image.Dispose()
        }
    }
    catch {
        return [ordered]@{ width = $null; height = $null; readError = $_.Exception.Message }
    }
}

function Normalize-LogLine([string]$Line) {
    # 只移除程序统一添加的日期时间前缀；保留 QR/Alignment/PartId 等业务标签。
    return ($Line -replace '^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]\s*', '').Trim()
}

$resolvedRunDirectory = Resolve-ExistingPath $RunDirectory '运行结果目录'
$fileRecords = @(Get-ChildItem -LiteralPath $resolvedRunDirectory -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $geometry = Get-ImageGeometry $_.FullName
        [ordered]@{
            relativePath = Get-RelativePath $resolvedRunDirectory $_.FullName
            lengthBytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            image = $geometry
        }
    })

$configRecord = $null
if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $resolvedConfigPath = Resolve-ExistingPath $ConfigPath '配置文件'
    $configRecord = [ordered]@{
        fileName = [System.IO.Path]::GetFileName($resolvedConfigPath)
        sha256 = (Get-FileHash -LiteralPath $resolvedConfigPath -Algorithm SHA256).Hash
        content = Get-Content -LiteralPath $resolvedConfigPath -Raw
    }
}

$resultLines = @()
if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $resolvedLogPath = Resolve-ExistingPath $LogPath '运行日志'
    $keywords = 'QR|二维码|Stitch|拼接|WhiteInk|白墨|Alignment|对准|Mark|内部缺陷|外部缺陷|细线断裂|检测完成|耗时|ms'
    $resultLines = Get-Content -LiteralPath $resolvedLogPath |
        Where-Object { $_ -match $keywords } |
        ForEach-Object { Normalize-LogLine $_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object
}

$manifest = [ordered]@{
    schemaVersion = 1
    generatedAt = (Get-Date).ToString('o')
    sourceDirectory = $resolvedRunDirectory
    config = $configRecord
    files = $fileRecords
    normalizedResultLines = $resultLines
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutputPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutputPath -Encoding UTF8
Write-Host "回归快照已生成：$resolvedOutputPath"
Write-Host "文件数：$($fileRecords.Count)，关键日志行：$($resultLines.Count)"
