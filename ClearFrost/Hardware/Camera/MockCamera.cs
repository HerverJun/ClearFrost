using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MVSDK_Net;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 模拟相机实现，用于调试和测试
    /// </summary>
    public class MockCamera : ICamera
    {
        private bool _isGrabbing = false;
        private bool _disposed = false;
        private byte[] _dummyBuffer;
        private GCHandle _bufferHandle;

        /// <summary>
        /// 相机是否已连接（模拟相机始终返回 true）
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

        public int IMV_EnumDevices(ref IMVDefine.IMV_DeviceList deviceList, uint interfaceType)
        {
            // Mock finding one device
            deviceList.nDevNum = 1;

            // Allocate unmanaged memory for one device info
            int size = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
            deviceList.pDevInfo = Marshal.AllocHGlobal(size);

            var info = new IMVDefine.IMV_DeviceInfo
            {
                serialNumber = "EF59632AAK00074"
            };
            Marshal.StructureToPtr(info, deviceList.pDevInfo, false);

            return IMVDefine.IMV_OK;
        }

        public int IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode mode, int index)
        {
            return IMVDefine.IMV_OK;
        }

        public int IMV_Open()
        {
            return IMVDefine.IMV_OK;
        }

        public int IMV_SetEnumFeatureSymbol(string name, string value)
        {
            return IMVDefine.IMV_OK;
        }

        public int IMV_SetDoubleFeatureValue(string name, double value)
        {
            return IMVDefine.IMV_OK;
        }

        public int IMV_SetBufferCount(int count)
        {
            return IMVDefine.IMV_OK;
        }

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
            _isGrabbing = false;
            return IMVDefine.IMV_OK;
        }

        public int IMV_DestroyHandle()
        {
            return IMVDefine.IMV_OK;
        }

        public int IMV_ExecuteCommandFeature(string name)
        {
            return IMVDefine.IMV_OK;
        }

        public bool IMV_IsGrabbing()
        {
            return _isGrabbing;
        }

        public int IMV_GetFrame(ref IMVDefine.IMV_Frame frame, int timeout)
        {
            if (!_isGrabbing || _disposed) return -1;

            // Simulate frame capture delay
            Thread.Sleep(50);

            frame.pData = _bufferHandle.AddrOfPinnedObject();
            frame.frameInfo = new IMVDefine.IMV_FrameInfo
            {
                width = 1280,
                height = 1024,
                size = (uint)_dummyBuffer.Length,
                pixelFormat = IMVDefine.IMV_EPixelType.gvspPixelMono8
            };

            return IMVDefine.IMV_OK;
        }

        public int IMV_ReleaseFrame(ref IMVDefine.IMV_Frame frame)
        {
            return IMVDefine.IMV_OK;
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



