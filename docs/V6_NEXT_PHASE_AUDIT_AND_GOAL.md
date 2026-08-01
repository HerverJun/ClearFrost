# V6 下一阶段代码审计与唯一首要 Goal

> 审计日期: 2026-08-01
> 代码证据基线: `github/V6_test@f8a8944f15e0800746b5191a081be8bf9afce219`
> 审计结论: 审计工作 `PASS`；V6 正式发布资格 `BLOCKED`
> 权威性: 本文是下一阶段执行入口。README、FIX_TODO、历史报告和既有 V6 文档仅作线索；若与本文冲突，以本文列出的当前代码、命令结果和外部状态为准。

## 1. 当前基线

### 1.1 Git 与远端

| 项目 | 审计结果 |
| --- | --- |
| 仓库 | `C:\Users\HerverJun\Desktop\ClearFrostV5` |
| 当前分支 | `V6_test` |
| 初始 HEAD | `f8a8944f15e0800746b5191a081be8bf9afce219` |
| 上游 | `github/V6_test` |
| 拉取后状态 | 本地 HEAD 与 `github/V6_test` 完全一致，ahead/behind 为 `0/0` |
| 初始工作树 | 干净 |
| GitHub main | `4d843a9f490e7680f2ac72f31993dcaac3b63bc6` |
| merge-base | `05bdaecce1cfa41f8f73583ce09e51d7cba18611` |
| 分叉 | `main` 独有 7 个提交，`V6_test` 独有 37 个提交 |
| main 操作 | 未修改、未切换、未合并、未重写、未删除 |
| 分支保护 | GitHub API 显示 `V6_test` 与 `main` 均未保护，required checks 为空 |

`f8a8944` 是一个合并提交，父提交为 `69436da` 和 `ceeb2d8`，冲突点是 `ClearFrost/Core/MultiModelManager.cs`。因此当前基线不是单线快进结果，后续修改必须以最终合并树为准，不能只阅读其中一个父分支。

`main` 的 7 个独有提交包含相机恢复、模型注册表刷新、存储路径和追溯 UI 等修复。本文不判断其是否应合入 V6，也不授权合并；如需吸收，只能在后续独立审计中逐提交证明。

### 1.2 最近一批提交实际增加的能力

| 提交组 | 当前代码中实际存在的增量 |
| --- | --- |
| `e095997` 至 `707e41f` | 生产授权、操作审计 outbox、检测生命周期锁、PLC handshake、生产闭环验证与 fail-closed 路径 |
| `dbb4691` 至 `35eb118` | 模型激活事务恢复、Replay 数据集/审批/evidence/gate/hash、生产模型身份绑定和补偿路径 |
| `d87c1cb` 至 `5bfa4e3` | 视觉调试工作台、现场诊断、交接报告、发布器、现场工位模板与轻量部署 UI |
| `e054cbb` 与 `c5fa7da` | Classification、Segmentation、OBB、Pose 的 descriptor、后处理、摘要、规则、ResultJson/UI 贯通，以及 synthetic smoke 框架 |
| `69436da` | 通用深度学习后处理注册表、postprocess options/模型元数据贯通、追溯身份扩展、大量 Web UI 桥接合同测试 |
| `7d7c7b5` 经 `f8a8944` 合入 | `IVisionModel`、`ModelResult`、`UnsupervisedDetector`，以及多模型管理器选择/回退路径调整 |

这些增量说明 V6 已拥有明显超过 V6.0 初始范围的 V6.1 研发代码面，但“代码面扩大”不等于“发布成熟度提高”。

### 1.3 版本、文档与发布身份

| 证据 | 状态 |
| --- | --- |
| `ClearFrost.csproj` | `<Version>6.0.0</Version>` |
| 根 README | 标题、badge、当前系列均称 `V6.0.0 正式版` |
| 发布器 | 默认读取项目版本，fallback 为 `6.0.0` |
| exe dry run | ProductVersion `6.0.0`，FileVersion `6.0.0.0` |
| 窗口标题 | `AppVersion.WindowTitle` 无条件追加“正式版” |
| 部署文档 | 明确写 `V6_test` 是研发试验分支，`main` 是 V5.9.x 稳定版 |
| 多任务文档 | 已命名为 V6.1，且代码已经包含对应实现 |
| 依赖打包脚本 | 文件名和说明仍写 `ClearFrostV5` |
| Git tag / GitHub Release | 没有 V6 tag；GitHub 最新 Release 仍为 V3.2 |
| README 时效 | 最后更新 2026-07-04，未覆盖 2026-08-01 合入的代码面 |

