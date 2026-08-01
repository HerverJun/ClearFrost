param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,
    [int]$MinimumHermeticTests = 853
)

$ErrorActionPreference = "Stop"
$errors = [System.Collections.Generic.List[string]]::new()

function Add-ValidationError([string]$Message) {
    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        [void]$errors.Add($Message)
    }
}

function Require-Property([object]$Object, [string]$Name) {
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Add-ValidationError "Missing evidence property: $Name"
        return $null
    }

    return $Object.PSObject.Properties[$Name].Value
}

function Is-CompleteSha([object]$Value) {
    return $null -ne $Value -and ([string]$Value) -match '^[0-9a-fA-F]{40}$'
}

if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
    Write-Error "Evidence file does not exist: $EvidencePath"
    exit 1
}

try {
    $evidence = Get-Content -LiteralPath $EvidencePath -Raw -Encoding UTF8 | ConvertFrom-Json

    if ((Require-Property $evidence "schemaVersion") -ne "v6-gate-1.0") {
        Add-ValidationError "Unsupported evidence schema version."
    }

    $headSha = Require-Property $evidence "headSha"
    $mainSha = Require-Property $evidence "mainSha"
    $remoteSha = Require-Property $evidence "remoteV6TestShaAtStart"
    foreach ($sha in @(
        @{ Name = "headSha"; Value = $headSha },
        @{ Name = "mainSha"; Value = $mainSha },
        @{ Name = "remoteV6TestShaAtStart"; Value = $remoteSha }
    )) {
        if (-not (Is-CompleteSha $sha.Value)) {
            Add-ValidationError "$($sha.Name) is not a complete 40-character SHA."
        }
        elseif ([string]$sha.Value -match '(?i)^(main|github/V6_test|origin/V6_test)$') {
            Add-ValidationError "$($sha.Name) contains a ref name instead of a SHA."
        }
    }

    $git = Require-Property $evidence "git"
    if ($null -ne $git) {
        foreach ($name in @("headSha", "mainSha", "remoteV6TestSha")) {
            $value = Require-Property $git $name
            if (-not (Is-CompleteSha $value)) {
                Add-ValidationError "git.$name is not a complete SHA."
            }
        }

        foreach ($name in @("repository", "workflowRunId", "workflowRunAttempt", "workflowRunUrl", "githubSha", "actionsIdentityStatus")) {
            [void](Require-Property $git $name)
        }

        if ($git.actionsIdentityStatus -eq "PASS") {
            if (-not (Is-CompleteSha $git.githubSha) -or
                -not [string]::Equals([string]$git.githubSha, [string]$git.headSha, [StringComparison]::OrdinalIgnoreCase)) {
                Add-ValidationError "Actions identity PASS requires githubSha to equal git.headSha."
            }
        }
    }

    $environment = Require-Property $evidence "environment"
    $dotnet = if ($null -eq $environment) { "" } else { [string](Require-Property $environment "dotnet") }
    if ($dotnet -notmatch '^8\.') {
        Add-ValidationError "Evidence dotnet SDK must be 8.x; actual '$dotnet'."
    }

    $development = Require-Property $evidence "developmentValidation"
    $overallStatus = [string](Require-Property $evidence "overallStatus")
    if ($null -ne $development -and $overallStatus -ne [string]$development.status) {
        Add-ValidationError "overallStatus must equal developmentValidation.status."
    }

    $promotion = Require-Property $evidence "promotion"
    $promotionEligibility = [string](Require-Property $evidence "promotionEligibility")
    if ($null -ne $promotion -and $promotionEligibility -ne [string]$promotion.status) {
        Add-ValidationError "promotionEligibility must equal promotion.status."
    }
    if ($promotionEligibility -eq "BLOCKED" -and @($evidence.promotionBlockingReasons).Count -eq 0) {
        Add-ValidationError "BLOCKED promotion must include promotionBlockingReasons."
    }

    $publish = Require-Property $evidence "publish"
    foreach ($profile in @("Lite", "Full")) {
        $publishStep = if ($null -eq $publish) { $null } else { Require-Property $publish $profile }
        if ($null -ne $publishStep -and [string]$publishStep.status -eq "NOT_VERIFIED") {
            $exitCodeProperty = $publishStep.PSObject.Properties["exitCode"]
            if ($null -ne $exitCodeProperty -and $null -ne $exitCodeProperty.Value) {
                Add-ValidationError "$profile NOT_VERIFIED publish evidence must not contain a non-null exitCode."
            }
        }
    }

    $tests = Require-Property $evidence "tests"
    $hermetic = if ($null -eq $tests) { $null } else { Require-Property $tests "hermetic" }
    if ($null -ne $hermetic) {
        $counters = Require-Property $hermetic "counters"
        if ($null -ne $counters) {
            if ([int]$counters.total -lt $MinimumHermeticTests) {
                Add-ValidationError "Hermetic total is below $MinimumHermeticTests."
            }
            if ([int]$counters.failed -ne 0 -or [int]$counters.errors -ne 0) {
                Add-ValidationError "Hermetic counters contain failures/errors."
            }
        }
        if ([string]$hermetic.status -eq "PASS" -and
            ($null -eq $counters -or [int]$counters.failed -ne 0 -or [int]$counters.errors -ne 0)) {
            Add-ValidationError "Hermetic PASS is inconsistent with its counters."
        }
    }

    $acceptance = Require-Property $evidence "acceptance"
    foreach ($name in 1..15 | ForEach-Object { "A$_" }) {
        $item = if ($null -eq $acceptance) { $null } else { Require-Property $acceptance $name }
        if ($null -eq $item) {
            continue
        }

        if ([string]$item.status -notin @("PASS", "BLOCKED", "NOT_VERIFIED")) {
            Add-ValidationError "$name has invalid status '$($item.status)'."
        }
        if ([string]::IsNullOrWhiteSpace([string]$item.reason)) {
            Add-ValidationError "$name must contain a reason matching its status."
        }
    }

    $a2 = $acceptance.A2
    if ($null -ne $a2 -and $a2.status -eq "PASS") {
        if ($evidence.build.restore.status -ne "PASS" -or
            -not (@($evidence.steps | Where-Object { $_.name -eq "tracked-only UTF-8 BOM and CRLF" -and $_.status -eq "PASS" }).Count -gt 0)) {
            Add-ValidationError "A2 PASS requires both encoding and Restore PASS."
        }
    }

    $a4 = $acceptance.A4
    if ($null -ne $a4 -and $a4.status -eq "PASS") {
        if ($null -eq $a4.counters -or [int]$a4.counters.failed -ne 0 -or [int]$a4.counters.errors -ne 0) {
            Add-ValidationError "A4 PASS requires zero hermetic failures/errors."
        }
    }

    $a6 = $acceptance.A6
    if ($null -ne $a6 -and $a6.status -eq "PASS" -and $git.actionsIdentityStatus -ne "PASS") {
        Add-ValidationError "A6 PASS requires Actions identity PASS."
    }

    $a13 = $acceptance.A13
    if ($null -ne $a13) {
        if ($null -eq $a13.negativeContract -or $null -eq $a13.positiveRelease) {
            Add-ValidationError "A13 must separately record negativeContract and positiveRelease."
        }
        elseif ([string]$a13.negativeContract.status -ne [string]$a13.status) {
            Add-ValidationError "A13 status must equal negativeContract.status."
        }
    }

    $a15 = $acceptance.A15
    if ($null -ne $a15 -and $a15.status -eq "PASS" -and [int]$a15.regressionFailures -ne 0) {
        Add-ValidationError "A15 PASS requires regressionFailures=0."
    }
}
catch {
    Add-ValidationError "Evidence JSON validation threw: $($_.Exception.Message)"
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "V6 evidence schema PASS: $EvidencePath"
exit 0
