param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$ReleaseEvidencePath = "",
    [string]$OutputPath = "",
    [string]$DotnetPath = "dotnet",
    [int]$StartupTimeoutSeconds = 10,
    [int]$Repetitions = 3
)

$ErrorActionPreference = "Stop"
$rootPath = [System.IO.Path]::GetFullPath($Root)
$releaseEvidenceFile = if ([string]::IsNullOrWhiteSpace($ReleaseEvidencePath)) {
    Join-Path $rootPath "artifacts\v6-g2\publish\release-lab-evidence.json"
}
else {
    [System.IO.Path]::GetFullPath($ReleaseEvidencePath)
}
$evidenceFile = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $rootPath "artifacts\v6-g2\publish\isolation-evidence.json"
}
else {
    [System.IO.Path]::GetFullPath($OutputPath)
}
$evidenceDirectory = Split-Path -Parent $evidenceFile
$isolationRoot = Join-Path $evidenceDirectory "isolated"
$migrationRoot = Join-Path $evidenceDirectory "migration-lab"
New-Item -ItemType Directory -Force -Path $evidenceDirectory, $isolationRoot, $migrationRoot | Out-Null

$blockingReasons = [System.Collections.Generic.List[string]]::new()
$notVerifiedReasons = [System.Collections.Generic.List[string]]::new()
$startupRecords = [System.Collections.Generic.List[object]]::new()

function Add-BlockingReason([string]$Reason) {
    if (-not [string]::IsNullOrWhiteSpace($Reason)) { [void]$blockingReasons.Add($Reason) }
}

function Add-NotVerifiedReason([string]$Reason) {
    if (-not [string]::IsNullOrWhiteSpace($Reason)) { [void]$notVerifiedReasons.Add($Reason) }
}

function Get-String([object]$Object, [string]$Name) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) { return "" }
    return [string]$Object.PSObject.Properties[$Name].Value
}

function Get-SourceHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return "" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-AssemblyName {
    $projectPath = Join-Path $rootPath "ClearFrost\ClearFrost.csproj"
    if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
        $projectText = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
        $match = [regex]::Match($projectText, "<AssemblyName>(?<name>[^<]+)</AssemblyName>")
        if ($match.Success) {
            return $match.Groups["name"].Value.Trim()
        }
    }

    return "ClearFrost"
}

function Test-TextForSourcePath([string]$Directory) {
    $findings = [System.Collections.Generic.List[string]]::new()
    $textExtensions = @(".json", ".log", ".txt", ".xml", ".config", ".html", ".js", ".css", ".md")
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File -Recurse -ErrorAction SilentlyContinue)) {
        if ($textExtensions -contains $file.Extension.ToLowerInvariant()) {
            try {
                $text = [System.IO.File]::ReadAllText($file.FullName)
                if ($text.Contains($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    [void]$findings.Add($file.FullName)
                }
                if ($text.Contains("C:\GreeVisionData", [System.StringComparison]::OrdinalIgnoreCase)) {
                    [void]$findings.Add($file.FullName)
                }
            }
            catch {
                [void]$findings.Add("$($file.FullName): $($_.Exception.Message)")
            }
        }
    }
    return @($findings | Select-Object -Unique)
}

