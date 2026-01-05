// ============================================================================
// 文件名: MultiModelManager.cs
// 描述:   YOLO 多模型管理器 - 实现主模型 + 辅助模型的自动切换检测
//
// 功能概述:
//   - 管理1个主模型 + 2个辅助模型
//   - 当主模型未检测到目标时，自动切换到辅助模型
//   - 级联检测：主模型 → 辅助模型1 → 辅助模型2
//   - 每次检测后自动切回主模型优先
//
// 作者: ClearFrost Team
// 创建日期: 2026-01-05
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace YoloDetection
{
    /// <summary>
    /// 模型角色枚举
    /// </summary>
    public enum ModelRole
    {
        /// <summary>主模型</summary>
        Primary,
        /// <summary>辅助模型1</summary>
        Auxiliary1,
        /// <summary>辅助模型2</summary>
        Auxiliary2,
        /// <summary>没有模型可用</summary>
        None
    }

    /// <summary>
    /// 多模型推理结果
    /// </summary>
    public class MultiModelInferenceResult
    {
        /// <summary>检测结果列表</summary>
        public List<YoloResult> Results { get; set; } = new List<YoloResult>();

        /// <summary>使用的模型角色</summary>
        public ModelRole UsedModel { get; set; } = ModelRole.None;

        /// <summary>使用的模型名称</summary>
        public string UsedModelName { get; set; } = "";

        /// <summary>使用的模型的标签列表（关键！用于正确显示检测结果）</summary>
        public string[] UsedModelLabels { get; set; } = Array.Empty<string>();

        /// <summary>是否经过了模型切换</summary>
        public bool WasFallback { get; set; } = false;

        /// <summary>检测到目标数量</summary>
        public int DetectionCount => Results.Count;
    }

    /// <summary>
    /// YOLO 多模型管理器 - 实现主模型 + 辅助模型的自动切换检测
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

        private readonly object _lock = new object();
        private bool _disposed = false;

        #endregion

        #region 公共属性

        /// <summary>主模型路径</summary>
        public string PrimaryModelPath => _primaryModelPath;

        /// <summary>辅助模型1路径</summary>
        public string Auxiliary1ModelPath => _auxiliary1ModelPath;

        /// <summary>辅助模型2路径</summary>
        public string Auxiliary2ModelPath => _auxiliary2ModelPath;

        /// <summary>主模型是否已加载</summary>
        public bool IsPrimaryLoaded => _primaryModel != null;

        /// <summary>辅助模型1是否已加载</summary>
        public bool IsAuxiliary1Loaded => _auxiliary1Model != null;

        /// <summary>辅助模型2是否已加载</summary>
        public bool IsAuxiliary2Loaded => _auxiliary2Model != null;

        /// <summary>是否启用多模型切换</summary>
        public bool EnableFallback { get; set; } = true;

        /// <summary>主模型命中次数</summary>
        public int PrimaryHitCount { get; private set; }

        /// <summary>辅助模型1命中次数</summary>
        public int Auxiliary1HitCount { get; private set; }

        /// <summary>辅助模型2命中次数</summary>
        public int Auxiliary2HitCount { get; private set; }

        /// <summary>总推理次数</summary>
        public int TotalInferenceCount { get; private set; }

        /// <summary>最后使用的模型</summary>
        public ModelRole LastUsedModel { get; private set; } = ModelRole.None;

        /// <summary>获取主模型的标签列表</summary>
        public string[] PrimaryLabels => _primaryModel?.Labels ?? Array.Empty<string>();

        /// <summary>获取主模型实例（用于读取属性，仅限内部访问）</summary>
        internal YoloDetector? PrimaryDetector => _primaryModel;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建多模型管理器
        /// </summary>
        /// <param name="useGpu">是否使用GPU</param>
        /// <param name="gpuDeviceId">GPU设备ID</param>
        public MultiModelManager(bool useGpu = true, int gpuDeviceId = 0)
        {
            _useGpu = useGpu;
            _gpuDeviceId = gpuDeviceId;
        }

        #endregion

        #region 模型加载

        /// <summary>
        /// 加载主模型
        /// </summary>
        public void LoadPrimaryModel(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            lock (_lock)
            {
                // 释放旧模型
                _primaryModel?.Dispose();
                _primaryModel = null;
                _primaryModelPath = "";

                try
                {
                    _primaryModel = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);
                    _primaryModelPath = modelPath;
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型加载成功: {System.IO.Path.GetFileName(modelPath)}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型加载失败: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 加载辅助模型1
        /// </summary>
        public void LoadAuxiliary1Model(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            lock (_lock)
            {
                // 释放旧模型
                _auxiliary1Model?.Dispose();
                _auxiliary1Model = null;
                _auxiliary1ModelPath = "";

                try
                {
                    _auxiliary1Model = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);
                    _auxiliary1ModelPath = modelPath;
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1加载成功: {System.IO.Path.GetFileName(modelPath)}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1加载失败: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 加载辅助模型2
        /// </summary>
        public void LoadAuxiliary2Model(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return;

            lock (_lock)
            {
                // 释放旧模型
                _auxiliary2Model?.Dispose();
                _auxiliary2Model = null;
                _auxiliary2ModelPath = "";

                try
                {
                    _auxiliary2Model = new YoloDetector(modelPath, 0, _gpuDeviceId, _useGpu);
                    _auxiliary2ModelPath = modelPath;
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2加载成功: {System.IO.Path.GetFileName(modelPath)}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2加载失败: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// 卸载辅助模型1
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
        /// 卸载辅助模型2
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

        #region 推理方法

        /// <summary>
        /// 带自动切换的推理 (核心方法)
        /// 级联检测：主模型 → 辅助模型1 → 辅助模型2
        /// 无论结果如何，下次调用时总是从主模型开始
        /// </summary>
        public MultiModelInferenceResult InferenceWithFallback(
            Bitmap image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1)
        {
            ThrowIfDisposed();

            var result = new MultiModelInferenceResult();
            TotalInferenceCount++;

            lock (_lock)
            {
                // 1. 尝试主模型
                if (_primaryModel != null)
                {
                    try
                    {
                        var results = _primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                        if (results.Count > 0)
                        {
                            PrimaryHitCount++;
                            LastUsedModel = ModelRole.Primary;
                            result.Results = results;
                            result.UsedModel = ModelRole.Primary;
                            result.UsedModelName = System.IO.Path.GetFileName(_primaryModelPath);
                            result.UsedModelLabels = _primaryModel.Labels ?? Array.Empty<string>();
                            result.WasFallback = false;
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 主模型推理异常: {ex.Message}");
                    }
                }

                // 如果不启用切换，直接返回空结果
                if (!EnableFallback)
                {
                    result.UsedModel = ModelRole.Primary;
                    result.UsedModelName = System.IO.Path.GetFileName(_primaryModelPath);
                    result.UsedModelLabels = _primaryModel?.Labels ?? Array.Empty<string>();
                    return result;
                }

                // 2. 尝试辅助模型1
                if (_auxiliary1Model != null)
                {
                    try
                    {
                        var results = _auxiliary1Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                        if (results.Count > 0)
                        {
                            Auxiliary1HitCount++;
                            LastUsedModel = ModelRole.Auxiliary1;
                            result.Results = results;
                            result.UsedModel = ModelRole.Auxiliary1;
                            result.UsedModelName = System.IO.Path.GetFileName(_auxiliary1ModelPath);
                            result.UsedModelLabels = _auxiliary1Model.Labels ?? Array.Empty<string>();
                            result.WasFallback = true;
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型1推理异常: {ex.Message}");
                    }
                }

                // 3. 尝试辅助模型2（无论结果如何都返回）
                if (_auxiliary2Model != null)
                {
                    try
                    {
                        var results = _auxiliary2Model.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
                        Auxiliary2HitCount++;
                        LastUsedModel = ModelRole.Auxiliary2;
                        result.Results = results;
                        result.UsedModel = ModelRole.Auxiliary2;
                        result.UsedModelName = System.IO.Path.GetFileName(_auxiliary2ModelPath);
                        result.UsedModelLabels = _auxiliary2Model.Labels ?? Array.Empty<string>();
                        result.WasFallback = true;
                        return result;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] 辅助模型2推理异常: {ex.Message}");
                    }
                }

                // 没有任何模型可用
                return result;
            }
        }

        /// <summary>
        /// 异步版本的带自动切换推理
        /// </summary>
        public async Task<MultiModelInferenceResult> InferenceWithFallbackAsync(
            Bitmap image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            // 在线程池中执行
            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode), cancellationToken);
        }

        /// <summary>
        /// 仅使用主模型推理（不使用切换）
        /// </summary>
        public List<YoloResult> InferencePrimaryOnly(
            Bitmap image,
            float confidence = 0.5f,
            float iouThreshold = 0.3f,
            bool globalIou = false,
            int preprocessingMode = 1)
        {
            ThrowIfDisposed();

            if (_primaryModel == null)
                return new List<YoloResult>();

            return _primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
        }

        #endregion

        #region 统计

        /// <summary>
        /// 重置统计信息
        /// </summary>
        public void ResetStatistics()
        {
            PrimaryHitCount = 0;
            Auxiliary1HitCount = 0;
            Auxiliary2HitCount = 0;
            TotalInferenceCount = 0;
        }

        /// <summary>
        /// 获取主模型命中率
        /// </summary>
        public double PrimaryHitRate => TotalInferenceCount > 0 ? (double)PrimaryHitCount / TotalInferenceCount : 0;

        /// <summary>
        /// 获取辅助模型1命中率
        /// </summary>
        public double Auxiliary1HitRate => TotalInferenceCount > 0 ? (double)Auxiliary1HitCount / TotalInferenceCount : 0;

        /// <summary>
        /// 获取辅助模型2命中率
        /// </summary>
        public double Auxiliary2HitRate => TotalInferenceCount > 0 ? (double)Auxiliary2HitCount / TotalInferenceCount : 0;

        #endregion

        #region 任务类型设置

        /// <summary>
        /// 设置所有模型的任务类型
        /// </summary>
        public void SetTaskMode(YoloTaskType taskType)
        {
            if (_primaryModel != null)
                _primaryModel.TaskMode = taskType;
            if (_auxiliary1Model != null)
                _auxiliary1Model.TaskMode = taskType;
            if (_auxiliary2Model != null)
                _auxiliary2Model.TaskMode = taskType;
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
