param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$InputReportPath = "",
    [string]$ModelMatrixPath = "",
    [string]$MigrationEvidencePath = "",
    [string]$ReleaseEvidencePath = "",
    [string]$IsolationEvidencePath = "",
    [string]$SoakEvidencePath = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$rootPath = [System.IO.Path]::GetFullPath($Root)
$errors = [System.Collections.Generic.List[string]]::new()
$notVerifiedReasons = [System.Collections.Generic.List[string]]::new()
$artifactReports = [ordered]@{}

function Add-Error([string]$Message) {
    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        [void]$errors.Add($Message)
    }
}

function Add-NotVerified([string]$Message) {
    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        [void]$notVerifiedReasons.Add($Message)
    }
}

function Get-PropertyValue([object]$Object, [string]$Name) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return $null
    }

    return $Object.PSObject.Properties[$Name].Value
}

function Get-String([object]$Object, [string]$Name) {
    $value = Get-PropertyValue $Object $Name
    if ($null -eq $value) {
        return ""
    }

    return [string]$value
}

function Get-Array([object]$Object, [string]$Name) {
    $value = Get-PropertyValue $Object $Name
    if ($null -eq $value) {
        return @()
    }

    return @($value)
}

function Is-Status([string]$Status) {
    return $Status -in @("PASS", "NOT_VERIFIED", "BLOCKED")
}

function Is-Sha256([string]$Value) {
    return $Value -match '^[0-9A-Fa-f]{64}$'
}

function Is-Sha1([string]$Value) {
    return $Value -match '^[0-9A-Fa-f]{40}$'
}

function Test-Identity([object]$Report, [string]$Name) {
    if ($null -eq $Report) {
        return $null
    }

    $identity = Get-PropertyValue $Report "identity"
    if ($null -eq $identity) {
        Add-Error "$Name evidence must contain the unified identity object."
        return $null
    }

    if (-not (Is-Sha1 (Get-String $identity "commitSha"))) {
        Add-Error "$Name identity commitSha must be a complete 40-character SHA-1."
    }
    if ([string]::IsNullOrWhiteSpace((Get-String $identity "productVersion"))) {
        Add-Error "$Name identity productVersion is required."
    }
    if (-not (Is-Sha256 (Get-String $identity "machineIdentityDigest"))) {
        Add-Error "$Name identity machineIdentityDigest must be a SHA-256 digest."
    }
    if ([string]::IsNullOrWhiteSpace((Get-String $identity "provider"))) {
        Add-Error "$Name identity provider is required, including NOT_VERIFIED."
    }
    foreach ($field in @("inputManifestSha256", "detectModelSha256", "validationImageSha256", "dllSha256")) {
        $value = Get-String $identity $field
        if (-not [string]::IsNullOrWhiteSpace($value) -and -not (Is-Sha256 $value)) {
            Add-Error "$Name identity $field must be empty or a complete SHA-256."
        }
    }
    try {
        $started = [DateTimeOffset]::Parse((Get-String $identity "runStartedAtUtc"), [Globalization.CultureInfo]::InvariantCulture)
        $finished = [DateTimeOffset]::Parse((Get-String $identity "runFinishedAtUtc"), [Globalization.CultureInfo]::InvariantCulture)
        if ($finished -lt $started) {
            Add-Error "$Name identity runFinishedAtUtc precedes runStartedAtUtc."
        }
    }
    catch {
        Add-Error "$Name identity runStartedAtUtc and runFinishedAtUtc must be ISO-8601 timestamps."
    }
    $status = Get-String $Report "status"
    if ($status -eq "PASS") {
        foreach ($field in @("inputManifestSha256", "detectModelSha256", "validationImageSha256", "dllSha256")) {
            if (-not (Is-Sha256 (Get-String $identity $field))) {
                Add-Error "$Name PASS identity requires $field."
            }
        }
    }
    return $identity
}

