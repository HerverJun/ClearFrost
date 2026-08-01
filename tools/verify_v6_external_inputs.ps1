param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$ManifestPath = "",
    [string]$ReportPath = "",
    [switch]$RequireDetect,
    [switch]$RequireDependencies
)

$ErrorActionPreference = "Stop"
$rootPath = [System.IO.Path]::GetFullPath($Root)
$resolvedManifestPath = $ManifestPath
if ([string]::IsNullOrWhiteSpace($resolvedManifestPath)) {
    $resolvedManifestPath = [string]$env:CLEARFROST_V6_INPUT_MANIFEST
}
if (-not [string]::IsNullOrWhiteSpace($resolvedManifestPath) -and
    -not [System.IO.Path]::IsPathRooted($resolvedManifestPath)) {
    $resolvedManifestPath = Join-Path $rootPath $resolvedManifestPath
}

$blockingReasons = [System.Collections.Generic.List[string]]::new()
$notVerifiedReasons = [System.Collections.Generic.List[string]]::new()
$modelRecords = [System.Collections.Generic.List[object]]::new()
$dependencyRecords = [System.Collections.Generic.List[object]]::new()

function Add-BlockingReason([string]$Reason) {
    if (-not [string]::IsNullOrWhiteSpace($Reason)) {
        [void]$blockingReasons.Add($Reason)
    }
}

function Add-NotVerifiedReason([string]$Reason) {
    if (-not [string]::IsNullOrWhiteSpace($Reason)) {
        [void]$notVerifiedReasons.Add($Reason)
    }
}

function Get-String([object]$Object, [string]$Name) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return ""
    }

    return [string]$Object.PSObject.Properties[$Name].Value
}

function Get-Bool([object]$Object, [string]$Name, [bool]$Default = $false) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return $Default
    }

    return [bool]$Object.PSObject.Properties[$Name].Value
}

function Get-Long([object]$Object, [string]$Name) {
    $value = Get-String $Object $Name
    $parsed = 0L
    if ([long]::TryParse($value, [ref]$parsed)) {
        return $parsed
    }

    return 0L
}

function Get-Array([object]$Object, [string]$Name) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return @()
    }

    return @($Object.PSObject.Properties[$Name].Value)
}

function Resolve-InputPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    if ([System.IO.Path]::IsPathRooted($Value)) {
        return [System.IO.Path]::GetFullPath($Value.Trim())
    }

    return [System.IO.Path]::GetFullPath((Join-Path $rootPath $Value.Trim()))
}

function Test-DirectoryHasReparsePoint([string]$Path) {
    try {
        $current = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
        while ($null -ne $current) {
            $current.Refresh()
            if ($current.Exists -and (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                return $true
            }

            $current = $current.Parent
        }

        return $false
    }
    catch {
        return $true
    }
}

function Test-TrackedPath([string]$Path) {
    try {
        $relativePath = [System.IO.Path]::GetRelativePath($rootPath, $Path)
        if ($relativePath.StartsWith("..", [System.StringComparison]::Ordinal) -or
            [System.IO.Path]::IsPathRooted($relativePath)) {
            return $false
        }

        $gitOutput = @(git -C $rootPath ls-files --error-unmatch -- $relativePath 2>$null)
        return $LASTEXITCODE -eq 0 -and $gitOutput.Count -gt 0
    }
    catch {
        return $false
    }
}

function Get-FileEvidence([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered]@{ exists = $false; path = ""; fileName = ""; bytes = 0; sha256 = ""; reparsePoint = $false }
    }

    $fullPath = Resolve-InputPath $Path
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        return [ordered]@{ exists = $false; path = $fullPath; fileName = [System.IO.Path]::GetFileName($fullPath); bytes = 0; sha256 = ""; reparsePoint = $false }
    }

    $item = Get-Item -LiteralPath $fullPath
    $isReparse = (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
        (Test-DirectoryHasReparsePoint ([System.IO.Path]::GetDirectoryName($fullPath)))
    return [ordered]@{
        exists = $true
        path = $fullPath
        fileName = $item.Name
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToUpperInvariant()
        reparsePoint = $isReparse
        tracked = Test-TrackedPath $fullPath
    }
}

function Get-DefaultModelLanes {
    return @("Detect", "Classification", "Segmentation", "OBB", "Pose")
}

function Get-DefaultDependencies {
    return @(
        "HaoCommunication.dll",
        "MVSDK_Net.dll",
        "MVSDKmd.dll"
    )
}

