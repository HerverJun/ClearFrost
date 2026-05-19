using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClearFrost.Config;
using ClearFrost.Hardware;
using ClearFrost.Services;
using FluentAssertions;
using MVSDK_Net;

namespace ClearFrost.Tests.Hardware.Camera
{
    public class CameraCaptureValidationTests
    {
        [Fact]
        public void CameraInstance_Open_sets_configured_pixel_format()
        {
            using var camera = new ScriptedCamera(
                IMVDefine.IMV_EPixelType.gvspPixelMono8,
                16,
                16,
                16 * 16);
            var config = new CameraConfig
            {
                Id = "cam-1",
                SerialNumber = "SN-001",
                DisplayName = "TestCam",
                PixelFormat = "Mono8"
            };

            using var instance = new CameraInstance(config.Id, config, () => camera);

            instance.Open().Should().BeTrue();

            camera.LastPixelFormat.Should().Be("Mono8");
        }

        [Fact]
        public void CameraInstance_Open_auto_pixel_format_prefers_supported_color_format()
        {
            using var camera = new ScriptedCamera(
                IMVDefine.IMV_EPixelType.gvspPixelBayRG8,
                16,
                16,
                16 * 16,
                supportedPixelFormats: new[] { "BayerRG8", "Mono8" });
            var config = new CameraConfig
            {
                Id = "cam-1",
                SerialNumber = "SN-001",
                DisplayName = "TestCam",
                PixelFormat = "Auto"
            };

            using var instance = new CameraInstance(config.Id, config, () => camera);

            instance.Open().Should().BeTrue();

            camera.LastPixelFormat.Should().Be("BayerRG8");
            camera.PixelFormatRequests.Should().Equal("BGR8", "RGB8", "BayerRG8");
        }

        [Fact]
        public void CameraInstance_Open_auto_pixel_format_falls_back_to_mono_when_color_unsupported()
        {
            using var camera = new ScriptedCamera(
                IMVDefine.IMV_EPixelType.gvspPixelMono8,
                16,
                16,
                16 * 16,
                supportedPixelFormats: new[] { "Mono8" });
            var config = new CameraConfig
            {
                Id = "cam-1",
                SerialNumber = "SN-001",
                DisplayName = "TestCam",
                PixelFormat = "Auto"
            };

            using var instance = new CameraInstance(config.Id, config, () => camera);

            instance.Open().Should().BeTrue();

            camera.LastPixelFormat.Should().Be("Mono8");
            camera.PixelFormatRequests.Should().EndWith("Mono8");
        }

        [Fact]
        public void CaptureFrame_rejects_sdk_error_status_frame()
        {
            using var camera = new ScriptedCamera(
                IMVDefine.IMV_EPixelType.gvspPixelMono8,
                16,
                16,
                16 * 16,
                status: 1);
            using var service = CreateStartedService(camera);

            var frame = service.CaptureFrame();

            frame.Should().BeNull();
            service.LastError.Should().Contain("status=1");
            camera.ReleaseFrameCount.Should().Be(1);
        }

        [Fact]
        public void CaptureFrame_rejects_short_frame_buffer()
        {
            using var camera = new ScriptedCamera(
                IMVDefine.IMV_EPixelType.gvspPixelMono8,
                16,
                16,
                16 * 8);
            using var service = CreateStartedService(camera);

            var frame = service.CaptureFrame();

            frame.Should().BeNull();
            service.LastError.Should().Contain("actual=");
            camera.ReleaseFrameCount.Should().Be(1);
        }

        [Fact]
        public void CaptureFrame_accepts_huaray_bayer_gr8_value()
        {
            using var camera = new ScriptedCamera(
                IMVDefine.IMV_EPixelType.gvspPixelBayGR8,
                8,
                8,
                8 * 8);
            using var service = CreateStartedService(camera);

            using var frame = service.CaptureFrame();

            frame.Should().NotBeNull();
            frame!.Channels().Should().Be(3);
            camera.ClearFrameBufferCount.Should().Be(1);
        }

