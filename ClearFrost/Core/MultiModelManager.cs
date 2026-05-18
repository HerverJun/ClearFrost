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
// 
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace ClearFrost.Yolo
{
    /// <summary>
    /// 
    /// </summary>
    public enum ModelRole
    {
        /// 
        Primary,
        /// 
        Auxiliary1,
        /// 
        Auxiliary2,
        /// 
        None
    }

    /// <summary>
    /// 
    /// </summary>
    public class MultiModelInferenceResult
    {
        /// 
        public List<YoloResult> Results { get; set; } = new List<YoloResult>();

        /// 
        public ModelRole UsedModel { get; set; } = ModelRole.None;

        /// 
        public string UsedModelName { get; set; } = "";

        /// 
        public string[] UsedModelLabels { get; set; } = Array.Empty<string>();

        /// 
        public bool WasFallback { get; set; } = false;

        /// <summary>
        /// 推理是否发生错误。所有候选模型均推理失败时置为 true。
        /// </summary>
        public bool HasError { get; set; }

        /// <summary>
        /// 推理错误说明。
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// 
        public int DetectionCount => Results.Count;
    }

    /// <summary>
    /// 
    /// </summary>
    public class MultiModelManager : IDisposable
    {
        #region ˽���ֶ�

        private YoloDetector? _primaryModel;
        private YoloDetector? _auxiliary1Model;
        private YoloDetector? _auxiliary2Model;

        private string _primaryModelPath = "";
        private string _auxiliary1ModelPath = "";
        private string _auxiliary2ModelPath = "";

        private bool _useGpu = true;
        private int _gpuDeviceId = 0;
        private bool _enableFallback = true;

        private readonly object _lock = new object();
        private readonly ReaderWriterLockSlim _modelLock = new ReaderWriterLockSlim();
        private bool _disposed = false;

        #endregion

        #region ��������

        /// 
        public string PrimaryModelPath => _primaryModelPath;

        /// 
        public string Auxiliary1ModelPath => _auxiliary1ModelPath;

        /// 
        public string Auxiliary2ModelPath => _auxiliary2ModelPath;

        /// 
        public bool IsPrimaryLoaded
        {
            get
            {
                _modelLock.EnterReadLock();
                try
                {
                    return !_disposed && _primaryModel != null;
                }
                finally
                {
                    _modelLock.ExitReadLock();
                }
            }
        }

        /// 
        public bool IsAuxiliary1Loaded
        {
            get
            {
                _modelLock.EnterReadLock();
                try
                {
                    return !_disposed && _auxiliary1Model != null;
                }
                finally
                {
                    _modelLock.ExitReadLock();
                }
            }
        }

        /// 
        public bool IsAuxiliary2Loaded
        {
            get
            {
                _modelLock.EnterReadLock();
                try
                {
                    return !_disposed && _auxiliary2Model != null;
                }
                finally
                {
                    _modelLock.ExitReadLock();
                }
            }
        }

        ///
        public bool EnableFallback
        {
            get
            {
                _modelLock.EnterReadLock();
                try
                {
                    return _enableFallback;
                }
                finally
                {
                    _modelLock.ExitReadLock();
                }
            }
            set
            {
                _modelLock.EnterWriteLock();
                try
                {
                    ThrowIfDisposed();
                    _enableFallback = value;
                }
                finally
                {
                    _modelLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// 当前 manager 创建时锁定的 GPU 启用状态。修改此值需重建 manager。
        /// </summary>
        public bool UseGpu => _useGpu;

        /// <summary>
        /// 当前 manager 使用的 GPU 设备 ID。仅在 UseGpu 为 true 时影响 DirectML provider。
        /// </summary>
        public int GpuDeviceId => _gpuDeviceId;

        /// 
        public int PrimaryHitCount { get; private set; }

        /// 
        public int Auxiliary1HitCount { get; private set; }

        /// 
        public int Auxiliary2HitCount { get; private set; }

        /// 
        public int TotalInferenceCount { get; private set; }

        /// 
        public ModelRole LastUsedModel { get; private set; } = ModelRole.None;

        /// 
        public string[] PrimaryLabels
        {
            get
            {
                _modelLock.EnterReadLock();
                try
                {
                    return _primaryModel?.Labels ?? Array.Empty<string>();
                }
                finally
                {
                    _modelLock.ExitReadLock();
                }
            }
        }

        /// 
        internal YoloDetector? PrimaryDetector => _primaryModel;

        #endregion

        #region ���캯��

        /// <summary>
        /// 
        /// </summary>
        /// 
        /// 
        public MultiModelManager(bool useGpu = true, int gpuDeviceId = 0)
        {
            _useGpu = useGpu;
            _gpuDeviceId = gpuDeviceId;
        }

        #endregion

        #region ģ�ͼ���

        /// <summary>
        /// 
        /// </summary>
        public void LoadPrimaryModel(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            ThrowIfDisposed();
            YoloDetector? newModel = null;
            YoloDetector? oldModel = null;

            try
            {
                newModel = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);

                _modelLock.EnterWriteLock();
                try
                {
                    ThrowIfDisposed();
                    oldModel = _primaryModel;
                    _primaryModel = newModel;
                    _primaryModelPath = modelPath;
                    newModel = null;
                }
                finally
                {
                    _modelLock.ExitWriteLock();
                }

                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ��ģ�ͼ��سɹ�: {System.IO.Path.GetFileName(modelPath)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ��ģ�ͼ���ʧ��: {ex.Message}");
                throw;
            }
            finally
            {
                oldModel?.Dispose();
                newModel?.Dispose();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void LoadAuxiliary1Model(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            ThrowIfDisposed();
            YoloDetector? newModel = null;
            YoloDetector? oldModel = null;

            try
            {
                newModel = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);

                _modelLock.EnterWriteLock();
                try
                {
                    ThrowIfDisposed();
                    oldModel = _auxiliary1Model;
                    _auxiliary1Model = newModel;
                    _auxiliary1ModelPath = modelPath;
                    newModel = null;
                }
                finally
                {
                    _modelLock.ExitWriteLock();
                }

                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��1���سɹ�: {System.IO.Path.GetFileName(modelPath)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��1����ʧ��: {ex.Message}");
                throw;
            }
            finally
            {
                oldModel?.Dispose();
                newModel?.Dispose();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void LoadAuxiliary2Model(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            ThrowIfDisposed();
            YoloDetector? newModel = null;
            YoloDetector? oldModel = null;

            try
            {
                newModel = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);

                _modelLock.EnterWriteLock();
                try
                {
                    ThrowIfDisposed();
                    oldModel = _auxiliary2Model;
                    _auxiliary2Model = newModel;
                    _auxiliary2ModelPath = modelPath;
                    newModel = null;
                }
                finally
                {
                    _modelLock.ExitWriteLock();
                }

                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��2���سɹ�: {System.IO.Path.GetFileName(modelPath)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��2����ʧ��: {ex.Message}");
                throw;
            }
            finally
            {
                oldModel?.Dispose();
                newModel?.Dispose();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void UnloadAuxiliary1Model()
        {
            YoloDetector? oldModel;

            _modelLock.EnterWriteLock();
            try
            {
                ThrowIfDisposed();
                oldModel = _auxiliary1Model;
                _auxiliary1Model = null;
                _auxiliary1ModelPath = "";
            }
            finally
            {
                _modelLock.ExitWriteLock();
            }

            oldModel?.Dispose();
        }

        /// <summary>
        /// 
        /// </summary>
        public void UnloadAuxiliary2Model()
        {
            YoloDetector? oldModel;

            _modelLock.EnterWriteLock();
            try
            {
                ThrowIfDisposed();
                oldModel = _auxiliary2Model;
                _auxiliary2Model = null;
                _auxiliary2ModelPath = "";
            }
            finally
            {
                _modelLock.ExitWriteLock();
            }

            oldModel?.Dispose();
        }

        #endregion

        #region ��������

        internal static int CountTargetLabelHits(IReadOnlyList<YoloResult>? results, string[]? labels, string? targetLabel)
        {
            if (results == null || results.Count == 0)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(targetLabel))
            {
                return results.Count;
            }

            labels ??= Array.Empty<string>();
            return results.Count(r =>
            {
                if (r.ClassId < 0 || r.ClassId >= labels.Length)
                {
                    return false;
                }

                return string.Equals(labels[r.ClassId], targetLabel, StringComparison.OrdinalIgnoreCase);
            });
        }

        internal static bool IsTargetSatisfied(IReadOnlyList<YoloResult>? results, string[]? labels, string? targetLabel, int targetCount)
        {
            if (string.IsNullOrWhiteSpace(targetLabel))
            {
                return results != null && results.Count > 0;
            }

            if (targetCount < 0)
            {
                return false;
            }

            return CountTargetLabelHits(results, labels, targetLabel) == targetCount;
        }

        private static bool ShouldReplaceBestResult(
            IReadOnlyList<YoloResult> candidateResults,
            string[] candidateLabels,
            IReadOnlyList<YoloResult> currentBestResults,
            string[] currentBestLabels,
            string? targetLabel,
            int targetCount)
        {
            if (string.IsNullOrWhiteSpace(targetLabel))
            {
                return false;
            }

            int candidateHits = CountTargetLabelHits(candidateResults, candidateLabels, targetLabel);
            int bestHits = CountTargetLabelHits(currentBestResults, currentBestLabels, targetLabel);

            if (targetCount == 0)
            {
                if (candidateHits != bestHits)
                {
                    return candidateHits < bestHits;
                }

                return candidateResults.Count < currentBestResults.Count;
            }

            if (candidateHits != bestHits)
            {
                return candidateHits > bestHits;
            }

            return candidateResults.Count > currentBestResults.Count;
        }

        /// <summary>
        /// 执行多模型推理，支持自动切换到辅助模型
        /// </summary>
        /// <param name="targetLabel">目标标签名（可选，用于判断是否需要切换模型）</param>
        public MultiModelInferenceResult InferenceWithFallback(
            Bitmap image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1,
            string? targetLabel = null,
            int targetCount = 0)
        {
            _modelLock.EnterReadLock();
            try
            {
                ThrowIfDisposed();

            var result = new MultiModelInferenceResult();
            YoloDetector? primaryModel;
            YoloDetector? auxiliary1Model;
            YoloDetector? auxiliary2Model;
            string primaryModelPath;
            string auxiliary1ModelPath;
            string auxiliary2ModelPath;
            bool enableFallback;
            List<YoloResult>? bestResults = null;
            ModelRole bestModelRole = ModelRole.None;
            string bestModelName = string.Empty;
            string[] bestModelLabels = Array.Empty<string>();
            bool bestWasFallback = false;
            int attemptedModelCount = 0;
            int successfulInferenceCount = 0;
            List<string> inferenceErrors = new List<string>();

            void CaptureBestResult(List<YoloResult> detections, ModelRole modelRole, string modelPath, string[] labels, bool wasFallback)
            {
                if (detections.Count == 0)
                {
                    return;
                }

                if (bestResults != null &&
                    !ShouldReplaceBestResult(detections, labels, bestResults, bestModelLabels, targetLabel, targetCount))
                {
                    return;
                }

                bestResults = detections;
                bestModelRole = modelRole;
                bestModelName = System.IO.Path.GetFileName(modelPath);
                bestModelLabels = labels;
                bestWasFallback = wasFallback;
            }

            // 仅保护模型引用读取，推理本身在锁外执行。
            lock (_lock)
            {
                TotalInferenceCount++;
                LastUsedModel = ModelRole.None;
                primaryModel = _primaryModel;
                auxiliary1Model = _auxiliary1Model;
                auxiliary2Model = _auxiliary2Model;
                primaryModelPath = _primaryModelPath;
                auxiliary1ModelPath = _auxiliary1ModelPath;
                auxiliary2ModelPath = _auxiliary2ModelPath;
                enableFallback = _enableFallback;
            }

            // 主模型推理
            if (primaryModel != null)
            {
                attemptedModelCount++;
                try
                {
                    var primaryResults = primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    successfulInferenceCount++;
                    var primaryLabels = primaryModel.Labels ?? Array.Empty<string>();
                    CaptureBestResult(primaryResults, ModelRole.Primary, primaryModelPath, primaryLabels, false);
                    bool primaryHit = IsTargetSatisfied(primaryResults, primaryLabels, targetLabel, targetCount);

                    // 目标标签命中（或未配置目标标签时任意命中）才停止切换
                    if (primaryHit)
                    {
                        lock (_lock)
                        {
                            PrimaryHitCount++;
                            LastUsedModel = ModelRole.Primary;
                        }

                        result.Results = primaryResults;
                        result.UsedModel = ModelRole.Primary;
                        result.UsedModelName = System.IO.Path.GetFileName(primaryModelPath);
                        result.UsedModelLabels = primaryLabels;
                        result.WasFallback = false;
                        return result;
                    }

                    if (primaryResults.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        int actualCount = CountTargetLabelHits(primaryResults, primaryLabels, targetLabel);
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型目标数量不满足，继续切换（目标: {targetLabel}, 期望: {targetCount}, 实际: {actualCount}）");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 主模型未检测到任何目标，尝试切换辅助模型...");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型推理异常: {ex.Message}");
                    inferenceErrors.Add($"主模型: {ex.Message}");
                }
            }

            if (!enableFallback)
            {
                if (bestResults != null)
                {
                    lock (_lock)
                    {
                        LastUsedModel = bestModelRole;
                    }

                    result.Results = bestResults;
                    result.UsedModel = bestModelRole;
                    result.UsedModelName = bestModelName;
                    result.UsedModelLabels = bestModelLabels;
                    result.WasFallback = bestWasFallback;
                    return result;
                }

                result.UsedModel = ModelRole.Primary;
                result.UsedModelName = System.IO.Path.GetFileName(primaryModelPath);
                result.UsedModelLabels = primaryModel?.Labels ?? Array.Empty<string>();
                MarkErrorIfAllAttemptsFailed(result, attemptedModelCount, successfulInferenceCount, inferenceErrors);
                return result;
            }

            // 尝试辅助模型1
            if (auxiliary1Model != null)
            {
                attemptedModelCount++;
                try
                {
                    System.Diagnostics.Debug.WriteLine("[MultiModelManager] 切换到辅助模型1进行检测...");
                    var aux1Results = auxiliary1Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    successfulInferenceCount++;
                    var aux1Labels = auxiliary1Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux1Results, ModelRole.Auxiliary1, auxiliary1ModelPath, aux1Labels, true);
                    bool aux1Hit = IsTargetSatisfied(aux1Results, aux1Labels, targetLabel, targetCount);

                    if (aux1Hit)
                    {
                        lock (_lock)
                        {
                            Auxiliary1HitCount++;
                            LastUsedModel = ModelRole.Auxiliary1;
                        }

                        result.Results = aux1Results;
                        result.UsedModel = ModelRole.Auxiliary1;
                        result.UsedModelName = System.IO.Path.GetFileName(auxiliary1ModelPath);
                        result.UsedModelLabels = aux1Labels;
                        result.WasFallback = true;
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 辅助模型1命中!");
                        return result;
                    }

                    if (aux1Results.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        int actualCount = CountTargetLabelHits(aux1Results, aux1Labels, targetLabel);
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1目标数量不满足，继续切换（目标: {targetLabel}, 期望: {targetCount}, 实际: {actualCount}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1推理异常: {ex.Message}");
                    inferenceErrors.Add($"辅助模型1: {ex.Message}");
                }
            }

            // 尝试辅助模型2
            if (auxiliary2Model != null)
            {
                attemptedModelCount++;
                try
                {
                    var aux2Results = auxiliary2Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    successfulInferenceCount++;
                    var aux2Labels = auxiliary2Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux2Results, ModelRole.Auxiliary2, auxiliary2ModelPath, aux2Labels, true);
                    bool aux2Hit = IsTargetSatisfied(aux2Results, aux2Labels, targetLabel, targetCount);

                    if (aux2Hit)
                    {
                        lock (_lock)
                        {
                            Auxiliary2HitCount++;
                            LastUsedModel = ModelRole.Auxiliary2;
                        }

                        result.Results = aux2Results;
                        result.UsedModel = ModelRole.Auxiliary2;
                        result.UsedModelName = System.IO.Path.GetFileName(auxiliary2ModelPath);
                        result.UsedModelLabels = aux2Labels;
                        result.WasFallback = true;
                        return result;
                    }

                    if (aux2Results.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        int actualCount = CountTargetLabelHits(aux2Results, aux2Labels, targetLabel);
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2目标数量不满足，结束切换（目标: {targetLabel}, 期望: {targetCount}, 实际: {actualCount}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��2�����쳣: {ex.Message}");
                    inferenceErrors.Add($"辅助模型2: {ex.Message}");
                }
            }

            if (bestResults != null)
            {
                lock (_lock)
                {
                    LastUsedModel = bestModelRole;
                }

                result.Results = bestResults;
                result.UsedModel = bestModelRole;
                result.UsedModelName = bestModelName;
                result.UsedModelLabels = bestModelLabels;
                result.WasFallback = bestWasFallback;
            }

            MarkErrorIfAllAttemptsFailed(result, attemptedModelCount, successfulInferenceCount, inferenceErrors);

            return result;
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        private static void MarkErrorIfAllAttemptsFailed(
            MultiModelInferenceResult result,
            int attemptedModelCount,
            int successfulInferenceCount,
            List<string> inferenceErrors)
        {
            if (attemptedModelCount <= 0 || successfulInferenceCount > 0 || inferenceErrors.Count == 0)
            {
                return;
            }

            result.HasError = true;
            result.ErrorMessage = string.Join("; ", inferenceErrors);
        }

        /// <summary>
        /// 异步执行多模型推理，支持自动切换到辅助模型
        /// </summary>
        public async Task<MultiModelInferenceResult> InferenceWithFallbackAsync(
            Bitmap image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1,
            string? targetLabel = null,
            int targetCount = 0,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            // 异步执行推理
            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode, targetLabel, targetCount), cancellationToken);
        }

        /// <summary>
        /// Mat 版本：执行多模型推理，支持自动切换到辅助模型
        /// </summary>
        /// <param name="targetLabel">目标标签名（可选，用于判断是否需要切换模型）</param>
        public MultiModelInferenceResult InferenceWithFallback(
            Mat image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1,
            string? targetLabel = null,
            int targetCount = 0)
        {
            _modelLock.EnterReadLock();
            try
            {
                ThrowIfDisposed();

            var result = new MultiModelInferenceResult();
            YoloDetector? primaryModel;
            YoloDetector? auxiliary1Model;
            YoloDetector? auxiliary2Model;
            string primaryModelPath;
            string auxiliary1ModelPath;
            string auxiliary2ModelPath;
            bool enableFallback;
            List<YoloResult>? bestResults = null;
            ModelRole bestModelRole = ModelRole.None;
            string bestModelName = string.Empty;
            string[] bestModelLabels = Array.Empty<string>();
            bool bestWasFallback = false;
            int attemptedModelCount = 0;
            int successfulInferenceCount = 0;
            List<string> inferenceErrors = new List<string>();

            void CaptureBestResult(List<YoloResult> detections, ModelRole modelRole, string modelPath, string[] labels, bool wasFallback)
            {
                if (detections.Count == 0)
                {
                    return;
                }

                if (bestResults != null &&
                    !ShouldReplaceBestResult(detections, labels, bestResults, bestModelLabels, targetLabel, targetCount))
                {
                    return;
                }

                bestResults = detections;
                bestModelRole = modelRole;
                bestModelName = System.IO.Path.GetFileName(modelPath);
                bestModelLabels = labels;
                bestWasFallback = wasFallback;
            }

            lock (_lock)
            {
                TotalInferenceCount++;
                LastUsedModel = ModelRole.None;
                primaryModel = _primaryModel;
                auxiliary1Model = _auxiliary1Model;
                auxiliary2Model = _auxiliary2Model;
                primaryModelPath = _primaryModelPath;
                auxiliary1ModelPath = _auxiliary1ModelPath;
                auxiliary2ModelPath = _auxiliary2ModelPath;
                enableFallback = _enableFallback;
            }

            if (primaryModel != null)
            {
                attemptedModelCount++;
                try
                {
                    var primaryResults = primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    successfulInferenceCount++;
                    var primaryLabels = primaryModel.Labels ?? Array.Empty<string>();
                    CaptureBestResult(primaryResults, ModelRole.Primary, primaryModelPath, primaryLabels, false);
                    bool primaryHit = IsTargetSatisfied(primaryResults, primaryLabels, targetLabel, targetCount);

                    if (primaryHit)
                    {
                        lock (_lock)
                        {
                            PrimaryHitCount++;
                            LastUsedModel = ModelRole.Primary;
                        }

                        result.Results = primaryResults;
                        result.UsedModel = ModelRole.Primary;
                        result.UsedModelName = System.IO.Path.GetFileName(primaryModelPath);
                        result.UsedModelLabels = primaryLabels;
                        result.WasFallback = false;
                        return result;
                    }

                    if (primaryResults.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        int actualCount = CountTargetLabelHits(primaryResults, primaryLabels, targetLabel);
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型目标数量不满足，继续切换（目标: {targetLabel}, 期望: {targetCount}, 实际: {actualCount}）");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 主模型未检测到任何目标，尝试切换辅助模型...");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型推理异常: {ex.Message}");
                    inferenceErrors.Add($"主模型: {ex.Message}");
                }
            }

            if (!enableFallback)
            {
                if (bestResults != null)
                {
                    lock (_lock)
                    {
                        LastUsedModel = bestModelRole;
                    }

                    result.Results = bestResults;
                    result.UsedModel = bestModelRole;
                    result.UsedModelName = bestModelName;
                    result.UsedModelLabels = bestModelLabels;
                    result.WasFallback = bestWasFallback;
                    return result;
                }

                result.UsedModel = ModelRole.Primary;
                result.UsedModelName = System.IO.Path.GetFileName(primaryModelPath);
                result.UsedModelLabels = primaryModel?.Labels ?? Array.Empty<string>();
                MarkErrorIfAllAttemptsFailed(result, attemptedModelCount, successfulInferenceCount, inferenceErrors);
                return result;
            }

            if (auxiliary1Model != null)
            {
                attemptedModelCount++;
                try
                {
                    System.Diagnostics.Debug.WriteLine("[MultiModelManager] 切换到辅助模型1进行检测...");
                    var aux1Results = auxiliary1Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    successfulInferenceCount++;
                    var aux1Labels = auxiliary1Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux1Results, ModelRole.Auxiliary1, auxiliary1ModelPath, aux1Labels, true);
                    bool aux1Hit = IsTargetSatisfied(aux1Results, aux1Labels, targetLabel, targetCount);

                    if (aux1Hit)
                    {
                        lock (_lock)
                        {
                            Auxiliary1HitCount++;
                            LastUsedModel = ModelRole.Auxiliary1;
                        }

                        result.Results = aux1Results;
                        result.UsedModel = ModelRole.Auxiliary1;
                        result.UsedModelName = System.IO.Path.GetFileName(auxiliary1ModelPath);
                        result.UsedModelLabels = aux1Labels;
                        result.WasFallback = true;
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 辅助模型1命中!");
                        return result;
                    }

                    if (aux1Results.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        int actualCount = CountTargetLabelHits(aux1Results, aux1Labels, targetLabel);
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1目标数量不满足，继续切换（目标: {targetLabel}, 期望: {targetCount}, 实际: {actualCount}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1推理异常: {ex.Message}");
                    inferenceErrors.Add($"辅助模型1: {ex.Message}");
                }
            }

            if (auxiliary2Model != null)
            {
                attemptedModelCount++;
                try
                {
                    var aux2Results = auxiliary2Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    successfulInferenceCount++;
                    var aux2Labels = auxiliary2Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux2Results, ModelRole.Auxiliary2, auxiliary2ModelPath, aux2Labels, true);
                    bool aux2Hit = IsTargetSatisfied(aux2Results, aux2Labels, targetLabel, targetCount);

                    if (aux2Hit)
                    {
                        lock (_lock)
                        {
                            Auxiliary2HitCount++;
                            LastUsedModel = ModelRole.Auxiliary2;
                        }

                        result.Results = aux2Results;
                        result.UsedModel = ModelRole.Auxiliary2;
                        result.UsedModelName = System.IO.Path.GetFileName(auxiliary2ModelPath);
                        result.UsedModelLabels = aux2Labels;
                        result.WasFallback = true;
                        return result;
                    }

                    if (aux2Results.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        int actualCount = CountTargetLabelHits(aux2Results, aux2Labels, targetLabel);
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2目标数量不满足，结束切换（目标: {targetLabel}, 期望: {targetCount}, 实际: {actualCount}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2推理异常: {ex.Message}");
                    inferenceErrors.Add($"辅助模型2: {ex.Message}");
                }
            }

            if (bestResults != null)
            {
                lock (_lock)
                {
                    LastUsedModel = bestModelRole;
                }

                result.Results = bestResults;
                result.UsedModel = bestModelRole;
                result.UsedModelName = bestModelName;
                result.UsedModelLabels = bestModelLabels;
                result.WasFallback = bestWasFallback;
            }

            MarkErrorIfAllAttemptsFailed(result, attemptedModelCount, successfulInferenceCount, inferenceErrors);

            return result;
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Mat 异步版本：执行多模型推理，支持自动切换到辅助模型
        /// </summary>
        public async Task<MultiModelInferenceResult> InferenceWithFallbackAsync(
            Mat image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1,
            string? targetLabel = null,
            int targetCount = 0,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode, targetLabel, targetCount), cancellationToken);
        }

        /// <summary>
        /// 
        /// </summary>
        public List<YoloResult> InferencePrimaryOnly(
            Bitmap image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1)
        {
            _modelLock.EnterReadLock();
            try
            {
                ThrowIfDisposed();

                YoloDetector? primaryModel = _primaryModel;
                if (primaryModel == null)
                    return new List<YoloResult>();

                return primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        public Bitmap? GeneratePrimaryResultImage(Bitmap original, List<YoloResult> results, string[] labels)
        {
            _modelLock.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                return _primaryModel == null
                    ? null
                    : (Bitmap)_primaryModel.GenerateImage(original, results, labels);
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        public Mat? GeneratePrimaryResultMat(Mat original, List<YoloResult> results, string[] labels)
        {
            _modelLock.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                return _primaryModel?.GenerateImageMat(original, results, labels);
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        public object? GetPrimaryLastMetrics()
        {
            _modelLock.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                return _primaryModel?.LastMetrics;
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        #endregion

        #region ͳ��

        /// <summary>
        /// 
        /// </summary>
        public void ResetStatistics()
        {
            PrimaryHitCount = 0;
            Auxiliary1HitCount = 0;
            Auxiliary2HitCount = 0;
            TotalInferenceCount = 0;
        }

        /// <summary>
        /// 
        /// </summary>
        public double PrimaryHitRate => TotalInferenceCount > 0 ? (double)PrimaryHitCount / TotalInferenceCount : 0;

        /// <summary>
        /// 
        /// </summary>
        public double Auxiliary1HitRate => TotalInferenceCount > 0 ? (double)Auxiliary1HitCount / TotalInferenceCount : 0;

        /// <summary>
        /// 
        /// </summary>
        public double Auxiliary2HitRate => TotalInferenceCount > 0 ? (double)Auxiliary2HitCount / TotalInferenceCount : 0;

        #endregion

        #region ������������

        /// <summary>
        /// 
        /// </summary>
        public void SetTaskMode(YoloTaskType taskType)
        {
            _modelLock.EnterWriteLock();
            try
            {
                ThrowIfDisposed();

                if (_primaryModel != null)
                    _primaryModel.TaskMode = taskType;
                if (_auxiliary1Model != null)
                    _auxiliary1Model.TaskMode = taskType;
                if (_auxiliary2Model != null)
                    _auxiliary2Model.TaskMode = taskType;
            }
            finally
            {
                _modelLock.ExitWriteLock();
            }
        }

        #endregion

        #region IDisposable

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MultiModelManager));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                YoloDetector? primaryModel;
                YoloDetector? auxiliary1Model;
                YoloDetector? auxiliary2Model;

                _modelLock.EnterWriteLock();
                try
                {
                    if (_disposed) return;

                    primaryModel = _primaryModel;
                    auxiliary1Model = _auxiliary1Model;
                    auxiliary2Model = _auxiliary2Model;
                    _primaryModel = null;
                    _auxiliary1Model = null;
                    _auxiliary2Model = null;
                    _primaryModelPath = "";
                    _auxiliary1ModelPath = "";
                    _auxiliary2ModelPath = "";
                    _disposed = true;
                }
                finally
                {
                    _modelLock.ExitWriteLock();
                }

                primaryModel?.Dispose();
                auxiliary1Model?.Dispose();
                auxiliary2Model?.Dispose();
                _modelLock.Dispose();
                return;
            }

            _disposed = true;
        }

        ~MultiModelManager()
        {
            Dispose(false);
        }

        #endregion
    }
}

