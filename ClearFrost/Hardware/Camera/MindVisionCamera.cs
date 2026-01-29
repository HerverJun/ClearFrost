using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 华睿 (Huaray) 工业相机实现 - 基于官方 MVSDK_Net
    /// 注意：类名保留 MindVisionCamera 以兼容现有代码
    /// </summary>
    public class MindVisionCamera : ICameraProvider
    {
        private readonly MyCamera _cam = new MyCamera();
        private bool _disposed = false;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private CameraDeviceInfo? _currentDevice;
        private List<CameraDeviceInfo> _cachedDevices = new();

        public string ProviderName => "Huaray";
        public bool IsConnected => _isConnected;
        public bool IsGrabbing => _isGrabbing && _cam.IMV_IsGrabbing();
        public CameraDeviceInfo? CurrentDevice => _currentDevice;

        public List<CameraDeviceInfo> EnumerateDevices()
        {
            _cachedDevices.Clear();
            var deviceList = new IMVDefine.IMV_DeviceList();

            try
            {
                Debug.WriteLine("[MindVisionCamera] Calling IMV_EnumDevices...");
                int result = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                Debug.WriteLine($"[MindVisionCamera] IMV_EnumDevices returned: {result}, nDevNum: {deviceList.nDevNum}");

                if (result != IMVDefine.IMV_OK)
                {
                    Debug.WriteLine($"[MindVisionCamera] IMV_EnumDevices failed with code: {result}");
                    return _cachedDevices;
                }

                if (deviceList.nDevNum == 0)
                {
                    Debug.WriteLine("[MindVisionCamera] No devices found");
                    return _cachedDevices;
                }

                int structSize = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
                Debug.WriteLine($"[MindVisionCamera] IMV_DeviceInfo struct size: {structSize} bytes");

                for (int i = 0; i < (int)deviceList.nDevNum; i++)
                {
                    var info = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                        deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i,
                        typeof(IMVDefine.IMV_DeviceInfo))!;

                    string sn = info.serialNumber ?? "";
                    Debug.WriteLine($"[MindVisionCamera] Device[{i}]: SN='{sn}'");

                    _cachedDevices.Add(new CameraDeviceInfo
                    {
                        SerialNumber = sn.Trim(),
                        Manufacturer = "Huaray",
                        Model = "Huaray Camera",
                        UserDefinedName = sn.Trim(),
                        InterfaceType = "Unknown"
                    });
                }

                Debug.WriteLine($"[MindVisionCamera] Total devices found: {_cachedDevices.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MindVisionCamera] EnumerateDevices error: {ex.Message}");
                Debug.WriteLine($"[MindVisionCamera] Stack trace: {ex.StackTrace}");
            }

            return _cachedDevices;
        }

        public bool Open(string serialNumber)
        {
            if (IsConnected) Close();

            try
            {
                // 先枚举设备找到索引
                if (_cachedDevices.Count == 0)
                    EnumerateDevices();

                int deviceIndex = -1;
                for (int i = 0; i < _cachedDevices.Count; i++)
                {
                    if (_cachedDevices[i].SerialNumber == serialNumber)
                    {
                        deviceIndex = i;
                        break;
                    }
                }

                if (deviceIndex < 0)
                {
                    Debug.WriteLine($"[MindVisionCamera] Device not found: {serialNumber}");
                    return false;
                }

                // 使用官方 MyCamera 创建句柄
                int result = _cam.IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode.modeByIndex, deviceIndex);
                if (result != IMVDefine.IMV_OK)
                {
                    Debug.WriteLine($"[MindVisionCamera] CreateHandle failed: {result}");
                    return false;
                }

                // 打开相机
                result = _cam.IMV_Open();
                if (result != IMVDefine.IMV_OK)
                {
                    _cam.IMV_DestroyHandle();
                    Debug.WriteLine($"[MindVisionCamera] Open failed: {result}");
                    return false;
                }

                _isConnected = true;
                _currentDevice = _cachedDevices[deviceIndex];
                Debug.WriteLine($"[MindVisionCamera] Opened: {serialNumber}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MindVisionCamera] Open error: {ex.Message}");
                return false;
            }
        }

        public bool Close()
        {
            if (!_isConnected) return true;

            try
            {
                if (_isGrabbing)
                    StopGrabbing();

                _cam.IMV_Close();
                _cam.IMV_DestroyHandle();
                _isConnected = false;
                _currentDevice = null;
                Debug.WriteLine("[MindVisionCamera] Closed");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MindVisionCamera] Close error: {ex.Message}");
                return false;
            }
        }

        public bool StartGrabbing()
        {
            if (!_isConnected) return false;
            if (_isGrabbing) return true;

            int result = _cam.IMV_StartGrabbing();
            _isGrabbing = result == IMVDefine.IMV_OK;
            Debug.WriteLine($"[MindVisionCamera] StartGrabbing: {(_isGrabbing ? "OK" : $"Failed {result}")}");
            return _isGrabbing;
        }

        public bool StopGrabbing()
        {
            if (!_isConnected) return true;
            if (!_isGrabbing) return true;

            int result = _cam.IMV_StopGrabbing();
            _isGrabbing = false;
            return result == IMVDefine.IMV_OK;
        }

        public CameraFrame? GetFrame(int timeoutMs = 1000)
        {
            if (!_isConnected || !_isGrabbing) return null;

            try
            {
                var frame = new IMVDefine.IMV_Frame();
                int result = _cam.IMV_GetFrame(ref frame, (uint)timeoutMs);

                if (result != IMVDefine.IMV_OK)
                {
                    return null;
                }

                var cameraFrame = new CameraFrame
                {
                    DataPtr = frame.pData,
                    Width = (int)frame.frameInfo.width,
                    Height = (int)frame.frameInfo.height,
                    Size = (int)frame.frameInfo.size,
                    PixelFormat = ConvertPixelFormat((uint)frame.frameInfo.pixelFormat),
                    FrameNumber = 0,
                    Timestamp = 0,
                    NeedsNativeRelease = true
                };

                return cameraFrame;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MindVisionCamera] GetFrame error: {ex.Message}");
                return null;
            }
        }

        public void ReleaseFrame(CameraFrame frame)
        {
            if (!_isConnected || !frame.NeedsNativeRelease) return;

            try
            {
                var imvFrame = new IMVDefine.IMV_Frame { pData = frame.DataPtr };
                _cam.IMV_ReleaseFrame(ref imvFrame);
            }
            catch (Exception ex) { Debug.WriteLine($"[MindVisionCamera] ReleaseFrame failed: {ex.Message}"); }
        }

        private static CameraPixelFormat ConvertPixelFormat(uint pixelType)
        {
            return (IMVDefine.IMV_EPixelType)pixelType switch
            {
                IMVDefine.IMV_EPixelType.gvspPixelMono8 => CameraPixelFormat.Mono8,
                _ => CameraPixelFormat.Unknown
            };
        }

        public bool SetExposure(double microseconds)
        {
            if (!_isConnected) return false;
            return _cam.IMV_SetDoubleFeatureValue("ExposureTime", microseconds) == IMVDefine.IMV_OK;
        }

        public bool SetGain(double value)
        {
            if (!_isConnected) return false;
            return _cam.IMV_SetDoubleFeatureValue("GainRaw", value) == IMVDefine.IMV_OK;
        }

        public bool SetTriggerMode(bool softwareTrigger)
        {
            if (!_isConnected) return false;

            if (softwareTrigger)
            {
                _cam.IMV_SetEnumFeatureSymbol("TriggerMode", "On");
                return _cam.IMV_SetEnumFeatureSymbol("TriggerSource", "Software") == IMVDefine.IMV_OK;
            }
            else
            {
                return _cam.IMV_SetEnumFeatureSymbol("TriggerMode", "Off") == IMVDefine.IMV_OK;
            }
        }

        public bool ExecuteSoftwareTrigger()
        {
            if (!_isConnected) return false;
            return _cam.IMV_ExecuteCommandFeature("TriggerSoftware") == IMVDefine.IMV_OK;
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            try
            {
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MindVisionCamera] Dispose error: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }

        ~MindVisionCamera() => Dispose(false);

        #endregion
    }
}
