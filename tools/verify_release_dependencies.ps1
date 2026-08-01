param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$OutputDir = "",
    [ValidateSet("Lite", "Full")]
    [string]$Profile = "",
    [switch]$RequireModel,
    [switch]$CleanRoom,
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"

$rootPath = [System.IO.Path]::GetFullPath($Root)
$outputPath = if ([string]::IsNullOrWhiteSpace($OutputDir)) { "" } else { [System.IO.Path]::GetFullPath($OutputDir) }
$hasPackagedOutput = -not [string]::IsNullOrWhiteSpace($outputPath)
$blockingReasons = [System.Collections.Generic.List[string]]::new()
$notVerifiedReasons = [System.Collections.Generic.List[string]]::new()
$dependencyRecords = [System.Collections.Generic.List[object]]::new()

function Get-PropertyValue([object]$Object, [string]$Name) {
    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Add-BlockingReason([string]$Reason) {
    [void]$blockingReasons.Add($Reason)
}

function Add-NotVerifiedReason([string]$Reason) {
    [void]$notVerifiedReasons.Add($Reason)
}

function Read-JsonFile([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-BlockingReason "Missing release input ${Label}: $Path"
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Add-BlockingReason "Invalid JSON in ${Label}: $Path ($($_.Exception.Message))"
        return $null
    }
}

function Resolve-ExistingCandidate([string[]]$Candidates) {
    foreach ($candidate in @($Candidates)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            return $fullPath
        }
    }

    return ""
}

function Get-FileEvidence([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [ordered]@{
            exists = $false
            path = ""
            sha256 = ""
            bytes = 0
        }
    }

    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        exists = $true
        path = $Path
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
        bytes = $item.Length
    }
}

function Normalize-PlcProvider([string]$Value) {
    $normalizedValue = if ($null -eq $Value) { "" } else { $Value.Trim().ToLowerInvariant() }
    switch ($normalizedValue) {
        "hsl" { return "Hsl" }
        "mcpx" { return "McpX" }
        "haocommunication" { return "HaoCommunication" }
        default {
            if ($null -eq $Value) {
                return ""
            }

            return $Value.Trim()
        }
    }
}

function Get-ContextProvider([object]$Context) {
    $provider = [string](Get-PropertyValue $Context "PlcDriverProvider")
    if ([string]::IsNullOrWhiteSpace($provider)) {
        return "HaoCommunication"
    }

    return Normalize-PlcProvider $provider
}

function Get-ContextCameras([object]$Context) {
    $cameras = [System.Collections.Generic.List[string]]::new()
    $cameraArray = Get-PropertyValue $Context "Cameras"
    foreach ($camera in @($cameraArray)) {
        $enabled = Get-PropertyValue $camera "IsEnabled"
        if ($null -ne $enabled -and $enabled -is [bool] -and -not $enabled) {
            continue
        }

        foreach ($propertyName in @("Manufacturer", "CameraManufacturer", "Brand", "CameraBrand")) {
            $value = [string](Get-PropertyValue $camera $propertyName)
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                [void]$cameras.Add($value.Trim())
            }
        }
    }

    foreach ($propertyName in @("CameraManufacturer", "CameraBrand")) {
        $value = [string](Get-PropertyValue $Context $propertyName)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            [void]$cameras.Add($value.Trim())
        }
    }

    if ($cameras.Count -eq 0) {
        [void]$cameras.Add("Huaray")
    }

    return @($cameras | Sort-Object -Unique)
}

