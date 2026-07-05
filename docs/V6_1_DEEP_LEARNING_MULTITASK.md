# V6.1 深度学习多任务第一阶段

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

## 低配 IPC 建议

- 优先使用小输入尺寸模型。
- 简单 OK/NG 分类优先使用 Classification。
- Detect 和 Segment 按工位复杂度谨慎选择，Segment 对 CPU/GPU 和内存更敏感。
- DirectML 初始化失败时可回退 CPU，但应在现场诊断中关注推理耗时。
- 不要为了补齐功能把传统视觉算法迁入 ClearFrost。

## 留到下一阶段

- 使用真实 Classification/Segmentation/OBB/Pose ONNX 模型做 smoke 验证。
- 分割半透明 mask overlay 的进一步打磨。
- 分类/分割规则在所有生产工位上的配置迁移策略。
- OBB/Pose 完整生产规则和 UI 参数编辑。
- WebView2 真实截图验收。
