# ClearFrost V4 工业运行平台 3-Day 增量升级计划

> 修订说明：本计划根据当前 ClearFrost V3 实际代码基线重写。原计划的方向正确，但一次性把 `InspectionEngine`、PLC 握手、Recipe、ModelRegistry、HealthMonitor、StartupDiagnostics、诊断包和压测全部做成完整平台，风险过高。新版计划改为“稳定增量主干”：先追溯闭环，再工业握手和健康监控，最后轻量平台化与现场工具。

---

## 0. 当前项目基线

当前项目不是从零开始，已经有一批可复用的稳定基础：

```text
AppRuntime 服务聚合
ICameraService / IDetectionService / IPlcService / IDatabaseService / IStorageService
DetectionTriggerGate 检测并发保护
ExecuteDetectionCycleAsync 检测主流程
ImageSaveQueue 图像异步保存队列
DetectionRecordQueue 数据库异步记录队列
MultiModelManager 多模型 fallback
AppConfig 原子保存和旧配置兼容
SQLite 旧库迁移
PLC 多协议支持和自动重连
MockCamera / 调试相机能力
```

当前基线验证：

```bash
dotnet build ClearFrost.sln -c Debug -p:Platform=x64
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj
```

审阅时已确认：

```text
build: 0 warning, 0 error
tests: 116 passed
```

因此，本轮升级的核心原则不是重造系统，而是把已有机制串成工业可追溯、可诊断、可回滚的主干。

---

## 1. 本轮总目标

3 天内完成可交付的 V4 主干预览版：

```text
InspectionId 全链路追溯
兼容旧流程的 InspectionContext
数据库兼容迁移
图片文件名和记录路径可追溯
PLC Legacy 保持默认
PLC HandshakeV1 最小可用
TriggerSeq / ResultSeq 防错位
TraceStatus 最小闭环
HealthMonitor / HealthSnapshot 基础版
RecipeManager 轻量版
ModelRegistry 轻量版
模型包 manifest/hash/labels/warmup 校验
StartupDiagnostics 基础版
诊断包导出基础版
无硬件模拟压测入口
```

本轮不追求完整平台 UI，不追求所有现场工具一次到位，不把旧配置和旧模型路径废掉。

---

## 2. 必须修正原计划的地方

### 2.1 InspectionEngine 不应第一天彻底接管

当前检测主流程集中在：

```text
ClearFrost/Views/主窗口.Vision.cs
ExecuteDetectionCycleAsync
```

它已经串起：

```text
取图
推理
ROI 过滤
PLC 写入
UI 展示
图像保存队列
数据库记录队列
统计
性能日志
```

第一天不建议把整段流程搬到新 `InspectionEngine`。正确做法：

```text
先新增 InspectionId / InspectionContext
用 InspectionContext 包住现有 ExecuteDetectionCycleAsync
保留旧入口
稳定后再逐步把流程抽到 InspectionEngine
```

### 2.2 数据库迁移必须明确

当前 `DetectionRecords` 表字段较少，需要兼容新增：

```text
InspectionId
TriggerSource
TriggerSeq
ResultSeq
TraceStatus
ImagePath
RenderedImagePath
ErrorStage
ErrorCode
ErrorMessage
TotalMs
CaptureMs
InferenceMs
RoiMs
PlcWriteMs
SaveImageMs
SaveRecordMs
RecipeId
RecipeVersion
ModelId
ModelVersion
ModelHash
WasFallback
UsedModelName
```

迁移要求：

```text
只能 ALTER TABLE ADD COLUMN
不得删除旧字段
不得重建表导致旧记录丢失
InspectionId 建索引
Timestamp / IsQualified 旧索引保留
旧库导入逻辑继续可用
旧 DetectionRecord 查询继续可用
```

### 2.3 TraceStatus 不能假装同步完成

当前图像和数据库都是异步队列。检测完成时只能知道是否入队，不能马上知道后台最终写盘是否成功。

因此第一版 TraceStatus 应这样定义：

```csharp
public enum TraceStatus
{
    Unknown,
    Queued,
    Full,
    Partial,
    Failed,
    Disabled
}
```

第一阶段规则：

```text
图像入队成功 + 数据库入队成功 -> Queued
图像入队失败 + 数据库入队成功 -> Partial
图像入队成功 + 数据库入队失败 -> Partial
图像入队失败 + 数据库入队失败 -> Failed
未启用追溯 -> Disabled
```