function Add-DependencyRecord(
    [string]$Kind,
    [string]$Name,
    [string]$FileName,
    [string[]]$SourceCandidates,
    [string]$PackagedRelativePath,
    [string[]]$RequiredBy,
    [bool]$ExternalInput,
    [bool]$Required = $true) {

    $sourcePath = Resolve-ExistingCandidate $SourceCandidates
    $packagedFile = if ($hasPackagedOutput) {
        Join-Path $outputPath $PackagedRelativePath
    }
    else {
        ""
    }

    $sourceEvidence = Get-FileEvidence $sourcePath
    $packagedEvidence = Get-FileEvidence $packagedFile
    $available = $sourceEvidence.exists -or $packagedEvidence.exists
    $status = "PASS"
    $reason = ""

    if (-not $Required) {
        $status = "NOT_REQUIRED"
    }
    elseif (-not $available) {
        if ($ExternalInput) {
            $status = "BLOCKED"
            $reason = "Required external dependency is unavailable."
        }
        elseif ($hasPackagedOutput) {
            $status = "BLOCKED"
            $reason = "Required packaged file is missing."
        }
        else {
            $status = "NOT_VERIFIED"
            $reason = "Packaged output was not supplied for verification."
        }
    }
    elseif ($hasPackagedOutput -and -not $packagedEvidence.exists) {
        $status = "BLOCKED"
        $reason = "Source dependency exists but was not copied to the package."
    }

    if ($status -eq "BLOCKED") {
        Add-BlockingReason "$Kind '$Name': $reason Required by: $($RequiredBy -join ', ')."
    }
    elseif ($status -eq "NOT_VERIFIED") {
        Add-NotVerifiedReason "$Kind '$Name': $reason"
    }

    [void]$dependencyRecords.Add([ordered]@{
        kind = $Kind
        name = $Name
        fileName = $FileName
        required = $Required
        requiredBy = @($RequiredBy)
        externalInput = $ExternalInput
        available = $sourceEvidence
        packaged = $packagedEvidence
        status = $status
        reason = $reason
    })
}

$configPath = Join-Path $rootPath "ClearFrost\config.json"
$presetPath = Join-Path $rootPath "ClearFrost\project-presets.json"
$config = Read-JsonFile $configPath "default config"
$presetRoot = Read-JsonFile $presetPath "project presets"

$contexts = [System.Collections.Generic.List[object]]::new()
if ($null -ne $config) {
    [void]$contexts.Add([pscustomobject]@{ name = "ClearFrost/config.json"; data = $config })
}
if ($null -ne $presetRoot) {
    foreach ($property in $presetRoot.PSObject.Properties) {
        [void]$contexts.Add([pscustomobject]@{
            name = "ClearFrost/project-presets.json::$($property.Name)"
            data = $property.Value
        })
    }
}

$plcRequirements = [System.Collections.Generic.List[object]]::new()
$cameraRequirements = [System.Collections.Generic.List[object]]::new()
foreach ($context in $contexts) {
    [void]$plcRequirements.Add([pscustomobject]@{
        provider = Get-ContextProvider $context.data
        context = $context.name
    })
    foreach ($camera in (Get-ContextCameras $context.data)) {
        [void]$cameraRequirements.Add([pscustomobject]@{
            manufacturer = $camera
            context = $context.name
        })
    }
}

$plcProviderNames = @($plcRequirements.provider | Sort-Object -Unique)
$cameraProviderNames = @($cameraRequirements.manufacturer | Sort-Object -Unique)

foreach ($provider in $plcProviderNames) {
    $requiredBy = @($plcRequirements | Where-Object { $_.provider -eq $provider } | Select-Object -ExpandProperty context)
    switch ($provider) {
        "Hsl" {
            Add-DependencyRecord "plc" $provider "HslCommunication.dll" @() "HslCommunication.dll" $requiredBy $false
        }
        "McpX" {
            Add-DependencyRecord "plc" $provider "McpXLib.dll" @() "McpXLib.dll" $requiredBy $false
        }
        "HaoCommunication" {
            $candidates = @(
                (Join-Path $rootPath "依赖\HaoCommunication.dll"),
                (Join-Path $rootPath "HaoCommunication.dll"),
                (Join-Path $rootPath "ClearFrost\DLL\HaoCommunication.dll")
            )
            if (-not $CleanRoom -and -not [string]::IsNullOrWhiteSpace($env:CLEARFROST_HAO_COMMUNICATION_PATH)) {
                $candidates = @($env:CLEARFROST_HAO_COMMUNICATION_PATH) + $candidates
            }

            Add-DependencyRecord "plc" $provider "HaoCommunication.dll" $candidates "HaoCommunication.dll" $requiredBy $true
        }
        default {
            Add-DependencyRecord "plc" $provider "$provider.dll" @() "$provider.dll" $requiredBy $true
            $lastRecord = $dependencyRecords[$dependencyRecords.Count - 1]
            $lastRecord.status = "BLOCKED"
            $lastRecord.reason = "Unsupported PLC provider name in config or preset."
            Add-BlockingReason "Unsupported PLC provider '$provider' in $($requiredBy -join ', ')."
        }
    }
}

