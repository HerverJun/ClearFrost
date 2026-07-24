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
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using ClearFrost.Core.Rules;
using ClearFrost.Helpers;
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
        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);
        private int _gpuDeviceId;
        private readonly List<string> _availableModels = new List<string>();
        private string _currentModelName = "未加载";
        private DetectionRuntimeStatus _runtimeStatus = new DetectionRuntimeStatus();
        private bool _disposed;
        private string[] _cachedLabels = Array.Empty<string>();
        private object? _cachedLastMetrics;

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
        public DetectionRuntimeModelSnapshot RuntimeModelSnapshot
        {
            get
            {
                MultiModelManager? manager = _modelManager;
                if (manager == null)
                {
                    return new DetectionRuntimeModelSnapshot();
                }

                return new DetectionRuntimeModelSnapshot
                {
                    Primary = new DetectionModelSlotSnapshot
                    {
                        Role = ModelRole.Primary,
                        IsLoaded = manager.IsPrimaryLoaded,
                        ModelPath = NormalizeRuntimePath(manager.PrimaryModelPath)
                    },
                    Auxiliary1 = new DetectionModelSlotSnapshot
                    {
                        Role = ModelRole.Auxiliary1,
                        IsLoaded = manager.IsAuxiliary1Loaded,
                        ModelPath = NormalizeRuntimePath(manager.Auxiliary1ModelPath)
                    },
                    Auxiliary2 = new DetectionModelSlotSnapshot
                    {
                        Role = ModelRole.Auxiliary2,
                        IsLoaded = manager.IsAuxiliary2Loaded,
                        ModelPath = NormalizeRuntimePath(manager.Auxiliary2ModelPath)
                    }
                };
            }
        }

        /// <summary>
        /// 获取当前主检测器实例（用于 Mat 直通渲染等优化路径）。
        /// </summary>
        internal IVisionModel? PrimaryDetector => _modelManager?.PrimaryDetector ?? _yolo;

        #endregion

        #region 构造函数

        public DetectionService(bool useGpu = false, int gpuDeviceId = 0)
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

            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
            if (!TryValidateModelFileForLoad(modelPath, out string safeModelPath, out string validationError))
            {
                ErrorOccurred?.Invoke(validationError);
                return false;
            }

            modelPath = safeModelPath;

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
                    string reason = ExceptionMessageFormatter.FormatForLog(ex);
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
                        ErrorOccurred?.Invoke($"CPU 回退加载模型失败: {ExceptionMessageFormatter.FormatForLog(cpuEx)}");
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
                ErrorOccurred?.Invoke($"加载模型失败: {ExceptionMessageFormatter.FormatForLog(ex)}");
                return false;
            }
            }
            finally
            {
                _lifecycleLock.Release();
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
                    if (TryValidateModelFileForLoad(preservedAux1Path, out string safeAux1Path, out _))
                    {
                        await TryLoadAuxiliaryModelAsync(manager, safeAux1Path, 1);
                    }
                    if (TryValidateModelFileForLoad(preservedAux2Path, out string safeAux2Path, out _))
                    {
                        await TryLoadAuxiliaryModelAsync(manager, safeAux2Path, 2);
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
                UpdateCachedFields();
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

        public void UnloadPrimaryModel()
        {
            if (!TryEnterLifecycleLock())
            {
                return;
            }

            try
            {
                _modelManager?.UnloadPrimaryModel();
                _yolo?.Dispose();
                _yolo = null;
                _currentModelName = "未加载";
                _cachedLabels = Array.Empty<string>();
                _cachedLastMetrics = null;
                _runtimeStatus = CreateRuntimeStatus(_useGpu, false, _gpuDeviceId, string.Empty);
            }
            finally
            {
                _lifecycleLock.Release();
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
                if (!TryValidateModelDirectory(modelsDirectory, out string safeModelsDirectory, out string directoryError))
                {
                    Debug.WriteLine($"[DetectionService] 模型目录不可用: {directoryError}");
                    ErrorOccurred?.Invoke(directoryError);
                    return false;
                }

                string[] discoveredModelFiles = Directory.GetFiles(safeModelsDirectory, "*.onnx", SearchOption.TopDirectoryOnly);
                var modelFiles = new List<string>();
                _availableModels.Clear();

                foreach (string file in discoveredModelFiles)
                {
                    if (!TryValidateModelFileForLoad(file, out string safeModelPath, out _))
                    {
                        continue;
                    }

                    modelFiles.Add(safeModelPath);
                    _availableModels.Add(Path.GetFileNameWithoutExtension(safeModelPath));
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
                ErrorOccurred?.Invoke($"扫描模型失败: {ExceptionMessageFormatter.FormatForLog(ex)}");
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
                if (!TryResolveSwitchModelPath(modelName, out string modelPath, out string validationError))
                {
                    ErrorOccurred?.Invoke(validationError);
                    return false;
                }

                if (_modelManager != null)
                {
                    await _lifecycleLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // 重新加载主模型
                        await Task.Run(() => _modelManager.LoadPrimaryModel(modelPath));

                        if (_modelManager.IsPrimaryLoaded)
                        {
                            _currentModelName = modelName;
                            UpdateCachedFields();
                            ModelLoaded?.Invoke(modelName);
                            return true;
                        }
                        return false;
                    }
                    finally
                    {
                        _lifecycleLock.Release();
                    }
                }

                return await LoadModelAsync(modelPath, _useGpu, _gpuDeviceId);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"切换模型失败: {ExceptionMessageFormatter.FormatForLog(ex)}");
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
            return await DetectAsync(
                image,
                confidence,
                iouThreshold,
                fallbackGoal,
                candidateEvaluator,
                YoloPreprocessingMode.StandardLetterBox).ConfigureAwait(false);
        }

        public async Task<DetectionResultData> DetectAsync(
            Mat image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal,
            MultiModelCandidateEvaluator? candidateEvaluator,
            YoloPreprocessingMode preprocessingMode)
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
                await _lifecycleLock.WaitAsync().ConfigureAwait(false);
                try
                {
                var inference = await RunInferenceAsync(image, confidence, iouThreshold, fallbackGoal, candidateEvaluator, preprocessingMode);
                sw.Stop();
                LastInferenceMs = sw.ElapsedMilliseconds;
                _cachedLastMetrics = _modelManager?.GetPrimaryLastMetrics() ?? _yolo?.LastMetrics;

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
                finally
                {
                    _lifecycleLock.Release();
                }
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
            return await DetectAsync(
                image,
                confidence,
                iouThreshold,
                fallbackGoal,
                candidateEvaluator,
                YoloPreprocessingMode.StandardLetterBox).ConfigureAwait(false);
        }

        public async Task<DetectionResultData> DetectAsync(
            Bitmap image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal,
            MultiModelCandidateEvaluator? candidateEvaluator,
            YoloPreprocessingMode preprocessingMode)
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
                await _lifecycleLock.WaitAsync().ConfigureAwait(false);
                try
                {
                var inference = await RunInferenceAsync(image, confidence, iouThreshold, fallbackGoal, candidateEvaluator, preprocessingMode);
                sw.Stop();
                LastInferenceMs = sw.ElapsedMilliseconds;
                _cachedLastMetrics = _modelManager?.GetPrimaryLastMetrics() ?? _yolo?.LastMetrics;

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
                finally
                {
                    _lifecycleLock.Release();
                }
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
            MultiModelCandidateEvaluator? candidateEvaluator,
            YoloPreprocessingMode preprocessingMode)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                var inferenceResult = await _modelManager.InferenceWithFallbackAsync(
                    image,
                    confidence,
                    iouThreshold,
                    false,
                    (int)preprocessingMode,
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
                    _yolo.Inference(image, confidence, iouThreshold, false, (int)preprocessingMode));
                return (allResults, "", _yolo.Labels, false, 1, string.Empty);
            }

            throw new InvalidOperationException("没有可用的检测模型");
        }

        private async Task<(List<YoloResult> Results, string UsedModelName, string[] UsedModelLabels, bool WasFallback, int FallbackAttemptCount, string FallbackSkippedReason)> RunInferenceAsync(
            Mat image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal,
            MultiModelCandidateEvaluator? candidateEvaluator,
            YoloPreprocessingMode preprocessingMode)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                var inferenceResult = await _modelManager.InferenceWithFallbackAsync(
                    image,
                    confidence,
                    iouThreshold,
                    false,
                    (int)preprocessingMode,
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
                    _yolo.Inference(image, confidence, iouThreshold, false, (int)preprocessingMode));
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
            try
            {
                var manager = _modelManager;
                if (manager != null && manager.IsPrimaryLoaded)
                {
                    Bitmap? image = manager.GeneratePrimaryResultImage(original, results, labels);
                    if (image != null)
                    {
                        return image;
                    }
                }

                var detector = _yolo;
                if (detector != null)
                {
                    return (Bitmap)detector.GenerateImage(original, results, labels);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DetectionService] 生成标注图像时发生异常: {ex.Message}");
            }

            // 返回原图的副本
            return new Bitmap(original);
        }

        internal Mat? GenerateResultMat(Mat original, List<YoloResult> results, string[] labels)
        {
            try
            {
                var manager = _modelManager;
                if (manager != null && manager.IsPrimaryLoaded)
                {
                    Mat? image = manager.GeneratePrimaryResultMat(original, results, labels);
                    if (image != null)
                    {
                        return image;
                    }
                }

                var detector = _yolo;
                if (detector != null)
                {
                    return detector.GenerateImageMat(original, results, labels);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DetectionService] 生成标注Mat时发生异常: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region 多模型管理

        /// <summary>
        /// 设置当前检测任务的模式（如检测、分割等）
        /// </summary>
        /// <param name="taskType">任务类型整数值</param>
        public void SetTaskMode(int taskType)
        {
            if (!TryEnterLifecycleLock())
            {
                return;
            }

            try
            {
                _modelManager?.SetTaskMode((YoloTaskType)taskType);
                if (_yolo != null)
                {
                    _yolo.TaskMode = (YoloTaskType)taskType;
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public void SetEnableFallback(bool enabled)
        {
            if (!TryEnterLifecycleLock())
            {
                return;
            }

            try
            {
                if (_modelManager != null)
                {
                    _modelManager.EnableFallback = enabled;
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task<bool> LoadAuxiliary1ModelAsync(string modelPath)
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_modelManager == null || string.IsNullOrEmpty(modelPath))
                    return false;

                if (!TryValidateModelFileForLoad(modelPath, out string safeModelPath, out string validationError))
                {
                    ErrorOccurred?.Invoke(validationError);
                    return false;
                }

                bool ok = await TryLoadAuxiliaryModelAsync(_modelManager, safeModelPath, 1);
                if (ok)
                {
                    Debug.WriteLine($"[DetectionService] 辅助模型1已加载: {Path.GetFileName(safeModelPath)}");
                }

                return ok;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task<bool> LoadAuxiliary2ModelAsync(string modelPath)
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_modelManager == null || string.IsNullOrEmpty(modelPath))
                    return false;

                if (!TryValidateModelFileForLoad(modelPath, out string safeModelPath, out string validationError))
                {
                    ErrorOccurred?.Invoke(validationError);
                    return false;
                }

                bool ok = await TryLoadAuxiliaryModelAsync(_modelManager, safeModelPath, 2);
                if (ok)
                {
                    Debug.WriteLine($"[DetectionService] 辅助模型2已加载: {Path.GetFileName(safeModelPath)}");
                }

                return ok;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public void UnloadAuxiliary1Model()
        {
            if (!TryEnterLifecycleLock())
            {
                return;
            }

            try
            {
                _modelManager?.UnloadAuxiliary1Model();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public void UnloadAuxiliary2Model()
        {
            if (!TryEnterLifecycleLock())
            {
                return;
            }

            try
            {
                _modelManager?.UnloadAuxiliary2Model();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public string[] GetLabels()
        {
            return (string[])_cachedLabels.Clone();
        }

        public object? GetLastMetrics()
        {
            return _cachedLastMetrics;
        }

        #endregion

        #region 私有方法

        private static bool TryValidateModelDirectory(string modelsDirectory, out string safeDirectory, out string error)
        {
            safeDirectory = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(modelsDirectory))
            {
                error = "模型目录为空。";
                return false;
            }

            try
            {
                string fullDirectory = Path.GetFullPath(modelsDirectory);
                var directory = new DirectoryInfo(fullDirectory);
                directory.Refresh();
                if (!directory.Exists)
                {
                    error = $"模型目录不存在: {fullDirectory}";
                    return false;
                }

                if (DirectoryPathHasReparsePoint(fullDirectory))
                {
                    error = $"模型目录包含链接目录，拒绝扫描: {fullDirectory}";
                    return false;
                }

                safeDirectory = fullDirectory;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                error = $"模型目录无效: {ExceptionMessageFormatter.FormatForLog(ex)}";
                return false;
            }
        }

        private static bool TryResolveSwitchModelPath(string modelName, out string modelPath, out string error)
        {
            modelPath = string.Empty;
            error = string.Empty;

            string trimmed = modelName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                error = "模型名称为空。";
                return false;
            }

            if (Path.IsPathRooted(trimmed) ||
                !string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal) ||
                trimmed.Contains("..", StringComparison.Ordinal))
            {
                error = $"模型名称不能包含路径段: {modelName}";
                return false;
            }

            string fileName = trimmed.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"{trimmed}.onnx";
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = $"模型名称包含非法字符: {modelName}";
                return false;
            }

            try
            {
                string modelsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX"));
                string fullPath = Path.GetFullPath(Path.Combine(modelsDir, fileName));
                if (!IsPathUnderDirectory(fullPath, modelsDir))
                {
                    error = $"模型路径必须位于 ONNX 目录内: {modelName}";
                    return false;
                }

                if (!TryValidateModelFileForLoad(fullPath, out modelPath, out error))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                error = $"模型路径无效: {ExceptionMessageFormatter.FormatForLog(ex)}";
                return false;
            }
        }

        private static bool TryValidateModelFileForLoad(string modelPath, out string safeModelPath, out string error)
        {
            safeModelPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                error = "模型文件路径为空。";
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(modelPath);
                if (!string.Equals(Path.GetExtension(fullPath), ".onnx", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"模型文件必须是 ONNX 文件: {fullPath}";
                    return false;
                }

                if (!File.Exists(fullPath))
                {
                    error = $"模型文件不存在: {fullPath}";
                    return false;
                }

                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) || DirectoryPathHasReparsePoint(directory))
                {
                    error = $"模型文件目录包含链接目录，拒绝加载: {fullPath}";
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                if (!file.Exists || HasReparsePoint(file))
                {
                    error = $"模型文件是链接文件或不可访问，拒绝加载: {fullPath}";
                    return false;
                }

                safeModelPath = fullPath;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                error = $"模型文件路径无效: {ExceptionMessageFormatter.FormatForLog(ex)}";
                return false;
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
        {
            try
            {
                var current = new DirectoryInfo(Path.GetFullPath(directory));
                while (current != null)
                {
                    current.Refresh();
                    if (current.Exists && HasReparsePoint(current))
                    {
                        return true;
                    }

                    current = current.Parent;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.StartsWith(
                normalizedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

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
                ErrorOccurred?.Invoke($"加载辅助模型{modelIndex}失败: {ExceptionMessageFormatter.FormatForLog(ex)}");
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

        private static string NormalizeRuntimePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static DetectionRuntimeStatus CreateRuntimeStatusFromDetector(
            IVisionModel? detector,
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

        private void UpdateCachedFields()
        {
            string[] labels = _modelManager?.PrimaryLabels ?? _yolo?.Labels ?? Array.Empty<string>();
            _cachedLabels = (string[])labels.Clone();
            _cachedLastMetrics = null;
        }

        private bool TryEnterLifecycleLock()
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                _lifecycleLock.Wait();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _lifecycleLock.Wait();
            try
            {
                _yolo?.Dispose();
                _yolo = null;

                _modelManager?.Dispose();
                _modelManager = null;
            }
            finally
            {
                _lifecycleLock.Release();
                _lifecycleLock.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
