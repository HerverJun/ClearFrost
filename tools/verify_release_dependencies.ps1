param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$OutputDir = "",
    [switch]$RequireModel
)

$ErrorActionPreference = "Stop"

$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) {
    $failures.Add($Message)
}

function Test-RequiredFile([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure "Missing ${Label}: $Path"
        return
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -le 0) {
        Add-Failure "Empty ${Label}: $Path"
    }
}

function Test-RequiredDirectory([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Add-Failure "Missing ${Label} directory: $Path"
        return
    }

    if (-not (Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        Add-Failure "Empty ${Label} directory: $Path"
    }
}

$projectPath = Join-Path $Root "ClearFrost\ClearFrost.csproj"
Test-RequiredFile $projectPath "main project"

if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
    $projectText = [System.IO.File]::ReadAllText($projectPath)
    if ($projectText -notmatch "<PlatformTarget>\s*x64\s*</PlatformTarget>") {
        Add-Failure "Project must keep PlatformTarget=x64: $projectPath"
    }
}

Test-RequiredFile (Join-Path $Root "ClearFrost\DLL\MVSDK_Net.dll") "Huaray managed SDK"
Test-RequiredFile (Join-Path $Root "x64依赖包\MVSDKmd.dll") "Huaray native SDK"
Test-RequiredFile (Join-Path $Root "海康依赖包\MvCameraControl.dll") "Hikvision native SDK"
Test-RequiredDirectory (Join-Path $Root "ClearFrost\html") "Web UI"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $debugOutput = Join-Path $Root "ClearFrost\bin\x64\Debug\net8.0-windows10.0.17763.0"
    $releaseOutput = Join-Path $Root "ClearFrost\bin\x64\Release\net8.0-windows10.0.17763.0"
    if (Test-Path -LiteralPath $releaseOutput -PathType Container) {
        $OutputDir = $releaseOutput
    }
    elseif (Test-Path -LiteralPath $debugOutput -PathType Container) {
        $OutputDir = $debugOutput
    }
}

if (-not [string]::IsNullOrWhiteSpace($OutputDir)) {
    $resolvedOutput = (Resolve-Path -LiteralPath $OutputDir -ErrorAction SilentlyContinue)
    if ($null -eq $resolvedOutput) {
        Add-Failure "Output directory does not exist: $OutputDir"
    }
    else {
        $outputPath = $resolvedOutput.Path
        Test-RequiredFile (Join-Path $outputPath "清霜视觉.exe") "application exe"
        Test-RequiredFile (Join-Path $outputPath "清霜视觉.dll") "application assembly"
        Test-RequiredFile (Join-Path $outputPath "MVSDK_Net.dll") "Huaray managed SDK output"
        Test-RequiredFile (Join-Path $outputPath "MVSDKmd.dll") "Huaray native SDK output"
        Test-RequiredFile (Join-Path $outputPath "MvCameraControl.dll") "Hikvision native SDK output"
        Test-RequiredFile (Join-Path $outputPath "DirectML.dll") "DirectML runtime"
        Test-RequiredFile (Join-Path $outputPath "onnxruntime.dll") "ONNX Runtime"
        Test-RequiredFile (Join-Path $outputPath "OpenCvSharp.dll") "OpenCvSharp runtime"
        Test-RequiredFile (Join-Path $outputPath "HslCommunication.dll") "PLC communication runtime"
        Test-RequiredFile (Join-Path $outputPath "Microsoft.Web.WebView2.Core.dll") "WebView2 runtime binding"
        Test-RequiredFile (Join-Path $outputPath "SQLitePCLRaw.provider.e_sqlite3.dll") "SQLite native provider"
        Test-RequiredFile (Join-Path $outputPath "html\index.html") "Web UI entry"
        Test-RequiredFile (Join-Path $outputPath "config.json") "runtime config"

        if ($RequireModel -and
            -not (Get-ChildItem -LiteralPath (Join-Path $outputPath "ONNX") -Filter "*.onnx" -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
            Add-Failure "No ONNX model found in output ONNX directory: $outputPath"
        }
    }
}
elseif ($RequireModel) {
    if (-not (Get-ChildItem -LiteralPath (Join-Path $Root "ClearFrost\ONNX") -Filter "*.onnx" -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        Add-Failure "No ONNX model found in source ONNX directory."
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}

Write-Host "Release dependency check passed."
