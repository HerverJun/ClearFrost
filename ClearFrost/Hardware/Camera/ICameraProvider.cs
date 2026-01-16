using System;
using System.Collections.Generic;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 
    /// </summary>
    public enum CameraPixelFormat
    {
        Mono8,
        RGB8,
        BGR8,
        BayerRG8,
        BayerGB8,
        BayerGR8,
        BayerBG8,
        Unknown
    }

    /// <summary>
    /// 
    /// </summary>
    public class CameraDeviceInfo
    {
        /// <summary>
        /// 
        /// </summary>
        public string SerialNumber { get; set; } = "";

        /// <summary>
        /// 
        /// </summary>
        public string Manufacturer { get; set; } = "";

        /// <summary>
        /// 
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// 
        /// </summary>
        public string UserDefinedName { get; set; } = "";

        /// <summary>
        /// 
        /// </summary>
        public string InterfaceType { get; set; } = "";

        public override string ToString() => $"[{Manufacturer}] {Model} ({SerialNumber})";
    }

    /// <summary>
    /// 
    /// </summary>
    public class CameraFrame : IDisposable
    {
        /// <summary>
        /// 
        /// </summary>
        public IntPtr DataPtr { get; set; } = IntPtr.Zero;

        /// <summary>
        /// 
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public CameraPixelFormat PixelFormat { get; set; } = CameraPixelFormat.Mono8;

        /// <summary>
        /// 
        /// </summary>
        public ulong FrameNumber { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public ulong Timestamp { get; set; }

        /// <summary>
        /// 
        /// </summary>
        internal bool NeedsNativeRelease { get; set; } = false;

        /// <summary>
        /// 
        /// </summary>
        internal Action<CameraFrame>? ReleaseCallback { get; set; }

        private bool _disposed = false;

        public void Dispose()
        {
            if (_disposed) return;
            ReleaseCallback?.Invoke(this);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~CameraFrame() => Dispose();
    }

    /// <summary>
    /// 
    /// </summary>
    public interface ICameraProvider : IDisposable
    {
        /// <summary>
        /// 
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// 
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 
        /// </summary>
        bool IsGrabbing { get; }

        /// <summary>
        /// 
        /// </summary>
        CameraDeviceInfo? CurrentDevice { get; }

        /// <summary>
        /// 
        /// </summary>
        List<CameraDeviceInfo> EnumerateDevices();

        /// <summary>
        /// 
        /// </summary>
        bool Open(string serialNumber);

        /// <summary>
        /// 
        /// </summary>
        bool Close();

        /// <summary>
        /// 
        /// </summary>
        bool StartGrabbing();

        /// <summary>
        /// 
        /// </summary>
        bool StopGrabbing();

        /// <summary>
        /// 
        /// </summary>
        /// 
        /// 
        CameraFrame? GetFrame(int timeoutMs = 1000);

        /// <summary>
        /// 
        /// </summary>
        bool SetExposure(double microseconds);

        /// <summary>
        /// 
        /// </summary>
        bool SetGain(double value);

        /// <summary>
        /// 
        /// </summary>
        /// 
        bool SetTriggerMode(bool softwareTrigger);

        /// <summary>
        /// 
        /// </summary>
        bool ExecuteSoftwareTrigger();
    }
}


