using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 真实工业相机实现，封装华睿 (Huaray) MVSDK
    /// 使用官方 MVSDK_Net.dll 中的 MyCamera 类
    /// </summary>
    public class RealCamera : ICamera, ICameraFeatureInspector, ICameraFramePixelConverter
    {
        private readonly MyCamera _cam = new MyCamera();
        private readonly string _targetSerialNumber;
        private bool _disposed = false;
        private bool _isConnected = false;
        private bool _handleCreated = false;
        private byte[]? _convertedFrameBuffer;
        private GCHandle _convertedFrameHandle;

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

        public IReadOnlyList<string> GetEnumFeatureEntries(string name)
        {
            if (!_isConnected || string.IsNullOrWhiteSpace(name) || !_cam.IMV_FeatureIsReadable(name))
            {
                return Array.Empty<string>();
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                uint entryCount = 0;
                int countResult = _cam.IMV_GetEnumFeatureEntryNum(name, ref entryCount);
                if (countResult != IMVDefine.IMV_OK || entryCount == 0)
                {
                    Debug.WriteLine($"[RealCamera] GetEnumFeatureEntryNum failed: Feature={name}, ErrorCode={countResult}");
                    return Array.Empty<string>();
                }

                int entrySize = Marshal.SizeOf<IMVDefine.IMV_EnumEntryInfo>();
                int bufferSize = checked(entrySize * (int)entryCount);
                buffer = Marshal.AllocHGlobal(bufferSize);
                var entryList = new IMVDefine.IMV_EnumEntryList
                {
                    nEnumEntryBufferSize = (uint)bufferSize,
                    pEnumEntryInfo = buffer
                };

                int listResult = _cam.IMV_GetEnumFeatureEntrys(name, ref entryList);
                if (listResult != IMVDefine.IMV_OK)
                {
                    Debug.WriteLine($"[RealCamera] GetEnumFeatureEntrys failed: Feature={name}, ErrorCode={listResult}");
                    return Array.Empty<string>();
                }

                var entries = new List<string>((int)entryCount);
                for (int i = 0; i < entryCount; i++)
                {
                    IntPtr itemPtr = IntPtr.Add(buffer, i * entrySize);
                    var entry = Marshal.PtrToStructure<IMVDefine.IMV_EnumEntryInfo>(itemPtr);
                    string entryName = entry.name?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(entryName))
                    {
                        entries.Add(entryName);
                    }
                }

                return entries;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealCamera] GetEnumFeatureEntries error: Feature={name}, {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
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

        public int IMV_ClearFrameBuffer()
        {
            return _cam.IMV_ClearFrameBuffer();
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

        public bool TryConvertFrameToBgr8(IMVDefine.IMV_Frame frame, out CameraFrame convertedFrame)
        {
            convertedFrame = null!;

            if (!_isConnected ||
                frame.pData == IntPtr.Zero ||
                frame.frameInfo.width == 0 ||
                frame.frameInfo.height == 0 ||
                !IsColorPixelFormat(frame.frameInfo.pixelFormat))
            {
                return false;
            }

            try
            {
                int destinationSize = checked((int)frame.frameInfo.width * (int)frame.frameInfo.height * 3);
                EnsureConvertedFrameBuffer(destinationSize);

                var convertParam = new IMVDefine.IMV_PixelConvertParam
                {
                    nWidth = frame.frameInfo.width,
                    nHeight = frame.frameInfo.height,
                    ePixelFormat = frame.frameInfo.pixelFormat,
                    pSrcData = frame.pData,
                    nSrcDataLen = frame.frameInfo.size,
                    nPaddingX = frame.frameInfo.paddingX,
                    nPaddingY = frame.frameInfo.paddingY,
                    eBayerDemosaic = IMVDefine.IMV_EBayerDemosaic.demosaicEdgeSensing,
                    eDstPixelFormat = IMVDefine.IMV_EPixelType.gvspPixelBGR8,
                    pDstBuf = _convertedFrameHandle.AddrOfPinnedObject(),
                    nDstBufSize = (uint)destinationSize
                };

                int result = _cam.IMV_PixelConvert(ref convertParam);
                if (result != IMVDefine.IMV_OK || convertParam.nDstDataLen == 0)
                {
                    Debug.WriteLine($"[RealCamera] PixelConvert to BGR8 failed: ErrorCode={result}, PixelFormat=0x{unchecked((uint)frame.frameInfo.pixelFormat):X8}");
                    return false;
                }

                convertedFrame = new CameraFrame
                {
                    DataPtr = _convertedFrameHandle.AddrOfPinnedObject(),
                    Width = (int)frame.frameInfo.width,
                    Height = (int)frame.frameInfo.height,
                    Size = checked((int)convertParam.nDstDataLen),
                    PixelFormat = CameraPixelFormat.BGR8,
                    FrameNumber = frame.frameInfo.blockId,
                    Timestamp = frame.frameInfo.timeStamp,
                    NeedsNativeRelease = false
                };

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealCamera] PixelConvert to BGR8 error: {ex.Message}");
                return false;
            }
        }

        private static bool IsColorPixelFormat(IMVDefine.IMV_EPixelType pixelFormat)
        {
            return pixelFormat is IMVDefine.IMV_EPixelType.gvspPixelRGB8
                or IMVDefine.IMV_EPixelType.gvspPixelBGR8
                or IMVDefine.IMV_EPixelType.gvspPixelBayRG8
                or IMVDefine.IMV_EPixelType.gvspPixelBayGB8
                or IMVDefine.IMV_EPixelType.gvspPixelBayGR8
                or IMVDefine.IMV_EPixelType.gvspPixelBayBG8;
        }

        private void EnsureConvertedFrameBuffer(int size)
        {
            if (_convertedFrameBuffer != null && _convertedFrameBuffer.Length >= size && _convertedFrameHandle.IsAllocated)
            {
                return;
            }

            ReleaseConvertedFrameBuffer();
            _convertedFrameBuffer = new byte[size];
            _convertedFrameHandle = GCHandle.Alloc(_convertedFrameBuffer, GCHandleType.Pinned);
        }

        private void ReleaseConvertedFrameBuffer()
        {
            if (_convertedFrameHandle.IsAllocated)
            {
                _convertedFrameHandle.Free();
            }

            _convertedFrameBuffer = null;
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

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealCamera] Dispose error: {ex.Message}");
            }
            finally
            {
                ReleaseConvertedFrameBuffer();
                _isConnected = false;
                _handleCreated = false;
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
