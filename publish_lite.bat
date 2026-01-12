@echo off
chcp 65001 >nul 2>&1
echo ========================================
echo   清霜视觉 ClearFrost - 轻量极速版
echo   (依赖本机 .NET环境 - 适合快速更新)
echo ========================================
echo.

set OUTPUT_DIR=ClearFrost_Lite
set PROJECT_PATH=ClearFrost\ClearFrost.csproj

echo [1/6] 清理旧输出目录...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

echo [2/6] 编译项目 (Framework-Dependent)...
REM --self-contained false: 不包含运行时，体积小
dotnet publish %PROJECT_PATH% -c Release -r win-x64 --self-contained false -o "%OUTPUT_DIR%" /p:PublishSingleFile=false /p:DebugType=None /p:DebugSymbols=false

if %ERRORLEVEL% neq 0 (
    echo 发布失败！
    pause
    exit /b 1
)

echo [3/6] 清理 ONNX 文件夹...
cd "%OUTPUT_DIR%\ONNX"
for %%f in (*.onnx) do (
    if /i not "%%f"=="yolo11n.onnx" del "%%f"
)
cd ..\..

echo [4/6] 深度清理...
del /q "%OUTPUT_DIR%\*.pdb" 2>nul
del /q "%OUTPUT_DIR%\*.xml" 2>nul
del /q "%OUTPUT_DIR%\*.deps.json" 2>nul

echo [5/6] 验证离线资源...
if not exist "%OUTPUT_DIR%\html\index.html" echo [警告] html资源缺失！
copy /y "check_env.bat" "%OUTPUT_DIR%\" >nul 2>&1

echo [6/6] 完成！
echo.
echo 输出目录: %OUTPUT_DIR%
echo 此版本需要目标机器安装 .NET Desktop Runtime 8.0
echo.
pause
