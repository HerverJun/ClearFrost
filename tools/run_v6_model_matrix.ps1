param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$ManifestPath = "",
    [string]$OutputPath = "",
    [int]$WarmupIterations = 100,
    [int]$Iterations = 1000,
    [string]$DotnetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$rootPath = [System.IO.Path]::GetFullPath($Root)
$evidenceRoot = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $rootPath "artifacts\v6-g2\models"
}
else {
    [System.IO.Path]::GetFullPath((Split-Path -Parent $OutputPath))
}
$reportPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $evidenceRoot "model-matrix.json"
}
else {
    [System.IO.Path]::GetFullPath($OutputPath)
}
$logRoot = Join-Path $evidenceRoot "logs"
$probeRoot = Join-Path $evidenceRoot "probes"
$profileRoot = Join-Path $evidenceRoot "profiles"
$inputReportPath = Join-Path $evidenceRoot "external-inputs.json"
$negativeRoot = Join-Path $evidenceRoot "negative"
$probeProjectPath = Join-Path $rootPath "tools\ClearFrost.YoloProbe\ClearFrost.YoloProbe.csproj"
$probeDllPath = Join-Path $rootPath "tools\ClearFrost.YoloProbe\bin\x64\Debug\net8.0-windows10.0.17763.0\ClearFrost.YoloProbe.dll"
New-Item -ItemType Directory -Force -Path $evidenceRoot, $logRoot, $probeRoot, $profileRoot, $negativeRoot | Out-Null

$blockingReasons = [System.Collections.Generic.List[string]]::new()
$notVerifiedReasons = [System.Collections.Generic.List[string]]::new()
$laneReports = [System.Collections.Generic.List[object]]::new()
$probeBuild = [ordered]@{
    status = "NOT_VERIFIED"
    project = $probeProjectPath
    output = $probeDllPath
    reason = "The probe is built lazily when a real model lane is available."
}

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

function Invoke-PowerShellScript([string]$ScriptPath, [string[]]$Arguments) {
    $shell = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -eq $shell) {
        $shell = Get-Command powershell.exe -ErrorAction Stop
    }

    $output = @(& $shell.Source -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1)
    return [pscustomobject]@{
        exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        output = @($output | ForEach-Object { [string]$_ })
    }
}

function Invoke-Probe([string]$Name, [string[]]$Arguments, [string]$OutputPath, [string]$ProfilePath) {
    $logPath = Join-Path $logRoot "$Name.log"
    $previousProfileRoot = [string]$env:CLEARFROST_DML_PROFILE_ROOT
    $env:CLEARFROST_DML_PROFILE_ROOT = $ProfilePath
    try {
        $probeArguments = @($probeDllPath) + @($Arguments)
        $lines = @(& $DotnetPath @probeArguments 2>&1)
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        [System.IO.File]::WriteAllLines($logPath, @($lines | ForEach-Object { [string]$_ }), [System.Text.UTF8Encoding]::new($false))
        $json = $null
        if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
            try {
                $json = Get-Content -LiteralPath $OutputPath -Raw -Encoding UTF8 | ConvertFrom-Json
            }
            catch {
                Add-BlockingReason "$Name produced invalid JSON: $($_.Exception.Message)"
            }
        }
        return [pscustomobject]@{
            name = $Name
            status = if ($exitCode -eq 0) { "PASS" } else { "BLOCKED" }
            exitCode = $exitCode
            command = ($DotnetPath + " " + ($probeArguments -join " "))
            log = $logPath
            reportPath = $OutputPath
            report = $json
        }
    }
    finally {
        $env:CLEARFROST_DML_PROFILE_ROOT = $previousProfileRoot
    }
}

