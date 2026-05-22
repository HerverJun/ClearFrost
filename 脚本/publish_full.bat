@echo off
chcp 65001 >nul 2>&1

if "%~1"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Mode Full
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Mode Full -Version "%~1" %2 %3 %4 %5 %6 %7 %8 %9
)

exit /b %ERRORLEVEL%
