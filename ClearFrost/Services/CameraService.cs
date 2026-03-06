using ClearFrost.Config;
using ClearFrost.Hardware;
// ============================================================================
// 文件名: CameraService.cs
// 作者: 蘅芜君
// 描述:   相机服务实现类
//
// 功能:
//   - 管理相机的连接和断开
//   - 提供统一的图像采集接口（自动抓取/软触发）
//   - 管理曝光、增益等参数设置
//   - 负责采集线程的生命周期管理
// ============================================================================

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using ClearFrost.Interfaces;
using MVSDK_Net;

namespace ClearFrost.Services
{
    /// <summary>
    /// 相机服务实现类，提供相机的控制和图像获取功能
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

        #region 公共属性

        public bool IsOpen => _cameraManager.ActiveCamera?.IsOpen ?? false;
        public string CameraName => _cameraManager.ActiveCamera?.Config.DisplayName ?? "未连接";
        public bool IsGrabbing => _cameraManager.ActiveCamera?.Camera.IMV_IsGrabbing() ?? false;
        public string? LastError { get; private set; }

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

        /// <summary>
        /// 打开指定序列号的相机
        /// </summary>
        /// <param name="serialNumber">相机序列号</param>
        /// <param name="manufacturer">厂商名称</param>
        /// <returns>成功返回 true</returns>
        public bool Open(string serialNumber, string manufacturer)
        {
            serialNumber = serialNumber?.Trim() ?? string.Empty;
            manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? "Huaray" : manufacturer.Trim();

            try
            {
                if (string.IsNullOrWhiteSpace(serialNumber))
                {
                    return FailOpen("未配置相机序列号，无法连接");
                }

                var instance = _cameraManager.Cameras.FirstOrDefault(c =>
                    string.Equals(c.Config.SerialNumber?.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase));

                if (instance == null)
                {
                    var newConfig = new CameraConfig
                    {
                        SerialNumber = serialNumber,
                        Manufacturer = manufacturer,
                        DisplayName = $"Camera-{serialNumber}",
                        IsEnabled = true
                    };
                    bool added = _cameraManager.AddCamera(newConfig);
                    if (!added)
                    {
                        return FailOpen($"未找到序列号为 {serialNumber} 的相机（厂商: {manufacturer}）");
                    }

                    instance = _cameraManager.Cameras.FirstOrDefault(c =>
                        string.Equals(c.Config.SerialNumber?.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase));
                }

                if (instance == null)
                {
                    return FailOpen($"未找到序列号为 {serialNumber} 的相机（厂商: {manufacturer}）");
                }

                // 同步活动相机，保证后续 StartCapture/CaptureFrame 操作目标一致
                _cameraManager.ActiveCameraId = instance.Id;

                bool success = instance.Open();
                if (success)
                {
                    LastError = null;
                    ConnectionChanged?.Invoke(true);
                    Debug.WriteLine($"[CameraService] 相机已打开 (SDK): {serialNumber}");
                    return true;
                }

                return FailOpen(
                    $"打开相机失败: {instance.Config.DisplayName} (序列号: {serialNumber}, 错误码: {instance.LastOpenResult})");
            }
            catch (Exception ex)
            {
                return FailOpen($"打开相机异常: {ex.Message} (序列号: {serialNumber})");
            }
        }

        /// <summary>
        /// 关闭当前相机并停止采集
        /// </summary>
        public void Close()
        {
            try
            {
                StopCapture();

                var activeCamera = _cameraManager.ActiveCamera;
                if (activeCamera != null)
                {
                    // 使用 CameraInstance.Close() 对齐生命周期，避免重复销毁句柄
                    activeCamera.Close();

                    ConnectionChanged?.Invoke(false);
                    Debug.WriteLine("[CameraService] 相机已关闭");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraService] 关闭相机异常: {ex.Message}");
            }
        }

        private bool FailOpen(string message)
        {
            LastError = message;
            ErrorOccurred?.Invoke(message);
            return false;
        }

        #endregion

        #region 采集控制