function Test-IdentitySet([object[]]$Reports) {
    $identities = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $Reports) {
        $identity = Test-Identity $entry.Report $entry.Name
        if ($null -ne $identity) {
            [void]$identities.Add([pscustomobject]@{ name = $entry.Name; identity = $identity })
        }
    }
    if ($identities.Count -le 1) {
        return
    }

    $reference = $identities[0].identity
    foreach ($entry in $identities | Select-Object -Skip 1) {
        foreach ($field in @(
                "commitSha", "productVersion", "inputManifestSha256", "detectModelSha256",
                "validationImageSha256", "dllSha256", "provider", "machineIdentityDigest")) {
            $expected = Get-String $reference $field
            $actual = Get-String $entry.identity $field
            if (-not [string]::Equals($expected, $actual, [StringComparison]::OrdinalIgnoreCase)) {
                Add-Error "Unified evidence identity conflict: $($entry.Name) $field does not match $($identities[0].name)."
            }
        }
    }
}

function Read-Report([string]$Name, [string]$Path, [string]$SchemaVersion) {
    $fullPath = if ([string]::IsNullOrWhiteSpace($Path)) { "" } else { [System.IO.Path]::GetFullPath($Path) }
    if ([string]::IsNullOrWhiteSpace($fullPath) -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Add-NotVerified "$Name evidence was not supplied."
        $artifactReports[$Name] = [ordered]@{ path = $fullPath; present = $false; status = "NOT_VERIFIED" }
        return $null
    }

    try {
        $report = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Add-Error "$Name evidence is not valid JSON: $($_.Exception.Message)"
        $artifactReports[$Name] = [ordered]@{ path = $fullPath; present = $true; status = "BLOCKED" }
        return $null
    }

    $actualSchema = Get-String $report "schemaVersion"
    if ($actualSchema -ne $SchemaVersion) {
        Add-Error "$Name evidence schemaVersion must be $SchemaVersion; actual '$actualSchema'."
    }

    $status = Get-String $report "status"
    if (-not (Is-Status $status)) {
        Add-Error "$Name evidence has invalid status '$status'."
        $status = "BLOCKED"
    }
    $artifactReports[$Name] = [ordered]@{ path = $fullPath; present = $true; status = $status }
    return $report
}

function Test-ReportStatus([object]$Report, [string]$Name) {
    if ($null -eq $Report) {
        return
    }

    $status = Get-String $Report "status"
    if ($status -eq "BLOCKED" -and (Get-Array $Report "blockingReasons").Count -eq 0) {
        Add-Error "$Name BLOCKED evidence must include blockingReasons."
    }
    if ($status -eq "PASS" -and (Get-Array $Report "blockingReasons").Count -gt 0) {
        Add-Error "$Name PASS evidence must not include blockingReasons."
    }
}

function Test-InputReport([object]$Report) {
    if ($null -eq $Report) { return }
    Test-ReportStatus $Report "input"
    $expectedLanes = @("Detect", "Classification", "Segmentation", "OBB", "Pose")
    $models = Get-Array $Report "models"
    $actualLanes = @($models | ForEach-Object { Get-String $_ "lane" })
    foreach ($lane in $expectedLanes) {
        if (@($actualLanes | Where-Object { $_ -eq $lane }).Count -ne 1) {
            Add-Error "input evidence must contain exactly one model record for lane '$lane'."
        }
    }

    foreach ($model in $models) {
        $lane = Get-String $model "lane"
        $status = Get-String $model "status"
        if (-not (Is-Status $status)) {
            Add-Error "input model '$lane' has invalid status '$status'."
        }
        if ($status -eq "PASS") {
            if (-not (Is-Sha256 (Get-String $model "sha256")) -or [long](Get-PropertyValue $model "bytes") -le 0) {
                Add-Error "input model '$lane' PASS requires actual SHA-256 and positive bytes."
            }
            if ([string]::IsNullOrWhiteSpace((Get-String $model "source")) -or
                [string]::IsNullOrWhiteSpace((Get-String $model "opset"))) {
                Add-Error "input model '$lane' PASS requires source and ONNX opset."
            }
        }
        $image = Get-PropertyValue $model "validationImage"
        if ($lane -eq "Detect" -and $status -eq "PASS" -and (Get-String $image "status") -ne "PASS") {
            Add-Error "Detect PASS requires a PASS validation image."
        }
    }
}

