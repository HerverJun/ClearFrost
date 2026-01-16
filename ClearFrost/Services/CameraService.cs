using ClearFrost.Config;
using ClearFrost.Hardware;
// ============================================================================
// 
// 
//
// 
// 
// 
// 
// ============================================================================

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    /// <summary>
    /// 
    /// </summary>
    public class CameraService : ICameraService
    {
        #region ˽���ֶ�

        private readonly CameraManager _cameraManager;
        private CancellationTokenSource? _captureCts;
        private Thread? _captureThread;
        private Mat? _lastFrame;
        private readonly object _frameLock = new object();
        private bool _disposed;

        #endregion

        #region �¼�

        public event Action<Mat>? FrameCaptured;
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? ErrorOccurred;

        #endregion

        #region ����

        public bool IsOpen => _cameraManager.ActiveCamera?.IsOpen ?? false;
        public string CameraName => _cameraManager.ActiveCamera?.Config.DisplayName ?? "δ����";

        public Mat? LastFrame
        {
            get
            {
                lock (_frameLock)
                {
                    return _lastFrame?.Clone();
                }
            }
        }

        #endregion

        #region ���캯��

        public CameraService(bool debugMode = false)
        {
            _cameraManager = new CameraManager(debugMode);
        }

        public CameraService(CameraManager cameraManager)
        {
            _cameraManager = cameraManager ?? throw new ArgumentNullException(nameof(cameraManager));
        }

        #endregion

        #region ��/�ر�

        public bool Open(string serialNumber, string manufacturer)
        {
            try
            {
                // 
                var instance = _cameraManager.Cameras.FirstOrDefault(c => c.Config.SerialNumber == serialNumber);

                if (instance == null)
                {
                    // 
                    var newConfig = new CameraConfig
                    {
                        SerialNumber = serialNumber,
                        Manufacturer = manufacturer,
                        DisplayName = $"Camera-{serialNumber}",
                        IsEnabled = true
                    };
                    _cameraManager.AddCamera(newConfig);
                    instance = _cameraManager.Cameras.FirstOrDefault(c => c.Config.SerialNumber == serialNumber);
                }

                if (instance == null)
                {
                    ErrorOccurred?.Invoke("�޷������������");
                    return false;
                }

                // 
                if (instance.Camera is ICameraProvider provider)
                {
                    bool opened = provider.Open(serialNumber);
                    if (opened)
                    {
                        ConnectionChanged?.Invoke(true);
                        Debug.WriteLine($"[CameraService] ����Ѵ�: {serialNumber}");
                        return true;
                    }
                }

                // 
                bool success = instance.Open();
                if (success)
                {
                    ConnectionChanged?.Invoke(true);
                    Debug.WriteLine($"[CameraService] ����Ѵ� (SDK): {serialNumber}");
                    return true;
                }

                ErrorOccurred?.Invoke("�����ʧ��");
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"������쳣: {ex.Message}");
                return false;
            }
        }

        public void Close()
        {
            try
            {
                StopCapture();

                var activeCamera = _cameraManager.ActiveCamera;
                if (activeCamera != null)
                {
                    if (activeCamera.Camera is ICameraProvider provider)
                    {
                        provider.Dispose();
                    }
                    else
                    {
                        activeCamera.Camera.IMV_StopGrabbing();
                        activeCamera.Camera.IMV_Close();
                        activeCamera.Camera.IMV_DestroyHandle();
                    }

                    ConnectionChanged?.Invoke(false);
                    Debug.WriteLine("[CameraService] ����ѹر�");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraService] �ر�����쳣: {ex.Message}");
            }
        }

        #endregion

        #region �ɼ�����

        public void StartCapture()
        {
            if (_captureThread != null && _captureThread.IsAlive)
            {
                return;
            }

            _captureCts = new CancellationTokenSource();
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "CameraService.Capture"
            };
            _captureThread.Start();

            Debug.WriteLine("[CameraService] ��ʼ�ɼ�");
        }

        public void StopCapture()
        {
            _captureCts?.Cancel();

            if (_captureThread != null && _captureThread.IsAlive)
            {
                _captureThread.Join(1000);
            }

            _captureCts?.Dispose();
            _captureCts = null;
            _captureThread = null;

            Debug.WriteLine("[CameraService] ֹͣ�ɼ�");
        }

        public void TriggerOnce()
        {
            try
            {
                var activeCamera = _cameraManager.ActiveCamera;
                if (activeCamera == null) return;

                if (activeCamera.Camera is ICameraProvider provider)
                {
                    provider.ExecuteSoftwareTrigger();
                }
                else
                {
                    activeCamera.Camera.IMV_ExecuteCommandFeature("TriggerSoftware");
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"�����ɼ�ʧ��: {ex.Message}");
            }
        }

        private void CaptureLoop()
        {
            var token = _captureCts?.Token ?? CancellationToken.None;
            var activeCamera = _cameraManager.ActiveCamera;

            if (activeCamera == null) return;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (activeCamera.Camera is ICameraProvider provider)
                    {
                        using var cameraFrame = provider.GetFrame(500);
                        if (cameraFrame != null && cameraFrame.DataPtr != IntPtr.Zero && cameraFrame.Width > 0 && cameraFrame.Height > 0)
                        {
                            // 
                            var matType = cameraFrame.PixelFormat == CameraPixelFormat.Mono8
                                ? MatType.CV_8UC1
                                : MatType.CV_8UC3;
                            using var tempMat = new Mat(cameraFrame.Height, cameraFrame.Width, matType, cameraFrame.DataPtr);

                            lock (_frameLock)
                            {
                                _lastFrame?.Dispose();
                                _lastFrame = tempMat.Clone();
                            }

                            FrameCaptured?.Invoke(_lastFrame.Clone());
                        }
                    }

                    Thread.Sleep(10);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CameraService] �ɼ��쳣: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        #endregion

        #region ��������

        public void SetExposure(double exposureUs)
        {
            try
            {
                var activeCamera = _cameraManager.ActiveCamera;
                if (activeCamera == null) return;

                if (activeCamera.Camera is ICameraProvider provider)
                {
                    provider.SetExposure(exposureUs);
                }
                else
                {
                    activeCamera.Camera.IMV_SetDoubleFeatureValue("ExposureTime", exposureUs);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"�����ع�ʧ��: {ex.Message}");
            }
        }

        public void SetGain(double gain)
        {
            try
            {
                var activeCamera = _cameraManager.ActiveCamera;
                if (activeCamera == null) return;

                if (activeCamera.Camera is ICameraProvider provider)
                {
                    provider.SetGain(gain);
                }
                else
                {
                    activeCamera.Camera.IMV_SetDoubleFeatureValue("Gain", gain);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"��������ʧ��: {ex.Message}");
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopCapture();
            Close();

            lock (_frameLock)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}