function Ensure-ProbeBuilt {
    if (Test-Path -LiteralPath $probeDllPath -PathType Leaf) {
        $probeBuild.status = "PASS"
        $probeBuild.reason = "The x64 Debug probe assembly already exists."
        return $true
    }

    $logPath = Join-Path $logRoot "probe-build.log"
    try {
        $lines = @(& $DotnetPath "build" $probeProjectPath "-c" "Debug" "-p:Platform=x64" 2>&1)
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        [System.IO.File]::WriteAllLines($logPath, @($lines | ForEach-Object { [string]$_ }), [System.Text.UTF8Encoding]::new($false))
        if ($exitCode -eq 0 -and (Test-Path -LiteralPath $probeDllPath -PathType Leaf)) {
            $probeBuild.status = "PASS"
            $probeBuild.exitCode = $exitCode
            $probeBuild.log = $logPath
            $probeBuild.reason = "The x64 Debug probe was built before direct DLL invocation."
            return $true
        }

        $probeBuild.status = "BLOCKED"
        $probeBuild.exitCode = $exitCode
        $probeBuild.log = $logPath
        $probeBuild.reason = "The x64 Debug probe assembly was not produced."
        Add-BlockingReason "Yolo probe build failed; see $logPath."
        return $false
    }
    catch {
        $probeBuild.status = "BLOCKED"
        $probeBuild.log = $logPath
        $probeBuild.reason = $_.Exception.Message
        Add-BlockingReason "Yolo probe build threw: $($_.Exception.Message)"
        return $false
    }
}

function Get-ProfileEvidence([string]$ProfilePath) {
    $files = @()
    if (Test-Path -LiteralPath $ProfilePath -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $ProfilePath -File -Recurse -ErrorAction SilentlyContinue)
    }
    return [ordered]@{
        root = $ProfilePath
        remainingFileCount = $files.Count
        remainingFiles = @($files | ForEach-Object {
            [ordered]@{
                path = $_.FullName
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            }
        })
        status = if ($files.Count -eq 0) { "PASS" } else { "BLOCKED" }
    }
}

function Get-ModelStatus([object]$InputModel) {
    if ($null -eq $InputModel) {
        return "NOT_VERIFIED"
    }
    return (Get-String $InputModel "status")
}

function Get-ModelRecord([object]$InputModel, [string]$Lane) {
    return [ordered]@{
        lane = $Lane
        status = Get-ModelStatus $InputModel
        identity = if ($null -eq $InputModel) { [ordered]@{} } else { $InputModel }
        cpu = [ordered]@{ status = "NOT_VERIFIED"; reason = "CPU lane was not run." }
        dml = [ordered]@{ status = "NOT_VERIFIED"; reason = "Strict DML lane was not run." }
        sessionLifecycle = [ordered]@{ status = "NOT_VERIFIED"; attempts = 0; reason = "No real model input was available." }
        negativeContracts = [ordered]@{ status = "NOT_VERIFIED"; cases = @() }
    }
}

$validatorScript = Join-Path $rootPath "tools\verify_v6_external_inputs.ps1"
$validatorArgs = @("-Root", $rootPath, "-ReportPath", $inputReportPath)
if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $validatorArgs += @("-ManifestPath", $ManifestPath)
}
$inputValidation = Invoke-PowerShellScript $validatorScript $validatorArgs
$inputReport = $null
if (Test-Path -LiteralPath $inputReportPath -PathType Leaf) {
    $inputReport = Get-Content -LiteralPath $inputReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    Add-BlockingReason "External input validator did not produce a report."
}

