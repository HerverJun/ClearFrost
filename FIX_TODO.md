# ClearFrost Fix TODO

更新时间: 2026-05-26

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

### 5. 数据集收集只在“全部路径失效”时才做目录兜底

- 状态: 已修复
- 影响:
  若数据库中一部分记录路径有效、另一部分失效，原逻辑不会对失效部分做标准目录补图，最终数据集会少收样本。
- 处理:
  已改为对失效记录做增量兜底，并按记录 ID 去重合并有效路径与补回路径。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`
  - `ClearFrost.Tests/Services/DatasetCollectionServiceTests.cs`

### 6. 数据集复制阶段全部失败时仍会返回成功

- 状态: 已修复
- 影响:
  复制阶段即便所有文件都复制失败，前端也可能显示“收集成功但 0 张”，误导现场人员。
- 处理:
  当实际复制数为 0 时返回失败；部分复制成功时在消息中明确标记为“部分成功”。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`
  - `ClearFrost.Tests/Services/DatasetCollectionServiceTests.cs`

### 7. 图像保存队列的 Channel 配置与读写方式不一致

- 状态: 已修复
- 影响:
  `ImageSaveQueue` 满队列时生产者线程会读取并丢弃最旧项，不能声明 `SingleReader = true`。
- 处理:
  已将 Channel 配置改为多读者语义，和当前丢弃旧图策略一致。
- 相关文件:
  - `ClearFrost/Services/ImageSaveQueue.cs`

### 8. 图像保存队列未检查 `Cv2.ImWrite()` 返回值

- 状态: 已修复
- 影响:
  若未检查 `Cv2.ImWrite()` 返回值，写盘失败会被误计为保存成功，影响健康状态和追溯可信度。
- 处理:
  当前代码已显式校验返回值，失败时抛出 `IOException` 并计入失败计数。
- 相关文件:
  - `ClearFrost/Services/ImageSaveQueue.cs`

### 9. 存储路径在运行时存在多套解释

- 状态: 已修复
- 影响:
  图像保存路径、启动诊断、数据集收集对 `StoragePath` 的解释不一致，盘符失效时容易出现数据落点和诊断状态不一致。
- 处理:
  已统一使用 `StorageService.ResolveStoragePath()`，窗口路径、数据集收集、启动诊断和存储服务共享同一套解析逻辑。
- 相关文件:
  - `ClearFrost/Services/StorageService.cs`
  - `ClearFrost/Services/StartupDiagnostics.cs`
  - `ClearFrost/Services/DatasetCollectionService.cs`
  - `ClearFrost/Views/主窗口.Fields.cs`

### 10. 保存设置后运行时服务不会跟着切换到新存储路径

- 状态: 已修复
- 影响:
  保存配置后 `_storageService` 和 `_statisticsService` 仍绑定旧路径，日志、统计、清理任务可能继续写到旧目录。
- 处理:
  已新增运行时存储路径刷新能力，保存设置和配置迁移导入后同步刷新存储服务与统计服务。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Services/StorageService.cs`
  - `ClearFrost/Services/StatisticsService.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost.Tests/AppRuntimeTests.cs`

### 11. 保存设置后不会重跑启动诊断

- 状态: 已修复
- 影响:
  如果设置修正后诊断结果不刷新，检测入口和 PLC 监听可能继续被旧阻塞项拦截。
- 处理:
  当前保存设置和配置迁移导入流程已调用 `RefreshStartupDiagnostics()`，并会刷新前端健康状态。
- 相关文件:
  - `ClearFrost/Views/主窗口.Init.cs`

### 12. 模型注册表只在启动时扫描一次

- 状态: 已修复
- 影响:
  运行中通过“刷新模型列表”或手动放入 ONNX 文件后，模型虽然可能被加载，但 `_modelRegistry` 没有刷新，检测记录里的 `ModelId` / `ModelVersion` / `ModelHash` 会退化成不完整兜底值。
- 处理:
  已新增运行时模型注册表刷新入口，模型列表刷新、主模型初始化/切换、配置迁移导入后都会重扫注册表并刷新启动诊断/健康状态。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/Views/主窗口.Vision.cs`
  - `ClearFrost.Tests/AppRuntimeTests.cs`