结论: 数字版本在 csproj、README 和发布器之间表面一致，但分支身份、功能身份和发布身份不一致。当前不能以“V6.0.0 正式版”对外承诺。

### 1.4 本机环境

- Windows x64，已安装 .NET SDK `9.0.304`。
- 已安装 .NET 8.0.19 与 .NET 9.0.8 runtime；本机没有 .NET 8 SDK，因此“精确使用 SDK 8 编译”在本机为 `NOT_VERIFIED`。
- 本机存在被 Git 忽略的 `MVSDK_Net.dll`、相机原生 SDK、`ClearFrost/ONNX/yolo26n.onnx` 和 5 个官方 YOLO11 ONNX 样例。
- 本机没有 `HaoCommunication.dll`。
- 本机有 NVIDIA T1000 8GB；可执行 DirectML 节点，但当前应用的 provider 证明逻辑错误地回退 CPU，详见风险 R3。

## 2. 证据等级

本文使用以下等级，避免把不同性质的证据混为一谈:

| 等级 | 含义 |
| --- | --- |
| E1 CODE | 当前基线中存在可达实现 |
| E2 AUTO | 当前自动测试实际执行并验证相关合同 |
| E3 REAL | 当前审计实际使用真实 ONNX 或真实硬件执行；必须说明输入、provider 和边界 |
| E4 PROD | 有可复现 CI/发布门禁、真实目标配置、升级/回滚和长稳证据，可对外承诺生产可用 |

E1/E2 不能自动升级为 E3；synthetic/unit smoke 不能称为真实 ONNX；真实通用 ONNX 的合成图片推理也不能称为产线准确率验证。

## 3. 已证实能力

### 3.1 构建、测试和基础发布

- 文本编码检查通过。
- 当前含本机私有依赖的工作区 Restore 通过。
- Debug x64 全解决方案 Build: `0 warning / 0 error`。
- 全量测试: `862 passed / 0 failed / 0 framework-skipped`，耗时约 17 秒。
- Release x64 全解决方案 Build: `0 warning / 0 error`。
- Release 输出依赖检查通过；带 `-RequireModel` 也通过，因为本机存在未追踪模型。
- Lite 和 Full dry-run 均能生成，版本字段一致；Lite 90 个文件约 190 MB，Full 549 个文件约 358 MB。
- Full dry-run 包使用隔离 `CLEARFROST_APPDATA_ROOT` 启动 12 秒未闪退，正常关闭且进程退出码为 0。

证据等级: 本机环境 E2；不满足 clean checkout，因此不是远端 E2，也不是 E4。

### 3.2 真实 ONNX CPU 通路

审计直接运行 `ClearFrost.YoloProbe`，使用本机被忽略的 Ultralytics 官方 YOLO11 ONNX，输入为工具生成的合成图，CPU provider 下结果如下:

| 任务 | 识别布局 | provider | 结果 |
| --- | --- | --- | --- |
| Detect | `RawYoloNoObjectness` | `CPUExecutionProvider` | 会话、预处理、推理、后处理通过；0 检出 |
| Segment | `SegmentRaw` | `CPUExecutionProvider` | 两输出张量通过；0 检出 |
| Pose | `PoseRaw` | `CPUExecutionProvider` | 关键点布局通过；0 检出 |
| OBB | `ObbRaw` | `CPUExecutionProvider` | 旋转框布局通过；0 检出 |
| Classification | `Classification` | `CPUExecutionProvider` | 通过；返回 1 个分类结果 |

证据等级: 通用模型兼容和真实推理为 E3；因为没有代表性产线图片、标签真值、阈值验收和目标节拍，准确率与产线可用性仍不是 E3/E4。

### 3.3 生产闭环纯软件合同

当前代码和自动测试对以下合同覆盖较强:

- 模型注册、manifest、hash、生产引用和激活事务恢复。
- Replay 数据集、人工复核、审批、evidence、gate、完整性扫描与 fail-closed。
- Recipe 版本、规则 JSON、模型/配方/规则/追溯字段保存。
- PLC handshake 序列、超时、ack、重置和断线状态机。
- 存储、SQLite、诊断包、审计 outbox 与多类路径逃逸/链接攻击防护。
- Classification/Segmentation/OBB/Pose 的后处理、摘要、规则和 UI/ResultJson 合同。

