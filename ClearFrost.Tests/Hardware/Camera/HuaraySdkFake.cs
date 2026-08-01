using System.Runtime.InteropServices;

namespace MVSDK_Net
{
    public static class IMVDefine
    {
        public enum IMV_ECreateHandleMode
        {
            modeByIndex = 0,
            modeByCameraKey = 1,
            modeByDeviceUserID = 2,
            modeByIPAddress = 3
        }

        public enum IMV_EInterfaceType
        {
            interfaceTypeAll = 0
        }

        public enum IMV_ECameraType
        {
            typeGige = 0
        }

        public enum IMV_EPixelType
        {
            gvspPixelMono8 = 0x01080001,
            gvspPixelBGR8 = 0x02180015
        }

        public enum IMV_EBayerDemosaic
        {
            demosaicEdgeSensing = 2
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct IMV_DeviceList
        {
            public uint nDevNum;
            public IntPtr pDevInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct IMV_DeviceInfo
        {
            public IMV_ECameraType nCameraType;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string cameraKey;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string cameraName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string serialNumber;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string vendorName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string modelName;

            public IMV_EInterfaceType nInterfaceType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_Frame
        {
            public IntPtr frameHandle;
            public IntPtr pData;
            public IMV_FrameInfo frameInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_FrameInfo
        {
            public ulong blockId;
            public uint status;
            public uint width;
            public uint height;
            public uint size;
            public IMV_EPixelType pixelFormat;
            public ulong timeStamp;
            public uint paddingX;
            public uint paddingY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_PixelConvertParam
        {
            public uint nWidth;
            public uint nHeight;
            public IMV_EPixelType ePixelFormat;
            public IntPtr pSrcData;
            public uint nSrcDataLen;
            public uint nPaddingX;
            public uint nPaddingY;
            public IMV_EBayerDemosaic eBayerDemosaic;
            public IMV_EPixelType eDstPixelFormat;
            public IntPtr pDstBuf;
            public uint nDstBufSize;
            public uint nDstDataLen;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct IMV_String
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string str;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct IMV_EnumEntryInfo
        {
            public ulong value;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string name;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_EnumEntryList
        {
            public uint nEnumEntryBufferSize;
            public IntPtr pEnumEntryInfo;
        }
    }

    public sealed class MyCamera
    {
        private static IntPtr _deviceInfoPointer;
        private static int _deviceCount;

        public static int EnumCallCount { get; private set; }

        public static uint LastInterfaceType { get; private set; }

        public static int CreateHandleCallCount { get; private set; }

        public static int CreatedIndex { get; private set; } = -1;

        public static int OpenCallCount { get; private set; }

        public static int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType)
        {
            EnumCallCount++;
            LastInterfaceType = interfaceType;
            deviceList.nDevNum = (uint)_deviceCount;
            deviceList.pDevInfo = _deviceInfoPointer;
            return 0;
        }

        public static void Reset()
        {
            ReleaseDeviceSnapshot();
            IMVDefine.IMV_DeviceInfo[] devices =
            {
                CreateDevice("SN-001", "Camera One"),
                CreateDevice("SN-002", "Camera Two")
            };

            int size = Marshal.SizeOf<IMVDefine.IMV_DeviceInfo>();
            _deviceInfoPointer = Marshal.AllocHGlobal(size * devices.Length);
            for (int index = 0; index < devices.Length; index++)
            {
                Marshal.StructureToPtr(devices[index], IntPtr.Add(_deviceInfoPointer, index * size), false);
            }

            _deviceCount = devices.Length;
            EnumCallCount = 0;
            LastInterfaceType = uint.MaxValue;
            CreateHandleCallCount = 0;
            CreatedIndex = -1;
            OpenCallCount = 0;
        }

        public static void ReleaseSnapshot()
        {
            ReleaseDeviceSnapshot();
        }

        public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index, string cameraKey)
        {
            CreateHandleCallCount++;
            CreatedIndex = index;
            return 0;
        }

        public int IMV_Open()
        {
            OpenCallCount++;
            return 0;
        }

        public int IMV_Close() => 0;

        public int IMV_DestroyHandle() => 0;

        public int IMV_StartGrabbing() => 0;

        public int IMV_StopGrabbing() => 0;

        public int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, uint timeout) => 0;

        public int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame) => 0;

        public bool IMV_FeatureIsReadable(string name) => true;

        public bool IMV_FeatureIsWriteable(string name) => true;

        public int IMV_GetEnumFeatureSymbol(string name, ref IMVDefine.IMV_String value) => 0;

        public int IMV_SetEnumFeatureSymbol(string name, string value) => 0;

        public int IMV_GetEnumFeatureEntryNum(string name, ref uint count) => 0;

        public int IMV_GetEnumFeatureEntrys(string name, ref IMVDefine.IMV_EnumEntryList entries) => 0;

        public int IMV_GetDoubleFeatureValue(string name, ref double value) => 0;

        public int IMV_SetDoubleFeatureValue(string name, double value) => 0;

        public int IMV_GetIntFeatureValue(string name, ref long value) => 0;

        public int IMV_SetIntFeatureValue(string name, long value) => 0;

        public int IMV_GetBoolFeatureValue(string name, ref bool value) => 0;

        public int IMV_SetBoolFeatureValue(string name, bool value) => 0;

        public int IMV_GetStringFeatureValue(string name, ref IMVDefine.IMV_String value) => 0;

        public int IMV_SetStringFeatureValue(string name, string value) => 0;

        public int IMV_GetEnumFeatureValue(string name, ref ulong value) => 0;

        public int IMV_SetEnumFeatureValue(string name, ulong value) => 0;

        public int IMV_PixelConvert(ref IMVDefine.IMV_PixelConvertParam parameters) => 0;

        public int IMV_SetBufferCount(uint count) => 0;

        public int IMV_ClearFrameBuffer() => 0;

        public int IMV_ExecuteCommandFeature(string name) => 0;

        public bool IMV_IsGrabbing() => false;

        private static IMVDefine.IMV_DeviceInfo CreateDevice(string serialNumber, string cameraName)
        {
            return new IMVDefine.IMV_DeviceInfo
            {
                nCameraType = IMVDefine.IMV_ECameraType.typeGige,
                cameraKey = serialNumber,
                cameraName = cameraName,
                serialNumber = serialNumber,
                vendorName = "Huaray",
                modelName = "FakeCamera",
                nInterfaceType = IMVDefine.IMV_EInterfaceType.interfaceTypeAll
            };
        }

        private static void ReleaseDeviceSnapshot()
        {
            if (_deviceInfoPointer == IntPtr.Zero)
            {
                return;
            }

            int size = Marshal.SizeOf<IMVDefine.IMV_DeviceInfo>();
            for (int index = 0; index < _deviceCount; index++)
            {
                Marshal.DestroyStructure<IMVDefine.IMV_DeviceInfo>(IntPtr.Add(_deviceInfoPointer, index * size));
            }

            Marshal.FreeHGlobal(_deviceInfoPointer);
            _deviceInfoPointer = IntPtr.Zero;
            _deviceCount = 0;
        }
    }
}
