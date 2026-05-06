# ClearFrost Fix TODO

更新时间: 2026-05-06

## 说明

这个文件用于暂存本轮排查中已经确认的问题，便于后续逐项修复和回归验证。

## 已修复

### 1. 数据集收集会提前过滤掉空路径记录

- 状态: 已修复
- 影响:
  当 `DetectionRecords` 中 `ImagePath` / `RenderedImagePath` 为空时，原逻辑会在 SQL 层直接过滤掉这些记录，导致后续标准目录兜底逻辑根本没有机会执行。
- 处理:
  已移除该 SQL 过滤条件，允许后续通过标准目录和文件名规则补回图片。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`
  - `ClearFrost.Tests/Services/DatasetCollectionServiceTests.cs`

### 2. `RenderedImagePath` 失效时不会回退到原图

- 状态: 已修复
- 影响:
  当 `RenderedImagePath` 已失效但 `ImagePath` 仍然有效时，数据集收集会错误地把整条记录视为无效，导致可收集样本被漏掉。
- 处理:
  已调整路径解析顺序: 优先使用存在的渲染图，不存在时回退到存在的原图。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`
  - `ClearFrost.Tests/Services/DatasetCollectionServiceTests.cs`

### 3. 标准目录按时间匹配时比较了错误日期

- 状态: 已修复
- 影响:
  `ExtractTimeFromFileName()` 只返回 `0001-01-01 HH:mm:ss`，原逻辑直接拿它和真实检测时间比较，导致时间兜底匹配几乎永远失败。
- 处理:
  已将文件名解析出的时间挂到记录当天日期上，再做时间窗口匹配。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`
  - `ClearFrost.Tests/Services/DatasetCollectionServiceTests.cs`

### 4. 设置页“快速切换项目预设”块位置异常

- 状态: 已修复
- 影响:
  预设区块被放在错误容器位置，导致设置页里看不到“快速切换项目预设”按钮区域。
- 处理:
  已将预设区块和数据集收集区块移回设置页右侧主内容区。
- 相关文件:
  - `ClearFrost/html/index.html`

## 待修复

### P1. 存储路径在运行时存在多套解释

- 状态: 待修复
- 影响:
  图像保存路径、启动诊断、数据集收集对 `StoragePath` 的解释并不一致。配置盘符失效时，部分逻辑会回退到 `C:\GreeVisionData`，但另一些逻辑仍使用原始配置路径，容易出现“程序能跑、数据集收不到、诊断还报错”的混乱状态。
- 建议:
  提供统一的“已解析存储根路径”来源，所有运行时组件都只消费这一处结果。
- 相关文件:
  - `ClearFrost/Views/主窗口.Fields.cs`
  - `ClearFrost/Services/StorageService.cs`
  - `ClearFrost/Services/StartupDiagnostics.cs`
  - `ClearFrost/Views/主窗口.Init.cs`

### P1. 保存设置后不会重跑启动诊断

- 状态: 待修复
- 影响:
  如果程序启动时诊断失败，用户后来在设置里修正了 PLC 地址、存储路径或模型环境，`_startupDiagnostics.CurrentReport` 仍然保留旧结果，检测入口和 PLC 监听仍可能被继续拦截，通常需要重启才能恢复。
- 建议:
  设置保存成功后立即重跑启动诊断，并同步刷新前端健康状态。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.cs`
  - `ClearFrost/Views/主窗口.Init.cs`

### P1. 保存设置后运行时服务不会跟着切换到新存储路径

- 状态: 待修复
- 影响:
  保存配置后，图像目录会跟随 `_appConfig.StoragePath` 变化，但 `_storageService` 和 `_statisticsService` 仍绑定旧路径，导致日志、统计、清理任务可能继续写到旧目录。
- 建议:
  统一路径来源，或者在设置保存后重建依赖存储根路径的运行时服务。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Services/StatisticsService.cs`
  - `ClearFrost/Views/主窗口.Init.cs`

### P1. 模型注册表只在启动时扫描一次

- 状态: 待修复
- 影响:
  运行中通过“刷新模型列表”或手动放入 ONNX 文件后，模型虽然可能被加载，但 `_modelRegistry` 没有刷新，检测记录里的 `ModelId` / `ModelVersion` / `ModelHash` 会退化成不完整兜底值。
- 建议:
  在模型列表刷新或模型切换后，补充注册表重扫和前端同步。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/Views/主窗口.Vision.cs`

### P1. 数据集收集只在“全部路径失效”时才做目录兜底

- 状态: 待修复
- 影响:
  当前逻辑只有在 `validRecords.Count == 0` 时才尝试标准目录匹配和磁盘扫描。若数据库中一部分记录路径有效、另一部分失效，则失效部分不会被补回，最终数据集会少收一截。
- 建议:
  对无效记录做增量兜底，而不是只有在全量失效时才进入兜底流程。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`

### P1. 数据集复制阶段全部失败时仍会返回成功

- 状态: 待修复
- 影响:
  复制阶段即便所有文件都复制失败，最终仍会返回 `Success = true`，前端可能显示“收集成功但 0 张”，会误导现场人员。
- 建议:
  当实际复制数为 0 且存在复制错误时，返回失败结果；部分成功时则明确标记为部分成功。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`

### P1. 图像保存队列未检查 `Cv2.ImWrite()` 返回值

- 状态: 待修复
- 影响:
  `Cv2.ImWrite()` 返回 `bool`，当前代码没有检查返回值。写盘函数即便返回 `false`，也会被计入 `SavedCount`，导致健康状态、追溯状态和数据集缺图问题都被掩盖。
- 建议:
  明确校验返回值，失败时计入 `FailedCount`，并补充日志和健康告警。
- 相关文件:
  - `ClearFrost/Services/ImageSaveQueue.cs`

### P2. 图像保存队列的 Channel 配置与读写方式不一致

- 状态: 待修复
- 影响:
  `ImageSaveQueue` 把 Channel 配成了 `SingleReader = true`，但在队列满时生产者线程也会直接 `Reader.TryRead()` 丢弃旧项，这与单读者假设不一致，可能引入并发下的不可预期行为。
- 建议:
  要么改为合法的多读者配置，要么改写丢弃策略，避免生产者直接读队列。
- 相关文件:
  - `ClearFrost/Services/ImageSaveQueue.cs`

## 建议修复顺序

1. 统一存储根路径解析，并修复“保存设置后不重跑诊断/不重建相关服务”的问题。
2. 修复 `ImageSaveQueue` 的真实写盘判定和 Channel 读写策略。
3. 修复数据集收集的增量兜底与“0 张仍成功”判定。
4. 补上模型注册表的运行时刷新逻辑。

## 备注

- 本文件是当前排查结果的阶段性记录，后续继续深挖时可持续补充。
- 如果下一轮开始实际修复，建议每修完一项就在这里同步状态，避免回归时漏项。
