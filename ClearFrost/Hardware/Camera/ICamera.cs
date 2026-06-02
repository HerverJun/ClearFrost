using System;
using System.Collections.Generic;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 
    /// </summary>
    public interface ICamera : IDisposable
    {
        /// <summary>
        /// 
        /// </summary>
        bool IsConnected { get; }

        int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType);
        int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index);
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
        int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout);
        int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame);
    }

    /// <summary>
    /// 相机枚举特性诊断能力。真实 SDK 支持时用于确认 PixelFormat 等节点的当前值和可选项。
    /// </summary>
    public interface ICameraFeatureInspector
    {
        bool TryGetEnumFeatureSymbol(string name, out string value);

        IReadOnlyList<string> GetEnumFeatureEntries(string name);
    }
}


