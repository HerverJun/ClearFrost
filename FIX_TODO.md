# ClearFrost Fix TODO

更新时间: 2026-07-05

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

### 5. 诊断包缺少明确的运行时模型/配方证据清单

- 状态: 已修复
- 影响:
  诊断包虽然包含健康快照、启动诊断和模型注册表，但缺少单独的运行时模型槽位、当前配方版本、启动阻断项和总览 manifest。现场远程排障时，需要人工在多个 JSON 中拼接“当前到底跑的是哪个模型/哪个配方”，且同名 package 与裸 ONNX 容易误判。
- 处理:
  诊断包新增 `diagnostic_manifest.json`、`recipe_summary.json`、`runtime_model_slots.json`、`model_registry_diagnostics.json`、`startup_blockers.json`；运行时槽位和现场诊断模型摘要均优先按完整模型路径匹配注册表，再按名称兜底，避免同名模型误配。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageExporter.cs`
  - `ClearFrost/Services/FieldDiagnostics.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

### 6. 前端现场诊断页缺少一眼可读的验收清单

- 状态: 已修复
- 影响:
  WebUI 诊断弹窗只显示设备基础状态、最近错误和调试按钮，现场售后仍需导出诊断包或翻日志，才能确认运行模型槽位、当前配方版本、启动阻断和队列风险。
- 处理:
  诊断弹窗新增“现场验收清单”，直接展示运行模型槽位匹配结果、当前配方版本与目标、启动阻断项和队列健康；前端源码与实际加载的 `bundle.js` 同步更新，并在契约测试中同时校验两者。
- 相关文件:
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost/html/css/style.css`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### 7. 诊断异常缺少结构化处理建议

- 状态: 已修复
- 影响:
  现场诊断页能显示启动阻断、模型未匹配、队列积压和最近错误，但维护人员仍需靠经验判断下一步操作，远程排障成本高。
- 处理:
  `FieldDiagnosticsSnapshot` 新增 `MaintenanceAdvice`，按启动阻断、相机/PLC 状态、模型运行时、模型注册表、队列压力和最近错误生成结构化建议；诊断包新增 `maintenance_advice.json`，前端诊断页新增“维护建议”区域并同步实际加载的 `bundle.js`。
- 相关文件:
  - `ClearFrost/Services/FieldDiagnostics.cs`
  - `ClearFrost/Services/DiagnosticPackageExporter.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost/html/css/style.css`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### 8. 诊断包缺少可直接阅读的现场报告

- 状态: 已修复
- 影响:
  诊断包已经包含多份 JSON，但远程售后或现场工程师仍需要打开多个文件拼接模型、配方、阻断项、队列和维护建议，不利于快速首轮判断。
- 处理:
  诊断包新增 `field_report.md`，汇总应用版本、启动状态、运行模型槽位、当前配方、队列与性能、启动阻断、维护建议、最近错误和关键包内文件；报告生成时会脱敏本地路径、操作员和条码相关内容，测试覆盖报告存在性、关键内容和路径脱敏。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageExporter.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

### 9. 诊断包导出缺少操作审计追溯

- 状态: 已修复
- 影响:
  现场人员导出诊断包后，系统只能在前端日志里看到即时提示，缺少可查询的操作者、角色、导出路径和当时启动阻断/维护建议数量，后续复盘远程排障过程不够完整。
- 处理:
  `AppRuntime.ExportDiagnosticPackageAsync()` 在导出成功或失败后写入 `OperationAuditService`，记录 `DiagnosticPackageExport` 操作、操作者、角色、包路径、启动阻断数量、维护建议数量和队列状态；新增回归测试验证成功导出时审计记录可查询。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost.Tests/AppRuntimeTests.cs`

### 10. 诊断包远程传输后缺少内容完整性索引

- 状态: 已修复
- 影响:
  诊断包在现场拷贝、远程传输或归档后，只能确认 zip 文件存在，无法快速核验包内关键 JSON、现场报告和日志片段是否被截断、漏传或被替换。
- 处理:
  诊断包新增 `diagnostic_index.json`，记录除索引自身外每个包内条目的文件名、原始字节数和 SHA-256；导出测试会逐条读取 zip 内原始字节并重算哈希，验证索引可用于远程完整性核验。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageExporter.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

### 11. 诊断包完整性摘要没有出现在导出结果和审计中

