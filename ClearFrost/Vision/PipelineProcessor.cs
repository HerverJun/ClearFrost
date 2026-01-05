// ============================================================================
// 文件名: PipelineProcessor.cs
// 描述:   传统视觉处理的核心流水线处理器
//         
// 功能概述:
//   - 动态管理图像处理算子链 (添加、删除、排序)
//   - 按顺序执行所有算子，每个算子输出作为下一个的输入
//   - 支持多种匹配算子: 模板匹配、AKAZE特征匹配、ORB特征匹配、金字塔形状匹配
//   - 提供配置导入/导出功能，支持流水线持久化
//
// 使用示例:
//   var processor = new PipelineProcessor();
//   processor.AddOperator(new GrayscaleOp());
//   processor.AddOperator(new TemplateMatchOp());
//   var result = await processor.ProcessAsync(inputMat);
//
// 作者: ClearFrost Team
// 创建日期: 2024
// ============================================================================
using OpenCvSharp;
using System.Diagnostics;

namespace YOLO.Vision
{
    /// <summary>
    /// 流程处理器
    /// 允许动态添加图像处理步骤，按顺序执行算子链
    /// </summary>
    public class PipelineProcessor : IVisionProcessor, IDisposable
    {
        private readonly List<OperatorInstance> _operators = new();
        private readonly object _lock = new();
        private bool _disposed = false;

        public string Name => "流程处理器";

        /// <summary>
        /// 当前算子列表
        /// </summary>
        public IReadOnlyList<OperatorInstance> Operators => _operators.AsReadOnly();

        /// <summary>
        /// 用于判定通过/失败的回调函数
        /// </summary>
        public Func<List<OperatorInstance>, bool>? PassCondition { get; set; }

        /// <summary>
        /// 获取最后一个算子的输出
        /// </summary>
        public Mat? GetLastOutput()
        {
            lock (_lock)
            {
                var last = _operators.OrderBy(o => o.Order).LastOrDefault();
                return last?.LastOutput;
            }
        }

        /// <summary>
        /// 添加算子
        /// </summary>
        /// <param name="op">算子实例</param>
        /// <param name="instanceId">实例ID（可选）</param>
        /// <returns>实例ID</returns>
        public string AddOperator(IImageOperator op, string? instanceId = null)
        {
            lock (_lock)
            {
                var instance = new OperatorInstance
                {
                    InstanceId = instanceId ?? Guid.NewGuid().ToString("N"),
                    Operator = op,
                    Order = _operators.Count
                };
                _operators.Add(instance);
                return instance.InstanceId;
            }
        }

        /// <summary>
        /// 在指定位置插入算子
        /// </summary>
        public string InsertOperator(int index, IImageOperator op, string? instanceId = null)
        {
            lock (_lock)
            {
                var instance = new OperatorInstance
                {
                    InstanceId = instanceId ?? Guid.NewGuid().ToString("N"),
                    Operator = op,
                    Order = index
                };
                _operators.Insert(index, instance);
                ReorderOperators();
                return instance.InstanceId;
            }
        }

        /// <summary>
        /// 移除算子
        /// </summary>
        /// <param name="instanceId">实例ID</param>
        /// <returns>是否移除成功</returns>
        public bool RemoveOperator(string instanceId)
        {
            lock (_lock)
            {
                var op = _operators.FirstOrDefault(o => o.InstanceId == instanceId);
                if (op == null) return false;
                _operators.Remove(op);
                ReorderOperators();
                return true;
            }
        }

        /// <summary>
        /// 移动算子位置
        /// </summary>
        public bool MoveOperator(string instanceId, int newIndex)
        {
            lock (_lock)
            {
                var op = _operators.FirstOrDefault(o => o.InstanceId == instanceId);
                if (op == null) return false;

                _operators.Remove(op);
                newIndex = Math.Clamp(newIndex, 0, _operators.Count);
                _operators.Insert(newIndex, op);
                ReorderOperators();
                return true;
            }
        }

        /// <summary>
        /// 清空所有算子
        /// </summary>
        public void ClearOperators()
        {
            lock (_lock)
            {
                _operators.Clear();
            }
        }

        /// <summary>
        /// 更新算子参数
        /// </summary>
        public bool UpdateOperatorParameter(string instanceId, string paramName, object value)
        {
            lock (_lock)
            {
                var op = _operators.FirstOrDefault(o => o.InstanceId == instanceId);
                if (op == null) return false;
                op.Operator.SetParameter(paramName, value);
                return true;
            }
        }

