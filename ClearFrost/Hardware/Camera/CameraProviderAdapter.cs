using System;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 
    /// 
    /// </summary>
    public class CameraProviderAdapter : ICamera
    {
        private const uint GvspPixelMono8 = 0x01080001;
        private const uint GvspPixelRgb8 = 0x02180014;
        private const uint GvspPixelBgr8 = 0x02180015;
        private const uint GvspPixelBayerRg8 = 0x01080009;
        private const uint GvspPixelBayerGb8 = 0x0108000A;
        private const uint GvspPixelBayerGr8 = 0x0108000B;
        private const uint GvspPixelBayerBg8 = 0x0108000C;

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
            // 
            return IMVDefine.IMV_OK;
        }

        public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index)
        {
            // 
            return IMVDefine.IMV_OK;
        }

        public int IMV_Open()
        {
            // 
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
            // 
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
            // 
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

            // 
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
                CameraPixelFormat.Mono8 => (IMVDefine.IMV_EPixelType)GvspPixelMono8,
                CameraPixelFormat.RGB8 => (IMVDefine.IMV_EPixelType)GvspPixelRgb8,
                CameraPixelFormat.BGR8 => (IMVDefine.IMV_EPixelType)GvspPixelBgr8,
                CameraPixelFormat.BayerRG8 => (IMVDefine.IMV_EPixelType)GvspPixelBayerRg8,
                CameraPixelFormat.BayerGB8 => (IMVDefine.IMV_EPixelType)GvspPixelBayerGb8,
                CameraPixelFormat.BayerGR8 => (IMVDefine.IMV_EPixelType)GvspPixelBayerGr8,
                CameraPixelFormat.BayerBG8 => (IMVDefine.IMV_EPixelType)GvspPixelBayerBg8,
                _ => throw new NotSupportedException($"不支持的像素格式: {format}")
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