证据等级: E1/E2。大多数服务级测试使用 fake/mock/fixture，不能升级为真实设备 E3。

### 3.4 前端生成链

- `index.html` 实际只加载 `js/bundle.js` 作为业务脚本。
- `ClearFrost.csproj` 的 `BundleWebUiScripts` 明确按 11 个源文件的固定顺序生成 bundle。
- Build 后以内存重建相同内容，期望与实际均为 449812 bytes，SHA-256 均为 `D76AC2AA9978933FBDC669292118BC63F1BACD4686F7405AF490E1EB07823E40`。
- Lite/Full 包内 bundle 与源 bundle hash 完全一致。
- Node `v24.14.0` 对 `ClearFrost/html/js/*.js` 全部执行语法检查并通过。
- Full 包启动未崩溃。

证据等级: 生成与装载资源一致性为 E2；没有可见 WebView2 截图和真实交互验收，视觉/交互仍为 `NOT_VERIFIED`。

## 4. 未证实或部分完成能力

### 4.1 GitHub Actions 和 clean checkout

这是当前最大的系统性风险。

- `.github/workflows/ci.yml` 的 push 分支只有 `main`、`master`、`codex/**`，没有 `V6_test`。
- 该 workflow 只存在于 `V6_test`，GitHub 默认分支 `main` 中没有 workflow 文件。
- GitHub API 返回 Actions enabled，但 `total_count=0` workflows、0 runs、HEAD 0 checks、0 statuses。
- workflow 只构建 Debug，不构建 Release，不运行 publish dry run。
- workflow 的 release dependency check 检查 Debug 输出，不是发布包。
- Git 追踪树中没有任何 `.dll` 或 `.onnx`；workflow 也没有依赖注入或 fixture 生成步骤。

实际 clean-room 复现:

1. 从 `git archive HEAD` 解压，仅保留 Git 追踪文件。
2. 编码检查 PASS，Restore PASS。
3. Debug x64 Build 因缺少 `MVSDK_Net.dll` 得到 `1 warning / 45 errors`。
4. 仅向隔离快照注入本机 `MVSDK_Net.dll` 后 Build PASS。
5. 随后全量测试为 `852 passed / 10 failed / 0 skipped`；10 项都因输出目录没有未追踪 ONNX 而失败。

因此当前 CI 文件即使加入 `V6_test` trigger，也不会在普通 GitHub clean checkout 上变绿。

### 4.2 测试 skip 语义

- xUnit 报告 0 skipped。
- `OptionalOnnxSmokeStatusTests` 在无模型时只断言字符串是 `SKIPPED`，测试本身仍 PASS；有模型时也只检查文件存在，不执行推理。
- `MultitaskOnnxSmokeTests` 在 synthetic 模型缺失时读取 `ONNX_GENERATION_SKIPPED.txt` 后正常返回，仍计为 PASS。
- 多个符号链接安全测试在环境无法创建链接时直接 `return`，不进入框架 skip 统计。

结论: `862/862` 是当前本机的有效回归信号，但不能解释为“没有跳过项”，也不能作为 clean CI 或真实模型证明。

### 4.3 默认 PLC 发布合同

- `AppConfig`、`config.json` 和 7 个内置工位预设都默认使用 `HaoCommunication`。
- 当前 Lite/Full dry-run 包均不含 `HaoCommunication.dll`。
- 发布器仅 warning；`verify_release_dependencies.ps1` 不检查该 DLL，仍返回 PASS。
- `package_dependencies.ps1` 仍以 V5 命名，且没有打包 `依赖/HaoCommunication.dll`。
- 使用 `127.0.0.1` 的无外部副作用 PLC probe 在装载阶段确定失败: `未找到信息部特调版通讯库 HaoCommunication.dll`。
- 启动诊断没有在连接前验证所选 provider 的 DLL 是否存在。

结论: 当前发布器可以对一个默认 PLC 路径必然不可用的包给出成功结论。真实 PLC、trigger、ack、断线恢复全部为 `NOT_VERIFIED`。

### 4.4 DirectML provider 证明与回退

实际使用官方 Detect ONNX 和 `--gpu`:

