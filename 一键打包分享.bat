@echo off
cd /d "%~dp0"

:: 尝试使用标准路径启动 PowerShell 执行脚本
if exist "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" (
    "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -ExecutionPolicy Bypass -File "share_code.ps1"
) else (
    echo Error: Could not find PowerShell.exe at standard location.
    pause
)
