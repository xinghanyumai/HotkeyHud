# 打包发布版（S8 定稿参数）
#
# 用法：右键「使用 PowerShell 运行」，或在仓库根目录：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1 -Compress
#
# 为什么默认**不压缩**：实测压缩会把常驻内存从 13 MB 顶到 52 MB
# （.NET 单文件压缩版在运行时把整个包解到内存里）。托盘常驻程序每天都要吃这 39 MB，
# 而 111 MB 的下载只付一次。要小体积就加 -Compress。
#
# 不裁剪（PublishTrimmed）：.NET SDK 明确拒绝 WinForms（NETSDK1175）

param(
    [switch]$Compress
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\HotkeyHud.App\HotkeyHud.App.csproj'
$output = Join-Path $root 'dist\HotkeyHud'

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

$compression = if ($Compress) { 'true' } else { 'false' }

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$compression `
    -o $output

if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

Get-ChildItem $output -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $output 'HotkeyHud.App.exe'
$item = Get-Item $exe
$hash = Get-FileHash $exe -Algorithm SHA256
$signature = (Get-AuthenticodeSignature $exe).Status

Write-Host ""
Write-Host ("打包完成：{0}（压缩={1}）" -f $output, $Compress)
Write-Host ("  体积    : {0:N0} 字节（{1:N1} MiB）" -f $item.Length, ($item.Length / 1MB))
Write-Host ("  版本    : {0}" -f $item.VersionInfo.FileVersion)
Write-Host ("  签名    : {0}" -f $signature)
Write-Host ("  SHA256  : {0}" -f $hash.Hash)
Write-Host ""
Write-Host "把 SHA256 抄进 README 第零节，用户下载后可自行核对。"
Write-Host "常驻内存：不压缩约 13 MB，压缩约 52 MB（实测）。"
