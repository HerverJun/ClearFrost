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
        /// 一键打开相机：自动查找目标相机并打开
        /// </summary>
        private async Task btnOpenCamera_LogicAsync()
        {
            if (IsShutdownInProgress)
            {
                await _uiController.LogToFrontend("软件正在退出，已忽略打开相机请求", "warning");
                return;
            }

            if (_isCameraOpening)
            {
                SafeFireAndForget(_uiController.LogToFrontend("相机正在连接中，请稍候...", "warning"), "相机防重入");
                return;
            }

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

                        var activeConfig = _appConfig.ActiveCamera;
                        if (activeConfig == null || string.IsNullOrWhiteSpace(activeConfig.SerialNumber))
                        {
                            return (false, "未配置活动相机或序列号为空", false, string.Empty);
                        }

                        // 先关闭旧连接，再重开当前配置相机（复用句柄，不销毁）
                        _cameraService.Close();
                        token.ThrowIfCancellationRequested();

                        bool openOk = _cameraService.Open(activeConfig.SerialNumber, activeConfig.Manufacturer);
                        if (!openOk)
                        {
                            string detail = _cameraService.LastError ?? $"打开相机失败: {activeConfig.DisplayName}";
                            return (false, detail, false, string.Empty);
                        }

                        var activeCamera = _cameraManager.ActiveCamera;
                        if (activeCamera == null)
                        {
                            throw new Exception("相机已打开，但无法获取活动相机实例");
                        }

                        cam = activeCamera.Camera;

                        token.ThrowIfCancellationRequested();
                        getParam();
                        if (!TryInitializeCapturePipeline(cam, token, out bool startupUsedMonoFallback, out string startupError, out string startupNoticeLocal))
                        {
                            throw new Exception(startupError);
                        }

                        return (true, string.Empty, startupUsedMonoFallback, startupNoticeLocal);
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
                    Debug.WriteLine("[OpenCamera] 软件已进入退出流程，忽略打开相机结果");
                    return;
                }

                if (success)
                {
                    await _uiController.UpdateConnection("cam", true);
                    await _uiController.LogToFrontend("相机开启成功", "success");
                    if (!string.IsNullOrWhiteSpace(startupNotice))
                    {
                        string level = startupNotice.Contains("仍输出单通道", StringComparison.Ordinal) ? "warning" : "info";
                        await _uiController.LogToFrontend(startupNotice, level);
                    }
                    if (usedMonoFallback)
                    {
                        await _uiController.LogToFrontend("默认像素格式取首帧失败，已自动回退为 Mono8。", "warning");
                    }
                    SafeFireAndForget(ConnectPlcViaServiceAsync(), "PLC自动连接");
                }
                else if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    await _uiController.LogToFrontend($"相机开启异常: {errorMessage}", "error");
                }

                if (!success)
                {
                    await _uiController.UpdateConnection("cam", false);
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[OpenCamera] 打开相机操作已取消");
            }
            finally
            {
                _isCameraOpening = false;
            }
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
                    if (TryPromoteMonoToColor(activeCamera, token, out string appliedPixelFormat))
                    {
                        startupNotice = $"检测到相机默认输出单通道，已自动切换到 {appliedPixelFormat}。";
                    }
                    else
                    {
                        startupNotice = "当前相机已正常取图，但仍输出单通道图像；请检查相机 PixelFormat 是否为彩色模式。";
                    }
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

        private bool TryPromoteMonoToColor(ICamera activeCamera, CancellationToken token, out string appliedPixelFormat)
        {
            appliedPixelFormat = string.Empty;

            if (activeCamera is not RealCamera realCamera)
            {
                return false;
            }

            if (!realCamera.TryGetEnumFeatureSymbol("PixelFormat", out string originalPixelFormat) ||
                string.IsNullOrWhiteSpace(originalPixelFormat) ||
                IsColorPixelFormat(originalPixelFormat))
            {
                return false;
            }

            foreach (string candidate in BuildPreferredColorPixelFormats(realCamera))
            {
                token.ThrowIfCancellationRequested();

                if (string.Equals(candidate, originalPixelFormat, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _cameraService.StopCapture();
                int setResult = activeCamera.IMV_SetEnumFeatureSymbol("PixelFormat", candidate);
                if (setResult != IMVDefine.IMV_OK)
                {
                    continue;
                }

                _cameraService.StartCapture();
                if (TryCaptureStartupFrame(1500, 1, token, out int channelCount) && channelCount > 1)
                {
                    appliedPixelFormat = candidate;
                    return true;
                }
            }

            _cameraService.StopCapture();
            activeCamera.IMV_SetEnumFeatureSymbol("PixelFormat", originalPixelFormat);
            _cameraService.StartCapture();
            TryCaptureStartupFrame(1000, 1, token, out _);
            return false;
        }

        private static bool IsColorPixelFormat(string pixelFormat)
        {
            if (string.IsNullOrWhiteSpace(pixelFormat))
            {
                return false;
            }

            return pixelFormat.Contains("RGB", StringComparison.OrdinalIgnoreCase)
                || pixelFormat.Contains("BGR", StringComparison.OrdinalIgnoreCase)
                || pixelFormat.Contains("Bayer", StringComparison.OrdinalIgnoreCase)
                || pixelFormat.Contains("YCbCr", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> BuildPreferredColorPixelFormats(RealCamera realCamera)
        {
            List<string> candidates = new List<string>();

            if (realCamera.TryGetEnumFeatureSymbol("PixelColorFilter", out string pixelColorFilter))
            {
                string? preferredBayer = MapPixelColorFilterToPixelFormat(pixelColorFilter);
                if (!string.IsNullOrWhiteSpace(preferredBayer))
                {
                    AddPixelFormatCandidate(candidates, preferredBayer);
                }
            }

            AddPixelFormatCandidate(candidates, "BayerRG8");
            AddPixelFormatCandidate(candidates, "BayerGB8");
            AddPixelFormatCandidate(candidates, "BayerGR8");
            AddPixelFormatCandidate(candidates, "BayerBG8");

            AddPixelFormatCandidate(candidates, "BGR8");
            AddPixelFormatCandidate(candidates, "RGB8");

            return candidates;
        }

        private static string? MapPixelColorFilterToPixelFormat(string pixelColorFilter)
        {
            if (pixelColorFilter.Contains("BayerRG", StringComparison.OrdinalIgnoreCase) ||
                pixelColorFilter.EndsWith("RG", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerRG8";
            }

            if (pixelColorFilter.Contains("BayerGB", StringComparison.OrdinalIgnoreCase) ||
                pixelColorFilter.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerGB8";
            }

            if (pixelColorFilter.Contains("BayerGR", StringComparison.OrdinalIgnoreCase) ||
                pixelColorFilter.EndsWith("GR", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerGR8";
            }

            if (pixelColorFilter.Contains("BayerBG", StringComparison.OrdinalIgnoreCase) ||
                pixelColorFilter.EndsWith("BG", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerBG8";
            }

            return null;
        }

        private static void AddPixelFormatCandidate(List<string> candidates, string pixelFormat)
        {
            if (candidates.Any(candidate => string.Equals(candidate, pixelFormat, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            candidates.Add(pixelFormat);
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
