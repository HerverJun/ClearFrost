# ClearFrostV5 - 依赖包打包脚本
# 用于创建一个包含所有非代码依赖的压缩包

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outputFile = "ClearFrostV5_依赖包_$timestamp.zip"

Write-Host "正在打包运行依赖文件..." -ForegroundColor Green

function Add-EmptyDirectoryEntryToZip([string]$ZipPath, [string]$DirectoryEntry) {
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    $entryName = $DirectoryEntry.Replace("\", "/").Trim("/")
    if ([string]::IsNullOrWhiteSpace($entryName)) {
        return
    }

    $entryName = "$entryName/"
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $exists = $false
        foreach ($entry in $archive.Entries) {
            if ([string]::Equals($entry.FullName, $entryName, [System.StringComparison]::OrdinalIgnoreCase)) {
                $exists = $true
                break
            }
        }

        if (-not $exists) {
            [void]$archive.CreateEntry($entryName)
        }
    }
    finally {
        $archive.Dispose()
    }
}

# 创建临时目录
$tempDir = ".\temp_dependencies"
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

# 复制 DLL 文件
if (Test-Path ".\ClearFrost\DLL") {
    Write-Host "  ✓ 复制 ClearFrost/DLL/" -ForegroundColor Cyan
    Copy-Item -Path ".\ClearFrost\DLL" -Destination "$tempDir\ClearFrost\DLL" -Recurse -Force
}

# 复制 x64依赖包
if (Test-Path ".\x64依赖包") {
    Write-Host "  ✓ 复制 x64依赖包/" -ForegroundColor Cyan
    Copy-Item -Path ".\x64依赖包" -Destination "$tempDir\x64依赖包" -Recurse -Force
}

# 创建空 ONNX 目录，不打包任何模型文件
Write-Host "  ✓ 创建空 ClearFrost/ONNX/（不包含模型文件）" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$tempDir\ClearFrost\ONNX" | Out-Null

# 创建说明文件
$readmeContent = @"
# ClearFrostV5 依赖包使用说明

## 使用方法：

1. 从 Git 克隆源代码：
   git clone <项目仓库地址>
   cd ClearFrostV5

2. 将本压缩包解压到项目根目录，覆盖对应文件夹

3. 安装 NuGet 依赖：
   dotnet restore

4. 编译运行：
   dotnet build ClearFrost.sln -c Release -p:Platform=x64
   dotnet run --project ClearFrost/ClearFrost.csproj -c Debug

## 包含内容：

- ClearFrost/DLL/ - 第三方通讯库
- x64依赖包/ - 相机SDK依赖
- ClearFrost/ONNX/ - 空模型目录（模型文件不随依赖包分发）

## 注意事项：

- 这些文件由于体积较大，未包含在Git仓库中
- 请妥善保管此依赖包
- ONNX 模型文件不会打入依赖包，部署后请按现场项目单独放入 ClearFrost/ONNX/ 目录

生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@

$readmeContent | Out-File -FilePath "$tempDir\依赖包说明.txt" -Encoding UTF8

# 防止依赖目录中误混入模型文件
Get-ChildItem -LiteralPath $tempDir -Filter "*.onnx" -File -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force
New-Item -ItemType Directory -Force -Path "$tempDir\ClearFrost\ONNX" | Out-Null

# 压缩
Write-Host "`n正在压缩文件..." -ForegroundColor Green
Compress-Archive -Path "$tempDir\*" -DestinationPath $outputFile -Force
Add-EmptyDirectoryEntryToZip $outputFile "ClearFrost/ONNX"

# 清理临时目录
Remove-Item -Path $tempDir -Recurse -Force

# 显示结果
$fileSize = (Get-Item $outputFile).Length / 1MB
Write-Host "`n✅ 打包完成！" -ForegroundColor Green
Write-Host "文件名: $outputFile" -ForegroundColor Yellow
Write-Host "大小: $([math]::Round($fileSize, 2)) MB" -ForegroundColor Yellow
Write-Host "`n请将此文件与源代码一起保存，以便在其他电脑上部署。" -ForegroundColor Cyan
