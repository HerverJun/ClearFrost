using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 真实工业相机实现，封装华睿 (Huaray) MVSDK
    /// 使用官方 MVSDK_Net.dll 中的 MyCamera 类
    /// </summary>
    public class RealCamera : ICamera
    {
        private readonly MyCamera _cam = new MyCamera();
        private readonly string _targetSerialNumber;
        private bool _disposed = false;
        private bool _isConnected = false;
        private bool _handleCreated = false;

        public RealCamera(string? targetSerialNumber = null)
        {
            _targetSerialNumber = targetSerialNumber?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// 相机是否已连接
        /// </summary>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// 静态方法：枚举所有相机设备（供其他类调用）
        /// </summary>
        public static int EnumDevicesStatic(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType)
        {
            return MyCamera.IMV_EnumDevices(ref deviceList, interfaceType);
        }

        // Implement ICamera methods - 代理到官方 MyCamera 类

        int ICamera.IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType)
        {
            return MyCamera.IMV_EnumDevices(ref deviceList, interfaceType);
        }

        public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index)
        {
            int result = _cam.IMV_CreateHandle(mode, index);
            _handleCreated = result == IMVDefine.IMV_OK;
            return result;
        }

        public int IMV_Open()
        {
            if (!_handleCreated && !EnsureHandleCreated())
            {
                return -1;
            }

            int result = _cam.IMV_Open();
            if (result == IMVDefine.IMV_OK) _isConnected = true;
            return result;
        }

        public int IMV_SetEnumFeatureSymbol(string name, string value)
        {
            return _cam.IMV_SetEnumFeatureSymbol(name, value);
        }

        public int IMV_SetDoubleFeatureValue(string name, double value)
        {
            return _cam.IMV_SetDoubleFeatureValue(name, value);
        }

        public bool IMV_FeatureIsReadable(string name)
        {
            return _cam.IMV_FeatureIsReadable(name);
        }

        public bool IMV_FeatureIsWriteable(string name)
        {
            return _cam.IMV_FeatureIsWriteable(name);
        }

        public bool TryGetEnumFeatureSymbol(string name, out string value)
        {
            value = string.Empty;

            if (!_isConnected || string.IsNullOrWhiteSpace(name) || !_cam.IMV_FeatureIsReadable(name))
            {
                return false;
            }

            IMVDefine.IMV_String symbol = new IMVDefine.IMV_String();
            int result = _cam.IMV_GetEnumFeatureSymbol(name, ref symbol);
            if (result != IMVDefine.IMV_OK)
            {
                return false;
            }

            value = symbol.str?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        public int IMV_SetBufferCount(int count)
        {
            return _cam.IMV_SetBufferCount((uint)count);
        }

        public int IMV_StartGrabbing()
        {
            return _cam.IMV_StartGrabbing();
        }

        public int IMV_StopGrabbing()
        {
            return _cam.IMV_StopGrabbing();
        }

        public int IMV_Close()
        {
            _isConnected = false;
            if (!_handleCreated)
            {
                return IMVDefine.IMV_OK;
            }

            return _cam.IMV_Close();
        }

        public int IMV_DestroyHandle()
        {
            _isConnected = false;
            if (!_handleCreated)
            {
                return IMVDefine.IMV_OK;
            }

            int result = _cam.IMV_DestroyHandle();
            _handleCreated = false;
            return result;
        }

        public int IMV_ExecuteCommandFeature(string name)
        {
            return _cam.IMV_ExecuteCommandFeature(name);
        }

        public bool IMV_IsGrabbing()
        {
            return _cam.IMV_IsGrabbing();
        }

        public int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout)
        {
            return _cam.IMV_GetFrame(ref frame, (uint)timeout);
        }

        public int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame)
        {
            return _cam.IMV_ReleaseFrame(ref frame);
        }

        private bool EnsureHandleCreated()
        {
            if (_handleCreated)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(_targetSerialNumber))
            {
                Debug.WriteLine("[RealCamera] Target serial number is empty, cannot create handle lazily.");
                return false;
            }

            try
            {
                var deviceList = new IMVDefine.IMV_DeviceList();
                int enumResult = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);
                if (enumResult != IMVDefine.IMV_OK || deviceList.nDevNum <= 0)
                {
                    Debug.WriteLine($"[RealCamera] Enumerate devices failed: {enumResult}");
                    return false;
                }

                int deviceIndex = FindDeviceIndexBySerial(deviceList, _targetSerialNumber);
                if (deviceIndex < 0)
                {
                    Debug.WriteLine($"[RealCamera] Camera not found: {_targetSerialNumber}");
                    return false;
                }

                int result = _cam.IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode.modeByIndex, deviceIndex);
                _handleCreated = result == IMVDefine.IMV_OK;
                if (!_handleCreated)
                {
                    Debug.WriteLine($"[RealCamera] Lazy create handle failed: {result}");
                }

                return _handleCreated;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealCamera] EnsureHandleCreated error: {ex.Message}");
                return false;
            }
        }

        private static int FindDeviceIndexBySerial(IMVDefine.IMV_DeviceList deviceList, string serialNumber)
        {
            for (int i = 0; i < (int)deviceList.nDevNum; i++)
            {
                var devInfo = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                    deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i,
                    typeof(IMVDefine.IMV_DeviceInfo))!;
                string foundSerial = devInfo.serialNumber?.Trim() ?? string.Empty;
                if (string.Equals(foundSerial, serialNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        #region IDisposable 实现

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
                if (_handleCreated && _cam.IMV_IsGrabbing())
                {
                    _cam.IMV_StopGrabbing();
                }

                if (_handleCreated)
                {
                    _cam.IMV_Close();
                    _cam.IMV_DestroyHandle();
                }

                _isConnected = false;
                _handleCreated = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealCamera] Dispose error: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }

        ~RealCamera()
        {
            Dispose(false);
        }

        #endregion
    }
}