- 机器存在 NVIDIA T1000。
- ONNX profiling 文件内出现 `DmlExecutionProvider` 节点，证明 DirectML 实际执行过。
- `SessionOptions.EndProfiling()` 实际返回并在工作目录生成 `onnxruntime_profile__*.json`。
- 当前代码只允许从 `%TEMP%/ClearFrostDmlProfiling` 读取 profile，于是把真实 DML 执行判为“文件缺失或路径不安全”，销毁 GPU session 后回退 CPU。
- YoloProbe 最终输出 `CPUExecutionProvider`，但仍以 exit code 0 结束，因为退出条件只看模型 descriptor 是否支持。
- 本轮生成的根目录 profile 已清理；测试输出目录中存在多次同类历史 profile，说明问题不是一次性现象。

结论: README 的“支持 DirectML GPU 加速”只有代码入口，当前运行合同未闭环，不能对外承诺。

### 4.5 相机、长稳、安装与回滚

- 相机测试覆盖像素格式、buffer 校验、生命周期和 fake camera；没有真实华睿/海康设备连接、取图、掉线恢复证据。
- `ClearFrost.SimStress` 明确使用 `FakeCameraService`、`FakeDetectionService`、`FakePlcService`。本轮 1000 周期、并发 4、0 注入失败的结果为 0 failure，但只证明模拟器和报告器可运行。
- 现场指南把该工具的 1 小时/8 小时运行列为上产线条件，不能等同于真实 AppRuntime、ONNX、相机、PLC、SQLite 和图像队列长稳。
- 仓库有目录发布脚本，但没有 installer、升级演练脚本、整包回滚脚本或版本间数据迁移验收。
- Lite `check_env.bat` 在本机提示未发现 WebView2 registry entry，但仍退出成功；Full 包实际能启动，说明该检查既不完整也不能作为 UI 可用证明。

以上均为 `NOT_VERIFIED`，不是失败通过。

## 5. 能力成熟度矩阵

| 能力 | E1 CODE | E2 AUTO | E3 REAL | E4 PROD |
| --- | --- | --- | --- | --- |
| Detect CPU 模型兼容 | 是 | 部分 | 是，官方模型+合成图 | 否，缺产线数据/节拍/门禁 |
| Classification | 是 | 是 | 是，官方模型+合成图 | 否 |
| Segment/OBB/Pose | 是 | 是 | 部分，真实模型执行但无正检出样本 | 否 |
| DirectML GPU | 是 | helper 测试 | 失败，实际 DML 被误判并回退 | 否 |
| 模型/配方/规则/追溯身份 | 是 | 是 | 无真实上线包/现场审计 | 否 |
| Replay 审批闭环 | 是 | 是 | 无真实生产模型审批演练 | 否 |
| PLC handshake/重连 | 是 | fake 状态机 | 否，且默认 driver 缺失 | 否 |
| 相机取图/重连 | 是 | fake/格式测试 | 否 | 否 |
| 长时间运行 | 模拟工具存在 | 短模拟 | 否 | 否 |
| Lite/Full 目录发布 | 是 | 脚本自检 | 本机 dry run 和启动 PASS | 否，依赖/升级/回滚未闭环 |
| 前端源/bundle 一致 | 是 | 是 | Full 包启动 | 否，缺可见交互验收 |

当前没有一个“相机 + 真实工位模型 + 判定规则 + PLC handshake + 追溯 + 断线恢复 + 长稳 + 可回滚发布”的完整配置达到 E4。

## 6. 风险排序

| 排名 | 风险 | 级别 | 判断依据 |
| --- | --- | --- | --- |
| R1 | 没有可执行的 clean-room CI/发布门禁 | P0 | 远端 0 workflow/run/check；纯 Git 快照 Build 45 errors；注入 SDK 后仍有 10 个模型依赖测试失败 |
| R2 | 发布包默认 PLC provider 缺失但脚本仍 PASS | P0 | 所有默认/预设选择 Hao；Lite/Full 无 DLL；本地 probe 在装载阶段失败 |
| R3 | DirectML 已执行却被误判为失败并回退 CPU | P0 | T1000 上 profile 含 DML，CLI 最终报告 CPU 且 exit 0；可能直接破坏节拍并制造假绿色 |
| R4 | 真实相机/PLC/触发/断线/长稳无证据 | P1 | 自动测试均为 fake/mock；SimStress 不运行真实应用链 |
| R5 | 版本和发布身份错误 | P1 | 研发分支与 V6.1 代码仍显示 V6.0 正式版；无 V6 tag/release/升级回滚 |
| R6 | 多任务生产语义未完成正样本验收 | P1 | 真实通用模型只在合成图执行；OBB/Pose 文档仍标预留 |
| R7 | WebView2 可见交互未验收 | P2 | bundle 一致且 app 可启动，但没有截图/交互/异常状态人工验收 |
| R8 | 测试依赖许可证告警 | P2 | Fluent Assertions 8.8 在测试输出中提示商业使用需要许可；需由项目所有者确认合规 |

