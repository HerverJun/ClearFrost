using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 
    /// </summary>
    public class HikvisionCamera : ICameraProvider
    {
        #region ���� SDK �����ͽṹ

        private const uint MV_OK = 0x00000000;
        private const uint MV_E_HANDLE = 0x80000000;
        private const uint MV_E_NETER = 0x80000006;

        // 
        private const uint MV_GIGE_DEVICE = 0x00000001;
        private const uint MV_USB_DEVICE = 0x00000004;
        private const uint MV_CAMERALINK_DEVICE = 0x00000008;

        // 
        private const uint MV_ACCESS_Exclusive = 1;

        // 
        private const uint PixelType_Gvsp_Mono8 = 0x01080001;
        private const uint PixelType_Gvsp_RGB8 = 0x02180014;
        private const uint PixelType_Gvsp_BGR8 = 0x02180015;
        private const uint PixelType_Gvsp_BayerRG8 = 0x01080009;
        private const uint PixelType_Gvsp_BayerGB8 = 0x0108000A;
        private const uint PixelType_Gvsp_BayerGR8 = 0x0108000B;
        private const uint PixelType_Gvsp_BayerBG8 = 0x0108000C;

        [StructLayout(LayoutKind.Sequential)]
        private struct MV_CC_DEVICE_INFO
        {
            public ushort nMajorVer;
            public ushort nMinorVer;
            public uint nMacAddrHigh;
            public uint nMacAddrLow;
            public uint nTLayerType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 540)]
            public byte[] Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] SpecialInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MV_CC_DEVICE_INFO_LIST
        {
            public uint nDeviceNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public IntPtr[] pDeviceInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MV_FRAME_OUT_INFO_EX
        {
            public ushort nWidth;
            public ushort nHeight;
            public uint enPixelType;
            public uint nFrameNum;
            public uint nDevTimeStampHigh;
            public uint nDevTimeStampLow;
            public uint nReserved0;
            public long nHostTimeStamp;
            public uint nFrameLen;
            public uint nSecondCount;
            public uint nCycleCount;
            public uint nCycleOffset;
            public float fGain;
            public float fExposureTime;
            public uint nAverageBrightness;
            public uint nRed;
            public uint nGreen;
            public uint nBlue;
            public uint nFrameCounter;
            public uint nTriggerIndex;
            public uint nInput;
            public uint nOutput;
            public ushort nOffsetX;
            public ushort nOffsetY;
            public ushort nChunkWidth;
            public ushort nChunkHeight;
            public uint nLostPacket;
            public uint nUnparsedChunkNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
            public uint[] nReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MV_FRAME_OUT
        {
            public IntPtr pBufAddr;
            public MV_FRAME_OUT_INFO_EX stFrameInfo;
            public uint nRes;
        }

        #endregion

        #region P/Invoke ����

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_EnumDevices")]
        private static extern int MV_CC_EnumDevices(uint nTLayerType, ref MV_CC_DEVICE_INFO_LIST pstDevList);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_CreateHandle")]
        private static extern int MV_CC_CreateHandle(ref IntPtr handle, IntPtr pstDevInfo);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_DestroyHandle")]
        private static extern int MV_CC_DestroyHandle(IntPtr handle);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_OpenDevice")]
        private static extern int MV_CC_OpenDevice(IntPtr handle, uint nAccessMode, ushort nSwitchoverKey);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_CloseDevice")]
        private static extern int MV_CC_CloseDevice(IntPtr handle);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_StartGrabbing")]
        private static extern int MV_CC_StartGrabbing(IntPtr handle);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_StopGrabbing")]
        private static extern int MV_CC_StopGrabbing(IntPtr handle);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_GetOneFrameTimeout")]
        private static extern int MV_CC_GetOneFrameTimeout(IntPtr handle, IntPtr pData, uint nDataSize, ref MV_FRAME_OUT_INFO_EX pFrameInfo, int nMsec);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_SetFloatValue")]
        private static extern int MV_CC_SetFloatValue(IntPtr handle, string strKey, float fValue);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_SetEnumValueByString")]
        private static extern int MV_CC_SetEnumValueByString(IntPtr handle, string strKey, string strValue);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_SetCommandValue")]
        private static extern int MV_CC_SetCommandValue(IntPtr handle, string strKey);

        [DllImport("MvCameraControl.dll", EntryPoint = "MV_CC_GetIntValue")]
        private static extern int MV_CC_GetIntValue(IntPtr handle, string strKey, ref uint pnValue);

        #endregion

        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed = false;
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private CameraDeviceInfo? _currentDevice;
        private List<CameraDeviceInfo> _cachedDevices = new();
        private MV_CC_DEVICE_INFO_LIST _deviceList;
        private byte[]? _frameBuffer;
        private GCHandle _frameBufferHandle;
        private int _targetDeviceIndex = -1;

        public string ProviderName => "Hikvision";
        public bool IsConnected => _isConnected && _handle != IntPtr.Zero;
        public bool IsGrabbing => _isGrabbing;
        public CameraDeviceInfo? CurrentDevice => _currentDevice;

        public List<CameraDeviceInfo> EnumerateDevices()
        {
            _cachedDevices.Clear();
            _deviceList = new MV_CC_DEVICE_INFO_LIST();
            _deviceList.pDeviceInfo = new IntPtr[256];

            try
            {
                int result = MV_CC_EnumDevices(MV_GIGE_DEVICE | MV_USB_DEVICE, ref _deviceList);
                if (result != MV_OK || _deviceList.nDeviceNum == 0)
                {
                    Debug.WriteLine($"[HikvisionCamera] No devices found or enum failed: 0x{result:X8}");
                    return _cachedDevices;
                }

                for (int i = 0; i < _deviceList.nDeviceNum; i++)
                {
                    if (_deviceList.pDeviceInfo[i] == IntPtr.Zero) continue;

                    var deviceInfo = Marshal.PtrToStructure<MV_CC_DEVICE_INFO>(_deviceList.pDeviceInfo[i]);
                    string serialNumber = ExtractSerialNumber(deviceInfo);
                    string interfaceType = deviceInfo.nTLayerType switch
                    {
                        MV_GIGE_DEVICE => "GigE",
                        MV_USB_DEVICE => "USB3",
                        MV_CAMERALINK_DEVICE => "CameraLink",
                        _ => "Unknown"
                    };

                    _cachedDevices.Add(new CameraDeviceInfo
                    {
                        SerialNumber = serialNumber,
                        Manufacturer = "Hikvision",
                        Model = $"HIK-{interfaceType}",
                        UserDefinedName = $"Hikvision Camera {i + 1}",
                        InterfaceType = interfaceType
                    });
                }

                Debug.WriteLine($"[HikvisionCamera] Found {_cachedDevices.Count} devices");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HikvisionCamera] EnumerateDevices error: {ex.Message}");
            }

            return _cachedDevices;
        }

        private string ExtractSerialNumber(MV_CC_DEVICE_INFO deviceInfo)
        {
            // 
            try
            {
                if (deviceInfo.SpecialInfo != null && deviceInfo.SpecialInfo.Length > 0)
                {
                    // 
                    // 
                    int offset = deviceInfo.nTLayerType == MV_GIGE_DEVICE ? 16 : 64;
                    int length = deviceInfo.nTLayerType == MV_GIGE_DEVICE ? 16 : 64;

                    string sn = System.Text.Encoding.ASCII.GetString(deviceInfo.SpecialInfo, offset, length);
                    return sn.TrimEnd('\0').Trim();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[HikvisionCamera] ExtractSerialNumber failed: {ex.Message}"); }

            // 
            return $"HIK-{deviceInfo.nMacAddrLow:X8}";
        }

        public bool Open(string serialNumber)
        {
            if (IsConnected) Close();

            try
            {
                // 
                if (_cachedDevices.Count == 0)
                    EnumerateDevices();

                // 
                _targetDeviceIndex = -1;
                for (int i = 0; i < _cachedDevices.Count; i++)
                {
                    if (_cachedDevices[i].SerialNumber == serialNumber)
                    {
                        _targetDeviceIndex = i;
                        break;
                    }
                }

                if (_targetDeviceIndex < 0 || _deviceList.pDeviceInfo[_targetDeviceIndex] == IntPtr.Zero)
                {
                    Debug.WriteLine($"[HikvisionCamera] Device not found: {serialNumber}");
                    return false;
                }

                // 
                int result = MV_CC_CreateHandle(ref _handle, _deviceList.pDeviceInfo[_targetDeviceIndex]);
                if (result != MV_OK)
                {
                    Debug.WriteLine($"[HikvisionCamera] CreateHandle failed: 0x{result:X8}");
                    return false;
                }

                // 
                result = MV_CC_OpenDevice(_handle, MV_ACCESS_Exclusive, 0);
                if (result != MV_OK)
                {
                    MV_CC_DestroyHandle(_handle);
                    _handle = IntPtr.Zero;
                    Debug.WriteLine($"[HikvisionCamera] OpenDevice failed: 0x{result:X8}");
                    return false;
                }

                // 
                int bufferSize = 4096 * 3000 * 3;
                _frameBuffer = new byte[bufferSize];
                _frameBufferHandle = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);

                _isConnected = true;
                _currentDevice = _cachedDevices[_targetDeviceIndex];
                Debug.WriteLine($"[HikvisionCamera] Opened: {serialNumber}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HikvisionCamera] Open error: {ex.Message}");
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

                MV_CC_CloseDevice(_handle);
                MV_CC_DestroyHandle(_handle);
                _handle = IntPtr.Zero;
                _isConnected = false;
                _currentDevice = null;

                if (_frameBufferHandle.IsAllocated)
                    _frameBufferHandle.Free();
                _frameBuffer = null;

                Debug.WriteLine("[HikvisionCamera] Closed");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HikvisionCamera] Close error: {ex.Message}");
                return false;
            }
        }

        public bool StartGrabbing()
        {
            if (!IsConnected) return false;
            if (_isGrabbing) return true;

            int result = MV_CC_StartGrabbing(_handle);
            _isGrabbing = result == MV_OK;
            Debug.WriteLine($"[HikvisionCamera] StartGrabbing: {(_isGrabbing ? "OK" : $"Failed 0x{result:X8}")}");
            return _isGrabbing;
        }

        public bool StopGrabbing()
        {
            if (!IsConnected) return true;
            if (!_isGrabbing) return true;

            int result = MV_CC_StopGrabbing(_handle);
            _isGrabbing = false;
            return result == MV_OK;
        }

        public CameraFrame? GetFrame(int timeoutMs = 1000)
        {
            if (!IsConnected || !_isGrabbing || _frameBuffer == null) return null;

            try
            {
                var frameInfo = new MV_FRAME_OUT_INFO_EX();
                int result = MV_CC_GetOneFrameTimeout(
                    _handle,
                    _frameBufferHandle.AddrOfPinnedObject(),
                    (uint)_frameBuffer.Length,
                    ref frameInfo,
                    timeoutMs);

                if (result != MV_OK)
                {
                    return null;
                }

                return new CameraFrame
                {
                    DataPtr = _frameBufferHandle.AddrOfPinnedObject(),
                    Width = frameInfo.nWidth,
                    Height = frameInfo.nHeight,
                    Size = (int)frameInfo.nFrameLen,
                    PixelFormat = ConvertPixelFormat(frameInfo.enPixelType),
                    FrameNumber = frameInfo.nFrameNum,
                    Timestamp = (ulong)frameInfo.nHostTimeStamp,
                    NeedsNativeRelease = false  // ʹ��Ԥ���仺���������赥���ͷ�
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HikvisionCamera] GetFrame error: {ex.Message}");
                return null;
            }
        }

        private static CameraPixelFormat ConvertPixelFormat(uint pixelType)
        {
            return pixelType switch
            {
                PixelType_Gvsp_Mono8 => CameraPixelFormat.Mono8,
                PixelType_Gvsp_RGB8 => CameraPixelFormat.RGB8,
                PixelType_Gvsp_BGR8 => CameraPixelFormat.BGR8,
                PixelType_Gvsp_BayerRG8 => CameraPixelFormat.BayerRG8,
                PixelType_Gvsp_BayerGB8 => CameraPixelFormat.BayerGB8,
                PixelType_Gvsp_BayerGR8 => CameraPixelFormat.BayerGR8,
                PixelType_Gvsp_BayerBG8 => CameraPixelFormat.BayerBG8,
                _ => CameraPixelFormat.Unknown
            };
        }

        public bool SetExposure(double microseconds)
        {
            if (!IsConnected) return false;
            return MV_CC_SetFloatValue(_handle, "ExposureTime", (float)microseconds) == MV_OK;
        }

        public bool SetGain(double value)
        {
            if (!IsConnected) return false;
            return MV_CC_SetFloatValue(_handle, "Gain", (float)value) == MV_OK;
        }

        public bool SetTriggerMode(bool softwareTrigger)
        {
            if (!IsConnected) return false;

            if (softwareTrigger)
            {
                MV_CC_SetEnumValueByString(_handle, "TriggerMode", "On");
                return MV_CC_SetEnumValueByString(_handle, "TriggerSource", "Software") == MV_OK;
            }
            else
            {
                return MV_CC_SetEnumValueByString(_handle, "TriggerMode", "Off") == MV_OK;
            }
        }

        public bool ExecuteSoftwareTrigger()
        {
            if (!IsConnected) return false;
            return MV_CC_SetCommandValue(_handle, "TriggerSoftware") == MV_OK;
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
                Debug.WriteLine($"[HikvisionCamera] Dispose error: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }

        ~HikvisionCamera() => Dispose(false);

        #endregion
    }
}


