using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace YOLO
{
    /// <summary>
    /// 相机提供者工厂
    /// </summary>
    public static class CameraProviderFactory
    {
        /// <summary>
        /// 支持的相机制造商列表
        /// </summary>
        public static readonly string[] SupportedManufacturers = { "MindVision", "Hikvision" };

        /// <summary>
        /// 根据制造商创建相机实例
        /// </summary>
        public static ICameraProvider Create(string manufacturer)
        {
            return manufacturer switch
            {
                "MindVision" => new MindVisionCamera(),
                "Hikvision" => new HikvisionCamera(),
                _ => throw new NotSupportedException($"Unsupported camera manufacturer: {manufacturer}")
            };
        }

        /// <summary>
        /// 创建模拟相机
        /// </summary>
        public static ICameraProvider CreateMock() => new MockCameraProvider();

        /// <summary>
        /// 探测所有支持品牌的相机
        /// </summary>
        /// <returns>所有发现的相机设备列表</returns>
        public static List<CameraDeviceInfo> DiscoverAll()
        {
            var allDevices = new List<CameraDeviceInfo>();

            // 尝试迈德威视
            try
            {
                using var mv = new MindVisionCamera();
                var devices = mv.EnumerateDevices();
                allDevices.AddRange(devices);
                Debug.WriteLine($"[CameraProviderFactory] MindVision: found {devices.Count} devices");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] MindVision enum failed: {ex.Message}");
            }

            // 尝试海康威视
            try
            {
                using var hik = new HikvisionCamera();
                var devices = hik.EnumerateDevices();
                allDevices.AddRange(devices);
                Debug.WriteLine($"[CameraProviderFactory] Hikvision: found {devices.Count} devices");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] Hikvision enum failed: {ex.Message}");
            }

            return allDevices;
        }

        /// <summary>
        /// 尝试自动检测相机并返回对应的提供者
        /// </summary>
        public static ICameraProvider? AutoDetect(string serialNumber)
        {
            // 先尝试迈德威视
            try
            {
                var mv = new MindVisionCamera();
                var devices = mv.EnumerateDevices();
                if (devices.Exists(d => d.SerialNumber == serialNumber))
                {
                    return mv;
                }
                mv.Dispose();
            }
            catch { }

            // 再尝试海康威视
            try
            {
                var hik = new HikvisionCamera();
                var devices = hik.EnumerateDevices();
                if (devices.Exists(d => d.SerialNumber == serialNumber))
                {
                    return hik;
                }
                hik.Dispose();
            }
            catch { }

            return null;
        }
    }

    /// <summary>
    /// 模拟相机提供者 (用于调试)
    /// </summary>
    public class MockCameraProvider : ICameraProvider
    {
        private bool _isConnected = false;
        private bool _isGrabbing = false;
        private bool _disposed = false;
        private byte[]? _dummyBuffer;
        private System.Runtime.InteropServices.GCHandle _bufferHandle;
        private CameraDeviceInfo? _currentDevice;

        public string ProviderName => "Mock";
        public bool IsConnected => _isConnected && !_disposed;
        public bool IsGrabbing => _isGrabbing;
        public CameraDeviceInfo? CurrentDevice => _currentDevice;

        public List<CameraDeviceInfo> EnumerateDevices()
        {
            return new List<CameraDeviceInfo>
            {
                new CameraDeviceInfo
                {
                    SerialNumber = "MOCK-001",
                    Manufacturer = "Mock",
                    Model = "Virtual Camera",
                    UserDefinedName = "Mock Camera for Testing",
                    InterfaceType = "Virtual"
                }
            };
        }

        public bool Open(string serialNumber)
        {
            if (_disposed) return false;

            // 创建测试图像 (1280x1024 灰度渐变)
            int w = 1280, h = 1024;
            _dummyBuffer = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    _dummyBuffer[y * w + x] = (byte)((x + y) % 255);

            _bufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(_dummyBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            _isConnected = true;
            _currentDevice = new CameraDeviceInfo
            {
                SerialNumber = serialNumber,
                Manufacturer = "Mock",
                Model = "Virtual Camera"
            };
            return true;
        }

        public bool Close()
        {
            _isConnected = false;
            _isGrabbing = false;
            _currentDevice = null;
            if (_bufferHandle.IsAllocated)
                _bufferHandle.Free();
            _dummyBuffer = null;
            return true;
        }

        public bool StartGrabbing()
        {
            if (!_isConnected) return false;
            _isGrabbing = true;
            return true;
        }

        public bool StopGrabbing()
        {
            _isGrabbing = false;
            return true;
        }

        public CameraFrame? GetFrame(int timeoutMs = 1000)
        {
            if (!_isConnected || !_isGrabbing || _dummyBuffer == null) return null;

            System.Threading.Thread.Sleep(50); // 模拟采集延迟

            return new CameraFrame
            {
                DataPtr = _bufferHandle.AddrOfPinnedObject(),
                Width = 1280,
                Height = 1024,
                Size = _dummyBuffer.Length,
                PixelFormat = CameraPixelFormat.Mono8,
                FrameNumber = (ulong)DateTime.Now.Ticks,
                Timestamp = (ulong)DateTime.Now.Ticks
            };
        }

        public bool SetExposure(double microseconds) => true;
        public bool SetGain(double value) => true;
        public bool SetTriggerMode(bool softwareTrigger) => true;
        public bool ExecuteSoftwareTrigger() => true;

        public void Dispose()
        {
            if (_disposed) return;
            Close();
            _disposed = true;
        }
    }
}
