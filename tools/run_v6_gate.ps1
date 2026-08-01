param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$EvidencePath = "",
    [switch]$ReportOnly,
    [switch]$KeepSnapshot,
    [switch]$PromotionGate
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
$promotionBlockingReasons = [System.Collections.Generic.List[string]]::new()
$logicalSkips = [System.Collections.Generic.List[object]]::new()
$manifest = $null
$minimumHermeticTests = 852

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

function Add-PromotionBlockingReason([string]$Reason) {
    if (-not [string]::IsNullOrWhiteSpace($Reason)) {
        [void]$promotionBlockingReasons.Add($Reason)
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

function Get-DotnetSdkVersion([string]$DotnetPath) {
    if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
        return ""
    }

    try {
        $versionOutput = @(& $DotnetPath "--version" 2>&1)
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        if ($exitCode -ne 0 -or $versionOutput.Count -ne 1) {
            return ""
        }

        $version = ([string]$versionOutput[0]).Trim()
        return $version
    }
    catch {
        return ""
    }
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
    [string]$LogName,
    [int[]]$ExpectedExitCodes = @(0)) {

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
    $status = if ($ExpectedExitCodes -contains $exitCode) { "PASS" } else { "BLOCKED" }
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
    $trxPath = Get-ChildItem -LiteralPath $ResultsDirectory -Filter "hermetic.trx" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
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

function Get-VerifiedGitSha([string]$GitPath, [string]$Ref) {
    if ([string]::IsNullOrWhiteSpace($GitPath) -or [string]::IsNullOrWhiteSpace($Ref)) {
        throw "Git executable or ref is empty."
    }

    $output = @(& $GitPath "rev-parse" "--verify" "--end-of-options" $Ref 2>&1)
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($exitCode -ne 0 -or $output.Count -ne 1) {
        throw "Unable to resolve Git ref '$Ref' with exit code $exitCode."
    }

    $sha = ([string]$output[0]).Trim()
    if ($sha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Git ref '$Ref' did not resolve to a complete SHA: '$sha'."
    }

    return $sha.ToLowerInvariant()
}

function Try-GetVerifiedGitSha([string]$GitPath, [string]$Ref) {
    try {
        return Get-VerifiedGitSha $GitPath $Ref
    }
    catch {
        return ""
    }
}

function Get-RemoteNames([string]$GitPath, [string]$PreferredRemote) {
    $names = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($PreferredRemote)) {
        [void]$names.Add($PreferredRemote.Trim())
    }

    $remoteOutput = @(& $GitPath "remote" 2>&1)
    $remoteExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($remoteExit -ne 0) {
        throw "Unable to list Git remotes with exit code $remoteExit."
    }

    foreach ($remote in $remoteOutput) {
        $name = ([string]$remote).Trim()
        if (-not [string]::IsNullOrWhiteSpace($name) -and -not $names.Contains($name)) {
            [void]$names.Add($name)
        }
    }

    return @($names)
}

function Resolve-RemoteSha(
    [string]$GitPath,
    [string[]]$RemoteNames,
    [string]$BranchName,
    [ref]$ResolvedRemote,
    [ref]$ResolvedRef) {

    foreach ($remote in @($RemoteNames)) {
        $refName = "refs/remotes/$remote/$BranchName"
        $sha = Try-GetVerifiedGitSha $GitPath $refName
        if (-not [string]::IsNullOrWhiteSpace($sha)) {
            $ResolvedRemote.Value = $remote
            $ResolvedRef.Value = $refName
            return $sha
        }
    }

    throw "Unable to resolve remote '$BranchName' from any configured Git remote."
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
$remoteName = ""
$remoteRef = ""
$dirtyEntries = @()
$gitMetadataStatus = "PASS"
$targetBranch = ""
$repository = [string]$env:GITHUB_REPOSITORY
$workflowRunId = [string]$env:GITHUB_RUN_ID
$workflowRunAttempt = [string]$env:GITHUB_RUN_ATTEMPT
$workflowRunUrl = [string]$env:GITHUB_RUN_URL
$actionsServerUrl = [string]$env:GITHUB_SERVER_URL
$githubSha = [string]$env:GITHUB_SHA
$actionsIdentityStatus = "NOT_VERIFIED"
$dotnetVersion = ""
try {
    $commit = Get-VerifiedGitSha $gitPath "HEAD^{commit}"
    $branch = (& $gitPath branch --show-current 2>&1).Trim()
    if ([string]::IsNullOrWhiteSpace($branch)) {
        $branch = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_HEAD_REF)) {
            $env:GITHUB_HEAD_REF.Trim()
        }
        elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
            $env:GITHUB_REF_NAME.Trim()
        }
        else {
            ""
        }
    }

    $preferredRemote = (& $gitPath config --get "branch.$branch.remote" 2>$null).Trim()
    $remoteNames = Get-RemoteNames $gitPath $preferredRemote
    $resolvedRemote = ""
    $resolvedRemoteRef = ""
    $remoteSha = Resolve-RemoteSha $gitPath $remoteNames "V6_test" ([ref]$resolvedRemote) ([ref]$resolvedRemoteRef)
    $remoteName = $resolvedRemote
    $remoteRef = $resolvedRemoteRef

    $mainRefs = [System.Collections.Generic.List[string]]::new()
    [void]$mainRefs.Add("refs/heads/main")
    foreach ($remote in @($remoteNames)) {
        [void]$mainRefs.Add("refs/remotes/$remote/main")
    }
    foreach ($mainRef in $mainRefs) {
        $mainSha = Try-GetVerifiedGitSha $gitPath $mainRef
        if (-not [string]::IsNullOrWhiteSpace($mainSha)) {
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($mainSha)) {
        throw "Unable to resolve main to a complete SHA."
    }

    $dirtyEntries = @(& $gitPath status --porcelain --untracked-files=all)
}
catch {
    $gitMetadataStatus = "BLOCKED"
    Add-BlockingReason "Unable to read Git metadata: $($_.Exception.Message)"
}