function New-MissingModelRecord([string]$Lane) {
    return [ordered]@{
        lane = $Lane
        status = "NOT_VERIFIED"
        allowed = $false
        fileName = ""
        path = ""
        sha256 = ""
        bytes = 0
        opset = "NOT_VERIFIED"
        inputName = ""
        outputNames = @()
        task = $Lane
        source = ""
        validationImage = [ordered]@{ status = "NOT_VERIFIED"; path = ""; sha256 = ""; bytes = 0 }
        reason = "No explicit external model input was supplied."
    }
}

function Test-Entry([object]$Entry, [string]$Kind) {
    $name = Get-String $Entry "name"
    $fileName = Get-String $Entry "fileName"
    $pathValue = Get-String $Entry "path"
    $expectedHash = (Get-String $Entry "sha256").Trim().ToUpperInvariant()
    $expectedBytes = Get-Long $Entry "bytes"
    $source = Get-String $Entry "source"
    $allowed = Get-Bool $Entry "allowed"
    $record = [ordered]@{
        name = $name
        fileName = $fileName
        path = ""
        status = "NOT_VERIFIED"
        allowed = $allowed
        source = $source
        sourceType = Get-String $Entry "sourceType"
        distributionStatus = Get-String $Entry "distributionStatus"
        version = Get-String $Entry "version"
        architecture = Get-String $Entry "architecture"
        expectedSha256 = $expectedHash
        actualSha256 = ""
        expectedBytes = $expectedBytes
        actualBytes = 0
        reason = ""
    }

    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($fileName) -or
        [string]::IsNullOrWhiteSpace($pathValue) -or [string]::IsNullOrWhiteSpace($source)) {
        $record.status = "BLOCKED"
        $record.reason = "$Kind entry must declare name, fileName, path, and source."
        Add-BlockingReason $record.reason
        return $record
    }

    $fullPath = Resolve-InputPath $pathValue
    $record.path = $fullPath
    if (-not $allowed) {
        $record.reason = "$Kind entry is not allowed for this verification."
        Add-NotVerifiedReason "$Kind '$name' is not allowed."
        return $record
    }

    if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
        $record.status = "BLOCKED"
        $record.reason = "$Kind '$name' must declare a 64-character SHA-256."
        Add-BlockingReason $record.reason
        return $record
    }

    if ($expectedBytes -le 0) {
        $record.status = "BLOCKED"
        $record.reason = "$Kind '$name' must declare a positive byte size."
        Add-BlockingReason $record.reason
        return $record
    }

    if (Test-DirectoryHasReparsePoint ([System.IO.Path]::GetDirectoryName($fullPath))) {
        $record.status = "BLOCKED"
        $record.reason = "$Kind '$name' is under a reparse-point path."
        Add-BlockingReason $record.reason
        return $record
    }

    $evidence = Get-FileEvidence $fullPath
    $record.actualSha256 = $evidence.sha256
    $record.actualBytes = $evidence.bytes
    if (-not $evidence.exists) {
        $record.reason = "The explicit external input file does not exist."
        Add-NotVerifiedReason "$Kind '$name' is unavailable."
        return $record
    }

    if ($evidence.reparsePoint) {
        $record.status = "BLOCKED"
        $record.reason = "$Kind '$name' is a reparse-point file or is reached through one."
        Add-BlockingReason $record.reason
        return $record
    }

    if ($evidence.tracked) {
        $record.status = "BLOCKED"
        $record.reason = "$Kind '$name' is tracked by Git; external inputs must not be committed."
        Add-BlockingReason $record.reason
        return $record
    }

    if ($evidence.sha256 -ne $expectedHash -or $evidence.bytes -ne $expectedBytes) {
        $record.status = "BLOCKED"
        $record.reason = "$Kind '$name' does not match its declared SHA-256 or byte size."
        Add-BlockingReason $record.reason
        return $record
    }

    $record.status = "PASS"
    $record.reason = "Explicit input exists and matches the declared identity."
    return $record
}

