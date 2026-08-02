function Resolve-V6G2Path([string]$Root, [string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return "" }
    if ([System.IO.Path]::IsPathRooted($PathValue)) { return [System.IO.Path]::GetFullPath($PathValue) }
    return [System.IO.Path]::GetFullPath((Join-Path $Root $PathValue))
}

function Get-V6G2FileSha256([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue) -or -not (Test-Path -LiteralPath $PathValue -PathType Leaf)) { return "" }
    try { return (Get-FileHash -LiteralPath $PathValue -Algorithm SHA256).Hash.ToUpperInvariant() } catch { return "" }
}

function Get-V6G2Sha256([string]$Value) {
    if ($null -eq $Value) { $Value = "" }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Value)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "") } finally { $sha.Dispose() }
}

function Get-V6G2CommitSha([string]$Root) {
    $environmentSha = [string]$env:GITHUB_SHA
    if ($environmentSha -match '^[0-9A-Fa-f]{40}$') { return $environmentSha.Trim().ToLowerInvariant() }
    try {
        $value = [string](git -C $Root rev-parse HEAD 2>$null)
        if ($value.Trim() -match '^[0-9A-Fa-f]{40}$') { return $value.Trim().ToLowerInvariant() }
    } catch { }
    return ""
}

function Get-V6G2ProductVersion([string]$Root) {
    $projectPath = Join-Path $Root "ClearFrost\ClearFrost.csproj"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { return "" }
    $text = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
    $match = [regex]::Match($text, '<Version>(?<version>[^<]+)</Version>')
    if ($match.Success) { return $match.Groups["version"].Value.Trim() }
    $prefix = [regex]::Match($text, '<VersionPrefix>(?<version>[^<]+)</VersionPrefix>')
    if ($prefix.Success) { return $prefix.Groups["version"].Value.Trim() }
    return ""
}

function Get-V6G2MachineIdentityDigest {
    return Get-V6G2Sha256 ((@([Environment]::MachineName, [Environment]::OSVersion.VersionString, [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) -join "|"))
}

function Get-V6G2ProductAssemblyPath([string]$Root) {
    $assemblyName = "ClearFrost"
    $projectPath = Join-Path $Root "ClearFrost\ClearFrost.csproj"
    if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
        $projectText = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
        $match = [regex]::Match($projectText, '<AssemblyName>(?<name>[^<]+)</AssemblyName>')
        if ($match.Success) { $assemblyName = $match.Groups["name"].Value.Trim() }
    }
    foreach ($candidate in @(
            (Join-Path $Root "ClearFrost\bin\x64\Debug\net8.0-windows10.0.17763.0\$assemblyName.dll"),
            (Join-Path $Root "ClearFrost\bin\x64\Release\net8.0-windows10.0.17763.0\$assemblyName.dll"),
            (Join-Path $Root "ClearFrost\bin\x64\Debug\net8.0-windows10.0.17763.0\ClearFrost.dll"),
            (Join-Path $Root "ClearFrost\bin\x64\Release\net8.0-windows10.0.17763.0\ClearFrost.dll"))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return ""
}

function Get-V6G2Value([object]$Object, [string[]]$Names) {
    if ($null -eq $Object) { return "" }
    foreach ($name in $Names) {
        if ($null -ne $Object.PSObject.Properties[$name]) { return [string]$Object.PSObject.Properties[$name].Value }
    }
    return ""
}

function Get-V6G2Long([object]$Object, [string[]]$Names) {
    foreach ($name in $Names) {
        if ($null -ne $Object -and $null -ne $Object.PSObject.Properties[$name]) {
            $value = 0L
            if ([long]::TryParse([string]$Object.PSObject.Properties[$name].Value, [ref]$value)) { return $value }
        }
    }
    return 0L
}

function Get-V6G2ExternalDependencies([string]$Root, [string]$InputManifestPath, [object[]]$ExternalDependencies) {
    $records = @($ExternalDependencies)
    if ($records.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($InputManifestPath)) {
        $path = Resolve-V6G2Path $Root $InputManifestPath
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try {
                $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
                $records = @($manifest.dependencies)
            } catch { $records = @() }
        }
    }
    return @($records | ForEach-Object {
        [ordered]@{
            name = Get-V6G2Value $_ @("name", "fileName")
            version = Get-V6G2Value $_ @("version")
            bytes = Get-V6G2Long $_ @("actualBytes", "expectedBytes", "bytes")
            sha256 = (Get-V6G2Value $_ @("actualSha256", "expectedSha256", "sha256")).Trim().ToUpperInvariant()
            role = Get-V6G2Value $_ @("role")
        }
    } | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.name) -and
        [long]$_.bytes -gt 0 -and
        [string]$_.sha256 -match '^[0-9A-F]{64}$'
    } | Sort-Object name, version)
}

