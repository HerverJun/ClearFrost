using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ClearFrost.Config;
using ClearFrost.Hardware;
using ClearFrost.Services;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Hardware.Camera
{
    public class CameraCaptureValidationTests
    {
        [Fact]
        public void CameraInstance_Open_sets_configured_pixel_format()
        {
            using var camera = new ScriptedCamera(
                CameraPixelFormat.Mono8,
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
                CameraPixelFormat.BayerRG8,
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
                CameraPixelFormat.Mono8,
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
        public void CameraInstance_Open_explicit_bgr_falls_back_to_bayer_color_without_mono()
        {
            using var camera = new ScriptedCamera(
                CameraPixelFormat.BayerRG8,
                16,
                16,
                16 * 16,
                supportedPixelFormats: new[] { "BayerRG8", "Mono8" });
            var config = new CameraConfig
            {
                Id = "cam-1",
                SerialNumber = "SN-001",
                DisplayName = "TestCam",
                PixelFormat = "BGR8"
            };

            using var instance = new CameraInstance(config.Id, config, () => camera);

            instance.Open().Should().BeTrue();

            camera.LastPixelFormat.Should().Be("BayerRG8");
            camera.PixelFormatRequests.Should().Equal("BGR8", "RGB8", "BayerRG8");
            camera.PixelFormatRequests.Should().NotContain("Mono8");
        }

        [Fact]
        public void CaptureFrame_rejects_sdk_error_status_frame()
        {
            using var camera = new ScriptedCamera(
                CameraPixelFormat.Mono8,
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
                CameraPixelFormat.Mono8,
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
                CameraPixelFormat.BayerGR8,
                8,
                8,
                8 * 8);
            using var service = CreateStartedService(camera);

            using var frame = service.CaptureFrame();

            frame.Should().NotBeNull();
            frame!.Channels().Should().Be(3);
            camera.ClearFrameBufferCount.Should().Be(1);
        }

        [Theory]
        [InlineData(0x01080009, BayerPattern.RGGB)]
        [InlineData(0x0108000A, BayerPattern.GBRG)]
        [InlineData(0x01080008, BayerPattern.GRBG)]
        [InlineData(0x0108000B, BayerPattern.BGGR)]
        public void ConvertRawFrameToMat_genicam_bayer_formats_preserve_red_blue_order(int pixelFormatValue, BayerPattern pattern)
        {
            const int width = 8;
            const int height = 8;
            byte[] buffer = CreateSolidRedBayerBuffer(width, height, pattern);

            using Mat mat = ConvertRawFrameToMat(buffer, width, height, unchecked((uint)pixelFormatValue));

            mat.Channels().Should().Be(3);
            Scalar mean = Cv2.Mean(mat);
            mean.Val2.Should().BeGreaterThan(mean.Val0 + 100);
        }

        [Fact]
        public void ConvertRawFrameToMat_bgr8_keeps_bgr_channel_order()
        {
            byte[] buffer =
            {
                10, 20, 30,
                40, 50, 60
            };

            using Mat mat = ConvertRawFrameToMat(buffer, 2, 1, 0x02180015);

            Vec3b first = mat.At<Vec3b>(0, 0);
            first.Item0.Should().Be(10);
            first.Item1.Should().Be(20);
            first.Item2.Should().Be(30);
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

        private static Mat ConvertRawFrameToMat(byte[] buffer, int width, int height, uint pixelFormat)
        {
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var method = typeof(CameraService).GetMethod(
                    "ConvertRawFrameToMat",
                    BindingFlags.NonPublic | BindingFlags.Static);
                method.Should().NotBeNull();

                return (Mat)method!.Invoke(null, new object[]
                {
                    handle.AddrOfPinnedObject(),
                    width,
                    height,
                    0,
                    pixelFormat
                })!;
            }
            finally
            {
                handle.Free();
            }
        }

        private static byte[] CreateSolidRedBayerBuffer(int width, int height, BayerPattern pattern)
        {
            var buffer = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (GetBayerColor(pattern, x, y) == BayerColor.Red)
                    {
                        buffer[y * width + x] = 255;
                    }
                }
            }

            return buffer;
        }

        private static BayerColor GetBayerColor(BayerPattern pattern, int x, int y)
        {
            bool evenRow = y % 2 == 0;
            bool evenColumn = x % 2 == 0;

            return pattern switch
            {
                BayerPattern.RGGB => evenRow
                    ? evenColumn ? BayerColor.Red : BayerColor.Green
                    : evenColumn ? BayerColor.Green : BayerColor.Blue,
                BayerPattern.GBRG => evenRow
                    ? evenColumn ? BayerColor.Green : BayerColor.Blue
                    : evenColumn ? BayerColor.Red : BayerColor.Green,
                BayerPattern.GRBG => evenRow
                    ? evenColumn ? BayerColor.Green : BayerColor.Red
                    : evenColumn ? BayerColor.Blue : BayerColor.Green,
                BayerPattern.BGGR => evenRow
                    ? evenColumn ? BayerColor.Blue : BayerColor.Green
                    : evenColumn ? BayerColor.Green : BayerColor.Red,
                _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, null)
            };
        }

        public enum BayerPattern
        {
            RGGB,
            GBRG,
            GRBG,
            BGGR
        }

        private enum BayerColor
        {
            Red,
            Green,
            Blue
        }

        private sealed class ScriptedCamera : ICamera
        {
            private readonly byte[] _buffer;
            private readonly GCHandle _bufferHandle;
            private readonly CameraPixelFormat _pixelFormat;
            private readonly uint _width;
            private readonly uint _height;
            private readonly uint _reportedSize;
            private readonly uint _status;
            private readonly HashSet<string>? _supportedPixelFormats;
            private bool _isGrabbing;
            private bool _disposed;

            public ScriptedCamera(
                CameraPixelFormat pixelFormat,
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

            public int IMV_EnumDevices(ref CameraDeviceList deviceList, uint interfaceType) => CameraSdk.Ok;

            public int IMV_CreateHandle(CameraCreateHandleMode mode, int index) => CameraSdk.Ok;

            public int IMV_Open()
            {
                IsConnected = true;
                return CameraSdk.Ok;
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

                return CameraSdk.Ok;
            }

            public int IMV_SetDoubleFeatureValue(string name, double value) => CameraSdk.Ok;

            public int IMV_SetBufferCount(int count) => CameraSdk.Ok;

            public int IMV_StartGrabbing()
            {
                _isGrabbing = true;
                return CameraSdk.Ok;
            }

            public int IMV_StopGrabbing()
            {
                _isGrabbing = false;
                return CameraSdk.Ok;
            }

            public int IMV_Close()
            {
                IsConnected = false;
                _isGrabbing = false;
                return CameraSdk.Ok;
            }

            public int IMV_DestroyHandle() => CameraSdk.Ok;

            public int IMV_ExecuteCommandFeature(string name) => CameraSdk.Ok;

            public int IMV_ClearFrameBuffer()
            {
                ClearFrameBufferCount++;
                return CameraSdk.Ok;
            }

            public bool IMV_IsGrabbing() => _isGrabbing;

            public int IMV_GetFrame(ref CameraFrame frame, int timeout)
            {
                if (!_isGrabbing || _disposed)
                {
                    return -1;
                }

                frame = new CameraFrame
                {
                    DataPtr = _bufferHandle.AddrOfPinnedObject(),
                    Status = _status,
                    Width = (int)_width,
                    Height = (int)_height,
                    Size = (int)_reportedSize,
                    PixelFormat = _pixelFormat,
                    RawPixelFormat = CameraSdk.ToRawPixelFormat(_pixelFormat)
                };

                return CameraSdk.Ok;
            }

            public int IMV_ReleaseFrame(ref CameraFrame frame)
            {
                ReleaseFrameCount++;
                return CameraSdk.Ok;
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
