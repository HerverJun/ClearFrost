// ============================================================================
// 文件名: UnsupervisedDetector.cs
// 描述:   无监督异常检测模型实现类，基于 ONNX Runtime。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ClearFrost.Yolo
{
    /// <summary>
    /// 无监督异常检测模型，支持 Autoencoder / PatchCore 等无监督 ONNX 模型，输出 AnomalyScore 并判定合格状态。
    /// </summary>
    public sealed class UnsupervisedDetector : IVisionModel
    {
        private bool _disposed;
        private InferenceSession? _session;
        private readonly string _modelPath;
        private string _inputName = "input";
        private string _outputName = "output";
        private int _inputWidth = 256;
        private int _inputHeight = 256;
        private readonly bool _useGpu;
        private readonly int _gpuDeviceId;

        /// <summary>
        /// 异常阈值。异常得分高于该值时判定为不合格。
        /// </summary>
        public float AnomalyThreshold { get; set; } = 0.5f;

        public string[] Labels { get; } = new[] { "anomaly" };

        public InferenceMetrics? LastMetrics { get; private set; }

        public bool RequestedGpu => _useGpu;

        public bool GpuActive { get; private set; }

        public int GpuDeviceId => _gpuDeviceId;

        public string ExecutionProvider { get; private set; } = "CPUExecutionProvider";

        public string GpuFailureReason { get; private set; } = string.Empty;

        public UnsupervisedDetector(string modelPath, int gpuDeviceId = 0, bool useGpu = false)
        {
            _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            _gpuDeviceId = gpuDeviceId;
            _useGpu = useGpu;

            InitializeSession();
        }

        private void InitializeSession()
        {
            if (!File.Exists(_modelPath))
            {
                throw new FileNotFoundException("无监督模型文件不存在", _modelPath);
            }

            using var options = new SessionOptions();
            if (_useGpu)
            {
                try
                {
                    // 尝试配置 DirectML 加速
                    options.AppendExecutionProvider_DML(_gpuDeviceId);
                    ExecutionProvider = "DmlExecutionProvider";
                    GpuActive = true;
                }
                catch (Exception ex)
                {
                    GpuActive = false;
                    GpuFailureReason = ex.Message;
                    ExecutionProvider = "CPUExecutionProvider";
                }
            }

            _session = new InferenceSession(_modelPath, options);

            // 获取输入张量的结构和大小
            if (_session.InputMetadata.Count > 0)
            {
                var inputMeta = _session.InputMetadata.First();
                _inputName = inputMeta.Key;
                var dims = inputMeta.Value.Dimensions;
                if (dims.Length >= 4)
                {
                    _inputHeight = dims[2] > 0 ? dims[2] : 256;
                    _inputWidth = dims[3] > 0 ? dims[3] : 256;
                }
            }

            if (_session.OutputMetadata.Count > 0)
            {
                _outputName = _session.OutputMetadata.First().Key;
            }
        }

        public ModelResult Inference(Bitmap image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = -1)
        {
            using var mat = OpenCvSharp.Extensions.BitmapConverter.ToMat(image);
            return Inference(mat, confidence, iouThreshold, globalIou, preprocessingMode);
        }

        public ModelResult Inference(Mat image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = -1)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnsupervisedDetector));
            if (image == null || image.Empty())
            {
                return new ModelResult { IsQualified = false, ErrorMessage = "输入图像为空" };
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. 前处理：缩放到模型输入尺寸 (如 256x256) 并根据通道数转换为 RGB
                using var resized = new Mat();
                Cv2.Resize(image, resized, new OpenCvSharp.Size(_inputWidth, _inputHeight));

                using var rgb = new Mat();
                int channels = resized.Channels();
                if (channels == 3)
                {
                    Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
                }
                else if (channels == 4)
                {
                    Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGRA2RGB);
                }
                else if (channels == 1)
                {
                    Cv2.CvtColor(resized, rgb, ColorConversionCodes.GRAY2RGB);
                }
                else
                {
                    throw new NotSupportedException($"不支持的图像通道数: {channels}");
                }

                // 2. 将 Mat 转换为 Float Array DenseTensor
                float[] tensorData = new float[3 * _inputWidth * _inputHeight];
                int pixelCount = _inputWidth * _inputHeight;

                unsafe
                {
                    byte* ptr = (byte*)rgb.Data.ToPointer();
                    int step = (int)rgb.Step();

                    for (int y = 0; y < _inputHeight; y++)
                    {
                        byte* row = ptr + y * step;
                        for (int x = 0; x < _inputWidth; x++)
                        {
                            byte* col = row + x * 3;
                            // 归一化到 0.0 - 1.0 之间
                            tensorData[y * _inputWidth + x] = col[0] / 255f; // R
                            tensorData[pixelCount + y * _inputWidth + x] = col[1] / 255f; // G
                            tensorData[2 * pixelCount + y * _inputWidth + x] = col[2] / 255f; // B
                        }
                    }
                }

                var dimensions = new[] { 1, 3, _inputHeight, _inputWidth };
                var inputTensor = new DenseTensor<float>(tensorData, dimensions);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                };

                // 3. 执行推理
                sw.Stop();
                long preprocessMs = sw.ElapsedMilliseconds;
                sw.Restart();

                if (_session == null) throw new InvalidOperationException("ONNX 推理会话未就绪");
                using var results = _session.Run(inputs);

                sw.Stop();
                long inferenceMs = sw.ElapsedMilliseconds;

                // 4. 解析输出成果
                float anomalyScore = 0.0f;
                var outputValue = results.FirstOrDefault(r => r.Name == _outputName);
                if (outputValue == null)
                {
                    return new ModelResult
                    {
                        IsQualified = false,
                        ErrorMessage = $"未能在推理输出中找到名为 '{_outputName}' 的节点"
                    };
                }

                var tensor = outputValue.AsTensor<float>();
                if (tensor == null)
                {
                    return new ModelResult
                    {
                        IsQualified = false,
                        ErrorMessage = $"无法将输出节点 '{_outputName}' 的张量解析为 float 类型"
                    };
                }

                anomalyScore = tensor.Length > 0 ? tensor.Max() : 0.0f;

                // 5. 判定并伪造 YoloResult (以便原有的渲染及规则引擎做红框标示)
                bool isQualified = anomalyScore < AnomalyThreshold;
                var fakeDetections = new List<YoloResult>();
                if (!isQualified)
                {
                    // 伪造一个全图缺陷框展示在 UI 上
                    var yoloResult = new YoloResult();
                    yoloResult.SetDetectionData(
                        centerX: image.Width / 2f,
                        centerY: image.Height / 2f,
                        width: image.Width,
                        height: image.Height,
                        confidence: anomalyScore,
                        classId: 0
                    );
                    fakeDetections.Add(yoloResult);
                }

                LastMetrics = new InferenceMetrics
                {
                    PreprocessMs = preprocessMs,
                    InferenceMs = inferenceMs,
                    PostprocessMs = 1
                };

                return new ModelResult
                {
                    Results = fakeDetections,
                    AnomalyScore = anomalyScore,
                    IsQualified = isQualified,
                    ErrorMessage = string.Empty
                };
            }
            catch (Exception ex)
            {
                return new ModelResult
                {
                    IsQualified = false,
                    ErrorMessage = $"无监督推理异常: {ex.Message}"
                };
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _session?.Dispose();
            _session = null;
        }
    }
}
