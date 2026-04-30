# ClearFrost V4 工业深度学习运行平台 3-Day TODO 计划

> 目标：按“大任务包 + 统一开发 + 统一审查”的 Vibe coding 节奏，在 3 天内把 ClearFrost 从当前工业视觉检测软件，升级为具备工业稳定性、深度学习平台化能力、现场部署与排障能力的工业运行平台主干版本。

---

## 0. 计划定位

这份计划不按 8～12 周、小卡片、碎片化节奏推进，而是按照你的开发习惯设计：

```text
给 AI 一个长任务
让 AI 连续处理
你统一审查
发现问题后再进入下一轮修复
```

推荐开发分支：

```text
feat/v4-industrial-platform-3day
```

推荐最终预览版本名：

```text
v4-industrial-platform-preview
```

推荐正式版本名：

```text
v4.1.0-industrial-platform
```

三天最终目标不是把每个 UI 细节都打磨到完美，而是完成工业深度学习运行平台的主骨架：

```text
InspectionEngine
InspectionId 全链路追溯
PLC Legacy + HandshakeV1
HealthMonitor
RecipeManager
ModelRegistry
模型包 manifest/hash/labels/warmup 校验
fallback 策略配置化
StartupDiagnostics
诊断包导出
无硬件模拟压测
```

---

## 1. 总体开发原则

### 1.1 不做碎片化小任务

本计划只保留 4 个大型任务包：

```text
TASK-1：工业检测核心重构包
TASK-2：PLC 握手、追溯、健康监控包
TASK-3：配方、模型包、深度学习平台包
TASK-4：启动自检、现场工具、压测验收包
```

每个任务包都可以一次性丢给 AI 执行。每个任务包结束后，你统一审查：

```text
是否能构建
是否能启动
旧功能是否没坏
核心目标是否达成
是否存在工业稳定性风险
```

---

### 1.2 兼容优先

工业现场升级最怕“新版本一装就停线”。因此所有关键升级都要保留兼容路径：

```text
旧 AppConfig 继续可用
旧模型路径方式继续兼容
PLC Legacy 模式继续可用
手动检测继续可用
旧 UI 不被大规模重写
新增能力优先通过配置或兼容适配接入
```

---

### 1.3 先搭主干，再补细节

3 天压缩开发不追求所有细节都一次到位，但必须把主干搭正：

```text
检测事务主干
PLC 工业握手主干
追溯主干
健康监控主干
配方主干
模型包主干
启动自检主干
压测主干
```

UI 美化、复杂权限、复杂报表、云同步、MES 深度对接等先不做。

---

### 1.4 工业稳定性高于功能数量

本次开发最重要的判断标准：

```text
不能把生产检测流程改乱
不能让 PLC 结果错位
不能让图像/数据库失败静默
不能让模型错误静默上线
不能让配置损坏后无法恢复
不能让现场人员完全不知道系统哪里坏了
```

---

## 2. 三天执行节奏

### Day 1：工业稳定主干

目标：

```text
把检测流程从 UI 主导升级为 InspectionEngine 主导。
建立 InspectionId、InspectionContext、InspectionResult、状态机、PLC 工业握手基础。
```

执行：

```text
TASK-1：工业检测核心重构包
TASK-2A：PLC 与追溯核心
```

Day 1 结束审查：

```text
1. 项目能否正常构建
2. 软件能否正常启动
3. 手动检测是否还能跑
4. PLC Legacy 模式是否没坏
5. 每次检测是否有 InspectionId
6. 日志、数据库、图像是否能通过 InspectionId 串联
7. 检测失败时是否能看到失败阶段
8. PLC HandshakeV1 是否有基本结构
```

---

### Day 2：深度学习平台主干

目标：

```text
把裸模型、裸配置升级成 RecipeManager + ModelRegistry。
模型从单个 ONNX 文件升级为模型包。
检测结果具备配方版本和模型版本追溯能力。
```

执行：

```text
TASK-3：配方、模型包、深度学习平台包
```

Day 2 结束审查：

```text
1. 旧 AppConfig 是否还能启动
2. default recipe 是否能自动生成
3. recipe 是否支持保存、备份、回滚
4. 模型包 manifest 是否真的被读取
5. hash / labels / task / input size / warmup 是否真的校验
6. 错误模型是否会被阻止进入生产状态
7. fallback 是否变成策略控制
8. 检测记录是否包含 RecipeId、RecipeVersion、ModelId、ModelVersion、ModelHash
```

---

### Day 3：现场可用化 + 压测收尾

目标：

```text
补齐启动自检、健康状态、诊断包、NG 检索、耗时趋势、模拟压测。
让版本具备现场交付、排障、验收基础。
```

执行：

```text
TASK-2B：健康监控与存储保护
TASK-4：启动自检、现场工具、压测验收包
```

Day 3 结束审查：

```text
1. StartupDiagnostics 是否可查看
2. 阻塞项失败时是否会阻止进入 Ready
3. HealthSnapshot 是否能反映现场真实状态
4. 图像保存失败、数据库失败、队列丢弃是否进入报警
5. 诊断包是否能生成
6. 诊断包是否排除明文密码
7. 是否能通过 InspectionId 查询记录
8. 是否能查看最近检测耗时趋势
9. 模拟压测是否不依赖真实硬件
10. 压测报告是否包含平均耗时、P95、P99、失败数、队列堆积、内存变化
```

---

## 3. 任务包总览

```text
[ ] TASK-1：工业检测核心重构包
[ ] TASK-2：PLC 握手、追溯、健康监控包
[ ] TASK-3：配方、模型包、深度学习平台包
[ ] TASK-4：启动自检、现场工具、压测验收包
```

---

# TASK-1：工业检测核心重构包

## 目标

把 ClearFrost 当前由 UI 主导的检测流程，升级为核心服务主导。

最终形成：

```text
InspectionEngine
InspectionContext
InspectionResult
InspectionStage
InspectionEngineState
InspectionId
```

UI 只负责：

```text
发起检测
显示状态
显示结果
显示健康信息
```

核心检测事务由 `InspectionEngine` 承担。

---

## TODO

```text
[ ] 创建 Core/Inspection/ 目录
[ ] 新增 InspectionIdGenerator
[ ] 新增 InspectionContext
[ ] 新增 InspectionResult
[ ] 新增 InspectionStage
[ ] 新增 InspectionEngineState
[ ] 新增 IInspectionEngine
[ ] 新增 InspectionEngine
[ ] 把现有手动检测入口接入 InspectionEngine
[ ] 把现有 PLC 触发检测入口接入 InspectionEngine
[ ] 保留旧 ExecuteDetectionCycleAsync 兼容路径，必要时作为内部调用
[ ] 每次检测开始时生成唯一 InspectionId
[ ] 日志中输出 InspectionId
[ ] 图像文件名中包含 InspectionId
[ ] 数据库记录中包含 InspectionId
[ ] 检测失败时记录 ErrorStage
[ ] 检测失败时记录 ErrorCode
[ ] 检测失败时记录 ErrorMessage
[ ] 检测完成时输出完整耗时分解
[ ] UI 显示当前 InspectionEngineState
[ ] 手动检测和 PLC 检测都通过统一的检测上下文传递核心信息
```