function Test-ModelEntry([object]$Entry) {
    $lane = (Get-String $Entry "lane").Trim()
    if ($lane -notin (Get-DefaultModelLanes)) {
        $reason = "Unsupported model lane '$lane'."
        Add-BlockingReason $reason
        return [ordered]@{ lane = $lane; status = "BLOCKED"; reason = $reason }
    }

    $base = Test-Entry $Entry "model"
    $record = [ordered]@{
        lane = $lane
        status = $base.status
        allowed = $base.allowed
        fileName = $base.fileName
        path = $base.path
        sha256 = $base.actualSha256
        bytes = $base.actualBytes
        expectedSha256 = $base.expectedSha256
        expectedBytes = $base.expectedBytes
        opset = Get-String $Entry "opset"
        inputName = Get-String $Entry "inputName"
        outputNames = @(Get-Array $Entry "outputNames")
        task = Get-String $Entry "task"
        source = $base.source
        sourceType = $base.sourceType
        reason = $base.reason
        validationImage = [ordered]@{ status = "NOT_VERIFIED"; path = ""; sha256 = ""; bytes = 0; reason = "No validation image was declared." }
    }

    $imageEntry = $null
    if ($null -ne $Entry.PSObject.Properties["validationImage"] -and
        $null -ne $Entry.validationImage -and
        $Entry.validationImage -isnot [string]) {
        $imageEntry = $Entry.validationImage
    }
    $imagePath = if ($null -ne $imageEntry) { Get-String $imageEntry "path" } else { Get-String $Entry "validationImagePath" }
    $imageExpectedHash = if ($null -ne $imageEntry) {
        (Get-String $imageEntry "expectedSha256")
    }
    else {
        (Get-String $Entry "validationImageSha256")
    }
    if ([string]::IsNullOrWhiteSpace($imageExpectedHash) -and $null -ne $imageEntry) {
        $imageExpectedHash = Get-String $imageEntry "sha256"
    }
    if ([string]::IsNullOrWhiteSpace($imageExpectedHash)) {
        $imageExpectedHash = Get-String $Entry "validationImageHash"
    }
    $imageExpectedBytes = if ($null -ne $imageEntry) {
        Get-Long $imageEntry "expectedBytes"
    }
    else {
        Get-Long $Entry "validationImageBytes"
    }
    if ($imageExpectedBytes -le 0 -and $null -ne $imageEntry) {
        $imageExpectedBytes = Get-Long $imageEntry "bytes"
    }
    if ($imageExpectedBytes -le 0) {
        $imageExpectedBytes = Get-Long $Entry "validationImageSize"
    }

    if (-not [string]::IsNullOrWhiteSpace($imagePath)) {
        $image = Get-FileEvidence $imagePath
        $record.validationImage = [ordered]@{
            status = "NOT_VERIFIED"
            path = $image.path
            expectedSha256 = $imageExpectedHash.Trim().ToUpperInvariant()
            actualSha256 = $image.sha256
            expectedBytes = $imageExpectedBytes
            actualBytes = $image.bytes
            reason = "Validation image identity has not been verified."
        }
        if ($image.exists -and $image.reparsePoint) {
            $record.status = "BLOCKED"
            $record.reason = "Validation image is a reparse-point path."
            Add-BlockingReason "$lane validation image is unsafe."
        }
        elseif (-not $image.exists) {
            Add-NotVerifiedReason "$lane validation image is unavailable."
        }
        elseif ($imageExpectedHash -notmatch '^[0-9A-Fa-f]{64}$' -or $imageExpectedBytes -le 0) {
            if ($lane -eq "Detect") {
                Add-NotVerifiedReason "Detect validation image must declare SHA-256 and positive byte size."
            }
            else {
                Add-NotVerifiedReason "$lane validation image did not declare SHA-256 and positive byte size."
            }
        }
        elseif ($image.tracked) {
            $record.status = "BLOCKED"
            $record.reason = "Validation image is tracked by Git; external inputs must not be committed."
            Add-BlockingReason "$lane validation image is tracked by Git."
        }
        elseif ($image.sha256 -ne $imageExpectedHash.Trim().ToUpperInvariant() -or
            $image.bytes -ne $imageExpectedBytes) {
            $record.status = "BLOCKED"
            $record.reason = "Validation image does not match its declared SHA-256 or byte size."
            Add-BlockingReason "$lane validation image identity mismatch."
        }
        else {
            $record.validationImage.status = "PASS"
            $record.validationImage.reason = "Explicit validation image exists and matches the declared identity."
        }
    }

    if ([string]::IsNullOrWhiteSpace($record.opset)) {
        $record.opset = "NOT_VERIFIED"
        if ($record.status -eq "PASS") {
            $record.status = "NOT_VERIFIED"
        }
        Add-NotVerifiedReason "Model '$lane' did not declare an ONNX opset."
    }

    if ($record.status -eq "PASS" -and $record.validationImage.status -eq "NOT_VERIFIED" -and $lane -eq "Detect") {
        $record.status = "NOT_VERIFIED"
        Add-NotVerifiedReason "Detect requires an explicit reproducible validation image."
    }

    return $record
}

function Write-Report([object]$Report) {
    $json = $Report | ConvertTo-Json -Depth 20
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $fullReportPath = [System.IO.Path]::GetFullPath($ReportPath)
        $directory = Split-Path -Parent $fullReportPath
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
        }
        [System.IO.File]::WriteAllText($fullReportPath, $json, [System.Text.UTF8Encoding]::new($false))
    }
    Write-Output $json
}

