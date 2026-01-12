@echo off
chcp 65001 >nul 2>&1
echo ========================================
echo   清霜视觉 - 环境诊断工具
echo ========================================
echo.

echo [1/4] 检查 .NET Runtime...
dotnet --list-runtimes 2>nul | findstr "Microsoft.WindowsDesktop.App 8" >nul
if %ERRORLEVEL% equ 0 (
    echo [OK] .NET Desktop Runtime 8.x 已安装
) else (
    echo [错误] 未找到 .NET Desktop Runtime 8.x
    echo        请下载安装: https://dotnet.microsoft.com/download/dotnet/8.0
)
echo.

echo [2/4] 检查 WebView2 Runtime...
reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo [OK] Microsoft Edge WebView2 已安装
) else (
    reg query "HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv >nul 2>&1
    if %ERRORLEVEL% equ 0 (
        echo [OK] Microsoft Edge WebView2 已安装
    ) else (
        echo [错误] 未找到 WebView2 Runtime
        echo        请下载安装: https://developer.microsoft.com/microsoft-edge/webview2/
    )
)
echo.

echo [3/4] 检查关键文件...
set MISSING=0
if not exist "清霜视觉.exe" (
    echo [错误] 清霜视觉.exe 不存在
    set MISSING=1
)
if not exist "html\index.html" (
    echo [错误] html\index.html 不存在
    set MISSING=1
)
if not exist "MVSDKmd.dll" (
    echo [警告] MVSDKmd.dll 不存在 (相机功能可能受影响)
)
if %MISSING% equ 0 (
    echo [OK] 核心文件完整
)
echo.

echo [4/4] 检查启动日志...
if exist "startup.log" (
    echo [INFO] 发现启动日志，内容如下:
    echo ----------------------------------------
    type "startup.log"
    echo ----------------------------------------
) else (
    echo [INFO] 未发现启动日志 (程序可能尚未运行)
)
echo.

if exist "Logs" (
    echo [INFO] 发现崩溃日志目录，最近日志:
    dir /b /o-d "Logs\crash_*.log" 2>nul | findstr /n "^" | findstr "^1:"
)
echo.

echo ========================================
echo   诊断完成
echo ========================================
pause
