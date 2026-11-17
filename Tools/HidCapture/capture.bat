@echo off
rem ============================================================
rem  HID capture helper - reverse engineering 2.4G battery
rem  protocols. No arguments = interactive mode (defaults to
rem  EWEADN GS02 249A:5C2F). All capture is read-only.
rem ============================================================
setlocal
cd /d "%~dp0"
dotnet run -c Release -- %*
echo.
pause