$manifest = $null
$manifestStatus = "NOT_VERIFIED"
if ([string]::IsNullOrWhiteSpace($resolvedManifestPath)) {
    Add-NotVerifiedReason "CLEARFROST_V6_INPUT_MANIFEST was not supplied; no external model or dependency was inspected."
}
elseif (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
    Add-NotVerifiedReason "The declared external input manifest does not exist: $resolvedManifestPath"
}
else {
    try {
        $manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ((Get-String $manifest "schemaVersion") -ne "v6-g2-inputs-1.0") {
            Add-BlockingReason "External input manifest schemaVersion must be v6-g2-inputs-1.0."
        }
        else {
            $manifestStatus = "PASS"
        }
    }
    catch {
        Add-BlockingReason "Unable to parse external input manifest: $($_.Exception.Message)"
    }
}

$declaredModels = if ($null -eq $manifest) { @() } else { Get-Array $manifest "models" }
$declaredDependencies = if ($null -eq $manifest) { @() } else { Get-Array $manifest "dependencies" }

foreach ($lane in Get-DefaultModelLanes) {
    $entries = @($declaredModels | Where-Object { (Get-String $_ "lane") -eq $lane })
    if ($entries.Count -eq 0) {
        [void]$modelRecords.Add((New-MissingModelRecord $lane))
        continue
    }
    if ($entries.Count -gt 1) {
        Add-BlockingReason "Model lane '$lane' has more than one declared entry."
    }
    [void]$modelRecords.Add((Test-ModelEntry $entries[0]))
}

foreach ($entry in $declaredModels) {
    $lane = Get-String $entry "lane"
    if ($lane -notin (Get-DefaultModelLanes)) {
        continue
    }
}

foreach ($dependencyName in Get-DefaultDependencies) {
    $entries = @($declaredDependencies | Where-Object {
        (Get-String $_ "fileName") -eq $dependencyName -or (Get-String $_ "name") -eq $dependencyName
    })
    if ($entries.Count -eq 0) {
        $record = [ordered]@{
            name = $dependencyName
            fileName = $dependencyName
            status = "NOT_VERIFIED"
            path = ""
            expectedSha256 = ""
            actualSha256 = ""
            expectedBytes = 0
            actualBytes = 0
            reason = "No explicit external dependency input was supplied."
        }
        [void]$dependencyRecords.Add($record)
        Add-NotVerifiedReason "Dependency '$dependencyName' was not supplied."
        continue
    }
    if ($entries.Count -gt 1) {
        Add-BlockingReason "Dependency '$dependencyName' has more than one declared entry."
    }
    [void]$dependencyRecords.Add((Test-Entry $entries[0] "dependency"))
}

foreach ($entry in $declaredDependencies) {
    $fileName = Get-String $entry "fileName"
    if ($fileName -notin (Get-DefaultDependencies)) {
        [void]$dependencyRecords.Add((Test-Entry $entry "dependency"))
    }
}

$detect = @($modelRecords | Where-Object { $_.lane -eq "Detect" })[0]
if ($RequireDetect -and $detect.status -ne "PASS") {
    Add-BlockingReason "Detect is required for this run, but its external model contract is not PASS."
}
if ($RequireDependencies -and @($dependencyRecords | Where-Object { $_.status -ne "PASS" }).Count -gt 0) {
    Add-BlockingReason "Required external dependencies are not all PASS."
}

$overallStatus = if ($blockingReasons.Count -gt 0) { "BLOCKED" } elseif ($notVerifiedReasons.Count -gt 0 -or $manifestStatus -ne "PASS") { "NOT_VERIFIED" } else { "PASS" }
$report = [ordered]@{
    schemaVersion = "v6-g2-inputs-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    root = $rootPath
    manifestPath = if ([string]::IsNullOrWhiteSpace($resolvedManifestPath)) { "" } else { [System.IO.Path]::GetFullPath($resolvedManifestPath) }
    manifestStatus = $manifestStatus
    models = @($modelRecords)
    dependencies = @($dependencyRecords)
    status = $overallStatus
    blockingReasons = @($blockingReasons)
    notVerifiedReasons = @($notVerifiedReasons)
    required = [ordered]@{
        detect = $RequireDetect.IsPresent
        dependencies = $RequireDependencies.IsPresent
    }
}

Write-Report $report
if ($overallStatus -eq "BLOCKED") {
    exit 1
}
if ($overallStatus -eq "NOT_VERIFIED") {
    exit 2
}
exit 0
