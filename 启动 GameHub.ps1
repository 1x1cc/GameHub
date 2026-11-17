# GameHub 一键启动脚本
# 功能：自动编译 + 启动最新代码
# 用法：双击「启动 GameHub.bat」，或右键本文件 -> 用 PowerShell 运行
#
# 步骤顺序很关键（旧版是「先清理/编译、后杀进程」，所以必然失败）：
#   1) 先结束正在运行的 GameHub —— 否则它锁住 bin\Release\GameHub.exe，
#      旧产物删不掉、新产物也覆盖不上，表现就是一句笼统的「编译失败」；
#   2) 清理旧产物；3) 编译；4) 启动；5) 退出时收尾。

$ProjectDir = $PSScriptRoot
$CsprojPath = "$ProjectDir\GameHub.csproj"
$ReleaseExe = "$ProjectDir\bin\Release\net8.0-windows10.0.19041.0\GameHub.exe"
$LogPath = "$env:TEMP\gamehub.log"

function Write-Step($text) { Write-Host $text -ForegroundColor Yellow }
function Write-Ok($text) { Write-Host $text -ForegroundColor Green }
function Write-Err($text) { Write-Host $text -ForegroundColor Red }

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  GameHub 一键启动脚本" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $CsprojPath)) {
    Write-Err "错误：找不到项目文件 $CsprojPath"
    Write-Host "请确保在 GameHub 目录下运行此脚本" -ForegroundColor Yellow
    Read-Host "按回车退出" | Out-Null
    exit 1
}

# ---------- [1/5] 先结束占用进程 ----------
Write-Step "[1/5] 结束正在运行的 GameHub..."
$running = @(Get-Process -Name GameHub -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    foreach ($proc in $running) {
        Write-Host ("  结束 PID {0}" -f $proc.Id) -ForegroundColor Gray
        try { $proc.Kill() } catch { Write-Host ("  结束失败：{0}" -f $_.Exception.Message) -ForegroundColor DarkYellow }
    }
    Start-Sleep -Milliseconds 1500
    Write-Ok "  已结束（不结束会锁住 exe，导致编译失败）"
} else {
    Write-Host "  没有正在运行的实例" -ForegroundColor Gray
}

# ---------- [2/5] 清理旧编译产物 ----------
Write-Step "[2/5] 清理旧编译产物..."
Remove-Item "$ProjectDir\bin\Release\net8.0-windows10.0.19041.0\GameHub.*" -Force -ErrorAction SilentlyContinue
Remove-Item "$ProjectDir\obj\Release" -Recurse -Force -ErrorAction SilentlyContinue
Write-Ok "  已清理"

# ---------- [3/5] 编译 ----------
Write-Step "[3/5] 编译 Release 版本..."
$buildOutput = & dotnet build $CsprojPath -c Release --nologo -v minimal 2>&1
$buildCode = $LASTEXITCODE
if ($buildCode -ne 0) {
    Write-Err "编译失败（退出码 $buildCode），最后 25 行输出："
    $buildOutput | Select-Object -Last 25 | ForEach-Object { Write-Host ("  {0}" -f $_) -ForegroundColor DarkGray }
    Read-Host "按回车退出" | Out-Null
    exit 1
}
Write-Ok "  编译成功"

if (-not (Test-Path $ReleaseExe)) {
    Write-Err "错误：编译后找不到 exe：$ReleaseExe"
    Read-Host "按回车退出" | Out-Null
    exit 1
}

# ---------- [4/5] 启动 ----------
$exeInfo = Get-Item $ReleaseExe
Write-Step "[4/5] 启动 GameHub..."
Write-Host ("  exe : {0}" -f $exeInfo.FullName) -ForegroundColor Gray
Write-Host ("  大小: {0} KB" -f [math]::Round($exeInfo.Length / 1KB)) -ForegroundColor Gray
Write-Host ("  时间: {0}" -f $exeInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')) -ForegroundColor Gray
Start-Process -FilePath $ReleaseExe
Write-Ok "  已启动"

Write-Host ""
Write-Host "快捷键说明：" -ForegroundColor Cyan
Write-Host "  Ctrl+Alt+F11  -  循环切换输出设备" -ForegroundColor White
Write-Host "  Ctrl+Alt+F7   -  循环切换输入设备" -ForegroundColor White
Write-Host ("日志文件：{0}" -f $LogPath) -ForegroundColor Gray
Write-Host ""

# ---------- [5/5] 等待并收尾 ----------
Write-Step "[5/5] 按回车关闭 GameHub 并退出脚本..."
Read-Host | Out-Null

foreach ($proc in @(Get-Process -Name GameHub -ErrorAction SilentlyContinue)) {
    try { $proc.Kill() } catch { }
}
Start-Sleep -Milliseconds 600
Write-Ok "已关闭"
Write-Host ""

if (Test-Path $LogPath) {
    Write-Host "=== 最后 10 行日志 ===" -ForegroundColor Cyan
    Get-Content $LogPath -Tail 10
} else {
    Write-Host "（暂无日志文件：$LogPath）" -ForegroundColor Gray
}
