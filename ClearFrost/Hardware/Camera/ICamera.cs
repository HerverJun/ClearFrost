using System;
using System.Collections.Generic;


namespace ClearFrost.Hardware
{
    /// <summary>
    /// Stable camera SDK boundary. Business code does not reference the private SDK assembly.
    /// </summary>
    public static class CameraSdk
    {
        public const int Ok = 0;

        public const uint GvspPixelMono8 = 0x01080001;
        public const uint GvspPixelRgb8 = 0x02180014;
        public const uint GvspPixelBgr8 = 0x02180015;
        public const uint GvspPixelBayerGr8 = 0x01080008;
        public const uint GvspPixelBayerRg8 = 0x01080009;
        public const uint GvspPixelBayerGb8 = 0x0108000A;
        public const uint GvspPixelBayerBg8 = 0x0108000B;

        public static CameraPixelFormat ToPixelFormat(uint rawPixelFormat)
        {
            return rawPixelFormat switch
            {
                GvspPixelMono8 => CameraPixelFormat.Mono8,
                GvspPixelRgb8 => CameraPixelFormat.RGB8,
                GvspPixelBgr8 => CameraPixelFormat.BGR8,
                GvspPixelBayerRg8 => CameraPixelFormat.BayerRG8,
                GvspPixelBayerGb8 => CameraPixelFormat.BayerGB8,
                GvspPixelBayerGr8 => CameraPixelFormat.BayerGR8,
                GvspPixelBayerBg8 => CameraPixelFormat.BayerBG8,
                _ => CameraPixelFormat.Unknown
            };
        }

        public static uint ToRawPixelFormat(CameraPixelFormat pixelFormat)
        {
            return pixelFormat switch
            {
                CameraPixelFormat.Mono8 => GvspPixelMono8,
                CameraPixelFormat.RGB8 => GvspPixelRgb8,
                CameraPixelFormat.BGR8 => GvspPixelBgr8,
                CameraPixelFormat.BayerRG8 => GvspPixelBayerRg8,
                CameraPixelFormat.BayerGB8 => GvspPixelBayerGb8,
                CameraPixelFormat.BayerGR8 => GvspPixelBayerGr8,
                CameraPixelFormat.BayerBG8 => GvspPixelBayerBg8,
                _ => 0
            };
        }
    }

    public enum CameraCreateHandleMode
    {
        ByIndex = 0,
        ByCameraKey = 1,
        ByDeviceUserId = 2,
        ByIpAddress = 3
    }

    /// <summary>
    /// In-process camera enumeration result without vendor unmanaged structures.
    /// </summary>
    public sealed class CameraDeviceList
    {
        public List<CameraDeviceInfo> Devices { get; } = new();

        public uint DeviceCount => (uint)Devices.Count;
    }

    /// <summary>
    ///
    /// </summary>
    public interface ICamera : IDisposable
    {
        /// <summary>
        ///
        /// </summary>
        bool IsConnected { get; }

        int IMV_EnumDevices(ref CameraDeviceList deviceList, uint interfaceType);
        int IMV_CreateHandle(CameraCreateHandleMode mode, int index);
        int IMV_Open();
        int IMV_SetEnumFeatureSymbol(string name, string value);
        int IMV_SetDoubleFeatureValue(string name, double value);
        int IMV_SetBufferCount(int count);
        int IMV_StartGrabbing();
        int IMV_StopGrabbing();
        int IMV_Close();
        int IMV_DestroyHandle();
        int IMV_ExecuteCommandFeature(string name);
        int IMV_ClearFrameBuffer();
        bool IMV_IsGrabbing();
        int IMV_GetFrame(ref CameraFrame frame, int timeout);
        int IMV_ReleaseFrame(ref CameraFrame frame);
    }

    /// <summary>
    /// 相机枚举特性诊断能力。真实 SDK 支持时用于确认 PixelFormat 等节点的当前值和可选项。
    /// </summary>
    public interface ICameraFeatureInspector
    {
        bool TryGetEnumFeatureSymbol(string name, out string value);

        IReadOnlyList<string> GetEnumFeatureEntries(string name);
    }

    /// <summary>
    /// 相机原始帧像素转换能力。华睿 SDK 支持时优先用于 Bayer/RGB 到 BGR8 的转换。
    /// </summary>
    public interface ICameraFramePixelConverter
    {
        bool TryConvertFrameToBgr8(CameraFrame frame, out CameraFrame convertedFrame);
    }
}