后台队列保存失败先进入 HealthMonitor 报警。后续版本再做记录回写，把 `Queued` 更新为 `Full/Partial/Failed`。

### 2.4 PLC HandshakeV1 需要触发上下文

当前 `IPlcService.TriggerReceived` 没有参数，无法传递 `TriggerSeq`。

本轮应新增：

```csharp
public sealed class PlcTriggerContext
{
    public string TriggerSource { get; init; } = "PLC";
    public int? TriggerSeq { get; init; }
    public DateTimeOffset TriggerTime { get; init; }
}
```

并扩展事件：

```csharp
event Action<PlcTriggerContext>? TriggerContextReceived;
```

旧事件保留：

```csharp
event Action? TriggerReceived;
```

Legacy 模式继续触发旧事件或传 `TriggerSeq = null`。

### 2.5 Recipe / ModelRegistry 第一版只做轻量兼容层

当前 `AppConfig` 已经承载目标标签、目标数量、主模型、辅助模型、fallback、GPU、阈值等关键设置。第一版 Recipe 不应夺走配置主权。

正确做法：

```text
RecipeManager 从 AppConfig 生成 default recipe
Recipe 保存为 AppConfig 的平台化快照
旧 AppConfig 仍是启动主入口
旧 ONNX 裸模型目录继续可用
模型包 manifest 缺失时 Warning，不直接阻止旧模式启动
启用严格模型包模式后，manifest/hash/labels/warmup 失败才阻止生产 Ready
```

---

## 3. 三天执行节奏

## Day 1：追溯最小闭环

目标：

```text
不大改检测流程，先让每次检测有唯一 InspectionId，并能串联日志、数据库、图片和前端。
```

任务：

```text
[x] 新增 Core/Inspection/InspectionIdGenerator
[x] 新增 Core/Inspection/InspectionContext
[x] 新增 Core/Inspection/InspectionStage
[x] 新增 Core/Inspection/TraceStatus
[x] 新增 DetectionCycleRequest 字段：InspectionId / TriggerSeq
[x] btnCapture_LogicAsync 开始时生成 InspectionId
[x] ExecuteDetectionCycleAsync 接收并贯穿 InspectionContext
[x] 日志输出 InspectionId
[x] UI 状态消息显示 InspectionId
[x] 图像文件名包含 InspectionId
[x] DetectionPersistencePayload 增加追溯字段
[x] DetectionRecord 增加追溯字段
[x] SQLite 兼容迁移新增列
[x] 保存失败记录 ErrorStage / ErrorCode / ErrorMessage
[x] 性能日志增加 InspectionId 和阶段耗时
```

Day 1 不做：

```text
不把完整流程搬进 InspectionEngine
不重写 UI
不改变 PLC Legacy 默认行为
不替换 AppConfig
```

Day 1 验收：

```text
[x] dotnet build 通过
[x] dotnet test 通过
[x] 手动检测入口仍可调用
[x] PLC Legacy 触发入口仍调用原检测路径
[x] 每次检测有 InspectionId
[x] 图片文件名包含 InspectionId
[x] 数据库记录包含 InspectionId
[x] 检测异常记录 ErrorStage / ErrorCode / ErrorMessage
[x] 图像队列/数据库队列入队失败不静默
```

---

## Day 2：PLC 握手与健康监控基础

目标：

```text
保留 Legacy 默认行为，在旁路增加 HandshakeV1 和 HealthSnapshot。
本阶段握手区作为旁路入口，不替换现有 PlcTriggerAddress 触发拍照和 PlcResultAddress 结果回写主流程。
```

任务：

```text
[x] 新增 PlcProtocolMode：Legacy / HandshakeV1
[x] AppConfig 新增 PlcProtocolMode，默认 Legacy
[x] AppConfig 新增 HandshakeV1 地址配置
[x] 新增 PlcTriggerContext
[x] IPlcService 保留 TriggerReceived，新增 TriggerContextReceived
[x] Legacy 模式 TriggerSeq = null
[x] HandshakeV1 读取 TriggerSeq
[x] 检测上下文绑定 TriggerSeq
[x] 完成检测后写 ResultSeq = TriggerSeq
[x] 完成检测后写 Result / InspectionDone / TraceSaved
[x] 检测开始写 VisionBusy=1
[x] 检测结束写 VisionBusy=0
[x] 异常时写 ErrorCode
[x] 新增 HealthMonitor
[x] 新增 HealthSnapshot
[x] HealthSnapshot 读取相机、PLC、模型、队列、磁盘、内存状态
[x] 队列 DroppedCount / FailedCount 进入健康报警
[x] PLC 写入失败进入健康报警
[x] 图像保存失败进入健康报警
[x] 数据库保存失败进入健康报警
[x] UI 或日志可查看 HealthSnapshot
```