- 状态: 已修复
- 影响:
  即使诊断包内部已经包含 `diagnostic_index.json`，现场人员导出后仍需要手动打开 zip 才能拿到核验信息，操作审计也无法直接记录本次导出的包级 SHA-256 和索引 SHA-256。
- 处理:
  `AppRuntime.ExportDiagnosticPackageAsync()` 返回导出摘要，包含包路径、zip SHA-256、索引 SHA-256、包大小和索引条目数；WebUI 导出结果显示短哈希、大小和条目数，完整哈希保留在 tooltip；操作审计写入同一份摘要，便于远程复核。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.Diagnostics.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost/html/css/style.css`
  - `ClearFrost.Tests/AppRuntimeTests.cs`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### 12. 诊断包导出可能覆盖旧包或留下半成品

- 状态: 已修复
- 影响:
  诊断包文件名原先只精确到秒，快速连续导出可能覆盖上一份包；同时导出过程直接写最终 zip，若被取消或中途失败，现场目录中可能留下半成品，误导远程排障。
- 处理:
  诊断包文件名增加毫秒和短随机后缀，导出时先写隐藏临时 `.tmp`，完整关闭 zip 后再移动到最终路径；失败或取消会清理临时文件。新增测试覆盖连续导出唯一性和取消导出不留半包。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageExporter.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

### 13. 诊断包缺少可复用的完整性校验器

- 状态: 已修复
- 影响:
  诊断包虽然已经生成 `diagnostic_index.json`，但系统内没有统一的校验入口。售后或后续 WebUI 功能若要判断包是否完整，需要重复编写解析 zip、重算 SHA-256、比对长度和哈希的逻辑。
- 处理:
  新增 `DiagnosticPackageIntegrityVerifier`，可读取诊断包索引并校验每个包内条目的存在性、字节数和 SHA-256，返回 Healthy/Warning/Blocking 和结构化 findings；测试覆盖正常包返回 Healthy，以及篡改 `field_report.md` 后返回 Blocking。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageIntegrityVerifier.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

### 14. 诊断包导出成功前没有执行完整性自检

- 状态: 已修复
- 影响:
  系统虽然可以生成索引并提供校验器，但导出主链路仍可能在未自检的情况下向前端报告成功，现场人员需要额外步骤才能确认当前包是否真正可用。
- 处理:
  `AppRuntime.ExportDiagnosticPackageAsync()` 在导出后立即调用 `DiagnosticPackageIntegrityVerifier`，只有 Healthy 才写成功审计并返回 WebUI；自检失败会删除本次生成的包并按失败审计。导出结果和前端诊断包区域新增自检状态、已验证条目数和 findings 数量。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.Diagnostics.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost/html/css/style.css`
  - `ClearFrost.Tests/AppRuntimeTests.cs`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

## 待修复

### P1. 存储路径在运行时存在多套解释

- 状态: 已修复
- 影响:
  图像保存路径、启动诊断、数据集收集对 `StoragePath` 的解释并不一致。配置盘符失效时，部分逻辑会回退到 `C:\GreeVisionData`，但另一些逻辑仍使用原始配置路径，容易出现“程序能跑、数据集收不到、诊断还报错”的混乱状态。
- 建议:
  提供统一的“已解析存储根路径”来源，所有运行时组件都只消费这一处结果。
- 处理:
  窗口层 `BaseStoragePath` 优先读取 `_storageService.BaseStoragePath`，数据集收集也改为使用实际生效路径，避免绕过 `StorageService` 的盘符回退逻辑。
- 相关文件:
  - `ClearFrost/Views/主窗口.Fields.cs`
  - `ClearFrost/Services/StorageService.cs`
  - `ClearFrost/Services/StartupDiagnostics.cs`
  - `ClearFrost/Views/主窗口.Init.cs`

### P1. 保存设置后不会重跑启动诊断

- 状态: 已修复
- 影响:
  如果程序启动时诊断失败，用户后来在设置里修正了 PLC 地址、存储路径或模型环境，`_startupDiagnostics.CurrentReport` 仍然保留旧结果，检测入口和 PLC 监听仍可能被继续拦截，通常需要重启才能恢复。
- 建议:
  设置保存成功后立即重跑启动诊断，并同步刷新前端健康状态。
