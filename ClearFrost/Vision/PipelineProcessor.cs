// ============================================================================
// 文件名: PipelineProcessor.cs
// 作者: 蘅芜君
// 描述:   视觉处理流水线处理器
//
// 功能:
//   - 管理图像处理算子的有序集合
//   - 依次执行算子处理图像
//   - 支持算子的增删改查
//   - 支持处理配置的导入导出
//
// 使用示例:
//   var processor = new PipelineProcessor();
//   processor.AddOperator(new GrayscaleOp());
//   processor.AddOperator(new TemplateMatchOp());
//   var result = await processor.ProcessAsync(inputMat);
//
// ============================================================================
using OpenCvSharp;
using System.Diagnostics;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 视觉处理流水线处理器。
    /// 负责按顺序执行一系列图像处理算子，并生成最终结果。
    /// </summary>
    public class PipelineProcessor : IVisionProcessor, IDisposable
    {
        private readonly List<OperatorInstance> _operators = new();
        private readonly object _lock = new();
        private bool _disposed = false;

        public string Name => "流水线处理器";

        /// <summary>
        /// 获取当前流水线中的所有算子实例
        /// </summary>
        public IReadOnlyList<OperatorInstance> Operators => _operators.AsReadOnly();

        /// <summary>
        /// 获取或设置一个条件函数，用于判断整个流水线处理是否通过。
        /// 如果为 null，则默认根据最后一个匹配算子的结果或无匹配算子时通过。
        /// </summary>
        public Func<List<OperatorInstance>, bool>? PassCondition { get; set; }

        /// <summary>
        /// 获取流水线最后一个算子的输出图像
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
        /// 在流水线末尾添加一个新算子
        /// </summary>
        /// <param name="op">图像算子实例</param>
        /// <param name="instanceId">指定实列ID，如果为空则自动生成</param>
        /// <returns>算子实例ID</returns>
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
        /// 在指定位置插入一个算子
        /// </summary>
        /// <param name="index">插入位置的索引</param>
        /// <param name="op">要插入的图像算子</param>
        /// <param name="instanceId">指定实例ID，如果为空则自动生成</param>
        /// <returns>算子实例ID</returns>
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
        /// 移除指定ID的算子
        /// </summary>
        /// <param name="instanceId">算子实例ID</param>
        /// <returns>如果移除成功返回 true</returns>
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
        /// 
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
        /// 
        /// </summary>
        public void ClearOperators()
        {
            lock (_lock)
            {
                _operators.Clear();
            }
        }

        /// <summary>
        /// 
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
        /// 
        /// </summary>
        public OperatorInstance? GetOperator(string instanceId)
        {
            lock (_lock)
            {
                return _operators.FirstOrDefault(o => o.InstanceId == instanceId);
            }
        }

        /// <summary>
        /// 
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
            // 
        }

        /// <summary>
        /// 异步执行整个视觉流水线处理
        /// </summary>
        public async Task<VisionResult> ProcessAsync(Mat input)
        {
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var result = new VisionResult();

                // 
                Mat? current = null;
                Mat? previous = null;

                try
                {
                    current = input.Clone();

                    lock (_lock)
                    {
                        foreach (var opInstance in _operators.OrderBy(o => o.Order))
                        {
                            // 
                            previous?.Dispose();
                            previous = current;

                            // 
                            current = opInstance.Operator.Execute(previous);

                            // 
                            opInstance.LastOutput?.Dispose();
                            opInstance.LastOutput = current.Clone();

                            // 
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
                            // 
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
                            // 
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
                            // 
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
                            // 
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

                    // 
                    if (PassCondition != null)
                    {
                        result.IsPass = PassCondition(_operators.ToList());
                    }
                    else if (result.Objects.Count == 0)
                    {
                        // 
                        result.IsPass = true;
                        result.Score = 1.0;
                    }

                    // 
                    if (string.IsNullOrEmpty(result.Message))
                    {
                        // 
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
                    // 
                    previous?.Dispose();
                    current?.Dispose();
                }

                sw.Stop();
                result.ProcessingTimeMs = sw.Elapsed.TotalMilliseconds;
                return result;
            });
        }

        /// <summary>
        /// 获取流水线处理的预览结果（通常用于UI显示）
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

                // 
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
        /// 
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
        /// 导出当前流水线的配置
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
        /// 从配置对象导入流水线设置（会清空现有算子）
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
    /// 算子实例包装类，包含算子对象及其元数据
    /// </summary>
    public class OperatorInstance
    {
        /// <summary>
        /// 唯一实例 ID
        /// </summary>
        public string InstanceId { get; set; } = string.Empty;

        /// 
        public IImageOperator Operator { get; set; } = null!;

        /// 
        public int Order { get; set; }

        /// 
        public bool IsEnabled { get; set; } = true;

        /// 
        public Mat? LastOutput { get; set; }
    }
}

