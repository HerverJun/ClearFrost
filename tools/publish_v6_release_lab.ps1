param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$ManifestPath = "",
    [string]$OutputRoot = "",
    [string]$EvidencePath = "",
    [string]$Version = "",
    [string]$DotnetPath = "dotnet",
    [switch]$CreateZip
)

$ErrorActionPreference = "Stop"
$rootPath = [System.IO.Path]::GetFullPath($Root)
. (Join-Path $rootPath "tools\v6_g2_identity.ps1")
$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $rootPath "artifacts\v6-g2\publish"
}
else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$evidenceFile = if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    Join-Path $rootPath "artifacts\v6-g2\publish\release-lab-evidence.json"
}
else {
    [System.IO.Path]::GetFullPath($EvidencePath)
}
$evidenceDirectory = Split-Path -Parent $evidenceFile
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot, $evidenceDirectory | Out-Null

$blockingReasons = [System.Collections.Generic.List[string]]::new()
$notVerifiedReasons = [System.Collections.Generic.List[string]]::new()
$packageRecords = [System.Collections.Generic.List[object]]::new()

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

function Get-Property([object]$Object, [string]$Name) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return $null
    }
    return $Object.PSObject.Properties[$Name].Value
}

function Get-ShortVersion {
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        return $Version.Trim().TrimStart([char[]]"vV")
    }
    $projectText = Get-Content -LiteralPath (Join-Path $rootPath "ClearFrost\ClearFrost.csproj") -Raw -Encoding UTF8
    $match = [regex]::Match($projectText, "<Version>(?<version>[^<]+)</Version>")
    if ($match.Success) {
        return $match.Groups["version"].Value.Trim()
    }
    return "6.1.0-preview.1"
}

function Get-AssemblyName {
    $projectText = Get-Content -LiteralPath (Join-Path $rootPath "ClearFrost\ClearFrost.csproj") -Raw -Encoding UTF8
    $match = [regex]::Match($projectText, "<AssemblyName>(?<name>[^<]+)</AssemblyName>")
    if ($match.Success) {
        return $match.Groups["name"].Value.Trim()
    }
    return "清霜视觉"
}

