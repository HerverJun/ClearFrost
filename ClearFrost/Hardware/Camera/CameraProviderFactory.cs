using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 
    /// </summary>
    public static class CameraProviderFactory
    {
        /// <summary>
        /// 
        /// </summary>
        public static readonly string[] SupportedManufacturers = { "Huaray", "MindVision", "Hikvision" };

        /// <summary>
        /// 
        /// </summary>
        public static ICameraProvider Create(string manufacturer)
        {
            return manufacturer switch
            {
                "Huaray" or "MindVision" => new MindVisionCamera(),  // MindVision 为历史兼容别名
                "Hikvision" => new HikvisionCamera(),
                _ => throw new NotSupportedException($"Unsupported camera manufacturer: {manufacturer}")
            };
        }

        /// <summary>
        /// 
        /// </summary>
        public static ICameraProvider CreateMock() => new MockCameraProvider();

        /// <summary>
        /// 发现所有品牌的相机（华睿 + 海康）
        /// </summary>
        public static List<CameraDeviceInfo> DiscoverAll()
        {
            var allDevices = new List<CameraDeviceInfo>();

            // 华睿相机枚举
            try
            {
                Debug.WriteLine("[CameraProviderFactory] Starting Huaray camera enumeration...");
                using var mv = new MindVisionCamera();
                var devices = mv.EnumerateDevices();
                allDevices.AddRange(devices);
                Debug.WriteLine($"[CameraProviderFactory] Huaray: found {devices.Count} devices");
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] Huaray SDK DLL not found: {ex.Message}");
                Debug.WriteLine($"[CameraProviderFactory] 请确保 MVSDKmd.dll 在程序目录中");
            }
            catch (BadImageFormatException ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] Huaray SDK DLL 格式错误 (32/64位不匹配): {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] Huaray enum failed: {ex.GetType().Name} - {ex.Message}");
                Debug.WriteLine($"[CameraProviderFactory] Stack: {ex.StackTrace}");
            }

            // 海康相机枚举
            try
            {
                Debug.WriteLine("[CameraProviderFactory] Starting Hikvision camera enumeration...");
                using var hik = new HikvisionCamera();
                var devices = hik.EnumerateDevices();
                allDevices.AddRange(devices);
                Debug.WriteLine($"[CameraProviderFactory] Hikvision: found {devices.Count} devices");
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] Hikvision SDK DLL not found: {ex.Message}");
                Debug.WriteLine($"[CameraProviderFactory] 请确保 MvCameraControl.dll 在程序目录中");
            }
            catch (BadImageFormatException ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] Hikvision SDK DLL 格式错误 (32/64位不匹配): {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraProviderFactory] Hikvision enum failed: {ex.GetType().Name} - {ex.Message}");
                Debug.WriteLine($"[CameraProviderFactory] Stack: {ex.StackTrace}");
            }

            Debug.WriteLine($"[CameraProviderFactory] Total cameras discovered: {allDevices.Count}");
            return allDevices;
        }

        /// <summary>
        /// 
        /// </summary>
        public static ICameraProvider? AutoDetect(string serialNumber)
        {
            // 
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

            // 
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
    /// 
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

            // 
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

            System.Threading.Thread.Sleep(50); // ģ��ɼ��ӳ�

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


