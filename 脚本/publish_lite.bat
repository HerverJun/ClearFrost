@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>&1
echo ========================================
echo   ClearFrost Lite Publish
echo   Framework-dependent Win-x64 package
echo ========================================
echo.

set "OUTPUT_DIR=ClearFrost_Lite"
set "PROJECT_PATH=ClearFrost\ClearFrost.csproj"
set "VERIFY_FAILED=0"

echo [1/6] Cleaning output directory...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

echo [2/6] Publishing project...
dotnet publish "%PROJECT_PATH%" -c Release -r win-x64 --self-contained false -o "%OUTPUT_DIR%" -p:Platform=x64 /p:PublishSingleFile=false /p:DebugType=None /p:DebugSymbols=false /p:RestoreIgnoreFailedSources=true /p:NuGetAudit=false
if errorlevel 1 (
    echo Publish failed.
    pause
    exit /b 1
)

echo [3/6] Preserving ONNX models...
if exist "%OUTPUT_DIR%\ONNX\*.onnx" (
    echo [OK] ONNX models preserved.
) else (
    echo [WARN] No ONNX models found in publish output.
)

echo [4/6] Removing debug symbols...
del /q "%OUTPUT_DIR%\*.pdb" 2>nul
del /q "%OUTPUT_DIR%\*.xml" 2>nul

echo [5/6] Verifying publish output...
if not exist "%OUTPUT_DIR%\html\index.html" (
    echo [ERROR] html\index.html is missing.
    set "VERIFY_FAILED=1"
) else (
    echo [OK] html assets found.
)

if not exist "%OUTPUT_DIR%\HslCommunication.dll" (
    echo [ERROR] HslCommunication.dll is missing.
    set "VERIFY_FAILED=1"
) else (
    echo [OK] HslCommunication.dll found.
)

if not exist "%OUTPUT_DIR%\McpXLib.dll" (
    echo [ERROR] McpXLib.dll is missing.
    set "VERIFY_FAILED=1"
) else (
    echo [OK] McpXLib.dll found.
)

if not exist "%OUTPUT_DIR%\*.deps.json" (
    echo [ERROR] .deps.json is missing. Lite publish cannot resolve NuGet dependencies without it.
    set "VERIFY_FAILED=1"
) else (
    echo [OK] .deps.json found.
)

copy /y "check_env.bat" "%OUTPUT_DIR%\" >nul 2>&1

if "%VERIFY_FAILED%"=="1" (
    echo Publish verification failed.
    pause
    exit /b 1
)

echo [6/6] Done.
echo Output: %OUTPUT_DIR%
echo.
pause
