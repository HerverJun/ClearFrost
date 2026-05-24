param(
    [string]$SamplesDir = "samples/yolo-official",
    [int]$Warmup = 1,
    [int]$Iterations = 3
)

$ErrorActionPreference = "Stop"
$reportsDir = Join-Path $SamplesDir "reports"
New-Item -ItemType Directory -Force -Path $reportsDir | Out-Null

$models = @(
    @{ Task = "detect"; Path = Join-Path $SamplesDir "detect/yolo11n.onnx" },
    @{ Task = "segment"; Path = Join-Path $SamplesDir "segment/yolo11n-seg.onnx" },
    @{ Task = "pose"; Path = Join-Path $SamplesDir "pose/yolo11n-pose.onnx" },
    @{ Task = "obb"; Path = Join-Path $SamplesDir "obb/yolo11n-obb.onnx" },
    @{ Task = "classify"; Path = Join-Path $SamplesDir "classify/yolo11n-cls.onnx" }
)

$summary = @()
foreach ($model in $models) {
    if (-not (Test-Path $model.Path)) {
        throw "Missing ONNX sample: $($model.Path)"
    }

    $reportPath = Join-Path $reportsDir "$($model.Task)-probe.json"
    Write-Host "[accept] $($model.Task) $($model.Path)"
    dotnet run --project tools/ClearFrost.YoloProbe -- `
        --model $model.Path `
        --task $model.Task `
        --benchmark `
        --warmup $Warmup `
        --iterations $Iterations `
        --out $reportPath

    if ($LASTEXITCODE -ne 0) {
        throw "YOLO probe failed for $($model.Task)"
    }

    $json = Get-Content -Raw -Path $reportPath | ConvertFrom-Json
    $summary += [pscustomobject]@{
        Task = $model.Task
        Path = $model.Path
        Supported = $json.Descriptor.IsSupported
        Layout = $json.Descriptor.PostprocessProfile.Layout
        Classes = $json.Descriptor.Labels.Count
        AvgMs = [math]::Round([double]$json.Benchmark.AverageMs, 2)
        P95Ms = [math]::Round([double]$json.Benchmark.P95Ms, 2)
        Fps = [math]::Round([double]$json.Benchmark.Fps, 1)
    }
}

$summaryPath = Join-Path $reportsDir "acceptance-summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path $summaryPath
$summary | Format-Table -AutoSize
Write-Host "[summary] $summaryPath"
