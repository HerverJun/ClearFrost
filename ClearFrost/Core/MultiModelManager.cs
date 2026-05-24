// ============================================================================
// 文件名: MultiModelManager.cs
// 作者: 蘅芜君
// 描述:   多模型推理管理器
//
// 功能:
//   - 管理主模型和两个辅助模型
//   - 根据目标规则自动回退到辅助模型
//   - 记录模型命中统计并提供结果渲染入口
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
    /// 多模型推理时使用的模型角色。
    /// </summary>
    public enum ModelRole
    {
        /// <summary>主模型。</summary>
        Primary,
        /// <summary>辅助模型 1。</summary>
        Auxiliary1,
        /// <summary>辅助模型 2。</summary>
        Auxiliary2,
        /// <summary>未使用任何模型。</summary>
        None
    }

    /// <summary>
    /// 多模型推理结果。
    /// </summary>
    public class MultiModelInferenceResult
    {
        /// <summary>检测结果列表。</summary>
        public List<YoloResult> Results { get; set; } = new List<YoloResult>();

        /// <summary>最终采用的模型角色。</summary>
        public ModelRole UsedModel { get; set; } = ModelRole.None;

        /// <summary>最终采用的模型文件名。</summary>
        public string UsedModelName { get; set; } = "";

        /// <summary>最终采用模型的标签数组。</summary>
        public string[] UsedModelLabels { get; set; } = Array.Empty<string>();

        /// <summary>是否使用了辅助模型回退。</summary>
        public bool WasFallback { get; set; } = false;

        /// <summary>
        /// 本次推理实际尝试的模型数量，包含主模型。
        /// </summary>
        public int FallbackAttemptCount { get; set; }

        /// <summary>
        /// 回退未继续或未命中的原因，空字符串表示已命中或无需提示。
        /// </summary>
        public string FallbackSkippedReason { get; set; } = string.Empty;

        /// <summary>
        /// 推理是否发生错误。所有候选模型均推理失败时置为 true。
        /// </summary>
        public bool HasError { get; set; }

        /// <summary>
        /// 推理错误说明。
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>检测目标数量。</summary>
        public int DetectionCount => Results.Count;
    }

    /// <summary>
    /// 单个候选模型的推理结果上下文。
    /// </summary>
    public sealed class MultiModelCandidate
    {
        /// <summary>该模型本次输出的检测框。</summary>
        public IReadOnlyList<YoloResult> Results { get; init; } = Array.Empty<YoloResult>();

        /// <summary>该模型对应的标签数组。</summary>
        public string[] Labels { get; init; } = Array.Empty<string>();

        /// <summary>该模型在多模型链路中的角色。</summary>
        public ModelRole ModelRole { get; init; } = ModelRole.None;

        /// <summary>该模型文件名，用于日志和前端展示。</summary>
        public string ModelName { get; init; } = string.Empty;

        /// <summary>该候选是否来自辅助模型回退。</summary>
        public bool WasFallback { get; init; }
    }

    /// <summary>
    /// 业务规则对候选模型结果的评估结论。
    /// </summary>
    public sealed class MultiModelCandidateEvaluation
    {
        /// <summary>候选结果是否满足当前检测规则。</summary>
        public bool IsMatch { get; init; }

        /// <summary>候选排序分数；未完全命中时也用于选择最接近规则的结果。</summary>
        public int Score { get; init; }

        /// <summary>规则评估摘要，用于调试日志。</summary>
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// 多模型回退过程中用于评估每个候选结果的业务规则委托。
    /// </summary>
    public delegate MultiModelCandidateEvaluation MultiModelCandidateEvaluator(MultiModelCandidate candidate);

    /// <summary>
    /// 管理主模型与辅助模型，并按规则执行多模型回退推理。
    /// </summary>
    public class MultiModelManager : IDisposable
    {
        #region 私有字段

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

        #region 属性

        /// <summary>主模型路径。</summary>
        public string PrimaryModelPath => _primaryModelPath;

        /// <summary>辅助模型 1 路径。</summary>
        public string Auxiliary1ModelPath => _auxiliary1ModelPath;

        /// <summary>辅助模型 2 路径。</summary>
        public string Auxiliary2ModelPath => _auxiliary2ModelPath;

        /// <summary>主模型是否已加载。</summary>
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

        /// <summary>辅助模型 1 是否已加载。</summary>
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

        /// <summary>辅助模型 2 是否已加载。</summary>
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

        /// <summary>是否启用辅助模型回退。</summary>
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

        /// <summary>主模型命中次数。</summary>
        public int PrimaryHitCount { get; private set; }

        /// <summary>辅助模型 1 命中次数。</summary>
        public int Auxiliary1HitCount { get; private set; }

        /// <summary>辅助模型 2 命中次数。</summary>
        public int Auxiliary2HitCount { get; private set; }

        /// <summary>总推理次数。</summary>
        public int TotalInferenceCount { get; private set; }

        /// <summary>上一次采用的模型角色。</summary>
        public ModelRole LastUsedModel { get; private set; } = ModelRole.None;

        /// <summary>主模型标签数组。</summary>
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

        /// <summary>当前主检测器实例。</summary>
        internal YoloDetector? PrimaryDetector => _primaryModel;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化多模型管理器。
        /// </summary>
        /// <param name="useGpu">是否启用 GPU 推理。</param>
        /// <param name="gpuDeviceId">GPU 设备 ID。</param>
        public MultiModelManager(bool useGpu = true, int gpuDeviceId = 0)
        {
            _useGpu = useGpu;
            _gpuDeviceId = gpuDeviceId;
        }

        #endregion

        #region 模型加载

        /// <summary>
        /// 加载或替换主模型。
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

                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型加载成功: {System.IO.Path.GetFileName(modelPath)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型加载失败: {ex.Message}");
                throw;
            }
            finally
            {
                oldModel?.Dispose();
                newModel?.Dispose();
            }
        }

        /// <summary>
        /// 加载或替换辅助模型 1。
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

                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1加载成功: {System.IO.Path.GetFileName(modelPath)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1加载失败: {ex.Message}");
                throw;
            }
            finally
            {
                oldModel?.Dispose();
                newModel?.Dispose();
            }
        }

        /// <summary>
        /// 加载或替换辅助模型 2。
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

                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2加载成功: {System.IO.Path.GetFileName(modelPath)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2加载失败: {ex.Message}");
                throw;
            }
            finally
            {
                oldModel?.Dispose();
                newModel?.Dispose();
            }
        }

        /// <summary>
        /// 卸载辅助模型 1。
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
        /// 卸载辅助模型 2。
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

        #region 推理逻辑

        /// <summary>
        /// 统计候选结果中指定标签的命中数量。
        /// </summary>
        /// <remarks>未配置目标标签时，所有检测框都视为命中。</remarks>
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

        /// <summary>
        /// 判断检测结果是否满足简单目标标签数量规则。
        /// </summary>
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

        /// <summary>
        /// 在没有外部规则评估器时，选择更接近目标数量的候选结果。
        /// </summary>
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
                // 目标数量为 0 表示希望该标签不出现，因此目标标签越少越好。
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
        /// 在启用规则评估器时，优先保留规则命中候选，其次保留分数更高的候选。
        /// </summary>
        private static bool ShouldReplaceEvaluatedResult(
            MultiModelCandidateEvaluation candidateEvaluation,
            IReadOnlyList<YoloResult> candidateResults,
            string[] candidateLabels,
            MultiModelCandidateEvaluation? currentEvaluation,
            IReadOnlyList<YoloResult>? currentBestResults,
            string[] currentBestLabels,
            string? targetLabel,
            int targetCount)
        {
            if (currentEvaluation == null || currentBestResults == null)
            {
                return true;
            }

            if (candidateEvaluation.IsMatch != currentEvaluation.IsMatch)
            {
                return candidateEvaluation.IsMatch;
            }

            if (candidateEvaluation.Score != currentEvaluation.Score)
            {
                return candidateEvaluation.Score > currentEvaluation.Score;
            }

            return ShouldReplaceBestResult(
                candidateResults,
                candidateLabels,
                currentBestResults,
                currentBestLabels,
                targetLabel,
                targetCount);
        }

        private void RecordModelHit(ModelRole role)
        {
            lock (_lock)
            {
                switch (role)
                {
                    case ModelRole.Primary:
                        PrimaryHitCount++;
                        break;
                    case ModelRole.Auxiliary1:
                        Auxiliary1HitCount++;
                        break;
                    case ModelRole.Auxiliary2:
                        Auxiliary2HitCount++;
                        break;
                }

                LastUsedModel = role;
            }
        }

        private static void PopulateInferenceResult(
            MultiModelInferenceResult result,
            List<YoloResult> detections,
            ModelRole modelRole,
            string modelPath,
            string[] labels,
            bool wasFallback)
        {
            result.Results = detections;
            result.UsedModel = modelRole;
            result.UsedModelName = System.IO.Path.GetFileName(modelPath);
            result.UsedModelLabels = labels;
            result.WasFallback = wasFallback;
        }

        private static MultiModelInferenceResult CompleteInferenceResult(
            MultiModelInferenceResult result,
            int attemptedModelCount,
            string? fallbackSkippedReason = null)
        {
            result.FallbackAttemptCount = attemptedModelCount;
            result.FallbackSkippedReason = fallbackSkippedReason ?? string.Empty;
            return result;
        }

        private static string ResolveFallbackSkippedReason(
            bool enableFallback,
            YoloDetector? auxiliary1Model,
            YoloDetector? auxiliary2Model,
            int attemptedModelCount,
            int successfulInferenceCount)
        {
            if (!enableFallback)
            {
                return "FallbackDisabled";
            }

            if (auxiliary1Model == null && auxiliary2Model == null)
            {
                return "NoAuxiliaryModelLoaded";
            }

            if (attemptedModelCount > 0 && successfulInferenceCount == 0)
            {
                return "AllInferenceFailed";
            }

            return "NoCandidateMatched";
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
            int targetCount = 0,
            MultiModelCandidateEvaluator? candidateEvaluator = null)
        {
            // 推理期间持有读锁，防止 UI 切换模型或关闭系统时把正在使用的检测器释放掉。
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
            // 当没有候选完全命中规则时，保留最接近目标的结果供前端展示和追溯。
            List<YoloResult>? bestResults = null;
            ModelRole bestModelRole = ModelRole.None;
            string bestModelName = string.Empty;
            string[] bestModelLabels = Array.Empty<string>();
            bool bestWasFallback = false;
            MultiModelCandidateEvaluation? bestEvaluation = null;
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

            bool CaptureEvaluatedResult(
                List<YoloResult> detections,
                ModelRole modelRole,
                string modelPath,
                string[] labels,
                bool wasFallback,
                MultiModelCandidateEvaluation evaluation)
            {
                if (bestResults != null &&
                    !ShouldReplaceEvaluatedResult(
                        evaluation,
                        detections,
                        labels,
                        bestEvaluation,
                        bestResults,
                        bestModelLabels,
                        targetLabel,
                        targetCount))
                {
                    return false;
                }

                bestResults = detections;
                bestModelRole = modelRole;
                bestModelName = System.IO.Path.GetFileName(modelPath);
                bestModelLabels = labels;
                bestWasFallback = wasFallback;
                bestEvaluation = evaluation;
                return true;
            }

            bool TryAcceptCandidate(
                List<YoloResult> detections,
                ModelRole modelRole,
                string modelPath,
                string[] labels,
                bool wasFallback)
            {
                string modelName = System.IO.Path.GetFileName(modelPath);
                if (candidateEvaluator != null)
                {
                    // 复杂规则（ROI 后数量、标签组合等）由调用方评估，管理器只负责候选排序和回退链路。
                    MultiModelCandidateEvaluation evaluation = candidateEvaluator(new MultiModelCandidate
                    {
                        Results = detections,
                        Labels = labels,
                        ModelRole = modelRole,
                        ModelName = modelName,
                        WasFallback = wasFallback
                    });
                    CaptureEvaluatedResult(detections, modelRole, modelPath, labels, wasFallback, evaluation);
                    if (!evaluation.IsMatch)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MultiModelManager] {modelRole} 规则未满足，继续评估候选模型: {evaluation.Summary}");
                        return false;
                    }

                    RecordModelHit(modelRole);
                    PopulateInferenceResult(result, detections, modelRole, modelPath, labels, wasFallback);
                    return true;
                }

                CaptureBestResult(detections, modelRole, modelPath, labels, wasFallback);
                if (!IsTargetSatisfied(detections, labels, targetLabel, targetCount))
                {
                    return false;
                }

                RecordModelHit(modelRole);
                PopulateInferenceResult(result, detections, modelRole, modelPath, labels, wasFallback);
                return true;
            }

            // _lock 只保护统计字段和模型快照读取；_modelLock 的读锁负责延长检测器生命周期。
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

                    // 目标标签命中（或未配置目标标签时任意命中）才停止切换
                    if (TryAcceptCandidate(primaryResults, ModelRole.Primary, primaryModelPath, primaryLabels, false))
                    {
                        return CompleteInferenceResult(result, attemptedModelCount);
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
                    // 禁用回退时不继续尝试辅助模型，但仍返回主模型的最佳可追溯结果。
                    lock (_lock)
                    {
                        LastUsedModel = bestModelRole;
                    }

                    result.Results = bestResults;
                    result.UsedModel = bestModelRole;
                    result.UsedModelName = bestModelName;
                    result.UsedModelLabels = bestModelLabels;
                    result.WasFallback = bestWasFallback;
                    return CompleteInferenceResult(result, attemptedModelCount, "FallbackDisabled");
                }

                result.UsedModel = ModelRole.Primary;
                result.UsedModelName = System.IO.Path.GetFileName(primaryModelPath);
                result.UsedModelLabels = primaryModel?.Labels ?? Array.Empty<string>();
                MarkErrorIfAllAttemptsFailed(result, attemptedModelCount, successfulInferenceCount, inferenceErrors);
                return CompleteInferenceResult(result, attemptedModelCount, "FallbackDisabled");
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

                    if (TryAcceptCandidate(aux1Results, ModelRole.Auxiliary1, auxiliary1ModelPath, aux1Labels, true))
                    {
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 辅助模型1命中!");
                        return CompleteInferenceResult(result, attemptedModelCount);
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

                    if (TryAcceptCandidate(aux2Results, ModelRole.Auxiliary2, auxiliary2ModelPath, aux2Labels, true))
                    {
                        return CompleteInferenceResult(result, attemptedModelCount);
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
                // 所有候选都未完全命中时，返回评分最优的候选，便于人工复核和调参。
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

            return CompleteInferenceResult(
                result,
                attemptedModelCount,
                string.IsNullOrWhiteSpace(result.FallbackSkippedReason)
                    ? ResolveFallbackSkippedReason(enableFallback, auxiliary1Model, auxiliary2Model, attemptedModelCount, successfulInferenceCount)
                    : result.FallbackSkippedReason);
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        /// <summary>
        /// 仅在所有已尝试模型都推理失败时把结果标记为错误。
        /// </summary>
        /// <remarks>单个模型失败但其它模型成功时，仍允许回退链路给出有效检测结果。</remarks>
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
            MultiModelCandidateEvaluator? candidateEvaluator = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            // 异步执行推理
            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode, targetLabel, targetCount, candidateEvaluator), cancellationToken);
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
            int targetCount = 0,
            MultiModelCandidateEvaluator? candidateEvaluator = null)
        {
            // Mat 路径用于相机实时流，和 Bitmap 路径保持相同的锁语义与回退策略。
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
            // 当没有候选完全命中规则时，保留最接近目标的结果供前端展示和追溯。
            List<YoloResult>? bestResults = null;
            ModelRole bestModelRole = ModelRole.None;
            string bestModelName = string.Empty;
            string[] bestModelLabels = Array.Empty<string>();
            bool bestWasFallback = false;
            MultiModelCandidateEvaluation? bestEvaluation = null;
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

            bool CaptureEvaluatedResult(
                List<YoloResult> detections,
                ModelRole modelRole,
                string modelPath,
                string[] labels,
                bool wasFallback,
                MultiModelCandidateEvaluation evaluation)
            {
                if (bestResults != null &&
                    !ShouldReplaceEvaluatedResult(
                        evaluation,
                        detections,
                        labels,
                        bestEvaluation,
                        bestResults,
                        bestModelLabels,
                        targetLabel,
                        targetCount))
                {
                    return false;
                }

                bestResults = detections;
                bestModelRole = modelRole;
                bestModelName = System.IO.Path.GetFileName(modelPath);
                bestModelLabels = labels;
                bestWasFallback = wasFallback;
                bestEvaluation = evaluation;
                return true;
            }

            bool TryAcceptCandidate(
                List<YoloResult> detections,
                ModelRole modelRole,
                string modelPath,
                string[] labels,
                bool wasFallback)
            {
                string modelName = System.IO.Path.GetFileName(modelPath);
                if (candidateEvaluator != null)
                {
                    // 复杂规则（ROI 后数量、标签组合等）由调用方评估，管理器只负责候选排序和回退链路。
                    MultiModelCandidateEvaluation evaluation = candidateEvaluator(new MultiModelCandidate
                    {
                        Results = detections,
                        Labels = labels,
                        ModelRole = modelRole,
                        ModelName = modelName,
                        WasFallback = wasFallback
                    });
                    CaptureEvaluatedResult(detections, modelRole, modelPath, labels, wasFallback, evaluation);
                    if (!evaluation.IsMatch)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MultiModelManager] {modelRole} 规则未满足，继续评估候选模型: {evaluation.Summary}");
                        return false;
                    }

                    RecordModelHit(modelRole);
                    PopulateInferenceResult(result, detections, modelRole, modelPath, labels, wasFallback);
                    return true;
                }

                CaptureBestResult(detections, modelRole, modelPath, labels, wasFallback);
                if (!IsTargetSatisfied(detections, labels, targetLabel, targetCount))
                {
                    return false;
                }

                RecordModelHit(modelRole);
                PopulateInferenceResult(result, detections, modelRole, modelPath, labels, wasFallback);
                return true;
            }

            lock (_lock)
            {
                // 统计字段和模型引用快照放在同一个临界区，确保本次推理日志对应同一组模型。
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

                    if (TryAcceptCandidate(primaryResults, ModelRole.Primary, primaryModelPath, primaryLabels, false))
                    {
                        return CompleteInferenceResult(result, attemptedModelCount);
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
                    // 禁用回退时不继续尝试辅助模型，但仍返回主模型的最佳可追溯结果。
                    lock (_lock)
                    {
                        LastUsedModel = bestModelRole;
                    }

                    result.Results = bestResults;
                    result.UsedModel = bestModelRole;
                    result.UsedModelName = bestModelName;
                    result.UsedModelLabels = bestModelLabels;
                    result.WasFallback = bestWasFallback;
                    return CompleteInferenceResult(result, attemptedModelCount, "FallbackDisabled");
                }

                result.UsedModel = ModelRole.Primary;
                result.UsedModelName = System.IO.Path.GetFileName(primaryModelPath);
                result.UsedModelLabels = primaryModel?.Labels ?? Array.Empty<string>();
                MarkErrorIfAllAttemptsFailed(result, attemptedModelCount, successfulInferenceCount, inferenceErrors);
                return CompleteInferenceResult(result, attemptedModelCount, "FallbackDisabled");
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

                    if (TryAcceptCandidate(aux1Results, ModelRole.Auxiliary1, auxiliary1ModelPath, aux1Labels, true))
                    {
                        System.Diagnostics.Debug.WriteLine("[MultiModelManager] 辅助模型1命中!");
                        return CompleteInferenceResult(result, attemptedModelCount);
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

                    if (TryAcceptCandidate(aux2Results, ModelRole.Auxiliary2, auxiliary2ModelPath, aux2Labels, true))
                    {
                        return CompleteInferenceResult(result, attemptedModelCount);
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
                // 所有候选都未完全命中时，返回评分最优的候选，便于人工复核和调参。
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

            return CompleteInferenceResult(
                result,
                attemptedModelCount,
                string.IsNullOrWhiteSpace(result.FallbackSkippedReason)
                    ? ResolveFallbackSkippedReason(enableFallback, auxiliary1Model, auxiliary2Model, attemptedModelCount, successfulInferenceCount)
                    : result.FallbackSkippedReason);
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
            MultiModelCandidateEvaluator? candidateEvaluator = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode, targetLabel, targetCount, candidateEvaluator), cancellationToken);
        }

        /// <summary>
        /// 仅使用主模型执行推理。
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

        #region 统计

        /// <summary>
        /// 重置模型命中统计。
        /// </summary>
        public void ResetStatistics()
        {
            PrimaryHitCount = 0;
            Auxiliary1HitCount = 0;
            Auxiliary2HitCount = 0;
            TotalInferenceCount = 0;
        }

        /// <summary>
        /// 主模型命中率。
        /// </summary>
        public double PrimaryHitRate => TotalInferenceCount > 0 ? (double)PrimaryHitCount / TotalInferenceCount : 0;

        /// <summary>
        /// 辅助模型 1 命中率。
        /// </summary>
        public double Auxiliary1HitRate => TotalInferenceCount > 0 ? (double)Auxiliary1HitCount / TotalInferenceCount : 0;

        /// <summary>
        /// 辅助模型 2 命中率。
        /// </summary>
        public double Auxiliary2HitRate => TotalInferenceCount > 0 ? (double)Auxiliary2HitCount / TotalInferenceCount : 0;

        #endregion

        #region 推理参数设置

        /// <summary>
        /// 设置所有已加载模型的任务模式。
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

