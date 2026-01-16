// ============================================================================
// 
// 
//         
// 
// 
// 
// 
// 
//
// 
//   var processor = new PipelineProcessor();
//   processor.AddOperator(new GrayscaleOp());
//   processor.AddOperator(new TemplateMatchOp());
//   var result = await processor.ProcessAsync(inputMat);
//
// 
// 
// ============================================================================
using OpenCvSharp;
using System.Diagnostics;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 
    /// 
    /// </summary>
    public class PipelineProcessor : IVisionProcessor, IDisposable
    {
        private readonly List<OperatorInstance> _operators = new();
        private readonly object _lock = new();
        private bool _disposed = false;

        public string Name => "流水线处理器";

        /// <summary>
        /// 
        /// </summary>
        public IReadOnlyList<OperatorInstance> Operators => _operators.AsReadOnly();

        /// <summary>
        /// 
        /// </summary>
        public Func<List<OperatorInstance>, bool>? PassCondition { get; set; }

        /// <summary>
        /// 
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
        /// 
        /// </summary>
        /// 
        /// 
        /// 
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
        /// 
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
        /// 
        /// </summary>
        /// 
        /// 
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
        /// 
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
        /// 
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
        /// 
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
        /// 
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
    /// 
    /// </summary>
    public class OperatorInstance
    {
        /// 
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

