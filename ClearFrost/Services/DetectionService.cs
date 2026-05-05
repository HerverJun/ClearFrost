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

        /// <summary>
        /// 获取当前主检测器实例（用于 Mat 直通渲染等优化路径）。
        /// </summary>
        internal YoloDetector? PrimaryDetector => _modelManager?.PrimaryDetector ?? _yolo;

        #endregion

        #region 构造函数

        public DetectionService(bool useGpu = true)
        {
            _useGpu = useGpu;
        }

        #endregion

        #region 模型管理

        /// <summary>
        /// 异步加载指定路径的 YOLO 模型
        /// </summary>
        /// <param name="modelPath">模型文件的完整路径</param>
        /// <param name="useGpu">是否使用 GPU 进行推理</param>
        /// <returns>如果是加载成功返回 true，否则返回 false</returns>
        public async Task<bool> LoadModelAsync(string modelPath, bool useGpu)
        {
            bool rebuildManager = false;
            bool committedTempManager = false;
            MultiModelManager? replacementManager = null;
            MultiModelManager? previousManager = null;

            try
            {
                if (!File.Exists(modelPath))
                {
                    ErrorOccurred?.Invoke($"模型文件不存在: {modelPath}");
                    return false;
                }

                rebuildManager = _modelManager != null && _modelManager.UseGpu != useGpu;
                string preservedAux1Path = string.Empty;
                string preservedAux2Path = string.Empty;
                bool preservedFallback = false;

                if (rebuildManager)
                {
                    previousManager = _modelManager ?? throw new InvalidOperationException("多模型管理器初始化失败");
                    preservedAux1Path = previousManager.Auxiliary1ModelPath ?? string.Empty;
                    preservedAux2Path = previousManager.Auxiliary2ModelPath ?? string.Empty;
                    preservedFallback = previousManager.EnableFallback;
                    replacementManager = new MultiModelManager(useGpu)
                    {
                        EnableFallback = preservedFallback
                    };
                    Debug.WriteLine($"[DetectionService] GPU 设置变化为 {useGpu}，准备重建多模型管理器");
                }
                else if (_modelManager == null)
                {
                    _modelManager = new MultiModelManager(useGpu);
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
                        _yolo = new YoloDetector(modelPath, 0, 0, useGpu);
                    });
                }
                else if (rebuildManager)
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
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"加载模型失败: {ex.Message}");
                return false;
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

                // 初始化多模型管理器 (显式回收旧资源以防内存泄漏)
                _modelManager?.Dispose();
                _yolo?.Dispose();
                _yolo = null;
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

        public async Task<DetectionResultData> DetectAsync(Mat image, float confidence, float iouThreshold,
            string? targetLabel = null, int targetCount = 0)
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
                var inference = await RunInferenceAsync(image, confidence, iouThreshold, targetLabel, targetCount);
                sw.Stop();
                LastInferenceMs = sw.ElapsedMilliseconds;

                PopulateResult(
                    result,
                    inference.Results,
                    inference.UsedModelName,
                    inference.UsedModelLabels,
                    inference.WasFallback,
                    sw.ElapsedMilliseconds,
                    targetLabel,
                    targetCount);

                DetectionCompleted?.Invoke(result);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return CreateFailedResult($"检测失败: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// 对图像执行检测（使用 Bitmap，支持目标标签和期望数量判定）
        /// </summary>
        /// <param name="image">输入图像</param>
        /// <param name="confidence">置信度阈值</param>
        /// <param name="iouThreshold">IOU 阈值</param>
        /// <param name="targetLabel">目标标签名（用于判定合格）</param>
        /// <param name="targetCount">期望目标数量（用于判定合格）</param>
        /// <returns>检测结果数据对象</returns>
        public async Task<DetectionResultData> DetectAsync(Bitmap image, float confidence, float iouThreshold,
            string? targetLabel = null, int targetCount = 0)
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
                var inference = await RunInferenceAsync(image, confidence, iouThreshold, targetLabel, targetCount);
                sw.Stop();
                LastInferenceMs = sw.ElapsedMilliseconds;

                PopulateResult(
                    result,
                    inference.Results,
                    inference.UsedModelName,
                    inference.UsedModelLabels,
                    inference.WasFallback,
                    sw.ElapsedMilliseconds,
                    targetLabel,
                    targetCount);

                DetectionCompleted?.Invoke(result);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return CreateFailedResult($"检测失败: {ex.Message}", sw.ElapsedMilliseconds);
            }
        }

        private async Task<(List<YoloResult> Results, string UsedModelName, string[] UsedModelLabels, bool WasFallback)> RunInferenceAsync(
            Bitmap image, float confidence, float iouThreshold, string? targetLabel, int targetCount)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                var inferenceResult = await _modelManager.InferenceWithFallbackAsync(
                    image, confidence, iouThreshold, false, 1, targetLabel, targetCount);
                if (inferenceResult.HasError)
                {
                    throw new InvalidOperationException(inferenceResult.ErrorMessage);
                }

                return (inferenceResult.Results, inferenceResult.UsedModelName, inferenceResult.UsedModelLabels, inferenceResult.WasFallback);
            }

            if (_yolo != null)
            {
                var allResults = await Task.Run(() =>
                    _yolo.Inference(image, confidence, iouThreshold, false, 1));
                return (allResults, "", _yolo.Labels, false);
            }

            throw new InvalidOperationException("没有可用的检测模型");
        }

        private async Task<(List<YoloResult> Results, string UsedModelName, string[] UsedModelLabels, bool WasFallback)> RunInferenceAsync(
            Mat image, float confidence, float iouThreshold, string? targetLabel, int targetCount)
        {
            if (_modelManager != null && _modelManager.IsPrimaryLoaded)
            {
                var inferenceResult = await _modelManager.InferenceWithFallbackAsync(
                    image, confidence, iouThreshold, false, 1, targetLabel, targetCount);
                if (inferenceResult.HasError)
                {
                    throw new InvalidOperationException(inferenceResult.ErrorMessage);
                }

                return (inferenceResult.Results, inferenceResult.UsedModelName, inferenceResult.UsedModelLabels, inferenceResult.WasFallback);
            }

            if (_yolo != null)
            {
                var allResults = await Task.Run(() =>
                    _yolo.Inference(image, confidence, iouThreshold, false, 1));
                return (allResults, "", _yolo.Labels, false);
            }

            throw new InvalidOperationException("没有可用的检测模型");
        }

        private void PopulateResult(
            DetectionResultData result,
            List<YoloResult> allResults,
            string usedModelName,
            string[] usedModelLabels,
            bool wasFallback,
            long elapsedMs,
            string? targetLabel,
            int targetCount)
        {
            bool isQualified;
            if (!string.IsNullOrWhiteSpace(targetLabel))
            {
                if (targetCount < 0)
                {
                    isQualified = false;
                    Debug.WriteLine($"[DetectionService] 判定失败: 目标数量不能为负数 ({targetCount})");
                }
                else
                {
                    int actualCount = allResults.Count(r =>
                    {
                        string detectedLabel = (r.ClassId >= 0 && r.ClassId < usedModelLabels.Length)
                            ? usedModelLabels[r.ClassId]
                            : "";
                        return detectedLabel.Equals(targetLabel, StringComparison.OrdinalIgnoreCase);
                    });

                    isQualified = actualCount == targetCount;
                    Debug.WriteLine($"[DetectionService] 判定: 目标标签='{targetLabel}', 期望数量={targetCount}, 实际数量={actualCount}, 是否合格={isQualified}");
                }
            }
            else
            {
                isQualified = allResults.Count == 0;
                Debug.WriteLine($"[DetectionService] 判定(默认): 检测结果数量={allResults.Count}, 是否合格={isQualified}");
            }

            result.IsQualified = isQualified;
            result.Results = allResults;
            result.ElapsedMs = elapsedMs;
            result.UsedModelLabels = usedModelLabels;
            result.UsedModelName = usedModelName;
            result.WasFallback = wasFallback;
            result.HasError = false;
            result.ErrorMessage = string.Empty;
        }

        private DetectionResultData CreateFailedResult(string message, long elapsedMs = 0)
        {
            ErrorOccurred?.Invoke(message);
            return new DetectionResultData
            {
                IsQualified = false,
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
