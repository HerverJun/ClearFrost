# V6.1 深度学习多任务

ClearFrost 是深度学习专用的轻量部署与现场运行软件，重点是 ONNX/YOLO 模型导入、推理、调试、OK/NG 输出和轻量追溯。ClearVision 才是传统视觉工具箱和复杂流程编排平台，Blob、圆检测、模板匹配、边缘、标定、几何测量和流程画布不应塞进 ClearFrost。

## 本阶段支持范围

- Detect：保留现有目标检测、ROI、数量/顺序/位置规则和现场生产流程。
- Classification：新增 Top1、TopK、分类规则和调试/追溯摘要。
- Segmentation：新增 mask 是否存在、mask 面积、局部覆盖率、分割实例列表和分割规则。
- OBB：保留并展示旋转框 angle，预留角度范围规则。
- Pose：保留并展示关键点数量、最高/最低关键点置信度、低置信度关键点数量，预留关键点规则。

## 模型任务识别

模型导入仍由 `YoloExportProbe` / `YoloModelDescriptor` 识别 ONNX metadata、输入尺寸、labels、输出布局、NMS 状态和执行任务模式。`DeepLearningModelTaskSummary` 将这些字段整理为工程师可读摘要：

- 任务类型：目标检测、图像分类、分割检测、姿态/关键点、旋转框检测、自动识别。
- 输入尺寸、labels 数量、输出布局、预处理模式。
- 是否内置 NMS、是否端到端 NMS-free、是否需要应用层 NMS。
- 是否支持 mask、Pose、OBB。
- 不支持布局时返回中文提示：当前模型输出格式暂不支持，请检查 ONNX 导出任务类型、输出张量和 labels metadata。

## Classification 调试怎么看

视觉调试工作台选择“分类判定”模板后，会生成 `Classification` 规则。运行后查看“深度学习任务摘要”：

- 分类 Top1 类别和置信度。
- TopK 列表，默认最多 5 项。
- labels 缺失时显示 `Class_<id>`。
- 判定原因会说明分类匹配、分类不匹配或分类置信度不足。

分类结果不会再被 ROI 当成坐标为 0 的检测框过滤掉。

## Segmentation 调试怎么看

视觉调试工作台选择“分割面积判定”模板后，会生成 `SegmentationArea` 规则。运行后查看：

- 分割实例数量。
- 每个实例的类别、置信度、外接框、mask 是否存在。
- mask 面积和覆盖率。
- 面积/覆盖率/数量/类别过滤的 OK/NG 原因。

当前 `MaskCoverage` 采用 `MaskData.Rows * MaskData.Cols` 作为分母，即局部 mask 像素覆盖率。它不伪装成整张原图面积比例；如果后续需要整图面积比例，应在真实 mask 映射稳定后扩展。

## OBB / Pose 调试怎么看

OBB 结果摘要显示旋转框数量、类别、置信度、中心、宽高和 angle。Pose 结果摘要显示目标数量、关键点数量、最高/最低关键点置信度和低置信度关键点数量。

OBB/Pose 本阶段以数据贯通和工程师可见为主，生产规则只做轻量预留，不作为完整上线验收规则。

## 追溯

检测记录不新增数据库列。分类/分割/OBB/Pose 摘要写入现有 `ResultJson` 扩展字段：

- `Results`：保留每个结果的类别、置信度、框、DataKind、Angle、HasMask、KeyPointCount。
- `DeepLearningSummary`：包含 Classification、Segmentation、OBB、Pose 摘要。

追溯列表查询会继续复用 `DetectionRecords.ResultJson`，不新增数据库列。当前前端在历史追溯卡片和详情栏中读取 `DeepLearningSummary`：

- Classification：显示 Top1 与置信度。
- Segmentation：显示实例数、mask 面积和覆盖率。
- OBB：显示首个旋转框 angle。
- Pose：显示目标数和关键点总数。
- 旧记录没有 `DeepLearningSummary` 时保持空摘要，不阻断历史图查看。

## 第二阶段 Smoke 矩阵

本阶段新增 `tools/create_multitask_smoke_models.py` 作为本地最小 ONNX 生成器。脚本不联网下载模型、不联网安装依赖；本机 Python 缺少 `onnx` 包时，会在 `ClearFrost.Tests/TestAssets/Models/ONNX_GENERATION_SKIPPED.txt` 写入 `ONNX_GENERATION_SKIPPED` 并正常退出。