### 13. 启动诊断缺少关键现场配置校验

- 状态: 已修复
- 影响:
  PLC IP/端口、串口触发源、视觉阈值和当前主模型配置错误可能到运行时才暴露，现场排障成本高。
- 处理:
  已补充触发源配置、PLC endpoint、视觉参数和当前主模型解析诊断；阻塞项会阻止生产检测，主模型缺失会给出非阻塞告警。
- 相关文件:
  - `ClearFrost/Services/StartupDiagnostics.cs`
  - `ClearFrost.Tests/Services/StartupDiagnosticsTests.cs`

### 14. 模型包导入缺少标准验收流程

- 状态: 已修复
- 影响:
  现场人员手工拷贝 ONNX 时容易遗漏 manifest、标签、hash 和 legacy ONNX 发布副本，导致模型可加载但追溯字段不完整。
- 处理:
  已新增模型包导入器，导入 ONNX 时生成 manifest、计算 SHA256、写入标签/版本，可同步发布到 ONNX 目录，并通过模型注册表立即验收。
- 相关文件:
  - `ClearFrost/Core/Models/ModelPackageImporter.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost.Tests/Core/Models/ModelPackageImporterTests.cs`
  - `ClearFrost.Tests/AppRuntimeTests.cs`

### 15. 模型包导入/验收能力未接入设置页

- 状态: 已修复
- 影响:
  即使服务层支持模型包验收，现场人员仍需要理解目录结构和手工拷贝流程，容易绕过标准导入链路。
- 处理:
  已在设置页增加“导入 ONNX 并生成模型包”入口，后端会选择 ONNX、收集模型包元数据、导入验收、刷新模型列表并加载为当前主模型。
- 相关文件:
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/settings.js`
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/Views/WebUIController.Messages.cs`
  - `ClearFrost/Views/主窗口.Init.cs`

### 16. 关键运维操作缺少审计日志

- 状态: 已修复
- 影响:
  设置保存、模型包导入、配置迁移、相机打开和 PLC 连接等关键动作只出现在前端运行日志或诊断日志中，现场追溯时缺少统一的按时间归档审计记录。
- 处理:
  已新增 `StorageService.WriteAuditLog`，按小时写入 `Logs/AuditLogs/yyyy年MM月dd日/yyyyMMddHH.txt`，并对关键操作写入成功/失败、类别、动作和详情。
- 相关文件:
  - `ClearFrost/Interfaces/IStorageService.cs`
  - `ClearFrost/Services/StorageService.cs`
  - `ClearFrost/Views/主窗口.Fields.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/Views/主窗口.Camera.cs`
  - `ClearFrost/Views/主窗口.PLC.cs`
  - `ClearFrost.Tests/Services/StorageServiceTests.cs`

### 17. 相机采集中断缺少自动恢复闭环

- 状态: 已修复
- 影响:
  相机 SDK 连接仍在但抓取状态意外停止时，检测链路会直接取帧失败，需要人工重新打开相机，现场连续生产恢复成本高。
- 处理:
  已在 `CameraService.CaptureFrame()` 前确认相机连接和抓取状态；抓取停止时自动重启采集后再取帧，连接断开时同步关闭状态并抛出可见错误。
- 相关文件:
  - `ClearFrost/Services/CameraService.cs`
  - `ClearFrost.Tests/Hardware/Camera/CameraCaptureValidationTests.cs`

### 18. 健康状态缺少长期趋势和维护建议

- 状态: 已修复
- 影响:
  健康状态此前主要反映当前连接、队列和最近错误，良率趋势下滑、磁盘逼近风险、硬件错误频发等现场维护信号不够直观。
- 处理:
  已将统计历史接入 `HealthMonitor`，健康快照新增 `Trends` 和 `MaintenanceAdvices`，会基于最近生产天数生成良率趋势、维护动作和健康等级；前端左侧新增健康趋势卡片展示建议。
- 相关文件:
  - `ClearFrost/Services/HealthMonitor.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost/html/css/style.css`
  - `ClearFrost.Tests/Services/HealthMonitorTests.cs`

