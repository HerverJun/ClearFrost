using System;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace YOLO
{
    /// <summary>
    /// ICameraProvider 到 ICamera 的适配器
    /// 允许新的相机提供者与现有的 CameraManager 兼容
    /// </summary>
    public class CameraProviderAdapter : ICamera
    {
        private readonly ICameraProvider _provider;
        private bool _disposed = false;
        private CameraFrame? _currentFrame;

        public CameraProviderAdapter(ICameraProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public bool IsConnected => _provider.IsConnected;

        public int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType)
        {
            // 适配器不实现枚举，这应该在外部通过 CameraProviderFactory 完成
            return IMVDefine.IMV_OK;
        }

        public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index)
        {
            // 新接口不需要这个步骤，返回成功
            return IMVDefine.IMV_OK;
        }

        public int IMV_Open()
        {
            // Open 应该已经在外部调用过
            return _provider.IsConnected ? IMVDefine.IMV_OK : -1;
        }

        public int IMV_SetEnumFeatureSymbol(string name, string value)
        {
            if (name == "TriggerMode")
            {
                _provider.SetTriggerMode(value == "On");
            }
            return IMVDefine.IMV_OK;
        }

        public int IMV_SetDoubleFeatureValue(string name, double value)
        {
            if (name == "ExposureTime")
                _provider.SetExposure(value);
            else if (name == "GainRaw" || name == "Gain")
                _provider.SetGain(value);
            return IMVDefine.IMV_OK;
        }

        public int IMV_SetBufferCount(int count)
        {
            // 新接口内部管理缓冲区
            return IMVDefine.IMV_OK;
        }

        public int IMV_StartGrabbing()
        {
            return _provider.StartGrabbing() ? IMVDefine.IMV_OK : -1;
        }

        public int IMV_StopGrabbing()
        {
            return _provider.StopGrabbing() ? IMVDefine.IMV_OK : -1;
        }

        public int IMV_Close()
        {
            return _provider.Close() ? IMVDefine.IMV_OK : -1;
        }

        public int IMV_DestroyHandle()
        {
            // 新接口通过 Dispose 释放
            return IMVDefine.IMV_OK;
        }

        public int IMV_ExecuteCommandFeature(string name)
        {
            if (name == "TriggerSoftware")
                _provider.ExecuteSoftwareTrigger();
            return IMVDefine.IMV_OK;
        }

        public bool IMV_IsGrabbing()
        {
            return _provider.IsGrabbing;
        }

        public int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout)
        {
            _currentFrame?.Dispose();
            _currentFrame = _provider.GetFrame(timeout);

            if (_currentFrame == null)
                return -1;

            // 填充旧接口的帧结构
            frame.pData = _currentFrame.DataPtr;
            frame.frameInfo = new IMVDefine.IMV_FrameInfo
            {
                width = (uint)_currentFrame.Width,
                height = (uint)_currentFrame.Height,
                size = (uint)_currentFrame.Size,
                pixelFormat = ConvertToMvPixelFormat(_currentFrame.PixelFormat)
            };

            return IMVDefine.IMV_OK;
        }

        public int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
            return IMVDefine.IMV_OK;
        }

        private static IMVDefine.IMV_EPixelType ConvertToMvPixelFormat(CameraPixelFormat format)
        {
            return format switch
            {
                CameraPixelFormat.Mono8 => IMVDefine.IMV_EPixelType.gvspPixelMono8,
                _ => IMVDefine.IMV_EPixelType.gvspPixelMono8  // 默认返回 Mono8
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            _currentFrame?.Dispose();
            _provider.Dispose();
            _disposed = true;
        }
    }
}