| 任务 | Real ONNX probe | Real ONNX inference | Synthetic/unit smoke | UI/ResultJson |
| --- | --- | --- | --- | --- |
| Detect | 使用仓库现有 Detect 流程和既有模型兼容测试；未新增大模型 | 依赖现场/本地模型，不伪装完成 | 保留 detect postprocessor、NMS、ROI 规则回归 | 检测框渲染保持兼容 |
| Classification | 当前环境跳过，原因：本地缺少 `onnx` 包 | 当前环境跳过 | 已覆盖 descriptor、postprocessor、TopK、规则、ROI 保留、日志摘要 | Top1/置信度写入 ResultJson 并在追溯显示 |
| Segmentation | 当前环境跳过，脚本可生成 output0 + prototype output1 | 当前环境跳过 | 已覆盖 descriptor、mask 面积/覆盖率、ROI 坐标过滤、日志摘要 | ResultJson/追溯显示实例数、面积、覆盖率 |
| OBB | 当前环境跳过 | 当前环境跳过 | 已覆盖 descriptor、angle 后处理和摘要、ROI 坐标过滤 | ResultJson/追溯显示 angle |
| Pose | 当前环境跳过 | 当前环境跳过 | 已覆盖 descriptor、keypoint 后处理和摘要、ROI 坐标过滤 | ResultJson/追溯显示 keypoint count |

如果后续环境安装了 `onnx` 包，可运行：

```bash
python tools/create_multitask_smoke_models.py
```

生成的单个 smoke ONNX 模型目标大小小于 1MB；若生成失败，测试会检查 skip 标记，不会把 synthetic 覆盖冒充成真实 ONNX 端到端 smoke。

## 任务感知渲染与日志

- Classification：渲染层不画无意义检测框，日志使用“分类结果：Top1=...，Confidence=...，判定=...”。
- Segmentation：当 `MaskData` 存在时，renderer 支持半透明 mask overlay；日志和追溯显示面积/覆盖率。无 mask 时仍显示分割摘要，不虚构 overlay。
- OBB：renderer 保留旋转框绘制，日志和追溯保留 angle。
- Pose：renderer 保留关键点绘制，日志和追溯保留关键点数量与低置信度统计。
- Detect：原有检测框渲染、NMS 和规则路径保持兼容。

## ROI 判定入口

生产管线、设置页测试推理、历史图复判和多模型候选选择均通过 `InspectionDecisionEvaluator` 完成 ROI 过滤与规则判定。旧的 `CreateRuleCandidateEvaluator()` / `FilterResultsByROI()` 私有分支已移除，避免后续误用。

统一行为：

- Classification 不做 ROI 过滤。
- Detect / Segmentation / OBB / Pose 等坐标结果按中心点进入 ROI。
- ROI 无效时 fail-closed，判定 NG，并保留错误原因。

## 规则模板与配置迁移准备

以下深度学习模板已纳入 JSON roundtrip 测试：

- `classification_judge`：适合图像分类 OK/NG。
- `segmentation_area`：适合分割实例面积和覆盖率。
- `obb_angle`：当前为轻量预览，后续完善完整生产规则。
- `pose_keypoints`：当前为轻量预览，后续完善完整生产规则。

## 低配 IPC 建议

- 优先使用小输入尺寸模型。
- 简单 OK/NG 分类优先使用 Classification。
- Detect 和 Segment 按工位复杂度谨慎选择，Segment 对 CPU/GPU 和内存更敏感。
- DirectML 初始化失败时可回退 CPU，但应在现场诊断中关注推理耗时。
- 不要为了补齐功能把传统视觉算法迁入 ClearFrost。

## 留到下一阶段

- 在具备 `onnx` 包或现场模型的环境中运行真实 Classification/Segmentation/OBB/Pose ONNX probe 与 inference smoke。
- 分割半透明 mask overlay 的现场效果打磨。
- 分类/分割规则在所有生产工位上的配置迁移策略。
- OBB/Pose 完整生产规则和 UI 参数编辑。
- WebView2 真实截图验收。
