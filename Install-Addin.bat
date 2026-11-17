@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0scripts\Install-Addin.ps1" -Configuration Release %*
if %errorlevel% neq 0 (
    echo.
    echo Installation failed!
    pause
    exit /b %errorlevel%
)
pause