$targetBranch = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_BASE_REF)) {
    $env:GITHUB_BASE_REF.Trim()
}
else {
    $branch
}
if ($targetBranch -ne "V6_test") {
    Add-BlockingReason "Gate must validate V6_test; actual target branch was '$targetBranch'."
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
if ($gitMetadataStatus -ne "PASS") {
    Add-BlockingReason "Git identity metadata is incomplete; all required SHA refs must resolve with --verify."
}

$dotnetVersion = Get-DotnetSdkVersion $dotnetPath
if ([string]::IsNullOrWhiteSpace($dotnetVersion)) {
    Add-BlockingReason "The active dotnet CLI did not return a version; SDK identity cannot be verified."
}
elseif ($dotnetVersion -notmatch '^8\.') {
    Add-BlockingReason "The active .NET SDK is '$dotnetVersion'; an 8.x SDK is required."
}

$isActionsEnvironment = [string]::Equals([string]$env:GITHUB_ACTIONS, "true", [StringComparison]::OrdinalIgnoreCase)
if ($isActionsEnvironment) {
    $expectedWorkflowRunUrl = ""
    if (-not [string]::IsNullOrWhiteSpace($actionsServerUrl) -and
        -not [string]::IsNullOrWhiteSpace($repository) -and
        -not [string]::IsNullOrWhiteSpace($workflowRunId)) {
        $expectedWorkflowRunUrl = "{0}/{1}/actions/runs/{2}" -f $actionsServerUrl.TrimEnd('/'), $repository.Trim('/'), $workflowRunId.Trim()
        if ([string]::IsNullOrWhiteSpace($workflowRunUrl)) {
            $workflowRunUrl = $expectedWorkflowRunUrl
        }
    }

    $missingActionsFields = @(
        @{ Name = "GITHUB_REPOSITORY"; Value = $repository },
        @{ Name = "GITHUB_RUN_ID"; Value = $workflowRunId },
        @{ Name = "GITHUB_RUN_ATTEMPT"; Value = $workflowRunAttempt },
        @{ Name = "GITHUB_RUN_URL"; Value = $workflowRunUrl },
        @{ Name = "GITHUB_SERVER_URL"; Value = $actionsServerUrl },
        @{ Name = "GITHUB_SHA"; Value = $githubSha }
    ) | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) }

    if ($missingActionsFields.Count -gt 0) {
        $actionsIdentityStatus = "BLOCKED"
        Add-BlockingReason "GitHub Actions identity is incomplete: $($missingActionsFields.Name -join ', ')."
    }
    elseif ($githubSha -notmatch '^[0-9a-fA-F]{40}$' -or
            -not [string]::Equals($githubSha.Trim().ToLowerInvariant(), $commit, [StringComparison]::OrdinalIgnoreCase)) {
        $actionsIdentityStatus = "BLOCKED"
        Add-BlockingReason "GITHUB_SHA '$githubSha' does not match verified HEAD '$commit'."
    }
    elseif ($workflowRunId -notmatch '^\d+$' -or
            $workflowRunAttempt -notmatch '^\d+$' -or
            -not [string]::Equals($workflowRunUrl.TrimEnd('/'), $expectedWorkflowRunUrl.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)) {
        $actionsIdentityStatus = "BLOCKED"
        Add-BlockingReason "GitHub Actions run URL, run ID, or attempt does not match the verified repository identity."
    }
    else {
        $actionsIdentityStatus = "PASS"
    }
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
$haoNegativeContractStatus = "BLOCKED"
$huarayContractStep = $null
$huarayContractReport = $null
$huaraySdkTestStep = $null
$huarayContractStatus = "NOT_VERIFIED"

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
        "--filter", "Lane!=ExternalModel&Lane!=ExternalHuaraySdk",
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
    $huarayContractReportPath = Join-Path $evidenceRoot "huaray-sdk-contract.json"
    $huarayContractScript = Join-Path $snapshotScriptsRoot "verify_huaray_sdk_contract.ps1"
    if (Test-Path -LiteralPath $huarayContractScript -PathType Leaf) {
        $huarayContractStep = Invoke-Step "external Huaray SDK contract" $powerShellPath @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $huarayContractScript,
            "-Root", $snapshotRoot, "-ReportPath", $huarayContractReportPath
        ) $snapshotRoot "huaray-sdk-contract"

        if (Test-Path -LiteralPath $huarayContractReportPath -PathType Leaf) {
            try {
                $huarayContractReport = Get-Content -LiteralPath $huarayContractReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($huarayContractReport.status -eq "BLOCKED") {
                    Add-BlockingReason "Huaray SDK contract lane is BLOCKED: $($huarayContractReport.reason)"
                }
                elseif ($huarayContractReport.status -eq "NOT_VERIFIED") {
                    Add-NotVerifiedReason "Huaray SDK contract lane: $($huarayContractReport.reason)"
                }
                $huarayContractStatus = [string]$huarayContractReport.status
            }
            catch {
                Add-BlockingReason "Huaray SDK contract report is invalid: $($_.Exception.Message)"
            }
        }
        else {
            Add-BlockingReason "Huaray SDK contract report was not produced: $huarayContractReportPath"
        }
    }
    else {
        Add-BlockingReason "Tracked Huaray SDK contract script is missing from the clean-room snapshot."
    }
}
else {
    Add-NotVerifiedReason "Huaray SDK contract lane did not run because the tracked snapshot or PowerShell was unavailable."
}

