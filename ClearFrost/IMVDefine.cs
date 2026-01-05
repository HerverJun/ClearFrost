using System;
using System.Runtime.InteropServices;

namespace MVSDK_Net
{
    public class IMVDefine
    {
        public const int IMV_OK = 0;

        public enum IMV_EInterfaceType
        {
            interfaceTypeAll = 0
        }

        public enum IMV_ECreateHandleMode
        {
            modeByIndex = 0
        }

        public enum IMV_EPixelType
        {
            gvspPixelMono8 = 0x01080001
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_DeviceInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string serialNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_DeviceList
        {
            public uint nDevNum;
            public IntPtr pDevInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_FrameInfo
        {
            public uint width;
            public uint height;
            public uint size;
            public IMV_EPixelType pixelFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMV_Frame
        {
            public IntPtr pData;
            public IMV_FrameInfo frameInfo;
        }
    }
}
