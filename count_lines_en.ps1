[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$projectRoot = $PSScriptRoot
$path = Join-Path $projectRoot "ClearFrost"
Write-Host "Scanning: $path"

if (Test-Path $path) {
    $files = Get-ChildItem -Path $path -Include *.cs, *.html, *.js, *.css -Recurse | Where-Object { $_.FullName -notmatch "\\(bin|obj|Properties|.vs)\\" }
    if ($files) {
        $stats = $files | Group-Object Extension | Select-Object Name, Count, @{N = 'Lines'; E = { ($_.Group | Get-Content | Measure-Object -Line).Lines } }
        $stats | Format-Table -AutoSize
        $total = ($stats | Measure-Object -Property Lines -Sum).Sum
        Write-Host "Total Lines: $total"
    }
    else {
        Write-Host "No files found."
    }
}
else {
    Write-Host "Path not found."
}
