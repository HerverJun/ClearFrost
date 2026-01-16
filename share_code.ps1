# 设置编码
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$projectRoot = $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd"
$shareDirName = "ClearFrost_Source_Share"
$shareDir = Join-Path $projectRoot $shareDirName
$zipName = "ClearFrost_Source_Share.zip"
$zipPath = Join-Path $projectRoot $zipName

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   正在准备分享代码包 (PowerShell版)..."
Write-Host "========================================================"
Write-Host ""

# 1. 清理旧文件
if (Test-Path $shareDir) {
    Remove-Item -Path $shareDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}
New-Item -ItemType Directory -Path $shareDir | Out-Null
Write-Host "[Step 1/3] 初始化目录完成" -ForegroundColor Green

# 2. 复制文件
Write-Host "[Step 2/3] 正在复制文件..." -ForegroundColor Green

# 定义要排除的文件夹和文件类型
$excludeItems = @("bin", "obj", ".vs", ".git", ".venv", "*.user", "*.suo", "*.db", "*.log", "*.zip", "packages")

# 复制函数的定义
function Copy-ProjectFiles {
    param (
        [string]$Source,
        [string]$Destination
    )
    
    if (-not (Test-Path $Source)) { return }

    Write-Host "   - 正在复制: $(Split-Path $Source -Leaf)"
    
    # 使用 Robocopy 的替代方案：Get-ChildItem 递归复制
    Copy-Item -Path $Source -Destination $Destination -Recurse -Force
    
    # 清理排除的项目
    foreach ($item in $excludeItems) {
        Get-ChildItem -Path $Destination -Include $item -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 2.1 复制根目录文件
Copy-Item -Path (Join-Path $projectRoot "ClearFrost.sln") -Destination $shareDir -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $projectRoot "README.md") -Destination $shareDir -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $projectRoot "ClearFrost\介绍.md") -Destination $shareDir -ErrorAction SilentlyContinue

# 2.2 复制主要项目目录
Copy-ProjectFiles -Source (Join-Path $projectRoot "ClearFrost") -Destination (Join-Path $shareDir "ClearFrost")
Copy-ProjectFiles -Source (Join-Path $projectRoot "ClearFrost.Tests") -Destination (Join-Path $shareDir "ClearFrost.Tests")

Write-Host ""
Write-Host "[Step 3/3] 正在压缩..." -ForegroundColor Green

# 3. 压缩
Compress-Archive -Path $shareDir -DestinationPath $zipPath -Force

# 清理
Remove-Item -Path $shareDir -Recurse -Force

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   打包完成!"
Write-Host "   文件已生成: $zipPath"
Write-Host "========================================================"
Write-Host ""
Write-Host "按回车键退出..."
Read-Host