function Prepare-IsolatedConfig([string]$PackagePath, [string]$AppDataPath) {
    $configDirectory = Join-Path $AppDataPath "Config"
    New-Item -ItemType Directory -Force -Path $configDirectory, (Join-Path $AppDataPath "Data"), (Join-Path $AppDataPath "Logs") | Out-Null
    $packageConfigPath = Join-Path $PackagePath "config.json"
    if (Test-Path -LiteralPath $packageConfigPath -PathType Leaf) {
        $config = Get-Content -LiteralPath $packageConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    else {
        $config = [pscustomobject]@{}
    }
    $config.StoragePath = Join-Path $AppDataPath "Data"
    $config.Cameras = @()
    $config.ActiveCameraId = ""
    $config.CameraSerialNumber = ""
    $config.TriggerSource = "Manual"
    $config.PlcIp = "127.0.0.1"
    $config.CurrentModelFileName = ""
    $config.EnableGpu = $false
    $config.IsDebugMode = $true
    $config.RequireApprovedModelsForProduction = $false
    [System.IO.File]::WriteAllText((Join-Path $configDirectory "config.json"), ($config | ConvertTo-Json -Depth 30), [System.Text.UTF8Encoding]::new($false))
    $packagePresetsPath = Join-Path $PackagePath "project-presets.json"
    if (Test-Path -LiteralPath $packagePresetsPath -PathType Leaf) {
        Copy-Item -LiteralPath $packagePresetsPath -Destination (Join-Path $configDirectory "project-presets.json") -Force
    }
}

function Invoke-IsolatedStartup([string]$Mode, [string]$PackagePath, [int]$Attempt) {
    $runRoot = Join-Path $isolationRoot ("{0}\attempt-{1}" -f $Mode.ToLowerInvariant(), $Attempt)
    if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
    $packageRunPath = Join-Path $runRoot "package"
    $appDataPath = Join-Path $runRoot "appdata"
    $processLogPath = Join-Path $runRoot "process.stdout.log"
    $processErrorPath = Join-Path $runRoot "process.stderr.log"
    New-Item -ItemType Directory -Force -Path $packageRunPath, $appDataPath | Out-Null
    Copy-Item -Path (Join-Path $PackagePath "*") -Destination $packageRunPath -Recurse -Force
    Prepare-IsolatedConfig $packageRunPath $appDataPath

    $exeName = (Get-AssemblyName) + ".exe"
    $exePath = Join-Path $packageRunPath $exeName
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        return [ordered]@{ mode = $Mode; attempt = $Attempt; status = "BLOCKED"; reason = "Package executable is missing."; runRoot = $runRoot }
    }

    $oldAppDataRoot = [string]$env:CLEARFROST_APPDATA_ROOT
    $oldProfileRoot = [string]$env:CLEARFROST_DML_PROFILE_ROOT
    $env:CLEARFROST_APPDATA_ROOT = $appDataPath
    $env:CLEARFROST_DML_PROFILE_ROOT = Join-Path $appDataPath "Profiles"
    $process = $null
    $started = $false
    $exitCode = $null
    $forcedTermination = $false
    try {
        $process = Start-Process -FilePath $exePath -WorkingDirectory $packageRunPath -WindowStyle Hidden -RedirectStandardOutput $processLogPath -RedirectStandardError $processErrorPath -PassThru
        $started = $true
        $deadline = (Get-Date).AddSeconds([Math]::Max(1, $StartupTimeoutSeconds))
        while ((Get-Date) -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { break }
            Start-Sleep -Milliseconds 500
        }
        $process.Refresh()
        if (-not $process.HasExited) {
            try {
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) { [void]$process.CloseMainWindow() }
                [void]$process.WaitForExit(3000)
            }
            catch { }
        }
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $forcedTermination = $true
            [void]$process.WaitForExit(3000)
        }
        $process.Refresh()
        if ($process.HasExited) { $exitCode = $process.ExitCode }
    }
    catch {
        return [ordered]@{ mode = $Mode; attempt = $Attempt; status = "BLOCKED"; reason = $_.Exception.Message; runRoot = $runRoot; started = $started }
    }
    finally {
        $env:CLEARFROST_APPDATA_ROOT = $oldAppDataRoot
        $env:CLEARFROST_DML_PROFILE_ROOT = $oldProfileRoot
    }

    $logsDirectory = Join-Path $appDataPath "Logs"
    $sourceFindings = Test-TextForSourcePath $appDataPath
    $residualProcess = $false
    if ($null -ne $process) {
        try {
            $process.Refresh()
            $residualProcess = -not $process.HasExited
        }
        catch { $residualProcess = $false }
    }
    $startupLog = Join-Path $logsDirectory "startup.log"
    $startupLogExists = Test-Path -LiteralPath $startupLog -PathType Leaf
    $healthFiles = @(Get-ChildItem -LiteralPath $logsDirectory -File -ErrorAction SilentlyContinue)
    $startupStatus = if ($started -and -not $residualProcess -and -not $forcedTermination -and
        $exitCode -eq 0 -and $startupLogExists -and $sourceFindings.Count -eq 0) { "PASS" } else { "BLOCKED" }
    $reason = if ($startupStatus -eq "PASS") {
        "Application ran and closed with an isolated AppData root and no source-path writes."
    }
    elseif ($sourceFindings.Count -gt 0) {
        "Isolated startup wrote a source or default-machine path into its evidence files."
    }
    elseif ($residualProcess) {
        "Application process remained after the controlled close window."
    }
    elseif ($forcedTermination) {
        "Application required forced termination and did not satisfy normal shutdown."
    }
    elseif (-not $startupLogExists) {
        "Application did not produce the isolated startup log."
    }
    elseif ($exitCode -ne 0) {
        "Application exited with a non-zero code: $exitCode."
    }
    else {
        "Application did not complete the isolated startup contract."
    }
    return [ordered]@{
        mode = $Mode
        attempt = $Attempt
        status = $startupStatus
        reason = $reason
        runRoot = $runRoot
        packagePath = $PackagePath
        appDataPath = $appDataPath
        started = $started
        processId = if ($null -eq $process) { 0 } else { $process.Id }
        exitCode = $exitCode
        forcedTermination = $forcedTermination
        residualProcess = $residualProcess
        startupLogExists = $startupLogExists
        healthLogCount = $healthFiles.Count
        sourcePathFindings = @($sourceFindings)
        preReleaseIdentity = Test-Path -LiteralPath (Join-Path $packageRunPath "V6_PACKAGE_MANIFEST.json") -PathType Leaf
        isolatedWriteRoot = $appDataPath
    }
}