R1 排第一，因为它使所有后续修复都无法在远端从干净输入被重复证明，并允许 R2/R3 继续以本机“全绿”形式存在。

## 7. 文档称已完成但证据不足的项目

| 文档/表述 | 当前证据结论 |
| --- | --- |
| README: `V6.0.0 正式版` | 无 V6 tag/release、无 CI、无 branch protection、无硬件/长稳/回滚证据，不能成立 |
| README: `支持 DirectML GPU 加速` | 实际 DML 节点执行后被路径校验误判，应用回退 CPU，当前只能称“有入口但运行合同失败” |
| README/发布器: 适合现场交付 | dry-run 包缺默认 Hao driver，发布器和依赖脚本仍 PASS，只能称“目录包可生成” |
| 现场指南: 1h/8h 压测作为上线判断 | 工具全链为 fake，不能证明真实应用长稳 |
| 多任务文档: synthetic smoke | 文档正确承认不是实模；但普通测试统计把逻辑 skip 计为 PASS，门禁表达仍不充分 |
| tracked YOLO acceptance reports | 模型本体被忽略，报告不与 CI、输入 hash、当前 SHA 强绑定；本轮已独立复跑，但仍不是产线数据验收 |

## 8. 路线选择

### 8.1 选择

采用“其他路线”: 暂停 V6.0 冻结发布，也暂停新增 V6.1 功能，先建立一个可从干净检出执行、结果 fail-closed、能区分 PASS/NOT_VERIFIED/BLOCKED 的 V6 发布候选门禁。

这不是同时修复所有产品缺陷。它先修复当前最大系统性风险: 项目没有可信的 go/no-go 机制。完成后，DirectML、Hao 依赖、真实硬件和长稳会以明确的阻塞状态出现，后续只能逐个关闭，不能再被本机私有文件或普通 PASS 测试掩盖。

### 8.2 被否决的路线

- 立即冻结并发布 V6.0: 否决。当前没有 clean CI，默认 PLC 包不可运行，DirectML 合同有确定缺陷，真实硬件和回滚未验证。
- 继续 V6.1 多任务: 否决。代码面已经超过验证能力，继续增加任务语义只会放大无门禁分支的风险。
- 只先修 DirectML: 暂不作为首要 Goal。它是下一批最紧急运行缺陷，但在 clean CI 建立前，修复本身仍无法被远端重复证明。
- 无差别合并 main: 否决。分支双向分叉且 main 有独有运行修复，只能后续逐提交审计。
- 大规模拆分 `AppRuntime`、重写 UI、加入传统视觉: 否决，均不降低当前最高风险。

## 9. 唯一首要 Goal

### V6-G1: 建立 clean-room、fail-closed 的 V6 发布候选门禁

#### 目标结果

让一个只拥有 Git 追踪内容的 Windows x64 环境能够执行明确分层的 V6 验证，并让 GitHub `V6_test` 的真实 run 对提交给出可信结论。任何缺失的私有 SDK、真实模型、默认 PLC driver、GPU provider、硬件或长稳证据必须成为机器可读的 `NOT_VERIFIED` 或 `BLOCKED`，不得被普通测试 PASS 或 publisher warning 掩盖。

完成该 Goal 不等于 V6 已可发布；它把“是否可发布”从文案判断变为可重复门禁，并为后续一次只关闭一个阻塞项建立基线。

#### 输入

- 唯一代码基线: 执行时重新 fetch 后的最新 `github/V6_test`。
- 当前 `.github/workflows/ci.yml`、测试项目、发布器、依赖检查器、YoloProbe/PLC probe。
- 私有 SDK 和真实 ONNX 只能通过明确、授权、可审计的外部输入进入相应 lane；不得提交到 Git。
- 本文列出的 R1-R8 是待表达的状态，不授权顺带开发对应功能。