function Get-V6G2ExternalDependencySetDigest([object[]]$Dependencies) {
    $canonical = @($Dependencies | Sort-Object name, version | ForEach-Object {
        "{0}|{1}|{2}|{3}|{4}" -f $_.name, $_.version, ([long]$_.bytes), $_.sha256, $_.role
    }) -join "`n"
    return Get-V6G2Sha256 $canonical
}

function Get-V6G2OrchestratorRunId {
    if (-not [string]::IsNullOrWhiteSpace([string]$env:CLEARFROST_V6_G2_ORCHESTRATOR_RUN_ID)) { return $env:CLEARFROST_V6_G2_ORCHESTRATOR_RUN_ID.Trim() }
    if (-not [string]::IsNullOrWhiteSpace([string]$env:GITHUB_RUN_ID)) {
        $attempt = if ([string]::IsNullOrWhiteSpace([string]$env:GITHUB_RUN_ATTEMPT)) { "1" } else { $env:GITHUB_RUN_ATTEMPT.Trim() }
        return "github:$($env:GITHUB_RUN_ID.Trim()):$attempt"
    }
    return "local-unbound"
}

function New-V6G2EvidenceIdentity {
    param(
        [string]$Root,
        [string]$InputManifestPath = "",
        [string]$DetectModelPath = "",
        [string]$ValidationImagePath = "",
        [string]$ProductAssemblyPath = "",
        [string]$Provider = "NOT_APPLICABLE",
        [string]$RunStartedAtUtc = "",
        [string]$RunFinishedAtUtc = "",
        [object[]]$ExternalDependencies = @()
    )

    $started = if ([string]::IsNullOrWhiteSpace($RunStartedAtUtc)) { [DateTime]::UtcNow.ToString("o") } else { $RunStartedAtUtc }
    $finished = if ([string]::IsNullOrWhiteSpace($RunFinishedAtUtc)) { [DateTime]::UtcNow.ToString("o") } else { $RunFinishedAtUtc }
    $assemblyPath = if ([string]::IsNullOrWhiteSpace($ProductAssemblyPath)) { Get-V6G2ProductAssemblyPath $Root } else { Resolve-V6G2Path $Root $ProductAssemblyPath }
    $dependencies = @(Get-V6G2ExternalDependencies $Root $InputManifestPath $ExternalDependencies)
    $commitSha = Get-V6G2CommitSha $Root
    $productVersion = Get-V6G2ProductVersion $Root
    $inputManifestSha256 = Get-V6G2FileSha256 (Resolve-V6G2Path $Root $InputManifestPath)
    $detectModelSha256 = Get-V6G2FileSha256 (Resolve-V6G2Path $Root $DetectModelPath)
    $validationImageSha256 = Get-V6G2FileSha256 (Resolve-V6G2Path $Root $ValidationImagePath)
    $productAssemblySha256 = Get-V6G2FileSha256 $assemblyPath
    $externalDependencySetDigest = Get-V6G2ExternalDependencySetDigest $dependencies
    $candidateDigest = Get-V6G2Sha256 (@($commitSha, $productVersion, $inputManifestSha256, $detectModelSha256, $validationImageSha256, $productAssemblySha256, $externalDependencySetDigest) -join "`n")
    $orchestratorRunId = Get-V6G2OrchestratorRunId
    $evidenceSetId = if ([string]::IsNullOrWhiteSpace([string]$env:CLEARFROST_V6_G2_EVIDENCE_SET_ID)) {
        Get-V6G2Sha256 "$candidateDigest|$orchestratorRunId"
    } else { $env:CLEARFROST_V6_G2_EVIDENCE_SET_ID.Trim() }
    return [ordered]@{
        commitSha = $commitSha
        productVersion = $productVersion
        inputManifestSha256 = $inputManifestSha256
        detectModelSha256 = $detectModelSha256
        validationImageSha256 = $validationImageSha256
        productAssemblySha256 = $productAssemblySha256
        externalDependencySetDigest = $externalDependencySetDigest
        externalDependencies = @($dependencies)
        candidateDigest = $candidateDigest
        evidenceSetId = $evidenceSetId
        orchestratorRunId = $orchestratorRunId
        workflowRunId = ([string]$env:GITHUB_RUN_ID).Trim()
        provider = if ([string]::IsNullOrWhiteSpace($Provider)) { "NOT_APPLICABLE" } else { $Provider }
        machineIdentityDigest = Get-V6G2MachineIdentityDigest
        runStartedAtUtc = $started
        runFinishedAtUtc = $finished
    }
}