foreach ($camera in $cameraProviderNames) {
    $requiredBy = @($cameraRequirements | Where-Object { $_.manufacturer -eq $camera } | Select-Object -ExpandProperty context)
    switch ($camera.Trim().ToLowerInvariant()) {
        "huaray" { $normalizedCamera = "Huaray" }
        "mindvision" { $normalizedCamera = "Huaray" }
        "hikvision" { $normalizedCamera = "Hikvision" }
        default { $normalizedCamera = $camera.Trim() }
    }

    if ($normalizedCamera -eq "Huaray") {
        $managedCandidates = @(
            (Join-Path $rootPath "ClearFrost\DLL\MVSDK_Net.dll"),
            (Join-Path $rootPath "MVSDK_Net.dll")
        )
        $nativeCandidates = @(
            (Join-Path $rootPath "x64依赖包\MVSDKmd.dll"),
            (Join-Path $rootPath "依赖\x64依赖包\MVSDKmd.dll")
        )
        if (-not $CleanRoom -and -not [string]::IsNullOrWhiteSpace($env:CLEARFROST_HUARAY_SDK_PATH)) {
            $managedCandidates = @($env:CLEARFROST_HUARAY_SDK_PATH) + $managedCandidates
            $nativeCandidates = @(
                (Join-Path ([System.IO.Path]::GetDirectoryName($env:CLEARFROST_HUARAY_SDK_PATH)) "MVSDKmd.dll")
            ) + $nativeCandidates
        }

        Add-DependencyRecord "camera" "Huaray managed SDK" "MVSDK_Net.dll" $managedCandidates "MVSDK_Net.dll" $requiredBy $true
        Add-DependencyRecord "camera" "Huaray native SDK" "MVSDKmd.dll" $nativeCandidates "MVSDKmd.dll" $requiredBy $true
    }
    elseif ($normalizedCamera -eq "Hikvision") {
        $candidates = @(
            (Join-Path $rootPath "海康依赖包\MvCameraControl.dll"),
            (Join-Path $rootPath "依赖\海康依赖包\MvCameraControl.dll")
        )
        if (-not $CleanRoom -and -not [string]::IsNullOrWhiteSpace($env:CLEARFROST_HIKVISION_SDK_PATH)) {
            $candidates = @($env:CLEARFROST_HIKVISION_SDK_PATH) + $candidates
        }

        Add-DependencyRecord "camera" "Hikvision native SDK" "MvCameraControl.dll" $candidates "MvCameraControl.dll" $requiredBy $true
    }
    else {
        Add-DependencyRecord "camera" $normalizedCamera "$normalizedCamera.dll" @() "$normalizedCamera.dll" $requiredBy $true
        Add-BlockingReason "Unsupported camera provider '$normalizedCamera' in $($requiredBy -join ', ')."
    }
}

$sourceInputs = @(
    @{ name = "Web UI entry"; fileName = "html\index.html"; source = (Join-Path $rootPath "ClearFrost\html\index.html"); package = "html\index.html" },
    @{ name = "Web UI bundle"; fileName = "html\js\bundle.js"; source = (Join-Path $rootPath "ClearFrost\html\js\bundle.js"); package = "html\js\bundle.js" },
    @{ name = "Runtime config"; fileName = "config.json"; source = $configPath; package = "config.json" },
    @{ name = "Project presets"; fileName = "project-presets.json"; source = $presetPath; package = "project-presets.json" }
)
foreach ($input in $sourceInputs) {
    Add-DependencyRecord "release-input" $input.name $input.fileName @($input.source) $input.package @("release profile") $false
}

