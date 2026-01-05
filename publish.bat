@echo off
chcp 65001 >nul
echo ========================================
echo   清霜视觉 ClearFrost V1.0Pro - 轻量发布
echo   (不包含.NET运行时/WebView2)
echo ========================================
echo.

set OUTPUT_DIR=ClearFrost_V1.0Pro
set PROJECT_PATH=ClearFrost\ClearFrost.csproj

echo [1/6] 清理旧输出目录...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

echo [2/6] 编译项目 (Framework-Dependent, 仅必备文件)...
REM 使用 framework-dependent 发布，不包含.NET运行时
dotnet publish %PROJECT_PATH% -c Release -r win-x64 --self-contained false -o "%OUTPUT_DIR%" /p:PublishSingleFile=false /p:DebugType=None /p:DebugSymbols=false

if %ERRORLEVEL% neq 0 (
    echo 发布失败！
    pause
    exit /b 1
)

echo [3/6] 清理 ONNX 文件夹，只保留默认模型...
cd "%OUTPUT_DIR%\ONNX"
for %%f in (*.onnx) do (
    if /i not "%%f"=="yolo11n.onnx" del "%%f"
)
cd ..\..

echo [4/6] 清理不必要的文件...
REM 删除 pdb 调试文件
del /q "%OUTPUT_DIR%\*.pdb" 2>nul
REM 删除 xml 文档文件 (可选，节省空间)
del /q "%OUTPUT_DIR%\*.xml" 2>nul
REM 删除开发工具相关
del /q "%OUTPUT_DIR%\*.deps.json" 2>nul

echo [5/6] 验证 HTML 离线资源...
REM 确保 html 文件夹包含离线依赖
if not exist "%OUTPUT_DIR%\html\index.html" (
    echo [警告] html\index.html 缺失！
)
if not exist "%OUTPUT_DIR%\html\tailwind.min.js" (
    echo [警告] html\tailwind.min.js 缺失！
)
if not exist "%OUTPUT_DIR%\html\chart.min.js" (
    echo [警告] html\chart.min.js 缺失！
)

echo [6/6] 统计发布包信息...
echo.
echo ========================================
echo   发布完成！
echo ========================================
echo.
echo 输出目录: %OUTPUT_DIR%
echo.

REM 显示文件夹大小
echo --- 目录结构 ---
dir /s /b "%OUTPUT_DIR%\*.exe" 2>nul
echo.
echo --- ONNX 模型 ---
dir /b "%OUTPUT_DIR%\ONNX\*.onnx" 2>nul
echo.
echo --- HTML 离线资源 ---
dir /b "%OUTPUT_DIR%\html\*.*" 2>nul
echo.

echo ========================================
echo   目标机器运行要求:
echo   1. 安装 .NET 8.0 Desktop Runtime (x64)
echo   2. 安装 Microsoft Edge WebView2 Runtime
echo   3. 安装相机驱动 (MV Camera SDK)
echo ========================================
pause
