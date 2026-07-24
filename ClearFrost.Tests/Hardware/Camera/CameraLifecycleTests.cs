using System;
using ClearFrost.Config;
using ClearFrost.Hardware;
using FluentAssertions;
using MVSDK_Net;

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
            manager.ActiveCamera!.Camera.Should().NotBeOfType<MockCamera>();
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
            manager.ActiveCamera!.Camera.Should().NotBeOfType<MockCamera>();
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

            public int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType) => IMVDefine.IMV_OK;

            public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index) => IMVDefine.IMV_OK;

            public int IMV_Open()
            {
                IsConnected = true;
                return IMVDefine.IMV_OK;
            }

            public int IMV_SetEnumFeatureSymbol(string name, string value) => IMVDefine.IMV_OK;

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

            public int IMV_ClearFrameBuffer() => IMVDefine.IMV_OK;

            public bool IMV_IsGrabbing() => _isGrabbing;

            public int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout) => IMVDefine.IMV_OK;

            public int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame) => IMVDefine.IMV_OK;

            public void Dispose()
            {
            }
        }
    }
}