$lanes = @("Detect", "Classification", "Segmentation", "OBB", "Pose")
foreach ($lane in $lanes) {
    $inputModel = if ($null -eq $inputReport) { $null } else { @($inputReport.models | Where-Object { (Get-String $_ "lane") -eq $lane }) | Select-Object -First 1 }
    $record = Get-ModelRecord $inputModel $lane
    if ((Get-ModelStatus $inputModel) -ne "PASS") {
        $reason = if ($null -eq $inputModel) { "No explicit model contract was supplied." } else { Get-String $inputModel "reason" }
        Add-NotVerifiedReason "${lane}: $reason"
        $record.cpu = [ordered]@{ status = "NOT_VERIFIED"; reason = $reason }
        $record.dml = [ordered]@{ status = "NOT_VERIFIED"; reason = $reason }
        [void]$laneReports.Add($record)
        continue
    }

    $task = (Get-String $inputModel "task").Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($task)) {
        $task = $lane.ToLowerInvariant()
    }
    $modelPath = Get-String $inputModel "path"
    $imagePath = Get-String $inputModel.validationImage "path"
    if ((Get-String $inputModel.validationImage "status") -ne "PASS" -or
        [string]::IsNullOrWhiteSpace($imagePath)) {
        $reason = "A real validation image is required; synthetic benchmark input is not permitted."
        Add-NotVerifiedReason "${lane}: $reason"
        $record.cpu = [ordered]@{ status = "NOT_VERIFIED"; reason = $reason }
        $record.dml = [ordered]@{ status = "NOT_VERIFIED"; reason = $reason }
        $record.sessionLifecycle = [ordered]@{ status = "NOT_VERIFIED"; attempts = 0; reason = $reason }
        [void]$laneReports.Add($record)
        continue
    }

    if (-not (Ensure-ProbeBuilt)) {
        $record.cpu = [ordered]@{ status = "BLOCKED"; reason = $probeBuild.reason; build = $probeBuild }
        $record.dml = [ordered]@{ status = "BLOCKED"; reason = $probeBuild.reason; build = $probeBuild }
        [void]$laneReports.Add($record)
        continue
    }

    $baseArgs = @(
        "--model", $modelPath,
        "--benchmark",
        "--warmup", ([string]$WarmupIterations),
        "--iterations", ([string]$Iterations),
        "--task", $task
    )
    if (-not [string]::IsNullOrWhiteSpace($imagePath)) {
        $baseArgs += @("--image", $imagePath)
    }

    $cpuOutputPath = Join-Path $probeRoot "$($lane.ToLowerInvariant())-cpu.json"
    $cpuResult = Invoke-Probe "$($lane.ToLowerInvariant())-cpu" ($baseArgs + @("--out", $cpuOutputPath)) $cpuOutputPath (Join-Path $profileRoot "$($lane.ToLowerInvariant())-cpu")
    $cpuReport = $cpuResult.report
    $cpuStatus = if ($cpuResult.exitCode -eq 0 -and $null -ne $cpuReport -and
        (Get-String $cpuReport.Benchmark "ExecutionProvider") -eq "CPUExecutionProvider" -and
        [bool]$cpuReport.Benchmark.ResultStructureValid) { "PASS" } else { "BLOCKED" }
    $cpuReason = if ($cpuStatus -eq "PASS") {
        "Real model CPU inference completed with valid output structure."
    }
    elseif ($null -eq $cpuReport) {
        "Real model CPU probe did not produce a report."
    }
    else {
        "CPU probe failed or did not report CPUExecutionProvider/valid results."
    }
    $record.cpu = [ordered]@{
        status = $cpuStatus
        exitCode = $cpuResult.exitCode
        log = $cpuResult.log
        report = $cpuReport
        reason = $cpuReason
    }
    if ($cpuStatus -ne "PASS") {
        Add-BlockingReason "$lane CPU real-model lane failed: $cpuReason"
    }

    $profilePath = Join-Path $profileRoot "$($lane.ToLowerInvariant())-dml"
    New-Item -ItemType Directory -Force -Path $profilePath | Out-Null
    $dmlOutputPath = Join-Path $probeRoot "$($lane.ToLowerInvariant())-dml.json"
    $dmlArgs = $baseArgs + @("--gpu", "--require-provider", "DmlExecutionProvider", "--out", $dmlOutputPath)
    $dmlResult = Invoke-Probe "$($lane.ToLowerInvariant())-dml" $dmlArgs $dmlOutputPath $profilePath
    $dmlReport = $dmlResult.report
    $profileEvidence = Get-ProfileEvidence $profilePath
    $dmlProvider = if ($null -eq $dmlReport) { "" } else { Get-String $dmlReport.Benchmark "ExecutionProvider" }
    $dmlStatus = if ($dmlResult.exitCode -eq 0 -and $null -ne $dmlReport -and
        $dmlProvider -eq "DmlExecutionProvider" -and [bool]$dmlReport.Benchmark.GpuActive -and
        [bool]$dmlReport.Benchmark.ResultStructureValid -and $profileEvidence.status -eq "PASS") { "PASS" } else { "BLOCKED" }
    $dmlReason = if ($dmlStatus -eq "PASS") {
        "Strict DML inference completed with actual DmlExecutionProvider and no residual profile."
    }
    elseif ($dmlProvider -eq "CPUExecutionProvider") {
        "DML was requested but actual execution was CPU; strict DML lane is BLOCKED."
    }
    elseif ($profileEvidence.status -ne "PASS") {
        "DML profile cleanup left residual files."
    }
    else {
        "DML initialization or strict provider validation failed."
    }
    $record.dml = [ordered]@{
        status = $dmlStatus
        requestedProvider = "DmlExecutionProvider"
        actualProvider = $dmlProvider
        exitCode = $dmlResult.exitCode
        log = $dmlResult.log
        report = $dmlReport
        profile = $profileEvidence
        reason = $dmlReason
    }
    if ($dmlStatus -eq "BLOCKED") {
        Add-BlockingReason "$lane DML: $dmlReason"
    }

    $sessionAttempts = 3
    $sessionResults = [System.Collections.Generic.List[object]]::new()
    for ($attempt = 1; $attempt -le $sessionAttempts; $attempt++) {
        $sessionOutputPath = Join-Path $probeRoot "$($lane.ToLowerInvariant())-cpu-session-$attempt.json"
        $sessionResult = Invoke-Probe "$($lane.ToLowerInvariant())-cpu-session-$attempt" @(
            "--model", $modelPath, "--benchmark", "--warmup", "1", "--iterations", "1", "--task", $task, "--out", $sessionOutputPath
        ) $sessionOutputPath (Join-Path $profileRoot "$($lane.ToLowerInvariant())-cpu-session-$attempt")
        [void]$sessionResults.Add([ordered]@{ attempt = $attempt; status = $sessionResult.status; exitCode = $sessionResult.exitCode; log = $sessionResult.log })
    }
    $sessionStatus = if (@($sessionResults | Where-Object { $_.status -ne "PASS" }).Count -eq 0) { "PASS" } else { "BLOCKED" }
    $record.sessionLifecycle = [ordered]@{ status = $sessionStatus; attempts = $sessionAttempts; results = @($sessionResults); reason = "Repeated real Session creation and disposal." }
    if ($sessionStatus -ne "PASS") {
        Add-BlockingReason "$lane repeated Session lifecycle failed."
    }

    $negativeCases = [System.Collections.Generic.List[object]]::new()
    $missingPath = Join-Path $negativeRoot "$($lane.ToLowerInvariant())-missing.onnx"
    $missing = Invoke-Probe "$($lane.ToLowerInvariant())-negative-missing" @("--model", $missingPath) (Join-Path $probeRoot "$($lane.ToLowerInvariant())-negative-missing.json") (Join-Path $profileRoot "$($lane.ToLowerInvariant())-negative-missing")
    [void]$negativeCases.Add([ordered]@{ name = "missing-model"; status = if ($missing.exitCode -ne 0) { "PASS" } else { "BLOCKED" }; exitCode = $missing.exitCode; log = $missing.log })
    $invalidPath = Join-Path $negativeRoot "$($lane.ToLowerInvariant())-invalid.onnx"
    [System.IO.File]::WriteAllBytes($invalidPath, [byte[]](0x43, 0x6C, 0x65, 0x61, 0x72, 0x46, 0x72, 0x6F, 0x73, 0x74))
    try {
        $invalid = Invoke-Probe "$($lane.ToLowerInvariant())-negative-invalid" @("--model", $invalidPath) (Join-Path $probeRoot "$($lane.ToLowerInvariant())-negative-invalid.json") (Join-Path $profileRoot "$($lane.ToLowerInvariant())-negative-invalid")
        [void]$negativeCases.Add([ordered]@{ name = "invalid-model"; status = if ($invalid.exitCode -ne 0) { "PASS" } else { "BLOCKED" }; exitCode = $invalid.exitCode; log = $invalid.log })
    }
    finally {
        Remove-Item -LiteralPath $invalidPath -Force -ErrorAction SilentlyContinue
    }
    $providerMismatchOutputPath = Join-Path $probeRoot "$($lane.ToLowerInvariant())-negative-provider-mismatch.json"
    $providerMismatch = Invoke-Probe "$($lane.ToLowerInvariant())-negative-provider-mismatch" @(
        "--model", $modelPath,
        "--image", $imagePath,
        "--benchmark",
        "--warmup", "0",
        "--iterations", "1",
        "--task", $task,
        "--require-provider", "DmlExecutionProvider",
        "--out", $providerMismatchOutputPath
    ) $providerMismatchOutputPath (Join-Path $profileRoot "$($lane.ToLowerInvariant())-negative-provider-mismatch")
    [void]$negativeCases.Add([ordered]@{ name = "requested-dml-actual-cpu"; status = if ($providerMismatch.exitCode -ne 0) { "PASS" } else { "BLOCKED" }; exitCode = $providerMismatch.exitCode; log = $providerMismatch.log })
    $negativeStatus = if (@($negativeCases | Where-Object { $_.status -ne "PASS" }).Count -eq 0) { "PASS" } else { "BLOCKED" }
    $record.negativeContracts = [ordered]@{ status = $negativeStatus; cases = @($negativeCases); reason = "Missing model, invalid model, and requested-provider mismatch are fail-closed." }
    if ($negativeStatus -ne "PASS") {
        Add-BlockingReason "$lane negative model/provider contracts failed."
    }

    [void]$laneReports.Add($record)
}

