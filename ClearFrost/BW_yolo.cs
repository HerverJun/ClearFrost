// ============================================================================
// 文件名: BW_yolo.cs
// 作者: 蘅芜君
// 描述:   基于 OnnxRuntime 的 YOLO 检测器封装
//
// 功能:
//   - 支持 YOLOv5, YOLOv8, YOLOv26 等模型
//   - YOLOv26 支持 NMS-free 端到端推理
//   - 支持检测、分割、姿态估计等多种任务
//   - 提供同步和异步推理接口
//   - 包含图像预处理和后处理逻辑
//
// ============================================================================
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace ClearFrost.Yolo
{
    /// <summary>
    /// YOLO 任务类型枚举
    /// </summary>
    public enum YoloTaskType
    {
        /// <summary>
        /// 分类任务
        /// </summary>
        Classify = 0,
        /// <summary>
        /// 检测任务
        /// </summary>
        Detect = 1,
        /// <summary>
        /// 分割任务（仅检测，不生成掩码）
        /// </summary>
        SegmentDetectOnly = 2,
        /// <summary>
        /// 分割任务（检测并生成掩码）
        /// </summary>
        SegmentWithMask = 3,
        /// <summary>
        /// 姿态估计任务（仅检测，不生成关键点）
        /// </summary>
        PoseDetectOnly = 4,
        /// <summary>
        /// 姿态估计任务（检测并生成关键点）
        /// </summary>
        PoseWithKeypoints = 5,
        /// <summary>
        /// 有向包围盒（Oriented Bounding Box）检测任务
        /// </summary>
        Obb = 6
    }

    /// <summary>
    /// YOLO 检测器配置类
    /// </summary>
    public class YoloDetectorConfig
    {
        /// <summary>
        /// ONNX 模型文件路径
        /// </summary>
        public string ModelPath { get; set; } = string.Empty;
        /// <summary>
        /// 是否使用 GPU 进行推理
        /// </summary>
        public bool UseGpu { get; set; } = false;
        /// <summary>
        /// GPU 设备ID，当 UseGpu 为 true 时有效
        /// </summary>
        public int GpuDeviceId { get; set; } = 0;
        /// <summary>
        /// YOLO 模型版本 (例如 5, 8)。0 表示自动检测。
        /// </summary>
        public int YoloVersion { get; set; } = 0;
        /// <summary>
        /// 默认置信度阈值
        /// </summary>
        public float DefaultConfidence { get; set; } = 0.5f;
        /// <summary>
        /// 默认 IOU 阈值
        /// </summary>
        public float DefaultIouThreshold { get; set; } = 0.45f;
        /// <summary>
        /// ONNX Runtime 内部操作线程数
        /// </summary>
        public int IntraOpNumThreads { get; set; } = Environment.ProcessorCount;
        /// <summary>
        /// ONNX Runtime 跨操作线程数
        /// </summary>
        public int InterOpNumThreads { get; set; } = 1;

        /// <summary>
        /// 验证配置参数
        /// </summary>
        /// <exception cref="ArgumentException">ModelPath 为空或无效</exception>
        /// <exception cref="ArgumentOutOfRangeException">GpuDeviceId 小于 0</exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ModelPath))
                throw new ArgumentException("ModelPath is required");
            if (GpuDeviceId < 0)
                throw new ArgumentOutOfRangeException(nameof(GpuDeviceId));
        }
    }

    /// <summary>
    /// YOLO 检测器实现类，封装了 ONNX Runtime 推理逻辑
    /// </summary>
    partial class YoloDetector : IDisposable
    {
        // ==================== 常量定义 ====================

        private const int YOLO_BOX_ELEMENTS = 4;
        private const int YOLO5_OBJECTNESS_INDEX = 4;
        private const int YOLO5_CLASS_START_INDEX = 5;
        private const int YOLO8_CLASS_START_INDEX = 4;
        private const int DEFAULT_MASK_CHANNELS = 32;
        private const int BASIC_DATA_LENGTH = 6;

        private const int LETTERBOX_FILL_COLOR_R = 114;
        private const int LETTERBOX_FILL_COLOR_G = 114;
        private const int LETTERBOX_FILL_COLOR_B = 114;
        private const float PIXEL_NORMALIZE_FACTOR = 255f;

        private const float DEFAULT_CONFIDENCE_THRESHOLD = 0.5f;
        private const float DEFAULT_IOU_THRESHOLD = 0.45f;

        // ==================== 字段定义 ====================

        private bool _disposed = false;
        private InferenceSession? _inferenceSession;
        private int _tensorWidth, _tensorHeight;
        private string _modelInputName = "";
        private string _modelOutputName = "";
        private int[] _inputTensorInfo = Array.Empty<int>();
        private int[] _outputTensorInfo = Array.Empty<int>();
        private int[]? _outputTensorInfo2_Segment;
        private int _inferenceImageWidth, _inferenceImageHeight;
        private DenseTensor<float>? _inputTensor;
        private float[]? _tensorBuffer;
        private bool _tensorBufferInitialized = false;
        private int _yoloVersion;
        private float _maskScaleW = 0;
        private float _maskScaleH = 0;
        private string _modelVersion = "";
        private string _taskType = "";
        private int _segWidth = 0;
        private int _poseWidth = 0;
        private float _scale = 1;
        private int _padLeft = 0;
        private int _padTop = 0;
        /// <summary>
        /// 模型识别的标签名称数组
        /// </summary>
        public string[] Labels { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 上次推理的性能指标
        /// </summary>
        public InferenceMetrics? LastMetrics { get; private set; }

        /// <summary>
        /// 全局工业渲染模式开关。开启后使用轻量绘制路径。
        /// </summary>
        public static bool IndustrialRenderMode { get; set; } = true;

        private readonly object _inferenceLock = new object();
        private readonly SemaphoreSlim _inferenceSemaphore = new SemaphoreSlim(1, 1);

        private YoloTaskType _executionTaskMode = YoloTaskType.Detect;

        /// <summary>
        /// 获取当前加载的 YOLO 模型版本
        /// </summary>
        public int YoloVersion => _yoloVersion;
        /// <summary>
        /// 获取模型输入张量的宽度
        /// </summary>
        public int InputWidth => _inputTensorInfo.Length > 3 ? _inputTensorInfo[3] : 0;
        /// <summary>
        /// 获取模型输入张量的高度
        /// </summary>
        public int InputHeight => _inputTensorInfo.Length > 2 ? _inputTensorInfo[2] : 0;
        /// <summary>
        /// 获取模型支持的类别数量
        /// </summary>
        public int ClassCount => Labels.Length;
        /// <summary>
        /// 获取模型的版本信息（从模型元数据中读取）
        /// </summary>
        public string ModelVersion => _modelVersion;
        /// <summary>
        /// 获取模型检测到的原始任务类型（从模型元数据中读取）
        /// </summary>
        public YoloTaskType TaskType => _executionTaskMode;

        /// <summary>
        /// 获取或设置当前执行的任务模式。
        /// 设置时会根据模型实际支持的任务类型进行调整。
        /// </summary>
        public YoloTaskType TaskMode
        {
            get { return _executionTaskMode; }
            set
            {
                if (_taskType == "classify")
                {
                    _executionTaskMode = YoloTaskType.Classify;
                }
                else if (_taskType == "detect")
                {
                    _executionTaskMode = YoloTaskType.Detect;
                }
                else if (_taskType == "segment")
                {
                    if (value == YoloTaskType.Detect || value == YoloTaskType.SegmentDetectOnly || value == YoloTaskType.SegmentWithMask)
                    {
                        _executionTaskMode = value;
                    }
                    else
                    {
                        _executionTaskMode = YoloTaskType.SegmentWithMask;
                    }
                }
                else if (_taskType == "pose")
                {
                    if (value == YoloTaskType.Detect || value == YoloTaskType.PoseDetectOnly || value == YoloTaskType.PoseWithKeypoints)
                    {
                        _executionTaskMode = value;
                    }
                    else
                    {
                        _executionTaskMode = YoloTaskType.PoseWithKeypoints;
                    }
                }
                else if (_taskType == "obb")
                {
                    if (value == YoloTaskType.Obb)
                    {
                        _executionTaskMode = value;
                    }
                    else
                    {
                        _executionTaskMode = YoloTaskType.Obb;
                    }
                }
                else
                {
                    _executionTaskMode = YoloTaskType.Detect;
                }
            }
        }

        /// <summary>
        /// 使用配置对象初始化 YOLO 检测器的新实例。
        /// </summary>
        /// <param name="config">YOLO 检测器配置对象。</param>
        public YoloDetector(YoloDetectorConfig config) : this(config.ModelPath, config.YoloVersion, config.GpuDeviceId, config.UseGpu)
        {
            config.Validate();
        }

        /// <summary>
        /// 初始化 YOLO 检测器的新实例。
        /// </summary>
        /// <param name="modelPath">ONNX 模型文件路径。</param>
        /// <param name="yoloVersion">YOLO 模型版本 (例如 5, 8)。0 表示自动检测。</param>
        /// <param name="gpuIndex">GPU 设备ID，当 useGpu 为 true 时有效。</param>
        /// <param name="useGpu">是否使用 GPU 进行推理。</param>
        /// <exception cref="ArgumentNullException">modelPath 为空。</exception>
        /// <exception cref="FileNotFoundException">模型文件不存在。</exception>
        /// <exception cref="Exception">模型类型不受支持。</exception>
        public YoloDetector(string modelPath, int yoloVersion = 0, int gpuIndex = 0, bool useGpu = false)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentNullException(nameof(modelPath));
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Model file not found: {modelPath}", modelPath);

            var options = CreateSessionOptions(useGpu, gpuIndex);

            try
            {
                _inferenceSession = new InferenceSession(modelPath, options);
            }
            catch
            {
                options.Dispose();
                throw;
            }

            _modelInputName = _inferenceSession.InputNames.First();
            _modelOutputName = _inferenceSession.OutputNames.First();
            _inputTensorInfo = _inferenceSession.InputMetadata[_modelInputName].Dimensions;
            _outputTensorInfo = _inferenceSession.OutputMetadata[_modelOutputName].Dimensions;
            var modelMetadata = _inferenceSession.ModelMetadata.CustomMetadataMap;
            if (modelMetadata.Keys.Contains("names"))
            {
                Labels = SplitLabelNames(modelMetadata["names"]!);
            }
            else
            {
                Labels = new string[0];
            }
            if (modelMetadata.Keys.Contains("version"))
            {
                _modelVersion = modelMetadata["version"];
            }
            if (modelMetadata.Keys.Contains("task"))
            {
                _taskType = modelMetadata["task"];
                if (_taskType == "segment")
                {
                    string modelOutputName2 = _inferenceSession.OutputNames[1];
                    _outputTensorInfo2_Segment = _inferenceSession.OutputMetadata[modelOutputName2].Dimensions;
                    _segWidth = _outputTensorInfo2_Segment[1];
                    _maskScaleW = 1f * _outputTensorInfo2_Segment[3] / _inputTensorInfo[3];
                    _maskScaleH = 1f * _outputTensorInfo2_Segment[2] / _inputTensorInfo[2];
                }
                else if (_taskType == "pose")
                {
                    if (_outputTensorInfo[1] > _outputTensorInfo[2])
                    {
                        _poseWidth = _outputTensorInfo[2] - 5;
                    }
                    else
                    {
                        _poseWidth = _outputTensorInfo[1] - 5;
                    }
                }
            }
            else
            {
                if (_outputTensorInfo.Length == 2)
                {
                    _taskType = "classify";
                }
                else if (_outputTensorInfo.Length == 3)
                {
                    if (_inferenceSession.OutputNames.Count == 1)
                    {
                        _taskType = "detect";
                    }
                    else if (_inferenceSession.OutputNames.Count == 2)
                    {
                        string modelOutputName2 = _inferenceSession.OutputNames[1];
                        _outputTensorInfo2_Segment = _inferenceSession.OutputMetadata[modelOutputName2].Dimensions;
                        _segWidth = _outputTensorInfo2_Segment[1];
                        _maskScaleW = 1f * _outputTensorInfo2_Segment[3] / _inputTensorInfo[3];
                        _maskScaleH = 1f * _outputTensorInfo2_Segment[2] / _inputTensorInfo[2];
                        _taskType = "segment";
                    }
                }
                else
                {
                    throw new Exception("Model not supported yet");
                }
            }
            TaskMode = YoloTaskType.Detect;
            _yoloVersion = DetermineModelVersion(yoloVersion);
            _tensorWidth = _inputTensorInfo[3];
            _tensorHeight = _inputTensorInfo[2];
        }

        /// <summary>
        /// 创建 ONNX Runtime 会话选项。
        /// </summary>
        /// <param name="useGpu">是否使用 GPU。</param>
        /// <param name="gpuIndex">GPU 设备ID。</param>
        /// <returns>配置好的 SessionOptions 对象。</returns>
        private static SessionOptions CreateSessionOptions(bool useGpu, int gpuIndex)
        {
            var options = new SessionOptions();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            options.EnableMemoryPattern = true;
            options.EnableCpuMemArena = true;
            options.IntraOpNumThreads = Environment.ProcessorCount;

            if (useGpu)
            {
                try
                {
                    options.AppendExecutionProvider_DML(gpuIndex);
                }
                catch (Exception)
                {
                    throw;
                }
            }

            return options;
        }

        /// <summary>
        /// 执行推理（同步）。
        /// </summary>
        /// <param name="image">输入图像。</param>
        /// <param name="confidence">置信度阈值。</param>
        /// <param name="iouThreshold">IOU 阈值。</param>
        /// <param name="globalIou">是否全局 NMS。</param>
        /// <param name="preprocessingMode">预处理模式 (0: Letterbox, 1: Resize)。</param>
        /// <returns>检测结果列表。</returns>
        /// <exception cref="ObjectDisposedException">如果检测器已被释放。</exception>
        /// <exception cref="ArgumentNullException">image 为空。</exception>
        /// <exception cref="ArgumentException">图像尺寸无效。</exception>
        /// <exception cref="ArgumentOutOfRangeException">confidence 或 iouThreshold 超出有效范围。</exception>
        public List<YoloResult> Inference(Bitmap image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = 1)
        {
            ThrowIfDisposed();
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (image.Width <= 0 || image.Height <= 0)
                throw new ArgumentException("Invalid image dimensions", nameof(image));
            if (confidence < 0 || confidence > 1)
                throw new ArgumentOutOfRangeException(nameof(confidence), "Must be between 0 and 1");
            if (iouThreshold < 0 || iouThreshold > 1)
                throw new ArgumentOutOfRangeException(nameof(iouThreshold), "Must be between 0 and 1");

            lock (_inferenceLock)
            {
                return InferenceInternal(image, confidence, iouThreshold, globalIou, preprocessingMode);
            }
        }

        /// <summary>
        /// 执行推理（异步）。
        /// </summary>
        /// <param name="image">输入图像。</param>
        /// <param name="confidence">置信度阈值。</param>
        /// <param name="iouThreshold">IOU 阈值。</param>
        /// <param name="globalIou">是否全局 NMS。</param>
        /// <param name="preprocessingMode">预处理模式。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>检测结果列表。</returns>
        /// <exception cref="ObjectDisposedException">如果检测器已被释放。</exception>
        /// <exception cref="ArgumentNullException">image 为空。</exception>
        /// <exception cref="OperationCanceledException">如果操作被取消。</exception>
        public async Task<List<YoloResult>> InferenceAsync(Bitmap image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = 1, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            await _inferenceSemaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(() => InferenceInternal(image, confidence, iouThreshold, globalIou, preprocessingMode), cancellationToken);
            }
            finally
            {
                _inferenceSemaphore.Release();
            }
        }

        /// <summary>
        /// 内部推理方法，执行图像预处理、模型推理和结果后处理。
        /// </summary>
        /// <param name="image">输入图像。</param>
        /// <param name="confidence">置信度阈值。</param>
        /// <param name="iouThreshold">IOU 阈值。</param>
        /// <param name="globalIou">是否全局 NMS。</param>
        /// <param name="preprocessingMode">预处理模式 (0: Letterbox, 1: Resize)。</param>
        /// <returns>检测结果列表。</returns>
        private List<YoloResult> InferenceInternal(Bitmap image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = 1)
        {
            ThrowIfDisposed();
            if (_inferenceSession == null) return new List<YoloResult>();

            var metrics = new InferenceMetrics();
            var sw = Stopwatch.StartNew();

            Bitmap? processedImage = null;

            try
            {
                // ==================== 预处理阶段 ====================
                EnsureTensorBuffer();
                Array.Clear(_tensorBuffer!, 0, _tensorBuffer!.Length);

                _scale = 1;
                _padLeft = 0;
                _padTop = 0;
                _inferenceImageWidth = image.Width;
                _inferenceImageHeight = image.Height;

                if (preprocessingMode == 0)
                {
                    processedImage = LetterboxResize(image);
                    ImageToTensor_Parallel(processedImage, _tensorBuffer!);
                }
                else if (preprocessingMode == 1)
                {
                    ImageToTensor_NoInterpolation(image, _tensorBuffer!);
                }
                _inputTensor = new DenseTensor<float>(_tensorBuffer!, _inputTensorInfo);
                IReadOnlyCollection<NamedOnnxValue> container = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_modelInputName, _inputTensor) };

                metrics.PreprocessMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();

                // ==================== �����׶� ====================
                List<YoloResult> filteredDataList;
                List<YoloResult> finalResult = new List<YoloResult>();

                if (_executionTaskMode == YoloTaskType.Classify)
                {
                    using var resultData = _inferenceSession.Run(container);
                    var output0 = resultData.First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    finalResult = FilterConfidence_Classify(output0, confidence);
                }
                else if (_executionTaskMode == YoloTaskType.Detect)
                {
                    using var resultData = _inferenceSession.Run(container);
                    var output0 = resultData.First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    if (_yoloVersion == 26)
                    {
                        // YOLOv26 NMS-free: 直接过滤置信度，输出已是最终结果
                        finalResult = FilterConfidence_Yolo26_Detect(output0, confidence);
                    }
                    else if (_yoloVersion == 8)
                    {
                        filteredDataList = FilterConfidence_Yolo8_9_11_Detect(output0, confidence);
                        finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                    }
                    else if (_yoloVersion == 5)
                    {
                        filteredDataList = FilterConfidence_Yolo5_Detect(output0, confidence);
                        finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                    }
                    else
                    {
                        filteredDataList = FilterConfidence_Yolo6_Detect(output0, confidence);
                        finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                    }
                }
                else if (_executionTaskMode == YoloTaskType.SegmentDetectOnly || _executionTaskMode == YoloTaskType.SegmentWithMask)
                {
                    using var resultData = _inferenceSession.Run(container);
                    var output0 = resultData.First().AsTensor<float>();
                    var output1 = resultData.ElementAtOrDefault(1)?.AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    if (_yoloVersion == 8)
                    {
                        filteredDataList = FilterConfidence_Yolo8_11_Segment(output0, confidence);
                    }
                    else
                    {
                        filteredDataList = FilterConfidence_Yolo5_Segment(output0, confidence);
                    }
                    finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                    RestoreMask(ref finalResult, output1);
                }
                else if (_executionTaskMode == YoloTaskType.PoseDetectOnly || _executionTaskMode == YoloTaskType.PoseWithKeypoints)
                {
                    using var resultData = _inferenceSession.Run(container);
                    var output0 = resultData.First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    filteredDataList = FilterConfidence_Pose(output0, confidence);
                    finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                }
                else if (_executionTaskMode == YoloTaskType.Obb)
                {
                    using var resultData = _inferenceSession.Run(container);
                    var output0 = resultData.First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    filteredDataList = FilterConfidence_Obb(output0, confidence);
                    finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                }

                // ==================== �����׶� ====================
                RestoreCoordinates(ref finalResult);
                if (_executionTaskMode != YoloTaskType.Classify)
                {
                    RemoveOutOfBoundsCoordinates(ref finalResult);
                }

                metrics.PostprocessMs = sw.Elapsed.TotalMilliseconds;
                metrics.DetectionCount = finalResult.Count;
                LastMetrics = metrics;

                return finalResult;
            }
            finally
            {
                processedImage?.Dispose();
            }
        }

        private int DetermineModelVersion(int version)
        {
            if (_taskType == "classify")
            {
                return 5;
            }
            // YOLOv26+ 使用 NMS-free 推理，显式指定版本
            if (version >= 26)
            {
                return 26;
            }

            // 根据输出张量形状自动检测 YOLOv26
            // v26 NMS-free 输出格式: [1, ~300, 6] (batch, num_detections, 6)
            // v8/v11 输出格式: [1, 84, 8400] 或 [1, 8400, 84]
            if (_outputTensorInfo.Length == 3)
            {
                int dim1 = _outputTensorInfo[1];
                int dim2 = _outputTensorInfo[2];
                // v26 特征: 第三维度恰好是 6 (x1, y1, x2, y2, conf, class)
                // 且第二维度通常是 300 (默认检测数量)
                if (dim2 == 6 && dim1 >= 100 && dim1 <= 500)
                {
                    return 26;
                }
            }

            if (version >= 8)
            {
                return 8;
            }
            else if (version < 8 && version >= 5)
            {
                return version;
            }
            // 从模型元数据中读取版本
            if (_modelVersion != "")
            {
                int ver = int.Parse(_modelVersion.Split('.')[0]);
                // 检测 v26+
                if (ver >= 26) return 26;
                return ver >= 8 ? 8 : ver;
            }
            int mid = _outputTensorInfo[1];
            int right = _outputTensorInfo[2];
            int size = mid < right ? mid : right;
            int labelCount = Labels.Length;
            if (labelCount == size - 4 - _segWidth)
            {
                return 8;
            }
            if (labelCount == 0 && mid < right)
            {
                return 8;
            }
            return 5;
        }

        private string[] SplitLabelNames(string name)
        {
            string removedBrackets = name.Replace("{", "").Replace("}", "");
            string[] splitArray = removedBrackets.Split(',');
            string[] returnArray = new string[splitArray.Length];
            for (int i = 0; i < splitArray.Length; i++)
            {
                int startIndex = splitArray[i].IndexOf(':') + 3;
                int endIndex = splitArray[i].Length - 1;
                returnArray[i] = splitArray[i].Substring(startIndex, endIndex - startIndex);
            }
            return returnArray;
        }

        public string GetLabelNameByIndex(int index)
        {
            if (index > Labels.Length || index < 0)
            {
                return "";
            }
            return Labels[index];
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _inferenceSession?.Dispose();
                _inferenceSemaphore?.Dispose();
                _inputTensor = null;
                _tensorBuffer = null;
                _tensorBufferInitialized = false;
            }

            _disposed = true;
        }

        ~YoloDetector()
        {
            Dispose(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(YoloDetector));
            }
        }
    }
}
