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
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// 
        /// 
        /// 
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
                // 
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
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ��ģ�������쳣: {ex.Message}");
                    }
                }

                // 
                if (!EnableFallback)
                {
                    result.UsedModel = ModelRole.Primary;
                    result.UsedModelName = System.IO.Path.GetFileName(_primaryModelPath);
                    result.UsedModelLabels = _primaryModel?.Labels ?? Array.Empty<string>();
                    return result;
                }

                // 
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
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��1�����쳣: {ex.Message}");
                    }
                }

                // 
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
                        System.Diagnostics.Debug.WriteLine($"[MultiModelManager] ����ģ��2�����쳣: {ex.Message}");
                    }
                }

                // 
                return result;
            }
        }

        /// <summary>
        /// 
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

            // 
            return await Task.Run(() => InferenceWithFallback(image, confidence, iouThreshold, globalIou, preprocessingMode), cancellationToken);
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

            if (_primaryModel == null)
                return new List<YoloResult>();

            return _primaryModel.Inference(image, confidence, iouThreshold, globalIou, preprocessingMode);
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

