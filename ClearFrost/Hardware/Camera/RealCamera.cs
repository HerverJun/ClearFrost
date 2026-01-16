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
        private bool _disposed = false;
        private bool _isConnected = false;

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
            return _cam.IMV_CreateHandle(mode, index);
        }

        public int IMV_Open()
        {
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
            return _cam.IMV_Close();
        }

        public int IMV_DestroyHandle()
        {
            _isConnected = false;
            return _cam.IMV_DestroyHandle();
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
                if (_cam.IMV_IsGrabbing())
                {
                    _cam.IMV_StopGrabbing();
                }
                _cam.IMV_Close();
                _cam.IMV_DestroyHandle();
                _isConnected = false;
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
