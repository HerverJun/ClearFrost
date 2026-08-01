using System;
using System.Collections.Generic;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// Adapts the provider-neutral camera contract to the legacy ICamera surface.
    /// </summary>
    public class CameraProviderAdapter : ICamera, ICameraFeatureInspector
    {
        private readonly ICameraProvider _provider;
        private readonly string _deviceSerialNumber;
        private bool _disposed;
        private CameraFrame? _currentFrame;

        public CameraProviderAdapter(ICameraProvider provider, string? deviceSerialNumber = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _deviceSerialNumber = string.IsNullOrWhiteSpace(deviceSerialNumber)
                ? provider.CurrentDevice?.SerialNumber ?? string.Empty
                : deviceSerialNumber.Trim();
        }

        public bool IsConnected => _provider.IsConnected;

        public int IMV_EnumDevices(ref CameraDeviceList deviceList, uint interfaceType)
        {
            deviceList.Devices.Clear();
            deviceList.Devices.AddRange(_provider.EnumerateDevices());
            return CameraSdk.Ok;
        }

        public int IMV_CreateHandle(CameraCreateHandleMode mode, int index) => CameraSdk.Ok;

        public int IMV_Open()
        {
            if (_provider.IsConnected)
            {
                return CameraSdk.Ok;
            }

            string serialNumber = !string.IsNullOrWhiteSpace(_deviceSerialNumber)
                ? _deviceSerialNumber
                : _provider.CurrentDevice?.SerialNumber ?? string.Empty;

            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                return -1;
            }

            try
            {
                return _provider.Open(serialNumber) ? CameraSdk.Ok : -1;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraProviderAdapter] Reopen failed: {ex.Message}");
                return -1;
            }
        }

        public int IMV_SetEnumFeatureSymbol(string name, string value)
        {
            if (name == "TriggerMode")
            {
                _provider.SetTriggerMode(value == "On");
            }
            else if (name == "PixelFormat")
            {
                return _provider.SetPixelFormat(value) ? CameraSdk.Ok : -1;
            }

            return CameraSdk.Ok;
        }

        public bool TryGetEnumFeatureSymbol(string name, out string value)
        {
            value = string.Empty;
            if (name != "PixelFormat" || _currentFrame == null)
            {
                return false;
            }

            value = _currentFrame.PixelFormat.ToString();
            return _currentFrame.PixelFormat != CameraPixelFormat.Unknown;
        }

        public IReadOnlyList<string> GetEnumFeatureEntries(string name)
        {
            if (name != "PixelFormat")
            {
                return Array.Empty<string>();
            }

            return new[]
            {
                "BGR8",
                "RGB8",
                "BayerRG8",
                "BayerGB8",
                "BayerGR8",
                "BayerBG8",
                "Mono8"
            };
        }

        public int IMV_SetDoubleFeatureValue(string name, double value)
        {
            if (name == "ExposureTime")
            {
                _provider.SetExposure(value);
            }
            else if (name == "GainRaw" || name == "Gain")
            {
                _provider.SetGain(value);
            }

            return CameraSdk.Ok;
        }

        public int IMV_SetBufferCount(int count) => CameraSdk.Ok;

        public int IMV_StartGrabbing() => _provider.StartGrabbing() ? CameraSdk.Ok : -1;

        public int IMV_StopGrabbing() => _provider.StopGrabbing() ? CameraSdk.Ok : -1;

        public int IMV_Close()
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
            return _provider.Close() ? CameraSdk.Ok : -1;
        }

        public int IMV_DestroyHandle() => CameraSdk.Ok;

        public int IMV_ExecuteCommandFeature(string name)
        {
            if (name == "TriggerSoftware")
            {
                _provider.ExecuteSoftwareTrigger();
            }

            return CameraSdk.Ok;
        }

        public int IMV_ClearFrameBuffer() => CameraSdk.Ok;

        public bool IMV_IsGrabbing() => _provider.IsGrabbing;

        public int IMV_GetFrame(ref CameraFrame frame, int timeout)
        {
            _currentFrame?.Dispose();
            _currentFrame = _provider.GetFrame(timeout);

            if (_currentFrame == null)
            {
                return -1;
            }

            frame = _currentFrame;
            return CameraSdk.Ok;
        }

        public int IMV_ReleaseFrame(ref CameraFrame frame)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
            frame = new CameraFrame();
            return CameraSdk.Ok;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _currentFrame?.Dispose();
            _provider.Dispose();
            _disposed = true;
        }
    }
}