- 处理:
  设置保存成功后会调用 `AppRuntime.RefreshStoragePath()` 和现有 `RefreshStartupDiagnostics()`，同步刷新启动诊断、运行时健康快照和前端状态。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.cs`
  - `ClearFrost/Views/主窗口.Init.cs`

### P1. 保存设置后运行时服务不会跟着切换到新存储路径

- 状态: 已修复
- 影响:
  保存配置后，图像目录会跟随 `_appConfig.StoragePath` 变化，但 `_storageService` 和 `_statisticsService` 仍绑定旧路径，导致日志、统计、清理任务可能继续写到旧目录。
- 建议:
  统一路径来源，或者在设置保存后重建依赖存储根路径的运行时服务。
- 处理:
  设置保存和配置迁移刷新后都会调用 `AppRuntime.RefreshStoragePath()`，同步 `StorageService`、`StatisticsService` 与启动诊断；新增 `AppRuntimeTests.RefreshStoragePath_运行时服务切换到最新存储路径` 防止回归。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Services/StatisticsService.cs`
  - `ClearFrost/Views/主窗口.Init.cs`

### P1. 模型注册表只在启动时扫描一次

- 状态: 已修复
- 影响:
  运行中通过“刷新模型列表”或手动放入 ONNX 文件后，模型虽然可能被加载，但 `_modelRegistry` 没有刷新，检测记录里的 `ModelId` / `ModelVersion` / `ModelHash` 会退化成不完整兜底值。
- 建议:
  在模型列表刷新或模型切换后，补充注册表重扫和前端同步。
- 处理:
  模型列表选项刷新会重新扫描注册表；检测记录追溯优先按运行时已加载模型路径解析注册表，避免同名 package/bare ONNX 误匹配。新增 `GetSelectionOptions_运行中新加入Onnx会被刷新发现` 和 `ExecuteAsync_模型追溯优先按运行时路径解析` 回归测试。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/Views/主窗口.Vision.cs`

### P1. 数据集收集只在“全部路径失效”时才做目录兜底

- 状态: 已修复
- 影响:
  当前逻辑只有在 `validRecords.Count == 0` 时才尝试标准目录匹配和磁盘扫描。若数据库中一部分记录路径有效、另一部分失效，则失效部分不会被补回，最终数据集会少收一截。
- 建议:
  对无效记录做增量兜底，而不是只有在全量失效时才进入兜底流程。
- 处理:
  对无效记录执行标准目录增量匹配，并新增 `CollectAsync_部分路径失效_增量标准目录补回` 回归测试。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`

### P1. 数据集复制阶段全部失败时仍会返回成功

- 状态: 已修复
- 影响:
  复制阶段即便所有文件都复制失败，最终仍会返回 `Success = true`，前端可能显示“收集成功但 0 张”，会误导现场人员。
- 建议:
  当实际复制数为 0 且存在复制错误时，返回失败结果；部分成功时则明确标记为部分成功。
- 处理:
  复制数为 0 时返回失败并清理空数据集目录；新增 `CollectAsync_复制阶段全部失败_返回失败并清理空目录` 回归测试。
- 相关文件:
  - `ClearFrost/Services/DatasetCollectionService.cs`

### P1. 图像保存队列未检查 `Cv2.ImWrite()` 返回值

- 状态: 已修复
- 影响:
  `Cv2.ImWrite()` 返回 `bool`，当前代码没有检查返回值。写盘函数即便返回 `false`，也会被计入 `SavedCount`，导致健康状态、追溯状态和数据集缺图问题都被掩盖。
- 建议:
  明确校验返回值，失败时计入 `FailedCount`，并补充日志和健康告警。
- 处理:
  写图返回 `false` 时抛出写盘异常并计入失败；新增 `ImageSaveQueue_写图返回False会计入失败` 回归测试。
- 相关文件:
  - `ClearFrost/Services/ImageSaveQueue.cs`

### P2. 图像保存队列的 Channel 配置与读写方式不一致

- 状态: 已修复
- 影响:
  `ImageSaveQueue` 把 Channel 配成了 `SingleReader = true`，但在队列满时生产者线程也会直接 `Reader.TryRead()` 丢弃旧项，这与单读者假设不一致，可能引入并发下的不可预期行为。
- 建议:
  要么改为合法的多读者配置，要么改写丢弃策略，避免生产者直接读队列。
