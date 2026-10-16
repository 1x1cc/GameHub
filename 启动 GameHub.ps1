# GameHub 一键启动脚本
# 功能：自动编译 + 启动最新代码
# 用法：右键点击此文件 -> 用 PowerShell 运行，或双击（需要配置关联）

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$CsprojPath = "$ProjectDir\GameHub.csproj"
$ReleaseExe = "$ProjectDir\bin\Release\net8.0-windows10.0.19041.0\GameHub.exe"
$DebugExe = "$ProjectDir\bin\Debug\net8.0-windows10.0.19041.0\GameHub.exe"
$LogPath = "$env:TEMP\gamehub.log"

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  GameHub 一键启动脚本" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# 检查项目文件
if (-not (Test-Path $CsprojPath)) {
    Write-Host "错误：找不到项目文件 $CsprojPath" -ForegroundColor Red
    Write-Host "请确保在 GameHub 目录下运行此脚本" -ForegroundColor Yellow
    Pause
    exit 1
}

# 清理旧编译产物
Write-Host "[1/4] 清理旧编译产物..." -ForegroundColor Yellow
Remove-Item "$ProjectDir\bin\Release\net8.0-windows10.0.19041.0\GameHub.*" -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectDir\obj\Release" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectDir\bin\Debug\net8.0-windows10.0.19041.0\GameHub.*" -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectDir\obj\Debug" -Recurse -Force -ErrorAction SilentlyContinue

# 编译 Release 版本
Write-Host "[2/4] 编译 Release 版本..." -ForegroundColor Yellow
$buildResult = dotnet build $CsprojPath -c Release /consoleloggerparameters:NoSummary 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败！" -ForegroundColor Red
    Write-Host $buildResult | Select-Object -Last 20
    Pause
    exit 1
}
Write-Host "编译成功！" -ForegroundColor Green

# 检查 exe 是否存在
if (-not (Test-Path $ReleaseExe)) {
    Write-Host "错误：编译后找不到 exe 文件 $ReleaseExe" -ForegroundColor Red
    Pause
    exit 1
}

# 显示编译信息
$exeInfo = Get-Item $ReleaseExe
Write-Host "[3/4] 启动 GameHub..." -ForegroundColor Yellow
Write-Host "  exe: $($exeInfo.FullName)" -ForegroundColor Gray
Write-Host "  大小: $([math]::Round($exeInfo.Length / 1KB)) KB" -ForegroundColor Gray
Write-Host "  时间: $($exeInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray
Write-Host ""

# 关闭旧进程
Write-Host "关闭旧进程（如有）..." -ForegroundColor Gray
Stop-Process -Name GameHub -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 启动新进程
Write-Host "[4/4] 启动成功！" -ForegroundColor Green
Write-Host ""
Write-Host "游戏已启动，PID: $PID" -ForegroundColor Green
Write-Host ""
Write-Host "快捷键说明：" -ForegroundColor Cyan
Write-Host "  Ctrl+Alt+F11  -  循环切换输出设备" -ForegroundColor White
Write-Host "  Ctrl+Alt+F7   -  循环切换输入设备" -ForegroundColor White
Write-Host ""
Write-Host "日志文件：$LogPath" -ForegroundColor Gray
Write-Host "按任意键关闭 GameHub 并退出脚本..." -ForegroundColor Yellow
Read-Host

# 关闭 GameHub
Write-Host "正在关闭 GameHub..." -ForegroundColor Yellow
Stop-Process -Name GameHub -Force -ErrorAction SilentlyContinue
Write-Host "已关闭。" -ForegroundColor Green
Write-Host ""

# 显示最后 10 行日志
if (Test-Path $LogPath) {
    Write-Host "=== 最后 10 行日志 ===" -ForegroundColor Cyan
    Get-Content $LogPath -Tail 10
}
