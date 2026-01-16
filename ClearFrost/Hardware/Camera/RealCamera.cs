using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 真实工业相机实现，封装华睿 (Huaray) MVSDK
    /// </summary>
    public class RealCamera : ICamera
    {
        private const string DLL_NAME = "MVSDKmd.dll";

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType);

        /// <summary>
        /// 静态方法：枚举所有相机设备（供其他类调用）
        /// </summary>
        public static int EnumDevicesStatic(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType)
        {
            return IMV_EnumDevices(ref deviceList, interfaceType);
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index, ref IntPtr handle);

        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed = false;
        private bool _isConnected = false;

        /// <summary>
        /// 相机是否已连接
        /// </summary>
        public bool IsConnected => _isConnected && _handle != IntPtr.Zero;

        // Implement ICamera methods

        int ICamera.IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType)
        {
            return IMV_EnumDevices(ref deviceList, interfaceType);
        }

        public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index)
        {
            return IMV_CreateHandle(mode, index, ref _handle);
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_Open(IntPtr handle);
        public int IMV_Open()
        {
            int result = IMV_Open(_handle);
            if (result == 0) _isConnected = true;
            return result;
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_SetEnumFeatureSymbol(IntPtr handle, string name, string value);
        public int IMV_SetEnumFeatureSymbol(string name, string value) => IMV_SetEnumFeatureSymbol(_handle, name, value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_SetDoubleFeatureValue(IntPtr handle, string name, double value);
        public int IMV_SetDoubleFeatureValue(string name, double value) => IMV_SetDoubleFeatureValue(_handle, name, value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_SetBufferCount(IntPtr handle, int count);
        public int IMV_SetBufferCount(int count) => IMV_SetBufferCount(_handle, count);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_StartGrabbing(IntPtr handle);
        public int IMV_StartGrabbing() => IMV_StartGrabbing(_handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_StopGrabbing(IntPtr handle);
        public int IMV_StopGrabbing() => IMV_StopGrabbing(_handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_Close(IntPtr handle);
        public int IMV_Close()
        {
            _isConnected = false;
            return IMV_Close(_handle);
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_DestroyHandle(IntPtr handle);
        public int IMV_DestroyHandle()
        {
            if (_handle == IntPtr.Zero) return 0;
            int res = IMV_DestroyHandle(_handle);
            _handle = IntPtr.Zero;
            _isConnected = false;
            return res;
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_ExecuteCommandFeature(IntPtr handle, string name);
        public int IMV_ExecuteCommandFeature(string name) => IMV_ExecuteCommandFeature(_handle, name);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool IMV_IsGrabbing(IntPtr handle);
        public bool IMV_IsGrabbing() => _handle != IntPtr.Zero && IMV_IsGrabbing(_handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_GetFrame(IntPtr handle, ref IMVDefine.IMV_Frame frame, int timeout);
        public int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout) => IMV_GetFrame(_handle, ref frame, timeout);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int IMV_ReleaseFrame(IntPtr handle, ref IMVDefine.IMV_Frame frame);
        public int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame) => IMV_ReleaseFrame(_handle, ref frame);

        #region IDisposable 实现

        /// <summary>
        /// 释放相机资源
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 释放资源的核心实现
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            try
            {
                if (_handle != IntPtr.Zero)
                {
                    if (IMV_IsGrabbing(_handle))
                    {
                        IMV_StopGrabbing(_handle);
                    }
                    IMV_Close(_handle);
                    IMV_DestroyHandle(_handle);
                    _handle = IntPtr.Zero;
                }
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

        /// <summary>
        /// 析构函数，防止资源泄漏
        /// </summary>
        ~RealCamera()
        {
            Dispose(false);
        }

        #endregion
    }
}