function Invoke-MigrationLab {
    $projectPath = Join-Path $rootPath "tools\ClearFrost.MigrationProbe\ClearFrost.MigrationProbe.csproj"
    $buildOutput = @(& $DotnetPath "build" $projectPath "-c" "Debug" "-p:Platform=x64" "--no-restore" 2>&1)
    $buildCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if ($buildCode -ne 0) {
        Add-BlockingReason "Migration probe build failed."
    }
    $dllPath = Join-Path $rootPath "tools\ClearFrost.MigrationProbe\bin\x64\Debug\net8.0-windows10.0.17763.0\ClearFrost.MigrationProbe.dll"
    $reportPath = Join-Path $migrationRoot "migration-evidence.json"
    if ($buildCode -eq 0 -and (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        $output = @(& $DotnetPath $dllPath "--root" $migrationRoot "--output" $reportPath 2>&1)
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        $logPath = Join-Path $migrationRoot "migration-probe.log"
        [System.IO.File]::WriteAllLines($logPath, @($output | ForEach-Object { [string]$_ }), [System.Text.UTF8Encoding]::new($false))
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            Add-BlockingReason "Migration probe did not produce evidence."
            return [ordered]@{ status = "BLOCKED"; exitCode = $exitCode; reportPath = $reportPath; log = $logPath }
        }
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        return [ordered]@{ status = Get-String $report "Status"; exitCode = $exitCode; reportPath = $reportPath; log = $logPath; scenarios = $report.Scenarios; rollback = $report.Rollback }
    }
    return [ordered]@{ status = "BLOCKED"; exitCode = $buildCode; reportPath = $reportPath; reason = "Migration probe executable was unavailable." }
}

$releaseReport = if (Test-Path -LiteralPath $releaseEvidenceFile -PathType Leaf) { Get-Content -LiteralPath $releaseEvidenceFile -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$migration = Invoke-MigrationLab
$migrationStatus = Get-String $migration "status"
if ($migrationStatus -eq "BLOCKED") { Add-BlockingReason "Migration/rollback lab is BLOCKED." }

if ($null -eq $releaseReport) {
    Add-NotVerifiedReason "Release-lab evidence was not supplied; isolated package startup was not executed."
}
else {
    foreach ($package in @($releaseReport.packages | Where-Object { (Get-String $_ "status") -eq "PASS" })) {
        $mode = Get-String $package "mode"
        for ($attempt = 1; $attempt -le [Math]::Max(1, $Repetitions); $attempt++) {
            $record = Invoke-IsolatedStartup $mode (Get-String $package "path") $attempt
            [void]$startupRecords.Add($record)
            if ((Get-String $record "status") -ne "PASS") { Add-BlockingReason "$mode isolated startup attempt $attempt failed." }
        }
    }
    if (@($releaseReport.packages | Where-Object { (Get-String $_ "status") -eq "PASS" }).Count -eq 0) {
        Add-NotVerifiedReason "No positive Lite/Full package was available for isolated startup."
    }
}

$startupStatus = if ($startupRecords.Count -eq 0) { "NOT_VERIFIED" } elseif (@($startupRecords | Where-Object { (Get-String $_ "status") -ne "PASS" }).Count -eq 0) { "PASS" } else { "BLOCKED" }
$labStatus = if ($blockingReasons.Count -gt 0) { "BLOCKED" } elseif ($notVerifiedReasons.Count -gt 0) { "NOT_VERIFIED" } else { "PASS" }
$report = [ordered]@{
    schemaVersion = "v6-g2-isolated-lab-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    root = $rootPath
    releaseEvidencePath = $releaseEvidenceFile
    migration = $migration
    startup = [ordered]@{ status = $startupStatus; repetitions = $Repetitions; runs = @($startupRecords) }
    status = $labStatus
    promotionEligibility = "BLOCKED"
    blockingReasons = @($blockingReasons | Select-Object -Unique)
    notVerifiedReasons = @($notVerifiedReasons | Select-Object -Unique)
    isolationContract = [ordered]@{
        sourceRoot = $rootPath
        appDataOverride = "CLEARFROST_APPDATA_ROOT"
        profileOverride = "CLEARFROST_DML_PROFILE_ROOT"
        sourcePathWritesRejected = $true
        residualProcessRejected = $true
    }
}
[System.IO.File]::WriteAllText($evidenceFile, ($report | ConvertTo-Json -Depth 30), [System.Text.UTF8Encoding]::new($false))
if ($null -ne $releaseReport) {
    $releaseReport | Add-Member -NotePropertyName isolatedLab -NotePropertyValue $report -Force
    $releaseReport | Add-Member -NotePropertyName migration -NotePropertyValue $migration -Force
    [System.IO.File]::WriteAllText($releaseEvidenceFile, ($releaseReport | ConvertTo-Json -Depth 30), [System.Text.UTF8Encoding]::new($false))
}
Write-Output ($report | ConvertTo-Json -Depth 30)
if ($labStatus -eq "BLOCKED") { exit 1 }
if ($labStatus -eq "NOT_VERIFIED") { exit 2 }
exit 0
