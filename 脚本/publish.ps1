param(
    [string]$Mode = "",
    [string]$Version = "",
    [string]$OutputRoot = "",
    [switch]$Zip,
    [switch]$OpenOutput,
    [switch]$NoPause,
    [switch]$KeepSymbols,
    [switch]$SkipClean
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$script:ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:RepoRoot = Split-Path -Parent $script:ScriptDir
$script:ProjectPath = Join-Path $script:RepoRoot "ClearFrost\ClearFrost.csproj"
$script:DefaultOutputRoot = Join-Path $script:RepoRoot "PublishOutput"

function Write-Header {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor DarkCyan
    Write-Host "  ClearFrost Publisher" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor DarkCyan
}

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "[*] $Text" -ForegroundColor Cyan
}

function Write-Ok([string]$Text) {
    Write-Host "[OK] $Text" -ForegroundColor Green
}

function Write-Warn([string]$Text) {
    Write-Host "[WARN] $Text" -ForegroundColor Yellow
}

function Write-Fail([string]$Text) {
    Write-Host "[ERROR] $Text" -ForegroundColor Red
}

function Pause-IfNeeded {
    if (-not $NoPause) {
        Write-Host ""
        Read-Host "Press Enter to exit" | Out-Null
    }
}

function Get-DefaultVersion {
    if (-not (Test-Path -LiteralPath $script:ProjectPath)) {
        return "5.7.0"
    }

    $content = Get-Content -LiteralPath $script:ProjectPath -Raw -Encoding UTF8
    if ($content -match "<Version>(?<version>[^<]+)</Version>") {
        return $Matches.version.Trim()
    }

    return "5.7.0"
}

function Get-AssemblyName {
    $content = Get-Content -LiteralPath $script:ProjectPath -Raw -Encoding UTF8
    if ($content -match "<AssemblyName>(?<name>[^<]+)</AssemblyName>") {
        return $Matches.name.Trim()
    }

    return [System.IO.Path]::GetFileNameWithoutExtension($script:ProjectPath)
}

