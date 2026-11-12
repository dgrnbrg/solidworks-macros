@echo off
setlocal
cd /d "%~dp0"
title ExportFacesToDXF - SOLIDWORKS Macro Installer

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-Macro.ps1"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Installation exited with code %ERRORLEVEL%.
)
echo.
echo Press any key to exit...
pause >nul
