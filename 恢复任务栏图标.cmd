@echo off
chcp 65001 >nul
title 恢复任务栏与桌面图标
echo.
echo   GameHub 闪退后如果任务栏 / 桌面图标不见了，运行这个就能恢复。
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0恢复任务栏图标.ps1"
echo.
echo   （如果上面显示 restored 0 window(s)，说明不是被藏起来，可重启桌面解决。）
pause
