$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd"
$shareDirName = "ClearFrost_Source_Share"
$shareDir = Join-Path $projectRoot $shareDirName
$zipName = "ClearFrost_Source_Share.zip"
$zipPath = Join-Path $projectRoot $zipName

Write-Host "Starting packaging..."

# 1. Cleaner
if (Test-Path $shareDir) { Remove-Item -Path $shareDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
New-Item -ItemType Directory -Path $shareDir | Out-Null
Write-Host "Step 1: Init directory done."

# 2. Copy Files
Write-Host "Step 2: Copying files..."
$excludeItems = @("bin", "obj", ".vs", ".git", ".venv", "*.user", "*.suo", "*.db", "*.log", "*.zip", "packages")

function Copy-ProjectFiles {
    param ([string]$Source, [string]$Destination)
    if (-not (Test-Path $Source)) { return }
    Copy-Item -Path $Source -Destination $Destination -Recurse -Force
    foreach ($item in $excludeItems) {
        Get-ChildItem -Path $Destination -Include $item -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Copy-Item -Path (Join-Path $projectRoot "ClearFrost.sln") -Destination $shareDir -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $projectRoot "README.md") -Destination $shareDir -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $projectRoot "ClearFrost\介绍.md") -Destination $shareDir -ErrorAction SilentlyContinue

Copy-ProjectFiles -Source (Join-Path $projectRoot "ClearFrost") -Destination (Join-Path $shareDir "ClearFrost")
Copy-ProjectFiles -Source (Join-Path $projectRoot "ClearFrost.Tests") -Destination (Join-Path $shareDir "ClearFrost.Tests")

# 3. Zip
Write-Host "Step 3: Zipping..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($shareDir, $zipPath)

# Cleanup
Remove-Item -Path $shareDir -Recurse -Force

Write-Host "Done! File created at: $zipPath"
