using System;
using ClearFrost.Config;
using ClearFrost.Hardware;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 相机实例，封装单个相机的操作
    /// </summary>
    public class CameraInstance : IDisposable
    {
        public string Id { get; }
        public CameraConfig Config { get; }
        public ICamera Camera { get; }
        public bool IsOpen { get; private set; }

        private bool _disposed;

        public CameraInstance(string id, CameraConfig config, ICamera camera)
        {
            Id = id;
            Config = config;
            Camera = camera;
        }

        public bool Open()
        {
            if (IsOpen) return true;

            int result = Camera.IMV_Open();
            IsOpen = result == IMVDefine.IMV_OK;

            if (IsOpen)
            {
                // 应用配置
                Camera.IMV_SetDoubleFeatureValue("ExposureTime", Config.ExposureTime);
                Camera.IMV_SetDoubleFeatureValue("GainRaw", Config.Gain);
                Camera.IMV_SetEnumFeatureSymbol("TriggerMode", "On");
                Camera.IMV_SetEnumFeatureSymbol("TriggerSource", "Software");
                Camera.IMV_SetBufferCount(10);
            }

            return IsOpen;
        }

        public void Close()
        {
            if (!IsOpen) return;

            if (Camera.IMV_IsGrabbing())
                Camera.IMV_StopGrabbing();

            Camera.IMV_Close();
            IsOpen = false;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Close();
                Camera.IMV_DestroyHandle();
                Camera.Dispose();
            }

            _disposed = true;
        }

        ~CameraInstance()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// 相机管理器，支持多相机的发现、管理和切换
    /// </summary>
    public class CameraManager : IDisposable
    {
        private readonly Dictionary<string, CameraInstance> _cameras = new();
        private readonly object _lock = new();
        private string _activeCameraId = "";
        private bool _disposed;
        private readonly bool _isDebugMode;

        public event EventHandler<string>? ActiveCameraChanged;
        public event EventHandler? CameraListChanged;

        public CameraManager(bool isDebugMode = false)
        {
            _isDebugMode = isDebugMode;
        }

        /// <summary>
        /// 获取所有相机实例
        /// </summary>
        public IReadOnlyList<CameraInstance> Cameras
        {
            get
            {
                lock (_lock)
                {
                    return _cameras.Values.ToList();
                }
            }
        }

        /// <summary>
        /// 获取当前活动相机
        /// </summary>
        public CameraInstance? ActiveCamera
        {
            get
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_activeCameraId)) return null;
                    return _cameras.TryGetValue(_activeCameraId, out var cam) ? cam : null;
                }
            }
        }

        /// <summary>
        /// 活动相机 ID
        /// </summary>
        public string ActiveCameraId
        {
            get => _activeCameraId;
            set
            {
                lock (_lock)
                {
                    if (_activeCameraId != value && _cameras.ContainsKey(value))
                    {
                        _activeCameraId = value;
                        ActiveCameraChanged?.Invoke(this, value);
                    }
                }
            }
        }

        /// <summary>
        /// 枚举系统中连接的相机 (简化版：返回序列号列表，仅华睿)
        /// 保留此方法以保持向后兼容
        /// </summary>
        public List<string> DiscoverCameras()
        {
            var result = new List<string>();

            if (_isDebugMode)
            {
                // 调试模式：返回模拟相机
                result.Add("MOCK_CAM_001");
                result.Add("MOCK_CAM_002");
                return result;
            }

            try
            {
                var deviceList = new IMVDefine.IMV_DeviceList();
                // 使用官方 SDK 的 MyCamera 静态方法进行设备枚举
                int enumResult = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                if (enumResult == IMVDefine.IMV_OK && deviceList.nDevNum > 0)
                {
                    for (int i = 0; i < (int)deviceList.nDevNum; i++)
                    {
                        var devInfo = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                            deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i,
                            typeof(IMVDefine.IMV_DeviceInfo))!;
                        if (!string.IsNullOrEmpty(devInfo.serialNumber))
                        {
                            result.Add(devInfo.serialNumber);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraManager] DiscoverCameras error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 枚举所有支持品牌的相机 (新版：返回完整设备信息)
        /// </summary>
        public List<CameraDeviceInfo> DiscoverAllCameras()
        {
            if (_isDebugMode)
            {
                return new List<CameraDeviceInfo>
                {
                    new CameraDeviceInfo
                    {
                        SerialNumber = "MOCK_CAM_001",
                        Manufacturer = "Mock",
                        Model = "Virtual Camera",
                        UserDefinedName = "Mock Camera 1",
                        InterfaceType = "Virtual"
                    },
                    new CameraDeviceInfo
                    {
                        SerialNumber = "MOCK_CAM_002",
                        Manufacturer = "Mock",
                        Model = "Virtual Camera",
                        UserDefinedName = "Mock Camera 2",
                        InterfaceType = "Virtual"
                    }
                };
            }

            // 使用工厂类发现所有品牌的相机
            return CameraProviderFactory.DiscoverAll();
        }

        /// <summary>
        /// 添加相机
        /// </summary>
        public bool AddCamera(CameraConfig config)
        {
            lock (_lock)
            {
                if (_cameras.ContainsKey(config.Id))
                {
                    Debug.WriteLine($"[CameraManager] Camera {config.Id} already exists");
                    return false;
                }

                ICamera camera;
                if (_isDebugMode)
                {
                    // 调试模式：使用模拟相机，不调用任何真实 SDK
                    camera = new MockCamera();
                }
                else
                {
                    try
                    {
                        // 根据制造商选择不同的相机实现
                        if (config.Manufacturer == "Hikvision")
                        {
                            // 海康威视相机：使用新的 ICameraProvider + 适配器
                            var hikCamera = new HikvisionCamera();
                            if (!hikCamera.Open(config.SerialNumber))
                            {
                                Debug.WriteLine($"[CameraManager] Failed to open Hikvision camera: {config.SerialNumber}");
                                hikCamera.Dispose();
                                return false;
                            }
                            camera = new CameraProviderAdapter(hikCamera);
                            Debug.WriteLine($"[CameraManager] Hikvision camera connected: {config.SerialNumber}");
                        }
                        else
                        {
                            // 华睿相机 (默认)：使用原有的 RealCamera 实现
                            camera = new RealCamera();

                            // 查找相机索引
                            var deviceList = new IMVDefine.IMV_DeviceList();
                            // 使用官方 SDK 的 MyCamera 静态方法进行设备枚举
                            MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                            Debug.WriteLine($"[CameraManager] Enumerated {deviceList.nDevNum} MindVision devices");

                            int deviceIndex = -1;

                            // 用户输入的序列号，清理空格
                            string targetSerial = config.SerialNumber?.Trim() ?? "";

                            for (int i = 0; i < (int)deviceList.nDevNum; i++)
                            {
                                var devInfo = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                                    deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i,
                                    typeof(IMVDefine.IMV_DeviceInfo))!;
                                string foundSerial = devInfo.serialNumber?.Trim() ?? "";

                                Debug.WriteLine($"[CameraManager] Device[{i}] SerialNumber: '{foundSerial}'");

                                // 忽略大小写比较
                                if (string.Equals(foundSerial, targetSerial, StringComparison.OrdinalIgnoreCase))
                                {
                                    deviceIndex = i;
                                    break;
                                }
                            }

                            if (deviceIndex < 0)
                            {
                                Debug.WriteLine($"[CameraManager] MindVision camera '{targetSerial}' not found in {deviceList.nDevNum} devices");
                                camera.Dispose();
                                return false;
                            }

                            int result = camera.IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode.modeByIndex, deviceIndex);
                            if (result != IMVDefine.IMV_OK)
                            {
                                Debug.WriteLine($"[CameraManager] Failed to create handle for {config.SerialNumber}");
                                camera.Dispose();
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // SDK DLL 缺失或加载失败时，回退到模拟相机
                        Debug.WriteLine($"[CameraManager] SDK error, falling back to MockCamera: {ex.Message}");
                        camera = new MockCamera();
                    }
                }

                var instance = new CameraInstance(config.Id, config, camera);
                _cameras[config.Id] = instance;

                // 如果是第一个相机，设为活动相机
                if (_cameras.Count == 1)
                {
                    _activeCameraId = config.Id;
                }

                CameraListChanged?.Invoke(this, EventArgs.Empty);
                Debug.WriteLine($"[CameraManager] Added camera: {config.DisplayName} ({config.Id}) - {config.Manufacturer}");
                return true;
            }
        }

        /// <summary>
        /// 移除相机
        /// </summary>
        public bool RemoveCamera(string id)
        {
            lock (_lock)
            {
                if (!_cameras.TryGetValue(id, out var instance))
                    return false;

                instance.Dispose();
                _cameras.Remove(id);

                // 如果移除的是活动相机，切换到第一个
                if (_activeCameraId == id)
                {
                    _activeCameraId = _cameras.Keys.FirstOrDefault() ?? "";
                    if (!string.IsNullOrEmpty(_activeCameraId))
                        ActiveCameraChanged?.Invoke(this, _activeCameraId);
                }

                CameraListChanged?.Invoke(this, EventArgs.Empty);
                Debug.WriteLine($"[CameraManager] Removed camera: {id}");
                return true;
            }
        }

        /// <summary>
        /// 获取指定相机
        /// </summary>
        public CameraInstance? GetCamera(string id)
        {
            lock (_lock)
            {
                return _cameras.TryGetValue(id, out var cam) ? cam : null;
            }
        }

        /// <summary>
        /// 从配置加载相机
        /// </summary>
        public void LoadFromConfig(AppConfig config)
        {
            foreach (var camConfig in config.Cameras.Where(c => c.IsEnabled))
            {
                AddCamera(camConfig);
            }

            if (!string.IsNullOrEmpty(config.ActiveCameraId) && _cameras.ContainsKey(config.ActiveCameraId))
            {
                _activeCameraId = config.ActiveCameraId;
            }
        }

        /// <summary>
        /// 保存到配置
        /// </summary>
        public void SaveToConfig(AppConfig config)
        {
            config.ActiveCameraId = _activeCameraId;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                lock (_lock)
                {
                    foreach (var cam in _cameras.Values)
                    {
                        cam.Dispose();
                    }
                    _cameras.Clear();
                }
            }

            _disposed = true;
        }

        ~CameraManager()
        {
            Dispose(false);
        }
    }
}