if ($hasPackagedOutput) {
    $runtimeInputs = @(
        @{ name = "Application executable"; fileName = "清霜视觉.exe"; package = "清霜视觉.exe" },
        @{ name = "Application assembly"; fileName = "清霜视觉.dll"; package = "清霜视觉.dll" },
        @{ name = "Runtime dependency graph"; fileName = "清霜视觉.deps.json"; package = "清霜视觉.deps.json" },
        @{ name = "Runtime config"; fileName = "清霜视觉.runtimeconfig.json"; package = "清霜视觉.runtimeconfig.json" },
        @{ name = "DirectML runtime"; fileName = "DirectML.dll"; package = "DirectML.dll" },
        @{ name = "ONNX Runtime"; fileName = "onnxruntime.dll"; package = "onnxruntime.dll" },
        @{ name = "OpenCvSharp runtime"; fileName = "OpenCvSharp.dll"; package = "OpenCvSharp.dll" },
        @{ name = "WebView2 runtime binding"; fileName = "Microsoft.Web.WebView2.Core.dll"; package = "Microsoft.Web.WebView2.Core.dll" },
        @{ name = "SQLite native provider"; fileName = "SQLitePCLRaw.provider.e_sqlite3.dll"; package = "SQLitePCLRaw.provider.e_sqlite3.dll" }
    )
    foreach ($input in $runtimeInputs) {
        Add-DependencyRecord "runtime" $input.name $input.fileName @() $input.package @("$Profile package") $false
    }
}
else {
    Add-NotVerifiedReason "No Lite/Full package output was supplied; packaged paths and hashes are not verified."
}

$sourceModels = if (Test-Path -LiteralPath (Join-Path $rootPath "ClearFrost\ONNX") -PathType Container) {
    @(Get-ChildItem -LiteralPath (Join-Path $rootPath "ClearFrost\ONNX") -Filter "*.onnx" -File -ErrorAction SilentlyContinue)
}
else {
    @()
}
$packagedModels = if ($hasPackagedOutput -and (Test-Path -LiteralPath (Join-Path $outputPath "ONNX") -PathType Container)) {
    @(Get-ChildItem -LiteralPath (Join-Path $outputPath "ONNX") -Filter "*.onnx" -File -ErrorAction SilentlyContinue)
}
else {
    @()
}
$modelFiles = @($sourceModels) + @($packagedModels)
$modelStatus = if ($modelFiles.Count -gt 0) { "PASS" } elseif ($RequireModel) { "BLOCKED" } else { "NOT_VERIFIED" }
$modelReason = if ($modelFiles.Count -gt 0) { "External model input is present; inference evidence is reported by a separate real-model lane." } else { "No real ONNX model was supplied; no real-model inference is claimed." }
if ($modelStatus -eq "BLOCKED") {
    Add-BlockingReason "Required real ONNX model input is missing."
}
elseif ($modelStatus -eq "NOT_VERIFIED") {
    Add-NotVerifiedReason $modelReason
}

$modelEvidence = @($modelFiles | ForEach-Object {
    [ordered]@{
        path = $_.FullName
        fileName = $_.Name
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        bytes = $_.Length
        source = if ($_.FullName.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) { "workspace-input" } else { "package-input" }
    }
})

$overallStatus = if ($blockingReasons.Count -gt 0) { "BLOCKED" } elseif ($notVerifiedReasons.Count -gt 0) { "NOT_VERIFIED" } else { "PASS" }
$report = [ordered]@{
    schemaVersion = "1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    root = $rootPath
    profile = $Profile
    cleanRoom = $CleanRoom.IsPresent
    configurationSources = @("ClearFrost/config.json", "ClearFrost/project-presets.json")
    requiredPlcProviders = @($plcRequirements)
    requiredCameraProviders = @($cameraRequirements)
    dependencies = @($dependencyRecords)
    models = [ordered]@{
        status = $modelStatus
        reason = $modelReason
        files = $modelEvidence
    }
    overallStatus = $overallStatus
    blockingReasons = @($blockingReasons)
    notVerifiedReasons = @($notVerifiedReasons)
    promotionEligibility = if ($overallStatus -eq "PASS" -and $hasPackagedOutput -and $modelStatus -eq "PASS") { "PASS" } else { "BLOCKED" }
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportFile = [System.IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $reportFile
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    }

    $json = $report | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($reportFile, $json, [System.Text.UTF8Encoding]::new($false))
}

Write-Output ($report | ConvertTo-Json -Depth 12)
if ($overallStatus -eq "BLOCKED") {
    exit 1
}
if ($overallStatus -eq "NOT_VERIFIED") {
    exit 2
}
exit 0
