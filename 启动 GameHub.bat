@echo off
chcp 65001 >nul 2>&1
setlocal

:: GameHub 一键启动批处理
:: 功能：自动编译 + 启动最新代码

set "PROJECT_DIR=%~dp0"
set "CSPROJ=%PROJECT_DIR%GameHub.csproj"
set "RELEASE_EXE=%PROJECT_DIR%bin\Release\net8.0-windows10.0.19041.0\GameHub.exe"

echo.
echo ================================================================
echo   GameHub 一键启动脚本
echo ================================================================
echo.

:: 检查项目文件
if not exist "%CSPROJ%" (
    echo [错误] 找不到项目文件 %CSPROJ%
    echo 请确保在 GameHub 目录下运行此脚本
    pause
    exit /b 1
)

:: 清理旧编译产物
echo [1/4] 清理旧编译产物...
if exist "%PROJECT_DIR%bin\Release\net8.0-windows10.0.19041.0\GameHub.exe" (
    del /q "%PROJECT_DIR%bin\Release\net8.0-windows10.0.19041.0\GameHub.*" >nul 2>&1
)
if exist "%PROJECT_DIR%obj\Release" (
    rmdir /s /q "%PROJECT_DIR%obj\Release" >nul 2>&1
)

:: 编译 Release 版本
echo [2/4] 编译 Release 版本...
dotnet build "%CSPROJ%" -c Release /consoleloggerparameters:NoSummary >nul 2>&1
if errorlevel 1 (
    echo [错误] 编译失败！
    pause
    exit /b 1
)
echo 编译成功！

:: 检查 exe 是否存在
if not exist "%RELEASE_EXE%" (
    echo [错误] 编译后找不到 exe 文件 %RELEASE_EXE%
    pause
    exit /b 1
)

:: 显示信息
for %%I in ("%RELEASE_EXE%") do (
    set "EXE_SIZE=%%~zI"
    set "EXE_TIME=%%~tI"
)
set /a EXE_SIZE_KB=%EXE_SIZE% / 1024

echo [3/4] 启动 GameHub...
echo   exe: %RELEASE_EXE%
echo   大小: %EXE_SIZE_KB% KB
echo   时间: %EXE_TIME%
echo.

:: 关闭旧进程
echo 关闭旧进程（如有）...
taskkill /f /im GameHub.exe >nul 2>&1
timeout /t 1 /nobreak >nul

:: 启动新进程
echo [4/4] 启动成功！
echo.
echo 游戏已启动
echo.
echo 快捷键说明：
echo   Ctrl+Alt+F11  -  循环切换输出设备
echo   Ctrl+Alt+F7   -  循环切换输入设备
echo.
echo 日志文件：%TEMP%\gamehub.log
echo.

:: 等待用户按键关闭
echo 按任意键关闭 GameHub 并退出脚本...
pause >nul

:: 关闭 GameHub
echo 正在关闭 GameHub...
taskkill /f /im GameHub.exe >nul 2>&1
echo 已关闭。
echo.

:: 显示最后 10 行日志
if exist "%TEMP%\gamehub.log" (
    echo === 最后 10 行日志 ===
    powershell -Command "Get-Content '%TEMP%\gamehub.log' -Tail 10"
)

endlocal