function Test-Benchmark([object]$Benchmark, [string]$Lane, [string]$Provider) {
    if ($null -eq $Benchmark) {
        Add-Error "$Lane $Provider PASS is missing benchmark evidence."
        return
    }

    if ($Provider -eq "CPU" -and (Get-String $Benchmark "executionProvider") -ne "CPUExecutionProvider") {
        Add-Error "$Lane CPU PASS does not identify CPUExecutionProvider."
    }
    if (-not [bool](Get-PropertyValue $Benchmark "resultStructureValid")) {
        Add-Error "$Lane $Provider PASS requires valid result structure evidence."
    }
    if ([int](Get-PropertyValue $Benchmark "invalidResultCount") -ne 0 -or
        [int](Get-PropertyValue $Benchmark "nanResultCount") -ne 0 -or
        [int](Get-PropertyValue $Benchmark "outOfBoundsResultCount") -ne 0) {
        Add-Error "$Lane $Provider PASS contains invalid, NaN, or out-of-bounds results."
    }
}

function Test-ModelMatrix([object]$Report) {
    if ($null -eq $Report) { return }
    Test-ReportStatus $Report "model matrix"
    $runParameters = Get-PropertyValue $Report "runParameters"
    if ((Get-String $Report "status") -eq "PASS" -and
        ([int](Get-PropertyValue $runParameters "warmupIterations") -lt 100 -or
         [int](Get-PropertyValue $runParameters "iterations") -lt 1000)) {
        Add-Error "model matrix PASS requires at least 100 warm-up and 1000 measured iterations."
    }

    $lanes = Get-Array $Report "lanes"
    foreach ($lane in @("Detect", "Classification", "Segmentation", "OBB", "Pose")) {
        $records = @($lanes | Where-Object { (Get-String $_ "lane") -eq $lane })
        if ($records.Count -ne 1) {
            Add-Error "model matrix must contain exactly one record for lane '$lane'."
            continue
        }
        $record = $records[0]
        $cpu = Get-PropertyValue $record "cpu"
        $dml = Get-PropertyValue $record "dml"
        if ((Get-String $cpu "status") -eq "PASS") {
            Test-Benchmark (Get-PropertyValue $cpu "report").Benchmark $lane "CPU"
        }
        if ((Get-String $dml "status") -eq "PASS") {
            if ((Get-String $dml "actualProvider") -ne "DmlExecutionProvider" -or
                -not [bool](Get-PropertyValue (Get-PropertyValue $dml "report").Benchmark "gpuActive")) {
                Add-Error "$lane DML PASS must identify actual DmlExecutionProvider and gpuActive=true."
            }
            Test-Benchmark (Get-PropertyValue $dml "report").Benchmark $lane "DML"
            $profile = Get-PropertyValue $dml "profile"
            if ((Get-String $profile "status") -ne "PASS" -or [int](Get-PropertyValue $profile "remainingFileCount") -ne 0) {
                Add-Error "$lane DML PASS requires a clean profile directory."
            }
        }
        if ((Get-String $dml "status") -eq "PASS" -and (Get-String $dml "actualProvider") -eq "CPUExecutionProvider") {
            Add-Error "$lane strict DML PASS cannot report CPUExecutionProvider."
        }
        $negative = Get-PropertyValue $record "negativeContracts"
        if ((Get-String $negative "status") -eq "PASS" -and
            @((Get-Array $negative "cases") | Where-Object { (Get-String $_ "status") -ne "PASS" }).Count -gt 0) {
            Add-Error "$lane negative contract PASS contains a non-PASS case."
        }
    }
}

