param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$textExtensions = @(
    ".cs", ".csproj", ".sln", ".props", ".targets",
    ".json", ".md", ".ps1", ".psm1", ".yml", ".yaml",
    ".config", ".xml", ".html", ".css", ".js"
)

$textFileNames = @(
    ".editorconfig",
    ".gitattributes",
    ".gitignore"
)

$bomRequiredExtensions = @(".cs", ".ps1")
$skipFragments = @(
    "\.git\",
    "\bin\",
    "\obj\",
    "\node_modules\",
    "\tailwind.min.js",
    "\cropper.min.js",
    "\bundle.js"
)

$badFiles = [System.Collections.Generic.List[string]]::new()
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$mojibakeSentinels = @(
    [string][char]0xFFFD,
    [string][char]0x951F,
    [string][char]0x00C3,
    [string][char]0x00C2,
    [string][char]0x8119,
    [string][char]0x8117,
    [string][char]0x5A11,
    [string][char]0x95BA,
    [string][char]0x93B5,
    [string][char]0x93C0,
    [string][char]0x95BD,
    [string][char]0x9410,
    [string][char]0x9347,
    [string][char]0x7459,
    [string][char]0x5F42,
    [string][char]0x6D93,
    [string][char]0x93C4,
    [string][char]0x7039,
    [string][char]0x93B4,
    [string][char]0x94FB,
    [string][char]0x59AB,
    [string][char]0x5A34,
    [string][char]0x9366,
    [string][char]0x9367,
    [string][char]0x9356,
    [string][char]0x9225
)

function Test-SkippedPath([string]$Path) {
    $normalizedPath = $Path.Replace("/", "\")
    foreach ($fragment in $skipFragments) {
        if ($normalizedPath -like "*$fragment*") {
            return $true
        }
    }

    return $false
}

Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
    $file = $_
    $extension = $file.Extension.ToLowerInvariant()
    if ($textExtensions -notcontains $extension -and $textFileNames -notcontains $file.Name.ToLowerInvariant()) {
        return
    }

    if (Test-SkippedPath $file.FullName) {
        return
    }

    [byte[]]$bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF

    if ($bomRequiredExtensions -contains $extension -and -not $hasBom) {
        $badFiles.Add("Missing UTF-8 BOM: $($file.FullName)")
    }

    try {
        $offset = if ($hasBom) { 3 } else { 0 }
        $length = $bytes.Length - $offset
        $text = $strictUtf8.GetString($bytes, $offset, $length)
    }
    catch {
        $badFiles.Add("Invalid UTF-8: $($file.FullName)")
        return
    }

    if ($text -match "(?<!`r)`n") {
        $badFiles.Add("Non-CRLF newline: $($file.FullName)")
    }

    foreach ($sentinel in $mojibakeSentinels) {
        if ($text.Contains($sentinel)) {
            $badFiles.Add("Mojibake sentinel U+{0:X4}: {1}" -f [int][char]$sentinel[0], $file.FullName)
            break
        }
    }
}

if ($badFiles.Count -gt 0) {
    $badFiles | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}

Write-Host "Text encoding check passed."
