# 打包发布版（S8 定稿参数）
#
# 用法：右键「使用 PowerShell 运行」，或在仓库根目录：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1
#
# 关键参数说明：
#   -p:PublishSingleFile=true                    打成单文件
#   -p:EnableCompressionInSingleFile=true        单文件压缩（110.9 MiB → 49.4 MiB）
#   -p:IncludeNativeLibrariesForSelfExtract=true 原生库也塞进单文件，首次运行解压到 %TEMP%\.net
#   不裁剪（PublishTrimmed）：.NET SDK 明确拒绝 WinForms（NETSDK1175）

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\HotkeyHud.App\HotkeyHud.App.csproj'
$output = Join-Path $root 'dist\HotkeyHud'

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output

if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

Get-ChildItem $output -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $output 'HotkeyHud.App.exe'
$item = Get-Item $exe
$hash = Get-FileHash $exe -Algorithm SHA256
$signature = (Get-AuthenticodeSignature $exe).Status

Write-Host ""
Write-Host "打包完成：$output"
Write-Host ("  体积    : {0:N0} 字节（{1:N1} MiB）" -f $item.Length, ($item.Length / 1MB))
Write-Host ("  版本    : {0}" -f $item.VersionInfo.FileVersion)
Write-Host ("  签名    : {0}" -f $signature)
Write-Host ("  SHA256  : {0}" -f $hash.Hash)
Write-Host ""
Write-Host "把 SHA256 抄进 README 第零节，用户下载后可自行核对。"
