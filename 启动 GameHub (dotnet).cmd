@echo off
rem ============================================================
rem  启动 GameHub（绕过 Windows 11「智能应用控制」对未签名 exe 的拦截）
rem
rem  原理：本脚本由系统自带、受信任的 cmd.exe 启动，
rem        再调用微软签名的 dotnet.exe 运行 GameHub.dll，
rem        因此不会触发 Smart App Control 的「无法验证其发布者」拦截。
rem
rem  前提：已安装 .NET 8 桌面运行时；若未安装，请用完整版
rem        （GameHub-v1.0.5-win-x64-full.zip，自包含，无需装 .NET），
rem        或先安装：https://dotnet.microsoft.com/download/dotnet/8.0
rem ============================================================
setlocal
cd /d "%~dp0"

if not exist "%~dp0GameHub.dll" (
  echo.
  echo [ERROR] GameHub.dll not found in: %~dp0
  echo         Please extract the WHOLE archive to a folder first,
  echo         then run this script from that folder.
  echo         ^(Do not run directly from inside the .zip archive.^)
  echo.
  pause
  exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo [ERROR] .NET runtime not found on this PC.
  echo         Option A^) Install .NET 8 Desktop Runtime:
  echo                   https://dotnet.microsoft.com/download/dotnet/8.0
  echo         Option B^) Use the self-contained package instead:
  echo                   GameHub-v1.0.5-win-x64-full.zip ^(no .NET needed^)
  echo                    - but Smart App Control may block GameHub.exe;
  echo                      turn Smart App Control off in that case.
  echo.
  pause
  exit /b 1
)

start "" dotnet "%~dp0GameHub.dll"
exit /b 0
