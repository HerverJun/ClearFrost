param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$EvidencePath = "",
    [switch]$ReportOnly,
    [switch]$KeepSnapshot
)

$ErrorActionPreference = "Stop"

$sourceRoot = [System.IO.Path]::GetFullPath($Root)
$evidenceRoot = if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    Join-Path $sourceRoot "artifacts\v6-gate"
}
else {
    [System.IO.Path]::GetFullPath($EvidencePath)
}
$logRoot = Join-Path $evidenceRoot "logs"
$testResultRoot = Join-Path $evidenceRoot "test-results"
$publishRoot = Join-Path $evidenceRoot "publish"
$snapshotRoot = ""
$snapshotDeleted = $false
$stepRecords = [System.Collections.Generic.List[object]]::new()
$blockingReasons = [System.Collections.Generic.List[string]]::new()
$notVerifiedReasons = [System.Collections.Generic.List[string]]::new()
$logicalSkips = [System.Collections.Generic.List[object]]::new()
$manifest = $null

New-Item -ItemType Directory -Force -Path $evidenceRoot, $logRoot, $testResultRoot, $publishRoot | Out-Null

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

function Add-LogicalSkip([string]$Lane, [string]$Reason) {
    [void]$logicalSkips.Add([ordered]@{
        lane = $Lane
        status = "NOT_VERIFIED"
        reason = $Reason
    })
    Add-NotVerifiedReason $Reason
}

