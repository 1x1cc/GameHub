# =====================================================================
#  GameHub 发布打包脚本
# ---------------------------------------------------------------------
#  作用：把 GameHub 编译发布并打成可直接上传 GitHub Release 的 zip。
#
#  用法：
#      powershell -ExecutionPolicy Bypass -File 打包发布.ps1
#      powershell -ExecutionPolicy Bypass -File 打包发布.ps1 -Version 1.1.1
#      powershell -ExecutionPolicy Bypass -File 打包发布.ps1 -Version 1.1.1 -SelfContained
#      powershell -ExecutionPolicy Bypass -File 打包发布.ps1 -Version 1.1.1 -NoFfmpeg
#
#  参数：
#      -Version        版本号（默认 1.1.1，需与 csproj <Version> 保持一致）
#      -SelfContained  自包含发布（内置 .NET，免装运行时；体积大、需联网拉取运行时）
#      -NoFfmpeg       不打包 ffmpeg.exe（"精简版"；完整版默认带上 ffmpeg）
#      -NoPdb          不打包 .pdb 调试符号（建议始终开）
#
#  产出（项目根目录）：
#      框架依赖 + ffmpeg   -> GameHub-v<Version>-win-x64.zip
#      框架依赖 + 无 ffmpeg -> GameHub-v<Version>-win-x64-lite.zip
#      自包含   + ffmpeg   -> GameHub-v<Version>-win-x64-full.zip
# =====================================================================

param(
    [string]$Version = "1.1.1",
    [switch]$SelfContained,
    [switch]$NoFfmpeg,
    [switch]$NoPdb
)

$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
$Csproj     = Join-Path $ProjectDir "GameHub.csproj"
$OutDir     = Join-Path $ProjectDir "publish\GameHub-v$Version"

if (-not (Test-Path $Csproj)) {
    throw "找不到项目文件：$Csproj"
}

# ---------- [1/3] 发布 ----------
Write-Host ""
Write-Host "[1/3] 发布 GameHub v$Version ..." -ForegroundColor Yellow
$publishArgs = @("publish", $Csproj, "-c", "Release", "-o", $OutDir, "--nologo", "-v", "minimal")
if ($SelfContained) {
    $publishArgs += @("-r", "win-x64", "--self-contained", "true")
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

# ---------- [2/3] 清理 ----------
Write-Host "[2/3] 清理临时文件 ..." -ForegroundColor Yellow
# 调试符号：发布给用户用不上，去掉省体积
if (-not $NoPdb) {
    Get-ChildItem $OutDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
# 运行时生成的录像目录不进包
Remove-Item (Join-Path $OutDir "GameHub Recordings") -Recurse -Force -ErrorAction SilentlyContinue
# 精简版：去掉 ffmpeg
if ($NoFfmpeg) {
    Remove-Item (Join-Path $OutDir "ffmpeg.exe") -Force -ErrorAction SilentlyContinue
}

# ---------- [3/3] 打包 ----------
if ($SelfContained) {
    $suffix = "-full"
}
elseif ($NoFfmpeg) {
    $suffix = "-lite"
}
else {
    $suffix = ""
}
$ZipPath = Join-Path $ProjectDir "GameHub-v$Version-win-x64$suffix.zip"

Write-Host "[3/3] 打包 $ZipPath ..." -ForegroundColor Yellow
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}
Compress-Archive -Path (Join-Path $OutDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal

$size = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$ZipPath（$size MB）" -ForegroundColor Green
Write-Host "上传到 GitHub Release（tag v$Version）即可供他人下载。" -ForegroundColor Gray
Write-Host ""
