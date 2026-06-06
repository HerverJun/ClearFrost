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

                var (success, errorMessage, startupNotice, usedMonoFallback) = await Task.Run(() =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        var activeConfig = _appConfig.ActiveCamera ?? _appConfig.EnsureActiveCameraConfigFromLegacy();
                        if (activeConfig == null || string.IsNullOrWhiteSpace(activeConfig.SerialNumber))
                        {
                            return (false, "未配置活动相机或序列号为空", string.Empty, false);
                        }

                        SynchronizeActiveCameraRegistration(activeConfig, recreateExisting: false);

                        // 先彻底释放旧句柄，再按当前配置重开，避免厂家软件或像素格式状态被旧句柄卡住。
                        ReleaseCameraResources();
                        token.ThrowIfCancellationRequested();

                        bool openOk = _cameraService.Open(activeConfig.SerialNumber, activeConfig.Manufacturer);
                        if (!openOk)
                        {
                            string detail = _cameraService.LastError ?? $"相机连接失败: {activeConfig.DisplayName}";
                            return (false, detail, string.Empty, false);
                        }

                        string mockCameraNotice = _cameraService.IsMockCamera
                            ? "警告：当前连接的是模拟相机，画面为软件生成的测试图，不是真实工业相机。请检查 IsDebugMode 和相机配置。"
                            : string.Empty;

                        token.ThrowIfCancellationRequested();
                        getParam();
                        if (!TryInitializeCapturePipeline(token, out bool startupUsedMonoFallback, out string startupError, out string startupNoticeLocal))
                        {
                            throw new Exception(startupError);
                        }

                        string combinedNotice = string.Join(" ",
                            new[] { mockCameraNotice, startupNoticeLocal }.Where(n => !string.IsNullOrWhiteSpace(n)));
                        return (true, string.Empty, combinedNotice, startupUsedMonoFallback);
                    }
                    catch (OperationCanceledException)
                    {
                        try { ReleaseCameraResources(); } catch { }
                        return (false, "操作已取消", string.Empty, false);
                    }
                    catch (Exception ex)
                    {
                        try { ReleaseCameraResources(); } catch { }
                        return (false, ex.Message, string.Empty, false);
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
                    var activeCameraConfig = _appConfig.ActiveCamera;
                    string operatorContext = BuildOperatorAuditContext();
                    string cameraDetail =
                        $"{operatorContext}; " +
                        $"Name={activeCameraConfig?.DisplayName ?? "-"}; SN={activeCameraConfig?.SerialNumber ?? "-"}; " +
                        $"Manufacturer={activeCameraConfig?.Manufacturer ?? "-"}; PixelFormat={activeCameraConfig?.PixelFormat ?? "Auto"}; " +
                        $"MonoFallback={usedMonoFallback}";
                    WriteAuditLogSafe("Camera", "Open", cameraDetail, success: true);
                    await _uiController.UpdateConnection("cam", true);
                    await _uiController.LogToFrontend("相机开启成功", "success");
                    if (!string.IsNullOrWhiteSpace(startupNotice))
                    {
                        string level = startupNotice.Contains("警告", StringComparison.Ordinal) ? "warning" : "info";
                        await _uiController.LogToFrontend(startupNotice, level);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    var activeCameraConfig = _appConfig.ActiveCamera;
                    string operatorContext = BuildOperatorAuditContext();
                    string cameraDetail =
                        $"{operatorContext}; " +
                        $"Name={activeCameraConfig?.DisplayName ?? "-"}; SN={activeCameraConfig?.SerialNumber ?? "-"}; " +
                        $"Manufacturer={activeCameraConfig?.Manufacturer ?? "-"}; Error={errorMessage}";
                    WriteAuditLogSafe("Camera", "Open", cameraDetail, success: false);
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

                    if (!await OpenCameraForPreviewAsync(activeConfig))
                    {
                        throw new InvalidOperationException(_cameraService.LastError ?? "相机预览连接失败");
                    }
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

        private async Task<bool> OpenCameraForPreviewAsync(CameraConfig activeConfig)
        {
            if (_isCameraOpening)
            {
                return false;
            }

            _isCameraOpening = true;
            try
            {
                return await Task.Run(() =>
                {
                    SynchronizeActiveCameraRegistration(activeConfig, recreateExisting: false);
                    ReleaseCameraResources();

                    bool openOk = _cameraService.Open(activeConfig.SerialNumber, activeConfig.Manufacturer);
                    if (!openOk)
                    {
                        return false;
                    }

                    CameraInstance? activeCamera = _cameraManager.ActiveCamera;
                    if (activeCamera == null)
                    {
                        _cameraService.Close();
                        return false;
                    }

                    getParam();
                    _cameraService.StartCapture();
                    return _cameraService.IsOpen && _cameraService.IsGrabbing;
                });
            }
            finally
            {
                _isCameraOpening = false;
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
                string previousPixelFormat = config.PixelFormat?.Trim() ?? string.Empty;
                string requestedPixelFormat = ReadJsonString(root, "pixelFormat", "PixelFormat").Trim();

                config.SerialNumber = serialNumber;
                config.Manufacturer = string.IsNullOrWhiteSpace(manufacturer)
                    ? (string.IsNullOrWhiteSpace(config.Manufacturer) ? "Huaray" : config.Manufacturer)
                    : manufacturer;
                config.DisplayName = string.IsNullOrWhiteSpace(displayName) ? serialNumber : displayName;
                config.ExposureTime = ReadJsonDouble(root, config.ExposureTime, "exposureTime", "ExposureTime");
                config.Gain = ReadJsonDouble(root, config.Gain, "gain", "Gain");
                if (!string.IsNullOrWhiteSpace(requestedPixelFormat))
                {
                    config.PixelFormat = NormalizeCameraPixelFormatForSave(requestedPixelFormat);
                }
                config.IsEnabled = true;

                cameraIdentityChanged = cameraIdentityChanged ||
                    !string.Equals(previousPixelFormat, config.PixelFormat?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

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

        private bool TryInitializeCapturePipeline(CancellationToken token, out bool usedMonoFallback, out string errorMessage, out string startupNotice)
        {
            usedMonoFallback = false;
            errorMessage = string.Empty;
            startupNotice = string.Empty;
            string requestedPixelFormat = _appConfig.ActiveCamera?.PixelFormat ?? "Auto";

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

            if (!TryFallbackToMono8())
            {
                errorMessage = $"按当前像素格式 {requestedPixelFormat} 获取首帧失败，且无法自动回退到 Mono8。请确认相机支持该格式，或在相机设置中改为 Auto/Bayer/Mono 后重试。";
                return false;
            }

            usedMonoFallback = true;
            token.ThrowIfCancellationRequested();

            if (TryCaptureStartupFrame(3000, 2, token, out _))
            {
                startupNotice = "相机按当前像素格式取图失败，已自动回退到 Mono8 并稳定采集。";
                return true;
            }

            errorMessage = $"按当前像素格式 {requestedPixelFormat} 获取首帧失败，回退到 Mono8 后仍无法获取图像。请确认相机曝光、触发模式和像素格式配置。";
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

        private bool TryFallbackToMono8()
        {
            try
            {
                _cameraService.StopCapture();
                if (!_cameraService.SetPixelFormat("Mono8"))
                {
                    Debug.WriteLine($"[OpenCamera] PixelFormat fallback to Mono8 failed: {_cameraService.LastError}");
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
                if (_cameraService is CameraService concreteCameraService)
                {
                    concreteCameraService.ReleaseCurrentCamera();
                }
                else
                {
                    _cameraService.Close();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[主窗口] ReleaseCameraResources failed: {ex.Message}"); }
        }

        #endregion
    }
}
