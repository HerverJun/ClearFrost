// ============================================================================
// 文件名: CameraService.cs
// 描述:   相机服务实现
//
// 功能:
//   - 封装 CameraManager 提供统一的相机控制
//   - 异步帧采集和事件通知
//   - 支持多品牌相机 (MindVision, Hikvision)
// ============================================================================

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using YOLO.Interfaces;

namespace YOLO.Services
{
    /// <summary>
    /// 相机服务实现
    /// </summary>
    public class CameraService : ICameraService
    {
        #region 私有字段

        private readonly CameraManager _cameraManager;
        private CancellationTokenSource? _captureCts;
        private Thread? _captureThread;
        private Mat? _lastFrame;
        private readonly object _frameLock = new object();
        private bool _disposed;

        #endregion

        #region 事件

        public event Action<Mat>? FrameCaptured;
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        public bool IsOpen => _cameraManager.ActiveCamera?.IsOpen ?? false;
        public string CameraName => _cameraManager.ActiveCamera?.Config.DisplayName ?? "未连接";

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

        #region 构造函数

        public CameraService(bool debugMode = false)
        {
            _cameraManager = new CameraManager(debugMode);
        }

        public CameraService(CameraManager cameraManager)
        {
            _cameraManager = cameraManager ?? throw new ArgumentNullException(nameof(cameraManager));
        }

        #endregion

        #region 打开/关闭

        public bool Open(string serialNumber, string manufacturer)
        {
            try
            {
                // 查找匹配的相机配置
                var instance = _cameraManager.Cameras.FirstOrDefault(c => c.Config.SerialNumber == serialNumber);

                if (instance == null)
                {
                    // 创建新配置
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
                    ErrorOccurred?.Invoke("无法创建相机配置");
                    return false;
                }

                // 尝试通过 ICameraProvider 接口打开
                if (instance.Camera is ICameraProvider provider)
                {
                    bool opened = provider.Open(serialNumber);
                    if (opened)
                    {
                        ConnectionChanged?.Invoke(true);
                        Debug.WriteLine($"[CameraService] 相机已打开: {serialNumber}");
                        return true;
                    }
                }

                // 尝试使用传统 SDK 接口打开
                bool success = instance.Open();
                if (success)
                {
                    ConnectionChanged?.Invoke(true);
                    Debug.WriteLine($"[CameraService] 相机已打开 (SDK): {serialNumber}");
                    return true;
                }

                ErrorOccurred?.Invoke("打开相机失败");
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"打开相机异常: {ex.Message}");
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
                    Debug.WriteLine("[CameraService] 相机已关闭");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraService] 关闭相机异常: {ex.Message}");
            }
        }

        #endregion

        #region 采集控制

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

            Debug.WriteLine("[CameraService] 开始采集");
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

            Debug.WriteLine("[CameraService] 停止采集");
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
                ErrorOccurred?.Invoke($"触发采集失败: {ex.Message}");
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
                            // 将 CameraFrame 转换为 Mat
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
                    Debug.WriteLine($"[CameraService] 采集异常: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        #endregion

        #region 参数设置

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
                ErrorOccurred?.Invoke($"设置曝光失败: {ex.Message}");
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
                ErrorOccurred?.Invoke($"设置增益失败: {ex.Message}");
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