---

## 建议新增类型

### InspectionIdGenerator

用途：

```text
为每一次检测生成唯一 ID。
```

建议格式：

```text
CF-20260429-153012-PLC-000001
CF-20260429-153015-MANUAL-A8F21C
```

字段意义：

```text
CF：ClearFrost
日期时间：检测触发时间
触发源：PLC / MANUAL / TEST
序号或短随机码：避免同毫秒冲突
```

---

### InspectionStage

建议枚举：

```csharp
public enum InspectionStage
{
    None,
    TriggerReceived,
    AcquiringImage,
    ImageAcquired,
    Inferencing,
    Evaluating,
    WritingPlc,
    SavingImage,
    SavingRecord,
    UpdatingUi,
    Completed,
    Failed
}
```

---

### InspectionEngineState

建议枚举：

```csharp
public enum InspectionEngineState
{
    Idle,
    Initializing,
    Ready,
    WaitingTrigger,
    Triggered,
    Acquiring,
    Inferencing,
    Evaluating,
    WritingPlc,
    SavingTrace,
    Completed,
    Error,
    Recovering,
    ShuttingDown
}
```

---

### InspectionContext

建议字段：

```csharp
public sealed class InspectionContext
{
    public string InspectionId { get; init; }
    public DateTimeOffset TriggerTime { get; init; }
    public string TriggerSource { get; init; }
    public int? TriggerSeq { get; init; }

    public InspectionStage CurrentStage { get; set; }

    public long CaptureMs { get; set; }
    public long InferenceMs { get; set; }
    public long RoiMs { get; set; }
    public long PlcWriteMs { get; set; }
    public long SaveImageMs { get; set; }
    public long SaveRecordMs { get; set; }
    public long TotalMs { get; set; }

    public string? ErrorStage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
```

---

### InspectionResult

建议字段：

```csharp
public sealed class InspectionResult
{
    public string InspectionId { get; init; }
    public bool IsOk { get; init; }

    public string TargetLabel { get; init; }
    public int ExpectedCount { get; init; }
    public int ActualCount { get; init; }

    public string? ModelName { get; init; }
    public string? ModelId { get; init; }
    public string? ModelVersion { get; init; }
    public string? ModelHash { get; init; }

    public bool WasFallback { get; init; }
    public string? UsedModelName { get; init; }

    public string? ImagePath { get; set; }
    public string? RenderedImagePath { get; set; }

    public TraceStatus TraceStatus { get; set; }

    public string? ErrorStage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
```

---

### IInspectionEngine

建议接口：

```csharp
public interface IInspectionEngine
{
    InspectionEngineState State { get; }

    event EventHandler<InspectionEngineStateChangedEventArgs>? StateChanged;

    Task<InspectionResult> RunOnceAsync(
        InspectionRequest request,
        CancellationToken cancellationToken);
}
```

---

## TASK-1 AI 长任务 Prompt

```text
你是 ClearFrost 项目的 C#/.NET 工业视觉软件工程师。

现在请执行 TASK-1：工业检测核心重构包。

目标：
把当前 ClearFrost 的检测流程升级为 InspectionEngine 驱动，但不要一次性破坏现有功能。需要建立工业检测事务的核心结构：InspectionId、InspectionContext、InspectionResult、InspectionStage、InspectionEngineState、IInspectionEngine、InspectionEngine。

背景要求：
ClearFrost 当前是 WinForms + WebView2 + ONNX Runtime + OpenCvSharp + 工业相机 + PLC 的工业视觉检测软件。现有检测流程已经可以运行，不能随意重写到不可运行。当前检测流程中已经有拍照、推理、ROI 过滤、PLC 写入、图像保存、数据库记录、前端展示等步骤。

强制要求：
1. 不要删除现有检测功能。
2. 不要破坏手动检测。
3. 不要破坏 PLC Legacy 触发模式。
4. 不要大规模重写 UI。
5. 不要引入大型新依赖。
6. 所有新增核心类放在 Core/Inspection/ 或合适的核心目录。
7. 每次检测必须生成唯一 InspectionId。
8. InspectionId 必须尽量贯穿日志、数据库、图像文件名、检测结果、前端显示。
9. 检测失败时必须记录失败阶段、错误码、错误信息。
10. 所有核心异常必须落日志，不能静默吞掉。
11. 保持项目 dotnet build 通过。

实现内容：
1. 新增 InspectionIdGenerator。
2. 新增 InspectionContext，包含 InspectionId、TriggerSource、TriggerSeq、CurrentStage、耗时字段、错误字段。
3. 新增 InspectionResult，包含 InspectionId、OK/NG、目标标签、期望数量、实际数量、模型信息、图像路径、追溯状态、错误信息。
4. 新增 InspectionStage 枚举。
5. 新增 InspectionEngineState 枚举。
6. 新增 IInspectionEngine。
7. 新增 InspectionEngine。
8. 将手动检测入口改为通过 InspectionEngine 执行。
9. 将 PLC 触发检测入口尽量改为通过 InspectionEngine 执行。
10. 保留旧流程兼容路径，避免一次性重构失败。
11. 让 UI 能显示当前 InspectionEngineState。
12. 修改数据库记录结构或写入逻辑，使 InspectionId 能保存。
13. 修改图像保存逻辑，使文件名包含 InspectionId。
14. 检测完成后输出完整检测摘要日志。

验收标准：
1. 项目能构建。
2. 软件能启动。
3. 手动检测可运行。
4. PLC Legacy 模式不被破坏。
5. 每次检测日志中可以看到 InspectionId。
6. 数据库记录中可以查到 InspectionId。
7. 保存图片文件名中包含 InspectionId。
8. 检测异常时能看到 ErrorStage。
9. UI 不因为检测引擎改造而明显卡死。
10. 旧配置文件仍然兼容。

最后请输出：
1. 修改文件列表。
2. 新增类型列表。
3. 检测流程变化说明。
4. 如何验证。
5. 已知风险。
```

---

## TASK-1 审查清单

```text
[ ] 项目能构建
[ ] 软件能启动
[ ] 手动检测还能运行
[ ] PLC Legacy 触发没被破坏
[ ] InspectionEngine 已成为新核心入口
[ ] 每次检测都有唯一 InspectionId
[ ] InspectionId 能在日志中看到
[ ] InspectionId 能在数据库中看到
[ ] InspectionId 能在图像文件名中看到
[ ] 检测失败能看到 ErrorStage / ErrorCode / ErrorMessage
[ ] UI 没有因为重构明显卡死
[ ] 没有大规模无意义重写 UI
```

