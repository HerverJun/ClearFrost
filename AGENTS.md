# AGENTS.md - 清霜视觉检测系统 (ClearFrost)

## 项目概述

工业级智能视觉检测平台，基于 C# .NET 8.0，集成 YOLO AI 检测、多模型自动切换、现代 Web UI。

- **目标框架**: .NET 8.0 (net8.0-windows10.0.17763.0)
- **平台**: x64 (Windows)
- **主项目**: ClearFrost (WinForms + WPF + WebView2)
- **测试项目**: ClearFrost.Tests

---

## 构建命令

```bash
# 还原依赖
dotnet restore

# 构建整个解决方案 (Debug x64)
dotnet build ClearFrost.sln -c Debug -p:Platform=x64

# 构建发布版本
dotnet build ClearFrost.sln -c Release -p:Platform=x64

# 仅构建主项目
dotnet build ClearFrost/ClearFrost.csproj -c Debug -p:Platform=x64

# 运行主项目
dotnet run --project ClearFrost/ClearFrost.csproj -c Debug

# 发布应用
dotnet publish ClearFrost/ClearFrost.csproj -c Release -p:Platform=x64 --self-contained
```

---

## 测试命令

```bash
# 运行所有测试
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj

# 运行单个测试类
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --filter "FullyQualifiedName~YoloResultTests"

# 运行单个测试方法
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --filter "YoloResultTests.BoundingBox_计算正确"

# 详细输出
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj -v n

# 带覆盖率 (需要 coverlet)
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --collect:"XPlat Code Coverage"
```

---

## 代码风格指南

### 编码规范

- **编码**: UTF-8 BOM (详见 .editorconfig)
- **换行**: CRLF
- **缩进**: 4 个空格
- **可空性**: 启用 (`<Nullable>enable</Nullable>`)
- **隐式 using**: 启用

### 命名约定

| 类型 | 命名规范 | 示例 |
|------|----------|------|
| 类/结构体 | PascalCase | `YoloDetector`, `DetectionResult` |
| 接口 | IPascalCase | `IDetectionService`, `ICamera` |
| 方法 | PascalCase | `DetectAsync()`, `LoadModel()` |
| 属性 | PascalCase | `public string ModelName { get; set; }` |
| 公共字段 | PascalCase | `public float Confidence;` |
| 私有字段 | _camelCase | `private YoloDetector? _yolo;` |
| 常量 | PascalCase | `const int MaxRetries = 3;` |
| 枚举 | PascalCase | `enum PlcProtocolType` |
| 本地变量 | camelCase | `var detectionCount = 0;` |
| 类型参数 | TPascalCase | `TResult`, `TInput` |

### 文件组织

```csharp
// ============================================================================
// 文件名: ClassName.cs
// 描述:   简要描述文件功能
//
// 功能:
//   - 功能点1
//   - 功能点2
// ============================================================================

using System;
using System.Collections.Generic;
// 第三方库
using OpenCvSharp;
// 本项目命名空间
using ClearFrost.Yolo;

namespace ClearFrost.Services
{
    /// <summary>
    /// 类描述
    /// </summary>
    public class ServiceName : IServiceInterface
    {
        #region 私有字段
        
        private readonly IDependency _dependency;
        private bool _disposed;
        
        #endregion

        #region 事件
        
        public event Action<DetectionResult>? DetectionCompleted;
        
        #endregion

        #region 属性
        
        public string Name { get; set; } = string.Empty;
        
        #endregion

        #region 构造函数
        
        public ServiceName(IDependency dependency)
        {
            _dependency = dependency;
        }
        
        #endregion

        #region 公共方法
        
        public async Task<DetectionResult> DetectAsync(Mat image)
        {
            // 实现
        }
        
        #endregion

        #region 私有方法
        
        private void ProcessResult(DetectionResult result)
        {
            // 实现
        }
        
        #endregion

        #region IDisposable
        
        public void Dispose()
        {
            if (_disposed) return;
            _dependency?.Dispose();
            _disposed = true;
        }
        
        #endregion
    }
}
```

### 错误处理