        /// <summary>
        /// 获取算子
        /// </summary>
        public OperatorInstance? GetOperator(string instanceId)
        {
            lock (_lock)
            {
                return _operators.FirstOrDefault(o => o.InstanceId == instanceId);
            }
        }

        /// <summary>
        /// 重新排序
        /// </summary>
        private void ReorderOperators()
        {
            for (int i = 0; i < _operators.Count; i++)
            {
                _operators[i].Order = i;
            }
        }

        public void Initialize()
        {
            // 初始化逻辑（如有需要）
        }

        /// <summary>
        /// 处理图像并返回结果
        /// </summary>
        public async Task<VisionResult> ProcessAsync(Mat input)
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var result = new VisionResult();

                // 定义在外部以便 finally 块访问
                Mat? current = null;
                Mat? previous = null;

                try
                {
                    current = input.Clone();

                    lock (_lock)
                    {
                        foreach (var opInstance in _operators.OrderBy(o => o.Order))
                        {
                            // 释放上一个步骤的输出（即当前步骤的输入）
                            previous?.Dispose();
                            previous = current;

                            // 执行当前步骤
                            current = opInstance.Operator.Execute(previous);

                            // 更新 LastOutput (必须 Clone，因为 current 可能会在后续被 Dispose)
                            opInstance.LastOutput?.Dispose();
                            opInstance.LastOutput = current.Clone();

                            // 检查模板匹配结果
                            if (opInstance.Operator is TemplateMatchOp tmOp && tmOp.LastMatchResult != null)
                            {
                                result.Objects.Add(new DetectedObject
                                {
                                    Label = "Template",
                                    Confidence = tmOp.LastMatchResult.Score,
                                    BoundingBox = tmOp.LastMatchResult.BoundingBox
                                });
                                result.Score = tmOp.LastMatchResult.Score;
                                result.IsPass = tmOp.LastMatchResult.IsMatch;
                            }
                            // 检查特征匹配结果
                            else if (opInstance.Operator is FeatureMatchOp fmOp && fmOp.LastMatchResult != null)
                            {
                                result.Objects.Add(new DetectedObject
                                {
                                    Label = "Feature",
                                    Confidence = fmOp.LastMatchResult.Score,
                                    BoundingBox = fmOp.LastMatchResult.BoundingBox
                                });
                                result.Score = fmOp.LastMatchResult.Score;
                                result.IsPass = fmOp.LastMatchResult.IsMatch;
                            }
                            // 检查 ORB 匹配结果
                            else if (opInstance.Operator is OrbMatchOp orbOp && orbOp.LastMatchResult != null)
                            {
                                result.Objects.Add(new DetectedObject
                                {
                                    Label = "ORB",
                                    Confidence = orbOp.LastMatchResult.Score,
                                    BoundingBox = orbOp.LastMatchResult.BoundingBox
                                });
                                result.Score = orbOp.LastMatchResult.Score;
                                result.IsPass = orbOp.LastMatchResult.IsMatch;
                            }
                            // 检查金字塔形状匹配结果
                            else if (opInstance.Operator is PyramidShapeMatchOp pyrOp && pyrOp.LastMatchResult != null)
                            {
                                result.Objects.Add(new DetectedObject
                                {
                                    Label = "Shape",
                                    Confidence = pyrOp.LastMatchResult.Score / 100.0,
                                    BoundingBox = pyrOp.LastMatchResult.BoundingBox
                                });
                                result.Score = pyrOp.LastMatchResult.Score;
                                result.IsPass = pyrOp.LastMatchResult.IsMatch;
                            }
                            // 检查背景差分结果
                            else if (opInstance.Operator is BackgroundDiffOp bgOp && bgOp.LastResult != null)
                            {
                                result.Objects.Add(new DetectedObject
                                {
                                    Label = "Presence",
                                    Confidence = bgOp.LastResult.Confidence / 100.0,
                                    BoundingBox = bgOp.LastResult.BoundingBox
                                });
                                result.Score = bgOp.LastResult.Confidence;
                                result.IsPass = bgOp.LastResult.IsPresent;
                                result.Message = bgOp.LastResult.Message;
                            }
                        }
                    }

                    // 如果有自定义通过条件
                    if (PassCondition != null)
                    {
                        result.IsPass = PassCondition(_operators.ToList());
                    }
                    else if (result.Objects.Count == 0)
                    {
                        // 如果没有模板匹配，默认通过
                        result.IsPass = true;
                        result.Score = 1.0;
                    }

                    // 优先使用算子反馈的详细消息
                    if (string.IsNullOrEmpty(result.Message))
                    {
                        // 查找最后一个有消息的匹配结果
                        var lastMatchMsg = _operators
                            .Select(o => (o.Operator as dynamic).LastMatchResult?.Message as string)
                            .LastOrDefault(msg => !string.IsNullOrEmpty(msg));

                        if (!string.IsNullOrEmpty(lastMatchMsg))
                        {
                            result.Message = lastMatchMsg;
                        }
                        else
                        {
                            result.Message = result.IsPass ? "检测通过" : "检测未通过";
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.IsPass = false;
                    result.Message = $"处理失败: {ex.Message}";
                }
                finally
                {
                    // 确保释放所有中间及最终产生的 Mat
                    previous?.Dispose();
                    current?.Dispose();
                }

                sw.Stop();
                result.ProcessingTimeMs = sw.Elapsed.TotalMilliseconds;
                return result;
            });
        }