---

# TASK-2：PLC 握手、追溯、健康监控包

## 目标

把 ClearFrost 从“能和 PLC 通信”升级为“工业可靠握手”。

同时建立：

```text
PLC HandshakeV1
TraceStatus
HealthMonitor
HealthSnapshot
磁盘保护
队列失败报警
追溯完整性标记
```

---

## TODO

```text
[ ] 新增 PlcProtocolMode：Legacy / HandshakeV1
[ ] Legacy 模式保持原行为
[ ] 新增 HandshakeV1 地址配置
[ ] 新增 VisionOnline
[ ] 新增 VisionReady
[ ] 新增 VisionBusy
[ ] 新增 TriggerSeq
[ ] 新增 ResultSeq
[ ] 新增 InspectionDone
[ ] 新增 ErrorCode
[ ] 新增 Heartbeat
[ ] 新增 TraceSaved
[ ] 检测开始写 VisionBusy=1
[ ] 检测完成写 Result
[ ] 检测完成写 ResultSeq=TriggerSeq
[ ] 检测完成写 InspectionDone=1
[ ] 检测结束写 VisionBusy=0
[ ] 异常时写 ErrorCode
[ ] 新增 TraceStatus：Unknown / Full / Partial / Failed / Disabled
[ ] 图像保存失败时 TraceStatus 不得为 Full
[ ] 数据库保存失败时 TraceStatus 不得为 Full
[ ] 队列满、队列丢弃、保存失败必须进入健康报警
[ ] 新增 HealthMonitor
[ ] 新增 HealthSnapshot
[ ] 记录相机状态
[ ] 记录 PLC 状态
[ ] 记录模型状态
[ ] 记录磁盘状态
[ ] 记录图像保存队列状态
[ ] 记录数据库记录队列状态
[ ] 记录最近错误
[ ] 记录最近一次检测耗时
[ ] 记录检测耗时 P95 / P99
[ ] 新增磁盘剩余空间检查
[ ] 新增 MinFreeDiskGb 配置
[ ] 磁盘不足时报警或停止保存
[ ] NG 图片优先保护
[ ] UI 显示健康状态
```

---

## PLC HandshakeV1 建议协议

### PLC -> Vision

```text
Trigger
TriggerSeq
ResetFault 可选
```

### Vision -> PLC

```text
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

### 推荐流程

```text
1. 系统启动并通过自检后，Vision 写 VisionOnline=1。
2. 系统可检测时，Vision 写 VisionReady=1。
3. PLC 写 Trigger=1。
4. PLC 写 TriggerSeq=N。
5. Vision 读到触发和 TriggerSeq。
6. Vision 生成 InspectionId，并绑定 TriggerSeq。
7. Vision 写 VisionBusy=1。
8. Vision 执行检测。
9. Vision 写 Result。
10. Vision 写 ResultSeq=N。
11. Vision 写 TraceSaved=1 或 0。
12. Vision 写 InspectionDone=1。
13. Vision 写 VisionBusy=0。
14. PLC 确认后复位 Trigger 或 Done。
```

### 关键要求

```text
ResultSeq 必须等于 TriggerSeq。
```

这是避免 PLC 结果错位的核心。

---

## TraceStatus

建议枚举：

```csharp
public enum TraceStatus
{
    Unknown,
    Full,
    Partial,
    Failed,
    Disabled
}
```

建议规则：

```text
检测成功 + 图像保存成功 + 数据库保存成功 -> Full
检测成功 + 图像保存失败 + 数据库保存成功 -> Partial
检测成功 + 图像保存成功 + 数据库保存失败 -> Partial
检测成功 + 图像保存失败 + 数据库保存失败 -> Failed
用户关闭追溯 -> Disabled
```

---

## HealthLevel

建议枚举：

```csharp
public enum HealthLevel
{
    Ok,
    Warning,
    Critical,
    Fatal
}
```

---

## HealthSnapshot 建议字段

```text
SystemUptime
HealthLevel
InspectionEngineState
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
RecordQueueLength
RecordQueueDroppedCount
FreeDiskGb
MemoryMb
RecentErrors
UpdatedAt
```

---

## TASK-2 AI 长任务 Prompt

```text
你是 ClearFrost 项目的 C#/.NET 工业视觉软件工程师。

现在请执行 TASK-2：PLC 握手、追溯、健康监控包。

目标：
在不破坏现有 PLC Legacy 模式的前提下，为 ClearFrost 增加工业级 PLC 握手协议 HandshakeV1，并建立追溯完整性和健康监控体系。

强制要求：
1. Legacy 模式必须保持兼容。
2. 新协议通过 PlcProtocolMode 控制。
3. 默认仍然使用 Legacy，避免现场升级后直接改变行为。
4. HandshakeV1 要支持 TriggerSeq 和 ResultSeq，避免结果错位。
5. PLC 写入失败必须记录日志和健康报警。
6. 图像保存失败、数据库保存失败、队列丢弃不能静默。
7. 不要把健康监控逻辑写散，必须建立 HealthMonitor 或等价统一模块。
8. 不要大规模重写 PLC 服务，优先在现有 PlcService 上兼容扩展。
9. 保持项目能构建和运行。

HandshakeV1 设计：
PLC -> Vision:
- Trigger
- TriggerSeq
- ResetFault 可选

Vision -> PLC:
- VisionOnline
- VisionReady
- VisionBusy
- InspectionDone
- Result
- ResultSeq
- ErrorCode
- Heartbeat
- TraceSaved

检测流程要求：
1. 系统可检测时写 VisionReady。
2. 检测开始时写 VisionBusy=1。
3. 读取 TriggerSeq，绑定到 InspectionContext。
4. 检测完成后写 Result。
5. 写 ResultSeq=TriggerSeq。
6. 写 InspectionDone=1。
7. 写 VisionBusy=0。
8. 如果检测异常，写 ErrorCode。
9. 如果追溯完整，写 TraceSaved=1，否则写 TraceSaved=0。

追溯要求：
1. 新增 TraceStatus：Unknown、Full、Partial、Failed、Disabled。
2. 检测成功但图像保存失败，TraceStatus=Partial 或 Failed。
3. 检测成功但数据库保存失败，TraceStatus=Partial 或 Failed。
4. 队列满导致丢图或丢记录，必须健康报警。
5. 数据库中尽量记录 TraceStatus、TriggerSeq、ResultSeq、ImagePath、ErrorStage、ErrorCode。

