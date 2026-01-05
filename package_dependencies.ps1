# 清霜V2 - 依赖包打包脚本
# 用于创建一个包含所有非代码依赖的压缩包

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outputFile = "清霜V2_依赖包_$timestamp.zip"

Write-Host "正在打包运行依赖文件..." -ForegroundColor Green

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

# 复制 ONNX 模型（可选）
if (Test-Path ".\ClearFrost\ONNX") {
    Write-Host "  ✓ 复制 ClearFrost/ONNX/ (模型文件)" -ForegroundColor Cyan
    Copy-Item -Path ".\ClearFrost\ONNX" -Destination "$tempDir\ClearFrost\ONNX" -Recurse -Force
}

# 创建说明文件
$readmeContent = @"
# 清霜V2 依赖包使用说明

## 使用方法：

1. 从 Git 克隆源代码：
   git clone https://gitee.com/jiao-xiake/ClearForst.git
   cd ClearForst

2. 将本压缩包解压到项目根目录，覆盖对应文件夹

3. 安装 NuGet 依赖：
   dotnet restore

4. 编译运行：
   dotnet build -c Release --arch x64
   dotnet run --project ClearFrost/ClearFrost.csproj

## 包含内容：

- ClearFrost/DLL/ - 第三方通讯库
- x64依赖包/ - 相机SDK依赖
- ClearFrost/ONNX/ - 训练好的AI模型

## 注意事项：

- 这些文件由于体积较大，未包含在Git仓库中
- 请妥善保管此依赖包
- 如需更新模型，只需替换 ClearFrost/ONNX/ 目录中的 .onnx 文件

生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@

$readmeContent | Out-File -FilePath "$tempDir\依赖包说明.txt" -Encoding UTF8

# 压缩
Write-Host "`n正在压缩文件..." -ForegroundColor Green
Compress-Archive -Path "$tempDir\*" -DestinationPath $outputFile -Force

# 清理临时目录
Remove-Item -Path $tempDir -Recurse -Force

# 显示结果
$fileSize = (Get-Item $outputFile).Length / 1MB
Write-Host "`n✅ 打包完成！" -ForegroundColor Green
Write-Host "文件名: $outputFile" -ForegroundColor Yellow
Write-Host "大小: $([math]::Round($fileSize, 2)) MB" -ForegroundColor Yellow
Write-Host "`n请将此文件与源代码一起保存，以便在其他电脑上部署。" -ForegroundColor Cyan