$negativeStatuses = @($laneReports | ForEach-Object { $_.negativeContracts.status } | Where-Object { $_ -ne "NOT_VERIFIED" })
$negativeContractStatus = if ($negativeStatuses.Count -gt 0 -and @($negativeStatuses | Where-Object { $_ -ne "PASS" }).Count -eq 0) { "PASS" } else { "NOT_VERIFIED" }
if ($null -ne $inputReport -and (Get-String $inputReport "status") -eq "BLOCKED") {
    Add-BlockingReason "External input contract required by the model matrix is BLOCKED."
}

$commitSha = ""
try {
    $commitSha = (git -C $rootPath rev-parse HEAD).Trim()
}
catch {
    $commitSha = ""
}
$status = if ($blockingReasons.Count -gt 0) { "BLOCKED" } elseif ($notVerifiedReasons.Count -gt 0) { "NOT_VERIFIED" } else { "PASS" }
$report = [ordered]@{
    schemaVersion = "v6-g2-model-matrix-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    commitSha = $commitSha
    root = $rootPath
    inputContract = $inputReport
    probeBuild = $probeBuild
    lanes = @($laneReports)
    negativeContract = [ordered]@{ status = $negativeContractStatus; reason = "Negative real-model and provider contracts are recorded per available lane." }
    status = $status
    blockingReasons = @($blockingReasons | Select-Object -Unique)
    notVerifiedReasons = @($notVerifiedReasons | Select-Object -Unique)
    runParameters = [ordered]@{
        warmupIterations = $WarmupIterations
        iterations = $Iterations
        strictDml = $true
        profileRoot = $profileRoot
    }
}
$json = $report | ConvertTo-Json -Depth 30
[System.IO.File]::WriteAllText($reportPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Output $json
if ($status -eq "BLOCKED") { exit 1 }
if ($status -eq "NOT_VERIFIED") { exit 2 }
exit 0
