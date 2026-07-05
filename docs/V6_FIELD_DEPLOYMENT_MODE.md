# V6 现场轻量落地模式

V6_test 默认面向普通产线采用现场轻量模式。班组长和一线人员只需要关注能否生产、设备状态和待处理问题，不需要日常操作模型审批、回放验证或验证记录。

## 模式说明

### 现场轻量模式

- 默认 `RequireApprovedModelsForProduction=false`。
- 普通 ONNX 和旧式模型引用可以进入生产准备流程。
- 不强制检查 Replay/Evidence/模型审批证据。
- 仍会检查模型文件存在性、运行时加载、Recipe 和 AppConfig 一致性、存储路径、PLC/相机等基础条件。
- 诊断摘要会记录：`当前为现场轻量模式，未强制模型审批证据。`

### 工程师高级维护

工程师可在“工程师详情（高级）”和“视觉调试（工程师）”中处理：

- 模型调试和规则验证
- 历史样本复判
- 诊断包和交接报告
- 模型回放验证、模型对比和验证记录

### 严格模型准入

严格模式适合高风险工位、质量追溯要求高、模型频繁迭代的场景。开启后：

- `RequireApprovedModelsForProduction=true`
- 未批准模型、非包模型、Gate 缺失、验证记录失效都会阻断生产模型上线。
- 一线主提示统一为：`当前模型未完成上线验证，请联系工程师完成模型验证，或切换回已验证模型。`
- 内部错误码仍保留在工程师详情、日志和诊断包中。

## 开启或关闭严格准入

运行配置字段：

```json
{
  "RequireApprovedModelsForProduction": false,
  "StrictModelPackageMode": false
}
```

- 关闭严格准入：将 `RequireApprovedModelsForProduction` 设为 `false`。
- 开启严格准入：将 `RequireApprovedModelsForProduction` 设为 `true`。
- `StrictModelPackageMode` 继续默认 `false`，除非工程师明确需要强制模型包结构校验。
- 从 V5.9.x/main 风格配置迁移到 V6_test 时，旧配置缺少该字段不会自动开启审批。
- 已有 V6 配置显式写入 `RequireApprovedModelsForProduction=true` 时会保留用户选择。

## 诊断中心怎么看

1. 先看“当前是否可以生产”。
2. 再看“待处理问题”，每条问题都有下一步建议。
3. 工程师需要排查时，再展开“工程师详情（高级）”查看 P95/P99、队列、审计链、Replay Gate、启动诊断原始项和内部错误码。

## 视觉调试工作台怎么用

1. 选择场景：螺钉数量检测、遥控器漏装检测、线序顺序检测、相对位置检测或自定义规则。
2. 获取图片：获取当前相机单帧，或从历史记录选择样本。
3. 运行验证：运行当前规则，或批量验证历史样本。

规则 JSON 默认隐藏在“高级：查看/编辑规则 JSON”中。保存前会校验 JSON 格式，无效 JSON 不允许保存。

## 分支说明

- `main` 仍是 V5.9.x 产线稳定版。
- `V6_test` 是研发试验分支。
- 本模式只在 `V6_test` 工作，禁止合并、重写或删除 `main`。