if (-not [string]::IsNullOrWhiteSpace($env:CLEARFROST_HUARAY_SDK_PATH)) {
    if (-not [string]::IsNullOrWhiteSpace($dotnetPath) -and $null -ne $debugBuildStep -and $debugBuildStep.status -eq "PASS") {
        $huaraySdkTestStep = Invoke-Step "ExternalHuaraySdk .NET 8 adapter contract test" $dotnetPath @(
            "test", $snapshotTestProject,
            "-c", "Debug",
            "-p:Platform=x64",
            "--no-build",
            "--filter", "Lane=ExternalHuaraySdk",
            "--logger", "console;verbosity=minimal"
        ) $snapshotRoot "huaray-sdk-adapter-test"
        if ($huaraySdkTestStep.status -eq "PASS" -and $huarayContractStatus -ne "BLOCKED") {
            $huarayContractStatus = "PASS"
        }
    }
    else {
        Add-BlockingReason "Huaray SDK was supplied but the .NET 8 adapter contract test could not run."
        $huarayContractStatus = "BLOCKED"
    }
}

if ($archiveStatus -eq "PASS" -and -not [string]::IsNullOrWhiteSpace($powerShellPath)) {
    $dependencyReportPath = Join-Path $evidenceRoot "release-dependencies.json"
    $dependencyScript = Join-Path $snapshotScriptsRoot "verify_release_dependencies.ps1"
    $dependencyStep = Invoke-Step "tracked-only release dependency precheck" $powerShellPath @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $dependencyScript,
        "-Root", $snapshotRoot, "-Profile", "Lite", "-CleanRoom", "-ReportPath", $dependencyReportPath
    ) $snapshotRoot "dependency-precheck" @(1)
    $dependencyReport = Get-DependencyReport $dependencyReportPath

    $haoRecords = @($dependencyReport.dependencies | Where-Object {
        $_.kind -eq "plc" -and
        $_.name -eq "HaoCommunication" -and
        $_.required -eq $true
    })
    $haoNegativeContractStatus = if (
        $dependencyStep.status -eq "PASS" -and
        $dependencyStep.exitCode -eq 1 -and
        $dependencyReport.overallStatus -eq "BLOCKED" -and
        $haoRecords.Count -eq 1 -and
        @($haoRecords | Where-Object {
            $_.status -ne "BLOCKED" -or
            $_.available.exists -eq $true -or
            $_.packaged.exists -eq $true -or
            [string]$_.reason -ne "Required external dependency is unavailable." -or
            @($_.requiredBy).Count -eq 0
        }).Count -eq 0
    ) { "PASS" } else { "BLOCKED" }

    if ($haoNegativeContractStatus -ne "PASS") {
        Add-BlockingReason "HaoCommunication missing-dependency negative contract was not proven."
    }

    foreach ($dependency in @($dependencyReport.dependencies | Where-Object { $_.required -eq $true -and $_.status -ne "PASS" })) {
        Add-PromotionBlockingReason "$($dependency.kind) '$($dependency.name)' is not available for positive promotion evidence."
    }
}

