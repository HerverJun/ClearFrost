using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 用于测试和离线调试的模拟相机。
    /// </summary>
    public class MockCamera : ICamera
    {
        private bool _isGrabbing = false;
        private bool _disposed = false;
        private byte[] _dummyBuffer;
        private GCHandle _bufferHandle;

        /// <summary>
        /// 相机是否处于可用连接状态。
        /// </summary>
        public bool IsConnected => !_disposed;

        public MockCamera()
        {
            // Create a dummy 1280x1024 Mono8 image (gray gradient)
            int w = 1280;
            int h = 1024;
            _dummyBuffer = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    _dummyBuffer[y * w + x] = (byte)((x + y) % 255);
                }
            }
            _bufferHandle = GCHandle.Alloc(_dummyBuffer, GCHandleType.Pinned);
        }

        public int IMV_EnumDevices(ref CameraDeviceList deviceList, uint interfaceType)
        {
            // Mock finding one device
            deviceList.Devices.Clear();
            deviceList.Devices.Add(new CameraDeviceInfo
            {
                SerialNumber = "EF59632AAK00074",
                Manufacturer = "Mock",
                Model = "Virtual Camera"
            });

            return CameraSdk.Ok;
        }

        public int IMV_CreateHandle(CameraCreateHandleMode mode, int index)
        {
            return CameraSdk.Ok;
        }

        public int IMV_Open()
        {
            return CameraSdk.Ok;
        }

        public int IMV_SetEnumFeatureSymbol(string name, string value)
        {
            return CameraSdk.Ok;
        }

        public int IMV_SetDoubleFeatureValue(string name, double value)
        {
            return CameraSdk.Ok;
        }

        public int IMV_SetBufferCount(int count)
        {
            return CameraSdk.Ok;
        }

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
            _isGrabbing = false;
            return CameraSdk.Ok;
        }

        public int IMV_DestroyHandle()
        {
            return CameraSdk.Ok;
        }

        public int IMV_ExecuteCommandFeature(string name)
        {
            return CameraSdk.Ok;
        }

        public int IMV_ClearFrameBuffer()
        {
            return CameraSdk.Ok;
        }

        public bool IMV_IsGrabbing()
        {
            return _isGrabbing;
        }

        public int IMV_GetFrame(ref CameraFrame frame, int timeout)
        {
            if (!_isGrabbing || _disposed) return -1;

            // Simulate frame capture delay
            Thread.Sleep(50);

            frame = new CameraFrame
            {
                DataPtr = _bufferHandle.AddrOfPinnedObject(),
                Width = 1280,
                Height = 1024,
                Size = _dummyBuffer.Length,
                PixelFormat = CameraPixelFormat.Mono8,
                RawPixelFormat = CameraSdk.GvspPixelMono8
            };

            return CameraSdk.Ok;
        }

        public int IMV_ReleaseFrame(ref CameraFrame frame)
        {
            return CameraSdk.Ok;
        }

        #region IDisposable 实现

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            try
            {
                _isGrabbing = false;
                if (_bufferHandle.IsAllocated)
                {
                    _bufferHandle.Free();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MockCamera] Dispose error: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }

        ~MockCamera()
        {
            Dispose(false);
        }

        #endregion
    }
}



