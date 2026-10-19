@echo off
setlocal
cd /d "%~dp0"

:: ========== GameHub 开发调试快捷启动 ==========
:: 用法: 双击运行, 或拖入 cmd.exe
:: ==============================================

echo [GameHub] 正在编译并启动...

dotnet run -c Debug -p GameHub.csproj --no-build 2>nul ^
    || dotnet build -c Debug -p GameHub.csproj /consoleloggerparameters:NoSummary;ForceNoAlign 2>nul ^
    && dotnet run -c Debug -p GameHub.csproj --no-restore --no-build

if errorlevel 1 (
    echo [GameHub] 编译失败，请检查错误信息。
    pause
    exit /b 1
)

echo [GameHub] 已关闭。
pause
