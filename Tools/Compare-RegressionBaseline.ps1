[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedPath,

    [Parameter(Mandatory = $true)]
    [string]$ActualPath
)

$ErrorActionPreference = 'Stop'

function Read-Manifest([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "基线文件不存在：$Path"
    }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Index-Files($Files) {
    $index = @{}
    foreach ($file in $Files) {
        $index[$file.relativePath] = $file
    }
    return $index
}

$expected = Read-Manifest $ExpectedPath
$actual = Read-Manifest $ActualPath
$differences = New-Object System.Collections.Generic.List[string]

if ($expected.schemaVersion -ne $actual.schemaVersion) {
    $differences.Add("快照格式版本不同：$($expected.schemaVersion) != $($actual.schemaVersion)")
}

if ($expected.config -and $actual.config -and $expected.config.sha256 -ne $actual.config.sha256) {
    $differences.Add('app_config.json 内容不同，当前结果不能作为同条件算法回归。')
}

$expectedFiles = Index-Files $expected.files
$actualFiles = Index-Files $actual.files
$allPaths = @($expectedFiles.Keys + $actualFiles.Keys) | Sort-Object -Unique
foreach ($relativePath in $allPaths) {
    if (-not $expectedFiles.ContainsKey($relativePath)) {
        $differences.Add("新增输出：$relativePath")
        continue
    }
    if (-not $actualFiles.ContainsKey($relativePath)) {
        $differences.Add("缺少输出：$relativePath")
        continue
    }

    $left = $expectedFiles[$relativePath]
    $right = $actualFiles[$relativePath]
    if ($left.lengthBytes -ne $right.lengthBytes -or $left.sha256 -ne $right.sha256) {
        $differences.Add("内容变化：$relativePath")
    }
    if ($left.image -and $right.image -and
        ($left.image.width -ne $right.image.width -or $left.image.height -ne $right.image.height)) {
        $differences.Add("图像尺寸变化：$relativePath，$($left.image.width)x$($left.image.height) -> $($right.image.width)x$($right.image.height)")
    }
}

$expectedLines = @($expected.normalizedResultLines)
$actualLines = @($actual.normalizedResultLines)
$lineDiff = Compare-Object -ReferenceObject $expectedLines -DifferenceObject $actualLines
foreach ($item in $lineDiff) {
    $label = if ($item.SideIndicator -eq '<=') { '修改后缺少日志' } else { '修改后新增日志' }
    $differences.Add("$label：$($item.InputObject)")
}

if ($differences.Count -eq 0) {
    Write-Host '回归快照一致：未发现输出文件、图像尺寸、内容哈希或关键日志差异。' -ForegroundColor Green
    return
}

Write-Host "发现 $($differences.Count) 项差异：" -ForegroundColor Yellow
$differences | ForEach-Object { Write-Host " - $_" }
throw '回归快照不一致，请先确认差异是否属于预期变化。'