        /// <summary>
        /// 获取处理后的预览图像
        /// </summary>
        public async Task<Mat> GetPreviewAsync(Mat input)
        {
            return await Task.Run(() =>
            {
                Mat current = input.Clone();
                Mat? previous = null;

                lock (_lock)
                {
                    foreach (var opInstance in _operators.OrderBy(o => o.Order))
                    {
                        previous?.Dispose();
                        previous = current;
                        current = opInstance.Operator.Execute(previous);
                    }
                }

                previous?.Dispose();

                // 如果结果是单通道，转回彩色以便显示
                if (current.Channels() == 1)
                {
                    Mat colored = new Mat();
                    Cv2.CvtColor(current, colored, ColorConversionCodes.GRAY2BGR);
                    current.Dispose();
                    return colored;
                }

                return current;
            });
        }

        /// <summary>
        /// 获取指定步骤的预览
        /// </summary>
        public async Task<Mat> GetStepPreviewAsync(Mat input, int stepIndex)
        {
            return await Task.Run(() =>
            {
                Mat current = input.Clone();
                Mat? previous = null;

                lock (_lock)
                {
                    var orderedOps = _operators.OrderBy(o => o.Order).Take(stepIndex + 1).ToList();
                    foreach (var opInstance in orderedOps)
                    {
                        previous?.Dispose();
                        previous = current;
                        current = opInstance.Operator.Execute(previous);
                    }
                }

                previous?.Dispose();

                if (current.Channels() == 1)
                {
                    Mat colored = new Mat();
                    Cv2.CvtColor(current, colored, ColorConversionCodes.GRAY2BGR);
                    current.Dispose();
                    return colored;
                }

                return current;
            });
        }

        /// <summary>
        /// 导出配置
        /// </summary>
        public VisionConfig ExportConfig()
        {
            lock (_lock)
            {
                return new VisionConfig
                {
                    Mode = VisionMode.Template,
                    Operators = _operators.OrderBy(o => o.Order).Select(o => new OperatorConfig
                    {
                        TypeId = o.Operator.TypeId,
                        InstanceId = o.InstanceId,
                        Parameters = new Dictionary<string, object>(o.Operator.Parameters)
                    }).ToList()
                };
            }
        }

        /// <summary>
        /// 导入配置
        /// </summary>
        public void ImportConfig(VisionConfig config)
        {
            lock (_lock)
            {
                ClearOperators();
                foreach (var opConfig in config.Operators)
                {
                    var op = OperatorFactory.CreateFromConfig(opConfig);
                    if (op != null)
                    {
                        AddOperator(op, opConfig.InstanceId);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                foreach (var op in _operators)
                {
                    op.LastOutput?.Dispose();
                    if (op.Operator is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                _operators.Clear();
            }
        }
    }

    /// <summary>
    /// 算子实例
    /// </summary>
    public class OperatorInstance
    {
        /// <summary>实例ID</summary>
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>算子</summary>
        public IImageOperator Operator { get; set; } = null!;

        /// <summary>执行顺序</summary>
        public int Order { get; set; }

        /// <summary>是否启用</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>最后输出（用于调试预览）</summary>
        public Mat? LastOutput { get; set; }
    }
}
