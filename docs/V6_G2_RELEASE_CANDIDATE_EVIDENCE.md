# V6-G2 Release Candidate Evidence

本文件只记录可复核的 V6-G2 实验室证据，不把缺少现场输入的结果包装成生产 PASS。所有证据均由当前 `V6_test` 工作区生成，模型、验证图、SDK DLL、许可证和现场配置不进入 Git。

## 结论

当前结论为 `PARTIAL`。V6-G2 的代码、输入边界、fail-closed 发布流程、隔离迁移/回滚实验和生产图 soak host 已建立并通过可执行的本地验证；本机没有授权的真实 ONNX 模型、验证图、DirectML 设备输入或现场相机/PLC，因此以下项目保持 `NOT_VERIFIED`：

- Detect 真模型 CPU/DML 矩阵和 100 warm-up / 1000 inference 证据。
- Lite/Full 正向发布包、包内模型/DLL hash 和隔离启动。
- 1 小时和 8 小时真实模型 soak。
- 真实相机、真实 PLC、FAT/SAT。

在这些输入补齐并重新生成机器 Evidence 之前，不建议进入 FAT/SAT，也不创建 tag 或 GitHub Release。

## 基线

任务开始时的代码基线为 `1a7d2cf2166205e1d99cddfdfc75f20fc291444d`，分支为 `V6_test`，并与 `github/V6_test` 一致。fetch 后的 `github/main` 为 `4d843a9f490e7680f2ac72f31993dcaac3b63bc6`；本轮不修改 `main`。