function Get-RelativePath([string]$BasePath, [string]$Path) {
    return ([System.IO.Path]::GetRelativePath([System.IO.Path]::GetFullPath($BasePath), [System.IO.Path]::GetFullPath($Path))).Replace("\", "/")
}

function Assert-SafeOutputTarget([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
    $source = $rootPath.TrimEnd("\", "/")
    if ($fullPath.Equals($source, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output must not be the source root."
    }
    if ($fullPath.StartsWith($source + "\", [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith((Join-Path $source "artifacts\"), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output under the source tree must be under artifacts."
    }
}

function Get-PackageHash([string]$Directory) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File -Recurse |
            Where-Object { $_.Name -ne "V6_PACKAGE_MANIFEST.json" } |
            Sort-Object FullName)) {
        $relative = Get-RelativePath $Directory $file.FullName
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        [void]$builder.Append($relative)
        [void]$builder.Append("|")
        [void]$builder.Append($hash)
        [void]$builder.Append("|")
        [void]$builder.Append($file.Length)
        [void]$builder.AppendLine()
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($builder.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Get-BundleHash([string]$BasePath) {
    $projectText = Get-Content -LiteralPath (Join-Path $BasePath "ClearFrost\ClearFrost.csproj") -Raw -Encoding UTF8
    $matches = [regex]::Matches($projectText, "'(?<file>html\\js\\[^']+\.js)'")
    $files = @($matches | ForEach-Object { $_.Groups["file"].Value } | Where-Object { $_ -ne "html\js\bundle.js" })
    if ($files.Count -eq 0) {
        throw "The deterministic Web UI source list is empty."
    }
    $texts = @()
    foreach ($file in $files) {
        $sourcePath = Join-Path $BasePath (Join-Path "ClearFrost" $file)
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Web UI source is missing: $file"
        }
        $texts += [System.IO.File]::ReadAllText($sourcePath)
    }
    $content = [string]::Join([Environment]::NewLine, $texts)
    $encoding = [System.Text.UTF8Encoding]::new($true)
    $bytes = @($encoding.GetPreamble()) + @($encoding.GetBytes($content))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash([byte[]]$bytes))).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Get-FileRecord([string]$RootDirectory, [System.IO.FileInfo]$File) {
    return [ordered]@{
        path = Get-RelativePath $RootDirectory $File.FullName
        bytes = $File.Length
        sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

function Get-InputRecord([object]$InputReport, [string]$Name, [string]$Lane = "") {
    if ($null -eq $InputReport) {
        return $null
    }
    if (-not [string]::IsNullOrWhiteSpace($Lane)) {
        return @($InputReport.models | Where-Object { (Get-String $_ "lane") -eq $Lane }) | Select-Object -First 1
    }
    return @($InputReport.dependencies | Where-Object { (Get-String $_ "name") -eq $Name -or (Get-String $_ "fileName") -eq $Name }) | Select-Object -First 1
}

function Invoke-InputValidator([string]$InputReportPath) {
    $validator = Join-Path $rootPath "tools\verify_v6_external_inputs.ps1"
    $shell = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -eq $shell) {
        $shell = Get-Command powershell.exe -ErrorAction Stop
    }
    $arguments = @("-Root", $rootPath, "-ReportPath", $InputReportPath)
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        $arguments += @("-ManifestPath", $ManifestPath)
    }
    $output = @(& $shell.Source -NoProfile -ExecutionPolicy Bypass -File $validator @arguments 2>&1)
    return [pscustomobject]@{
        exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        output = @($output | ForEach-Object { [string]$_ })
    }
}

function Remove-UnlistedExternalFiles([string]$PackagePath) {
    foreach ($fileName in @("HaoCommunication.dll", "MVSDK_Net.dll", "MVSDKmd.dll", "MvCameraControl.dll")) {
        $candidate = Join-Path $PackagePath $fileName
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            Remove-Item -LiteralPath $candidate -Force
        }
    }
    foreach ($onnx in @(Get-ChildItem -LiteralPath $PackagePath -Filter "*.onnx" -File -Recurse -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $onnx.FullName -Force
    }
    $onnxDirectory = Join-Path $PackagePath "ONNX"
    if (Test-Path -LiteralPath $onnxDirectory) {
        Remove-Item -LiteralPath $onnxDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $onnxDirectory | Out-Null
}

function Copy-ExternalInputs([string]$PackagePath, [object]$InputReport) {
    $detect = Get-InputRecord $InputReport "" "Detect"
    $detectFileName = Get-String $detect "fileName"
    if ([string]::IsNullOrWhiteSpace($detectFileName) -or
        [System.IO.Path]::IsPathRooted($detectFileName) -or
        $detectFileName.Contains("..") -or
        [System.IO.Path]::GetFileName($detectFileName) -ne $detectFileName) {
        throw "External Detect model fileName is unsafe: $detectFileName"
    }
    $detectTarget = Join-Path (Join-Path $PackagePath "ONNX") $detectFileName
    Copy-Item -LiteralPath (Get-String $detect "path") -Destination $detectTarget -Force

    foreach ($dependency in @($InputReport.dependencies | Where-Object { (Get-String $_ "status") -eq "PASS" })) {
        $fileName = Get-String $dependency "fileName"
        if ([string]::IsNullOrWhiteSpace($fileName) -or $fileName.Contains("..") -or [System.IO.Path]::IsPathRooted($fileName)) {
            throw "External dependency fileName is unsafe: $fileName"
        }
        Copy-Item -LiteralPath (Get-String $dependency "path") -Destination (Join-Path $PackagePath $fileName) -Force
    }
}

function Test-Package([string]$Mode, [string]$PackagePath, [object]$InputReport, [string]$CommitSha, [string]$PackageVersion, [string]$BundleHash) {
    $errors = [System.Collections.Generic.List[string]]::new()
    $assemblyName = Get-AssemblyName
    $exePath = Join-Path $PackagePath ($assemblyName + ".exe")
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { [void]$errors.Add("Application executable is missing.") }
    if (-not (Test-Path -LiteralPath (Join-Path $PackagePath "html\index.html") -PathType Leaf)) { [void]$errors.Add("Web UI entry is missing.") }
    $bundlePath = Join-Path $PackagePath "html\js\bundle.js"
    if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) { [void]$errors.Add("Web UI bundle is missing.") }
    elseif ((Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToUpperInvariant() -ne $BundleHash) { [void]$errors.Add("Web UI bundle hash does not match the source contract.") }
    $onnxFiles = @(Get-ChildItem -LiteralPath (Join-Path $PackagePath "ONNX") -Filter "*.onnx" -File -ErrorAction SilentlyContinue)
    $detect = Get-InputRecord $InputReport "" "Detect"
    $detectFileName = Get-String $detect "fileName"
    if ([string]::IsNullOrWhiteSpace($detectFileName) -or
        [System.IO.Path]::IsPathRooted($detectFileName) -or
        $detectFileName.Contains("..") -or
        [System.IO.Path]::GetFileName($detectFileName) -ne $detectFileName) {
        [void]$errors.Add("The authorized Detect model fileName is unsafe.")
    }
    if ($onnxFiles.Count -ne 1 -or $onnxFiles[0].Name -ne $detectFileName) { [void]$errors.Add("The package must contain exactly the authorized Detect model.") }
    $requiredNames = @("HaoCommunication.dll", "MVSDK_Net.dll", "MVSDKmd.dll")
    foreach ($name in $requiredNames) {
        $dependency = Get-InputRecord $InputReport $name
        $packageDependencyPath = Join-Path $PackagePath $name
        if (-not (Test-Path -LiteralPath $packageDependencyPath -PathType Leaf)) {
            [void]$errors.Add("Required external dependency is missing: $name")
            continue
        }

        $expectedDependencyHash = Get-String $dependency "actualSha256"
        $actualDependencyHash = (Get-FileHash -LiteralPath $packageDependencyPath -Algorithm SHA256).Hash.ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($expectedDependencyHash) -or
            $actualDependencyHash -ne $expectedDependencyHash.ToUpperInvariant()) {
            [void]$errors.Add("Packaged dependency hash does not match the authorized input: $name")
        }
    }
    $detectInputHash = Get-String $detect "sha256"
    $packagedDetectPath = Join-Path (Join-Path $PackagePath "ONNX") (Get-String $detect "fileName")
    if (-not (Test-Path -LiteralPath $packagedDetectPath -PathType Leaf)) {
        [void]$errors.Add("Authorized Detect model is missing from the package.")
    }
    elseif ([string]::IsNullOrWhiteSpace($detectInputHash) -or
        (Get-FileHash -LiteralPath $packagedDetectPath -Algorithm SHA256).Hash.ToUpperInvariant() -ne $detectInputHash.ToUpperInvariant()) {
        [void]$errors.Add("Packaged Detect model hash does not match the authorized input.")
    }
    $forbidden = @(Get-ChildItem -LiteralPath $PackagePath -File -Recurse | Where-Object { $_.Name -match "MockCamera|SimStress|ClearFrost.Tests|Stub" })
    if ($forbidden.Count -gt 0) { [void]$errors.Add("Test or fake artifact is present in the package: $($forbidden.Name -join ', ')") }
    if ($Mode -eq "Full" -and -not (Test-Path -LiteralPath (Join-Path $PackagePath "hostfxr.dll") -PathType Leaf)) { [void]$errors.Add("Full package is missing hostfxr.dll and is not self-contained.") }
    if ($Mode -eq "Lite" -and (Test-Path -LiteralPath (Join-Path $PackagePath "hostfxr.dll") -PathType Leaf)) { [void]$errors.Add("Lite package unexpectedly contains the self-contained host runtime.") }

    $textExtensions = @(".json", ".config", ".txt", ".xml", ".deps", ".runtimeconfig", ".md", ".html", ".js", ".css")
    foreach ($file in @(Get-ChildItem -LiteralPath $PackagePath -File -Recurse)) {
        if ($textExtensions -contains $file.Extension.ToLowerInvariant()) {
            $text = [System.IO.File]::ReadAllText($file.FullName)
            if ($text.Contains($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) { [void]$errors.Add("Package contains a source-root absolute path: $($file.Name)") }
            if ($text -match "[A-Za-z]:\\Users\\[^\r\n]+") { [void]$errors.Add("Package contains a machine-specific absolute path: $($file.Name)") }
        }
    }

    $fileRecords = @(Get-ChildItem -LiteralPath $PackagePath -File -Recurse | ForEach-Object { Get-FileRecord $PackagePath $_ })
    $manifest = [ordered]@{
        schemaVersion = "v6-g2-package-1.0"
        mode = $Mode
        packageVersion = $PackageVersion
        commitSha = $CommitSha
        runtimeIdentifier = "win-x64"
        selfContained = $Mode -eq "Full"
        bundleSha256 = $BundleHash
        externalInputs = @($InputReport.models | Where-Object { (Get-String $_ "lane") -eq "Detect" } | ForEach-Object {
            [ordered]@{ kind = "model"; lane = $_.lane; fileName = $_.fileName; sha256 = $_.sha256; packagePath = "ONNX/$($_.fileName)" }
        }) + @($InputReport.dependencies | Where-Object { (Get-String $_ "status") -eq "PASS" } | ForEach-Object {
            [ordered]@{ kind = "dependency"; name = $_.name; fileName = $_.fileName; sha256 = $_.actualSha256; packagePath = $_.fileName }
        })
        files = $fileRecords
    }
    $manifestPath = Join-Path $PackagePath "V6_PACKAGE_MANIFEST.json"
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20), [System.Text.UTF8Encoding]::new($false))
    $packageHash = Get-PackageHash $PackagePath
    $manifest.packageHash = $packageHash
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20), [System.Text.UTF8Encoding]::new($false))
    $packageHash = Get-PackageHash $PackagePath
    if ($errors.Count -gt 0) {
        return [ordered]@{ mode = $Mode; status = "BLOCKED"; path = $PackagePath; packageHash = $packageHash; errors = @($errors); manifestPath = $manifestPath }
    }
    return [ordered]@{ mode = $Mode; status = "PASS"; path = $PackagePath; packageHash = $packageHash; fileCount = $fileRecords.Count; manifestPath = $manifestPath; errors = @() }
}

