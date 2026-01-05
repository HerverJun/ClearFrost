using System;
using MVSDK_Net;

namespace YOLO
{
    /// <summary>
    /// 相机接口，定义工业相机的基本操作
    /// </summary>
    public interface ICamera : IDisposable
    {
        /// <summary>
        /// 相机是否已连接
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
        bool IMV_IsGrabbing();
        int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout);
        int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame);
    }
}
