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
using ClearFrost.Vision;
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
        private void btnOpenCamera_Logic()
        {
            // 先释放之前可能存在的相机资源，避免重复打开错误 (-116)
            ReleaseCameraResources();

            // 重新初始化 CancellationTokenSource
            m_cts = new CancellationTokenSource();

            // 查找目标相机
            _targetCameraIndex = FindTargetCamera();

            if (_targetCameraIndex == -1)
            {
                return; // 查找失败，已在日志中报警
            }

            try
            {
                int res = cam.IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode.modeByIndex, _targetCameraIndex);
                if (res != IMVDefine.IMV_OK) throw new Exception($"创建句柄失败:{res}");

                res = cam.IMV_Open();
                if (res != IMVDefine.IMV_OK) throw new Exception($"打开相机失败:{res}");

                cam.IMV_SetEnumFeatureSymbol("TriggerSource", "Software");
                cam.IMV_SetEnumFeatureSymbol("TriggerMode", "On");
                cam.IMV_SetBufferCount(8);

                getParam();

                res = cam.IMV_StartGrabbing();
                if (res != IMVDefine.IMV_OK) throw new Exception($"启动采集失败:{res}");

                // 验证相机真正工作：触发一次拍照并等待首帧
                res = cam.IMV_ExecuteCommandFeature("TriggerSoftware");
                if (res != IMVDefine.IMV_OK)
                {
                    throw new Exception($"软件触发失败，相机可能未正确连接:{res}");
                }

                // 等待并获取首帧验证相机工作正常
                IMVDefine.IMV_Frame testFrame = new IMVDefine.IMV_Frame();
                bool shouldReleaseTestFrame = false;
                try
                {
                    res = cam.IMV_GetFrame(ref testFrame, 2000); // 2秒超时
                    shouldReleaseTestFrame = res == IMVDefine.IMV_OK;
                    if (!shouldReleaseTestFrame || testFrame.frameInfo.size == 0)
                    {
                        throw new Exception($"获取首帧失败，相机可能未正确工作:{res}");
                    }
                }
                finally
                {
                    if (shouldReleaseTestFrame)
                    {
                        cam.IMV_ReleaseFrame(ref testFrame);
                    }
                }

                if (renderThread != null && renderThread.IsAlive) renderThread.Join(100);
                renderThread = new Thread(DisplayThread);
                renderThread.IsBackground = true;
                renderThread.Start();

                SafeFireAndForget(_uiController.UpdateConnection("cam", true), "更新相机状态");
                SafeFireAndForget(_uiController.LogToFrontend("相机开启成功", "success"), "相机开启日志");
                // 自动连接PLC
                SafeFireAndForget(ConnectPlcViaServiceAsync(), "PLC自动连接");
            }
            catch (Exception ex)
            {
                ReleaseCameraResources();
                SafeFireAndForget(_uiController.LogToFrontend($"相机开启异常: {ex.Message}", "error"), "开启相机异常");
            }
        }

        private void getParam()
        {
            cam.IMV_SetEnumFeatureSymbol("PixelFormat", "Mono8");

            var config = _appConfig.ActiveCamera;
            if (config != null)
            {
                cam.IMV_SetDoubleFeatureValue("ExposureTime", config.ExposureTime);
                cam.IMV_SetDoubleFeatureValue("GainRaw", config.Gain);
            }
        }

        private void DisplayThread()
        {
            try
            {
                foreach (var frame in m_frameQueue.GetConsumingEnumerable(m_cts.Token))
                {
                    SafeFireAndForget(ProcessFrame(frame), "处理图像帧");
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task ProcessFrame(IMVDefine.IMV_Frame frame)
        {
            await Task.Yield();
            IMVDefine.IMV_Frame temp = frame;
            cam.IMV_ReleaseFrame(ref temp);
        }

        private void ReleaseCameraResources()
        {
            try
            {
                m_cts.Cancel();
                renderThread?.Join(200);
                if (cam != null)
                {
                    cam.IMV_StopGrabbing();
                    cam.IMV_Close();
                    cam.IMV_DestroyHandle();
                }
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
