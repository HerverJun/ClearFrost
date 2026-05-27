using MVSDK_Net;
using ClearFrost.Hardware;
using ClearFrost.Config;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.IO;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClearFrost.Yolo;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    public partial class 主窗口
    {
        #region 4. 相机控制逻辑

        private int FindTargetCamera()
        {
            try
            {
                var config = _appConfig.ActiveCamera;
                if (config == null || string.IsNullOrEmpty(config.SerialNumber))
                {
                    SafeFireAndForget(_uiController.LogToFrontend("未配置活动相机序列号", "error"), "查找相机");
                    return -1;
                }

                string targetSn = config.SerialNumber?.Trim() ?? "";

                // 使用官方 SDK 的 MyCamera 静态方法进行设备枚举
                IMVDefine.IMV_DeviceList deviceList = new IMVDefine.IMV_DeviceList();
                int res = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                if (res != IMVDefine.IMV_OK || deviceList.nDevNum == 0)
                {
                    SafeFireAndForget(_uiController.LogToFrontend("未找到任何相机设备", "error"), "查找相机");
                    return -1;
                }

                Debug.WriteLine($"[FindTargetCamera] Looking for '{targetSn}' in {deviceList.nDevNum} devices");

                for (int i = 0; i < (int)deviceList.nDevNum; i++)
                {
                    var info = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                        deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i,
                        typeof(IMVDefine.IMV_DeviceInfo))!;

                    string foundSn = info.serialNumber?.Trim() ?? "";
                    Debug.WriteLine($"[FindTargetCamera] Device[{i}] SerialNumber: '{foundSn}'");

                    if (foundSn.Equals(targetSn, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                // 未找到匹配的序列号
                SafeFireAndForget(_uiController.LogToFrontend($"未找到序列号为 {targetSn} 的相机", "error"), "查找相机");
                SafeFireAndForget(_uiController.LogToFrontend($"请检查相机连接或在设置中修改序列号", "warning"), "查找相机提示");
                return -1;
            }
            catch (DllNotFoundException dllEx)
            {
                SafeFireAndForget(_uiController.LogToFrontend($"相机驱动缺失: {dllEx.Message}", "error"), "驱动检查");
                return -1;
            }
            catch (Exception ex)
            {
                SafeFireAndForget(_uiController.LogToFrontend($"查找相机异常: {ex.Message}", "error"), "查找相机异常");
                return -1;
            }
        }

        /// <summary>
        /// 启动系统时连接相机：自动查找目标相机并打开采集。
        /// </summary>
        private async Task<bool> btnOpenCamera_LogicAsync(bool startTriggerSource = true)
        {
            if (IsShutdownInProgress)
            {
                await _uiController.LogToFrontend("软件正在退出，已忽略启动系统请求", "warning");
                return false;
            }

            if (_isCameraOpening)
            {
                SafeFireAndForget(_uiController.LogToFrontend("相机正在连接中，请稍候...", "warning"), "相机防重入");
                return false;
            }

            bool cameraStarted = false;
            _isCameraOpening = true;
            try
            {
                await _uiController.LogToFrontend("正在搜索并连接相机...", "info");
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_appShutdownCts.Token);
                CancellationToken token = linkedCts.Token;

                var (success, errorMessage, usedMonoFallback, startupNotice) = await Task.Run(() =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        var activeConfig = _appConfig.ActiveCamera ?? _appConfig.EnsureActiveCameraConfigFromLegacy();
                        if (activeConfig == null || string.IsNullOrWhiteSpace(activeConfig.SerialNumber))
                        {
                            return (false, "未配置活动相机或序列号为空", false, string.Empty);
                        }

                        SynchronizeActiveCameraRegistration(activeConfig, recreateExisting: false);

                        // 先关闭旧连接，再重开当前配置相机（复用句柄，不销毁）
                        _cameraService.Close();
                        token.ThrowIfCancellationRequested();

                        bool openOk = _cameraService.Open(activeConfig.SerialNumber, activeConfig.Manufacturer);
                        if (!openOk)
                        {
                            string detail = _cameraService.LastError ?? $"相机连接失败: {activeConfig.DisplayName}";
                            return (false, detail, false, string.Empty);
                        }

                        var activeCamera = _cameraManager.ActiveCamera;
                        if (activeCamera == null)
                        {
                            throw new Exception("相机已打开，但无法获取活动相机实例");
                        }

                        cam = activeCamera.Camera;
                        string mockCameraNotice = cam is MockCamera
                            ? "警告：当前连接的是模拟相机，画面为软件生成的测试图，不是真实工业相机。请检查 IsDebugMode 和相机配置。"
                            : string.Empty;

                        token.ThrowIfCancellationRequested();
                        getParam();
                        if (!TryInitializeCapturePipeline(cam, token, out bool startupUsedMonoFallback, out string startupError, out string startupNoticeLocal))
                        {
                            throw new Exception(startupError);
                        }

                        string combinedNotice = string.Join(" ",
                            new[] { mockCameraNotice, startupNoticeLocal }.Where(n => !string.IsNullOrWhiteSpace(n)));
                        return (true, string.Empty, startupUsedMonoFallback, combinedNotice);
                    }
                    catch (OperationCanceledException)
                    {
                        try { _cameraService.Close(); } catch { }
                        return (false, "操作已取消", false, string.Empty);
                    }
                    catch (Exception ex)
                    {
                        try { _cameraService.Close(); } catch { }
                        return (false, ex.Message, false, string.Empty);
                    }
                }, token);

                if (IsShutdownInProgress)
                {
                    Debug.WriteLine("[StartSystem] 软件已进入退出流程，忽略相机连接结果");
                    return false;
                }

                if (success)
                {
                    cameraStarted = true;
                    await _uiController.UpdateConnection("cam", true);
                    await _uiController.LogToFrontend("相机开启成功", "success");
                    if (!string.IsNullOrWhiteSpace(startupNotice))
                    {
                        string level = startupNotice.Contains("警告", StringComparison.Ordinal) ? "warning" : "info";
                        await _uiController.LogToFrontend(startupNotice, level);
                    }
                    if (usedMonoFallback)
                    {
                        await _uiController.LogToFrontend("默认像素格式取首帧失败，已自动回退为 Mono8。", "warning");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    await _uiController.LogToFrontend($"相机开启异常: {errorMessage}", "error");
                    RecordHealthError("Camera", $"相机未连接: {errorMessage}");
                    await _uiController.SendUiCommand("toast", new
                    {
                        message = $"未连接相机: {errorMessage}",
                        type = "warning",
                        durationMs = 3000
                    });
                }

                if (!success)
                {
                    await _uiController.UpdateConnection("cam", false);
                    await SendHealthSnapshotToFrontendAsync();
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[StartSystem] 相机连接操作已取消");
            }
            finally
            {
                _isCameraOpening = false;
            }

            if (cameraStarted && startTriggerSource && !IsShutdownInProgress)
            {
                await StartTriggerSourceAsync();
            }

            return cameraStarted;
        }

        private void getParam()
        {
            var config = _appConfig.ActiveCamera;
            if (config != null)
            {
                _cameraService.SetExposure(config.ExposureTime);
                _cameraService.SetGain(config.Gain);
            }
        }

        private async Task CaptureCameraPreviewFrameAsync(string payloadJson)
        {
            if (IsShutdownInProgress)
            {
                return;
            }

            await _uiController.SendUiCommand("cameraPreviewStatus", new
            {
                isBusy = true,
                message = "正在连接相机并获取画面..."
            });

            try
            {
                CameraConfig? activeConfig = ApplyCameraPreviewPayload(payloadJson);
                if (activeConfig == null || string.IsNullOrWhiteSpace(activeConfig.SerialNumber))
                {
                    throw new InvalidOperationException("未配置活动相机或序列号为空");
                }

                bool needsOpen = !_cameraService.IsOpen || !_cameraService.IsGrabbing;
                if (needsOpen)
                {
                    if (_isCameraOpening)
                    {
                        throw new InvalidOperationException("相机正在连接中，请稍候重试");
                    }

                    await btnOpenCamera_LogicAsync();
                }
                else
                {
                    getParam();
                }

                if (!_cameraService.IsOpen)
                {
                    throw new InvalidOperationException(_cameraService.LastError ?? "相机未连接");
                }

                if (!_cameraService.IsGrabbing)
                {
                    _cameraService.StartCapture();
                }

                using Mat? frame = await Task.Run(() => _cameraService.CaptureFrame(3000));
                if (frame == null || frame.Empty())
                {
                    throw new InvalidOperationException(_cameraService.LastError ?? "获取单帧失败");
                }

                await _uiController.SendCameraPreviewFrame(frame);
                await _uiController.SendUiCommand("cameraPreviewStatus", new
                {
                    isBusy = false,
                    message = "预览已更新",
                    type = "success"
                });
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"相机预览取帧失败: {ex.Message}", "error");
                await _uiController.SendUiCommand("cameraPreviewStatus", new
                {
                    isBusy = false,
                    message = $"获取失败: {ex.Message}",
                    type = "error"
                });
                await _uiController.SendUiCommand("toast", new
                {
                    message = $"获取单帧失败: {ex.Message}",
                    type = "warning",
                    durationMs = 3000
                });
            }
        }

        private CameraConfig? ApplyCameraPreviewPayload(string payloadJson)
        {
            CameraConfig? activeConfig = _appConfig.ActiveCamera ?? _appConfig.EnsureActiveCameraConfigFromLegacy();
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return activeConfig;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(payloadJson);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return activeConfig;
                }

                string serialNumber = ReadJsonString(root, "serialNumber", "SerialNumber").Trim();
                if (string.IsNullOrWhiteSpace(serialNumber))
                {
                    return activeConfig;
                }

                string cameraId = ReadJsonString(root, "cameraId", "CameraId").Trim();
                string manufacturer = ReadJsonString(root, "manufacturer", "Manufacturer").Trim();
                string displayName = ReadJsonString(root, "displayName", "DisplayName").Trim();

                CameraConfig config =
                    (!string.IsNullOrWhiteSpace(cameraId)
                        ? _appConfig.Cameras.FirstOrDefault(c => c.Id == cameraId)
                        : null) ??
                    _appConfig.Cameras.FirstOrDefault(c =>
                        string.Equals(c.SerialNumber?.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase)) ??
                    activeConfig ??
                    new CameraConfig();

                bool isNewConfig = !_appConfig.Cameras.Any(c => c.Id == config.Id);
                bool cameraIdentityChanged =
                    !string.Equals(config.SerialNumber?.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(manufacturer) &&
                     !string.Equals(config.Manufacturer?.Trim(), manufacturer, StringComparison.OrdinalIgnoreCase));

                config.SerialNumber = serialNumber;
                config.Manufacturer = string.IsNullOrWhiteSpace(manufacturer)
                    ? (string.IsNullOrWhiteSpace(config.Manufacturer) ? "Huaray" : config.Manufacturer)
                    : manufacturer;
                config.DisplayName = string.IsNullOrWhiteSpace(displayName) ? serialNumber : displayName;
                config.ExposureTime = ReadJsonDouble(root, config.ExposureTime, "exposureTime", "ExposureTime");
                config.Gain = ReadJsonDouble(root, config.Gain, "gain", "Gain");
                config.IsEnabled = true;

                if (isNewConfig)
                {
                    _appConfig.Cameras.Add(config);
                }

                _appConfig.ActiveCameraId = config.Id;
                SynchronizeActiveCameraRegistration(config, recreateExisting: cameraIdentityChanged);
                return config;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraPreview] 解析预览相机参数失败: {ex.Message}");
                return activeConfig;
            }
        }

        private static string ReadJsonString(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind != JsonValueKind.Null &&
                    value.ValueKind != JsonValueKind.Undefined)
                {
                    return value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? string.Empty
                        : value.ToString();
                }
            }

            return string.Empty;
        }

        private static double ReadJsonDouble(JsonElement root, double fallback, params string[] names)
        {
            foreach (string name in names)
            {
                if (!root.TryGetProperty(name, out JsonElement value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String &&
                    double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    return number;
                }
            }

            return fallback;
        }

        private bool TryInitializeCapturePipeline(ICamera activeCamera, CancellationToken token, out bool usedMonoFallback, out string errorMessage, out string startupNotice)
        {
            usedMonoFallback = false;
            errorMessage = string.Empty;
            startupNotice = string.Empty;

            _cameraService.StartCapture();
            token.ThrowIfCancellationRequested();

            if (TryCaptureStartupFrame(2000, 2, token, out int channelCount))
            {
                if (channelCount == 1)
                {
                    startupNotice = "相机当前输出单通道图像，已按工业检测模式稳定采集。";
                }
                else if (channelCount >= 3)
                {
                    startupNotice = "相机当前输出彩色图像，已按彩色检测链路采集。";
                }

                return true;
            }

            if (!TryFallbackToMono8(activeCamera))
            {
                errorMessage = "获取首帧失败，且无法自动回退到 Mono8。";
                return false;
            }

            usedMonoFallback = true;
            token.ThrowIfCancellationRequested();

            if (TryCaptureStartupFrame(3000, 2, token, out _))
            {
                return true;
            }

            errorMessage = "默认像素格式取首帧失败，回退到 Mono8 后仍无法获取图像。";
            return false;
        }

        private bool TryCaptureStartupFrame(int timeoutMs, int attempts, CancellationToken token, out int channelCount)
        {
            channelCount = 0;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                Mat? testFrame = null;
                try
                {
                    testFrame = _cameraService.CaptureFrame(timeoutMs);
                    if (testFrame != null && !testFrame.Empty())
                    {
                        channelCount = testFrame.Channels();
                        return true;
                    }
                }
                finally
                {
                    testFrame?.Dispose();
                }

                if (attempt + 1 < attempts)
                {
                    Thread.Sleep(80);
                }
            }

            return false;
        }

        private bool TryFallbackToMono8(ICamera activeCamera)
        {
            try
            {
                _cameraService.StopCapture();
                int result = activeCamera.IMV_SetEnumFeatureSymbol("PixelFormat", "Mono8");
                if (result != IMVDefine.IMV_OK)
                {
                    Debug.WriteLine($"[OpenCamera] PixelFormat fallback to Mono8 failed: {result}");
                    return false;
                }

                _cameraService.StartCapture();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenCamera] PixelFormat fallback exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 释放相机资源（对齐厂商 Grab/Form1 清理时序）
        /// 顺序：停标志 → 等线程退出 → StopGrabbing → Close → DestroyHandle
        /// </summary>
        private void ReleaseCameraResources()
        {
            try
            {
                _cameraService.Close();
            }
            catch (Exception ex) { Debug.WriteLine($"[主窗口] ReleaseCameraResources failed: {ex.Message}"); }
        }

        private Bitmap ConvertFrameToBitmap(IMVDefine.IMV_Frame frame)
        {
            if (frame.frameInfo.pixelFormat != IMVDefine.IMV_EPixelType.gvspPixelMono8) throw new Exception("非Mono8格式");

            int width = (int)frame.frameInfo.width;
            int height = (int)frame.frameInfo.height;
            int srcStride = width + (int)frame.frameInfo.paddingX; // SDK 帧的实际行步长

            var bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
            BitmapData? bmpData = null;
            try
            {
                ColorPalette palette = bitmap.Palette;
                for (int i = 0; i < 256; i++) palette.Entries[i] = Color.FromArgb(i, i, i);
                bitmap.Palette = palette;
                bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);

                int dstStride = bmpData.Stride; // Bitmap 的行步长（可能含对齐填充）

                if (srcStride == dstStride)
                {
                    // stride 一致，可以整块拷贝
                    CopyMemory(bmpData.Scan0, frame.pData, (uint)(srcStride * height));
                }
                else
                {
                    // stride 不一致，逐行拷贝有效像素
                    for (int row = 0; row < height; row++)
                    {
                        CopyMemory(
                            bmpData.Scan0 + row * dstStride,
                            frame.pData + row * srcStride,
                            (uint)width);
                    }
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            finally
            {
                if (bmpData != null)
                {
                    bitmap.UnlockBits(bmpData);
                }
            }
        }

        /// <summary>
        /// 将相机帧转换为 OpenCV Mat 格式
        /// 注意：SDK 帧可能有 paddingX 对齐，stride 不一定等于 width
        /// </summary>
        private Mat ConvertFrameToMat(IMVDefine.IMV_Frame frame)
        {
            int width = (int)frame.frameInfo.width;
            int height = (int)frame.frameInfo.height;
            int srcStride = width + (int)frame.frameInfo.paddingX; // SDK 帧的实际行步长

            // 创建 Mono8 格式的 Mat
            Mat mat = new Mat(height, width, MatType.CV_8UC1);
            try
            {
                int dstStride = (int)mat.Step(); // OpenCV Mat 的行步长

                // 复制图像数据（处理 stride 对齐）
                unsafe
                {
                    byte* srcPtr = (byte*)frame.pData.ToPointer();
                    byte* dstPtr = (byte*)mat.Data.ToPointer();

                    if (srcStride == dstStride)
                    {
                        // stride 一致，整块高效拷贝
                        long totalBytes = (long)srcStride * height;
                        Buffer.MemoryCopy(srcPtr, dstPtr, totalBytes, totalBytes);
                    }
                    else
                    {
                        // stride 不一致，逐行拷贝有效像素，跳过 padding
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

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        #endregion
    }
}