function Test-Release([object]$Report) {
    if ($null -eq $Report) { return }
    Test-ReportStatus $Report "release lab"
    if ((Get-String $Report "commitSha") -and -not (Is-Sha1 (Get-String $Report "commitSha"))) {
        Add-Error "release lab commitSha must be a complete 40-character SHA when present."
    }
    if ((Get-String $Report "bundleSha256") -and -not (Is-Sha256 (Get-String $Report "bundleSha256"))) {
        Add-Error "release lab bundleSha256 must be a complete SHA-256 when present."
    }
    $mutation = Get-PropertyValue $Report "releaseMutation"
    if ([bool](Get-PropertyValue $mutation "tagCreated") -or [bool](Get-PropertyValue $mutation "githubReleaseCreated")) {
        Add-Error "release lab must not create a tag or GitHub release."
    }
    $packages = Get-Array $Report "packages"
    foreach ($mode in @("Lite", "Full")) {
        $matches = @($packages | Where-Object { (Get-String $_ "mode") -eq $mode })
        if ($matches.Count -ne 1) {
            Add-Error "release lab must contain exactly one $mode package record."
            continue
        }
        $package = $matches[0]
        $status = Get-String $package "status"
        if (-not (Is-Status $status)) { Add-Error "$mode package has invalid status '$status'." }
        if ($status -eq "NOT_VERIFIED" -and $null -ne $package.PSObject.Properties["exitCode"] -and $null -ne $package.exitCode) {
            Add-Error "$mode NOT_VERIFIED package must not have a non-null exitCode."
        }
        if ($status -eq "PASS") {
            if (-not (Test-Path -LiteralPath (Get-String $package "path") -PathType Container)) {
                Add-Error "$mode PASS package path does not exist."
            }
            if (-not (Is-Sha256 (Get-String $package "packageHash"))) {
                Add-Error "$mode PASS package requires packageHash."
            }
        }
    }
}

function Test-Isolation([object]$Report) {
    if ($null -eq $Report) { return }
    Test-ReportStatus $Report "isolation lab"
    $migration = Get-PropertyValue $Report "migration"
    $migrationStatus = Get-String $migration "status"
    if ($migrationStatus -eq "PASS") {
        if ((Get-String (Get-PropertyValue $migration "rollback") "status") -ne "PASS") {
            Add-Error "isolation PASS migration requires PASS rollback evidence."
        }
        if (@((Get-Array $migration "scenarios") | Where-Object { (Get-String $_ "status") -ne "PASS" }).Count -gt 0) {
            Add-Error "isolation PASS migration contains a non-PASS scenario."
        }
    }
    $startup = Get-PropertyValue $Report "startup"
    if ((Get-String $startup "status") -eq "PASS" -and
        @((Get-Array $startup "runs") | Where-Object { (Get-String $_ "status") -ne "PASS" }).Count -gt 0) {
        Add-Error "isolation PASS startup contains a non-PASS run."
    }
}