G1-R2 远端门禁已闭合：最新 V6 Actions run 为 [30708487100](https://github.com/HerverJun/ClearFrost/actions/runs/30708487100)，head SHA 为基线 SHA，状态为 `completed / success`。该工作流的最终 enforcement 同时检查 gate outcome、Evidence schema 和 Development validation；本轮没有改写该结果。

## 外部输入合同

入口为 `tools/verify_v6_external_inputs.ps1`，支持 `-ManifestPath` 或 `CLEARFROST_V6_INPUT_MANIFEST`。每个模型和依赖必须提供文件名、路径、来源、SHA-256 和字节数；路径必须不是 Git tracked 文件或 reparse point。模型 lane 固定为 Detect、Classification、Segmentation、OBB、Pose，Detect 还必须提供可重复验证图。

本机验证结果：

- `artifacts/v6-g2/models/external-inputs.json`: `NOT_VERIFIED`。
- `artifacts/v6-g2/models/model-matrix.json`: `NOT_VERIFIED`。
- `artifacts/v6-g2/publish/external-inputs.json`: `NOT_VERIFIED`。

缺少输入时不会扫描整个磁盘，也不会用 synthetic ONNX 或 synthetic 图像代替。

## Model / Provider

`tools/run_v6_model_matrix.ps1` 调用真实 `ClearFrost.YoloProbe` 和生产 YOLO/ONNX 路径。CPU lane 要求真实 `CPUExecutionProvider`、有效结果结构、无 NaN/越界结果；严格 DML lane 要求实际 `DmlExecutionProvider`、`GpuActive=true`、清理后的 profile，以及至少 100 次 warm-up 和 1000 次推理。请求 DML 但实际落到 CPU 会是 `BLOCKED`，不会被日志文字猜测为 PASS。

本轮没有真实模型，所以 CPU、DML、负向模型合同和内存/延迟矩阵均为 `NOT_VERIFIED`。DirectML profile 根目录已支持通过 `CLEARFROST_DML_PROFILE_ROOT` 隔离，并与本次 Session 绑定；当前没有可报告的实际 Provider。

## 发布与隔离

`tools/publish_v6_release_lab.ps1` 只在 Detect 模型和白名单 DLL 全部 `PASS` 时执行 Lite/Full 正向 publish。它验证：

- Lite / Full 的 runtime 差异、Web UI bundle hash、包内文件清单和模型/DLL hash。
- 包内没有测试项目、MockCamera、SimStress、Stub 或源树绝对路径。
- 包 manifest 的版本、commit SHA、运行时标识和外部输入身份。
- zip round-trip（启用 `-CreateZip` 时）。

本轮 Lite 和 Full 均为 `NOT_VERIFIED`，其 `exitCode` 保持空值；没有生成正向发布包。

`tools/run_v6_release_isolation.ps1` 在正向包可用时使用临时包目录、全新的 AppData 和 profile 根目录，重复三次检查启动日志、正常退出码、配置/数据库写入根、文件锁和残留进程。没有正向包时启动项为 `NOT_VERIFIED`。迁移实验独立执行并为 `PASS`：合法 V5.9 配置、缺字段、历史路径、损坏配置、模型引用、中途失败恢复、snapshot rollback 和二次幂等启动均有机器报告。

## 生产图 soak

`tools/ClearFrost.V6SoakHost` 复用 `AppRuntime`、`DetectionService`、`InspectionPipelineService`、真实 SQLite、图像保存队列和检测记录队列。相机和 PLC 仅是硬件边界适配器；它们不能被解释成真实相机或 FAT/SAT。故障计划绑定 seed，并记录注入时间、错误码、终态、恢复状态和最终一致性扫描。

本轮生成：

- `artifacts/v6-g2/soak/soak-evidence.json`: 缺少外部输入，`NOT_VERIFIED`。
- `artifacts/v6-g2/evidence-schema.json`: schema 校验 `PASS`，聚合状态 `NOT_VERIFIED`。

Soak host 已覆盖并可在真实输入到位后执行：正常 OK/NG、无目标/多目标、尺寸错误、短帧、相机取图失败、PLC 断开/恢复、PLC 写失败、握手 ACK 超时、SQLite 短锁、图像目标不可写、图像/记录队列压力、模型暂不可用、单次推理异常、取消和正常/取消关闭。明确终态失败属于允许的 fault terminal outcome，但仍必须进入记录、追踪和一致性扫描。

## 证据入口

| 证据 | 入口 | 当前状态 |
| --- | --- | --- |
| G1 gate | `artifacts/v6-gate/evidence.json` | 远端 run success |
| 外部输入 | `artifacts/v6-g2/models/external-inputs.json` | `NOT_VERIFIED` |
| 模型矩阵 | `artifacts/v6-g2/models/model-matrix.json` | `NOT_VERIFIED` |
| 发布实验室 | `artifacts/v6-g2/publish/release-lab-evidence.json` | `NOT_VERIFIED` |
| 隔离与迁移 | `artifacts/v6-g2/publish/isolation-evidence.json` | migration `PASS`, startup `NOT_VERIFIED` |
| soak | `artifacts/v6-g2/soak/soak-evidence.json` | `NOT_VERIFIED` |
| 统一 schema | `artifacts/v6-g2/evidence-schema.json` | schema `PASS`, aggregate `NOT_VERIFIED` |

统一检查入口为 `tools/validate_v6_g2_evidence.ps1`。它只接受 `PASS`、`NOT_VERIFIED`、`BLOCKED` 三种状态，并检查 schema、SHA/字节数、CPU/DML Provider、发布 exit code、隔离回滚、队列排空、最终一致性和禁止 release mutation 等约束。

## 进入下一阶段的条件

1. 提供授权的真实 Detect ONNX、可重复验证图以及所需 DLL 的外部 manifest，并重新通过输入 hash/字节数检查。
2. 在支持 DirectML 的目标机执行 CPU 基线、严格 DML lane、Session 生命周期和负向合同；不支持 DML 时保留 `BLOCKED`，不降级冒充通过。
3. 生成并验证 Lite/Full 包，完成三次隔离启动、迁移和回滚证据。
4. 先完成真实模型 1 小时 soak，门禁通过后再运行 8 小时 soak，核对资源趋势、InspectionId、图像/记录/trace 一致性和全部故障恢复。
5. 最后由真实相机、PLC 和 FAT/SAT 环境完成联合验收；在此之前这些项必须继续显示 `NOT_VERIFIED`。