function Convert-VersionSpec([string]$Spec) {
    if ([string]::IsNullOrWhiteSpace($Spec)) {
        throw "Version is required."
    }

    $normalized = $Spec.Trim()
    if ($normalized.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    if ($normalized -notmatch "^\d+(\.\d+){1,3}$") {
        throw "Invalid version '$Spec'. Use 5.7, 5.7.0, or 5.7.0.1."
    }

    $parts = @()
    foreach ($part in $normalized.Split(".")) {
        $number = 0
        if (-not [int]::TryParse($part, [ref]$number) -or $number -lt 0) {
            throw "Invalid version '$Spec'."
        }

        $parts += $number
    }

    while ($parts.Count -lt 3) {
        $parts += 0
    }

    $packageVersion = "{0}.{1}.{2}" -f $parts[0], $parts[1], $parts[2]

    while ($parts.Count -lt 4) {
        $parts += 0
    }

    $assemblyVersion = "{0}.{1}.{2}.{3}" -f $parts[0], $parts[1], $parts[2], $parts[3]

    [pscustomobject]@{
        PackageVersion = $packageVersion
        AssemblyVersion = $assemblyVersion
        DisplayVersion = "V$packageVersion"
    }
}

function Read-PublishMode {
    Write-Host ""
    Write-Host "Select package mode:" -ForegroundColor White
    Write-Host "  1. Lite  - framework-dependent, smaller package"
    Write-Host "  2. Full  - self-contained, includes .NET runtime"
    Write-Host "  3. Both  - build both packages"
    Write-Host ""

    $choice = Read-Host "Mode [1]"
    if ([string]::IsNullOrWhiteSpace($choice)) {
        return "Lite"
    }

    switch ($choice.Trim().ToLowerInvariant()) {
        "1" { return "Lite" }
        "lite" { return "Lite" }
        "2" { return "Full" }
        "full" { return "Full" }
        "3" { return "Both" }
        "both" { return "Both" }
        default { throw "Unknown publish mode '$choice'." }
    }
}

function Normalize-Mode([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return Read-PublishMode
    }

    switch ($Value.Trim().ToLowerInvariant()) {
        "1" { return "Lite" }
        "lite" { return "Lite" }
        "2" { return "Full" }
        "full" { return "Full" }
        "3" { return "Both" }
        "both" { return "Both" }
        default { throw "Unknown publish mode '$Value'." }
    }
}

function Read-VersionOrDefault([string]$DefaultVersion) {
    Write-Host ""
    $inputVersion = Read-Host "Version [$DefaultVersion]"
    if ([string]::IsNullOrWhiteSpace($inputVersion)) {
        return $DefaultVersion
    }

    return $inputVersion.Trim()
}

function Read-ZipChoice {
    Write-Host ""
    $choice = Read-Host "Create zip archive? [Y/n]"
    if ([string]::IsNullOrWhiteSpace($choice)) {
        return $true
    }

    return -not $choice.Trim().StartsWith("n", [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ModesToPublish([string]$PublishMode) {
    if ($PublishMode -eq "Both") {
        return @("Lite", "Full")
    }

    return @($PublishMode)
}

function Assert-OutputPath([string]$RootPath, [string]$TargetPath) {
    $fullRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd("\", "/")
    $fullTarget = [System.IO.Path]::GetFullPath($TargetPath)

    if (-not $fullTarget.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean output outside root: $fullTarget"
    }
}

function Remove-ExistingOutput([string]$RootPath, [string]$TargetPath) {
    if ($SkipClean -or -not (Test-Path -LiteralPath $TargetPath)) {
        return
    }

    Assert-OutputPath $RootPath $TargetPath
    Remove-Item -LiteralPath $TargetPath -Recurse -Force
}

function Remove-DebugFiles([string]$TargetPath) {
    if ($KeepSymbols) {
        Write-Warn "Keeping PDB/XML files because -KeepSymbols was specified."
        return
    }

    Get-ChildItem -LiteralPath $TargetPath -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $TargetPath -Filter "*.xml" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

function Write-GeneratedCheckEnv($PublishMode, $VersionInfo, [string]$TargetPath, [string]$AssemblyName) {
    $lines = @(
        "@echo off",
        "chcp 65001 >nul 2>&1",
        "setlocal EnableExtensions",
        "set `"FAILED=0`"",
        "set `"APP_EXE=%~dp0$AssemblyName.exe`"",
        "",
        "echo ========================================",
        "echo ClearFrost Environment Check",
        "echo ========================================",
        "echo Mode: $PublishMode",
        "echo Version: $($VersionInfo.PackageVersion)",
        "echo.",
        "",
        "if exist `"%APP_EXE%`" (",
        "    echo [OK] Application exe found.",
        ") else (",
        "    echo [ERROR] Application exe missing: %APP_EXE%",
        "    set `"FAILED=1`"",
        ")",
        "",
        "if exist `"%~dp0html\index.html`" (",
        "    echo [OK] HTML assets found.",
        ") else (",
        "    echo [ERROR] HTML assets missing.",
        "    set `"FAILED=1`"",
        ")",
        ""
    )

    if ($PublishMode -eq "Lite") {
        $lines += @(
            "where dotnet >nul 2>&1",
            "if errorlevel 1 (",
            "    echo [ERROR] dotnet command not found. Install .NET 8 Desktop Runtime x64.",
            "    set `"FAILED=1`"",
            ") else (",
            "    echo [OK] dotnet command found.",
            "    dotnet --list-runtimes | findstr /C:`"Microsoft.WindowsDesktop.App 8.`" >nul",
            "    if errorlevel 1 (",
            "        echo [ERROR] .NET 8 Desktop Runtime was not found.",
            "        set `"FAILED=1`"",
            "    ) else (",
            "        echo [OK] .NET 8 Desktop Runtime found.",
            "    )",
            ")",
            ""
        )
    }
    else {
        $lines += @(
            "echo [OK] Full package includes .NET runtime.",
            ""
        )
    }

    $lines += @(
        "set `"WEBVIEW2_FOUND=0`"",
        "reg query `"HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`" /v pv >nul 2>&1 && set `"WEBVIEW2_FOUND=1`"",
        "reg query `"HKCU\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`" /v pv >nul 2>&1 && set `"WEBVIEW2_FOUND=1`"",
        "if `"%WEBVIEW2_FOUND%`"==`"1`" (",
        "    echo [OK] WebView2 Runtime registry entry found.",
        ") else (",
        "    echo [WARN] WebView2 Runtime registry entry was not found. Install Microsoft Edge WebView2 Runtime if the app cannot start.",
        ")",
        "",
        "echo.",
        "if `"%FAILED%`"==`"1`" (",
        "    echo Environment check failed.",
        "    pause",
        "    exit /b 1",
        ")",
        "",
        "echo Environment check passed.",
        "pause",
        "exit /b 0"
    )

    Set-Content -LiteralPath (Join-Path $TargetPath "check_env.bat") -Value $lines -Encoding UTF8
}

function Write-VersionManifest($PublishMode, $VersionInfo, [string]$TargetPath, [string]$ExePath) {
    $fileVersion = ""
    $productVersion = ""
    if (Test-Path -LiteralPath $ExePath) {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
        $fileVersion = $info.FileVersion
        $productVersion = $info.ProductVersion
    }

    $manifest = @(
        "ClearFrost Release",
        "Mode: $PublishMode",
        "PackageVersion: $($VersionInfo.PackageVersion)",
        "AssemblyVersion: $($VersionInfo.AssemblyVersion)",
        "ProductVersion: $productVersion",
        "FileVersion: $fileVersion",
        "RuntimeIdentifier: win-x64",
        "Configuration: Release",
        "PublishedAt: $((Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))",
        "Output: $TargetPath"
    )

    Set-Content -LiteralPath (Join-Path $TargetPath "VERSION.txt") -Value $manifest -Encoding UTF8
}

function Reset-OnnxOutputDirectory([string]$TargetPath) {
    $packagedModels = @(Get-ChildItem -LiteralPath $TargetPath -Filter "*.onnx" -File -Recurse -ErrorAction SilentlyContinue)
    foreach ($model in $packagedModels) {
        Remove-Item -LiteralPath $model.FullName -Force
    }

    $onnxPath = Join-Path $TargetPath "ONNX"
    if (Test-Path -LiteralPath $onnxPath) {
        Remove-Item -LiteralPath $onnxPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $onnxPath | Out-Null
    Write-Ok "ONNX directory created empty; model files are excluded from the package."
}

function Add-EmptyDirectoryEntryToZip([string]$ZipPath, [string]$DirectoryEntry) {
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    $entryName = $DirectoryEntry.Replace("\", "/").Trim("/")
    if ([string]::IsNullOrWhiteSpace($entryName)) {
        return
    }

    $entryName = "$entryName/"
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $exists = $false
        foreach ($entry in $archive.Entries) {
            if ([string]::Equals($entry.FullName, $entryName, [System.StringComparison]::OrdinalIgnoreCase)) {
                $exists = $true
                break
            }
        }

        if (-not $exists) {
            [void]$archive.CreateEntry($entryName)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Verify-PublishOutput($PublishMode, $VersionInfo, [string]$TargetPath, [string]$ExePath) {
    $errors = @()
    $warnings = @()

    if (-not (Test-Path -LiteralPath $ExePath)) {
        $errors += "Application exe is missing: $ExePath"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $TargetPath "html\index.html"))) {
        $errors += "html\index.html is missing."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $TargetPath "HslCommunication.dll"))) {
        $errors += "HslCommunication.dll is missing."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $TargetPath "McpXLib.dll"))) {
        $errors += "McpXLib.dll is missing."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $TargetPath "HaoCommunication.dll"))) {
        $warnings += "HaoCommunication.dll is missing. HaoCommunication PLC driver will be unavailable unless the DLL is copied next to the exe."
    }

    $deps = Get-ChildItem -LiteralPath $TargetPath -Filter "*.deps.json" -File -ErrorAction SilentlyContinue
    if ($PublishMode -eq "Lite" -and $deps.Count -eq 0) {
        $errors += ".deps.json is missing. Lite package cannot resolve NuGet dependencies without it."
    }

    $onnxPath = Join-Path $TargetPath "ONNX"
    if (-not (Test-Path -LiteralPath $onnxPath -PathType Container)) {
        $errors += "ONNX directory is missing. The package must include an empty ONNX folder."
    }
    else {
        $onnxContents = @(Get-ChildItem -LiteralPath $onnxPath -Force -ErrorAction SilentlyContinue)
        if ($onnxContents.Count -gt 0) {
            $errors += "ONNX directory must be empty in release packages."
        }
    }

    $onnxFiles = @(Get-ChildItem -LiteralPath $TargetPath -Filter "*.onnx" -File -Recurse -ErrorAction SilentlyContinue)
    if ($onnxFiles.Count -gt 0) {
        $errors += "ONNX model files must not be packaged: $($onnxFiles.FullName -join '; ')"
    }

    if ($PublishMode -eq "Full" -and -not (Test-Path -LiteralPath (Join-Path $TargetPath "MVSDKmd.dll"))) {
        $warnings += "MVSDKmd.dll is missing."
    }

    if (Test-Path -LiteralPath $ExePath) {
        $fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
        if ($fileInfo.ProductVersion -notlike "$($VersionInfo.PackageVersion)*") {
            $warnings += "Exe ProductVersion is '$($fileInfo.ProductVersion)', expected '$($VersionInfo.PackageVersion)'."
        }
    }

    foreach ($warning in $warnings) {
        Write-Warn $warning
    }

    if ($errors.Count -gt 0) {
        foreach ($errorMessage in $errors) {
            Write-Fail $errorMessage
        }

        throw "Publish verification failed."
    }

    Write-Ok "Publish output verified."
}

function Invoke-PublishPackage($PublishMode, $VersionInfo, [string]$ResolvedOutputRoot, [bool]$CreateZip) {
    $isFull = $PublishMode -eq "Full"
    $selfContained = "false"
    if ($isFull) {
        $selfContained = "true"
    }

    $targetName = "ClearFrost_{0}_{1}" -f $VersionInfo.PackageVersion, $PublishMode
    $targetPath = Join-Path $ResolvedOutputRoot $targetName
    $assemblyName = Get-AssemblyName
    $exePath = Join-Path $targetPath ($assemblyName + ".exe")

    Write-Step "Preparing $PublishMode package"
    New-Item -ItemType Directory -Force -Path $ResolvedOutputRoot | Out-Null
    Remove-ExistingOutput $ResolvedOutputRoot $targetPath
    New-Item -ItemType Directory -Force -Path $targetPath | Out-Null

    Write-Step "Running dotnet publish"
    $publishArgs = @(
        "publish",
        $script:ProjectPath,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", $selfContained,
        "-o", $targetPath,
        "-p:Platform=x64",
        "-p:Version=$($VersionInfo.PackageVersion)",
        "-p:PackageVersion=$($VersionInfo.PackageVersion)",
        "-p:AssemblyVersion=$($VersionInfo.AssemblyVersion)",
        "-p:FileVersion=$($VersionInfo.AssemblyVersion)",
        "-p:InformationalVersion=$($VersionInfo.PackageVersion)",
        "-p:IncludeSourceRevisionInInformationalVersion=false",
        "-p:PublishSingleFile=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:RestoreIgnoreFailedSources=true",
        "-p:NuGetAudit=false"
    )

    $dotnetOutput = & dotnet @publishArgs 2>&1
    $publishExitCode = $LASTEXITCODE
    foreach ($line in $dotnetOutput) {
        if ($line -match "\berror\b") {
            Write-Host $line -ForegroundColor Red
        }
        elseif ($line -match "\bwarning\b") {
            Write-Host $line -ForegroundColor Yellow
        }
        else {
            Write-Host $line
        }
    }

    if ($publishExitCode -ne 0) {
        throw "dotnet publish failed with exit code $publishExitCode."
    }

    Write-Step "Post-processing output"
    Remove-DebugFiles $targetPath
    Reset-OnnxOutputDirectory $targetPath

    $checkEnvPath = Join-Path $script:RepoRoot "check_env.bat"
    if (Test-Path -LiteralPath $checkEnvPath) {
        Copy-Item -LiteralPath $checkEnvPath -Destination $targetPath -Force
        Write-Ok "check_env.bat copied."
    }
    else {
        Write-GeneratedCheckEnv $PublishMode $VersionInfo $targetPath $assemblyName
        Write-Ok "check_env.bat generated."
    }

    Write-VersionManifest $PublishMode $VersionInfo $targetPath $exePath
    Verify-PublishOutput $PublishMode $VersionInfo $targetPath $exePath

    $zipPath = ""
    if ($CreateZip) {
        Write-Step "Creating zip archive"
        $zipPath = "$targetPath.zip"
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }

        Compress-Archive -Path (Join-Path $targetPath "*") -DestinationPath $zipPath -CompressionLevel Optimal
        Add-EmptyDirectoryEntryToZip $zipPath "ONNX"
        Write-Ok "Zip created: $zipPath"
    }

    [pscustomobject]@{
        Mode = $PublishMode
        Output = $targetPath
        Zip = $zipPath
    }
}

try {
    Write-Header
    Set-Location -LiteralPath $script:RepoRoot

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet CLI was not found in PATH."
    }

    if (-not (Test-Path -LiteralPath $script:ProjectPath)) {
        throw "Project file not found: $script:ProjectPath"
    }

    $prompted = $false
    if ([string]::IsNullOrWhiteSpace($Mode)) {
        $prompted = $true
    }

    $publishMode = Normalize-Mode $Mode
    $defaultVersion = Get-DefaultVersion
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $prompted = $true
        $Version = Read-VersionOrDefault $defaultVersion
    }

    $versionInfo = Convert-VersionSpec $Version

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = $script:DefaultOutputRoot
    }

    $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    $createZip = $Zip.IsPresent
    if (-not $createZip -and $prompted) {
        $createZip = Read-ZipChoice
    }

    Write-Host ""
    Write-Host "Publish plan" -ForegroundColor White
    Write-Host "  Mode:       $publishMode"
    Write-Host "  Version:    $($versionInfo.PackageVersion)"
    Write-Host "  Assembly:   $($versionInfo.AssemblyVersion)"
    Write-Host "  OutputRoot: $resolvedOutputRoot"
    Write-Host "  Zip:        $createZip"

    $results = @()
    foreach ($modeToPublish in (Get-ModesToPublish $publishMode)) {
        $results += Invoke-PublishPackage $modeToPublish $versionInfo $resolvedOutputRoot $createZip
    }

    Write-Host ""
    Write-Host "Done" -ForegroundColor Green
    foreach ($result in $results) {
        Write-Host "  [$($result.Mode)] $($result.Output)"
        if (-not [string]::IsNullOrWhiteSpace($result.Zip)) {
            Write-Host "        $($result.Zip)"
        }
    }

    if ($OpenOutput) {
        Invoke-Item -LiteralPath $resolvedOutputRoot
    }

    Pause-IfNeeded
    exit 0
}
catch {
    Write-Host ""
    Write-Fail $_.Exception.Message
    Pause-IfNeeded
    exit 1
}