HandshakeV1 推荐地址：

```text
PLC -> Vision:
Trigger
TriggerSeq
ResetFault 可选

Vision -> PLC:
VisionOnline
VisionReady
VisionBusy
InspectionDone
Result
ResultSeq
ErrorCode
Heartbeat
TraceSaved
```

关键规则：

```text
ResultSeq 必须等于 TriggerSeq
默认 PlcProtocolMode 必须是 Legacy
HandshakeV1 失败不能影响 Legacy 模式
```

Day 2 验收：

```text
[x] dotnet build 通过
[x] dotnet test 通过
[x] 旧 config 未配置 PlcProtocolMode 时默认 Legacy
[x] Legacy PLC 触发行为不变
[x] HandshakeV1 模拟读到 TriggerSeq
[x] ResultSeq 写回等于 TriggerSeq
[x] PLC 写失败有日志和 HealthMonitor 记录
[x] HealthSnapshot 能看到队列长度、丢弃数、失败数
[x] HealthSnapshot 能看到最近错误
```

---

## Day 3：轻量平台化与现场工具

目标：

```text
在不替换旧配置的前提下，补齐 Recipe、ModelRegistry、StartupDiagnostics、诊断包和无硬件压测基础能力。
```

任务：

```text
[x] 新增 Recipe
[x] 新增 RecipeManager
[x] 从 AppConfig 生成 default recipe
[x] Recipe 原子保存
[x] Recipe 保存前备份
[x] Recipe 回滚上一版本
[x] 检测记录写入 RecipeId / RecipeVersion
[x] 新增 ModelPackageManifest
[x] 新增 ModelRegistry
[x] 扫描 models 或 ONNX 目录
[x] 旧裸 ONNX 模式继续可用
[x] 严格模型包模式下校验 manifest
[x] 严格模型包模式下校验 model.onnx 存在
[x] 严格模型包模式下校验 hash
[x] 严格模型包模式下校验 labels
[x] 严格模型包模式下执行 warmup
[x] 检测记录写入 ModelId / ModelVersion / ModelHash
[x] fallback 记录 WasFallback / UsedModelName
[x] 新增 StartupDiagnostics
[x] 检查 WebView2 Runtime
[x] 检查 OpenCV native dll
[x] 检查相机 SDK dll
[x] 检查数据库目录可写
[x] 检查存储目录可写
[x] 检查日志目录可写
[x] 检查 PLC 地址配置
[x] 检查相机配置
[x] 检查磁盘剩余空间
[x] 新增 DiagnosticPackageExporter
[x] 诊断包导出 logs / sanitized config / recipe / manifest / health / recent records / system info
[x] 诊断包脱敏 AdminPassword
[x] 诊断包不打包历史大图和完整模型
[x] 新增无硬件模拟压测入口
[x] 复用 MockCamera 或新增 FakeCameraService
[x] 新增 FakePlcService / FakeDetectionService 或等价测试实现
[x] 输出压测报告
```

Day 3 验收：

```text
[x] dotnet build 通过（默认 bin 输出被正在运行的 VS 调试进程占用；使用临时 OutDir 验证 0 warning / 0 error）
[x] dotnet test 通过
[x] 旧 AppConfig 可启动
[x] default recipe 可生成
[x] recipe 可保存和回滚
[x] 旧 ONNX 模型路径仍兼容
[x] manifest 缺失在旧模式为 Warning
[x] hash 错误在严格模式阻止 Ready
[x] StartupDiagnostics 可查看
[x] 阻塞项失败不会进入 Ready
[x] 诊断包可生成
[x] 诊断包不含明文 AdminPassword
[x] 模拟压测不依赖真实相机、真实 PLC、真实模型
[x] 压测报告包含平均耗时、P95、P99、失败数、队列堆积、内存变化
```

---

## 4. 建议新增类型

### InspectionIdGenerator

