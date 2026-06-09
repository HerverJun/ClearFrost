# PLC 测试说明

本目录下的测试按职责划分为 3 组，目的是让 PLC 相关回归验证更容易筛选、定位和扩展。

## 分类

### `PLC.Factory`

文件：`ClearFrost.Tests/Hardware/PLC/PlcFactoryTests.cs`

覆盖范围：

- `Hsl` 驱动下 5 种协议的工厂创建
- `McpX` 驱动下三菱 ASCII / Binary 的创建
- `McpX` 驱动对非三菱协议的拒绝行为

适合验证：

- 驱动选择逻辑
- 工厂分发逻辑
- 新增协议时的工厂回归

### `PLC.AdapterState`

文件：`ClearFrost.Tests/Hardware/PLC/PlcRecoveryTests.cs`

覆盖范围：

- `Hsl` 适配器在失败路径下是否正确降级连接状态
- `McpX` 适配器在空连接 / 失败路径下是否正确降级连接状态

适合验证：

- “读写失败后仍自认已连接”的回归问题
- 适配器层状态分裂问题

### `PLC.ServiceRecovery`

文件：`ClearFrost.Tests/Hardware/PLC/PlcRecoveryTests.cs`

覆盖范围：

- `PlcService.MonitoringLoop()` 在读失败后的坏连接清理
- `PlcService.TryReconnectAsync()` 在重连失败后的设备引用清理

适合验证：

- 服务层异常恢复逻辑
- 坏连接对象是否被丢弃
- 自动重连前是否回到干净状态

---

## 运行方式

### 运行全部 PLC 测试

```bash
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --filter "FullyQualifiedName~Hardware.PLC"
```

### 只运行工厂测试

```bash
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --filter "FullyQualifiedName~PlcFactoryTests"
```

### 只运行适配器状态测试

```bash
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --filter "FullyQualifiedName~PlcAdapterStateTests"
```

### 只运行服务恢复测试

```bash
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --filter "FullyQualifiedName~PlcServiceRecoveryTests"
```

---

## 当前边界

这些测试属于“非侵入式测试骨架”，特点是：

- 不修改主项目可测试性结构
- 不引入真实 PLC 或网络依赖
- 优先验证失败路径上的状态变化与资源清理

当前没有覆盖的内容：

- 真实 PLC 网络抖动下的端到端恢复
- UI 层状态展示与日志联动
- 多入口并发触发下的重连协同

如果后续要继续扩展，建议优先补：

1. `WriteResultAsync()` / `WriteReleaseSignalAsync()` 的失败恢复测试
2. 触发监听与自动重连的更细粒度时序测试
3. 面向真实设备或模拟器的集成测试
