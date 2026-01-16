using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 迈德威视相机实现
    /// </summary>
    public class MindVisionCamera : ICameraProvider
    {
        private const string DLL_NAME = "MVSDKmd.dll";

        #region P/Invoke 声明

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index, ref IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_Open(IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_Close(IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_DestroyHandle(IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_StartGrabbing(IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_StopGrabbing(IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool IMV_IsGrabbing(IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_GetFrame(IntPtr handle, ref IMVDefine.IMV_Frame frame, int timeout);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_ReleaseFrame(IntPtr handle, ref IMVDefine.IMV_Frame frame);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_SetDoubleFeatureValue(IntPtr handle, string name, double value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_SetEnumFeatureSymbol(IntPtr handle, string name, string value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_ExecuteCommandFeature(IntPtr handle, string name);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_SetBufferCount(IntPtr handle, int count);

        #endregion

        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed = false;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private CameraDeviceInfo? _currentDevice;
        private IMVDefine.IMV_Frame _lastFrame;
        private List<CameraDeviceInfo> _cachedDevices = new();

        public string ProviderName => "MindVision";
        public bool IsConnected => _isConnected && _handle != IntPtr.Zero;
        public bool IsGrabbing => _isGrabbing && _handle != IntPtr.Zero && IMV_IsGrabbing(_handle);
        public CameraDeviceInfo? CurrentDevice => _currentDevice;

        public List<CameraDeviceInfo> EnumerateDevices()
        {
            _cachedDevices.Clear();
            var deviceList = new IMVDefine.IMV_DeviceList();

            try
            {
                // 使用 RealCamera 静态方法进行设备枚举
                int result = RealCamera.EnumDevicesStatic(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);
                if (result != IMVDefine.IMV_OK || deviceList.nDevNum == 0)
                {
                    return _cachedDevices;
                }

                int structSize = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
                for (int i = 0; i < deviceList.nDevNum; i++)
                {
                    IntPtr ptr = IntPtr.Add(deviceList.pDevInfo, i * structSize);
                    var info = Marshal.PtrToStructure<IMVDefine.IMV_DeviceInfo>(ptr);

                    _cachedDevices.Add(new CameraDeviceInfo
                    {
                        SerialNumber = info.serialNumber ?? "",
                        Manufacturer = "MindVision",
                        Model = "MindVision Camera",
                        UserDefinedName = info.serialNumber ?? "",
                        InterfaceType = "Unknown"
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MindVisionCamera] EnumerateDevices error: {ex.Message}");
            }

            return _cachedDevices;
        }

        public bool Open(string serialNumber)
        {
            if (IsConnected) Close();

            try
            {
                // 先枚举设备
                if (_cachedDevices.Count == 0)
                    EnumerateDevices();

                // 查找设备索引
                int deviceIndex = -1;
                var deviceList = new IMVDefine.IMV_DeviceList();
                IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                int structSize = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
                for (int i = 0; i < deviceList.nDevNum; i++)
                {
                    IntPtr ptr = IntPtr.Add(deviceList.pDevInfo, i * structSize);
                    var info = Marshal.PtrToStructure<IMVDefine.IMV_DeviceInfo>(ptr);
                    if (info.serialNumber == serialNumber)
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

                // 创建句柄
                int result = IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode.modeByIndex, deviceIndex, ref _handle);
                if (result != IMVDefine.IMV_OK)
                {
                    Debug.WriteLine($"[MindVisionCamera] CreateHandle failed: {result}");
                    return false;
                }

                // 打开相机
                result = IMV_Open(_handle);
                if (result != IMVDefine.IMV_OK)
                {
                    IMV_DestroyHandle(_handle);
                    _handle = IntPtr.Zero;
                    Debug.WriteLine($"[MindVisionCamera] Open failed: {result}");
                    return false;
                }

                // 设置缓冲区
                IMV_SetBufferCount(_handle, 3);

                _isConnected = true;
                _currentDevice = _cachedDevices.Find(d => d.SerialNumber == serialNumber);
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
            if (!IsConnected) return true;

            try
            {
                if (_isGrabbing)
                    StopGrabbing();

                IMV_Close(_handle);
                IMV_DestroyHandle(_handle);
                _handle = IntPtr.Zero;
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
            if (!IsConnected) return false;
            if (_isGrabbing) return true;

            int result = IMV_StartGrabbing(_handle);
            _isGrabbing = result == IMVDefine.IMV_OK;
            return _isGrabbing;
        }

        public bool StopGrabbing()
        {
            if (!IsConnected) return true;
            if (!_isGrabbing) return true;

            int result = IMV_StopGrabbing(_handle);
            _isGrabbing = false;
            return result == IMVDefine.IMV_OK;
        }

        public CameraFrame? GetFrame(int timeoutMs = 1000)
        {
            if (!IsConnected || !_isGrabbing) return null;

            try
            {
                _lastFrame = new IMVDefine.IMV_Frame();
                int result = IMV_GetFrame(_handle, ref _lastFrame, timeoutMs);
                if (result != IMVDefine.IMV_OK)
                    return null;

                var frame = new CameraFrame
                {
                    DataPtr = _lastFrame.pData,
                    Width = (int)_lastFrame.frameInfo.width,
                    Height = (int)_lastFrame.frameInfo.height,
                    Size = (int)_lastFrame.frameInfo.size,
                    PixelFormat = ConvertPixelFormat(_lastFrame.frameInfo.pixelFormat),
                    FrameNumber = 0,  // SDK may not provide this
                    Timestamp = (ulong)DateTime.Now.Ticks,
                    NeedsNativeRelease = true,
                    ReleaseCallback = ReleaseNativeFrame
                };

                return frame;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MindVisionCamera] GetFrame error: {ex.Message}");
                return null;
            }
        }

        private void ReleaseNativeFrame(CameraFrame frame)
        {
            if (_handle != IntPtr.Zero)
            {
                IMV_ReleaseFrame(_handle, ref _lastFrame);
            }
        }

        private static CameraPixelFormat ConvertPixelFormat(IMVDefine.IMV_EPixelType format)
        {
            // Only use Mono8 which is guaranteed to exist, return Unknown for others
            if (format == IMVDefine.IMV_EPixelType.gvspPixelMono8)
                return CameraPixelFormat.Mono8;
            return CameraPixelFormat.Unknown;
        }

        public bool SetExposure(double microseconds)
        {
            if (!IsConnected) return false;
            return IMV_SetDoubleFeatureValue(_handle, "ExposureTime", microseconds) == IMVDefine.IMV_OK;
        }

        public bool SetGain(double value)
        {
            if (!IsConnected) return false;
            return IMV_SetDoubleFeatureValue(_handle, "GainRaw", value) == IMVDefine.IMV_OK;
        }

        public bool SetTriggerMode(bool softwareTrigger)
        {
            if (!IsConnected) return false;

            if (softwareTrigger)
            {
                IMV_SetEnumFeatureSymbol(_handle, "TriggerMode", "On");
                return IMV_SetEnumFeatureSymbol(_handle, "TriggerSource", "Software") == IMVDefine.IMV_OK;
            }
            else
            {
                return IMV_SetEnumFeatureSymbol(_handle, "TriggerMode", "Off") == IMVDefine.IMV_OK;
            }
        }

        public bool ExecuteSoftwareTrigger()
        {
            if (!IsConnected) return false;
            return IMV_ExecuteCommandFeature(_handle, "TriggerSoftware") == IMVDefine.IMV_OK;
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