        private static CameraService CreateStartedService(ScriptedCamera camera)
        {
            var config = new CameraConfig
            {
                Id = "cam-1",
                SerialNumber = "SN-001",
                DisplayName = "TestCam",
                Manufacturer = "Huaray"
            };
            var manager = new CameraManager(false, _ => camera);
            manager.AddCamera(config);
            var service = new CameraService(manager);

            service.Open(config.SerialNumber, config.Manufacturer).Should().BeTrue();
            service.StartCapture();
            return service;
        }

        private sealed class ScriptedCamera : ICamera
        {
            private readonly byte[] _buffer;
            private readonly GCHandle _bufferHandle;
            private readonly IMVDefine.IMV_EPixelType _pixelFormat;
            private readonly uint _width;
            private readonly uint _height;
            private readonly uint _reportedSize;
            private readonly uint _status;
            private readonly HashSet<string>? _supportedPixelFormats;
            private bool _isGrabbing;
            private bool _disposed;

            public ScriptedCamera(
                IMVDefine.IMV_EPixelType pixelFormat,
                uint width,
                uint height,
                uint reportedSize,
                uint status = 0,
                IReadOnlyCollection<string>? supportedPixelFormats = null)
            {
                _pixelFormat = pixelFormat;
                _width = width;
                _height = height;
                _reportedSize = reportedSize;
                _status = status;
                _supportedPixelFormats = supportedPixelFormats == null
                    ? null
                    : new HashSet<string>(supportedPixelFormats, StringComparer.OrdinalIgnoreCase);
                _buffer = new byte[Math.Max(1, (int)(width * height * 3))];
                for (int i = 0; i < _buffer.Length; i++)
                {
                    _buffer[i] = (byte)(i % 251);
                }

                _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            }

            public bool IsConnected { get; private set; }

            public string LastPixelFormat { get; private set; } = string.Empty;

            public List<string> PixelFormatRequests { get; } = new();

            public int ClearFrameBufferCount { get; private set; }

            public int ReleaseFrameCount { get; private set; }

            public int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType) => IMVDefine.IMV_OK;

            public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index) => IMVDefine.IMV_OK;

            public int IMV_Open()
            {
                IsConnected = true;
                return IMVDefine.IMV_OK;
            }

            public int IMV_SetEnumFeatureSymbol(string name, string value)
            {
                if (name == "PixelFormat")
                {
                    PixelFormatRequests.Add(value);
                    LastPixelFormat = value;
                    if (_supportedPixelFormats != null && !_supportedPixelFormats.Contains(value))
                    {
                        return -1;
                    }
                }

                return IMVDefine.IMV_OK;
            }

            public int IMV_SetDoubleFeatureValue(string name, double value) => IMVDefine.IMV_OK;

            public int IMV_SetBufferCount(int count) => IMVDefine.IMV_OK;

            public int IMV_StartGrabbing()
            {
                _isGrabbing = true;
                return IMVDefine.IMV_OK;
            }

            public int IMV_StopGrabbing()
            {
                _isGrabbing = false;
                return IMVDefine.IMV_OK;
            }

            public int IMV_Close()
            {
                IsConnected = false;
                _isGrabbing = false;
                return IMVDefine.IMV_OK;
            }

            public int IMV_DestroyHandle() => IMVDefine.IMV_OK;

            public int IMV_ExecuteCommandFeature(string name) => IMVDefine.IMV_OK;

            public int IMV_ClearFrameBuffer()
            {
                ClearFrameBufferCount++;
                return IMVDefine.IMV_OK;
            }

            public bool IMV_IsGrabbing() => _isGrabbing;

            public int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout)
            {
                if (!_isGrabbing || _disposed)
                {
                    return -1;
                }

                frame.pData = _bufferHandle.AddrOfPinnedObject();
                frame.frameInfo = new IMVDefine.IMV_FrameInfo
                {
                    status = _status,
                    width = _width,
                    height = _height,
                    size = _reportedSize,
                    pixelFormat = _pixelFormat
                };

                return IMVDefine.IMV_OK;
            }

            public int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame)
            {
                ReleaseFrameCount++;
                return IMVDefine.IMV_OK;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_bufferHandle.IsAllocated)
                {
                    _bufferHandle.Free();
                }
            }
        }
    }
}