- 处理:
  Channel 使用 `SingleReader = false`，并新增 `ImageSaveQueue_队列满时丢弃最旧待写项并保持计数一致` 回归测试。
- 相关文件:
  - `ClearFrost/Services/ImageSaveQueue.cs`

### P2. 诊断包核验摘要缺少一键复制入口

- 状态: 已修复
- 影响:
  现场导出诊断包后，包路径、SHA-256、自检状态和索引条目数只能人工抄录，交接给研发或质检时容易漏项或抄错。
- 建议:
  在诊断包导出区域增加一键复制核验摘要，并让运行时加载的 bundle 与源脚本保持一致。
- 处理:
  诊断包区域新增“复制核验摘要”按钮，复制内容包含路径、包 SHA-256、索引 SHA-256、大小、自检状态、索引/验证条目数、异常数量和导出时间；补充 WebUI 契约测试覆盖 HTML 入口、`render-main.js` 与 `bundle.js` 的摘要构造和命令导出。
- 相关文件:
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### P2. 诊断包缺少历史列表和二次核验入口

- 状态: 已修复
- 影响:
  现场人员只能看到刚导出的诊断包，后续远程售后若要复查上一包或确认传输前后是否被篡改，需要人工到目录里找文件并另行运行校验逻辑，不利于交接闭环。
- 建议:
  诊断页提供最近诊断包列表，并支持对历史包一键复核；复核动作必须约束在诊断目录内并写入操作审计。
- 处理:
  新增诊断包历史扫描和 `DiagnosticPackageVerify` 审计，WebUI 诊断包区域新增历史列表、刷新和复核入口；复核命令复用完整性校验器，返回包 SHA-256、索引 SHA-256、自检状态、条目数和异常数，并拒绝诊断目录外路径。新增运行时测试覆盖历史排序、相对文件名复核、审计记录和目录约束。
- 相关文件:
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/Views/主窗口.Diagnostics.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/state.js`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost.Tests/AppRuntimeTests.cs`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### P2. 诊断包复核和维护复检结果缺少现场交接报告

- 状态: 已修复
- 影响:
  诊断包复核结果、维护建议处理/复检记录和关键审计已经存在，但交班时仍需要现场人员分别打开诊断页、审计 outbox 和诊断包历史进行人工汇总，容易漏掉复检失败项或诊断包完整性结论。
- 建议:
  提供一键导出的现场交接报告，按当前诊断快照汇总设备、模型、配方和队列状态，并把诊断包导出/复核审计、维护建议闭环和下一班关注项放进同一份可归档文件。
- 处理:
  新增 `FieldHandoffReportExporter`，导出 Markdown 现场交接报告；`AppRuntime.ExportFieldHandoffReportAsync()` 会聚合当前诊断快照、最近诊断包、维护建议历史和关键审计，并写入 `FieldHandoffReportExport` 审计；诊断快照新增结构化 `ShiftTasks`，WebUI 诊断中心新增“班次待办”、导出交接报告、历史报告刷新和“复制交接摘要”入口，便于交班时快速追溯最近报告和当前未闭环事项；班次待办卡片的“已处理/复检”会通过 `ShiftTaskAction` 单独审计，并同步复用维护建议闭环。