        /// <summary>
        /// 启动后台采集线程
        /// </summary>
        public void StartCapture()
        {
            var activeCamera = _cameraManager.ActiveCamera;
            if (activeCamera == null)
            {
                return;
            }

            try
            {
                if (!activeCamera.Camera.IMV_IsGrabbing())
                {
                    int res = activeCamera.Camera.IMV_StartGrabbing();
                    if (res != IMVDefine.IMV_OK)
                    {
                        ErrorOccurred?.Invoke($"启动采集失败: {res}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"启动采集异常: {ex.Message}");
                return;
            }

            // 仅在直接 provider 实例下启动采集线程；适配器/SDK 模式不需要后台轮询线程
            if (activeCamera.Camera is not ICameraProvider)
            {
                return;
            }

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

        /// <summary>
        /// 停止采集线程
        /// </summary>
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

            try
            {
                var activeCamera = _cameraManager.ActiveCamera;
                if (activeCamera != null && activeCamera.Camera.IMV_IsGrabbing())
                {
                    activeCamera.Camera.IMV_StopGrabbing();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraService] StopGrabbing failed: {ex.Message}");
            }

            Debug.WriteLine("[CameraService] 停止采集");
        }

        /// <summary>
        /// 执行一次软触发（仅在软触发模式下有效）
        /// </summary>
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

        /// <summary>
        /// 触发拍照并获取帧（原子操作: TriggerSoftware -> GetFrame -> 转 Mat -> ReleaseFrame）
        /// </summary>
        /// <param name="timeoutMs">取帧超时（毫秒）</param>
        /// <returns>成功返回图像，失败返回 null</returns>
        public Mat? CaptureFrame(int timeoutMs = 3000)
        {
            var camera = _cameraManager.ActiveCamera?.Camera;
            if (camera == null)
            {
                return null;
            }

            IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
            bool shouldReleaseFrame = false;

            try
            {
                int res = camera.IMV_ExecuteCommandFeature("TriggerSoftware");
                if (res != IMVDefine.IMV_OK)
                {
                    return null;
                }

                res = camera.IMV_GetFrame(ref frame, timeoutMs);
                shouldReleaseFrame = res == IMVDefine.IMV_OK;
                if (!shouldReleaseFrame)
                {
                    return null;
                }

                if (frame.frameInfo.size == 0 || frame.pData == IntPtr.Zero)
                {
                    return null;
                }

                Mat mat = ConvertFrameToMat(frame);
                lock (_frameLock)
                {
                    _lastFrame?.Dispose();
                    _lastFrame = mat.Clone();
                }

                return mat;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraService] CaptureFrame failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (shouldReleaseFrame)
                {
                    camera.IMV_ReleaseFrame(ref frame);
                }
            }
        }

        /// <summary>
        /// 后台采集循环方法
        /// </summary>
        private void CaptureLoop()
        {
            var token = _captureCts?.Token ?? CancellationToken.None;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var activeCamera = _cameraManager.ActiveCamera;
                    if (activeCamera?.Camera is not ICameraProvider provider)
                    {
                        Thread.Sleep(30);
                        continue;
                    }

                    using var cameraFrame = provider.GetFrame(500);
                    if (cameraFrame != null && cameraFrame.DataPtr != IntPtr.Zero && cameraFrame.Width > 0 && cameraFrame.Height > 0)
                    {
                        var matType = cameraFrame.PixelFormat == CameraPixelFormat.Mono8
                            ? MatType.CV_8UC1
                            : MatType.CV_8UC3;
                        using var tempMat = new Mat(cameraFrame.Height, cameraFrame.Width, matType, cameraFrame.DataPtr);
                        Mat capturedFrame = tempMat.Clone();

                        lock (_frameLock)
                        {
                            _lastFrame?.Dispose();
                            _lastFrame = capturedFrame;
                        }

                        var frameCapturedHandler = FrameCaptured;
                        if (frameCapturedHandler != null)
                        {
                            using Mat eventFrame = capturedFrame.Clone();
                            frameCapturedHandler.Invoke(eventFrame);
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

        /// <summary>
        /// 将相机帧转换为 OpenCV Mat 格式
        /// </summary>
        /// <param name="frame">SDK 原始帧</param>
        /// <returns>OpenCV Mat（Mono8）</returns>
        private static Mat ConvertFrameToMat(IMVDefine.IMV_Frame frame)
        {
            int width = (int)frame.frameInfo.width;
            int height = (int)frame.frameInfo.height;
            int srcStride = width + (int)frame.frameInfo.paddingX;

            Mat mat = new Mat(height, width, MatType.CV_8UC1);

            try
            {
                int dstStride = (int)mat.Step();

                unsafe
                {
                    byte* srcPtr = (byte*)frame.pData.ToPointer();
                    byte* dstPtr = (byte*)mat.Data.ToPointer();

                    if (srcStride == dstStride)
                    {
                        long totalBytes = (long)srcStride * height;
                        Buffer.MemoryCopy(srcPtr, dstPtr, totalBytes, totalBytes);
                    }
                    else
                    {
                        for (int row = 0; row < height; row++)
                        {
                            Buffer.MemoryCopy(
                                srcPtr + (long)row * srcStride,
                                dstPtr + (long)row * dstStride,
                                width,
                                width);
                        }
                    }
                }

                return mat;
            }
            catch
            {
                mat.Dispose();
                throw;
            }
        }

        #endregion

        #region 参数设置

        /// <summary>
        /// 设置曝光时间
        /// </summary>
        /// <param name="exposureUs">曝光时间（微秒）</param>
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

        /// <summary>
        /// 设置增益值
        /// </summary>
        /// <param name="gain">增益值</param>
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