健康监控要求：
新增 HealthMonitor / HealthSnapshot，至少包含：
- 系统运行时长
- InspectionEngineState
- 相机状态
- PLC 状态
- 模型状态
- 最近一次检测耗时
- 图像保存队列长度
- 数据库队列长度
- 队列丢弃数量
- 磁盘剩余空间
- 内存占用
- 最近错误
- HealthLevel：Ok / Warning / Critical / Fatal

磁盘保护要求：
1. 新增 MinFreeDiskGb 配置。
2. 磁盘不足时进入 Warning 或 Critical。
3. 不允许磁盘满了还无限保存图片。
4. NG 图片优先保护。

验收标准：
1. Legacy PLC 模式可继续使用。
2. HandshakeV1 在模拟或配置环境下能跑通。
3. 每次 PLC 检测都有 TriggerSeq/ResultSeq。
4. ResultSeq 必须等于 TriggerSeq。
5. PLC 断线或写入失败有日志和健康报警。
6. 图像保存失败有健康报警。
7. 数据库保存失败有健康报警。
8. 队列丢弃有健康报警。
9. UI 或接口能看到 HealthSnapshot。
10. 项目能构建。

最后请输出：
1. 修改文件列表。
2. 新增配置项。
3. PLC HandshakeV1 地址说明。
4. 健康监控字段说明。
5. 如何验证 Legacy 和 HandshakeV1。
6. 已知风险。
```

---

## TASK-2 审查清单

```text
[ ] Legacy PLC 模式没有被破坏
[ ] HandshakeV1 通过 PlcProtocolMode 控制
[ ] 默认仍是 Legacy 或兼容旧配置
[ ] TriggerSeq 被读取
[ ] ResultSeq 被写回
[ ] ResultSeq 等于 TriggerSeq
[ ] Busy / Done / Result 写入顺序合理
[ ] PLC 写入失败有日志
[ ] PLC 写入失败进入健康报警
[ ] 图像保存失败进入健康报警
[ ] 数据库保存失败进入健康报警
[ ] 队列丢弃进入健康报警
[ ] HealthSnapshot 字段足够现场排障
[ ] 磁盘不足不会继续无限保存图片
[ ] NG 图片优先保护
```

---

# TASK-3：配方、模型包、深度学习平台包

## 目标

把 ClearFrost 从“工业检测软件”升级为“深度学习工业运行平台”。

核心是：

```text
RecipeManager
ModelRegistry
ModelPackage
manifest.json
模型 hash
模型 labels 校验
模型 task 校验
模型 input size 校验
模型 warmup
fallback 策略配置化
配方版本与回滚
```

---

## TODO

```text
[ ] 新增 Recipe 数据结构
[ ] 新增 CameraRecipe
[ ] 新增 ModelRecipe
[ ] 新增 DetectionRuleRecipe
[ ] 新增 PlcRecipe
[ ] 新增 StorageRecipe
[ ] 新增 FallbackPolicy
[ ] 新增 RecipeManager
[ ] 当前 AppConfig 可生成 default recipe
[ ] 配方保存使用原子写入
[ ] 配方保存前自动备份
[ ] 配方支持 history
[ ] 配方支持 rollback
[ ] 配方支持 import
[ ] 配方支持 export
[ ] 新增 ModelPackage 结构
[ ] 新增 manifest.json 读取
[ ] 新增 ModelRegistry
[ ] 扫描 models 目录
[ ] 校验 model.onnx 存在
[ ] 校验 model hash
[ ] 校验 labels
[ ] 校验 task
[ ] 校验 input size
[ ] 模型 warmup
[ ] warmup 耗时写入日志
[ ] warmup 失败时模型不可用
[ ] fallback 策略配置化
[ ] RequireSameLabels=true 时禁止标签不一致辅助模型
[ ] fallback 发生时记录 WasFallback
[ ] fallback 发生时记录 UsedModelName
[ ] fallback 发生时记录 UsedModelVersion
[ ] 检测记录写入 RecipeId
[ ] 检测记录写入 RecipeName
[ ] 检测记录写入 RecipeVersion
[ ] 检测记录写入 ModelId
[ ] 检测记录写入 ModelVersion
[ ] 检测记录写入 ModelHash
[ ] 检测记录写入 ModelPackagePath
[ ] 检测记录写入 FallbackPolicy
```

---

## Recipe 建议结构

```csharp
public sealed class Recipe
{
    public string RecipeId { get; init; }
    public string Name { get; set; }
    public string ProductCode { get; set; }
    public string Version { get; set; }