#### 必须输出

1. 一个从 Git clean checkout 可运行的 hermetic lane，至少执行编码、Restore、Debug x64 Build、全量 hermetic tests、Release x64 Build、前端 bundle 一致性。
2. 一个在 GitHub 上真实覆盖 `V6_test` push/PR 的 workflow；必须有实际远端 run/check 证据后才算完成远端部分。
3. 明确的测试分层和状态模型。缺真实模型/硬件时不能以普通 PASS 伪装；synthetic fixture 必须标为 synthetic。
4. 一个 fail-closed 发布预检，按最终 config/预设/包内容校验所选择的 PLC provider、相机 SDK、Web UI、运行时和模型输入策略。当前默认 Hao 缺失时，正式发布资格必须失败，而不是 warning 后 PASS。
5. 一个机器可读的 release evidence manifest，至少绑定 commit SHA、branch、dirty 状态、版本、SDK、测试结果、bundle hash、依赖清单、模型 hash/provider 结果以及 `PASS/NOT_VERIFIED/BLOCKED` 状态。
6. 纠正 V6 的预发布身份。门禁未满足且没有 tag/release 时，README、应用标题或发布材料不得宣称“正式版”。
7. 对 DirectML 等硬件相关验证提供可观测的 required-provider 判定；请求 DML 后回退 CPU 必须可被门禁判为失败。该 Goal 不要求顺带修复 DirectML 实现本身。

#### 非范围

- 不修复 DirectML profile/provider 生产实现，除非只做门禁所需的最小可观测性改动。
- 不接入或改写相机、PLC 协议业务逻辑。
- 不把默认 Hao 静默切换为 Hsl/McpX；驱动选择变化必须有独立真实硬件等价性证据。
- 不新增 Classification/Segmentation/OBB/Pose 功能。
- 不实现 installer、升级和整包回滚功能；只把其缺失表达为 release blocker。
- 不重写 UI、不大拆 `AppRuntime`、不加入传统视觉。
- 不修改、合并、重写或删除 `main`。
- 不提交真实模型、私有 SDK、现场配置、现场 IP/序列号或生成的 release 二进制。

## 10. 分阶段执行步骤

### Phase 1: 冻结门禁合同

- 从最新 `github/V6_test` 重新记录 SHA、工作树和 main 分叉。
- 定义 hermetic、private-dependency、real-model、GPU、hardware 五类 lane 的输入与状态。
- 定义 release promotion 的必需项；未满足项必须阻断“正式版”，但可允许开发 CI 对预期的 NOT_VERIFIED 给出机器可读报告。

### Phase 2: 解除 clean checkout 隐式依赖

- 选择最小且不污染生产包的相机 SDK 编译策略，使 tracked-only checkout 可编译；不得把测试 shim 带入 Release。
- 让 hermetic tests 使用明确的微型 synthetic fixture 或纯接口 fake；不得读取开发机 `ClearFrost/ONNX`。
- 把真实 ONNX 测试移动到明确输入的 real-model lane，报告模型 hash、task、provider、输入类型和结果。

### Phase 3: 建立 CI

- 让 workflow 覆盖 `V6_test` push 和 PR。
- 执行编码、Restore、Debug Build、hermetic tests、Release Build、JS 语法和 bundle 确定性检查。
- 保证失败日志直接指出缺少的输入，不使用 `RestoreIgnoreFailedSources` 或逻辑 skip 抹平错误。
- 在有推送授权时验证一次 GitHub 实际 run；没有远端 run 不得宣称 CI 已覆盖。

### Phase 4: 建立发布预检与 evidence manifest

- 从实际发布配置和内置预设推导所需 driver/runtime，而不是维护一份脱节的硬编码“部分依赖”列表。
- 缺默认 provider 时阻断 promotion；若使用外部依赖，记录来源类型、文件名、版本/hash 和是否允许分发。
- 绑定 bundle hash、版本、SHA 和 dirty 状态。
- real-model/GPU/hardware/long-run 未提供时记录 NOT_VERIFIED，并将 production eligibility 置为 BLOCKED。

### Phase 5: 端到端验收与文档校正

