// ============================================================================
// 文件名: BW_yolo.cs
// 描述:   YOLO 深度学习目标检测引擎 - 基于 ONNX Runtime 的推理核心
//
// 功能概述:
//   - 支持 YOLOv5/v6/v8/v9/v11 模型自动识别
//   - 支持多种任务类型: 分类(classify)、检测(detect)、分割(segment)、姿态(pose)、OBB
//   - 支持 CPU 和 GPU (DirectML) 推理加速
//   - 内置两种预处理模式: 高精度/高速度
//
// 核心方法:
//   - YoloDetector(modelPath, ...): 构造函数，加载 ONNX 模型
//   - Inference(image, ...):        主推理入口，返回检测结果列表
//   - NmsFilter(...):               非极大值抑制后处理
//
// 数据结构:
//   - YoloResult: 检测结果结构，包含 BasicData[6]={cx, cy, w, h, conf, class_idx}
//
// 作者: ClearFrost Team (基于开源 YOLO 推理库改进)
// 创建日期: 2024
// ============================================================================
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;


namespace YoloDetection
{
    public enum YoloTaskType
    {
        Classify = 0,
        Detect = 1,
        SegmentDetectOnly = 2,
        SegmentWithMask = 3,
        PoseDetectOnly = 4,
        PoseWithKeypoints = 5,
        Obb = 6
    }

