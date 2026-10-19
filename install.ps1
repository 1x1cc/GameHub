# =====================================================================
#  GameHub 覆盖安装 / 更新脚本
# ---------------------------------------------------------------------
#  作用：把新版 GameHub 覆盖安装到固定目录；已安装旧版的电脑再次运行
#        本脚本时，会先关闭正在运行的旧进程、再覆盖文件，不会因为
#        “文件被占用”而报错；同时保留用户设置、清理缓存、修正自启动路径。
#
#  用法：
#      右键 install.ps1 -> 使用 PowerShell 运行
#      或双击「安装 GameHub.bat」
#      或命令行： powershell -ExecutionPolicy Bypass -File install.ps1
#
#  参数：
#      -SourceDir  <路径>   新版文件所在目录（缺省自动探测 publish / bin\Release）
#      -InstallDir <路径>   安装目录，缺省 %LOCALAPPDATA%\Programs\GameHub
#      -KeepLogs            保留日志与崩溃报告（缺省清理）
#      -NoShortcut          不创建开始菜单快捷方式
# =====================================================================

param(
    [string]$SourceDir = "",
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\GameHub",
    [switch]$KeepLogs,
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"

function Fail([string]$Msg) {
    Write-Host ""
    Write-Host "[失败] $Msg" -ForegroundColor Red
    Write-Host ""
    Read-Host "按回车键退出"
    exit 1
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "   GameHub 覆盖安装 / 更新" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host ""

# ---------- 1. 定位新版文件目录 ----------
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $candidates = @(
        (Join-Path $PSScriptRoot "publish\win-x64"),
        (Join-Path $PSScriptRoot "bin\Release\net8.0-windows10.0.19041.0\win-x64"),
        (Join-Path $PSScriptRoot "bin\Release\net8.0-windows10.0.19041.0"),
        (Join-Path $PSScriptRoot "bin\Debug\net8.0-windows10.0.19041.0")
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "GameHub.exe")) { $SourceDir = $c; break }
    }
}

if ([string]::IsNullOrWhiteSpace($SourceDir) -or -not (Test-Path (Join-Path $SourceDir "GameHub.exe"))) {
    Fail "找不到新版本的 GameHub.exe。请先执行 dotnet publish 生成新版，或用 -SourceDir 指定目录。"
}

Write-Host "[1/5] 新版目录：$SourceDir" -ForegroundColor Gray
Write-Host "      安装目录：$InstallDir" -ForegroundColor Gray
Write-Host ""

# ---------- 2. 关闭正在运行的 GameHub ----------
# 关键：旧进程会锁住 GameHub.exe/dll，直接覆盖必然报“文件被占用”。
Write-Host "[2/5] 关闭正在运行的 GameHub..." -ForegroundColor Yellow
Get-Process -Name "GameHub" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Get-Process -Name "GameHub" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

# ---------- 3. 覆盖拷贝 ----------
Write-Host "[3/5] 覆盖安装文件..." -ForegroundColor Yellow
try {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force
}
catch {
    Fail "覆盖文件失败：$($_.Exception.Message)"
}

$installedExe = Join-Path $InstallDir "GameHub.exe"
if (-not (Test-Path $installedExe)) {
    Fail "安装后找不到 $installedExe"
}

# ---------- 4. 清理缓存（不碰用户设置）----------
if (-not $KeepLogs) {
    Write-Host "[4/5] 清理缓存（日志 / 旧崩溃报告）..." -ForegroundColor Yellow
    Remove-Item "$env:TEMP\gamehub.log" -Force -ErrorAction SilentlyContinue
    Remove-Item "$env:TEMP\gamehub-res.log" -Force -ErrorAction SilentlyContinue
    $reports = Join-Path $env:APPDATA "GameHub\reports"
    if (Test-Path $reports) {
        Remove-Item (Join-Path $reports "*.txt") -Force -ErrorAction SilentlyContinue
    }
    Write-Host "      已清理；用户设置 settings.json 已保留。" -ForegroundColor Gray
}
else {
    Write-Host "[4/5] 跳过清理（-KeepLogs）" -ForegroundColor Gray
}

# ---------- 5. 自启动路径修正 + 开始菜单快捷方式 ----------
Write-Host "[5/5] 修正自启动 / 创建快捷方式..." -ForegroundColor Yellow
$runKey = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$cmd = "`"$installedExe`" --autostart"
$existing = Get-ItemProperty -Path $runKey -Name "GameHub" -ErrorAction SilentlyContinue
if ($existing) {
    Set-ItemProperty -Path $runKey -Name "GameHub" -Value $cmd
    Write-Host "      已开启开机自启动，并指向新路径" -ForegroundColor Gray
}
else {
    Write-Host "      当前未开启开机自启动（保持 off）" -ForegroundColor Gray
}

if (-not $NoShortcut) {
    try {
        $ws = New-Object -ComObject WScript.Shell
        $lnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\GameHub.lnk"
        $sc = $ws.CreateShortcut($lnk)
        $sc.TargetPath = $installedExe
        $sc.WorkingDirectory = $InstallDir
        $sc.IconLocation = "$installedExe,0"
        $sc.Description = "GameHub - 音频与游戏工具箱"
        $sc.Save()
        Write-Host "      开始菜单快捷方式已创建" -ForegroundColor Gray
    }
    catch {
        Write-Host "      快捷方式创建失败（不影响安装）：$($_.Exception.Message)" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "完成！GameHub 已覆盖安装到：" -ForegroundColor Green
Write-Host "  $installedExe" -ForegroundColor Green
Write-Host ""
Write-Host "运行 $installedExe 即可使用新版。" -ForegroundColor Gray
Write-Host "你的设置已保留：%APPDATA%\GameHub\settings.json" -ForegroundColor Gray
Write-Host ""
Read-Host "按回车键退出"