- 相关文件:
  - `ClearFrost/Services/FieldHandoffReportExporter.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/Views/主窗口.Diagnostics.cs`
  - `ClearFrost/Views/主窗口.Init.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/state.js`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/css/style.css`
  - `ClearFrost.Tests/AppRuntimeTests.cs`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### P2. 班次待办缺少责任组、截止时间和超时升级

- 状态: 已修复
- 影响:
  交接报告和诊断页虽然能列出班次待办，但缺少责任组、首次发现、截止时间和升级等级；若截止时间按刷新时刻滚动计算，未处理问题会一直显示为未超时，现场班组难以判断哪些事项必须优先升级。
- 建议:
  为维护建议记录稳定的首次发现时间，并按等级/复检状态推导责任组、截止时间、超时状态和升级等级；WebUI 与交接报告同步显示这些字段。
- 处理:
  新增维护建议首次发现记录，班次待办会按首次发现或最近动作计算 SLA；PLC/相机/模型/队列/存储/数据库类问题自动分派建议责任组，超时项升级为 `Overdue` 并在前端高亮。交接报告新增首次发现、截止和升级列，契约测试覆盖运行时 bundle 中的字段。
- 相关文件:
  - `ClearFrost/Services/MaintenanceAdviceResolutionStore.cs`
  - `ClearFrost/Services/FieldDiagnostics.cs`
  - `ClearFrost/Services/FieldHandoffReportExporter.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost/html/css/style.css`
  - `ClearFrost.Tests/AppRuntimeTests.cs`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### P2. 关键操作审计缺少防篡改证据链

- 状态: 已修复
- 影响:
  操作审计 outbox 原先能记录和查询关键操作，但单条 NDJSON 被人工修改、删除或重排后，系统无法在交班或质检时给出明确证据，生产放行、维护复检、诊断包复核等记录的可信度不足。
- 建议:
  审计写入时追加上一条记录哈希和自身哈希，提供链式校验入口，并把链状态纳入现场交接报告。
- 处理:
  `OperationAuditService` 新增 `PreviousRecordSha256` / `RecordSha256` 链式封存和 `VerifyChainAsync()` 校验；CSV 导出包含哈希字段，交接报告汇总审计链状态、已验证条数和异常数量。测试覆盖正常链路 Healthy、篡改后 Blocking，以及交接报告写入 `AuditChainStatus=Healthy`。
- 相关文件:
  - `ClearFrost/Services/OperationAuditService.cs`
  - `ClearFrost/Services/FieldHandoffReportExporter.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost.Tests/Services/OperationAuditServiceTests.cs`
  - `ClearFrost.Tests/AppRuntimeTests.cs`

### P2. 诊断包缺少审计链可信度摘要

- 状态: 已修复
- 影响:
  关键操作审计已经有链式哈希，但现场远程排障通常优先传诊断包；如果诊断包不包含审计链校验摘要，研发或质检仍无法从单个包判断本机 outbox 是否存在改写、断链或旧格式风险。
- 建议:
  诊断包只包含脱敏后的审计链摘要，不打包原始 outbox；摘要应进入完整性索引和现场报告。
- 处理:
  诊断包新增 `operation_audit_chain.json`，包含审计链状态、记录总数、已验证数量、最后记录 SHA-256 和脱敏 findings；`diagnostic_manifest.json` 与 `field_report.md` 同步展示审计链结论，完整性索引会覆盖该摘要。测试验证摘要不泄漏本地路径，且篡改 finding 可在报告中定位。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageExporter.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

### P2. 诊断包完整性校验未验证索引元数据

- 状态: 已修复
- 影响:
  `diagnostic_index.json` 已能逐条记录包内文件长度和 SHA-256，但校验器原先没有检查索引自身声明的哈希算法、条目数和总未压缩字节数。如果索引元数据被人工改写，远程复核可能仍只看到条目级校验结果，缺少对索引自描述一致性的阻断判断。
- 建议:
  校验器应验证索引格式版本、哈希算法、`EntryCount` 与条目数、`TotalUncompressedBytes` 与索引条目长度之和。
- 处理:
  `DiagnosticPackageIntegrityVerifier` 新增索引元数据校验，发现不支持的哈希算法、条目数不一致、总字节数不一致或负数统计值时返回 Blocking finding；新增测试覆盖只篡改 `diagnostic_index.json` 元数据即可触发 Blocking。
- 相关文件:
  - `ClearFrost/Services/DiagnosticPackageIntegrityVerifier.cs`
  - `ClearFrost.Tests/Services/DiagnosticPackageExporterTests.cs`

### P2. WebUI 审计中心看不到审计链状态

- 状态: 已修复
- 影响:
  审计 outbox 已具备链式防篡改能力，交接报告和诊断包也能携带摘要，但现场操作员在 WebUI 审计中心只能查记录和导出 CSV，无法当场确认审计链是否 Healthy、是否有断链或改写风险。
- 建议:
  在审计弹窗增加审计链校验入口和状态摘要，并在记录表中显示记录哈希短码。
- 处理:
  WebUI 新增 `verify_audit_chain` 命令，审计弹窗打开时自动校验链状态，也支持手动“校验链”；状态条展示 Healthy/Warning/Blocking、已验证条数、异常数和最后记录哈希。审计记录查询结果带 `previousRecordSha256` / `recordSha256`，表格显示记录哈希短码；契约测试覆盖 HTML、history 源码、运行 bundle 和控制器命令。
- 相关文件:
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/history.js`
  - `ClearFrost/html/js/bundle.js`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

