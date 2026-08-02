function Resolve-V6G2Path([string]$Root, [string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return ""
    }
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $Root $PathValue))
}

function Get-V6G2FileSha256([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue) -or -not (Test-Path -LiteralPath $PathValue -PathType Leaf)) {
        return ""
    }
    try {
        return (Get-FileHash -LiteralPath $PathValue -Algorithm SHA256).Hash.ToUpperInvariant()
    }
    catch {
        return ""
    }
}

function Get-V6G2CommitSha([string]$Root) {
    $environmentSha = [string]$env:GITHUB_SHA
    if ($environmentSha -match '^[0-9A-Fa-f]{40}$') {
        return $environmentSha.Trim().ToLowerInvariant()
    }
    try {
        $value = [string](git -C $Root rev-parse HEAD 2>$null)
        if ($value.Trim() -match '^[0-9A-Fa-f]{40}$') {
            return $value.Trim().ToLowerInvariant()
        }
    }
    catch { }
    return ""
}

function Get-V6G2ProductVersion([string]$Root) {
    $projectPath = Join-Path $Root "ClearFrost\ClearFrost.csproj"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        return ""
    }
    $text = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
    $match = [regex]::Match($text, '<Version>(?<version>[^<]+)</Version>')
    if ($match.Success) {
        return $match.Groups["version"].Value.Trim()
    }
    $prefix = [regex]::Match($text, '<VersionPrefix>(?<version>[^<]+)</VersionPrefix>')
    if ($prefix.Success) {
        return $prefix.Groups["version"].Value.Trim()
    }
    return ""
}

function Get-V6G2MachineIdentityDigest {
    $payload = @(
        [Environment]::MachineName,
        [Environment]::OSVersion.VersionString,
        [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    ) -join "|"
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($payload)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToUpperInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-V6G2ProductionDllPath([string]$Root) {
    $assemblyName = "ClearFrost"
    $projectPath = Join-Path $Root "ClearFrost\ClearFrost.csproj"
    if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
        $projectText = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
        $match = [regex]::Match($projectText, '<AssemblyName>(?<name>[^<]+)</AssemblyName>')
        if ($match.Success) {
            $assemblyName = $match.Groups["name"].Value.Trim()
        }
    }

    $candidates = @(
        (Join-Path $Root "ClearFrost\bin\x64\Debug\net8.0-windows10.0.17763.0\$assemblyName.dll"),
        (Join-Path $Root "ClearFrost\bin\x64\Release\net8.0-windows10.0.17763.0\$assemblyName.dll"),
        (Join-Path $Root "ClearFrost\bin\x64\Debug\net8.0-windows10.0.17763.0\ClearFrost.dll"),
        (Join-Path $Root "ClearFrost\bin\x64\Release\net8.0-windows10.0.17763.0\ClearFrost.dll")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    return ""
}

function New-V6G2EvidenceIdentity {
    param(
        [string]$Root,
        [string]$InputManifestPath = "",
        [string]$DetectModelPath = "",
        [string]$ValidationImagePath = "",
        [string]$DllPath = "",
        [string]$Provider = "NOT_VERIFIED",
        [string]$RunStartedAtUtc = "",
        [string]$RunFinishedAtUtc = ""
    )

    $started = if ([string]::IsNullOrWhiteSpace($RunStartedAtUtc)) { [DateTime]::UtcNow.ToString("o") } else { $RunStartedAtUtc }
    $finished = if ([string]::IsNullOrWhiteSpace($RunFinishedAtUtc)) { [DateTime]::UtcNow.ToString("o") } else { $RunFinishedAtUtc }
    $resolvedDll = if ([string]::IsNullOrWhiteSpace($DllPath)) { Get-V6G2ProductionDllPath $Root } else { Resolve-V6G2Path $Root $DllPath }
    return [ordered]@{
        commitSha = Get-V6G2CommitSha $Root
        productVersion = Get-V6G2ProductVersion $Root
        inputManifestSha256 = Get-V6G2FileSha256 (Resolve-V6G2Path $Root $InputManifestPath)
        detectModelSha256 = Get-V6G2FileSha256 (Resolve-V6G2Path $Root $DetectModelPath)
        validationImageSha256 = Get-V6G2FileSha256 (Resolve-V6G2Path $Root $ValidationImagePath)
        dllSha256 = Get-V6G2FileSha256 $resolvedDll
        provider = if ([string]::IsNullOrWhiteSpace($Provider)) { "NOT_VERIFIED" } else { $Provider }
        machineIdentityDigest = Get-V6G2MachineIdentityDigest
        runStartedAtUtc = $started
        runFinishedAtUtc = $finished
    }
}
