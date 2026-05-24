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
        private const uint GvspPixelMono8 = 0x01080001;
        private const uint GvspPixelRgb8 = 0x02180014;
        private const uint GvspPixelBgr8 = 0x02180015;
        private const uint GvspPixelBayerGr8 = 0x01080008;
        private const uint GvspPixelBayerRg8 = 0x01080009;
        private const uint GvspPixelBayerGb8 = 0x0108000A;
        private const uint GvspPixelBayerBg8 = 0x0108000B;

        #region 私有字段

        private readonly CameraManager _cameraManager;
        private CancellationTokenSource? _captureCts;
        private Thread? _captureThread;
        private Mat? _lastFrame;
        private readonly object _frameLock = new object();
        private readonly SemaphoreSlim _cameraOperationLock = new SemaphoreSlim(1, 1);
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

        private void CacheLastFrameReference(Mat frame)
        {
            Mat lastFrame = CreateMatReference(frame);

            lock (_frameLock)
            {
                _lastFrame?.Dispose();
                _lastFrame = lastFrame;
            }
        }

        private static Mat CreateMatReference(Mat frame)
        {
            if (frame.Empty())
            {
                return frame.Clone();
            }

            // 保留引用计数即可让 LastFrame 跨调用方 Dispose 存活，无需热路径整图复制。
            return frame.SubMat(new Rect(0, 0, frame.Width, frame.Height));
        }

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
            _cameraOperationLock.Wait();
            try
            {
            if (_disposed)
            {
                return FailOpen("相机服务已释放");
            }

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
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        /// <summary>
        /// 关闭当前相机并停止采集
        /// </summary>
        public void Close()
        {
            _cameraOperationLock.Wait();
            try
            {
                CloseCore();
            }
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        private void CloseCore()
        {
            try
            {
                StopCaptureCore();

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

        public CameraInstance? SwitchActiveCamera(string cameraId)
        {
            _cameraOperationLock.Wait();
            try
            {
                StopCaptureCore();

                var previousCamera = _cameraManager.ActiveCamera;
                if (previousCamera != null && previousCamera.IsOpen)
                {
                    previousCamera.Close();
                }

                _cameraManager.ActiveCameraId = cameraId;
                var activeCamera = _cameraManager.ActiveCamera;
                if (activeCamera != null)
                {
                    if (activeCamera.IsOpen)
                    {
                        StartCaptureCore();
                    }
                }

                return activeCamera;
            }
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        #endregion

        #region 采集控制

        /// <summary>
        /// 启动后台采集线程
        /// </summary>
        public void StartCapture()
        {
            _cameraOperationLock.Wait();
            try
            {
                StartCaptureCore();
            }
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        private void StartCaptureCore()
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

                activeCamera.SetGrabbing(true);
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
            _cameraOperationLock.Wait();
            try
            {
                StopCaptureCore();
            }
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        private void StopCaptureCore()
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

                activeCamera?.SetGrabbing(false);
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
            _cameraOperationLock.Wait();
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
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        /// <summary>
        /// 触发拍照并获取帧（原子操作: TriggerSoftware -> GetFrame -> 转 Mat -> ReleaseFrame）
        /// </summary>
        /// <param name="timeoutMs">取帧超时（毫秒）</param>
        /// <returns>成功返回图像，失败返回 null</returns>
        public Mat? CaptureFrame(int timeoutMs = 3000)
        {
            _cameraOperationLock.Wait();
            try
            {
            var camera = _cameraManager.ActiveCamera?.Camera;
            if (camera == null)
            {
                LastError = "未找到活动相机实例";
                return null;
            }

            IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
            bool shouldReleaseFrame = false;

            try
            {
                int clearRes = camera.IMV_ClearFrameBuffer();
                if (clearRes != IMVDefine.IMV_OK)
                {
                    Debug.WriteLine($"[CameraService] ClearFrameBuffer failed: {clearRes}");
                }

                int res = camera.IMV_ExecuteCommandFeature("TriggerSoftware");
                if (res != IMVDefine.IMV_OK)
                {
                    LastError = $"软触发失败: {res}";
                    return null;
                }

                res = camera.IMV_GetFrame(ref frame, timeoutMs);
                shouldReleaseFrame = res == IMVDefine.IMV_OK;
                if (!shouldReleaseFrame)
                {
                    LastError = $"取帧失败: {res}";
                    return null;
                }

                if (frame.frameInfo.size == 0 || frame.pData == IntPtr.Zero)
                {
                    LastError = "SDK 返回空帧";
                    return null;
                }

                if (!TryValidateFrame(frame, out string validationError))
                {
                    LastError = validationError;
                    Debug.WriteLine($"[CameraService] Invalid frame rejected: {validationError}");
                    return null;
                }

                Mat mat = ConvertFrameToMat(frame);
                CacheLastFrameReference(mat);

                LastError = null;
                return mat;
            }
            catch (Exception ex)
            {
                LastError = $"采集转换失败: {ex.Message}";
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
            finally
            {
                _cameraOperationLock.Release();
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
                        using Mat capturedFrame = ConvertCameraFrameToMat(cameraFrame);
                        CacheLastFrameReference(capturedFrame);

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
        /// <returns>OpenCV Mat</returns>
        private static Mat ConvertFrameToMat(IMVDefine.IMV_Frame frame)
        {
            int width = (int)frame.frameInfo.width;
            int height = (int)frame.frameInfo.height;
            int paddingX = (int)frame.frameInfo.paddingX;
            uint pixelFormat = unchecked((uint)frame.frameInfo.pixelFormat);

            return ConvertRawFrameToMat(frame.pData, width, height, paddingX, pixelFormat);
        }

        private static Mat ConvertCameraFrameToMat(CameraFrame frame)
        {
            return frame.PixelFormat switch
            {
                CameraPixelFormat.Mono8 => CopyFrameBufferToMat(frame.DataPtr, frame.Width, frame.Height, frame.Width, frame.Width, MatType.CV_8UC1),
                CameraPixelFormat.BGR8 => CopyFrameBufferToMat(frame.DataPtr, frame.Width, frame.Height, frame.Width * 3, frame.Width * 3, MatType.CV_8UC3),
                CameraPixelFormat.RGB8 => ConvertRgbMatToBgr(CopyFrameBufferToMat(frame.DataPtr, frame.Width, frame.Height, frame.Width * 3, frame.Width * 3, MatType.CV_8UC3)),
                CameraPixelFormat.BayerRG8 => ConvertBayerMatToBgr(frame.DataPtr, frame.Width, frame.Height, frame.Width, ColorConversionCodes.BayerRG2BGR),
                CameraPixelFormat.BayerGB8 => ConvertBayerMatToBgr(frame.DataPtr, frame.Width, frame.Height, frame.Width, ColorConversionCodes.BayerGB2BGR),
                CameraPixelFormat.BayerGR8 => ConvertBayerMatToBgr(frame.DataPtr, frame.Width, frame.Height, frame.Width, ColorConversionCodes.BayerGR2BGR),
                CameraPixelFormat.BayerBG8 => ConvertBayerMatToBgr(frame.DataPtr, frame.Width, frame.Height, frame.Width, ColorConversionCodes.BayerBG2BGR),
                _ => throw new NotSupportedException($"不支持的相机帧像素格式: {frame.PixelFormat}")
            };
        }

        private static Mat ConvertRawFrameToMat(IntPtr dataPtr, int width, int height, int paddingX, uint pixelFormat)
        {
            return pixelFormat switch
            {
                GvspPixelMono8 => CopyFrameBufferToMat(dataPtr, width, height, width + paddingX, width, MatType.CV_8UC1),
                GvspPixelBgr8 => CopyFrameBufferToMat(dataPtr, width, height, width * 3 + paddingX, width * 3, MatType.CV_8UC3),
                GvspPixelRgb8 => ConvertRgbMatToBgr(CopyFrameBufferToMat(dataPtr, width, height, width * 3 + paddingX, width * 3, MatType.CV_8UC3)),
                GvspPixelBayerRg8 => ConvertBayerMatToBgr(dataPtr, width, height, width + paddingX, ColorConversionCodes.BayerRG2BGR),
                GvspPixelBayerGb8 => ConvertBayerMatToBgr(dataPtr, width, height, width + paddingX, ColorConversionCodes.BayerGB2BGR),
                GvspPixelBayerGr8 => ConvertBayerMatToBgr(dataPtr, width, height, width + paddingX, ColorConversionCodes.BayerGR2BGR),
                GvspPixelBayerBg8 => ConvertBayerMatToBgr(dataPtr, width, height, width + paddingX, ColorConversionCodes.BayerBG2BGR),
                _ => throw new NotSupportedException($"不支持的 SDK 帧像素格式: 0x{pixelFormat:X8}")
            };
        }

        private static bool TryValidateFrame(IMVDefine.IMV_Frame frame, out string error)
        {
            error = string.Empty;

            int width = (int)frame.frameInfo.width;
            int height = (int)frame.frameInfo.height;
            int paddingX = (int)frame.frameInfo.paddingX;
            uint pixelFormat = unchecked((uint)frame.frameInfo.pixelFormat);

            if (frame.frameInfo.status != 0)
            {
                error = $"SDK 返回异常帧: status={frame.frameInfo.status}, format=0x{pixelFormat:X8}, size={frame.frameInfo.size}";
                return false;
            }

            if (width <= 0 || height <= 0 || paddingX < 0)
            {
                error = $"SDK 帧尺寸异常: width={width}, height={height}, paddingX={paddingX}, format=0x{pixelFormat:X8}";
                return false;
            }

            if (!TryGetMinimumFrameBytes(width, height, paddingX, pixelFormat, out long minimumBytes, out string formatError))
            {
                error = formatError;
                return false;
            }

            long actualBytes = frame.frameInfo.size;
            if (actualBytes < minimumBytes)
            {
                error = $"SDK 帧长度不足: actual={actualBytes}, expected>={minimumBytes}, width={width}, height={height}, paddingX={paddingX}, format=0x{pixelFormat:X8}";
                return false;
            }

            return true;
        }

        private static bool TryGetMinimumFrameBytes(int width, int height, int paddingX, uint pixelFormat, out long minimumBytes, out string error)
        {
            minimumBytes = 0;
            error = string.Empty;

            int rowBytes = pixelFormat switch
            {
                GvspPixelMono8 => width,
                GvspPixelBayerRg8 => width,
                GvspPixelBayerGb8 => width,
                GvspPixelBayerGr8 => width,
                GvspPixelBayerBg8 => width,
                GvspPixelBgr8 => checked(width * 3),
                GvspPixelRgb8 => checked(width * 3),
                _ => -1
            };

            if (rowBytes < 0)
            {
                error = $"不支持的 SDK 帧像素格式: 0x{pixelFormat:X8}";
                return false;
            }

            long srcStride = (long)rowBytes + paddingX;
            if (srcStride < rowBytes)
            {
                error = $"SDK 帧步长异常: rowBytes={rowBytes}, paddingX={paddingX}, format=0x{pixelFormat:X8}";
                return false;
            }

            minimumBytes = checked(srcStride * height);
            return true;
        }

        private static Mat ConvertRgbMatToBgr(Mat rgbMat)
        {
            try
            {
                Mat bgrMat = new Mat();
                Cv2.CvtColor(rgbMat, bgrMat, ColorConversionCodes.RGB2BGR);
                return bgrMat;
            }
            finally
            {
                rgbMat.Dispose();
            }
        }

        private static Mat ConvertBayerMatToBgr(IntPtr dataPtr, int width, int height, int srcStride, ColorConversionCodes conversionCode)
        {
            using Mat bayerMat = CopyFrameBufferToMat(dataPtr, width, height, srcStride, width, MatType.CV_8UC1);
            Mat bgrMat = new Mat();
            Cv2.CvtColor(bayerMat, bgrMat, conversionCode);
            return bgrMat;
        }

        private static Mat CopyFrameBufferToMat(IntPtr dataPtr, int width, int height, int srcStride, int rowBytes, MatType matType)
        {
            Mat mat = new Mat(height, width, matType);

            try
            {
                int dstStride = (int)mat.Step();

                unsafe
                {
                    byte* srcPtr = (byte*)dataPtr.ToPointer();
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
                                rowBytes,
                                rowBytes);
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
            _cameraOperationLock.Wait();
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
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        /// <summary>
        /// 设置增益值
        /// </summary>
        /// <param name="gain">增益值</param>
        public void SetGain(double gain)
        {
            _cameraOperationLock.Wait();
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
            finally
            {
                _cameraOperationLock.Release();
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;

            _cameraOperationLock.Wait();
            try
            {
                if (_disposed) return;
                _disposed = true;

                CloseCore();

                lock (_frameLock)
                {
                    _lastFrame?.Dispose();
                    _lastFrame = null;
                }
            }
            finally
            {
                _cameraOperationLock.Release();
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}

