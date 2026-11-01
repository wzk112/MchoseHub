# MCHOSE 设备控制台 —— 一键构建脚本
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\MchoseHub.App\MchoseHub.App.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet SDK，请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download'
}

$args = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '-p:Platform=x64',
    '--nologo'
)

if ($Portable) {
    $args += '-p:WindowsAppSDKSelfContained=true'
    $args += '--self-contained=true'
    Write-Host '模式：绿色版（自带 Windows App SDK 运行时，体积较大）' -ForegroundColor Yellow
} else {
    Write-Host '模式：框架依赖（需要机器上装有 Windows App Runtime 1.8）' -ForegroundColor Cyan
}

& dotnet @args
if ($LASTEXITCODE -ne 0) {
    throw "构建失败，退出码 $LASTEXITCODE"
}

$publish = Join-Path $root "src\MchoseHub.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\publish"
$exe = Join-Path $publish 'MchoseHub.exe'

if (Test-Path $exe) {
    $size = [math]::Round(((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host ''
    Write-Host '构建成功' -ForegroundColor Green
    Write-Host "  可执行文件：$exe"
    Write-Host "  产物大小：  $size MB"
} else {
    throw "未找到构建产物：$exe"
}
