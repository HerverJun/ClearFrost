using System;
using ClearFrost.Config;
using ClearFrost.Hardware;
using FluentAssertions;

namespace ClearFrost.Tests.Hardware.Camera
{
    public class CameraLifecycleTests
    {
        [Fact]
        public void CameraInstance_状态机会按预期流转()
        {
            var camera = new FakeCamera();
            var config = new CameraConfig
            {
                Id = "cam-1",
                SerialNumber = "SN-001",
                DisplayName = "TestCam"
            };
            using var instance = new CameraInstance(config.Id, config, () => camera);

            instance.State.Should().Be(CameraInstanceState.Registered);

            instance.Open().Should().BeTrue();
            instance.State.Should().Be(CameraInstanceState.Open);

            instance.SetGrabbing(true);
            instance.State.Should().Be(CameraInstanceState.Grabbing);

            instance.SetGrabbing(false);
            instance.State.Should().Be(CameraInstanceState.Open);

            instance.Close();
            instance.State.Should().Be(CameraInstanceState.Registered);

            instance.Dispose();
            instance.State.Should().Be(CameraInstanceState.Disposed);
        }

        [Fact]
        public void CameraManager_LoadFromConfig只注册不打开设备()
        {
            int factoryCallCount = 0;
            var config = new CameraConfig
            {
                Id = "cam-1",
                SerialNumber = "SN-001",
                DisplayName = "TestCam",
                Manufacturer = "Huaray",
                IsEnabled = true
            };
            var appConfig = new AppConfig
            {
                Cameras = { config },
                ActiveCameraId = config.Id
            };
            using var manager = new CameraManager(false, _ =>
            {
                factoryCallCount++;
                return new FakeCamera();
            });

            manager.LoadFromConfig(appConfig);

            manager.Cameras.Should().ContainSingle(cam => cam.Id == config.Id);
            manager.ActiveCameraId.Should().Be(config.Id);
            manager.ActiveCamera.Should().NotBeNull();
            manager.ActiveCamera!.State.Should().Be(CameraInstanceState.Registered);
            manager.ActiveCamera.IsOpen.Should().BeFalse();
            factoryCallCount.Should().Be(0);
        }

        [Fact]
        public void CameraManager_调试模式不会把真实相机配置强制替换成模拟相机()
        {
            var config = new CameraConfig
            {
                Id = "cam-real",
                SerialNumber = "EF59632AAK00291",
                DisplayName = "RealCam",
                Manufacturer = "Huaray",
                IsEnabled = true
            };
            var appConfig = new AppConfig
            {
                Cameras = { config },
                ActiveCameraId = config.Id,
                IsDebugMode = true
            };
            using var manager = new CameraManager(appConfig.IsDebugMode);

            manager.LoadFromConfig(appConfig);

            manager.ActiveCamera.Should().NotBeNull();
#if DEBUG
            manager.ActiveCamera!.Camera.Should().NotBeOfType<MockCamera>();
#else
            manager.ActiveCamera!.Camera.Should().BeOfType<RealCamera>();
#endif
        }

        [Fact]
        public void CameraManager_Mock配置遵循构建安全开关()
        {
            var config = new CameraConfig
            {
                Id = "cam-mock",
                SerialNumber = "MOCK_CAM_001",
                DisplayName = "MockCam",
                Manufacturer = "Mock",
                IsEnabled = true
            };
            var appConfig = new AppConfig
            {
                Cameras = { config },
                ActiveCameraId = config.Id,
                IsDebugMode = true
            };
            using var manager = new CameraManager(appConfig.IsDebugMode);

            manager.LoadFromConfig(appConfig);

            manager.ActiveCamera.Should().NotBeNull();
#if DEBUG
            manager.ActiveCamera!.Camera.Should().BeOfType<MockCamera>();
#else
            manager.ActiveCamera!.Camera.Should().BeOfType<RealCamera>();
#endif
        }

        [Fact]
        public void CameraManager_ReloadFromConfig遇到重复Id时保留原注册表()
        {
            var originalConfig = new CameraConfig
            {
                Id = "cam-original",
                SerialNumber = "SN-ORIGINAL",
                DisplayName = "OriginalCam",
                Manufacturer = "Huaray",
                IsEnabled = true
            };
            var appConfig = new AppConfig
            {
                Cameras = { originalConfig },
                ActiveCameraId = originalConfig.Id
            };
            using var manager = new CameraManager(false, _ => new FakeCamera());
            manager.LoadFromConfig(appConfig);

            var duplicateConfig = new AppConfig
            {
                Cameras =
                {
                    new CameraConfig { Id = "dup", SerialNumber = "SN-1", IsEnabled = true },
                    new CameraConfig { Id = "dup", SerialNumber = "SN-2", IsEnabled = true }
                },
                ActiveCameraId = "dup"
            };

            Action act = () => manager.ReloadFromConfig(duplicateConfig);

            act.Should().Throw<InvalidOperationException>().WithMessage("*重复*");
            manager.Cameras.Should().ContainSingle(camera => camera.Id == "cam-original");
            manager.ActiveCameraId.Should().Be("cam-original");
        }

        private sealed class FakeCamera : ICamera
        {
            private bool _isGrabbing;

            public bool IsConnected { get; private set; }

            public int IMV_EnumDevices(ref CameraDeviceList deviceList, uint interfaceType) => CameraSdk.Ok;

            public int IMV_CreateHandle(CameraCreateHandleMode mode, int index) => CameraSdk.Ok;

            public int IMV_Open()
            {
                IsConnected = true;
                return CameraSdk.Ok;
            }

            public int IMV_SetEnumFeatureSymbol(string name, string value) => CameraSdk.Ok;

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

            public int IMV_ClearFrameBuffer() => CameraSdk.Ok;

            public bool IMV_IsGrabbing() => _isGrabbing;

            public int IMV_GetFrame(ref CameraFrame frame, int timeout) => CameraSdk.Ok;

            public int IMV_ReleaseFrame(ref CameraFrame frame) => CameraSdk.Ok;

            public void Dispose()
            {
            }
        }
    }
}