```csharp
// 使用异常进行错误处理
public async Task<DetectionResult> DetectAsync(Mat image)
{
    try
    {
        if (image.Empty())
            throw new ArgumentException("图像为空", nameof(image));
        
        // 业务逻辑
    }
    catch (OnnxRuntimeException ex)
    {
        _logger.LogError(ex, "ONNX 推理失败");
        ErrorOccurred?.Invoke($"推理错误: {ex.Message}");
        throw; // 或返回错误结果
    }
}

// 使用 null 条件操作符和空合并
var model = _modelManager?.CurrentModel ?? throw new InvalidOperationException("模型未加载");

// 可空类型明确处理
public string? GetModelName() => _yolo?.ModelName;
```

### 异步编程

```csharp
// 异步方法命名以 Async 结尾
public async Task<DetectionResult> DetectAsync(Mat image)
{
    return await Task.Run(() => Detect(image));
}

// 使用 CancellationToken 支持取消
public async Task<DetectionResult> DetectAsync(
    Mat image, 
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    // ...
}

// 正确处理 IDisposable 和 async
await using var session = new InferenceSession(modelPath);
```

### 注释规范

```csharp
// 单行注释用于简短说明

/* 
 * 多行注释用于复杂逻辑说明
 */

/// <summary>
/// XML 文档注释用于公共 API
/// </summary>
/// <param name="image">输入图像</param>
/// <returns>检测结果</returns>
/// <exception cref="ArgumentException">图像为空时抛出</exception>
public DetectionResult Detect(Mat image)

// TODO: 标记待办事项
// HACK: 标记临时方案
// FIXME: 标记需要修复的问题
```

---

## 测试规范

```csharp
using Xunit;
using FluentAssertions;
using Moq;

namespace ClearFrost.Tests.Services;

public class DetectionServiceTests
{
    [Fact]
    public void Detect_正常图像_返回结果()
    {
        // Arrange
        var service = new DetectionService();
        var image = new Mat(100, 100, MatType.CV_8UC3);
        
        // Act
        var result = service.Detect(image);
        
        // Assert
        result.Should().NotBeNull();
        result.Detections.Should().HaveCountGreaterThan(0);
    }
    
    [Theory]
    [InlineData(0.5f, true)]
    [InlineData(0.9f, false)]
    public void IsConfident_不同阈值_返回正确结果(float threshold, bool expected)
    {
        // 参数化测试
    }
    
    [Fact]
    public async Task DetectAsync_异步操作_正确完成()
    {
        // 异步测试
        await service.DetectAsync(image);
    }
}
```

---

## 项目结构

```
ClearFrost/
├── Services/          # 业务服务层
├── Views/             # UI 层 (WinForms)
├── ViewModels/        # 视图模型
├── Models/            # 数据模型
├── Interfaces/        # 接口定义
├── Core/              # 核心逻辑
├── Yolo/              # YOLO 推理引擎
├── Vision/            # 传统视觉算法
├── Hardware/          # 硬件驱动
│   ├── Camera/        # 相机接口
│   └── PLC/           # PLC 通讯
├── Config/            # 配置管理
├── Helpers/           # 工具类
└── html/              # Web UI 前端
```

---

## 依赖说明

- **Microsoft.ML.OnnxRuntime.DirectML**: ONNX 推理 (GPU 加速)
- **OpenCvSharp4**: OpenCV 图像处理
- **Microsoft.Web.WebView2**: Web UI 嵌入
- **HslCommunication**: PLC 通讯库
- **Microsoft.Data.Sqlite**: 数据存储

---

## 注意事项

1. **x64 平台**: 项目强制使用 x64 平台，不要修改为 AnyCPU
2. **不安全代码**: 允许使用 unsafe 代码块 (`<AllowUnsafeBlocks>True</AllowUnsafeBlocks>`)
3. **本地 DLL**: MVSDK_Net.dll 等本地依赖放在 `DLL/` 和 `x64依赖包/` 目录
4. **模型文件**: ONNX 模型文件不提交到 Git (已在 .gitignore 中排除)
5. **中文编码**: 源代码文件使用 UTF-8 BOM 编码以支持中文注释
