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

function Get-Sha256([string]$Value) {
    if ($null -eq $Value) { $Value = "" }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Value)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "") } finally { $sha.Dispose() }
}

function Get-ExternalDependencyDigest([object[]]$Dependencies) {
    $lines = @($Dependencies | Sort-Object { Get-String $_ "name" }, { Get-String $_ "version" } | ForEach-Object {
        "{0}|{1}|{2}|{3}|{4}" -f (Get-String $_ "name"), (Get-String $_ "version"),
        ([long](Get-PropertyValue $_ "bytes")), (Get-String $_ "sha256").ToUpperInvariant(), (Get-String $_ "role")
    })
    return Get-Sha256 ($lines -join "`n")
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
        Add-Error "$Name identity provider is required, including NOT_APPLICABLE."
    }
    foreach ($field in @("inputManifestSha256", "detectModelSha256", "validationImageSha256", "productAssemblySha256")) {
        $value = Get-String $identity $field
        if (-not [string]::IsNullOrWhiteSpace($value) -and -not (Is-Sha256 $value)) {
            Add-Error "$Name identity $field must be empty or a complete SHA-256."
        }
    }
    foreach ($field in @("externalDependencySetDigest", "candidateDigest")) {
        if (-not (Is-Sha256 (Get-String $identity $field))) {
            Add-Error "$Name identity $field must be a complete SHA-256."
        }
    }
    foreach ($field in @("evidenceSetId", "orchestratorRunId")) {
        if ([string]::IsNullOrWhiteSpace((Get-String $identity $field))) {
            Add-Error "$Name identity $field is required to prevent cross-run evidence assembly."
        }
    }
    $dependencies = Get-Array $identity "externalDependencies"
    foreach ($dependency in $dependencies) {
        foreach ($field in @("name", "version", "role")) {
            if ([string]::IsNullOrWhiteSpace((Get-String $dependency $field))) {
                Add-Error "$Name identity external dependency requires $field."
            }
        }
        if ([long](Get-PropertyValue $dependency "bytes") -lt 0 -or
            (-not [string]::IsNullOrWhiteSpace((Get-String $dependency "sha256")) -and -not (Is-Sha256 (Get-String $dependency "sha256")))) {
            Add-Error "$Name identity external dependency has invalid bytes or SHA-256."
        }
    }
    if ((Get-ExternalDependencyDigest $dependencies) -ne (Get-String $identity "externalDependencySetDigest").ToUpperInvariant()) {
        Add-Error "$Name identity externalDependencySetDigest does not match name-sorted externalDependencies."
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
        foreach ($field in @("inputManifestSha256", "detectModelSha256", "validationImageSha256", "productAssemblySha256")) {
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
    if ((Get-String $reference "orchestratorRunId") -eq "local-unbound" -and
        @($Reports | Where-Object { (Get-String $_.Report "requiredStatus") -eq "PASS" -or (Get-String $_.Report "status") -eq "PASS" }).Count -gt 0) {
        Add-Error "Positive G2 evidence requires an explicit orchestratorRunId/evidenceSetId; local-unbound reports cannot be assembled across runs."
    }
    foreach ($entry in $identities | Select-Object -Skip 1) {
        foreach ($field in @(
                "commitSha", "productVersion", "inputManifestSha256", "detectModelSha256",
                "validationImageSha256", "productAssemblySha256", "externalDependencySetDigest",
                "candidateDigest", "evidenceSetId", "orchestratorRunId", "workflowRunId", "machineIdentityDigest")) {
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
    foreach ($field in @("requiredStatus", "compatibilityStatus", "overallStatus")) {
        if (-not (Is-Status (Get-String $Report $field))) {
            Add-Error "input evidence $field must be PASS, NOT_VERIFIED, or BLOCKED."
        }
    }
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
    $detect = @($models | Where-Object { (Get-String $_ "lane") -eq "Detect" }) | Select-Object -First 1
    if ((Get-String $detect "status") -eq "PASS" -and (Get-String $Report "requiredStatus") -ne "PASS") {
        Add-Error "Detect PASS must produce input requiredStatus=PASS independently of optional lanes."
    }
    if ((Get-String $detect "status") -ne "PASS" -and (Get-String $Report "requiredStatus") -eq "PASS") {
        Add-Error "Detect missing or failed input cannot produce requiredStatus=PASS."
    }
    $optional = @($models | Where-Object { (Get-String $_ "lane") -ne "Detect" })
    if (@($optional | Where-Object { (Get-String $_ "status") -eq "BLOCKED" }).Count -gt 0 -and
        (Get-String $Report "compatibilityStatus") -ne "BLOCKED") {
        Add-Error "Optional model failure must remain visible as compatibilityStatus=BLOCKED."
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
    foreach ($field in @("requiredStatus", "compatibilityStatus", "overallStatus")) {
        if (-not (Is-Status (Get-String $Report $field))) {
            Add-Error "model matrix $field must be PASS, NOT_VERIFIED, or BLOCKED."
        }
    }
    $runParameters = Get-PropertyValue $Report "runParameters"
    if ((Get-String $Report "status") -eq "PASS" -and
        ([int](Get-PropertyValue $runParameters "warmupIterations") -lt 100 -or
         [int](Get-PropertyValue $runParameters "iterations") -lt 1000)) {
        Add-Error "model matrix PASS requires at least 100 warm-up and 1000 measured iterations."
    }

    $lanes = Get-Array $Report "lanes"
    $detectRecord = $null
    foreach ($lane in @("Detect", "Classification", "Segmentation", "OBB", "Pose")) {
        $records = @($lanes | Where-Object { (Get-String $_ "lane") -eq $lane })
        if ($records.Count -ne 1) {
            Add-Error "model matrix must contain exactly one record for lane '$lane'."
            continue
        }
        $record = $records[0]
        if ($lane -eq "Detect") { $detectRecord = $record }
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
    if ($null -ne $detectRecord) {
        $detectCpu = Get-PropertyValue $detectRecord "cpu"
        $detectDml = Get-PropertyValue $detectRecord "dml"
        $detectPass = (Get-String $detectCpu "status") -eq "PASS" -and (Get-String $detectDml "status") -eq "PASS"
        if ($detectPass -and (Get-String $Report "requiredStatus") -ne "PASS") {
            Add-Error "Detect CPU and DML PASS must produce model-matrix requiredStatus=PASS."
        }
        if (-not $detectPass -and (Get-String $Report "requiredStatus") -eq "PASS") {
            Add-Error "Detect requiredStatus=PASS requires both actual CPU and DML PASS lanes."
        }
    }
    $optionalRecords = @($lanes | Where-Object { (Get-String $_ "lane") -ne "Detect" })
    if (@($optionalRecords | Where-Object {
            (Get-String (Get-PropertyValue $_ "cpu") "status") -eq "BLOCKED" -or
            (Get-String (Get-PropertyValue $_ "dml") "status") -eq "BLOCKED"
        }).Count -gt 0 -and (Get-String $Report "compatibilityStatus") -ne "BLOCKED") {
        Add-Error "Optional model lane failure must remain visible as compatibilityStatus=BLOCKED."
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
    foreach ($field in @("requiredStatus", "compatibilityStatus", "overallStatus")) {
        if (-not (Is-Status (Get-String $Report $field))) {
            Add-Error "soak $field must be PASS, NOT_VERIFIED, or BLOCKED."
        }
    }
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
        foreach ($sample in Get-Array $scenarioExecution "samples") {
            $kind = Get-String $sample "kind"
            $resultCount = [int](Get-PropertyValue $sample "resultCount")
            if ($kind -eq "has-target" -and $resultCount -lt 1) { Add-Error "has-target must execute with resultCount >= 1." }
            if ($kind -eq "no-target" -and $resultCount -ne 0) { Add-Error "no-target must execute with resultCount == 0." }
            if ($kind -eq "multi-target" -and $resultCount -lt 2) { Add-Error "multi-target must execute with resultCount >= 2." }
            if ($kind -eq "short-frame" -and (Get-String $sample "injectionBoundary") -ne "camera") { Add-Error "short-frame must be injected at the camera boundary." }
            if ($kind -eq "wrong-size" -and (Get-String $sample "injectionBoundary") -ne "input-contract") { Add-Error "wrong-size must use the explicit input-size contract." }
            if ($kind -eq "inference-exception" -and (Get-String $sample "injectionBoundary") -ne "inference") { Add-Error "inference-exception must be injected at the inference boundary." }
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
        if ([int](Get-PropertyValue $runtime "ResidualTaskCount") -ne 0 -or
            -not [bool](Get-PropertyValue $runtime "ownedWorkersExited")) {
            Add-Error "soak PASS requires all ClearFrost-owned workers and tasks to exit."
        }
        if ((Get-String $runtime "threadStabilityStatus") -ne "PASS" -or
            [int](Get-PropertyValue $runtime "ResidualThreadCount") -gt [int](Get-PropertyValue $runtime "allowedThreadPoolDelta")) {
            Add-Error "soak PASS requires a stable post-shutdown thread window within the allowed ThreadPool delta."
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
        $recoveryCycleIds = @{}
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
            $healthyId = Get-String $fault "NextHealthyInspectionId"
            if ([string]::IsNullOrWhiteSpace($healthyId)) {
                Add-Error "soak PASS fault recovery must bind a dedicated following healthy cycle."
            }
            elseif ($recoveryCycleIds.ContainsKey($healthyId)) {
                Add-Error "soak PASS must not use one healthy cycle to recover multiple faults."
            }
            else {
                $recoveryCycleIds[$healthyId] = $true
            }
        }
    }
}

function Test-ProviderSemantics([object]$Input, [object]$Model, [object]$Migration, [object]$Release, [object]$Isolation, [object]$Soak) {
    foreach ($entry in @(
            [pscustomobject]@{ name = "input"; report = $Input },
            [pscustomobject]@{ name = "migration"; report = $Migration },
            [pscustomobject]@{ name = "release"; report = $Release },
            [pscustomobject]@{ name = "isolation"; report = $Isolation })) {
        $identity = Get-PropertyValue $entry.report "identity"
        if ($null -ne $identity -and (Get-String $identity "provider") -ne "NOT_APPLICABLE") {
            Add-Error "$($entry.name) does not execute inference and must state identity.provider=NOT_APPLICABLE."
        }
    }
    if ($null -ne $Model) {
        $required = Get-String $Model "requiredStatus"
        $provider = Get-String (Get-PropertyValue $Model "identity") "provider"
        if ($required -eq "PASS" -and $provider -ne "CPUExecutionProvider;DmlExecutionProvider") {
            Add-Error "model matrix required PASS must derive identity.provider from actual CPU and DML probes."
        }
        if ($required -ne "PASS" -and $provider -ne "NOT_VERIFIED") {
            Add-Error "model matrix without a verified Detect matrix must state identity.provider=NOT_VERIFIED."
        }
    }
    if ($null -ne $Soak) {
        $provider = Get-PropertyValue $Soak "provider"
        $identityProvider = Get-String (Get-PropertyValue $Soak "identity") "provider"
        $actual = if ($null -eq $provider) { "NOT_VERIFIED" } else { Get-String $provider "executionProvider" }
        if ([string]::IsNullOrWhiteSpace($actual)) { $actual = "NOT_VERIFIED" }
        if ($identityProvider -ne $actual) {
            Add-Error "soak identity.provider must come from DetectionService.RuntimeStatus, not a fixed or environment value."
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
Test-ProviderSemantics $inputReport $model $migration $release $isolation $soak

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