    public class YoloDetectorConfig
    {
        public string ModelPath { get; set; } = string.Empty;
        public bool UseGpu { get; set; } = false;
        public int GpuDeviceId { get; set; } = 0;
        public int YoloVersion { get; set; } = 0;
        public float DefaultConfidence { get; set; } = 0.5f;
        public float DefaultIouThreshold { get; set; } = 0.45f;
        public int IntraOpNumThreads { get; set; } = Environment.ProcessorCount;
        public int InterOpNumThreads { get; set; } = 1;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ModelPath))
                throw new ArgumentException("ModelPath is required");
            if (GpuDeviceId < 0)
                throw new ArgumentOutOfRangeException(nameof(GpuDeviceId));
        }
    }

    class YoloDetector : IDisposable
    {
        // ==================== 常量定义 ====================

        // YOLO 输出格式常量
        private const int YOLO_BOX_ELEMENTS = 4;               // cx, cy, w, h
        private const int YOLO5_OBJECTNESS_INDEX = 4;          // YOLOv5 objectness 位置
        private const int YOLO5_CLASS_START_INDEX = 5;         // YOLOv5 类别概率起始位置
        private const int YOLO8_CLASS_START_INDEX = 4;         // YOLOv8+ 类别概率起始位置
        private const int DEFAULT_MASK_CHANNELS = 32;          // 分割任务默认 mask 通道数
        private const int BASIC_DATA_LENGTH = 6;               // BasicData 数组长度

        // 预处理常量
        private const int LETTERBOX_FILL_COLOR_R = 114;
        private const int LETTERBOX_FILL_COLOR_G = 114;
        private const int LETTERBOX_FILL_COLOR_B = 114;
        private const float PIXEL_NORMALIZE_FACTOR = 255f;     // 像素归一化因子

        // NMS 默认参数
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
        public string[] Labels { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 获取最近一次推理的性能指标
        /// </summary>
        public InferenceMetrics? LastMetrics { get; private set; }

        // ==================== 线程安全支持 ====================

        /// <summary>
        /// 用于同步推理的锁对象
        /// </summary>
        private readonly object _inferenceLock = new object();

        /// <summary>
        /// 用于异步推理的信号量，确保推理操作串行执行
        /// </summary>
        private readonly SemaphoreSlim _inferenceSemaphore = new SemaphoreSlim(1, 1);

        private YoloTaskType _executionTaskMode = YoloTaskType.Detect;

        /// <summary>
        /// Yolo Model Version (5, 6, 8, etc.)
        /// </summary>
        public int YoloVersion => _yoloVersion;

        /// <summary>
        /// Model Input Width (from Tensor Info)
        /// </summary>
        public int InputWidth => _inputTensorInfo.Length > 3 ? _inputTensorInfo[3] : 0;

        /// <summary>
        /// Model Input Height (from Tensor Info)
        /// </summary>
        public int InputHeight => _inputTensorInfo.Length > 2 ? _inputTensorInfo[2] : 0;

        /// <summary>
        /// Number of classes
        /// </summary>
        public int ClassCount => Labels.Length;

        /// <summary>
        /// Model Version from metadata
        /// </summary>
        public string ModelVersion => _modelVersion;

        /// <summary>
        /// Current Task Type
        /// </summary>
        public YoloTaskType TaskType => _executionTaskMode;

        /// <summary>
        /// Task Mode (Backward Compatibility)
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
                // Detection
                else if (_taskType == "detect")
                {
                    _executionTaskMode = YoloTaskType.Detect;
                }
                // Segmentation
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
                // Pose
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
                    // Default to detection if no data
                    _executionTaskMode = YoloTaskType.Detect;
                }
            }
        }
        public YoloDetector(YoloDetectorConfig config) : this(config.ModelPath, config.YoloVersion, config.GpuDeviceId, config.UseGpu)
        {
            config.Validate();
        }

        /// <summary>
        /// Initialize YoloDetector
        /// </summary>
        /// <param name="modelPath">Must use ONNX model</param>      
        /// <param name="yoloVersion"></param>
        /// <param name="gpuIndex">0 by default</param>
        /// <param name="useGpu">false by default</param>
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
                // Ensure options are disposed if session creation fails, though SessionOptions are finalizable.
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
                    else
                    {
                        // throw new Exception("Model not supported yet");
                    }
                }
                else
                {
                    throw new Exception("Model not supported yet");
                }
            }
            TaskMode = YoloTaskType.Detect; // Default, will be set by logic
            _yoloVersion = DetermineModelVersion(yoloVersion);
            _tensorWidth = _inputTensorInfo[3];
            _tensorHeight = _inputTensorInfo[2];
        }

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
                    // Fallback or log if needed, or let it bubble up
                    // For now keeping simple as per requirement
                    throw;
                }
            }

            return options;
        }
        /// <summary>
        /// Main Inference Function (Thread-Safe)
        /// </summary>
        /// <param name="image">Image data</param>
        /// <param name="confidence">0-1 float, higher value means faster speed but higher accuracy requirement.</param>
        /// <param name="iouThreshold">0-1 float, higher value increases probability of overlapping boxes.</param>
        /// <param name="globalIou">false means different classes allow overlap, true means all classes respect iou threshold.</param>  
        /// <param name="preprocessingMode">0: High accuracy mode (better for small objects); 1: High speed mode (better for large images).</param>   
        /// <returns>Returns list with basic data format {center_x, center_y, width, height, confidence, class_index}</returns>
        /// <summary>
        /// Main Inference Function (Thread-Safe)
        /// </summary>
        /// <param name="image">Image data</param>
        /// <param name="confidence">0-1 float, higher value means faster speed but higher accuracy requirement.</param>
        /// <param name="iouThreshold">0-1 float, higher value increases probability of overlapping boxes.</param>
        /// <param name="globalIou">false means different classes allow overlap, true means all classes respect iou threshold.</param>  
        /// <param name="preprocessingMode">0: High accuracy mode (better for small objects); 1: High speed mode (better for large images).</param>   
        /// <returns>Returns list with basic data format {center_x, center_y, width, height, confidence, class_index}</returns>
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
        /// Asynchronous Inference Function (Thread-Safe)
        /// </summary>
        /// <param name="image">Image data</param>
        /// <param name="confidence">0-1 float, higher value means faster speed but higher accuracy requirement.</param>
        /// <param name="iouThreshold">0-1 float, higher value increases probability of overlapping boxes.</param>
        /// <param name="globalIou">false means different classes allow overlap, true means all classes respect iou threshold.</param>  
        /// <param name="preprocessingMode">0: High accuracy mode (better for small objects); 1: High speed mode (better for large images).</param>   
        /// <returns>Returns list with basic data format {center_x, center_y, width, height, confidence, class_index}</returns>
        /// <summary>
        /// Asynchronous Inference Function (Thread-Safe)
        /// </summary>
        /// <param name="image">Image data</param>
        /// <param name="confidence">0-1 float, higher value means faster speed but higher accuracy requirement.</param>
        /// <param name="iouThreshold">0-1 float, higher value increases probability of overlapping boxes.</param>
        /// <param name="globalIou">false means different classes allow overlap, true means all classes respect iou threshold.</param>  
        /// <param name="preprocessingMode">0: High accuracy mode (better for small objects); 1: High speed mode (better for large images).</param>   
        /// <returns>Returns list with basic data format {center_x, center_y, width, height, confidence, class_index}</returns>
        public async Task<List<YoloResult>> InferenceAsync(Bitmap image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = 1, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            await _inferenceSemaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 在线程池中执行推理以避免阻塞调用线程
                return await Task.Run(() => InferenceInternal(image, confidence, iouThreshold, globalIou, preprocessingMode), cancellationToken);
            }
            finally
            {
                _inferenceSemaphore.Release();
            }
        }

        /// <summary>
        /// Internal Inference Implementation (Not Thread-Safe - Use Inference() or InferenceAsync() instead)
        /// </summary>
        /// <param name="image">Image data</param>
        /// <param name="confidence">0-1 float, higher value means faster speed but higher accuracy requirement.</param>
        /// <param name="iouThreshold">0-1 float, higher value increases probability of overlapping boxes.</param>
        /// <param name="globalIou">false means different classes allow overlap, true means all classes respect iou threshold.</param>  
        /// <param name="preprocessingMode">0: High accuracy mode (better for small objects); 1: High speed mode (better for large images).</param>   
        /// <returns>Returns list with basic data format {center_x, center_y, width, height, confidence, class_index}</returns>
        /// <summary>
        /// Internal Inference Implementation (Not Thread-Safe - Use Inference() or InferenceAsync() instead)
        /// </summary>
        /// <param name="image">Image data</param>
        /// <param name="confidence">0-1 float, higher value means faster speed but higher accuracy requirement.</param>
        /// <param name="iouThreshold">0-1 float, higher value increases probability of overlapping boxes.</param>
        /// <param name="globalIou">false means different classes allow overlap, true means all classes respect iou threshold.</param>  
        /// <param name="preprocessingMode">0: High accuracy mode (better for small objects); 1: High speed mode (better for large images).</param>   
        /// <returns>Returns list with basic data format {center_x, center_y, width, height, confidence, class_index}</returns>
        private List<YoloResult> InferenceInternal(Bitmap image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = 1)
        {
            ThrowIfDisposed();
            if (_inferenceSession == null) return new List<YoloResult>();

            var metrics = new InferenceMetrics();
            var sw = Stopwatch.StartNew();

            Bitmap? processedImage = null; // Track internally created bitmap for disposal

            try
            {
                // ==================== 预处理阶段 ====================
                // 确保 tensor buffer 已初始化并复用
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

                // ==================== 推理阶段 ====================
                Tensor<float> output0;
                Tensor<float> output1;
                List<YoloResult> filteredDataList;
                List<YoloResult> finalResult = new List<YoloResult>();

                if (_executionTaskMode == YoloTaskType.Classify)
                {
                    output0 = _inferenceSession.Run(container).First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    finalResult = FilterConfidence_Classify(output0, confidence);
                }
                else if (_executionTaskMode == YoloTaskType.Detect)
                {
                    output0 = _inferenceSession.Run(container).First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    if (_yoloVersion == 8)
                    {
                        filteredDataList = FilterConfidence_Yolo8_9_11_Detect(output0, confidence);
                    }
                    else if (_yoloVersion == 5)
                    {
                        filteredDataList = FilterConfidence_Yolo5_Detect(output0, confidence);
                    }
                    else
                    {
                        filteredDataList = FilterConfidence_Yolo6_Detect(output0, confidence);
                    }
                    finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                }
                else if (_executionTaskMode == YoloTaskType.SegmentDetectOnly || _executionTaskMode == YoloTaskType.SegmentWithMask)
                {
                    var resultData = _inferenceSession.Run(container);
                    output0 = resultData.First().AsTensor<float>();
                    output1 = resultData.ElementAtOrDefault(1)?.AsTensor<float>();
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
                    output0 = _inferenceSession.Run(container).First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    filteredDataList = FilterConfidence_Pose(output0, confidence);
                    finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                }
                else if (_executionTaskMode == YoloTaskType.Obb)
                {
                    output0 = _inferenceSession.Run(container).First().AsTensor<float>();
                    metrics.InferenceMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    filteredDataList = FilterConfidence_Obb(output0, confidence);
                    finalResult = NmsFilter(filteredDataList, iouThreshold, globalIou);
                }

                // ==================== 后处理阶段 ====================
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
        private Bitmap ResizeImage(Bitmap image)
        {
            float scaledImageWidth = _inferenceImageWidth;
            float scaledImageHeight = _inferenceImageHeight;
            if (scaledImageWidth > _tensorWidth || scaledImageHeight > _tensorHeight)
            {
                _scale = (_tensorWidth / scaledImageWidth) < (_tensorHeight / scaledImageHeight) ? (_tensorWidth / scaledImageWidth) : (_tensorHeight / scaledImageHeight);
                scaledImageWidth = scaledImageWidth * _scale;
                scaledImageHeight = scaledImageHeight * _scale;
            }
            Bitmap scaledImage = new Bitmap((int)scaledImageWidth, (int)scaledImageHeight);
            using (Graphics graphics = Graphics.FromImage(scaledImage))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Default;
                graphics.DrawImage(image, 0, 0, scaledImageWidth, scaledImageHeight);
            }
            return scaledImage;
        }

        /// <summary>
        /// Letterbox resize: scales image while preserving aspect ratio and pads to target size.
        /// Uses YOLO standard gray color (114, 114, 114) for padding.
        /// </summary>
        private Bitmap LetterboxResize(Bitmap image)
        {
            float scaleW = (float)_tensorWidth / _inferenceImageWidth;
            float scaleH = (float)_tensorHeight / _inferenceImageHeight;
            _scale = Math.Min(scaleW, scaleH);

            int newW = (int)(_inferenceImageWidth * _scale);
            int newH = (int)(_inferenceImageHeight * _scale);

            // Calculate padding to center the image
            _padLeft = (_tensorWidth - newW) / 2;
            _padTop = (_tensorHeight - newH) / 2;

            // Create letterboxed image with YOLO standard gray background
            Bitmap letterboxedImage = new Bitmap(_tensorWidth, _tensorHeight);
            using (Graphics graphics = Graphics.FromImage(letterboxedImage))
            {
                // Fill with YOLO standard gray (114, 114, 114)
                graphics.Clear(Color.FromArgb(LETTERBOX_FILL_COLOR_R, LETTERBOX_FILL_COLOR_G, LETTERBOX_FILL_COLOR_B));
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                graphics.DrawImage(image, _padLeft, _padTop, newW, newH);
            }
            return letterboxedImage;
        }
        /// <summary>
        /// Ensures tensor buffer is allocated and ready for use.
        /// </summary>
        private void EnsureTensorBuffer()
        {
            int requiredLength = _inputTensorInfo[1] * _inputTensorInfo[2] * _inputTensorInfo[3];
            if (!_tensorBufferInitialized || _tensorBuffer == null || _tensorBuffer.Length != requiredLength)
            {
                _tensorBuffer = new float[requiredLength];
                _tensorBufferInitialized = true;
            }
        }

        private void ImageToTensor_Parallel(Bitmap image, float[] buffer)
        {
            int height = image.Height;
            int width = image.Width;
            int channels = _inputTensorInfo[1];
            int tensorHeight = _inputTensorInfo[2];
            int tensorWidth = _inputTensorInfo[3];
            int channelSize = tensorHeight * tensorWidth;

            BitmapData imageData = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = imageData.Stride;
            IntPtr scan0 = imageData.Scan0;
            try
            {
                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        IntPtr pixel = IntPtr.Add(scan0, y * stride + x * 3);
                        // BGR -> RGB, channel-first layout
                        int baseIndex = y * tensorWidth + x;
                        buffer[2 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // B -> channel 2
                        pixel = IntPtr.Add(pixel, 1);
                        buffer[1 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // G -> channel 1
                        pixel = IntPtr.Add(pixel, 1);
                        buffer[0 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // R -> channel 0
                    }
                });
            }
            finally
            {
                image.UnlockBits(imageData);
            }
        }
        private void ImageToTensor_NoInterpolation(Bitmap image, float[] buffer)
        {
            int tensorHeight = _inputTensorInfo[2];
            int tensorWidth = _inputTensorInfo[3];
            int channelSize = tensorHeight * tensorWidth;

            BitmapData imageData = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = imageData.Stride;
            IntPtr scan0 = imageData.Scan0;

            float scaledImageWidth = _inferenceImageWidth;
            float scaledImageHeight = _inferenceImageHeight;
            if (scaledImageWidth > _tensorWidth || scaledImageHeight > _tensorHeight)
            {
                _scale = (_tensorWidth / scaledImageWidth) < (_tensorHeight / scaledImageHeight) ? (_tensorWidth / scaledImageWidth) : (_tensorHeight / scaledImageHeight);
                scaledImageWidth = scaledImageWidth * _scale;
                scaledImageHeight = scaledImageHeight * _scale;
            }

            float factor = 1 / _scale;
            for (int y = 0; y < (int)scaledImageHeight; y++)
            {
                for (int x = 0; x < (int)scaledImageWidth; x++)
                {
                    int xPos = (int)(x * factor);
                    int yPos = (int)(y * factor);
                    IntPtr pixel = IntPtr.Add(scan0, yPos * stride + xPos * 3);
                    // BGR -> RGB, channel-first layout
                    int baseIndex = y * tensorWidth + x;
                    buffer[2 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // B -> channel 2
                    pixel = IntPtr.Add(pixel, 1);
                    buffer[1 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // G -> channel 1
                    pixel = IntPtr.Add(pixel, 1);
                    buffer[0 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // R -> channel 0
                }
            }
            image.UnlockBits(imageData);
        }
        public byte[] BitmapToBytes(Bitmap image)
        {
            byte[] result = null;
            using (MemoryStream stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Bmp);
                result = stream.ToArray();
            }
            return result;
        }
        private List<YoloResult> FilterConfidence_Yolo8_11_Segment(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < data.Dimensions[1] - 4 - _segWidth; j++)
                    {
                        if (data[0, j + 4, i] >= confidence)
                        {
                            if (tempConfidence < data[0, j + 4, i])
                            {
                                tempConfidence = data[0, j + 4, i];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                        basicData[0] = data[0, 0, i];
                        basicData[1] = data[0, 1, i];
                        basicData[2] = data[0, 2, i];
                        basicData[3] = data[0, 3, i];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        for (int ii = 0; ii < _segWidth; ii++)
                        {
                            int pos = data.Dimensions[1] - _segWidth + ii;
                            mask.At<float>(0, ii) = data[0, pos, i];
                        }
                        temp.MaskData = mask;
                        temp.BasicData = basicData;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                float[] dataArray = data.ToArray();
                for (int i = 0; i < dataArray.Length; i += outputSize)
                {
                    tempConfidence = 0f;
                    index = -1;
                    for (int j = 0; j < outputSize - 4 - _segWidth; j++)
                    {
                        if (dataArray[i + 4 + j] > confidence)
                        {
                            if (tempConfidence < dataArray[i + 4 + j])
                            {
                                tempConfidence = dataArray[i + 4 + j];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                        basicData[0] = dataArray[i];
                        basicData[1] = dataArray[i + 1];
                        basicData[2] = dataArray[i + 2];
                        basicData[3] = dataArray[i + 3];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        for (int ii = 0; ii < _segWidth; ii++)
                        {
                            int pos = i + outputSize - _segWidth + ii;
                            mask.At<float>(0, ii) = dataArray[pos];
                        }
                        temp.MaskData = mask;
                        temp.BasicData = basicData;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }
        private List<YoloResult> FilterConfidenceGeneric(Tensor<float> data, float confidence, int boxOffset, bool hasObjectness)
        {
            // Determine loop limit decrement based on what existing methods used
            // V8/V6: _segWidth - _poseWidth
            // V5: _segWidth (assumes pose is 0 or handled elsewhere? we will use seg+pose to be safe if pose is 0)
            int extraDecrement = _segWidth + _poseWidth;

            bool isMidSize = data.Dimensions[1] < data.Dimensions[2];
            int dim1 = data.Dimensions[1]; // channels
            int dim2 = data.Dimensions[2]; // items

            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, dim2, i =>
                {
                    float maxScore = 0f;
                    int maxClassIndex = -1;

                    if (hasObjectness)
                    {
                        if (data[0, 4, i] < confidence) return;
                    }

                    int loopStart = hasObjectness ? 5 : boxOffset;
                    // boxOffset passed: V8=4, V6=5. hasObjectness: V5=true(pass 5).

                    // Loop over Classes
                    // Existing V8: j from 0 to dim1 - 4 - extra. Access j+4.
                    // Generic loop k from loopStart to dim1 - extra.

                    for (int k = loopStart; k < dim1 - extraDecrement; k++)
                    {
                        float score = data[0, k, i];
                        if (score >= confidence)
                        {
                            if (score > maxScore)
                            {
                                maxScore = score;
                                maxClassIndex = k - boxOffset; // Map back to 0-based class index relative to boxOffset??
                                // V8: j=index. class 0 is at 4. (4-4=0).
                                // V5: j=index. class 0 is at 5. (5-5=0). 
                                // V6: j=index. class 0 is at 5. (5-5=0).
                                // Wait, if I pass boxOffset=4 for V8, and loopStart=4. k=4 -> class 0. Correct.
                                // If I pass boxOffset=5 for V5, loopStart=5. k=5 -> class 0. Correct.
                            }
                        }
                    }

                    if (maxClassIndex != -1)
                    {
                        YoloResult temp = new YoloResult();
                        temp.CenterX = data[0, 0, i];
                        temp.CenterY = data[0, 1, i];
                        temp.Width = data[0, 2, i];
                        temp.Height = data[0, 3, i];
                        temp.Confidence = maxScore;
                        temp.ClassId = maxClassIndex;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList();
            }
            else
            {
                // Sequential (Flattened)
                List<YoloResult> resultList = new List<YoloResult>();
                float[] dataArray = data.ToArray();
                int channelCount = dim2; // In this case dim2 is separate channels?

                for (int i = 0; i < dataArray.Length; i += channelCount)
                {
                    float maxScore = 0f;
                    int maxClassIndex = -1;

                    if (hasObjectness)
                    {
                        if (dataArray[i + 4] < confidence) continue;
                    }

                    int loopStart = hasObjectness ? 5 : boxOffset;
                    // channels are at i + k.

                    for (int k = loopStart; k < channelCount - extraDecrement; k++)
                    {
                        float score = dataArray[i + k];
                        if (score >= confidence)
                        {
                            if (score > maxScore)
                            {
                                maxScore = score;
                                maxClassIndex = k - boxOffset;
                            }
                        }
                    }

                    if (maxClassIndex != -1)
                    {
                        YoloResult temp = new YoloResult();
                        temp.CenterX = dataArray[i];
                        temp.CenterY = dataArray[i + 1];
                        temp.Width = dataArray[i + 2];
                        temp.Height = dataArray[i + 3];
                        temp.Confidence = maxScore;
                        temp.ClassId = maxClassIndex;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }

        private List<YoloResult> FilterConfidence_Yolo8_9_11_Detect(Tensor<float> data, float confidence)
        {
            return FilterConfidenceGeneric(data, confidence, 4, false);
        }
        private List<YoloResult> FilterConfidence_Yolo5_Segment(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    if (data[0, 4, i] >= confidence)
                    {
                        for (int j = 0; j < data.Dimensions[1] - 5 - _segWidth; j++)
                        {
                            if (tempConfidence < data[0, j + 5, i])
                            {
                                tempConfidence = data[0, j + 5, i];
                                index = j;
                            }
                        }
                        if (index != -1)
                        {
                            float[] basicData = new float[BASIC_DATA_LENGTH];
                            YoloResult temp = new YoloResult();
                            Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                            basicData[0] = data[0, 0, i];
                            basicData[1] = data[0, 1, i];
                            basicData[2] = data[0, 2, i];
                            basicData[3] = data[0, 3, i];
                            basicData[4] = tempConfidence;
                            basicData[5] = index;
                            for (int ii = 0; ii < _segWidth; ii++)
                            {
                                int pos = data.Dimensions[1] - _segWidth + ii;
                                mask.At<float>(0, ii) = data[0, pos, i];
                            }
                            temp.MaskData = mask;
                            temp.BasicData = basicData;
                            resultBag.Add(temp);
                        }
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                float[] dataArray = data.ToArray();
                for (int i = 0; i < dataArray.Length; i += outputSize)
                {
                    if (dataArray[i + 4] >= confidence)
                    {
                        tempConfidence = 0f;
                        for (int j = 0; j < outputSize - 5 - _segWidth; j++)
                        {
                            if (tempConfidence < dataArray[i + 5 + j])
                            {
                                tempConfidence = dataArray[i + 5 + j];
                                index = j;
                            }
                        }
                        if (index != -1)
                        {
                            float[] basicData = new float[BASIC_DATA_LENGTH];
                            YoloResult temp = new YoloResult();
                            Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                            basicData[0] = dataArray[i];
                            basicData[1] = dataArray[i + 1];
                            basicData[2] = dataArray[i + 2];
                            basicData[3] = dataArray[i + 3];
                            basicData[4] = dataArray[i + 4];
                            basicData[5] = index;
                            for (int ii = 0; ii < _segWidth; ii++)
                            {
                                int pos = i + outputSize - _segWidth + ii;
                                mask.At<float>(0, ii) = dataArray[pos];
                            }
                            temp.BasicData = basicData;
                            temp.MaskData = mask;
                            resultList.Add(temp);
                        }
                    }
                }
                return resultList;
            }
        }
        private List<YoloResult> FilterConfidence_Yolo5_Detect(Tensor<float> data, float confidence)
        {
            return FilterConfidenceGeneric(data, confidence, 5, true);
        }
        private List<YoloResult> FilterConfidence_Yolo6_Detect(Tensor<float> data, float confidence)
        {
            return FilterConfidenceGeneric(data, confidence, 5, false);
        }
        private List<YoloResult> FilterConfidence_Classify(Tensor<float> data, float confidence)
        {
            List<YoloResult> resultList = new List<YoloResult>();
            for (int i = 0; i < data.Dimensions[1]; i++)
            {
                if (data[0, i] >= confidence)
                {
                    float[] filterInfo = new float[2];
                    YoloResult temp = new YoloResult();
                    // Class confidence
                    filterInfo[0] = data[0, i];
                    // Class index
                    filterInfo[1] = i;
                    temp.BasicData = filterInfo;
                    resultList.Add(temp);
                }
            }
            SortConfidence(resultList);
            return resultList;
        }
        private List<YoloResult> FilterConfidence_Pose(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < data.Dimensions[1] - 4 - _segWidth - _poseWidth; j++)
                    {
                        if (data[0, j + 4, i] >= confidence)
                        {
                            if (tempConfidence < data[0, j + 4, i])
                            {
                                tempConfidence = data[0, j + 4, i];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        basicData[0] = data[0, 0, i];
                        basicData[1] = data[0, 1, i];
                        basicData[2] = data[0, 2, i];
                        basicData[3] = data[0, 3, i];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        temp.BasicData = basicData;
                        int poseIndex = 0;
                        PosePoint[] keyPoints = new PosePoint[_poseWidth / 3];
                        for (int ii = 0; ii < _poseWidth; ii += 3)
                        {
                            PosePoint p1 = new PosePoint();
                            p1.X = data[0, 5 + ii, i];
                            p1.Y = data[0, 6 + ii, i];
                            p1.Score = data[0, 7 + ii, i];
                            keyPoints[poseIndex] = p1;
                            poseIndex++;
                        }
                        temp.KeyPoints = keyPoints;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                float[] dataArray = data.ToArray();
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                for (int i = 0; i < dataArray.Length; i += outputSize)
                {
                    tempConfidence = 0f;
                    index = -1;
                    for (int j = 0; j < outputSize - 4 - _poseWidth; j++)
                    {
                        if (dataArray[i + 4 + j] > confidence)
                        {
                            if (tempConfidence < dataArray[i + 4 + j])
                            {
                                tempConfidence = dataArray[i + 4 + j];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        basicData[0] = dataArray[i];
                        basicData[1] = dataArray[i + 1];
                        basicData[2] = dataArray[i + 2];
                        basicData[3] = dataArray[i + 3];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        temp.BasicData = basicData;
                        int poseIndex = 0;
                        PosePoint[] keyPoints = new PosePoint[_poseWidth / 3];
                        for (int ii = 0; ii < _poseWidth; ii += 3)
                        {
                            PosePoint p1 = new PosePoint();
                            p1.X = dataArray[i + 5 + ii];
                            p1.Y = dataArray[i + 6 + ii];
                            p1.Score = dataArray[i + 7 + ii];
                            keyPoints[poseIndex] = p1;
                            poseIndex++;
                        }
                        temp.KeyPoints = keyPoints;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }
        private List<YoloResult> FilterConfidence_Obb(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                int outputSize = data.Dimensions[1];
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < data.Dimensions[1] - 5; j++)
                    {
                        if (data[0, j + 4, i] >= confidence)
                        {
                            if (tempConfidence < data[0, j + 4, i])
                            {
                                tempConfidence = data[0, j + 4, i];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[7];
                        YoloResult temp = new YoloResult();
                        basicData[0] = data[0, 0, i];
                        basicData[1] = data[0, 1, i];
                        basicData[2] = data[0, 2, i];
                        basicData[3] = data[0, 3, i];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        basicData[6] = data[0, outputSize - 1, i];
                        temp.BasicData = basicData;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                float[] dataArray = data.ToArray();
                for (int i = 0; i < dataArray.Length; i += outputSize)
                {
                    tempConfidence = 0f;
                    index = -1;
                    for (int j = 0; j < outputSize - 5; j++)
                    {
                        if (dataArray[i + 4 + j] > confidence)
                        {
                            if (tempConfidence < dataArray[i + 4 + j])
                            {
                                tempConfidence = dataArray[i + 4 + j];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[7];
                        YoloResult temp = new YoloResult();
                        basicData[0] = dataArray[i];
                        basicData[1] = dataArray[i + 1];
                        basicData[2] = dataArray[i + 2];
                        basicData[3] = dataArray[i + 3];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        basicData[6] = dataArray[i + outputSize - 1];
                        temp.BasicData = basicData;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }
        private int DetermineModelVersion(int version)
        {
            if (_taskType == "classify")
            {
                return 5;
            }
            if (version >= 8)
            {
                return 8;
            }
            else if (version < 8 && version >= 5)
            {
                return version;
            }
            if (_modelVersion != "")
            {
                int ver = int.Parse(_modelVersion.Split('.')[0]);
                return ver;
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
        private List<YoloResult> NmsFilter(List<YoloResult> initialFilterList, float iouThreshold, bool globalIou)
        {
            if (initialFilterList.Count == 0)
                return new List<YoloResult>();

            // 先按置信度排序
            SortConfidence(initialFilterList);

            if (globalIou)
            {
                // 全局IoU模式：所有类别一起做NMS
                return NmsFilterGlobal(initialFilterList, iouThreshold);
            }
            else
            {
                // 按类别分组并行处理
                return NmsFilterByClass(initialFilterList, iouThreshold);
            }
        }

        /// <summary>
        /// NMS by class: groups detections by class and processes each group in parallel.
        /// </summary>
        private List<YoloResult> NmsFilterByClass(List<YoloResult> sortedList, float iouThreshold)
        {
            // Group by class index
            var groups = sortedList.GroupBy(r => r.ClassId);

            ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();

            Parallel.ForEach(groups, group =>
            {
                var groupList = group.ToList();
                var nmsResults = NmsFilterSingleGroup(groupList, iouThreshold);
                foreach (var result in nmsResults)
                {
                    resultBag.Add(result);
                }
            });

            return resultBag.ToList();
        }

        /// <summary>
        /// Performs NMS on a single group of detections (same class).
        /// Input should already be sorted by confidence in descending order.
        /// </summary>
        private List<YoloResult> NmsFilterSingleGroup(List<YoloResult> sortedGroup, float iouThreshold)
        {
            if (sortedGroup.Count == 0)
                return new List<YoloResult>();

            List<YoloResult> kept = new List<YoloResult>();
            bool[] suppressed = new bool[sortedGroup.Count];

            for (int i = 0; i < sortedGroup.Count; i++)
            {
                if (suppressed[i])
                    continue;

                // Keep this detection (highest remaining confidence)
                kept.Add(sortedGroup[i]);

                // Suppress all detections with IoU > threshold
                for (int j = i + 1; j < sortedGroup.Count; j++)
                {
                    if (suppressed[j])
                        continue;

                    float iou = CalculateIntersectionOverUnion(sortedGroup[i], sortedGroup[j]);
                    if (iou > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }

            return kept;
        }

        /// <summary>
        /// Global NMS: all classes are treated together, any overlapping boxes are suppressed.
        /// </summary>
        private List<YoloResult> NmsFilterGlobal(List<YoloResult> sortedList, float iouThreshold)
        {
            if (sortedList.Count == 0)
                return new List<YoloResult>();

            List<YoloResult> kept = new List<YoloResult>();
            bool[] suppressed = new bool[sortedList.Count];

            for (int i = 0; i < sortedList.Count; i++)
            {
                if (suppressed[i])
                    continue;

                // Keep this detection
                kept.Add(sortedList[i]);

                // Suppress all detections with IoU > threshold (regardless of class)
                for (int j = i + 1; j < sortedList.Count; j++)
                {
                    if (suppressed[j])
                        continue;

                    float iou = CalculateIntersectionOverUnion(sortedList[i], sortedList[j]);
                    if (iou > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }

            return kept;
        }
        private float CalculateIntersectionOverUnion(YoloResult box1, YoloResult box2)
        {
            float width1 = box1.Width;
            float height1 = box1.Height;
            float width2 = box2.Width;
            float height2 = box2.Height;

            float x1_min = box1.CenterX - width1 / 2;
            float y1_min = box1.CenterY - height1 / 2;
            float x1_max = box1.CenterX + width1 / 2;
            float y1_max = box1.CenterY + height1 / 2;

            float x2_min = box2.CenterX - width2 / 2;
            float y2_min = box2.CenterY - height2 / 2;
            float x2_max = box2.CenterX + width2 / 2;
            float y2_max = box2.CenterY + height2 / 2;

            float intersectionArea, unionArea;
            float left = Math.Max(x1_min, x2_min);
            float top = Math.Max(y1_min, y2_min);
            float right = Math.Min(x1_max, x2_max);
            float bottom = Math.Min(y1_max, y2_max);

            if (left < right && top < bottom)
            {
                intersectionArea = (right - left) * (bottom - top);
            }
            else
            {
                intersectionArea = 0;
            }
            float area1 = width1 * height1;
            float area2 = width2 * height2;
            unionArea = area1 + area2 - intersectionArea;
            return intersectionArea / unionArea;
        }
        private void SortConfidence(List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                dataList.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            }
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
        /// <summary>
        /// Get the preset classification label name from the model
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public string GetLabelNameByIndex(int index)
        {
            if (index > Labels.Length || index < 0)
            {
                return "";
            }
            return Labels[index];
        }
        private void RestoreCoordinates(ref List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                // Updated to use strong properties
                for (int i = 0; i < dataList.Count; i++)
                {
                    dataList[i].CenterX = (dataList[i].CenterX - _padLeft) / _scale;
                    dataList[i].CenterY = (dataList[i].CenterY - _padTop) / _scale;
                    dataList[i].Width /= _scale;
                    dataList[i].Height /= _scale;
                }

                if (dataList[0].KeyPoints != null)
                {
                    for (int i = 0; i < dataList.Count; i++)
                    {
                        if (dataList[i].KeyPoints == null) continue;
                        for (int j = 0; j < dataList[i].KeyPoints.Length; j++)
                        {
                            // Subtract padding offset first, then scale back to original size
                            dataList[i].KeyPoints[j].X = (dataList[i].KeyPoints[j].X - _padLeft) / _scale;
                            dataList[i].KeyPoints[j].Y = (dataList[i].KeyPoints[j].Y - _padTop) / _scale;
                        }
                    }
                }
            }
        }
        private void RestoreDrawingCoordinates(ref List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    dataList[i].CenterX -= dataList[i].Width / 2;
                    dataList[i].CenterY -= dataList[i].Height / 2;
                }
            }
        }
        private void RestoreCenterCoordinates(ref List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    dataList[i].CenterX += dataList[i].Width / 2;
                    dataList[i].CenterY += dataList[i].Height / 2;
                }
            }
        }
        private void RemoveOutOfBoundsCoordinates(ref List<YoloResult> dataList)
        {
            // Remove in reverse order
            for (int i = dataList.Count - 1; i >= 0; i--)
            {
                if (dataList[i].CenterX > _inferenceImageWidth ||
                    dataList[i].CenterY > _inferenceImageHeight ||
                    dataList[i].Width > _inferenceImageWidth ||
                    dataList[i].Height > _inferenceImageHeight)
                {
                    dataList.RemoveAt(i);
                }
            }
        }
        private void RestoreMask(ref List<YoloResult> data, Tensor<float>? output1)
        {
            if (output1 == null) return;
            if (_outputTensorInfo2_Segment == null || _outputTensorInfo2_Segment.Length < 4) return;
            var output1Array = output1.ToArray();
            if (output1Array == null || output1Array.Length == 0) return;
            Mat ot1 = new Mat(_segWidth, _outputTensorInfo2_Segment[2] * _outputTensorInfo2_Segment[3], MatType.CV_32F, output1Array);
            for (int i = 0; i < data.Count; i++)
            {
                var currentMask = data[i].MaskData;
                if (currentMask == null || currentMask.Empty()) continue;
                Mat originalMask = currentMask * ot1;
                Parallel.For(0, originalMask.Cols, col =>
                {
                    originalMask.At<float>(0, col) = Sigmoid(originalMask.At<float>(0, col));
                });
                Mat reshapedMask = originalMask.Reshape(1, _outputTensorInfo2_Segment[2], _outputTensorInfo2_Segment[3]);
                int maskX1 = Math.Abs((int)((data[i].CenterX - data[i].Width / 2) * _maskScaleW));
                int maskY1 = Math.Abs((int)((data[i].CenterY - data[i].Height / 2) * _maskScaleH));
                int maskX2 = (int)(data[i].Width * _maskScaleW);
                int maskY2 = (int)(data[i].Height * _maskScaleH);
                if (maskX2 + maskX1 > _outputTensorInfo2_Segment[3]) maskX2 = _outputTensorInfo2_Segment[3] - maskX1;
                if (maskY1 + maskY2 > _outputTensorInfo2_Segment[2]) maskY2 = _outputTensorInfo2_Segment[2] - maskY1;
                Rect region = new Rect(maskX1, maskY1, maskX2, maskY2);
                Mat cropped = new Mat(reshapedMask, region);
                Mat restoredMask = new Mat();
                int enlargedWidth = (int)(cropped.Width / _maskScaleW / _scale);
                int enlargedHeight = (int)(cropped.Height / _maskScaleH / _scale);
                Cv2.Resize(cropped, restoredMask, new OpenCvSharp.Size(enlargedWidth, enlargedHeight));
                Cv2.Threshold(restoredMask, restoredMask, 0.5, 1, ThresholdTypes.Binary);
                data[i].MaskData = restoredMask;
            }
        }
        private float Sigmoid(float value)
        {
            return 1 / (1 + (float)Math.Exp(-value));
        }
        /// <returns>Returns ObbRectangle structure, representing logic of four points</returns>
        public ObbRectangle ConvertObbCoordinates(YoloResult data)
        {
            float x = data.BasicData[0];
            float y = data.BasicData[1];
            float w = data.BasicData[2];
            float h = data.BasicData[3];
            float r = data.BasicData[6];
            float cos_value = (float)Math.Cos(r);
            float sin_value = (float)Math.Sin(r);
            float[] vec1 = { w / 2 * cos_value, w / 2 * sin_value };
            float[] vec2 = { -h / 2 * sin_value, h / 2 * cos_value };
            ObbRectangle obbRectangle = new ObbRectangle();
            obbRectangle.pt1 = new PointF(x + vec1[0] + vec2[0], y + vec1[1] + vec2[1]);
            obbRectangle.pt2 = new PointF(x + vec1[0] - vec2[0], y + vec1[1] - vec2[1]);
            obbRectangle.pt3 = new PointF(x - vec1[0] - vec2[0], y - vec1[1] - vec2[1]);
            obbRectangle.pt4 = new PointF(x - vec1[0] + vec2[0], y - vec1[1] + vec2[1]);
            return obbRectangle;
        }
        /// <summary>
        /// Draw inference results on original image
        /// </summary>
        /// <param name="image">Original image</param>
        /// <param name="results">List of results</param>
        /// <param name="labels">Labels array</param>
        public Image GenerateImage(Image image, List<YoloResult> results, string[] labels, Pen? borderPen = null, Font? font = null, SolidBrush? textColorBrush = null, SolidBrush? textBackgroundBrush = null, bool randomMaskColor = true, Color[]? specifiedMaskColors = null, Color? nonMaskBackgroundColor = null, int classificationLimit = 5, float keyPointConfidenceThreshold = 0.5f)
        {
            Bitmap returnImage = new Bitmap(image.Width, image.Height);

            // Track resources created internally that need disposal
            Pen? ownedBorderPen = null;
            Font? ownedFont = null;
            SolidBrush? ownedTextColorBrush = null;
            SolidBrush? ownedTextBackgroundBrush = null;

            try
            {
                // Create default resources if not provided (and track them for disposal)
                if (borderPen == null)
                {
                    int penWidth = (image.Width > image.Height ? image.Height : image.Width) / 235;
                    ownedBorderPen = new Pen(Color.BlueViolet, penWidth);
                    borderPen = ownedBorderPen;
                }
                if (font == null)
                {
                    int fontWidth = (image.Width > image.Height ? image.Height : image.Width) / 90;
                    ownedFont = new Font("SimSun", fontWidth, FontStyle.Bold);
                    font = ownedFont;
                }
                if (textColorBrush == null)
                {
                    ownedTextColorBrush = new SolidBrush(Color.Black);
                    textColorBrush = ownedTextColorBrush;
                }
                if (textBackgroundBrush == null)
                {
                    ownedTextBackgroundBrush = new SolidBrush(Color.Orange);
                    textBackgroundBrush = ownedTextBackgroundBrush;
                }

                using (Graphics g = Graphics.FromImage(returnImage))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    float textWidth;
                    float textHeight;
                    g.DrawImage(image, 0, 0, image.Width, image.Height);
                    string textContent;

                    // Classify
                    if (_executionTaskMode == YoloTaskType.Classify)
                    {
                        RestoreDrawingCoordinates(ref results);
                        float xPos = 10;
                        float yPos = 10;
                        for (int i = 0; i < results.Count; i++)
                        {
                            if (i >= classificationLimit) break;
                            int labelIndex = (int)results[i].BasicData[1];
                            string confidence = results[i].BasicData[0].ToString("_0.00");
                            string labelName;
                            if (labelIndex + 1 > labels.Length)
                            {
                                labelName = "No Label Name";
                            }
                            else
                            {
                                labelName = labels[labelIndex];
                            }
                            textContent = labelName + confidence;
                            textWidth = g.MeasureString(textContent + "_0.00", font).Width;
                            textHeight = g.MeasureString(textContent + "_0.00", font).Height;
                            g.FillRectangle(textBackgroundBrush, xPos, yPos, textWidth * 0.8f, textHeight);
                            g.DrawString(textContent, font, textColorBrush, new PointF(xPos, yPos));
                            yPos += textHeight;
                        }
                        RestoreCenterCoordinates(ref results);
                    }

                    // Draw Mask
                    if (_executionTaskMode == YoloTaskType.SegmentDetectOnly || _executionTaskMode == YoloTaskType.SegmentWithMask)
                    {
                        RestoreDrawingCoordinates(ref results);
                        if (nonMaskBackgroundColor != null)
                        {
                            using (Bitmap bgImage = new Bitmap(image.Width, image.Height))
                            {
                                using (Graphics bgGraphics = Graphics.FromImage(bgImage))
                                {
                                    bgGraphics.Clear((Color)nonMaskBackgroundColor);
                                }
                                g.DrawImage(bgImage, PointF.Empty);
                            }
                        }
                        for (int i = 0; i < results.Count; i++)
                        {
                            Rectangle rect = new Rectangle((int)results[i].BasicData[0], (int)results[i].BasicData[1], (int)results[i].BasicData[2], (int)results[i].BasicData[3]);
                            Color color;
                            if (specifiedMaskColors == null)
                            {
                                if (randomMaskColor)
                                {
                                    Random R = new Random();
                                    color = Color.FromArgb(180, R.Next(0, 255), R.Next(0, 255), R.Next(0, 255));
                                }
                                else
                                {
                                    color = Color.FromArgb(180, 0, 255, 0);
                                }
                            }
                            else
                            {
                                if (results[i].ClassId + 1 > specifiedMaskColors.Length)
                                {
                                    color = Color.FromArgb(180, 0, 255, 0);
                                }
                                else
                                {
                                    color = specifiedMaskColors[results[i].ClassId];
                                }
                            }
                            var maskData = results[i].MaskData;
                            if (maskData != null && !maskData.Empty())
                            {
                                using (Bitmap mask = GenerateMaskImageParallel(maskData, color))
                                {
                                    g.DrawImage(mask, rect);
                                }
                            }
                        }
                        RestoreCenterCoordinates(ref results);
                    }

                    if (_executionTaskMode == YoloTaskType.Detect || _executionTaskMode == YoloTaskType.SegmentWithMask || _executionTaskMode == YoloTaskType.PoseWithKeypoints)
                    {
                        RestoreDrawingCoordinates(ref results);
                        for (int i = 0; i < results.Count; i++)
                        {
                            string confidence = results[i].BasicData[4].ToString("_0.00");
                            if ((int)results[i].BasicData[5] + 1 > labels.Length)
                            {
                                textContent = confidence;
                            }
                            else
                            {
                                textContent = labels[(int)results[i].BasicData[5]] + confidence;
                            }
                            textWidth = g.MeasureString(textContent + "_0.00", font).Width;
                            textHeight = g.MeasureString(textContent + "_0.00", font).Height;
                            Rectangle rect = new Rectangle((int)results[i].BasicData[0], (int)results[i].BasicData[1], (int)results[i].BasicData[2], (int)results[i].BasicData[3]);
                            g.DrawRectangle(borderPen, rect);
                            g.FillRectangle(textBackgroundBrush, results[i].BasicData[0] - borderPen.Width / 2 - 1, results[i].BasicData[1] - textHeight - borderPen.Width / 2 - 1, textWidth * 0.8f, textHeight);
                            g.DrawString(textContent, font, textColorBrush, results[i].BasicData[0] - borderPen.Width / 2 - 1, results[i].BasicData[1] - textHeight - borderPen.Width / 2 - 1);
                        }
                        RestoreCenterCoordinates(ref results);
                    }

                    if (_executionTaskMode == YoloTaskType.PoseDetectOnly || _executionTaskMode == YoloTaskType.PoseWithKeypoints)
                    {
                        RestoreDrawingCoordinates(ref results);
                        if (results.Count > 0 && results[0].KeyPoints.Length == 17)
                        {
                            Color[] colorGroup = new Color[]
                            {
                                Color.Yellow,
                                Color.LawnGreen,
                                Color.LawnGreen,
                                Color.SpringGreen,
                                Color.SpringGreen,
                                Color.Blue,
                                Color.Blue,
                                Color.Firebrick,
                                Color.Firebrick,
                                Color.Firebrick,
                                Color.Firebrick,
                                Color.Blue,
                                Color.Blue,
                                Color.Orange,
                                Color.Orange,
                                Color.Orange,
                                Color.Orange
                            };
                            int dotRadius = (image.Width > image.Height ? image.Height : image.Width) / 100;
                            int lineWidth = (image.Width > image.Height ? image.Height : image.Width) / 150;

                            for (int i = 0; i < results.Count; i++)
                            {
                                using (Pen lineStyle0 = new Pen(colorGroup[0], lineWidth))
                                using (Pen lineStyle5 = new Pen(colorGroup[5], lineWidth))
                                using (Pen lineStyle1 = new Pen(colorGroup[1], lineWidth))
                                {
                                    PointF shoulderCenter = new PointF((results[i].KeyPoints[5].X + results[i].KeyPoints[6].X) / 2 + dotRadius, (results[i].KeyPoints[5].Y + results[i].KeyPoints[6].Y) / 2 + dotRadius);
                                    if (results[i].KeyPoints[0].Score > keyPointConfidenceThreshold && results[i].KeyPoints[5].Score > keyPointConfidenceThreshold && results[i].KeyPoints[6].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle0, new PointF(results[i].KeyPoints[0].X + dotRadius, results[i].KeyPoints[0].Y + dotRadius), shoulderCenter);
                                    if (results[i].KeyPoints[5].Score > keyPointConfidenceThreshold && results[i].KeyPoints[6].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[5].X + dotRadius, results[i].KeyPoints[5].Y + dotRadius), new PointF(results[i].KeyPoints[6].X + dotRadius, results[i].KeyPoints[6].Y + dotRadius));
                                    if (results[i].KeyPoints[11].Score > keyPointConfidenceThreshold && results[i].KeyPoints[12].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[11].X + dotRadius, results[i].KeyPoints[11].Y + dotRadius), new PointF(results[i].KeyPoints[12].X + dotRadius, results[i].KeyPoints[12].Y + dotRadius));
                                    if (results[i].KeyPoints[5].Score > keyPointConfidenceThreshold && results[i].KeyPoints[11].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[5].X + dotRadius, results[i].KeyPoints[5].Y + dotRadius), new PointF(results[i].KeyPoints[11].X + dotRadius, results[i].KeyPoints[11].Y + dotRadius));
                                    if (results[i].KeyPoints[6].Score > keyPointConfidenceThreshold && results[i].KeyPoints[12].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[6].X + dotRadius, results[i].KeyPoints[6].Y + dotRadius), new PointF(results[i].KeyPoints[12].X + dotRadius, results[i].KeyPoints[12].Y + dotRadius));
                                    if (results[i].KeyPoints[0].Score > keyPointConfidenceThreshold && results[i].KeyPoints[1].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle0, new PointF(results[i].KeyPoints[0].X + dotRadius, results[i].KeyPoints[0].Y + dotRadius), new PointF(results[i].KeyPoints[1].X + dotRadius, results[i].KeyPoints[1].Y + dotRadius));
                                    if (results[i].KeyPoints[0].Score > keyPointConfidenceThreshold && results[i].KeyPoints[2].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle0, new PointF(results[i].KeyPoints[0].X + dotRadius, results[i].KeyPoints[0].Y + dotRadius), new PointF(results[i].KeyPoints[2].X + dotRadius, results[i].KeyPoints[2].Y + dotRadius));
                                    if (results[i].KeyPoints[1].Score > keyPointConfidenceThreshold && results[i].KeyPoints[3].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle1, new PointF(results[i].KeyPoints[1].X + dotRadius, results[i].KeyPoints[1].Y + dotRadius), new PointF(results[i].KeyPoints[3].X + dotRadius, results[i].KeyPoints[3].Y + dotRadius));
                                    if (results[i].KeyPoints[2].Score > keyPointConfidenceThreshold && results[i].KeyPoints[4].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle1, new PointF(results[i].KeyPoints[2].X + dotRadius, results[i].KeyPoints[2].Y + dotRadius), new PointF(results[i].KeyPoints[4].X + dotRadius, results[i].KeyPoints[4].Y + dotRadius));

                                    for (int j = 5; j < results[i].KeyPoints.Length - 2; j++)
                                    {
                                        if (results[i].KeyPoints[j].Score > keyPointConfidenceThreshold && results[i].KeyPoints[j + 2].Score > keyPointConfidenceThreshold)
                                        {
                                            if (j != 9 && j != 10)
                                            {
                                                using (Pen lineStyleJ = new Pen(colorGroup[j + 2], lineWidth))
                                                {
                                                    g.DrawLine(lineStyleJ, new PointF(results[i].KeyPoints[j].X + dotRadius, results[i].KeyPoints[j].Y + dotRadius), new PointF(results[i].KeyPoints[j + 2].X + dotRadius, results[i].KeyPoints[j + 2].Y + dotRadius));
                                                }
                                            }
                                        }
                                    }
                                    for (int j = 0; j < results[i].KeyPoints.Length; j++)
                                    {
                                        if (results[i].KeyPoints[j].Score > keyPointConfidenceThreshold)
                                        {
                                            Rectangle position = new Rectangle((int)results[i].KeyPoints[j].X, (int)results[i].KeyPoints[j].Y, dotRadius * 2, dotRadius * 2);
                                            using (SolidBrush dotBrush = new SolidBrush(colorGroup[j]))
                                            {
                                                g.FillEllipse(dotBrush, position);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (results.Count > 0)
                        {
                            Color[] colorGroup = new Color[]
                            {
                                Color.Yellow,
                                Color.Red,
                                Color.SpringGreen,
                                Color.Blue,
                                Color.Firebrick,
                                Color.Blue,
                                Color.Orange,
                                Color.Beige,
                                Color.LightGreen,
                                Color.DarkGreen,
                                Color.Magenta,
                                Color.White,
                                Color.OrangeRed,
                                Color.Orchid,
                                Color.PaleGoldenrod,
                                Color.PaleGreen,
                                Color.PaleTurquoise,
                                Color.PaleVioletRed,
                                Color.PaleGreen,
                                Color.PaleTurquoise,
                            };
                            int dotRadius = (image.Width > image.Height ? image.Height : image.Width) / 100;
                            foreach (var item in results)
                            {
                                for (int i = 0; i < item.KeyPoints.Length; i++)
                                {
                                    if (item.KeyPoints[i].Score > keyPointConfidenceThreshold)
                                    {
                                        Rectangle position = new Rectangle((int)item.KeyPoints[i].X, (int)item.KeyPoints[i].Y, dotRadius * 2, dotRadius * 2);
                                        using (SolidBrush dotBrush = new SolidBrush(i > 20 ? Color.SaddleBrown : colorGroup[i]))
                                        {
                                            g.FillEllipse(dotBrush, position);
                                        }
                                    }
                                }
                            }
                        }
                        RestoreCenterCoordinates(ref results);
                    }

                    if (_executionTaskMode == YoloTaskType.Obb)
                    {
                        for (int i = 0; i < results.Count; i++)
                        {
                            string confidence = results[i].BasicData[4].ToString("_0.00");
                            if ((int)results[i].BasicData[5] + 1 > labels.Length)
                            {
                                textContent = confidence;
                            }
                            else
                            {
                                textContent = labels[(int)results[i].BasicData[5]] + confidence;
                            }
                            textWidth = g.MeasureString(textContent + "_0.00", font).Width;
                            textHeight = g.MeasureString(textContent + "_0.00", font).Height;
                            ObbRectangle obb = ConvertObbCoordinates(results[i]);
                            PointF[] pf = { obb.pt1, obb.pt2, obb.pt3, obb.pt4, obb.pt1 };
                            g.DrawLines(borderPen, pf);
                            PointF bottomRight = pf[0];
                            foreach (var point in pf)
                            {
                                if (point.X >= bottomRight.X && point.Y >= bottomRight.Y)
                                {
                                    bottomRight = point;
                                }
                            }
                            g.FillRectangle(textBackgroundBrush, bottomRight.X - borderPen.Width / 2 - 1, bottomRight.Y + borderPen.Width / 2 - 1, textWidth * 0.8f, textHeight);
                            g.DrawString(textContent, font, textColorBrush, bottomRight.X - borderPen.Width / 2 - 1, bottomRight.Y + borderPen.Width / 2 - 1);
                        }
                    }
                } // Graphics disposed here
            }
            finally
            {
                // Dispose only the resources we created internally
                ownedBorderPen?.Dispose();
                ownedFont?.Dispose();
                ownedTextColorBrush?.Dispose();
                ownedTextBackgroundBrush?.Dispose();
            }

            return returnImage;
        }
        private Bitmap GenerateMaskImageParallel(Mat matData, Color color)
        {
            Bitmap maskImage = new Bitmap(matData.Width, matData.Height, PixelFormat.Format32bppArgb);
            BitmapData maskImageData = maskImage.LockBits(new Rectangle(0, 0, maskImage.Width, maskImage.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int height = maskImage.Height;
            int width = maskImage.Width;
            Parallel.For(0, height, i =>
            {
                for (int j = 0; j < width; j++)
                {
                    if (matData.At<float>(i, j) == 1)
                    {
                        IntPtr startPixel = IntPtr.Add(maskImageData.Scan0, i * maskImageData.Stride + j * 4);
                        byte[] colorInfo = new byte[] { color.B, color.G, color.R, color.A };
                        Marshal.Copy(colorInfo, 0, startPixel, 4);
                    }
                }
            });
            maskImage.UnlockBits(maskImageData);
            return maskImage;
        }
        /// <summary>
        /// Use this method to release resources after use
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases managed and unmanaged resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // 释放托管资源
                _inferenceSession?.Dispose();
                _inferenceSemaphore?.Dispose();
                _inputTensor = null;
                _tensorBuffer = null;
                _tensorBufferInitialized = false;
            }

            _disposed = true;
        }

        /// <summary>
        /// Finalizer
        /// </summary>
        ~YoloDetector()
        {
            Dispose(false);
        }

        /// <summary>
        /// Throws ObjectDisposedException if this object has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(YoloDetector));
            }
        }
    }
    public class YoloResult : IDisposable
    {
        // 基础检测数据 - 强类型属性
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Confidence { get; set; }
        public int ClassId { get; set; }

        // OBB 旋转角度（仅 OBB 任务使用）
        public float? Angle { get; set; }

        // 兼容属性 - 保持向后兼容
        public float[] BasicData
        {
            get => Angle.HasValue
                ? new float[] { CenterX, CenterY, Width, Height, Confidence, ClassId, Angle.Value }
                : new float[] { CenterX, CenterY, Width, Height, Confidence, ClassId };
            set
            {
                if (value.Length >= 6)
                {
                    CenterX = value[0];
                    CenterY = value[1];
                    Width = value[2];
                    Height = value[3];
                    Confidence = value[4];
                    ClassId = (int)value[5];
                    if (value.Length >= 7) Angle = value[6];
                }
            }
        }

        // 计算属性
        public float Left => CenterX - Width / 2;
        public float Top => CenterY - Height / 2;
        public float Right => CenterX + Width / 2;
        public float Bottom => CenterY + Height / 2;
        public RectangleF BoundingBox => new RectangleF(Left, Top, Width, Height);
        public float Area => Width * Height;

        // 分割和姿态数据
        public Mat? MaskData { get; set; }
        public PosePoint[] KeyPoints { get; set; } = Array.Empty<PosePoint>();

        // IDisposable 实现
        private bool _disposed = false;

        public void Dispose()
        {
            if (_disposed) return;
            MaskData?.Dispose();
            MaskData = null;
            _disposed = true;
        }
    }
    public class PosePoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Score { get; set; }
    }
    class KalmanFilterTracker
    {
        KalmanFilter kalman = new KalmanFilter(4, 2, 0);
        KalmanFilterTracker()
        {
            kalman.MeasurementMatrix = new Mat(2, 4, MatType.CV_32F, new float[]
                      {
            1, 0, 0, 0,
            0, 1, 0, 0
                      });
            kalman.TransitionMatrix = new Mat(4, 4, MatType.CV_32F, new float[]
            {
            1, 0, 1, 0,
            0, 1, 0, 1,
            0, 0, 1, 0,
            0, 0, 0, 1
            });
            kalman.ControlMatrix = new Mat(4, 2, MatType.CV_32F, new float[]
            {
            0, 0,
            0, 0,
            1, 0,
            0, 1
            });
            kalman.ProcessNoiseCov = new Mat(4, 4, MatType.CV_32F, new float[]
            {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
            });
            kalman.MeasurementNoiseCov = new Mat(2, 2, MatType.CV_32F, new float[]
            {
            1, 0,
            0, 1
            });
        }
        public PointF PredictNextPosition()
        {
            Mat prediction = kalman.Predict();
            PointF result = new PointF(prediction.At<float>(0), prediction.At<float>(1));
            return result;
        }
        public void UpdateCorrectCoordinates(PointF correctedPoint)
        {
            Mat correction = new Mat(2, 1, MatType.CV_32F, new float[] { correctedPoint.X, correctedPoint.Y });
            kalman.Correct(correction);
        }
    }
    public struct ObbRectangle
    {
        public PointF pt1;
        public PointF pt2;
        public PointF pt3;
        public PointF pt4;
    }

    /// <summary>
    /// 推理性能指标类，用于测量各阶段耗时
    /// </summary>
    public class InferenceMetrics
    {
        /// <summary>
        /// 预处理耗时（毫秒）：包括图像缩放、tensor转换
        /// </summary>
        public double PreprocessMs { get; set; }

        /// <summary>
        /// 推理耗时（毫秒）：ONNX Runtime 模型执行时间
        /// </summary>
        public double InferenceMs { get; set; }

        /// <summary>
        /// 后处理耗时（毫秒）：包括置信度过滤、NMS、坐标恢复
        /// </summary>
        public double PostprocessMs { get; set; }

        /// <summary>
        /// 总耗时（毫秒）
        /// </summary>
        public double TotalMs => PreprocessMs + InferenceMs + PostprocessMs;

        /// <summary>
        /// 推理帧率 (FPS)
        /// </summary>
        public double FPS => TotalMs > 0 ? 1000.0 / TotalMs : 0;

        /// <summary>
        /// 检测到的目标数量
        /// </summary>
        public int DetectionCount { get; set; }

        public override string ToString() =>
            $"Preprocess: {PreprocessMs:F2}ms, Inference: {InferenceMs:F2}ms, " +
            $"Postprocess: {PostprocessMs:F2}ms, Total: {TotalMs:F2}ms, FPS: {FPS:F1}, Detections: {DetectionCount}";
    }
}