// ============================================================================
// 文件名: DetectionService.cs
// 作者: 蘅芜君
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
using ClearFrost.Core.Rules;
using ClearFrost.Interfaces;
using ClearFrost.Yolo;

namespace ClearFrost.Services
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
        private int _gpuDeviceId;
        private readonly List<string> _availableModels = new List<string>();
        private string _currentModelName = "未加载";
        private DetectionRuntimeStatus _runtimeStatus = new DetectionRuntimeStatus();
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
        public DetectionRuntimeStatus RuntimeStatus => _runtimeStatus;

        /// <summary>
        /// 获取当前主检测器实例（用于 Mat 直通渲染等优化路径）。
        /// </summary>
        internal YoloDetector? PrimaryDetector => _modelManager?.PrimaryDetector ?? _yolo;

        #endregion

        #region 构造函数

        public DetectionService(bool useGpu = true, int gpuDeviceId = 0)
        {
            _useGpu = useGpu;
            _gpuDeviceId = Math.Max(0, gpuDeviceId);
            _runtimeStatus = CreateRuntimeStatus(useGpu, false, _gpuDeviceId, string.Empty);
        }

        #endregion

        #region 模型管理

        /// <summary>
        /// 异步加载指定路径的 YOLO 模型
        /// </summary>
        /// <param name="modelPath">模型文件的完整路径</param>
        /// <param name="useGpu">是否使用 GPU 进行推理</param>
        /// <returns>如果是加载成功返回 true，否则返回 false</returns>
        public async Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0)
        {
            gpuDeviceId = Math.Max(0, gpuDeviceId);
            _gpuDeviceId = gpuDeviceId;

            if (!File.Exists(modelPath))
            {
                ErrorOccurred?.Invoke($"模型文件不存在: {modelPath}");
                return false;
            }

            if (useGpu)
            {
                try
                {
                    bool loaded = await LoadModelCoreAsync(modelPath, useGpu: true, gpuDeviceId).ConfigureAwait(false);
                    if (loaded)
                    {
                        _runtimeStatus = CreateRuntimeStatusFromDetector(PrimaryDetector, true, gpuDeviceId);
                        if (!_runtimeStatus.GpuActive && !string.IsNullOrWhiteSpace(_runtimeStatus.GpuFailureReason))
                        {
                            Debug.WriteLine(
                                $"[DetectionService] DirectML GPU 不可用，已自动回退 CPU: {_runtimeStatus.GpuFailureReason}");
                        }
                    }
                    return loaded;
                }
                catch (Exception ex)
                {
                    string reason = ex.Message;
                    Debug.WriteLine($"[DetectionService] DirectML GPU 加载失败，回退 CPU: {ex}");

                    try
                    {
                        bool loaded = await LoadModelCoreAsync(modelPath, useGpu: false, gpuDeviceId).ConfigureAwait(false);
                        _runtimeStatus = CreateRuntimeStatus(true, false, gpuDeviceId, reason);
                        if (loaded)
                        {
                            Debug.WriteLine(
                                $"[DetectionService] DirectML GPU 失败后已成功回退 CPU: {reason}");
                            return true;
                        }

                        ErrorOccurred?.Invoke($"CPU 回退加载模型失败: {reason}");
                        return loaded;
                    }
                    catch (Exception cpuEx)
                    {
                        _runtimeStatus = CreateRuntimeStatus(true, false, gpuDeviceId, reason);
                        ErrorOccurred?.Invoke($"CPU 回退加载模型失败: {cpuEx.Message}");
                        return false;
                    }
                }
            }

            try
            {
                bool loaded = await LoadModelCoreAsync(modelPath, useGpu: false, gpuDeviceId).ConfigureAwait(false);
                _runtimeStatus = CreateRuntimeStatus(false, false, gpuDeviceId, string.Empty);
                return loaded;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"加载模型失败: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> LoadModelCoreAsync(string modelPath, bool useGpu, int gpuDeviceId)
        {
            bool rebuildManager = false;
            bool committedTempManager = false;
            MultiModelManager? replacementManager = null;
            MultiModelManager? previousManager = null;

            try
            {
                rebuildManager = _modelManager == null ||
                    _modelManager.UseGpu != useGpu ||
                    (useGpu && _modelManager.GpuDeviceId != gpuDeviceId);
                string preservedAux1Path = string.Empty;
                string preservedAux2Path = string.Empty;
                bool preservedFallback = false;

                if (rebuildManager)
                {
                    previousManager = _modelManager;
                    if (previousManager != null)
                    {
                        preservedAux1Path = previousManager.Auxiliary1ModelPath ?? string.Empty;
                        preservedAux2Path = previousManager.Auxiliary2ModelPath ?? string.Empty;
                        preservedFallback = previousManager.EnableFallback;
                    }

                    replacementManager = new MultiModelManager(useGpu, gpuDeviceId)
                    {
                        EnableFallback = preservedFallback
                    };
                    Debug.WriteLine($"[DetectionService] 推理后端变化为 GPU={useGpu}, Device={gpuDeviceId}，准备重建多模型管理器");
                }

                MultiModelManager manager = replacementManager ?? _modelManager
                    ?? throw new InvalidOperationException("多模型管理器初始化失败");

                // 使用多模型管理器加载主模型
                await Task.Run(() => manager.LoadPrimaryModel(modelPath));

                if (!manager.IsPrimaryLoaded)
                {
                    if (rebuildManager)
                    {
                        return false;
                    }

                    // 如果多模型管理器加载失败，回退到单模型模式
                    await Task.Run(() =>
                    {
                        _yolo?.Dispose(); // 显式释放旧资源
                        _yolo = new YoloDetector(modelPath, 0, gpuDeviceId, useGpu);
                    });
                }

                if (rebuildManager)
                {
                    // 主模型加载成功且本次发生过 GPU 重建，按新 GPU 设置恢复辅助槽。
                    if (!string.IsNullOrEmpty(preservedAux1Path) && File.Exists(preservedAux1Path))
                    {
                        await TryLoadAuxiliaryModelAsync(manager, preservedAux1Path, 1);
                    }
                    if (!string.IsNullOrEmpty(preservedAux2Path) && File.Exists(preservedAux2Path))
                    {
                        await TryLoadAuxiliaryModelAsync(manager, preservedAux2Path, 2);
                    }

                    _modelManager = manager;
                    committedTempManager = true;
                    previousManager?.Dispose();
                    previousManager = null;
                }

                string modelName = Path.GetFileNameWithoutExtension(modelPath);
                if (!_availableModels.Contains(modelName))
                {
                    _availableModels.Add(modelName);
                }

                _currentModelName = modelName;
                ModelLoaded?.Invoke(modelName);
                Debug.WriteLine($"[DetectionService] 模型已加载: {modelName} (MultiModelManager: {_modelManager?.IsPrimaryLoaded ?? false})");
                return true;
            }
            finally
            {
                if (rebuildManager && !committedTempManager)
                {
                    replacementManager?.Dispose();
                }
            }
        }

        /// <summary>
        /// 扫描指定目录下的所有 ONNX 模型并加载第一个找到的模型
        /// </summary>
        /// <param name="modelsDirectory">模型目录路径</param>
        /// <param name="useGpu">是否使用 GPU</param>
        /// <returns>如果有模型加载成功返回 true，否则返回 false</returns>
        public async Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0)
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

                // 加载主模型 (第一个找到的模型)
                string primaryModelPath = modelFiles[0];
                if (await LoadModelAsync(primaryModelPath, useGpu, gpuDeviceId).ConfigureAwait(false))
                {
                    Debug.WriteLine($"[DetectionService] 多模型管理器初始化完成: {primaryModelPath}");
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

        /// <summary>
        /// 切换当前使用的模型
        /// </summary>
        /// <param name="modelName">模型名称（不含扩展名）</param>
        /// <returns>切换成功返回 true</returns>
        public async Task<bool> SwitchModelAsync(string modelName)
        {
            try
            {
                string modelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX");
                string fileName = modelName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                    ? modelName
                    : $"{modelName}.onnx";
                string modelPath = Path.Combine(modelsDir, fileName);

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

                return await LoadModelAsync(modelPath, _useGpu, _gpuDeviceId);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"切换模型失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 检测方法

        public async Task<DetectionResultData> DetectAsync(
            Mat image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal = null,
            MultiModelCandidateEvaluator? candidateEvaluator = null)
        {
            var result = new DetectionResultData();

            if (image == null || image.Empty())
            {
                return CreateFailedResult("输入图像为空");
            }

            if (!IsModelLoaded)
            {
                return CreateFailedResult("模型未加载");
            }

            var sw = Stopwatch.StartNew();

            try
            {
                var inference = await RunInferenceAsync(image, confidence, iouThreshold, fallbackGoal, candidateEvaluator);
                sw.Stop();
                LastInferenceMs = sw.ElapsedMilliseconds;

                PopulateResult(
                    result,
                    inference.Results,
                    inference.UsedModelName,
                    inference.UsedModelLabels,
                    inference.WasFallback,
                    inference.FallbackAttemptCount,
                    inference.FallbackSkippedReason,
                    sw.ElapsedMilliseconds);

                DetectionCompleted?.Invoke(result);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return CreateFailedResult($"检测失败: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        public async Task<DetectionResultData> DetectAsync(
            Bitmap image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal = null,
            MultiModelCandidateEvaluator? candidateEvaluator = null)
        {
            var result = new DetectionResultData();

            if (image == null)
            {
                return CreateFailedResult("输入图像为空");
            }

            if (!IsModelLoaded)
            {
                return CreateFailedResult("模型未加载");
            }

            var sw = Stopwatch.StartNew();

            try
            {
                var inference = await RunInferenceAsync(image, confidence, iouThreshold, fallbackGoal, candidateEvaluator);
                sw.Stop();
                LastInferenceMs = sw.ElapsedMilliseconds;

                PopulateResult(
                    result,
                    inference.Results,
                    inference.UsedModelName,
                    inference.UsedModelLabels,
                    inference.WasFallback,
                    inference.FallbackAttemptCount,
                    inference.FallbackSkippedReason,
                    sw.ElapsedMilliseconds);

                DetectionCompleted?.Invoke(result);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return CreateFailedResult($"检测失败: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        private async Task<(List<YoloResult> Results, string UsedModelName, string[] UsedModelLabels, bool WasFallback, int FallbackAttemptCount, string FallbackSkippedReason)> RunInferenceAsync(
            Bitmap image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal,
            MultiModelCandidateEvaluator? candidateEvaluator)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                var inferenceResult = await _modelManager.InferenceWithFallbackAsync(
                    image,
                    confidence,
                    iouThreshold,
                    false,
                    1,
                    fallbackGoal?.TargetLabel,
                    fallbackGoal?.TargetCount ?? 0,
                    candidateEvaluator);
                if (inferenceResult.HasError)
                {
                    throw new InvalidOperationException(inferenceResult.ErrorMessage);
                }

                return (
                    inferenceResult.Results,
                    inferenceResult.UsedModelName,
                    inferenceResult.UsedModelLabels,
                    inferenceResult.WasFallback,
                    inferenceResult.FallbackAttemptCount,
                    inferenceResult.FallbackSkippedReason);
            }

            if (_yolo != null)
            {
                var allResults = await Task.Run(() =>
                    _yolo.Inference(image, confidence, iouThreshold, false, 1));
                return (allResults, "", _yolo.Labels, false, 1, string.Empty);
            }

            throw new InvalidOperationException("没有可用的检测模型");
        }

        private async Task<(List<YoloResult> Results, string UsedModelName, string[] UsedModelLabels, bool WasFallback, int FallbackAttemptCount, string FallbackSkippedReason)> RunInferenceAsync(
            Mat image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal,
            MultiModelCandidateEvaluator? candidateEvaluator)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                var inferenceResult = await _modelManager.InferenceWithFallbackAsync(
                    image,
                    confidence,
                    iouThreshold,
                    false,
                    1,
                    fallbackGoal?.TargetLabel,
                    fallbackGoal?.TargetCount ?? 0,
                    candidateEvaluator);
                if (inferenceResult.HasError)
                {
                    throw new InvalidOperationException(inferenceResult.ErrorMessage);
                }

                return (
                    inferenceResult.Results,
                    inferenceResult.UsedModelName,
                    inferenceResult.UsedModelLabels,
                    inferenceResult.WasFallback,
                    inferenceResult.FallbackAttemptCount,
                    inferenceResult.FallbackSkippedReason);
            }

            if (_yolo != null)
            {
                var allResults = await Task.Run(() =>
                    _yolo.Inference(image, confidence, iouThreshold, false, 1));
                return (allResults, "", _yolo.Labels, false, 1, string.Empty);
            }

            throw new InvalidOperationException("没有可用的检测模型");
        }

        private void PopulateResult(
            DetectionResultData result,
            List<YoloResult> allResults,
            string usedModelName,
            string[] usedModelLabels,
            bool wasFallback,
            int fallbackAttemptCount,
            string fallbackSkippedReason,
            long elapsedMs)
        {
            Debug.WriteLine($"[DetectionService] 推理完成: 检测结果数量={allResults.Count}, 耗时={elapsedMs}ms");
            result.IsQualified = false;
            result.IsRuleEvaluated = false;
            result.Results = allResults;
            result.ElapsedMs = elapsedMs;
            result.UsedModelLabels = usedModelLabels;
            result.UsedModelName = usedModelName;
            result.WasFallback = wasFallback;
            result.FallbackAttemptCount = fallbackAttemptCount;
            result.FallbackSkippedReason = fallbackSkippedReason ?? string.Empty;
            result.HasError = false;
            result.ErrorMessage = string.Empty;
        }

        private DetectionResultData CreateFailedResult(string message, long elapsedMs = 0)
        {
            ErrorOccurred?.Invoke(message);
            return new DetectionResultData
            {
                IsQualified = false,
                IsRuleEvaluated = false,
                Results = new List<YoloResult>(),
                ElapsedMs = elapsedMs,
                UsedModelLabels = Array.Empty<string>(),
                UsedModelName = _currentModelName,
                HasError = true,
                ErrorMessage = message
            };
        }
        #endregion

        #region 结果可视化

        /// <summary>
        /// 生成包含检测结果标注的图像
        /// </summary>
        /// <param name="original">原始图像</param>
        /// <param name="results">检测结果列表</param>
        /// <param name="labels">标签数组</param>
        /// <returns>标注后的图像</returns>
        public Bitmap GenerateResultImage(Bitmap original, List<YoloResult> results, string[] labels)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                Bitmap? image = _modelManager.GeneratePrimaryResultImage(original, results, labels);
                if (image != null)
                {
                    return image;
                }
            }

            if (_yolo != null)
            {
                return (Bitmap)_yolo.GenerateImage(original, results, labels);
            }

            // 返回原图的副本
            return new Bitmap(original);
        }

        internal Mat? GenerateResultMat(Mat original, List<YoloResult> results, string[] labels)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                Mat? image = _modelManager.GeneratePrimaryResultMat(original, results, labels);
                if (image != null)
                {
                    return image;
                }
            }

            return _yolo?.GenerateImageMat(original, results, labels);
        }

        #endregion

        #region 多模型管理

        /// <summary>
        /// 设置当前检测任务的模式（如检测、分割等）
        /// </summary>
        /// <param name="taskType">任务类型整数值</param>
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

            bool ok = await TryLoadAuxiliaryModelAsync(_modelManager, modelPath, 1);
            if (ok)
            {
                Debug.WriteLine($"[DetectionService] 辅助模型1已加载: {Path.GetFileName(modelPath)}");
            }

            return ok;
        }

        public async Task<bool> LoadAuxiliary2ModelAsync(string modelPath)
        {
            if (_modelManager == null || string.IsNullOrEmpty(modelPath))
                return false;

            bool ok = await TryLoadAuxiliaryModelAsync(_modelManager, modelPath, 2);
            if (ok)
            {
                Debug.WriteLine($"[DetectionService] 辅助模型2已加载: {Path.GetFileName(modelPath)}");
            }

            return ok;
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
            return _modelManager?.GetPrimaryLastMetrics() ?? _yolo?.LastMetrics;
        }

        #endregion

        #region 私有方法

        private async Task<bool> TryLoadAuxiliaryModelAsync(MultiModelManager manager, string modelPath, int modelIndex)
        {
            if (string.IsNullOrEmpty(modelPath))
            {
                return false;
            }

            try
            {
                await Task.Run(() =>
                {
                    if (modelIndex == 1)
                    {
                        manager.LoadAuxiliary1Model(modelPath);
                    }
                    else
                    {
                        manager.LoadAuxiliary2Model(modelPath);
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"加载辅助模型{modelIndex}失败: {ex.Message}");
                return false;
            }
        }

        private static DetectionRuntimeStatus CreateRuntimeStatus(
            bool gpuRequested,
            bool gpuActive,
            int gpuDeviceId,
            string gpuFailureReason)
        {
            return new DetectionRuntimeStatus
            {
                GpuRequested = gpuRequested,
                GpuActive = gpuActive,
                GpuDeviceId = Math.Max(0, gpuDeviceId),
                ExecutionProvider = gpuActive ? "DmlExecutionProvider" : "CPUExecutionProvider",
                GpuFailureReason = gpuFailureReason ?? string.Empty
            };
        }

        private static DetectionRuntimeStatus CreateRuntimeStatusFromDetector(
            YoloDetector? detector,
            bool gpuRequested,
            int gpuDeviceId)
        {
            if (detector == null)
            {
                return CreateRuntimeStatus(gpuRequested, false, gpuDeviceId, string.Empty);
            }

            return new DetectionRuntimeStatus
            {
                GpuRequested = gpuRequested,
                GpuActive = detector.GpuActive,
                GpuDeviceId = Math.Max(0, detector.GpuDeviceId),
                ExecutionProvider = detector.ExecutionProvider,
                GpuFailureReason = detector.GpuFailureReason ?? string.Empty
            };
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