function Publish-Mode([string]$Mode, [string]$PackageVersion, [string]$CommitSha, [object]$InputReport, [string]$BundleHash) {
    $targetPath = Join-Path $resolvedOutputRoot ("ClearFrost_{0}_{1}" -f $PackageVersion, $Mode)
    if (Test-Path -LiteralPath $targetPath) {
        Remove-Item -LiteralPath $targetPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $targetPath | Out-Null
    $isFull = $Mode -eq "Full"
    $arguments = @(
        "publish",
        (Join-Path $rootPath "ClearFrost\ClearFrost.csproj"),
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", ([string]$isFull).ToLowerInvariant(),
        "-o", $targetPath,
        "-p:Platform=x64",
        "-p:Version=$PackageVersion",
        "-p:PackageVersion=$PackageVersion",
        "-p:InformationalVersion=$PackageVersion",
        "-p:IncludeSourceRevisionInInformationalVersion=false",
        "-p:PublishSingleFile=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:NuGetAudit=false"
    )
    $logPath = Join-Path $evidenceDirectory ("publish-{0}.log" -f $Mode.ToLowerInvariant())
    $output = @(& $DotnetPath @arguments 2>&1)
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    [System.IO.File]::WriteAllLines($logPath, @($output | ForEach-Object { [string]$_ }), [System.Text.UTF8Encoding]::new($false))
    if ($exitCode -ne 0) {
        return [ordered]@{ mode = $Mode; status = "BLOCKED"; exitCode = $exitCode; path = $targetPath; log = $logPath; errors = @("dotnet publish failed.") }
    }
    try {
        Remove-UnlistedExternalFiles $targetPath
        Copy-ExternalInputs $targetPath $InputReport
        $result = Test-Package $Mode $targetPath $InputReport $CommitSha $PackageVersion $BundleHash
    }
    catch {
        return [ordered]@{
            mode = $Mode
            status = "BLOCKED"
            exitCode = $exitCode
            path = $targetPath
            log = $logPath
            errors = @("Package staging or validation failed: $($_.Exception.Message)")
        }
    }
    $result.exitCode = $exitCode
    $result.log = $logPath
    if ($CreateZip -and $result.status -eq "PASS") {
        $zipPath = "$targetPath.zip"
        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
        Compress-Archive -Path (Join-Path $targetPath "*") -DestinationPath $zipPath -CompressionLevel Optimal
        $result.zip = [ordered]@{ path = $zipPath; sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant() }
        $extractRoot = Join-Path $evidenceDirectory ("zip-roundtrip-{0}" -f $Mode.ToLowerInvariant())
        if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        $extractedHash = Get-PackageHash $extractRoot
        $result.zip.roundTripStatus = if ($extractedHash -eq $result.packageHash) { "PASS" } else { "BLOCKED" }
        $result.zip.extractedPackageHash = $extractedHash
        if ($result.zip.roundTripStatus -ne "PASS") { Add-BlockingReason "$Mode zip round trip changed the package contract." }
    }
    return $result
}

Assert-SafeOutputTarget $resolvedOutputRoot
$inputReportPath = Join-Path $evidenceDirectory "external-inputs.json"
$inputValidation = Invoke-InputValidator $inputReportPath
$inputReport = if (Test-Path -LiteralPath $inputReportPath -PathType Leaf) { Get-Content -LiteralPath $inputReportPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$detect = Get-InputRecord $inputReport "" "Detect"
$requiredDependencies = @("HaoCommunication.dll", "MVSDK_Net.dll", "MVSDKmd.dll")
$missingRequired = @()
if ($null -eq $detect -or (Get-String $detect "status") -ne "PASS") { $missingRequired += "Detect model" }
foreach ($name in $requiredDependencies) {
    $dependency = Get-InputRecord $inputReport $name
    if ($null -eq $dependency -or (Get-String $dependency "status") -ne "PASS") { $missingRequired += $name }
}
$requiredInputStatus = if ($missingRequired.Count -eq 0) { "PASS" } elseif ($null -eq $inputReport) { "NOT_VERIFIED" } elseif ((Get-String $inputReport "status") -eq "BLOCKED") { "BLOCKED" } else { "NOT_VERIFIED" }
if ($requiredInputStatus -ne "PASS") {
    Add-NotVerifiedReason "Positive Lite/Full packages require an explicit Detect model and all required external dependencies. Missing or invalid: $($missingRequired -join ', ')."
}

$packageVersion = Get-ShortVersion
$commitSha = ""
try { $commitSha = (git -C $rootPath rev-parse HEAD).Trim() } catch { $commitSha = "" }
$bundleHash = ""
try { $bundleHash = Get-BundleHash $rootPath } catch { Add-BlockingReason "Unable to compute deterministic Web UI bundle hash: $($_.Exception.Message)" }

if ($requiredInputStatus -eq "PASS" -and $inputReport -ne $null -and $bundleHash -ne "") {
    foreach ($mode in @("Lite", "Full")) {
        [void]$packageRecords.Add((Publish-Mode $mode $packageVersion $commitSha $inputReport $bundleHash))
    }
}
else {
    foreach ($mode in @("Lite", "Full")) {
        [void]$packageRecords.Add([ordered]@{
            mode = $mode
            status = "NOT_VERIFIED"
            exitCode = $null
            path = ""
            reason = "Positive publish was not executed because required external inputs were not PASS."
        })
    }
}

$publishStatus = if (@($packageRecords | Where-Object { $_.status -eq "BLOCKED" }).Count -gt 0) {
    "BLOCKED"
}
elseif (@($packageRecords | Where-Object { $_.status -ne "PASS" }).Count -eq 0) {
    "PASS"
}
else {
    "NOT_VERIFIED"
}
if ($publishStatus -eq "BLOCKED" -and $requiredInputStatus -eq "BLOCKED") {
    $publishStatus = "BLOCKED"
    Add-BlockingReason "Required external input validation is BLOCKED; positive publishing is fail-closed."
}
$inputManifestForIdentity = if ($null -eq $inputReport) { "" } else { Get-String $inputReport "manifestPath" }
$identity = New-V6G2EvidenceIdentity -Root $rootPath `
    -InputManifestPath $inputManifestForIdentity `
    -DetectModelPath (Get-String $detect "path") `
    -ValidationImagePath (Get-String $detect.validationImage "path") `
    -Provider "NOT_APPLICABLE" `
    -ExternalDependencies $(if ($null -eq $inputReport) { @() } else { @($inputReport.dependencies) })
$report = [ordered]@{
    schemaVersion = "v6-g2-release-lab-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    root = $rootPath
    commitSha = $commitSha
    identity = $identity
    packageVersion = $packageVersion
    runtimeIdentifier = "win-x64"
    inputValidation = $inputReport
    requiredInputStatus = $requiredInputStatus
    requiredStatus = $requiredInputStatus
    compatibilityStatus = if ($null -eq $inputReport) { "NOT_VERIFIED" } else { Get-String $inputReport "compatibilityStatus" }
    overallStatus = $publishStatus
    providerSemantics = "NOT_APPLICABLE: release lab packages binaries but does not execute model inference."
    requiredInputMissing = @($missingRequired)
    bundleSha256 = $bundleHash
    packages = @($packageRecords)
    status = $publishStatus
    promotionEligibility = if ($publishStatus -eq "PASS") { "PASS" } else { "BLOCKED" }
    blockingReasons = @($blockingReasons | Select-Object -Unique)
    notVerifiedReasons = @($notVerifiedReasons | Select-Object -Unique)
    releaseMutation = [ordered]@{ tagCreated = $false; githubReleaseCreated = $false }
}
[System.IO.File]::WriteAllText($evidenceFile, ($report | ConvertTo-Json -Depth 30), [System.Text.UTF8Encoding]::new($false))
Write-Output ($report | ConvertTo-Json -Depth 30)
if ($publishStatus -eq "BLOCKED") { exit 1 }
if ($publishStatus -eq "NOT_VERIFIED") { exit 2 }
exit 0