### 19. 追溯页缺少现场 CSV 报表导出

- 状态: 已修复
- 影响:
  异常追溯只能在界面查看，现场日报、质量复盘和客户留档需要手工截图或复制字段，容易遗漏条码、班次、模型、配方和耗时信息。
- 处理:
  已新增生产追溯 CSV 导出服务，记录汇总、导出操作员、班次、条码、触发源、模型/配方、耗时、错误和图像路径；追溯弹窗新增“导出CSV”按钮，导出当前日期/小时的 NG 报表到 `Logs/Reports`。
- 相关文件:
  - `ClearFrost/Services/ProductionReportExporter.cs`
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/history.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost.Tests/Services/ProductionReportExporterTests.cs`

### 20. PLC 结果写回缺少可配置重试

- 状态: 已修复
- 影响:
  现场 PLC 写回短暂失败时，原逻辑会直接判定写入失败并记录异常，容易把瞬时通讯抖动扩大成生产停线或误报。
- 处理:
  已新增独立的 PLC 写回重试次数和间隔配置，并接入结果写回与 HandshakeV1 信号写入；启动诊断会提示越界配置，设置页可直接调整。
- 相关文件:
  - `ClearFrost/Config/AppConfig.cs`
  - `ClearFrost/Services/InspectionPipelineService.cs`
  - `ClearFrost/Services/StartupDiagnostics.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/settings.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost.Tests/Services/InspectionPipelineServiceTests.cs`

### 21. PLC 写回反复失败缺少告警升级和处置建议

- 状态: 已修复
- 影响:
  PLC 结果写回或 HandshakeV1 信号写入在短时间内反复失败时，原健康页只展示单条错误，现场人员难以判断是否需要停机排查 PLC 地址、互锁或网络链路。
- 处理:
  已将 10 分钟内 PLC 写回/握手写入失败纳入维护建议；达到 2 次提示 warning，达到 3 次升级 critical，并携带最近检测 ID 和现场验证动作，告警中心会按维护建议自动生成活动告警。
- 相关文件:
  - `ClearFrost/Services/HealthMonitor.cs`
  - `ClearFrost.Tests/Services/HealthMonitorTests.cs`

### 22. 主窗口仍持有活动相机 SDK 句柄

- 状态: 已修复
- 影响:
  窗口层同时持有 `_cameraService` 和旧版 `cam` SDK 句柄，像素格式回退等操作绕过服务层，后续多相机切换、模拟相机判定和硬件恢复容易出现状态不一致。
- 处理:
  已将模拟相机判定和像素格式设置封装到 `ICameraService`，启动首帧失败后的 Mono8 回退改由服务层完成；主窗口不再保存活动 SDK 句柄，并移除未使用的旧版帧转换/查找代码。
- 相关文件:
  - `ClearFrost/Interfaces/ICameraService.cs`
  - `ClearFrost/Services/CameraService.cs`
  - `ClearFrost/Views/主窗口.Fields.cs`
  - `ClearFrost/Views/主窗口.Camera.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost.Tests/Hardware/Camera/CameraCaptureValidationTests.cs`

### 23. 自动生产触发可在未登录操作员时启动

- 状态: 已修复
- 影响:
  PLC/串口光电自动触发监听启动后，检测记录会持续落入“未登录”操作员，削弱班次追溯、责任确认和异常复盘可信度。
- 处理:
  已新增 `RequireOperatorForProductionStart` 配置，默认开启；自动生产触发监听启动前要求已登录操作员，登录后相机就绪时自动恢复触发源，退出登录会停止自动触发监听。设置页可关闭该门禁以兼容特殊无人值守现场。
- 相关文件:
  - `ClearFrost/Config/AppConfig.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/Views/主窗口.PLC.cs`
  - `ClearFrost/Views/主窗口.Vision.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/settings.js`
  - `ClearFrost.Tests/Config/AppConfigTests.cs`

### 24. 强制放行缺少高权限门禁和放行原因

- 状态: 已修复
- 影响:
  “强制放行”会直接向 PLC 写入放行信号，属于质量旁路动作；若普通操作员即可执行且无原因记录，现场复盘无法判断为何绕过检测结果。
- 处理:
  已将 `ManualRelease` 权限提升为 Technician 或更高；前端点击强制放行时要求确认并填写放行原因，后端会再次校验原因不能为空，并将地址、写入值和原因写入审计日志。
- 相关文件:
  - `ClearFrost/Services/OperatorPermissionService.cs`
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/Views/主窗口.PLC.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/boot.js`
  - `ClearFrost.Tests/Services/OperatorPermissionServiceTests.cs`

