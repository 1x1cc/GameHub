@echo off
rem ============================================================
rem  GameHub launcher
rem
rem  This file is intentionally ASCII-only.
rem  cmd.exe parses .bat files with the console code page; a
rem  UTF-8 file containing Chinese text gets split mid-character,
rem  so a line like  "echo [1/4] ..."  was turned into a bogus
rem  command and the launch failed with "is not recognized".
rem  All Chinese output lives in the companion .ps1 (UTF-8 + BOM),
rem  which PowerShell reads correctly.
rem
rem  The .ps1 is discovered at runtime instead of being written
rem  here, so no non-ASCII bytes appear in this file at all.
rem ============================================================
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ps1 = Get-ChildItem -LiteralPath '%~dp0' -Filter '*.ps1' -File | Select-Object -First 1; if (-not $ps1) { Write-Host '[ERROR] launcher .ps1 not found next to this .bat' -ForegroundColor Red; Read-Host 'Press Enter to exit'; exit 1 }; & $ps1.FullName"

endlocal