```text
格式建议：
CF-20260429-153012-PLC-000001
CF-20260429-153015-MANUAL-A8F21C
```

要求：

```text
同毫秒不冲突
适合文件名
适合数据库索引
包含触发源
```

### InspectionContext

```csharp
public sealed class InspectionContext
{
    public string InspectionId { get; init; } = string.Empty;
    public DateTimeOffset TriggerTime { get; init; }
    public string TriggerSource { get; init; } = string.Empty;
    public int? TriggerSeq { get; init; }
    public int? ResultSeq { get; set; }
    public InspectionStage CurrentStage { get; set; }
    public TraceStatus TraceStatus { get; set; }
    public long CaptureMs { get; set; }
    public long InferenceMs { get; set; }
    public long RoiMs { get; set; }
    public long PlcWriteMs { get; set; }
    public long SaveImageMs { get; set; }
    public long SaveRecordMs { get; set; }
    public long TotalMs { get; set; }
    public string? ImagePath { get; set; }
    public string? ErrorStage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
```

### HealthSnapshot

```text
SystemUptime
HealthLevel
InspectionState
CameraStatus
PlcStatus
ModelStatus
RecipeStatus
StorageStatus
DatabaseStatus
LastInspectionId
LastInspectionTotalMs
RecentInspectionP95Ms
RecentInspectionP99Ms
ImageQueueLength
ImageQueueDroppedCount
ImageQueueFailedCount
RecordQueueLength
RecordQueueDroppedCount
RecordQueueFailedCount
FreeDiskGb
MemoryMb
RecentErrors
UpdatedAt
```

### StartupDiagnostics

每个检查项输出：

```text
Name
Status: Pass / Warning / Fail
Message
Details
IsBlocking
```

---

## 5. 本轮不建议做

```text
彻底把 ExecuteDetectionCycleAsync 搬进 InspectionEngine
大规模重写 Web UI
废弃旧 AppConfig
废弃旧 ONNX 裸模型路径
强制所有模型必须 manifest
复杂账号权限系统
复杂 MES 对接
完整报表系统
云同步
完整数据标注系统
复杂多工位架构
复杂动画和 UI 美化
```

这些应放到 V4.1 或 V4.2。

---

## 6. 每轮统一审查清单

每完成一个阶段，必须审查：

```text
[ ] 是否 build 通过
[ ] 是否 test 通过
[ ] 是否破坏手动检测
[ ] 是否破坏 PLC Legacy
[ ] 是否破坏旧配置加载
[ ] 是否引入 UI 卡死
[ ] 是否可能导致检测流程阻塞
[ ] 是否可能导致 PLC 结果错位
[ ] 是否可能导致图片或数据库记录丢失而无报警
[ ] 是否所有核心异常都有日志
[ ] 是否模型错误可能静默上线
[ ] 是否配置保存失败可能损坏当前配置
[ ] 是否诊断包可能泄露明文密码
[ ] 是否保留回滚或兼容路径
```

---

## 7. 最终收敛 Checklist

进入预览版交付前必须跑完：

```text
[ ] dotnet build ClearFrost.sln -c Debug -p:Platform=x64
[ ] dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj
[ ] 软件启动
[ ] 旧 config 启动
[ ] 手动检测
[ ] PLC Legacy 基本验证
[ ] HandshakeV1 模拟验证
[ ] InspectionId 查询
[ ] 图片文件名检查
[ ] 数据库记录检查
[ ] 队列丢弃报警检查
[ ] HealthSnapshot 查看
[ ] StartupDiagnostics 查看
[ ] default recipe 生成
[ ] recipe 保存和回滚
[ ] 旧 ONNX 模型加载
[ ] 模型包 manifest 缺失测试
[ ] hash 错误测试
[ ] labels 不匹配测试
[ ] warmup 失败测试
[ ] 诊断包导出
[ ] 诊断包脱敏检查
[ ] 模拟压测 1000 次
[ ] 压测报告生成
```

---

## 8. 最终判断标准

本轮成功不是“界面更漂亮”，而是：

```text
每次检测可追溯
PLC 结果不错位
保存失败不静默
模型上线有校验
配置变更可回滚
异常状态可诊断
现场问题可导出
稳定性可压测
旧功能仍可用
```

一句话：先把 ClearFrost V3 的稳定基础变成 V4 工业运行平台主干，不用 3 天硬造完整平台。