### 25. 操作员登录会话可跨班次长期沿用

- 状态: 已修复
- 影响:
  `operator-session.json` 中的已登录状态此前不会过期，软件重启后可能继续沿用多天前的操作员身份，导致自动生产触发、手动检测和质量旁路动作绑定到错误人员。
- 处理:
  已新增 `OperatorSessionMaxHours` 配置，默认 12 小时并限制在 1-72 小时；操作员会话服务在加载和读取当前会话时自动让过期或未来时间异常的会话回到“未登录”。设置页可调整会话有效期，配置变更审计会记录该字段。
- 相关文件:
  - `ClearFrost/Config/AppConfig.cs`
  - `ClearFrost/Services/OperatorSessionService.cs`
  - `ClearFrost/Services/ConfigurationChangeTracker.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/settings.js`
  - `ClearFrost.Tests/Services/OperatorSessionServiceTests.cs`
  - `ClearFrost.Tests/Config/AppConfigTests.cs`

### 26. 登录界面可自选高权限角色

- 状态: 已修复
- 影响:
  权限矩阵虽然限制了模型、设置、强制放行等关键动作，但登录界面此前允许直接选择 Technician/Engineer/Administrator，导致角色本身缺少可信边界。
- 处理:
  已新增角色授予校验：Operator 登录保持开放；Technician 及以上角色必须由当前同级/更高级已登录账号确认，或由受信任的本机管理员上下文授予。拒绝时写入权限审计并同步前端会话状态。
- 相关文件:
  - `ClearFrost/Services/OperatorPermissionService.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost.Tests/Services/OperatorPermissionServiceTests.cs`

### 27. 审计日志缺少完整性校验

- 状态: 已修复
- 影响:
  操作审计此前是普通文本行，关键动作记录被人工修改后系统无法提示异常，削弱权限拒绝、配置变更和强制放行记录的审核可信度。
- 处理:
  新写入的审计日志增加 `PrevHash/Hash` 链式 SHA256 摘要；读取审计时会标记 `Valid`、`Tampered` 或 `Legacy`，前端审计表新增完整性列显示“有效/异常/旧版”。
- 相关文件:
  - `ClearFrost/Services/AuditLogIntegrity.cs`
  - `ClearFrost/Services/StorageService.cs`
  - `ClearFrost/Services/AuditLogReader.cs`
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/history.js`
  - `ClearFrost.Tests/Services/StorageServiceTests.cs`
  - `ClearFrost.Tests/Services/AuditLogReaderTests.cs`

### 28. 诊断包缺少内容清单和审计可信度摘要

- 状态: 已修复
- 影响:
  现场导出的诊断包此前只打包配置、健康状态和日志文件，支持人员需要手工判断包内文件是否完整、审计日志是否存在篡改风险。
- 处理:
  诊断包新增 `package_manifest.json`，记录每个条目的大小和 SHA256；新增 `audit_integrity_summary.json`，汇总审计日志 `Valid/Tampered/Legacy` 数量并列出异常/旧版发现，便于现场支持快速定位可信度问题。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageExporter.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

## 待修复

当前 P1 待办已清空，后续继续按现场交付标准补强更多硬件恢复细节、权限审计细节和现场运维指导。

## 建议修复顺序

1. 继续增强 PLC/相机异常恢复细节和维护指导。
2. 继续细化权限拒绝、自动触发暂停和恢复动作的审计/前端提示。

## 备注

- 本文件是当前排查结果的阶段性记录，后续继续深挖时可持续补充。
- 如果下一轮开始实际修复，建议每修完一项就在这里同步状态，避免回归时漏项。
