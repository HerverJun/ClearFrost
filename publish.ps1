$projectPath = ".\YOLO\YOLO.csproj"
$publishDir = ".\PublishOutput"

# Clean previous publish
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

# Publish
Write-Host "Publishing application..." -ForegroundColor Green
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishDir /p:DebugType=None /p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish FAILED!" -ForegroundColor Red
    exit 1
}

# Verify Critical Files
$onnxDir = Join-Path $publishDir "ONNX"
$htmlDir = Join-Path $publishDir "html"
$exePath = Join-Path $publishDir "YOLO.exe"

if (-not (Test-Path $onnxDir)) {
    Write-Host "WARNING: ONNX folder is MISSING!" -ForegroundColor Yellow
}
else {
    $count = (Get-ChildItem $onnxDir *.onnx).Count
    Write-Host "ONNX Model count: $count" -ForegroundColor Cyan
}

if (-not (Test-Path $htmlDir)) {
    Write-Host "WARNING: html folder is MISSING!" -ForegroundColor Red
}

if (Test-Path $exePath) {
    Write-Host "SUCCESS! Output path: $publishDir" -ForegroundColor Green
    Write-Host "Please zip the '$publishDir' folder and copy it to the target machine." -ForegroundColor Green
}
else {
    Write-Host "FAILED: .exe not found in output." -ForegroundColor Red
}
