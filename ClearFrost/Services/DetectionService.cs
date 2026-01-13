// ============================================================================
// 文件名: DetectionService.cs
// 描述:   检测服务实现
//
// 功能:
//   - 封装 YOLO 推理逻辑
//   - 多模型管理和自动切换
//   - 检测结果生成
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using YOLO.Interfaces;
using YoloDetection;

namespace YOLO.Services
{
    /// <summary>
    /// 检测服务实现
    /// </summary>
    public class DetectionService : IDetectionService
    {
        #region 私有字段

        private YoloDetector? _yolo;
        private MultiModelManager? _modelManager;
        private readonly bool _useGpu;
        private readonly List<string> _availableModels = new List<string>();
        private string _currentModelName = "未加载";
        private bool _disposed;

        #endregion

        #region 事件

        public event Action<DetectionResultData>? DetectionCompleted;
        public event Action<string>? ModelLoaded;
        public event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        public bool IsModelLoaded => _modelManager?.IsPrimaryLoaded ?? _yolo != null;
        public string CurrentModelName => _currentModelName;
        public IReadOnlyList<string> AvailableModels => _availableModels.AsReadOnly();
        public long LastInferenceMs { get; private set; }

        #endregion

        #region 构造函数

        public DetectionService(bool useGpu = true)
        {
            _useGpu = useGpu;
        }

        #endregion

        #region 模型管理

