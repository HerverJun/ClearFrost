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
    public enum CameraInstanceState
    {
        Registered,
        Open,
        Grabbing,
        Disposed
    }

    /// <summary>
    /// 相机实例，封装单个相机的操作
    /// </summary>
    public class CameraInstance : IDisposable
    {
        private readonly Func<ICamera> _cameraFactory;
        private readonly object _sync = new object();
        private ICamera? _camera;
        private bool _disposed;

        public string Id { get; }
        public CameraConfig Config { get; }
        public ICamera Camera
        {
            get
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(CameraInstance));
                    }

                    _camera ??= _cameraFactory();
                    return _camera;
                }
            }
        }

        public bool IsOpen { get; private set; }
        public CameraInstanceState State { get; private set; } = CameraInstanceState.Registered;

        /// <summary>
        /// 最近一次 IMV_Open 的 SDK 返回码（用于诊断）
        /// </summary>
        public int LastOpenResult { get; private set; }

        public CameraInstance(string id, CameraConfig config, Func<ICamera> cameraFactory)
        {
            Id = id;
            Config = config;
            _cameraFactory = cameraFactory ?? throw new ArgumentNullException(nameof(cameraFactory));
        }

        public bool Open()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(CameraInstance));
                }

                if (IsOpen)
                {
                    return true;
                }

                ICamera camera = Camera;
                int result = camera.IMV_Open();
                LastOpenResult = result;
                IsOpen = result == IMVDefine.IMV_OK;
                if (!IsOpen)
                {
                    Debug.WriteLine($"[CameraInstance] IMV_Open failed: ErrorCode={result}, Camera={Config.DisplayName}");
                    return false;
                }

                camera.IMV_SetDoubleFeatureValue("ExposureTime", Config.ExposureTime);
                camera.IMV_SetDoubleFeatureValue("GainRaw", Config.Gain);
                if (!string.IsNullOrWhiteSpace(Config.PixelFormat))
                {
                    int pixelResult = camera.IMV_SetEnumFeatureSymbol("PixelFormat", Config.PixelFormat.Trim());
                    if (pixelResult != IMVDefine.IMV_OK)
                    {
                        Debug.WriteLine($"[CameraInstance] Set PixelFormat failed: {Config.PixelFormat}, ErrorCode={pixelResult}");
                    }
                }
                camera.IMV_SetEnumFeatureSymbol("TriggerSelector", "FrameStart");
                camera.IMV_SetEnumFeatureSymbol("TriggerMode", "On");
                camera.IMV_SetEnumFeatureSymbol("TriggerSource", "Software");
                camera.IMV_SetBufferCount(10);
                State = CameraInstanceState.Open;

                return true;
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                if (!IsOpen || _camera == null)
                {
                    State = _disposed ? CameraInstanceState.Disposed : CameraInstanceState.Registered;
                    return;
                }

                if (_camera.IMV_IsGrabbing())
                {
                    _camera.IMV_StopGrabbing();
                }

                _camera.IMV_Close();
                IsOpen = false;
                State = CameraInstanceState.Registered;
            }
        }

        internal void SetGrabbing(bool isGrabbing)
        {
            lock (_sync)
            {
                if (_disposed || !IsOpen)
                {
                    return;
                }

                State = isGrabbing ? CameraInstanceState.Grabbing : CameraInstanceState.Open;
            }
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
                lock (_sync)
                {
                    try
                    {
                        if (_camera != null)
                        {
                            Close();
                            _camera.IMV_DestroyHandle();
                            _camera.Dispose();
                            _camera = null;
                        }
                    }
                    finally
                    {
                        State = CameraInstanceState.Disposed;
                    }
                }
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
        private readonly Func<CameraConfig, ICamera>? _cameraFactoryOverride;

        public event EventHandler<string>? ActiveCameraChanged;
        public event EventHandler? CameraListChanged;

        public CameraManager(bool isDebugMode = false)
            : this(isDebugMode, null)
        {
        }

        internal CameraManager(bool isDebugMode, Func<CameraConfig, ICamera>? cameraFactoryOverride)
        {
            _isDebugMode = isDebugMode;
            _cameraFactoryOverride = cameraFactoryOverride;
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

            if (ShouldUseMockCamera())
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
            if (ShouldUseMockCamera())
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
        /// 专门使用海康SDK枚举相机 (用于超级搜索)
        /// </summary>
        public List<CameraDeviceInfo> DiscoverHikvisionCameras()
        {
            if (ShouldUseMockCamera())
            {
                return new List<CameraDeviceInfo>
                {
                    new CameraDeviceInfo
                    {
                        SerialNumber = "MOCK_HIK_001",
                        Manufacturer = "Hikvision",
                        Model = "HIK-GigE",
                        UserDefinedName = "Mock Hikvision 1",
                        InterfaceType = "GigE"
                    }
                };
            }

            try
            {
                using var hikContext = new HikvisionCamera();
                return hikContext.EnumerateDevices();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraManager] Hikvision discovery failed: {ex.Message}");
                return new List<CameraDeviceInfo>();
            }
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

                Func<ICamera> cameraFactory = () => CreateCamera(config);
                var instance = new CameraInstance(config.Id, config, cameraFactory);
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

        private ICamera CreateCamera(CameraConfig config)
        {
            if (_cameraFactoryOverride != null)
            {
                return _cameraFactoryOverride(config);
            }

            if (ShouldUseMockCamera() && IsMockCameraConfig(config))
            {
                return new MockCamera();
            }

            if (string.Equals(config.Manufacturer, "Hikvision", StringComparison.OrdinalIgnoreCase))
            {
                return new CameraProviderAdapter(new HikvisionCamera(), config.SerialNumber);
            }

            return new RealCamera(config.SerialNumber);
        }

        private static bool IsMockCameraConfig(CameraConfig config)
        {
            return string.Equals(config.Manufacturer, "Mock", StringComparison.OrdinalIgnoreCase) ||
                   config.SerialNumber.StartsWith("MOCK_", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldUseMockCamera()
        {
            if (!_isDebugMode)
            {
                return false;
            }

#if DEBUG
            return true;
#else
            return string.Equals(
                Environment.GetEnvironmentVariable("CLEARFROST_ENABLE_MOCK_CAMERA"),
                "1",
                StringComparison.OrdinalIgnoreCase);
#endif
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


