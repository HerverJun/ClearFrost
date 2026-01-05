using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using MVSDK_Net;

namespace YOLO
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
        /// 枚举系统中连接的相机 (简化版：返回序列号列表)
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
                int enumResult = RealCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                if (enumResult == IMVDefine.IMV_OK && deviceList.nDevNum > 0)
                {
                    int structSize = Marshal.SizeOf<IMVDefine.IMV_DeviceInfo>();
                    for (int i = 0; i < deviceList.nDevNum; i++)
                    {
                        IntPtr ptr = deviceList.pDevInfo + i * structSize;
                        var devInfo = Marshal.PtrToStructure<IMVDefine.IMV_DeviceInfo>(ptr);
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
                        camera = new RealCamera();

                        // 查找相机索引
                        var deviceList = new IMVDefine.IMV_DeviceList();
                        RealCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                        int deviceIndex = -1;
                        int structSize = Marshal.SizeOf<IMVDefine.IMV_DeviceInfo>();

                        for (int i = 0; i < deviceList.nDevNum; i++)
                        {
                            IntPtr ptr = deviceList.pDevInfo + i * structSize;
                            var devInfo = Marshal.PtrToStructure<IMVDefine.IMV_DeviceInfo>(ptr);
                            if (devInfo.serialNumber == config.SerialNumber)
                            {
                                deviceIndex = i;
                                break;
                            }
                        }

                        if (deviceIndex < 0)
                        {
                            Debug.WriteLine($"[CameraManager] Camera {config.SerialNumber} not found");
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
                Debug.WriteLine($"[CameraManager] Added camera: {config.DisplayName} ({config.Id})");
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