- 在 tracked-only 快照中执行完整 hermetic lane。
- 在当前本机 private-dependency 环境执行发布负向/正向合同和隔离启动 smoke。
- 验证 requested DML 但 CPU fallback 会被门禁识别。
- 校正 README/应用显示和部署文档，使其只陈述证据支持的成熟度。

## 11. 验收矩阵

| ID | 验收项 | 必须结果 |
| --- | --- | --- |
| A1 | Git 范围 | 只改 `V6_test`；`main` SHA 不变；无无关用户改动被覆盖 |
| A2 | tracked-only 编码/Restore | PASS |
| A3 | tracked-only Debug x64 Build | PASS，0 error；不得依赖工作树中未追踪 DLL |
| A4 | tracked-only hermetic tests | PASS；无“模型缺失但测试 PASS”的逻辑；测试数减少必须逐项解释 |
| A5 | tracked-only Release x64 Build | PASS；测试 shim/compile stub 不得进入 Release 输出 |
| A6 | GitHub 覆盖 | `V6_test` 的真实 push/PR run 可见；HEAD 有 check；workflow 不再是 0 runs |
| A7 | 默认 PLC 依赖负向测试 | 缺 `HaoCommunication.dll` 时 promotion 非 0 退出并说明哪个 config/preset 要求它 |
| A8 | 私有依赖边界 | Git 中仍无私有 DLL/真实模型；外部输入有 hash/版本/授权说明 |
| A9 | 测试证据分级 | synthetic、real ONNX、GPU、hardware 状态在机器报告中不能混淆 |
| A10 | provider 约束 | `require DmlExecutionProvider` 时 CPU fallback 必须失败并包含 failure reason |
| A11 | 前端链 | 源文件重建 bundle 与实际加载/发布 bundle hash 一致；JS syntax PASS |
| A12 | 版本身份 | SHA、branch、dirty、产品版本一致；无绿色 promotion/tag 时不显示“正式版” |
| A13 | 发布/启动 | 有授权依赖时 Lite/Full contract PASS，隔离 AppData 启动并正常关闭；无授权依赖时明确 BLOCKED |
| A14 | 不可执行验收 | 真实相机/PLC/8h 未执行时必须是 NOT_VERIFIED，且 production eligibility 必须 BLOCKED |
| A15 | 回归 | 当前本机 862 项回归不得新增未解释失败；fail-closed、审批、追溯和安全断言不得放宽 |

## 12. 回滚与兼容要求

- 保持 `net8.0-windows10.0.17763.0`、Windows x64、现有配置 schema 和数据库 schema。
- 生产 Release 必须继续加载真实相机 SDK；任何 CI stub 必须在 MSBuild 和产物检查双重保证下无法进入生产包。
- 不改变 PLC provider、协议、地址、ack 时序或 fallback 语义。
- 不为门禁变绿降低审批、hash、路径安全、fail-closed、追溯或审计要求。
- workflow、验证脚本和身份文案应能作为一个独立 Goal 提交整体回滚；回滚不得删除审计证据或改写 main。
- 发布预检失败不得留下被命名为正式版的半成品目录或 evidence manifest PASS。

## 13. 后续 Goal 候选

以下仅是候选，不在 V6-G1 中提前实施:

1. 修复 DirectML profiling/provider 证明和 profile 生命周期，并在 T1000 + 真实 ONNX 上证明 DML active、CPU fallback 和无残留。
2. 闭合默认 HaoCommunication 发布输入，或在真实 PLC 上证明并审批替代 provider；随后验证 trigger/ack/断线恢复。
3. 使用真实华睿/海康相机、真实工位模型和目标 PLC 完成端到端 FAT/SAT。
4. 把 fake SimStress 替换或补充为真实 AppRuntime 1h/8h soak，验证内存、队列、存储和断线恢复。
5. 完成 installer、升级、配置迁移和整包回滚演练。
6. 在前述生产基础稳定后，再关闭 V6.1 多任务的正样本、规则/UI 和现场验收矩阵。

## 14. 本轮命令结果摘要