function Test-Soak([object]$Report) {
    if ($null -eq $Report) { return }
    Test-ReportStatus $Report "soak"
    $promotionEligibility = Get-String $Report "promotionEligibility"
    if (-not (Is-Status $promotionEligibility)) {
        Add-Error "soak promotionEligibility has invalid status '$promotionEligibility'."
    }
    if ((Get-String $Report "commitSha") -and -not (Is-Sha1 (Get-String $Report "commitSha"))) {
        Add-Error "soak commitSha must be a complete 40-character SHA when present."
    }
    $runtime = Get-PropertyValue $Report "runtime"
    if ((Get-String $Report "status") -eq "PASS") {
        foreach ($name in @("isolatedAppData", "isolatedStorage", "startupCompleted", "fileLocksReleased")) {
            if (-not [bool](Get-PropertyValue $runtime $name)) {
                Add-Error "soak PASS requires runtime.$name=true."
            }
        }
        if ((Get-String (Get-PropertyValue $Report "finalConsistency") "status") -ne "PASS") {
            Add-Error "soak PASS requires PASS finalConsistency."
        }
        $queues = Get-PropertyValue $Report "queues"
        if ([long](Get-PropertyValue $queues "imagePending") -ne 0 -or
            [long](Get-PropertyValue $queues "recordPending") -ne 0 -or
            [long](Get-PropertyValue $queues "imageInFlight") -ne 0 -or
            [long](Get-PropertyValue $queues "recordInFlight") -ne 0) {
            Add-Error "soak PASS requires drained image and record queues with no in-flight work."
        }
    }
    if ((Get-String $Report "promotionEligibility") -eq "PASS" -and
        @((Get-Array $Report "notVerifiedReasons") | Where-Object { $_ -match "camera|PLC|FAT|SAT" }).Count -gt 0) {
        Add-Error "soak promotion PASS cannot coexist with real camera/PLC/FAT/SAT NOT_VERIFIED evidence."
    }

    $consistency = Get-PropertyValue $Report "finalConsistency"
    foreach ($field in @("MissingRecords", "MissingImages", "MissingTraceRecords")) {
        if ([long](Get-PropertyValue $consistency $field) -gt 0) {
            Add-Error "soak $field must block the evidence decision."
        }
    }
    if ((Get-String $consistency "QueueStatus") -eq "TIMEOUT") {
        Add-Error "soak queue drain TIMEOUT must be BLOCKED."
    }
    if ((Get-String $Report "status") -eq "PASS") {
        if ((Get-String $Report "evidenceType") -ne "production-component harness") {
            Add-Error "soak PASS must identify the production-component harness boundary."
        }
        if ((Get-String $Report "scenarioCoverageStatus") -ne "PASS") {
            Add-Error "soak PASS requires a PASS external scenario manifest contract."
        }
        $scenarioContract = Get-PropertyValue $Report "scenarioContract"
        $scenarioKinds = @((Get-Array $scenarioContract "samples") | ForEach-Object { Get-String $_ "kind" })
        foreach ($kind in @("has-target", "no-target", "multi-target", "short-frame", "wrong-size", "inference-exception")) {
            if (@($scenarioKinds | Where-Object { $_ -eq $kind }).Count -lt 1) {
                Add-Error "soak PASS scenario manifest must contain at least one '$kind' sample."
            }
        }
        $scenarioExecution = Get-PropertyValue $Report "scenarioExecution"
        if ((Get-String $scenarioExecution "status") -ne "PASS" -or
            [int](Get-PropertyValue $scenarioExecution "executedSamples") -ne [int](Get-PropertyValue $scenarioExecution "expectedSamples") -or
            @((Get-Array $scenarioExecution "samples") | Where-Object { (Get-String $_ "status") -ne "PASS" }).Count -gt 0) {
            Add-Error "soak PASS requires every declared scenario sample to execute and match its contract."
        }
        if ((Get-String $consistency "status") -ne "PASS" -or
            (Get-String $consistency "QueueStatus") -ne "DRAINED" -or
            [long](Get-PropertyValue $consistency "MissingRecords") -ne 0 -or
            [long](Get-PropertyValue $consistency "MissingImages") -ne 0 -or
            [long](Get-PropertyValue $consistency "MissingTraceRecords") -ne 0) {
            Add-Error "soak PASS requires a drained, complete final consistency scan."
        }
        foreach ($name in @("fileRenameVerification", "sqliteOpenVerification", "profileResidualStatus", "childProcessStatus", "threadStatus", "taskStatus")) {
            if ((Get-String $runtime $name) -ne "PASS") {
                Add-Error "soak PASS requires runtime.$name=PASS."
            }
        }
        if ([int](Get-PropertyValue $runtime "ResidualThreadCount") -ne 0 -or
            [int](Get-PropertyValue $runtime "ResidualTaskCount") -ne 0) {
            Add-Error "soak PASS requires zero residual threads and tasks."
        }
        $resources = Get-PropertyValue $Report "resources"
        $trend = Get-PropertyValue $resources "trend"
        $latency = Get-PropertyValue $resources "queueLatency"
        if ([int](Get-PropertyValue $trend "sampleCount") -lt 3 -or (Get-String $trend "status") -ne "PASS") {
            Add-Error "soak PASS requires a PASS resource trend based on at least three samples."
        }
        foreach ($queueName in @("image", "record", "cycle")) {
            if ([int](Get-PropertyValue (Get-PropertyValue $latency $queueName) "sampleCount") -le 0) {
                Add-Error "soak PASS requires queue percentile samples for $queueName."
            }
        }
        $faults = Get-PropertyValue $Report "faults"
        foreach ($fault in (Get-Array $faults "events")) {
            if (-not [bool](Get-PropertyValue $fault "Planned") -or
                -not [bool](Get-PropertyValue $fault "Injected") -or
                -not [bool](Get-PropertyValue $fault "FaultCleared") -or
                -not [bool](Get-PropertyValue $fault "NextHealthyCycleRecovered") -or
                (Get-String $fault "RecoveryStatus") -ne "RECOVERED") {
                Add-Error "soak PASS contains a fault without planned/injected/cleared/healthy-cycle recovery evidence."
            }
            if (-not [string]::Equals((Get-String $fault "ErrorCode"), (Get-String $fault "ExpectedErrorCode"), [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals((Get-String $fault "ActualTerminalState"), (Get-String $fault "ExpectedTerminalState"), [StringComparison]::OrdinalIgnoreCase)) {
                Add-Error "soak PASS contains a fault with an unexpected error code or terminal state."
            }
        }
    }
}

function Test-Migration([object]$Report) {
    if ($null -eq $Report) { return }
    Test-ReportStatus $Report "migration lab"

    $scenarios = Get-Array $Report "scenarios"
    $configLabScenarios = @($scenarios | Where-Object {
        (Get-String $_ "Name") -like "config-import-lab-*"
    })
    if ($configLabScenarios.Count -eq 0) {
        Add-Error "migration lab must contain config-import lab scenarios."
    }
    $requiredConfigLabNames = @(
        "config-import-lab-valid-migration-idempotence",
        "config-import-lab-missing-fields",
        "config-import-lab-historical-path",
        "config-import-lab-corrupt-config",
        "config-import-lab-model-reference",
        "config-import-lab-mid-migration-failure-recovery"
    )
    foreach ($requiredName in $requiredConfigLabNames) {
        if (@($configLabScenarios | Where-Object { (Get-String $_ "Name") -eq $requiredName }).Count -ne 1) {
            Add-Error "migration lab must contain exactly one '$requiredName' scenario."
        }
    }
    foreach ($scenario in $configLabScenarios) {
        if ((Get-String $scenario "Status") -ne "PASS") {
            Add-Error "migration config-import lab scenario '$((Get-String $scenario "Name"))' must be PASS."
        }
    }

    $rollback = Get-PropertyValue $Report "rollback"
    if ((Get-String $rollback "Status") -ne "PASS") {
        Add-Error "migration lab requires PASS snapshot rollback evidence."
    }

    $realUpgrade = @($scenarios | Where-Object {
        (Get-String $_ "Name") -eq "real-v6-upgrade-startup"
    })
    if ($realUpgrade.Count -ne 1) {
        Add-Error "migration lab must contain exactly one real-v6-upgrade-startup scenario."
    }
    elseif ((Get-String $Report "status") -eq "PASS" -and
        (Get-String $realUpgrade[0] "Status") -ne "PASS") {
        Add-Error "migration PASS requires a PASS real-v6-upgrade-startup scenario."
    }
    elseif ((Get-String $Report "status") -eq "NOT_VERIFIED" -and
        (Get-String $realUpgrade[0] "Status") -notin @("PASS", "NOT_VERIFIED")) {
        Add-Error "migration real-v6-upgrade-startup has an invalid status."
    }
}

$inputPath = if ($InputReportPath) { $InputReportPath } else { Join-Path $rootPath "artifacts\v6-g2\models\external-inputs.json" }
$modelPath = if ($ModelMatrixPath) { $ModelMatrixPath } else { Join-Path $rootPath "artifacts\v6-g2\models\model-matrix.json" }
$migrationPath = if ($MigrationEvidencePath) { $MigrationEvidencePath } else { Join-Path $rootPath "artifacts\v6-g2\migration\migration-evidence.json" }
$releasePath = if ($ReleaseEvidencePath) { $ReleaseEvidencePath } else { Join-Path $rootPath "artifacts\v6-g2\publish\release-lab-evidence.json" }
$isolationPath = if ($IsolationEvidencePath) { $IsolationEvidencePath } else { Join-Path $rootPath "artifacts\v6-g2\publish\isolation-evidence.json" }
$soakPath = if ($SoakEvidencePath) { $SoakEvidencePath } else { Join-Path $rootPath "artifacts\v6-g2\soak\soak-evidence.json" }

$inputReport = Read-Report "input" $inputPath "v6-g2-inputs-1.0"
$model = Read-Report "modelMatrix" $modelPath "v6-g2-model-matrix-1.0"
$migration = Read-Report "migration" $migrationPath "v6-g2-migration-lab-1.0"
$release = Read-Report "release" $releasePath "v6-g2-release-lab-1.0"
$isolation = Read-Report "isolation" $isolationPath "v6-g2-isolated-lab-1.0"
$soak = Read-Report "soak" $soakPath "v6-g2-soak-1.0"

Test-IdentitySet @(
    [pscustomobject]@{ Name = "input"; Report = $inputReport },
    [pscustomobject]@{ Name = "modelMatrix"; Report = $model },
    [pscustomobject]@{ Name = "migration"; Report = $migration },
    [pscustomobject]@{ Name = "release"; Report = $release },
    [pscustomobject]@{ Name = "isolation"; Report = $isolation },
    [pscustomobject]@{ Name = "soak"; Report = $soak }
)

Test-InputReport $inputReport
Test-ModelMatrix $model
Test-Migration $migration
Test-Release $release
Test-Isolation $isolation
Test-Soak $soak

$artifactStatuses = @($artifactReports.Values | ForEach-Object { [string]$_.status })
foreach ($artifact in $artifactReports.GetEnumerator()) {
    if ([string]$artifact.Value.status -eq "NOT_VERIFIED") {
        Add-NotVerified "$($artifact.Key) evidence status is NOT_VERIFIED."
    }
}
$status = if ($errors.Count -gt 0 -or $artifactStatuses -contains "BLOCKED") {
    "BLOCKED"
}
elseif ($artifactStatuses -contains "NOT_VERIFIED") {
    "NOT_VERIFIED"
}
else {
    "PASS"
}
$report = [ordered]@{
    schemaVersion = "v6-g2-evidence-validation-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    root = $rootPath
    status = $status
    schemaStatus = if ($errors.Count -eq 0) { "PASS" } else { "BLOCKED" }
    artifacts = $artifactReports
    errors = @($errors | Select-Object -Unique)
    notVerifiedReasons = @($notVerifiedReasons | Select-Object -Unique)
}

$json = $report | ConvertTo-Json -Depth 30
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fullOutputPath) | Out-Null
    [System.IO.File]::WriteAllText($fullOutputPath, $json, [System.Text.UTF8Encoding]::new($false))
}
Write-Output $json
if ($status -eq "BLOCKED") { exit 1 }
if ($status -eq "NOT_VERIFIED") { exit 2 }
exit 0