### P2. 启动诊断未覆盖关键证据目录

- 状态: 已修复
- 影响:
  启动诊断原先只确认存储根、日志和数据库目录可写，但维护闭环、审计 outbox、诊断包和交接报告分别落在 `System` 与 `Logs` 下的子目录中。如果这些子目录被权限、占用或部署策略破坏，系统可能运行后才在导出/审计时失败。
- 建议:
  将关键证据目录纳入启动自检；审计 outbox 和 `System` 证据目录应阻塞启动，诊断包与交接报告目录应至少显示非阻塞诊断项。
- 处理:
  `StartupDiagnostics` 新增 `System evidence directory`、`Audit outbox directory`、`Diagnostic package directory`、`Handoff report directory` 可写检查；测试验证目录会被创建、状态为 Pass，并区分阻塞与非阻塞属性。
- 相关文件:
  - `ClearFrost/Services/StartupDiagnostics.cs`
  - `ClearFrost.Tests/Services/StartupDiagnosticsTests.cs`

### P2. 离线回放读图可能短暂占用样本文件

- 状态: 已修复
- 影响:
  离线回放使用 OpenCV `ImRead(path)` 直接按路径读图时，Windows 上偶发出现源图片句柄释放滞后；批量回放后立即归档、清理或替换样本文件时，可能遇到文件被占用。
- 建议:
  回放服务先由 .NET 读取图片字节并关闭文件句柄，再用 OpenCV `ImDecode` 解码为 `Mat`，确保进入推理前源文件已释放。
- 处理:
  `OfflineReplayService` 改为 `File.ReadAllBytesAsync()` + `Cv2.ImDecode()`，保留原有缺图、坏图、推理失败统计语义；离线回放测试和全链路针对性测试覆盖通过。
- 相关文件:
  - `ClearFrost/Services/OfflineReplayService.cs`
  - `ClearFrost.Tests/Services/OfflineReplayServiceTests.cs`

### P2. 维护建议只能查看，缺少处理/复检闭环

- 状态: 已修复
- 影响:
  诊断页能给出维护建议，但现场人员处理后无法在系统内留下“谁处理、何时处理、复检是否通过”的证据，交班或远程售后复盘仍要依赖口头记录。
- 建议:
  为维护建议生成稳定标识，支持“已处理”和“复检”动作；复检应基于当前诊断快照判断问题是否仍存在，并写入操作审计。
- 处理:
  新增维护建议闭环存储和 `MaintenanceAdviceAction` 审计；诊断快照会带维护建议处理状态和最近处理记录；WebUI 维护建议卡片新增“已处理/复检”入口，并显示最近处理/复检记录。复检通过表示该建议已从当前诊断快照消失，复检失败则保留为可筛选审计风险。
- 相关文件:
  - `ClearFrost/Services/MaintenanceAdviceResolutionStore.cs`
  - `ClearFrost/Services/FieldDiagnostics.cs`
  - `ClearFrost/AppRuntime.cs`
  - `ClearFrost/Views/WebUIController.cs`
  - `ClearFrost/Views/主窗口.Diagnostics.cs`
  - `ClearFrost/html/index.html`
  - `ClearFrost/html/js/state.js`
  - `ClearFrost/html/js/render-main.js`
  - `ClearFrost.Tests/AppRuntimeTests.cs`
  - `ClearFrost.Tests/Views/WebUIDiagnosticsPageContractTests.cs`

## 建议修复顺序

1. 已完成：统一存储根路径解析，并修复“保存设置后不重跑诊断/不重建相关服务”的问题。
2. 已完成：补充 `ImageSaveQueue` 的极端失败测试，确认真实写盘判定和 Channel 丢弃策略在高并发下稳定。
3. 已完成：补充数据集收集的部分路径失效、复制阶段 0 成功等回归测试。
4. 已完成：验证模型注册表刷新链路在模型切换、包审批和前端列表刷新时的追溯字段完整性。

下一轮建议：继续增强现场运行闭环，例如为交接报告增加历史列表、打开/复制摘要入口，并把关键阻断项升级为班次级待办看板。

## 备注

- 本文件是当前排查结果的阶段性记录，后续继续深挖时可持续补充。
- 如果下一轮开始实际修复，建议每修完一项就在这里同步状态，避免回归时漏项。