    public CameraRecipe Camera { get; set; }
    public ModelRecipe Model { get; set; }
    public DetectionRuleRecipe DetectionRules { get; set; }
    public PlcRecipe Plc { get; set; }
    public StorageRecipe Storage { get; set; }
    public FallbackPolicy FallbackPolicy { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

---

## Recipe JSON 示例

```json
{
  "recipeId": "default",
  "name": "Default Recipe",
  "productCode": "DEFAULT",
  "version": "1.0.0",
  "camera": {
    "cameraId": "",
    "serialNumber": "",
    "exposure": 8000,
    "gain": 1.0,
    "triggerMode": "Software"
  },
  "model": {
    "primaryModelPackageId": "default-model",
    "auxiliaryModelPackageIds": [],
    "confidence": 0.5,
    "iou": 0.3
  },
  "detectionRules": {
    "targetLabel": "screw",
    "expectedCount": 1,
    "roiEnabled": false,
    "roi": null
  },
  "plc": {
    "protocolMode": "Legacy",
    "ip": "192.168.1.10",
    "port": 502,
    "triggerAddress": "D555",
    "resultAddress": "D556"
  },
  "storage": {
    "saveOkImages": false,
    "saveNgImages": true,
    "saveRenderedImages": true,
    "retentionDays": 30,
    "minFreeDiskGb": 5
  },
  "fallbackPolicy": {
    "mode": "Sequential",
    "requireSameLabels": true,
    "recordAllModelResults": true,
    "allowAuxModelForOk": false,
    "allowAuxModelForNg": true,
    "maxModels": 3
  },
  "createdAt": "2026-04-29T00:00:00+08:00",
  "updatedAt": "2026-04-29T00:00:00+08:00"
}
```

---

## ModelPackage 推荐目录结构

```text
models/
  screw-detector-v3/
    model.onnx
    labels.json
    manifest.json
    samples/
      ok.jpg
      ng.jpg
```

---

## manifest.json 示例

```json
{
  "model_id": "screw-detector",
  "model_version": "3.2.1",
  "task": "detect",
  "runtime": "onnxruntime-directml",
  "input_width": 640,
  "input_height": 640,
  "labels": ["screw"],
  "model_hash": "sha256:REPLACE_WITH_REAL_HASH",
  "recommended_confidence": 0.5,
  "recommended_iou": 0.3
}
```

---

## FallbackPolicy 建议结构

```json
{
  "mode": "Sequential",
  "requireSameLabels": true,
  "recordAllModelResults": true,
  "allowAuxModelForOk": false,
  "allowAuxModelForNg": true,
  "maxModels": 3
}
```

可选模式：

```text
Disabled
Sequential
RuleBased
```

---

## TASK-3 AI 长任务 Prompt

```text
你是 ClearFrost 项目的 C#/.NET 工业视觉软件工程师。

现在请执行 TASK-3：配方、模型包、深度学习平台包。

目标：
把 ClearFrost 从单一检测软件升级为适合深度学习部署的工业运行平台。需要新增 RecipeManager、ModelRegistry、ModelPackage、模型 manifest、模型 hash 校验、模型 warmup、fallback 策略配置化、配方版本和回滚。

强制要求：
1. 不能破坏当前 AppConfig。
2. 当前配置必须能迁移或映射成 default recipe。
3. 旧模型路径方式需要保留兼容，不能导致现有用户启动失败。
4. 新的 RecipeManager 先以 JSON 文件方式实现，不要引入复杂数据库。
5. 配方保存必须原子写入，失败不能破坏当前配方。
6. 模型包校验失败时不能进入 Ready 状态。
7. 模型 labels 与配方目标标签不一致时必须给出明确错误。
8. 多模型 fallback 必须受策略控制，不能无约束乱切。
9. 检测记录中必须尽量写入 RecipeId、RecipeVersion、ModelId、ModelVersion、ModelHash。
10. 项目必须能构建运行。

Recipe 要求：
新增 Recipe 数据结构，至少包含：
- RecipeId
- Name
- ProductCode
- Version
- Camera 配置
- Model 配置
- DetectionRules
- Plc 配置
- Storage 配置
- FallbackPolicy
- CreatedAt
- UpdatedAt

RecipeManager 要求：
1. 启动时加载当前 recipe。
2. 如果不存在 recipe，则从 AppConfig 生成 default recipe。
3. 支持保存 recipe。
4. 支持 recipe history。
5. 支持恢复上一个 recipe。
6. 支持导入导出 recipe。
7. 保存失败不能覆盖旧 recipe。

ModelPackage 结构：
models/
  model-package-id/
    model.onnx
    labels.json
    manifest.json
    samples/
      ok.jpg
      ng.jpg

manifest.json 至少包含：
- model_id
- model_version
- task
- runtime
- input_width
- input_height
- labels
- model_hash
- recommended_confidence
- recommended_iou

ModelRegistry 要求：
1. 扫描 models 目录。
2. 读取 manifest。
3. 校验 model.onnx 是否存在。
4. 校验 sha256 hash。
5. 校验 labels。
6. 校验 task 类型。
7. 校验 input size。
8. 支持模型 warmup。
9. warmup 耗时写日志。
10. 模型错误时健康状态 NotReady 或 Fatal。

FallbackPolicy 要求：
新增配置：
- Mode：Disabled / Sequential / RuleBased
- RequireSameLabels
- RecordAllModelResults
- AllowAuxModelForOk
- AllowAuxModelForNg
- MaxModels

要求：
1. RequireSameLabels=true 时，辅助模型 labels 不一致不能启用。
2. fallback 发生时，检测记录必须记录 WasFallback、UsedModelName、UsedModelVersion。
3. 不允许 fallback 结果不可追溯。

数据库/追溯要求：
检测记录尽量增加：
- RecipeId
- RecipeName
- RecipeVersion
- ModelId
- ModelVersion
- ModelHash
- ModelPackagePath
- FallbackPolicy
- WasFallback

验收标准：
1. 软件能从旧 AppConfig 启动。
2. 启动后能生成或加载 default recipe。
3. recipe 可以保存、备份、回滚。
4. models 目录下的模型包可以被扫描。
5. manifest 缺失时有明确错误。
6. hash 不匹配时模型不可用。
7. labels 不匹配时模型不可用。
8. warmup 失败时模型不可用。
9. fallback 策略可以控制多模型行为。
10. 检测记录中能看到 recipe 和 model 信息。
11. 项目能构建。

最后请输出：
1. 修改文件列表。
2. 新增目录结构。
3. Recipe JSON 示例。
4. Model manifest JSON 示例。
5. 旧 AppConfig 兼容方式。
6. 如何验证。
7. 已知风险。
```

---

## TASK-3 审查清单

```text
[ ] 旧 AppConfig 仍可启动
[ ] default recipe 可自动生成
[ ] recipe 可保存
[ ] recipe 保存是原子写入
[ ] recipe 保存前有备份
[ ] recipe 可回滚
[ ] recipe 可导入导出
[ ] 模型包 manifest 被实际读取
[ ] model.onnx 缺失时有明确错误
[ ] hash 错误时模型不可用
[ ] labels 不匹配时模型不可用
[ ] task 不支持时模型不可用
[ ] input size 不支持时模型不可用
[ ] warmup 失败时模型不可用
[ ] fallback 受策略控制
[ ] RequireSameLabels=true 时辅助模型标签不一致无法启用
[ ] 检测记录包含 RecipeId / RecipeVersion / ModelHash
```

---

# TASK-4：启动自检、现场工具、压测验收包

## 目标

把系统变成“现场好用、好部署、好排障”。

核心是：

```text
StartupDiagnostics
健康大屏数据
诊断包导出
NG 图像检索基础能力
检测耗时趋势
维护工具
模拟压测
验收报告
```

---

## TODO

```text
[ ] 新增 StartupDiagnostics
[ ] 检查 WebView2 Runtime
[ ] 检查 OpenCV native dll
[ ] 检查相机 SDK dll
[ ] 检查数据库目录可写
[ ] 检查存储目录可写
[ ] 检查日志目录可写
[ ] 检查当前 recipe 是否有效
[ ] 检查模型文件或模型包
[ ] 检查模型可加载
[ ] 检查 DirectML / CPU runtime
[ ] 检查 PLC IP 和地址配置是否合法
[ ] 检查相机序列号配置是否合法
[ ] 检查磁盘剩余空间
[ ] UI 显示启动自检结果
[ ] Health 页面或接口显示 HealthSnapshot
[ ] 支持一键导出诊断包
[ ] 诊断包包含 logs
[ ] 诊断包包含 config snapshot
[ ] 诊断包包含 current recipe
[ ] 诊断包包含 model manifest
[ ] 诊断包包含 health snapshot
[ ] 诊断包包含 recent detection records
[ ] 诊断包包含 recent error records
[ ] 诊断包包含 system info
[ ] 诊断包包含 startup diagnostics result
[ ] 诊断包排除明文密码
[ ] 诊断包排除不必要的大量历史图片
[ ] 支持通过 InspectionId 查询记录
[ ] 支持 NG 记录基础检索
[ ] 支持按日期查询记录
[ ] 支持按 OK/NG 查询记录
[ ] 支持按 TargetLabel 查询记录
[ ] 支持按 RecipeId 查询记录
[ ] 支持按 ModelId 查询记录
[ ] 支持最近 100 或 500 次检测耗时趋势
[ ] 支持清理旧 OK 图
[ ] 支持保留 NG 图
[ ] 支持测试 PLC 读写
[ ] 支持测试相机拍照
[ ] 支持测试模型推理
[ ] 支持恢复上一个 recipe
[ ] 新增 FakeCameraService
[ ] 新增 FakePlcService
[ ] 新增 FakeDetectionService
[ ] 新增模拟压测入口
[ ] 支持连续模拟触发 1000 次
[ ] 支持配置触发间隔
[ ] 支持配置随机失败率
[ ] 支持配置图像保存慢
[ ] 支持配置数据库写入慢
[ ] 输出压测报告
```

---

## StartupDiagnostics 检查项结构

建议结构：

```csharp
public sealed class DiagnosticCheckResult
{
    public string Name { get; init; }
    public DiagnosticStatus Status { get; init; }
    public string Message { get; init; }
    public string? Details { get; init; }
    public bool IsBlocking { get; init; }
}
```

状态：

```csharp
public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail
}
```

检查项建议：

```text
WebView2 Runtime
OpenCV native dll
相机 SDK dll
数据库目录可写
存储目录可写
日志目录可写
当前 recipe 有效
模型包存在
模型可加载
DirectML 可用
CPU fallback 可用
PLC 配置合法
相机配置合法
磁盘剩余空间足够
```

---

## 诊断包建议内容

```text
diagnostic-package/
  logs/
  config/
    app-config-sanitized.json
  recipe/
    current-recipe.json
  model/
    manifest.json
  health/
    health-snapshot.json
    startup-diagnostics.json
  records/
    recent-detection-records.json
    recent-error-records.json
  system/
    system-info.json
```

必须排除：

```text
明文密码
无关系统文件
大量历史图片
完整模型文件，除非用户明确需要
```

---

## 模拟压测报告建议字段

```text
TotalCount
SuccessCount
FailedCount
AverageTotalMs
P95TotalMs
P99TotalMs
MaxTotalMs
AverageCaptureMs
AverageInferenceMs
AveragePlcWriteMs
AverageSaveImageMs
ImageQueueMaxLength
RecordQueueMaxLength
ImageDroppedCount
RecordDroppedCount
PlcWriteFailedCount
CameraCaptureFailedCount
ModelInferenceFailedCount
MemoryStartMb
MemoryEndMb
StartedAt
FinishedAt
Duration
```

---

## TASK-4 AI 长任务 Prompt

```text
你是 ClearFrost 项目的 C#/.NET 工业视觉软件工程师。

现在请执行 TASK-4：启动自检、现场工具、压测验收包。

目标：
把 ClearFrost 提升为适合现场部署、排障和验收的工业运行平台。需要新增启动自检、健康状态页面数据、诊断包导出、NG 检索基础能力、检测耗时趋势、维护工具和模拟压测。

强制要求：
1. 不要破坏现有 UI。
2. 不要把启动自检做成阻塞死循环。
3. 自检失败要给出明确原因。
4. 关键失败项要阻止进入 Ready 状态。
5. 诊断包不能导出明文密码。
6. 诊断包不能无脑打包所有大图。
7. 压测不能依赖真实相机、真实 PLC、真实模型。
8. 模拟服务必须与真实服务接口尽量一致。
9. 项目必须能构建运行。

StartupDiagnostics 要求：
检查以下项目：
- WebView2 Runtime
- OpenCV native dll
- 相机 SDK dll
- 数据库目录可写
- 存储目录可写
- 当前 recipe 是否有效
- 模型包是否存在
- 模型是否可加载
- DirectML 是否可用，不可用则提示 CPU fallback
- PLC IP 和地址配置是否合法
- 相机序列号配置是否合法
- 磁盘剩余空间
- 日志目录可写

每个检查项输出：
- Name
- Status：Pass / Warning / Fail
- Message
- Details
- IsBlocking

健康大屏数据要求：
通过现有 WebView2 或后端接口向前端提供 HealthSnapshot，至少包含：
- 系统运行时长
- 当前检测状态
- 相机状态
- PLC 状态
- 模型状态
- 磁盘剩余
- 内存占用
- 最近一次检测耗时
- P95/P99 检测耗时
- 图像队列长度
- 数据库队列长度
- 最近错误
- HealthLevel

诊断包要求：
一键导出 zip，包含：
- logs
- 当前 config 快照
- 当前 recipe
- 当前 model manifest
- health snapshot
- 最近检测记录
- 最近错误记录
- system info
- startup diagnostics result

必须排除：
- 明文密码
- 不必要的大量历史图片
- 无关系统文件

NG 检索基础要求：
支持按以下条件查询：
- 日期
- OK/NG
- InspectionId
- TargetLabel
- RecipeId
- ModelId

检测趋势要求：
保留最近 100 次或 500 次检测耗时，显示或输出：
- TotalMs
- CaptureMs
- InferenceMs
- PlcWriteMs
- SaveImageMs
- SaveRecordMs
- P95
- P99

维护工具要求：
至少提供后端能力或 UI 入口：
- 清理旧 OK 图
- 保留 NG 图
- 测试 PLC 读写
- 测试相机拍照
- 测试模型推理
- 导出诊断包
- 恢复上一个 recipe

模拟压测要求：
新增 FakeCameraService、FakePlcService、FakeDetectionService 或等价模拟实现。
提供一个模拟压测入口：
- 连续触发 1000 次
- 可配置触发间隔
- 可配置随机失败率
- 可配置图像保存慢
- 可配置数据库写入慢
- 输出压测报告

压测报告包含：
- 总次数
- 成功次数
- 失败次数
- 平均耗时
- P95
- P99
- 最大耗时
- 图像队列最大长度
- 数据库队列最大长度
- 丢图数量
- 丢记录数量
- PLC 写入失败次数
- 相机取图失败次数
- 内存开始/结束值

验收标准：
1. 软件启动后能看到或获取 StartupDiagnostics。
2. 阻塞项失败时系统不能进入 Ready。
3. HealthSnapshot 可以被 UI 或日志查看。
4. 诊断包可以生成。
5. 诊断包不包含明文密码。
6. 可以通过 InspectionId 查询记录。
7. 可以看到最近检测耗时趋势。
8. 模拟压测可以不接硬件运行。
9. 压测结束能输出报告。
10. 项目能构建。

最后请输出：
1. 修改文件列表。
2. StartupDiagnostics 检查项说明。
3. HealthSnapshot 字段说明。
4. 诊断包内容说明。
5. 压测入口说明。
6. 如何验证。
7. 已知风险。
```

---

## TASK-4 审查清单

```text
[ ] StartupDiagnostics 可查看
[ ] 每个自检项有 Pass / Warning / Fail
[ ] 每个失败项有明确原因
[ ] 阻塞项失败时系统不能进入 Ready
[ ] HealthSnapshot 字段足够排障
[ ] 诊断包可以生成
[ ] 诊断包不包含明文密码
[ ] 诊断包没有打包大量历史图片
[ ] 可以通过 InspectionId 查询记录
[ ] 可以查询 NG 记录
[ ] 可以看到最近检测耗时趋势
[ ] 可以清理旧 OK 图并保留 NG 图
[ ] 可以测试 PLC 读写
[ ] 可以测试相机拍照
[ ] 可以测试模型推理
[ ] 模拟压测不依赖真实硬件
[ ] 压测报告字段完整
```

---

# 4. 三天执行版 TODO

## Day 1：工业稳定主干

### TASK-1：工业检测核心重构包

```text
[ ] 创建 Core/Inspection/
[ ] 新增 InspectionIdGenerator
[ ] 新增 InspectionContext
[ ] 新增 InspectionResult
[ ] 新增 InspectionStage
[ ] 新增 InspectionEngineState
[ ] 新增 IInspectionEngine
[ ] 新增 InspectionEngine
[ ] 手动检测接入 InspectionEngine
[ ] PLC 检测接入 InspectionEngine
[ ] 日志贯穿 InspectionId
[ ] 数据库记录贯穿 InspectionId
[ ] 图像文件名贯穿 InspectionId
[ ] 检测失败记录 ErrorStage/ErrorCode/ErrorMessage
[ ] UI 显示当前检测状态
```

### TASK-2A：PLC 与追溯核心

```text
[ ] 新增 PlcProtocolMode：Legacy / HandshakeV1
[ ] Legacy 保持兼容
[ ] 新增 HandshakeV1 配置
[ ] 新增 VisionOnline
[ ] 新增 VisionReady
[ ] 新增 VisionBusy
[ ] 新增 TriggerSeq
[ ] 新增 ResultSeq
[ ] 新增 InspectionDone
[ ] 新增 ErrorCode
[ ] 新增 Heartbeat
[ ] 新增 TraceSaved
[ ] 检测完成写 ResultSeq=TriggerSeq
[ ] 新增 TraceStatus
```

### Day 1 验收

```text
[ ] 项目能构建
[ ] 软件能启动
[ ] 手动检测可运行
[ ] PLC Legacy 未破坏
[ ] 每次检测有 InspectionId
[ ] 图片/数据库/日志可通过 InspectionId 串联
[ ] PLC HandshakeV1 有基本实现
[ ] 检测失败能看到失败阶段
```

---

## Day 2：深度学习平台主干

### TASK-3：配方、模型包、深度学习平台包

```text
[ ] 新增 Recipe
[ ] 新增 RecipeManager
[ ] AppConfig 生成 default recipe
[ ] recipe 原子保存
[ ] recipe history
[ ] recipe rollback
[ ] recipe import/export
[ ] 新增 ModelPackage
[ ] 新增 manifest.json 解析
[ ] 新增 ModelRegistry
[ ] 扫描 models 目录
[ ] 校验 model.onnx 存在
[ ] 校验 model hash
[ ] 校验 labels
[ ] 校验 task
[ ] 校验 input size
[ ] 模型 warmup
[ ] warmup 失败时模型不可用
[ ] 新增 FallbackPolicy
[ ] fallback 受策略控制
[ ] RequireSameLabels=true 时禁止标签不一致辅助模型
[ ] 检测记录写入 RecipeId
[ ] 检测记录写入 RecipeVersion
[ ] 检测记录写入 ModelId
[ ] 检测记录写入 ModelVersion
[ ] 检测记录写入 ModelHash
```

### Day 2 验收

```text
[ ] 旧 AppConfig 仍可启动
[ ] default recipe 可自动生成
[ ] recipe 可保存、备份、回滚
[ ] 模型包可扫描
[ ] manifest 缺失有明确错误
[ ] hash 错误时模型不可用
[ ] labels 不匹配时模型不可用
[ ] warmup 失败时模型不可用
[ ] fallback 策略可配置
[ ] 检测记录包含 recipe/model 信息
```

---

## Day 3：现场可用化与压测

### TASK-2B：健康监控与存储保护

```text
[ ] 新增 HealthMonitor
[ ] 新增 HealthSnapshot
[ ] 记录相机状态
[ ] 记录 PLC 状态
[ ] 记录模型状态
[ ] 记录磁盘状态
[ ] 记录图像队列状态
[ ] 记录数据库队列状态
[ ] 队列丢弃进入报警
[ ] 图像保存失败进入报警
[ ] 数据库保存失败进入报警
[ ] 新增 MinFreeDiskGb
[ ] 磁盘不足时报警
[ ] NG 图像优先保留
```

### TASK-4：启动自检、现场工具、压测验收包

```text
[ ] 新增 StartupDiagnostics
[ ] 检查 WebView2 Runtime
[ ] 检查 OpenCV native dll
[ ] 检查相机 SDK dll
[ ] 检查数据库目录可写
[ ] 检查存储目录可写
[ ] 检查当前 recipe
[ ] 检查模型包
[ ] 检查模型可加载
[ ] 检查 DirectML / CPU runtime
[ ] 检查 PLC 配置
[ ] 检查相机配置
[ ] 检查磁盘剩余
[ ] UI 显示启动自检结果
[ ] UI 或后端暴露 HealthSnapshot
[ ] 新增诊断包导出
[ ] 诊断包包含 logs/config/recipe/model manifest/health/recent records
[ ] 诊断包排除明文密码
[ ] 支持 InspectionId 查询
[ ] 支持 NG 记录基础检索
[ ] 支持最近检测耗时趋势
[ ] 支持清理旧 OK 图
[ ] 支持测试 PLC 读写
[ ] 支持测试相机拍照
[ ] 支持测试模型推理
[ ] 新增 FakeCameraService
[ ] 新增 FakePlcService
[ ] 新增 FakeDetectionService
[ ] 新增模拟压测入口
[ ] 连续模拟触发 1000 次
[ ] 输出压测报告
```

### Day 3 验收

```text
[ ] 项目能构建
[ ] 软件能启动
[ ] StartupDiagnostics 可查看
[ ] 阻塞失败项会阻止 Ready
[ ] HealthSnapshot 可查看
[ ] 诊断包可生成
[ ] 诊断包不包含明文密码
[ ] 可通过 InspectionId 查记录
[ ] 可查看耗时趋势
[ ] 模拟压测可不接硬件运行
[ ] 压测报告包含平均耗时/P95/P99/失败数/队列堆积/内存变化
```

---

# 5. 三天最终交付标准

3 天结束后，不要求所有 UI 都完美，但必须达到下面标准：

```text
[ ] ClearFrost 有独立 InspectionEngine
[ ] 检测流程有 InspectionId
[ ] 每次检测可以通过 InspectionId 串联日志、数据库、图像
[ ] PLC 有 Legacy 和 HandshakeV1 两种模式
[ ] HandshakeV1 支持 TriggerSeq / ResultSeq
[ ] 图像保存失败、数据库保存失败、队列丢弃不再静默
[ ] 系统有 HealthMonitor / HealthSnapshot
[ ] 系统有 RecipeManager
[ ] 系统有 ModelRegistry
[ ] 模型包支持 manifest、hash、labels、warmup 校验
[ ] fallback 策略可配置
[ ] 启动时有 StartupDiagnostics
[ ] 可导出诊断包
[ ] 可运行无硬件模拟压测
[ ] 项目能构建
[ ] 软件能启动
[ ] 旧配置基本兼容
```

---

# 6. 三天内不建议做的事情

为了保证 3 天真的能落地，下面这些先不要做：

```text
复杂账号权限系统
复杂 MES 深度对接
复杂多工位架构
复杂前端动画
完整报表系统
完整云端同步
复杂模型训练管理
完整数据标注系统
多语言国际化
过度美化 UI
完整权限审计系统
复杂用户组织架构
```

本轮重点是工业运行平台主骨架：

```text
先稳定
再平台化
最后现场好用
```

---

# 7. 推荐实际操作节奏

## 第一轮：工业核心

执行：

```text
TASK-1 + TASK-2A
```

审查：

```text
build
启动
手动检测
PLC Legacy
InspectionId
状态机
基本 HandshakeV1
```

---

## 第二轮：深度学习平台

执行：

```text
TASK-3
```

审查：

```text
旧配置兼容
default recipe
recipe 保存/备份/回滚
模型包扫描
manifest 校验
hash 校验
labels 校验
warmup
fallback 策略
检测记录 recipe/model 追溯
```

---

## 第三轮：现场化与压测

执行：

```text
TASK-2B + TASK-4
```

审查：

```text
HealthMonitor
StartupDiagnostics
诊断包
NG 查询
耗时趋势
模拟压测
磁盘保护
队列报警
```

---

## 第四轮：只做修复，不加新功能

目标：

```text
dotnet build 通过
软件能启动
手动检测可运行
Legacy PLC 可用
模拟压测可运行
诊断包可生成
关键错误可见
```

这一轮不允许继续扩功能，只做收敛。

---

# 8. 总控 AI Prompt

可以直接复制给 AI：

```text
你是 ClearFrost 项目的主力 C#/.NET 工业视觉软件工程师。

我要在 3 天内把 ClearFrost 从当前工业视觉检测软件，升级为适合深度学习部署的工业运行平台。请在 feat/v4-industrial-platform-3day 分支上执行开发。

总体目标：
1. 工业检测核心引擎 InspectionEngine。
2. InspectionId 全链路追溯。
3. PLC Legacy 兼容 + HandshakeV1 工业握手。
4. HealthMonitor 健康监控。
5. RecipeManager 配方系统。
6. ModelRegistry 模型包系统。
7. 模型 manifest/hash/labels/warmup 校验。
8. fallback 策略配置化。
9. StartupDiagnostics 启动自检。
10. 诊断包导出。
11. 无硬件模拟压测。

开发原则：
1. 不要破坏旧配置。
2. 不要破坏手动检测。
3. 不要破坏 PLC Legacy 模式。
4. 不要大规模重写 UI。
5. 不要引入大型新依赖。
6. 新功能优先通过配置开关或兼容路径接入。
7. 每个核心失败都必须有日志。
8. 工业现场风险高的地方必须可回滚。
9. 项目必须保持能构建、能启动。
10. 最终输出修改文件列表、验证方式和风险说明。

请按以下顺序执行：
第一部分：实现工业检测核心重构包。
第二部分：实现 PLC 握手、追溯、健康监控包。
第三部分：实现配方、模型包、深度学习平台包。
第四部分：实现启动自检、现场工具、压测验收包。

每完成一部分，请总结：
1. 修改了什么。
2. 新增了什么。
3. 如何验证。
4. 有哪些风险。
5. 哪些地方保留了兼容路径。
```

---

# 9. 统一审查 Prompt

每次 AI 做完一大包后，用这个 Prompt 让它自查，也可以你自己按这个标准审查：

```text
请以工业视觉检测产品的标准审查这次改动。

重点检查：
1. 是否可能导致检测流程阻塞。
2. 是否可能导致 PLC 结果错位。
3. 是否可能导致图像或数据库记录丢失而无报警。
4. 是否破坏旧配置兼容性。
5. 是否破坏手动检测。
6. 是否破坏 PLC Legacy 模式。
7. 是否存在异常未捕获。
8. 是否影响 UI 响应。
9. 是否有足够日志用于现场排障。
10. 是否有回滚或兼容路径。
11. 模型错误是否可能被静默上线。
12. 配方保存失败是否可能损坏当前配置。
13. 诊断包是否可能泄露明文密码。

请输出：
1. 必须修改的问题。
2. 建议修改的问题。
3. 可以后续处理的问题。
4. 是否建议合入。
```

---

# 10. 最终收敛 Checklist

进入最终提交前，必须跑完：

```text
[ ] dotnet build
[ ] 软件启动
[ ] 旧 AppConfig 启动
[ ] 手动检测
[ ] PLC Legacy 模式基本验证
[ ] HandshakeV1 模拟验证
[ ] default recipe 生成
[ ] recipe 保存
[ ] recipe 回滚
[ ] 模型包扫描
[ ] manifest 缺失测试
[ ] hash 错误测试
[ ] labels 不匹配测试
[ ] warmup 失败测试
[ ] HealthSnapshot 查看
[ ] StartupDiagnostics 查看
[ ] 诊断包导出
[ ] 诊断包脱敏检查
[ ] InspectionId 查询
[ ] NG 记录查询
[ ] 最近耗时趋势
[ ] 模拟压测 1000 次
[ ] 压测报告生成
```

---

# 11. 最终判断标准

本轮 3 天开发成功的标志不是“界面多漂亮”，而是：

```text
检测流程有独立核心
PLC 通信有工业握手
模型上线有校验
配置变更可回滚
检测结果可追溯
异常状态可诊断
现场问题可导出
稳定性可压测
```

这就是 ClearFrost 从工业视觉检测软件迈向深度学习工业运行平台的主干版本。
