@echo off
chcp 65001 >nul 2>&1
echo ========================================
echo   清霜视觉 ClearFrost V1.0Pro - 全量独立版
echo   (内置 .NET运行时 - 无需环境即可运行)
echo ========================================
echo.

set OUTPUT_DIR=ClearFrost_Full
set PROJECT_PATH=ClearFrost\ClearFrost.csproj

echo [1/6] 清理旧输出目录...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

echo [2/6] 编译项目 (Self-Contained)...
REM --self-contained true: 内置运行时，体积大但通用
dotnet publish %PROJECT_PATH% -c Release -r win-x64 --self-contained true -o "%OUTPUT_DIR%" /p:PublishSingleFile=false /p:DebugType=None /p:DebugSymbols=false

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

echo [4/6] 基础清理...
del /q "%OUTPUT_DIR%\*.pdb" 2>nul
del /q "%OUTPUT_DIR%\*.xml" 2>nul

echo [5/6] 验证...
if not exist "%OUTPUT_DIR%\html\index.html" echo [警告] html资源缺失！
if not exist "%OUTPUT_DIR%\MVSDKmd.dll" (
    echo [警告] 相机SDK核心文件缺失 MVSDKmd.dll
) else (
    echo [OK] 相机SDK依赖已包含
)
copy /y "check_env.bat" "%OUTPUT_DIR%\" >nul 2>&1

echo [6/6] 完成！
echo.
echo 输出目录: %OUTPUT_DIR%
echo 此版本可直接在未安装环境的 Win10/11 x64 上运行
echo.
pause
