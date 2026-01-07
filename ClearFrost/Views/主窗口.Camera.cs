using MVSDK_Net;
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
using YoloDetection;
using YOLO.Vision;
using YOLO.Helpers;
using YOLO.Interfaces;
using YOLO.Services;

namespace YOLO
{
    public partial class 主窗口
    {
        #region 4. 相机控制逻辑

        /// <summary>
        /// 查找并返回目标相机的索引，找不到返回-1
        /// </summary>
        private int FindTargetCamera()
        {
            try
            {
                IMVDefine.IMV_DeviceList deviceList = new IMVDefine.IMV_DeviceList();
                int res = cam.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                if (res != IMVDefine.IMV_OK || deviceList.nDevNum == 0)
                {
                    _uiController.LogToFrontend("✗ 未找到任何相机设备", "error");
                    return -1;
                }

                for (int i = 0; i < deviceList.nDevNum; i++)
                {
                    var infoPtr = deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i;
                    var infoObj = Marshal.PtrToStructure(infoPtr, typeof(IMVDefine.IMV_DeviceInfo));
                    if (infoObj == null) continue;
                    var info = (IMVDefine.IMV_DeviceInfo)infoObj;

                    if (info.serialNumber.Equals(_appConfig.CameraSerialNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                // 未找到匹配的序列号
                _uiController.LogToFrontend($"✗ 未找到序列号为 {_appConfig.CameraSerialNumber} 的相机", "error");
                _uiController.LogToFrontend($"请检查相机连接或在设置中修改序列号", "warning");
                return -1;
            }
            catch (DllNotFoundException dllEx)
            {
                _uiController.LogToFrontend($"相机驱动缺失: {dllEx.Message}", "error");
                return -1;
            }
            catch (Exception ex)
            {
                _uiController.LogToFrontend($"查找相机异常: {ex.Message}", "error");
                return -1;
            }
        }

        /// <summary>
        /// 一键打开相机：自动查找目标相机并打开
        /// </summary>
        private void btnOpenCamera_Logic()
        {
            // 先查找目标相机
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

                if (renderThread != null && renderThread.IsAlive) renderThread.Join(100);
                renderThread = new Thread(DisplayThread);
                renderThread.IsBackground = true;
                renderThread.Start();

                SafeFireAndForget(_uiController.UpdateConnection("cam", true), "更新相机状态");
                SafeFireAndForget(_uiController.LogToFrontend("✓ 相机开启成功", "success"), "相机开启日志");
                // 自动连接PLC
                SafeFireAndForget(ConnectPlcViaServiceAsync(), "PLC自动连接");
            }
            catch (Exception ex)
            {
                ReleaseCameraResources();
                _uiController.LogToFrontend($"相机开启异常: {ex.Message}", "error");
            }
        }

        private void getParam()
        {
            cam.IMV_SetEnumFeatureSymbol("PixelFormat", "Mono8");
            cam.IMV_SetDoubleFeatureValue("ExposureTime", _appConfig.ExposureTime);
            cam.IMV_SetDoubleFeatureValue("GainRaw", _appConfig.GainRaw);
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
            catch { }
        }

        private Bitmap ConvertFrameToBitmap(IMVDefine.IMV_Frame frame)
        {
            if (frame.frameInfo.pixelFormat != IMVDefine.IMV_EPixelType.gvspPixelMono8) throw new Exception("非Mono8格式");
            var bitmap = new Bitmap((int)frame.frameInfo.width, (int)frame.frameInfo.height, PixelFormat.Format8bppIndexed);
            ColorPalette palette = bitmap.Palette;
            for (int i = 0; i < 256; i++) palette.Entries[i] = Color.FromArgb(i, i, i);
            bitmap.Palette = palette;
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            CopyMemory(bmpData.Scan0, frame.pData, (uint)frame.frameInfo.size);
            bitmap.UnlockBits(bmpData);
            return bitmap;
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        #endregion
    }
}