| 命令/检查 | 结果 |
| --- | --- |
| `git fetch github V6_test main --tags` | PASS；HEAD 与远端一致 |
| `git rev-list --left-right --count github/main...github/V6_test` | `7 37` |
| `tools/verify_text_encoding.ps1` | PASS |
| `dotnet restore ClearFrost.sln` | PASS |
| `dotnet build ClearFrost.sln -c Debug -p:Platform=x64` | PASS，0 warning/0 error |
| `dotnet test ... -c Debug -p:Platform=x64 --no-build` | PASS，862/862，框架 skip 0 |
| `dotnet build ClearFrost.sln -c Release -p:Platform=x64` | PASS，0 warning/0 error |
| `verify_release_dependencies.ps1` 对 Release 输出 | PASS；检查器未覆盖 Hao |
| `publish.ps1 -Mode Both -Version 6.0.0` | PASS，但 Lite/Full 均 warning 缺 Hao |
| 对 dry-run Lite/Full 再做依赖检查 | PASS，仍未发现默认 Hao 缺失 |
| Full 包隔离启动 12 秒 | PASS，正常关闭，exit 0 |
| Lite `check_env.bat` | PASS，warning 未发现 WebView2 registry entry |
| 5 类官方 YOLO11 CPU probe | PASS；真实 ONNX + 合成图 |
| Detect `--gpu` probe | PARTIAL/BLOCKED；profile 含 DML，最终 CPU fallback，CLI exit 0 |
| 1000 cycle SimStress | PASS 作为 fake 工具；非真实长稳证据 |
| JS `node --check` | PASS |
| bundle 重建/hash/发布包比对 | PASS，完全一致 |
| GitHub workflows/runs/checks API | BLOCKED；均为 0 |
| tracked-only clean Build | BLOCKED；缺 MVSDK，45 errors |
| clean 快照注入 MVSDK 后 tests | BLOCKED；852 pass/10 fail，缺 ONNX |
| 真实相机/PLC/断线恢复 | NOT_VERIFIED；本轮未连接或写入现场硬件 |
| installer/升级/整包回滚 | NOT_VERIFIED；仓库无对应实现/演练 |

## 15. 下一次 Codex 精简 Goal Prompt

```text
你在 ClearFrost 仓库中执行唯一 Goal: 建立 clean-room、fail-closed 的 V6 发布候选门禁。

先 fetch，并以最新 github/V6_test 为唯一代码基线；记录 SHA、工作树和与 main 的分叉。禁止修改、合并、重写或删除 main，禁止覆盖用户改动。

已知基线证据: GitHub 当前没有 workflow/run/check；tracked-only 快照因缺 MVSDK_Net.dll 产生 45 个 Build error；仅注入该 DLL 后仍有 10 个测试因未追踪 ONNX 失败；发布包默认/全部预设选择 HaoCommunication，但包内缺 HaoCommunication.dll 且 publisher/依赖检查仍 PASS；请求 DirectML 时 profile 有 DML 节点，当前 probe 最终回退 CPU 却 exit 0。

产品边界: 本 Goal 只建立可信门禁和证据表达，不开发新视觉功能，不重写 UI，不大拆 AppRuntime，不改变 PLC 协议/地址/默认 provider，不加入传统视觉。不得提交私有 SDK、真实模型、现场配置或生成二进制。synthetic 绝不能描述成 real ONNX。

必须交付:
- tracked-only Windows x64 环境可完成编码、Restore、Debug Build、hermetic tests、Release Build、JS/bundle 确定性验证；测试 stub 不得进入 Release。
- GitHub workflow 实际覆盖 V6_test push/PR；没有真实远端 run/check 不得声称已覆盖。
- tests 分清 hermetic/synthetic、real-model、GPU、hardware；缺输入必须是机器可读 NOT_VERIFIED/BLOCKED，不能普通 PASS。
- 发布预检从实际 config/预设推导依赖；当前缺 Hao 时必须阻断正式 promotion，不得 warning 后 PASS，也不得静默切换 provider。
- 生成绑定 SHA/branch/dirty/version/test/bundle/dependency/model/provider 状态的 release evidence manifest。
- requested DML 却 CPU fallback 必须能被 required-provider 门禁判失败；本 Goal 不顺带修复 DirectML 核心实现，除非是最小可观测性改动。
- 门禁未绿且无 tag/release 时，README/应用/发布材料不得称“正式版”。

按 docs/V6_NEXT_PHASE_AUDIT_AND_GOAL.md 的 A1-A15 验收。运行全部本地可执行验证；真实硬件、外部依赖或远端 push 无授权时明确 BLOCKED，不伪装通过。最终只提交该 Goal 范围内的代码、测试、workflow 和文档，不推送，除非环境有明确自动推送授权。
```
