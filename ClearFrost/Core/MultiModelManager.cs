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

        private readonly object _lock = new object();
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
        public bool IsPrimaryLoaded => _primaryModel != null;

        /// 
        public bool IsAuxiliary1Loaded => _auxiliary1Model != null;

        /// 
        public bool IsAuxiliary2Loaded => _auxiliary2Model != null;

        /// 
        public bool EnableFallback { get; set; } = true;

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
        public string[] PrimaryLabels => _primaryModel?.Labels ?? Array.Empty<string>();

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

            lock (_lock)
            {
                // 
                _primaryModel?.Dispose();
                _primaryModel = null;
                _primaryModelPath = "";

                try
                {
                    _primaryModel = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);
                    _primaryModelPath = modelPath;
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ��ģ�ͼ��سɹ�: {System.IO.Path.GetFileName(modelPath)}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ��ģ�ͼ���ʧ��: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void LoadAuxiliary1Model(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            lock (_lock)
            {
                // 
                _auxiliary1Model?.Dispose();
                _auxiliary1Model = null;
                _auxiliary1ModelPath = "";

                try
                {
                    _auxiliary1Model = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);
                    _auxiliary1ModelPath = modelPath;
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��1���سɹ�: {System.IO.Path.GetFileName(modelPath)}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��1����ʧ��: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void LoadAuxiliary2Model(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            lock (_lock)
            {
                // 
                _auxiliary2Model?.Dispose();
                _auxiliary2Model = null;
                _auxiliary2ModelPath = "";

                try
                {
                    _auxiliary2Model = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);
                    _auxiliary2ModelPath = modelPath;
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��2���سɹ�: {System.IO.Path.GetFileName(modelPath)}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��2����ʧ��: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void UnloadAuxiliary1Model()
        {
            lock (_lock)
            {
                _auxiliary1Model?.Dispose();
                _auxiliary1Model = null;
                _auxiliary1ModelPath = "";
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void UnloadAuxiliary2Model()
        {
            lock (_lock)
            {
                _auxiliary2Model?.Dispose();
                _auxiliary2Model = null;
                _auxiliary2ModelPath = "";
            }
        }

        #endregion

        #region ��������

        private static bool HasTargetLabelHit(IReadOnlyList<YoloResult> results, string[] labels, string? targetLabel)
        {
            if (results == null || results.Count == 0)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetLabel))
            {
                return true;
            }

            return results.Any(r =>
            {
                if (r.ClassId < 0 || r.ClassId >= labels.Length)
                {
                    return false;
                }

                return string.Equals(labels[r.ClassId], targetLabel, StringComparison.OrdinalIgnoreCase);
            });
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
            string? targetLabel = null)
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

            void CaptureBestResult(List<YoloResult> detections, ModelRole modelRole, string modelPath, string[] labels, bool wasFallback)
            {
                if (detections.Count == 0 || bestResults != null)
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
                primaryModel = _primaryModel;
                auxiliary1Model = _auxiliary1Model;
                auxiliary2Model = _auxiliary2Model;
                primaryModelPath = _primaryModelPath;
                auxiliary1ModelPath = _auxiliary1ModelPath;
                auxiliary2ModelPath = _auxiliary2ModelPath;
                enableFallback = EnableFallback;
            }

            // 主模型推理
            if (primaryModel != null)
            {
                try
                {
                    var primaryResults = primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    var primaryLabels = primaryModel.Labels ?? Array.Empty<string>();
                    CaptureBestResult(primaryResults, ModelRole.Primary, primaryModelPath, primaryLabels, false);
                    bool primaryHit = HasTargetLabelHit(primaryResults, primaryLabels, targetLabel);

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
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型检测到非目标标签，继续切换（目标: {targetLabel}）");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 主模型未检测到任何目标，尝试切换辅助模型...");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型推理异常: {ex.Message}");
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
                return result;
            }

            // 尝试辅助模型1
            if (auxiliary1Model != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[MultiModelManager] 切换到辅助模型1进行检测...");
                    var aux1Results = auxiliary1Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    var aux1Labels = auxiliary1Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux1Results, ModelRole.Auxiliary1, auxiliary1ModelPath, aux1Labels, true);
                    bool aux1Hit = HasTargetLabelHit(aux1Results, aux1Labels, targetLabel);

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
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1检测到非目标标签，继续切换（目标: {targetLabel}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1推理异常: {ex.Message}");
                }
            }

            // 尝试辅助模型2
            if (auxiliary2Model != null)
            {
                try
                {
                    var aux2Results = auxiliary2Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    var aux2Labels = auxiliary2Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux2Results, ModelRole.Auxiliary2, auxiliary2ModelPath, aux2Labels, true);
                    bool aux2Hit = HasTargetLabelHit(aux2Results, aux2Labels, targetLabel);

                    lock (_lock)
                    {
                        if (aux2Hit)
                        {
                            Auxiliary2HitCount++;
                        }
                        LastUsedModel = ModelRole.Auxiliary2;
                    }

                    if (aux2Hit)
                    {
                        result.Results = aux2Results;
                        result.UsedModel = ModelRole.Auxiliary2;
                        result.UsedModelName = System.IO.Path.GetFileName(auxiliary2ModelPath);
                        result.UsedModelLabels = aux2Labels;
                        result.WasFallback = true;
                        return result;
                    }

                    if (aux2Results.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2检测到非目标标签，结束切换（目标: {targetLabel}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��2�����쳣: {ex.Message}");
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

            return result;
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
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            // 异步执行推理
            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode, targetLabel), cancellationToken);
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
            string? targetLabel = null)
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

            void CaptureBestResult(List<YoloResult> detections, ModelRole modelRole, string modelPath, string[] labels, bool wasFallback)
            {
                if (detections.Count == 0 || bestResults != null)
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
                primaryModel = _primaryModel;
                auxiliary1Model = _auxiliary1Model;
                auxiliary2Model = _auxiliary2Model;
                primaryModelPath = _primaryModelPath;
                auxiliary1ModelPath = _auxiliary1ModelPath;
                auxiliary2ModelPath = _auxiliary2ModelPath;
                enableFallback = EnableFallback;
            }

            if (primaryModel != null)
            {
                try
                {
                    var primaryResults = primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    var primaryLabels = primaryModel.Labels ?? Array.Empty<string>();
                    CaptureBestResult(primaryResults, ModelRole.Primary, primaryModelPath, primaryLabels, false);
                    bool primaryHit = HasTargetLabelHit(primaryResults, primaryLabels, targetLabel);

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
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型检测到非目标标签，继续切换（目标: {targetLabel}）");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 主模型未检测到任何目标，尝试切换辅助模型...");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型推理异常: {ex.Message}");
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
                return result;
            }

            if (auxiliary1Model != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[MultiModelManager] 切换到辅助模型1进行检测...");
                    var aux1Results = auxiliary1Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    var aux1Labels = auxiliary1Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux1Results, ModelRole.Auxiliary1, auxiliary1ModelPath, aux1Labels, true);
                    bool aux1Hit = HasTargetLabelHit(aux1Results, aux1Labels, targetLabel);

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
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1检测到非目标标签，继续切换（目标: {targetLabel}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1推理异常: {ex.Message}");
                }
            }

            if (auxiliary2Model != null)
            {
                try
                {
                    var aux2Results = auxiliary2Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                    var aux2Labels = auxiliary2Model.Labels ?? Array.Empty<string>();
                    CaptureBestResult(aux2Results, ModelRole.Auxiliary2, auxiliary2ModelPath, aux2Labels, true);
                    bool aux2Hit = HasTargetLabelHit(aux2Results, aux2Labels, targetLabel);

                    lock (_lock)
                    {
                        if (aux2Hit)
                        {
                            Auxiliary2HitCount++;
                        }
                        LastUsedModel = ModelRole.Auxiliary2;
                    }

                    if (aux2Hit)
                    {
                        result.Results = aux2Results;
                        result.UsedModel = ModelRole.Auxiliary2;
                        result.UsedModelName = System.IO.Path.GetFileName(auxiliary2ModelPath);
                        result.UsedModelLabels = aux2Labels;
                        result.WasFallback = true;
                        return result;
                    }

                    if (aux2Results.Count > 0 && !string.IsNullOrWhiteSpace(targetLabel))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2检测到非目标标签，结束切换（目标: {targetLabel}）");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2推理异常: {ex.Message}");
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

            return result;
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
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode, targetLabel), cancellationToken);
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
            ThrowIfDisposed();

            YoloDetector? primaryModel;
            lock (_lock)
            {
                primaryModel = _primaryModel;
            }

            if (primaryModel == null)
                return new List<YoloResult>();

            return primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
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
            YoloDetector? primaryModel;
            YoloDetector? auxiliary1Model;
            YoloDetector? auxiliary2Model;

            lock (_lock)
            {
                primaryModel = _primaryModel;
                auxiliary1Model = _auxiliary1Model;
                auxiliary2Model = _auxiliary2Model;
            }

            if (primaryModel != null)
                primaryModel.TaskMode = taskType;
            if (auxiliary1Model != null)
                auxiliary1Model.TaskMode = taskType;
            if (auxiliary2Model != null)
                auxiliary2Model.TaskMode = taskType;
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
                lock (_lock)
                {
                    _primaryModel?.Dispose();
                    _auxiliary1Model?.Dispose();
                    _auxiliary2Model?.Dispose();

                    _primaryModel = null;
                    _auxiliary1Model = null;
                    _auxiliary2Model = null;
                }
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