$litePublishStep = [ordered]@{
    name = "Lite positive publish evidence"
    status = "NOT_VERIFIED"
    exitCode = 0
    reason = "Development validation does not claim an authorized Lite package."
}
$fullPublishStep = [ordered]@{
    name = "Full positive publish evidence"
    status = "NOT_VERIFIED"
    exitCode = 0
    reason = "Development validation does not claim an authorized Full package."
}
Add-PromotionBlockingReason "Authorized positive Lite and Full package evidence was not supplied."
$positivePublishStatus = if ($litePublishStep.status -eq "PASS" -and $fullPublishStep.status -eq "PASS") {
    "PASS"
}
elseif ($litePublishStep.status -eq "BLOCKED" -or $fullPublishStep.status -eq "BLOCKED") {
    "BLOCKED"
}
else {
    "NOT_VERIFIED"
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
foreach ($logicalSkip in @($logicalSkips)) {
    Add-PromotionBlockingReason "$($logicalSkip.lane): $($logicalSkip.reason)"
}

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

$hermeticFailureCount = [int]$testCounters.failed + [int]$testCounters.errors
$hermeticStatus = if (
    $null -ne $testStep -and
    $testStep.status -eq "PASS" -and
    [int]$testCounters.total -ge $minimumHermeticTests -and
    $hermeticFailureCount -eq 0
) { "PASS" } else { "BLOCKED" }
$hermeticStatusReason = if ($hermeticStatus -eq "PASS") {
    "Hermetic test command passed with $($testCounters.total) tests and zero failed/error results."
}
else {
    "Hermetic lane requires at least $minimumHermeticTests tests and zero failures/errors; observed total=$($testCounters.total), failed=$($testCounters.failed), errors=$($testCounters.errors)."
}
if ($hermeticStatus -ne "PASS") {
    Add-BlockingReason $hermeticStatusReason
}
$hermeticTestEvidence = [ordered]@{
    status = $hermeticStatus
    lane = "Hermetic unit/contract + synthetic descriptor"
    filter = "Lane!=ExternalModel"
    command = if ($null -eq $testStep) { "" } else { $testStep.command }
    counters = $testCounters
    reason = $hermeticStatusReason
    externalTestsExcluded = $true
    minimumTests = $minimumHermeticTests
    failureCount = $hermeticFailureCount
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

$developmentStatus = if ($blockingReasons.Count -gt 0) { "BLOCKED" } else { "PASS" }
$promotionEligibility = if (
    $developmentStatus -eq "PASS" -and
    $promotionBlockingReasons.Count -eq 0 -and
    $logicalSkips.Count -eq 0
) { "PASS" } else { "BLOCKED" }

$encodingRestoreStatus = if (
    $null -ne $encodingStep -and
    $encodingStep.status -eq "PASS" -and
    $null -ne $restoreStep -and
    $restoreStep.status -eq "PASS"
) { "PASS" } else { "BLOCKED" }
$a2Reason = if ($encodingRestoreStatus -eq "PASS") {
    "Tracked-only encoding and Restore both passed."
}
else {
    "A2 requires both tracked-only encoding and Restore to pass."
}
$a6Reason = if ($actionsIdentityStatus -eq "PASS") {
    "GitHub Actions HEAD, GITHUB_SHA, repository, run ID, attempt, and URL match."
}
elseif ($isActionsEnvironment) {
    "A6 is BLOCKED because the real Actions run identity is incomplete or does not match the verified HEAD SHA."
}
else {
    "A6 is NOT_VERIFIED locally; a real Actions run with matching SHA and run metadata is required."
}
$a7Reason = if ($haoNegativeContractStatus -eq "PASS") {
    "HaoCommunication missing-dependency negative contract passed with expected exit code 1 and no available or packaged DLL."
}
else {
    "HaoCommunication missing-dependency negative contract was not precisely verified."
}
$a13Reason = if ($haoNegativeContractStatus -eq "PASS") {
    "Negative missing-dependency contract PASS; positive Lite/Full release status is $positivePublishStatus and remains separate, so promotion is BLOCKED until authorized release evidence exists."
}
else {
    "Negative missing-dependency contract is BLOCKED; positive Lite/Full release status is $positivePublishStatus."
}
$a15Reason = if ($hermeticFailureCount -eq 0 -and $hermeticTestEvidence.status -eq "PASS") {
    "Actual hermetic regression failures/errors are zero across $($testCounters.total) tests."
}
else {
    "Actual hermetic regression failures/errors must be zero; observed failed=$($testCounters.failed), errors=$($testCounters.errors)."
}

$acceptance = [ordered]@{
    A1 = [ordered]@{ status = if ($targetBranch -eq "V6_test" -and $dirtyEntries.Count -eq 0) { "PASS" } else { "BLOCKED" }; reason = "V6_test target and clean worktree are required." }
    A2 = [ordered]@{ status = $encodingRestoreStatus; reason = $a2Reason }
    A3 = [ordered]@{ status = if ($null -ne $debugBuildStep -and $debugBuildStep.status -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Tracked-only Debug x64 build." }
    A4 = [ordered]@{ status = $hermeticTestEvidence.status; reason = $hermeticStatusReason; counters = $testCounters }
    A5 = [ordered]@{ status = if ($null -ne $releaseBuildStep -and $releaseBuildStep.status -eq "PASS" -and $releaseCompileStep.status -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Tracked-only Release x64 build and Release MockCamera exclusion." }
    A6 = [ordered]@{ status = $actionsIdentityStatus; reason = $a6Reason }
    A7 = [ordered]@{ status = $haoNegativeContractStatus; reason = $a7Reason }
    A8 = [ordered]@{ status = if ($trackedDlls.Count -eq 0 -and $trackedOnnx.Count -eq 0) { "PASS" } else { "BLOCKED" }; reason = "Private binaries and models must remain external." }
    A9 = [ordered]@{ status = "PASS"; reason = "Manifest separates synthetic, real-model, GPU, hardware, installation, and long-run lanes." }
    A10 = [ordered]@{ status = "PASS"; reason = "Strict requested/actual provider mismatch contract is unit-tested; real GPU execution remains NOT_VERIFIED." }
    A11 = [ordered]@{ status = if ($bundleStatus -eq "PASS" -and $jsSyntaxStatus -eq "PASS") { "PASS" } else { "BLOCKED" }; reason = "Deterministic bundle and JavaScript syntax." }
    A12 = [ordered]@{ status = $versionIdentityStatus; reason = "V6_test uses a prerelease version and non-formal application label." }
    A13 = [ordered]@{
        status = $haoNegativeContractStatus
        reason = $a13Reason
        negativeContract = [ordered]@{ status = $haoNegativeContractStatus; dependency = "HaoCommunication" }
        positiveRelease = [ordered]@{
            status = $positivePublishStatus
            Lite = $litePublishStep
            Full = $fullPublishStep
        }
    }
    A14 = [ordered]@{ status = "NOT_VERIFIED"; reason = "Real camera, PLC, 8-hour long-run, installation, and rollback evidence is absent; promotion remains BLOCKED." }
    A15 = [ordered]@{ status = if ($hermeticTestEvidence.status -eq "PASS" -and $hermeticFailureCount -eq 0) { "PASS" } else { "BLOCKED" }; reason = $a15Reason; regressionFailures = $hermeticFailureCount }
}

$manifest = [ordered]@{
    schemaVersion = "v6-gate-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    commit = $commit
    headSha = $commit
    branch = $branch
    targetBranch = $targetBranch
    dirty = $dirtyEntries.Count -gt 0
    dirtyEntries = @($dirtyEntries)
    mainSha = $mainSha
    remoteV6TestShaAtStart = $remoteSha
    git = [ordered]@{
        headSha = $commit
        mainSha = $mainSha
        remoteV6TestSha = $remoteSha
        remoteName = $remoteName
        remoteRef = $remoteRef
        repository = $repository
        workflowRunId = $workflowRunId
        workflowRunAttempt = $workflowRunAttempt
        workflowRunUrl = $workflowRunUrl
        githubSha = $githubSha.Trim()
        actionsIdentityStatus = $actionsIdentityStatus
    }
    productVersion = $productVersion
    releaseIdentity = if ($productVersion -match "-") { "pre-release-candidate" } else { "development-build" }
    environment = [ordered]@{
        os = [System.Environment]::OSVersion.VersionString
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        dotnet = $dotnetVersion
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
    huaraySdkContract = [ordered]@{
        status = $huarayContractStatus
        reason = if ($huarayContractStatus -eq "PASS") {
            "Supplied SDK public contract and the .NET 8 ClearFrost adapter contract test passed without camera connection."
        }
        elseif ($huarayContractStatus -eq "BLOCKED") {
            "Supplied SDK or adapter contract did not pass."
        }
        else {
            "CLEARFROST_HUARAY_SDK_PATH was not supplied; external SDK contract remains NOT_VERIFIED."
        }
        publicContract = $huarayContractReport
        adapterTest = if ($null -eq $huaraySdkTestStep) {
            [ordered]@{ status = "NOT_VERIFIED"; reason = "ExternalHuaraySdk test was not run because no SDK input was supplied." }
        }
        else {
            $huaraySdkTestStep
        }
    }
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
    developmentValidation = [ordered]@{
        status = $developmentStatus
        blockingReasons = @($blockingReasons)
        notVerifiedReasons = @($notVerifiedReasons)
    }
    promotion = [ordered]@{
        status = $promotionEligibility
        blockingReasons = @($promotionBlockingReasons)
        eligibility = $promotionEligibility
    }
    overallStatus = $developmentStatus
    blockingReasons = @($blockingReasons)
    notVerifiedReasons = @($notVerifiedReasons)
    promotionBlockingReasons = @($promotionBlockingReasons)
    promotionEligibility = $promotionEligibility
}

$manifestPath = Join-Path $evidenceRoot "evidence.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))

Write-Output "V6 gate evidence: $manifestPath"
Write-Output "Development validation: $developmentStatus"
Write-Output "Promotion eligibility: $promotionEligibility"
Write-Output "Hermetic tests: $($hermeticTestEvidence.status) ($($testCounters.passed)/$($testCounters.total))"

if ($ReportOnly) {
    exit 0
}
if ($PromotionGate) {
    if ($promotionEligibility -eq "PASS") {
        exit 0
    }

    exit 1
}
if ($developmentStatus -eq "PASS") {
    exit 0
}
exit 1