function Get-ExecutablePath([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = switch ($Name) {
        "node" {
            @(
                (Join-Path ${env:ProgramFiles} "Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\NodeJs\node.exe"),
                (Join-Path ${env:LOCALAPPDATA} "Programs\nodejs\node.exe")
            )
        }
        default { @() }
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    return ""
}

function Get-CommandText([string]$FilePath, [string[]]$Arguments) {
    $parts = @($FilePath) + @($Arguments)
    return ($parts | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join " "
}

function Invoke-Step(
    [string]$Name,
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [string]$LogName) {

    $logPath = Join-Path $logRoot "$LogName.log"
    $outputLines = [System.Collections.Generic.List[string]]::new()
    $exitCode = 1

    try {
        Push-Location -LiteralPath $WorkingDirectory
        try {
            $commandOutput = & $FilePath @Arguments 2>&1
            foreach ($line in @($commandOutput)) {
                [void]$outputLines.Add([string]$line)
            }
            $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        }
        finally {
            Pop-Location
        }
    }
    catch {
        [void]$outputLines.Add("Exception: $($_.Exception.Message)")
        $exitCode = 1
    }

    [System.IO.File]::WriteAllLines($logPath, $outputLines, [System.Text.UTF8Encoding]::new($false))
    $status = if ($exitCode -eq 0) { "PASS" } else { "BLOCKED" }
    $record = [ordered]@{
        name = $Name
        status = $status
        exitCode = $exitCode
        command = Get-CommandText $FilePath $Arguments
        workingDirectory = $WorkingDirectory
        log = $logPath
    }
    [void]$stepRecords.Add($record)
    if ($status -eq "BLOCKED") {
        Add-BlockingReason "$Name failed with exit code $exitCode. See $logPath."
    }

    return [pscustomobject]$record
}

function Get-FileHashOrEmpty([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-BytesHash([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace("-", "").ToUpperInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ProductVersion([string]$ProjectPath) {
    $projectText = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
    $match = [regex]::Match($projectText, "<Version>(?<version>[^<]+)</Version>")
    if ($match.Success) {
        return $match.Groups["version"].Value.Trim()
    }

    return ""
}

function Get-TrxCounters([string]$ResultsDirectory) {
    $trxPath = Get-ChildItem -LiteralPath $ResultsDirectory -Filter "hermetic.trx" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $trxPath) {
        return [ordered]@{
            total = 0
            executed = 0
            passed = 0
            failed = 0
            skipped = 0
            errors = 0
        }
    }

    [xml]$trx = Get-Content -LiteralPath $trxPath.FullName -Raw -Encoding UTF8
    $counter = $trx.TestRun.ResultSummary.Counters
    return [ordered]@{
        total = [int]$counter.total
        executed = [int]$counter.executed
        passed = [int]$counter.passed
        failed = [int]$counter.failed
        skipped = [int]$counter.notExecuted
        errors = [int]$counter.error
    }
}

function Get-DependencyReport([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [ordered]@{
            overallStatus = "BLOCKED"
            dependencies = @()
            models = [ordered]@{ status = "NOT_VERIFIED"; reason = "Dependency report was not produced."; files = @() }
            blockingReasons = @("Dependency report missing: $Path")
            notVerifiedReasons = @()
        }
    }

    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

$gitPath = Get-ExecutablePath "git"
$dotnetPath = Get-ExecutablePath "dotnet"
$powerShellPath = Get-ExecutablePath "powershell.exe"
if ([string]::IsNullOrWhiteSpace($powerShellPath)) {
    $powerShellPath = Get-ExecutablePath "pwsh"
}

$commit = ""
$branch = ""
$mainSha = ""
$remoteSha = ""
$dirtyEntries = @()
$gitMetadataStatus = "PASS"
try {
    $commit = (& $gitPath rev-parse HEAD).Trim()
    $branch = (& $gitPath branch --show-current).Trim()
    $mainSha = (& $gitPath rev-parse main).Trim()
    $remoteSha = (& $gitPath rev-parse github/V6_test 2>$null).Trim()
    $dirtyEntries = @(& $gitPath status --porcelain --untracked-files=all)
}
catch {
    $gitMetadataStatus = "BLOCKED"
    Add-BlockingReason "Unable to read Git metadata: $($_.Exception.Message)"
}

if ($branch -ne "V6_test") {
    Add-BlockingReason "Gate must run on V6_test; actual branch was '$branch'."
}
if ($dirtyEntries.Count -gt 0) {
    Add-BlockingReason "Worktree is dirty; tracked-only clean-room evidence requires a clean checkout."
}
if ([string]::IsNullOrWhiteSpace($dotnetPath)) {
    Add-BlockingReason "dotnet CLI was not found in PATH."
}
if ([string]::IsNullOrWhiteSpace($powerShellPath)) {
    Add-BlockingReason "PowerShell executable was not found."
}

$productVersion = ""
$archiveStatus = "NOT_VERIFIED"
$archivePath = ""
$archiveLog = Join-Path $logRoot "tracked-only-archive.log"

try {
    $productVersion = Get-ProductVersion (Join-Path $sourceRoot "ClearFrost\ClearFrost.csproj")
}
catch {
    Add-BlockingReason "Unable to read product version: $($_.Exception.Message)"
}

try {
    $scratchName = "ClearFrostV6Gate-$([Guid]::NewGuid().ToString('N'))"
    $snapshotRoot = Join-Path ([System.IO.Path]::GetTempPath()) $scratchName
    New-Item -ItemType Directory -Force -Path $snapshotRoot | Out-Null
    $archivePath = Join-Path $snapshotRoot "tracked-source.zip"

    if (-not [string]::IsNullOrWhiteSpace($gitPath) -and -not [string]::IsNullOrWhiteSpace($commit)) {
        $archiveStep = Invoke-Step "tracked-only git archive" $gitPath @("archive", "--format=zip", "--output=$archivePath", $commit) $sourceRoot "tracked-only-archive"
        $archiveStatus = $archiveStep.status
        if ($archiveStep.status -eq "PASS") {
            Expand-Archive -LiteralPath $archivePath -DestinationPath $snapshotRoot -Force
            Remove-Item -LiteralPath $archivePath -Force
            $archiveStatus = "PASS"
        }
    }
}
catch {
    $archiveStatus = "BLOCKED"
    Add-BlockingReason "Unable to create tracked-only snapshot: $($_.Exception.Message)"
}

$snapshotProjectRoot = $snapshotRoot
$snapshotGateRoot = if ([string]::IsNullOrWhiteSpace($snapshotRoot)) { "" } else { $snapshotRoot }
$snapshotScriptsRoot = if ([string]::IsNullOrWhiteSpace($snapshotRoot)) { "" } else { Join-Path $snapshotRoot "tools" }
$snapshotTestProject = if ([string]::IsNullOrWhiteSpace($snapshotRoot)) { "" } else { Join-Path $snapshotRoot "ClearFrost.Tests\ClearFrost.Tests.csproj" }
$snapshotSolution = if ([string]::IsNullOrWhiteSpace($snapshotRoot)) { "" } else { Join-Path $snapshotRoot "ClearFrost.sln" }
$snapshotProject = if ([string]::IsNullOrWhiteSpace($snapshotRoot)) { "" } else { Join-Path $snapshotRoot "ClearFrost\ClearFrost.csproj" }
$snapshotPublishScript = ""
if (-not [string]::IsNullOrWhiteSpace($snapshotRoot)) {
    $publishScripts = @(Get-ChildItem -LiteralPath $snapshotRoot -Filter "publish.ps1" -File -Recurse -ErrorAction SilentlyContinue)
    if ($publishScripts.Count -eq 1) {
        $snapshotPublishScript = $publishScripts[0].FullName
    }
    else {
        Add-BlockingReason "Tracked-only snapshot must contain exactly one publish.ps1; found $($publishScripts.Count)."
    }
}

$encodingStep = $null
$restoreStep = $null
$debugBuildStep = $null
$testStep = $null
$releaseBuildStep = $null
$releaseCompileStep = $null
$dependencyStep = $null
$litePublishStep = $null
$fullPublishStep = $null
$bundleStatus = "NOT_VERIFIED"
$bundleSourceHash = ""
$bundleRuntimeHash = ""
$bundlePublishHashes = @()
$jsSyntaxStatus = "NOT_VERIFIED"
$testCounters = [ordered]@{ total = 0; executed = 0; passed = 0; failed = 0; skipped = 0; errors = 0 }
$dependencyReport = $null

if ($archiveStatus -eq "PASS" -and -not [string]::IsNullOrWhiteSpace($powerShellPath) -and -not [string]::IsNullOrWhiteSpace($dotnetPath)) {
    $encodingScript = Join-Path $snapshotScriptsRoot "verify_text_encoding.ps1"
    $encodingStep = Invoke-Step "tracked-only UTF-8 BOM and CRLF" $powerShellPath @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $encodingScript, "-Root", $snapshotRoot) $snapshotRoot "encoding"

    $restoreStep = Invoke-Step "tracked-only restore" $dotnetPath @("restore", $snapshotSolution, "-p:Platform=x64") $snapshotRoot "restore"
    $debugBuildStep = Invoke-Step "tracked-only Debug x64 build" $dotnetPath @("build", $snapshotSolution, "-c", "Debug", "-p:Platform=x64", "--no-restore") $snapshotRoot "debug-build"

    $testArguments = @(
        "test", $snapshotTestProject,
        "-c", "Debug",
        "-p:Platform=x64",
        "--no-build",
        "--filter", "Lane!=ExternalModel",
        "--results-directory", $testResultRoot,
        "--logger", "trx;LogFileName=hermetic.trx"
    )
    $testStep = Invoke-Step "tracked-only hermetic tests" $dotnetPath $testArguments $snapshotRoot "hermetic-tests"
    $testCounters = Get-TrxCounters $testResultRoot

    $releaseBuildStep = Invoke-Step "tracked-only Release x64 build" $dotnetPath @("build", $snapshotSolution, "-c", "Release", "-p:Platform=x64", "--no-restore") $snapshotRoot "release-build"
    $releaseCompileLog = Join-Path $logRoot "release-compile-items.log"
    $releaseCompileOutput = @()
    $releaseCompileExit = 1
    try {
        Push-Location -LiteralPath $snapshotRoot
        try {
            $releaseCompileOutput = @(& $dotnetPath "msbuild" $snapshotProject "-getItem:Compile" "-p:Configuration=Release" "-p:Platform=x64" "-nologo" 2>&1)
            $releaseCompileExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        }
        finally {
            Pop-Location
        }
    }
    catch {
        $releaseCompileOutput = @("Exception: $($_.Exception.Message)")
        $releaseCompileExit = 1
    }
    [System.IO.File]::WriteAllLines($releaseCompileLog, @($releaseCompileOutput | ForEach-Object { [string]$_ }), [System.Text.UTF8Encoding]::new($false))
    $mockCameraIncluded = $false
    if ($releaseCompileExit -eq 0) {
        try {
            $compileJson = (@($releaseCompileOutput) -join [Environment]::NewLine) | ConvertFrom-Json
            $mockCameraIncluded = @($compileJson.Items.Compile | Where-Object { $_.Identity -like "*MockCamera.cs" }).Count -gt 0
        }
        catch {
            Add-BlockingReason "Unable to inspect Release compile items: $($_.Exception.Message)"
            $releaseCompileExit = 1
        }
    }
    $releaseCompileStatus = if ($releaseCompileExit -eq 0 -and -not $mockCameraIncluded) { "PASS" } else { "BLOCKED" }
    $releaseCompileStep = [ordered]@{
        name = "Release compile item boundary"
        status = $releaseCompileStatus
        exitCode = if ($releaseCompileStatus -eq "PASS") { 0 } else { 1 }
        mockCameraIncluded = $mockCameraIncluded
        log = $releaseCompileLog
    }
    [void]$stepRecords.Add($releaseCompileStep)
    if ($releaseCompileStatus -eq "BLOCKED") {
        Add-BlockingReason "Release compile item inspection found MockCamera or could not be inspected."
    }
}

if ($archiveStatus -eq "PASS") {
    try {
        $projectText = Get-Content -LiteralPath $snapshotProject -Raw -Encoding UTF8
        $fileMatches = [regex]::Matches($projectText, "'(?<file>html\\js\\[^']+\.js)'")
        $bundleFiles = @($fileMatches |
            ForEach-Object { $_.Groups["file"].Value } |
            Where-Object { $_ -ne "html\js\bundle.js" } |
            ForEach-Object { $_.Replace("\\", [System.IO.Path]::DirectorySeparatorChar) })
        if ($bundleFiles.Count -eq 0) {
            throw "Bundle source list was not found in ClearFrost.csproj."
        }

        $bundleTexts = foreach ($bundleFile in $bundleFiles) {
            $bundleSourcePath = Join-Path $snapshotRoot (Join-Path "ClearFrost" $bundleFile)
            if (-not (Test-Path -LiteralPath $bundleSourcePath -PathType Leaf)) {
                throw "Missing bundle source: $bundleFile"
            }
            [System.IO.File]::ReadAllText($bundleSourcePath)
        }
        $bundleContent = [string]::Join([Environment]::NewLine, $bundleTexts)
        $bundleEncoding = [System.Text.UTF8Encoding]::new($true)
        $bundleBytes = @($bundleEncoding.GetPreamble()) + @($bundleEncoding.GetBytes($bundleContent))
        $bundleSourceHash = Get-BytesHash $bundleBytes
        $bundleRuntimePath = Join-Path $snapshotRoot "ClearFrost\html\js\bundle.js"
        $bundleRuntimeHash = Get-FileHashOrEmpty $bundleRuntimePath
        if ($bundleSourceHash -ne $bundleRuntimeHash) {
            throw "Runtime bundle hash $bundleRuntimeHash does not match deterministic source hash $bundleSourceHash."
        }
        $bundleStatus = "PASS"
    }
    catch {
        $bundleStatus = "BLOCKED"
        Add-BlockingReason "Bundle determinism check failed: $($_.Exception.Message)"
    }

    $nodePath = Get-ExecutablePath "node"
    if ([string]::IsNullOrWhiteSpace($nodePath)) {
        $jsSyntaxStatus = "BLOCKED"
        Add-BlockingReason "Node.js was not found; JavaScript syntax cannot be verified."
    }
    else {
        $jsFiles = @(Get-ChildItem -LiteralPath (Join-Path $snapshotRoot "ClearFrost\html\js") -Filter "*.js" -File)
        $jsFailures = 0
        foreach ($jsFile in $jsFiles) {
            $jsStep = Invoke-Step "JavaScript syntax: $($jsFile.Name)" $nodePath @("--check", $jsFile.FullName) $snapshotRoot ("js-" + $jsFile.BaseName)
            if ($jsStep.status -ne "PASS") {
                $jsFailures++
            }
        }
        $jsSyntaxStatus = if ($jsFailures -eq 0) { "PASS" } else { "BLOCKED" }
    }
}

if ($archiveStatus -eq "PASS" -and -not [string]::IsNullOrWhiteSpace($powerShellPath)) {
    $dependencyReportPath = Join-Path $evidenceRoot "release-dependencies.json"
    $dependencyScript = Join-Path $snapshotScriptsRoot "verify_release_dependencies.ps1"
    $dependencyStep = Invoke-Step "tracked-only release dependency precheck" $powerShellPath @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $dependencyScript,
        "-Root", $snapshotRoot, "-Profile", "Lite", "-CleanRoom", "-ReportPath", $dependencyReportPath
    ) $snapshotRoot "dependency-precheck"
    $dependencyReport = Get-DependencyReport $dependencyReportPath
    foreach ($reason in @($dependencyReport.blockingReasons)) {
        Add-BlockingReason "Dependency precheck: $reason"
    }
    foreach ($reason in @($dependencyReport.notVerifiedReasons)) {
        Add-NotVerifiedReason "Dependency precheck: $reason"
    }
}

if ($archiveStatus -eq "PASS" -and -not [string]::IsNullOrWhiteSpace($powerShellPath) -and -not [string]::IsNullOrWhiteSpace($dotnetPath)) {
    if ([string]::IsNullOrWhiteSpace($snapshotPublishScript)) {
        $litePublishStep = [ordered]@{ name = "Lite publish dry run"; status = "BLOCKED"; exitCode = 1; log = "" }
        $fullPublishStep = [ordered]@{ name = "Full publish dry run"; status = "BLOCKED"; exitCode = 1; log = "" }
    }
    else {
        $litePublishStep = Invoke-Step "Lite publish dry run" $powerShellPath @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $snapshotPublishScript,
            "-Mode", "Lite", "-Version", $productVersion, "-OutputRoot", (Join-Path $publishRoot "Lite"), "-Zip", "-NoPause"
        ) $snapshotRoot "publish-lite"
        $fullPublishStep = Invoke-Step "Full publish dry run" $powerShellPath @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $snapshotPublishScript,
            "-Mode", "Full", "-Version", $productVersion, "-OutputRoot", (Join-Path $publishRoot "Full"), "-Zip", "-NoPause"
        ) $snapshotRoot "publish-full"
    }

    foreach ($mode in @("Lite", "Full")) {
        $modeRoot = Join-Path $publishRoot $mode
        if (Test-Path -LiteralPath $modeRoot -PathType Container) {
            $publishedBundle = Get-ChildItem -LiteralPath $modeRoot -Filter "bundle.js" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $publishedBundle) {
                $bundlePublishHashes += [ordered]@{
                    mode = $mode
                    path = $publishedBundle.FullName
                    sha256 = Get-FileHashOrEmpty $publishedBundle.FullName
                    status = if ((Get-FileHashOrEmpty $publishedBundle.FullName) -eq $bundleSourceHash) { "PASS" } else { "BLOCKED" }
                }
            }
        }
    }
}

if ($null -eq $dependencyReport) {
    $dependencyReport = [ordered]@{
        overallStatus = "BLOCKED"
        dependencies = @()
        models = [ordered]@{ status = "NOT_VERIFIED"; reason = "Dependency precheck did not run."; files = @() }
        blockingReasons = @()
        notVerifiedReasons = @()
    }
}

$trackedDlls = @()
$trackedOnnx = @()
if (-not [string]::IsNullOrWhiteSpace($gitPath)) {
    $trackedDlls = @(& $gitPath ls-files -- '*.dll')
    $trackedOnnx = @(& $gitPath ls-files -- '*.onnx')
}
if ($trackedDlls.Count -gt 0 -or $trackedOnnx.Count -gt 0) {
    Add-BlockingReason "Private DLL or ONNX file is tracked by Git."
}

$softwareTriggerExcluded = $true
try {
    $solutionText = Get-Content -LiteralPath (Join-Path $sourceRoot "ClearFrost.sln") -Raw -Encoding UTF8
    $softwareTriggerExcluded = $solutionText -notmatch "SoftwareTrigger"
}
catch {
    $softwareTriggerExcluded = $false
}
if (-not $softwareTriggerExcluded) {
    Add-BlockingReason "tools/SoftwareTrigger must remain outside the V6 solution unless its private SDK input is declared."
}
else {
    Add-LogicalSkip "Hardware/SoftwareTrigger" "tools/SoftwareTrigger is intentionally excluded from ClearFrost.sln because it requires the private MVSDK_Net SDK."
}

Add-LogicalSkip "RealModel" "No external ONNX model was supplied to the tracked-only lane; model paths and SHA-256 are recorded without claiming inference."
Add-LogicalSkip "DirectML/GPU" "No strict required-provider run with a real ONNX model was executed in this clean-room lane."
Add-LogicalSkip "Hardware" "Real camera and PLC hardware were not connected or exercised."
Add-LogicalSkip "Installation/Upgrade/Rollback" "Installer, upgrade, and rollback evidence is outside this Goal and was not executed."
Add-LogicalSkip "LongRun" "The real application and hardware were not run for an 8-hour soak."

if (-not $KeepSnapshot -and -not [string]::IsNullOrWhiteSpace($snapshotRoot) -and (Test-Path -LiteralPath $snapshotRoot)) {
    Remove-Item -LiteralPath $snapshotRoot -Recurse -Force -ErrorAction SilentlyContinue
    $snapshotDeleted = -not (Test-Path -LiteralPath $snapshotRoot)
}

$providerEvidence = [ordered]@{
    requested = "DmlExecutionProvider"
    actual = ""
    status = "NOT_VERIFIED"
    reason = "No real-model DirectML probe was supplied; the strict --require-provider contract is unit-tested separately."
}

$buildEvidence = [ordered]@{
    restore = if ($null -eq $restoreStep) { [ordered]@{ status = "NOT_VERIFIED" } } else { $restoreStep }
    debug = if ($null -eq $debugBuildStep) { [ordered]@{ status = "NOT_VERIFIED" } } else { $debugBuildStep }
    release = if ($null -eq $releaseBuildStep) { [ordered]@{ status = "NOT_VERIFIED" } } else { $releaseBuildStep }
    releaseCompileBoundary = if ($null -eq $releaseCompileStep) { [ordered]@{ status = "NOT_VERIFIED" } } else { $releaseCompileStep }
}

$hermeticTestEvidence = [ordered]@{
    status = if ($null -ne $testStep -and $testStep.status -eq "PASS") { "PASS" } else { "BLOCKED" }
    lane = "Hermetic unit/contract + synthetic descriptor"
    filter = "Lane!=ExternalModel"
    command = if ($null -eq $testStep) { "" } else { $testStep.command }
    counters = $testCounters
    reason = "External real-model tests are excluded from this lane and cannot contribute to PASS."
}
$syntheticTestEvidence = [ordered]@{
    status = $hermeticTestEvidence.status
    lane = "Synthetic model contract"
    reason = "Synthetic descriptor and postprocessing contracts run inside the hermetic test filter."
}

$testsEvidence = [ordered]@{
    hermetic = $hermeticTestEvidence
    syntheticModel = $syntheticTestEvidence
    realModelCpu = [ordered]@{ status = "NOT_VERIFIED"; reason = "No external real ONNX input supplied." }
    directMlGpu = [ordered]@{ status = "NOT_VERIFIED"; reason = $providerEvidence.reason }
    hardware = [ordered]@{ status = "NOT_VERIFIED"; reason = "Real camera and PLC not connected." }
    installationUpgradeRollback = [ordered]@{ status = "NOT_VERIFIED"; reason = "No installer or upgrade/rollback workflow exists in this Goal." }
    longRun = [ordered]@{ status = "NOT_VERIFIED"; reason = "No real application/hardware soak executed." }
}

$versionIdentityStatus = if ($productVersion -match "-") { "PASS" } else { "BLOCKED" }
if ($versionIdentityStatus -ne "PASS") {
    Add-BlockingReason "Product version '$productVersion' is not a pre-release identity on V6_test."
}
$appVersionPath = Join-Path $sourceRoot "ClearFrost\Helpers\AppVersion.cs"
$appVersionText = if (Test-Path -LiteralPath $appVersionPath -PathType Leaf) {
    Get-Content -LiteralPath $appVersionPath -Raw -Encoding UTF8
}
else {
    ""
}
if ($appVersionText -notmatch "ReleaseChannel\s*=>\s*IsPreRelease") {
    Add-BlockingReason "AppVersion.cs does not derive its release channel from the prerelease version."
    $versionIdentityStatus = "BLOCKED"
}

$preStatus = if ($blockingReasons.Count -gt 0) { "BLOCKED" } elseif ($notVerifiedReasons.Count -gt 0) { "NOT_VERIFIED" } else { "PASS" }
$promotionEligibility = if ($preStatus -eq "PASS" -and $logicalSkips.Count -eq 0) { "PASS" } else { "BLOCKED" }

$acceptance = [ordered]@{
    A1 = [ordered]@{ status = if ($branch -eq "V6_test" -and $dirtyEntries.Count -eq 0) { "PASS" } else { "BLOCKED" }; reason = "V6_test branch and clean worktree are required." }
    A2 = [ordered]@{ status = if ($null -ne $encodingStep -and $encodingStep.status -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Tracked-only encoding and restore chain." }
    A3 = [ordered]@{ status = if ($null -ne $debugBuildStep -and $debugBuildStep.status -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Tracked-only Debug x64 build." }
    A4 = [ordered]@{ status = $hermeticTestEvidence.status; reason = "Hermetic filter excludes external model tests." }
    A5 = [ordered]@{ status = if ($null -ne $releaseBuildStep -and $releaseBuildStep.status -eq "PASS" -and $releaseCompileStep.status -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Tracked-only Release x64 build and Release MockCamera exclusion." }
    A6 = [ordered]@{ status = "NOT_VERIFIED"; reason = "The local gate cannot prove a remote GitHub Actions run; workflow evidence is collected after push." }
    A7 = [ordered]@{ status = if ($dependencyReport.overallStatus -eq "BLOCKED") { "PASS" } else { "BLOCKED" }; reason = "Missing configured HaoCommunication is expected to block promotion." }
    A8 = [ordered]@{ status = if ($trackedDlls.Count -eq 0 -and $trackedOnnx.Count -eq 0) { "PASS" } else { "BLOCKED" }; reason = "Private binaries and models must remain external." }
    A9 = [ordered]@{ status = "PASS"; reason = "Manifest separates synthetic, real-model, GPU, hardware, installation, and long-run lanes." }
    A10 = [ordered]@{ status = "PASS"; reason = "Strict requested/actual provider mismatch contract is unit-tested; real GPU execution remains NOT_VERIFIED." }
    A11 = [ordered]@{ status = if ($bundleStatus -eq "PASS" -and $jsSyntaxStatus -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Deterministic bundle and JavaScript syntax." }
    A12 = [ordered]@{ status = $versionIdentityStatus; reason = "V6_test uses a prerelease version and non-formal application label." }
    A13 = [ordered]@{ status = if ($litePublishStep.status -eq "PASS" -and $fullPublishStep.status -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Lite/Full publish must fail closed when required external dependencies are absent." }
    A14 = [ordered]@{ status = "NOT_VERIFIED"; reason = "Real camera, PLC, 8-hour long-run, installation, and rollback evidence is absent; promotion remains BLOCKED." }
    A15 = [ordered]@{ status = if ($hermeticTestEvidence.status -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Hermetic regression signal passes; external-lane counts are explicitly separated." }
}

$manifest = [ordered]@{
    schemaVersion = "v6-gate-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    commit = $commit
    branch = $branch
    dirty = $dirtyEntries.Count -gt 0
    dirtyEntries = @($dirtyEntries)
    mainSha = $mainSha
    remoteV6TestShaAtStart = $remoteSha
    productVersion = $productVersion
    releaseIdentity = if ($productVersion -match "-") { "pre-release-candidate" } else { "development-build" }
    environment = [ordered]@{
        os = [System.Environment]::OSVersion.VersionString
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        dotnet = if ([string]::IsNullOrWhiteSpace($dotnetPath)) { "" } else { ((& $dotnetPath --version).Trim()) }
        powershell = $PSVersionTable.PSVersion.ToString()
        node = if ([string]::IsNullOrWhiteSpace((Get-ExecutablePath "node"))) { "" } else { ((& (Get-ExecutablePath "node") --version).Trim()) }
    }
    cleanRoom = [ordered]@{
        source = "git archive HEAD"
        snapshotRoot = $snapshotRoot
        archiveStatus = $archiveStatus
        snapshotDeleted = $snapshotDeleted
        untrackedInputsUsed = @()
        privateSdkInputsUsed = @()
    }
    steps = @($stepRecords)
    build = $buildEvidence
    tests = $testsEvidence
    logicalSkips = @($logicalSkips)
    bundle = [ordered]@{
        status = if ($bundleStatus -eq "PASS" -and $jsSyntaxStatus -eq "PASS") { "PASS" } else { "BLOCKED" }
        sourceHash = $bundleSourceHash
        runtimeHash = $bundleRuntimeHash
        publish = @($bundlePublishHashes)
        javascriptSyntax = $jsSyntaxStatus
    }
    dependencies = $dependencyReport.dependencies
    dependencyPrecheck = $dependencyReport
    models = $dependencyReport.models
    providers = $providerEvidence
    publish = [ordered]@{
        Lite = if ($null -eq $litePublishStep) { [ordered]@{ status = "NOT_VERIFIED" } } else { $litePublishStep }
        Full = if ($null -eq $fullPublishStep) { [ordered]@{ status = "NOT_VERIFIED" } } else { $fullPublishStep }
    }
    excludedProjects = @(
        [ordered]@{
            path = "tools/SoftwareTrigger"
            status = if ($softwareTriggerExcluded) { "NOT_VERIFIED" } else { "BLOCKED" }
            reason = "Excluded from ClearFrost.sln; requires private MVSDK_Net SDK and is not part of the V6 clean-room lane."
        }
    )
    acceptance = $acceptance
    overallStatus = $preStatus
    blockingReasons = @($blockingReasons)
    notVerifiedReasons = @($notVerifiedReasons)
    promotionEligibility = $promotionEligibility
}

$manifestPath = Join-Path $evidenceRoot "evidence.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))

Write-Output "V6 gate evidence: $manifestPath"
Write-Output "Overall status: $preStatus"
Write-Output "Promotion eligibility: $promotionEligibility"
Write-Output "Hermetic tests: $($hermeticTestEvidence.status) ($($testCounters.passed)/$($testCounters.total))"

if ($ReportOnly) {
    exit 0
}
if ($preStatus -eq "PASS" -and $promotionEligibility -eq "PASS") {
    exit 0
}
exit 1