        public async Task<bool> LoadModelAsync(string modelPath, bool useGpu)
        {
            try
            {
                if (!File.Exists(modelPath))
                {
                    ErrorOccurred?.Invoke($"模型文件不存在: {modelPath}");
                    return false;
                }

                await Task.Run(() =>
                {
                    _yolo = new YoloDetector(modelPath, 0, 0, useGpu);
                });

                string modelName = Path.GetFileNameWithoutExtension(modelPath);
                if (!_availableModels.Contains(modelName))
                {
                    _availableModels.Add(modelName);
                }

                _currentModelName = modelName;
                ModelLoaded?.Invoke(modelName);
                Debug.WriteLine($"[DetectionService] 模型已加载: {modelName}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"加载模型失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu)
        {
            try
            {
                if (!Directory.Exists(modelsDirectory))
                {
                    Debug.WriteLine($"[DetectionService] 模型目录不存在: {modelsDirectory}");
                    return false;
                }

                var modelFiles = Directory.GetFiles(modelsDirectory, "*.onnx");
                _availableModels.Clear();

                foreach (var file in modelFiles)
                {
                    _availableModels.Add(Path.GetFileNameWithoutExtension(file));
                }

                if (_availableModels.Count == 0)
                {
                    Debug.WriteLine("[DetectionService] 未找到任何模型文件");
                    return false;
                }

                // 初始化多模型管理器
                _modelManager = new MultiModelManager(useGpu);

                // 加载主模型 (第一个找到的模型)
                string primaryModelPath = modelFiles[0];
                await Task.Run(() => _modelManager.LoadPrimaryModel(primaryModelPath));

                if (_modelManager.IsPrimaryLoaded)
                {
                    string loadedName = System.IO.Path.GetFileNameWithoutExtension(_modelManager.PrimaryModelPath);
                    _currentModelName = loadedName;
                    ModelLoaded?.Invoke(loadedName);
                    Debug.WriteLine($"[DetectionService] 多模型管理器初始化完成: {_modelManager.PrimaryModelPath}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"扫描模型失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SwitchModelAsync(string modelName)
        {
            try
            {
                string modelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX");
                string modelPath = Path.Combine(modelsDir, $"{modelName}.onnx");

                if (_modelManager != null)
                {
                    // 重新加载主模型
                    await Task.Run(() => _modelManager.LoadPrimaryModel(modelPath));

                    if (_modelManager.IsPrimaryLoaded)
                    {
                        _currentModelName = modelName;
                        ModelLoaded?.Invoke(modelName);
                        return true;
                    }
                    return false;
                }

                return await LoadModelAsync(modelPath, _useGpu);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"切换模型失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 检测方法

        public async Task<DetectionResultData> DetectAsync(Mat image, float confidence, float iouThreshold)
        {
            using var bitmap = image.ToBitmap();
            return await DetectAsync(bitmap, confidence, iouThreshold);
        }

        public async Task<DetectionResultData> DetectAsync(Bitmap image, float confidence, float iouThreshold)
        {
            var result = new DetectionResultData
            {
                OriginalBitmap = new Bitmap(image)
            };

            if (!IsModelLoaded)
            {
                ErrorOccurred?.Invoke("模型未加载");
                result.IsQualified = false;
                return result;
            }

            var sw = Stopwatch.StartNew();

            try
            {
                List<YoloResult> allResults;
                string usedModelName = "";
                string[] usedModelLabels = Array.Empty<string>();
                bool wasFallback = false;

                // 使用多模型管理器进行推理
                if (_modelManager != null && _modelManager.IsPrimaryLoaded)
                {
                    var inferenceResult = await _modelManager.InferenceWithFallbackAsync(
                        image, confidence, iouThreshold, false, 1);

                    allResults = inferenceResult.Results;
                    usedModelName = inferenceResult.UsedModelName;
                    usedModelLabels = inferenceResult.UsedModelLabels;
                    wasFallback = inferenceResult.WasFallback;
                }
                else if (_yolo != null)
                {
                    // 向后兼容：使用单模型推理
                    allResults = await Task.Run(() =>
                        _yolo.Inference(image, confidence, iouThreshold, false, 0));
                    usedModelLabels = _yolo.Labels;
                }
                else
                {
                    throw new InvalidOperationException("没有可用的检测模型");
                }

                sw.Stop();
                LastInferenceMs = sw.ElapsedMilliseconds;

                // 简单判定：无检测结果视为合格
                bool isQualified = allResults.Count == 0;

                result.IsQualified = isQualified;
                result.Results = allResults;
                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.UsedModelLabels = usedModelLabels;
                result.UsedModelName = usedModelName;
                result.WasFallback = wasFallback;

                DetectionCompleted?.Invoke(result);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                ErrorOccurred?.Invoke($"检测失败: {ex.Message}");
                result.IsQualified = false;
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }
        }

        #endregion

        #region 结果可视化

        public Bitmap GenerateResultImage(Bitmap original, List<YoloResult> results, string[] labels)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                // 使用 MultiModelManager 的 GenerateImage 方法
                var detector = _modelManager.PrimaryDetector;
                if (detector != null)
                {
                    return (Bitmap)detector.GenerateImage(original, results, labels);
                }
            }

            if (_yolo != null)
            {
                return (Bitmap)_yolo.GenerateImage(original, results, labels);
            }

            // 返回原图的副本
            return new Bitmap(original);
        }

        #endregion

        #region 多模型管理

        public void SetTaskMode(int taskType)
        {
            _modelManager?.SetTaskMode((YoloTaskType)taskType);
            if (_yolo != null)
            {
                _yolo.TaskMode = (YoloTaskType)taskType;
            }
        }

        public void SetEnableFallback(bool enabled)
        {
            if (_modelManager != null)
            {
                _modelManager.EnableFallback = enabled;
            }
        }

        public async Task<bool> LoadAuxiliary1ModelAsync(string modelPath)
        {
            if (_modelManager == null || string.IsNullOrEmpty(modelPath))
                return false;

            try
            {
                await Task.Run(() => _modelManager.LoadAuxiliary1Model(modelPath));
                Debug.WriteLine($"[DetectionService] 辅助模型1已加载: {Path.GetFileName(modelPath)}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"加载辅助模型1失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> LoadAuxiliary2ModelAsync(string modelPath)
        {
            if (_modelManager == null || string.IsNullOrEmpty(modelPath))
                return false;

            try
            {
                await Task.Run(() => _modelManager.LoadAuxiliary2Model(modelPath));
                Debug.WriteLine($"[DetectionService] 辅助模型2已加载: {Path.GetFileName(modelPath)}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"加载辅助模型2失败: {ex.Message}");
                return false;
            }
        }

        public void UnloadAuxiliary1Model()
        {
            _modelManager?.UnloadAuxiliary1Model();
        }

        public void UnloadAuxiliary2Model()
        {
            _modelManager?.UnloadAuxiliary2Model();
        }

        public string[] GetLabels()
        {
            return _modelManager?.PrimaryLabels ?? _yolo?.Labels ?? Array.Empty<string>();
        }

        public object? GetLastMetrics()
        {
            return _modelManager?.PrimaryDetector?.LastMetrics ?? _yolo?.LastMetrics;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _yolo?.Dispose();
            _yolo = null;

            _modelManager?.Dispose();
            _modelManager = null;

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
