using System;
using System.Collections.Generic;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 通用像素格式枚举
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
    /// 相机设备信息
    /// </summary>
    public class CameraDeviceInfo
    {
        /// <summary>
        /// 序列号
        /// </summary>
        public string SerialNumber { get; set; } = "";

        /// <summary>
        /// 制造商 ("MindVision" / "Hikvision")
        /// </summary>
        public string Manufacturer { get; set; } = "";

        /// <summary>
        /// 型号
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// 用户自定义名称
        /// </summary>
        public string UserDefinedName { get; set; } = "";

        /// <summary>
        /// 接口类型 (GigE / USB3 / CameraLink)
        /// </summary>
        public string InterfaceType { get; set; } = "";

        public override string ToString() => $"[{Manufacturer}] {Model} ({SerialNumber})";
    }

    /// <summary>
    /// 通用相机数据帧
    /// </summary>
    public class CameraFrame : IDisposable
    {
        /// <summary>
        /// 图像数据指针
        /// </summary>
        public IntPtr DataPtr { get; set; } = IntPtr.Zero;

        /// <summary>
        /// 图像宽度
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// 图像高度
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// 数据大小 (bytes)
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// 像素格式
        /// </summary>
        public CameraPixelFormat PixelFormat { get; set; } = CameraPixelFormat.Mono8;

        /// <summary>
        /// 帧号
        /// </summary>
        public ulong FrameNumber { get; set; }

        /// <summary>
        /// 时间戳
        /// </summary>
        public ulong Timestamp { get; set; }

        /// <summary>
        /// 是否需要释放原生资源 (由提供者决定)
        /// </summary>
        internal bool NeedsNativeRelease { get; set; } = false;

        /// <summary>
        /// 释放帧的回调 (由提供者设置)
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
    /// 通用相机提供者接口 - 不依赖任何特定 SDK
    /// </summary>
    public interface ICameraProvider : IDisposable
    {
        /// <summary>
        /// 提供者名称 (MindVision / Hikvision)
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// 相机是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 相机是否正在采集
        /// </summary>
        bool IsGrabbing { get; }

        /// <summary>
        /// 当前连接的设备信息
        /// </summary>
        CameraDeviceInfo? CurrentDevice { get; }

        /// <summary>
        /// 枚举系统中的相机设备
        /// </summary>
        List<CameraDeviceInfo> EnumerateDevices();

        /// <summary>
        /// 打开指定序列号的相机
        /// </summary>
        bool Open(string serialNumber);

        /// <summary>
        /// 关闭相机
        /// </summary>
        bool Close();

        /// <summary>
        /// 开始采集
        /// </summary>
        bool StartGrabbing();

        /// <summary>
        /// 停止采集
        /// </summary>
        bool StopGrabbing();

        /// <summary>
        /// 获取一帧图像
        /// </summary>
        /// <param name="timeoutMs">超时时间 (毫秒)</param>
        /// <returns>帧数据，失败返回 null</returns>
        CameraFrame? GetFrame(int timeoutMs = 1000);

        /// <summary>
        /// 设置曝光时间 (微秒)
        /// </summary>
        bool SetExposure(double microseconds);

        /// <summary>
        /// 设置增益
        /// </summary>
        bool SetGain(double value);

        /// <summary>
        /// 设置触发模式
        /// </summary>
        /// <param name="softwareTrigger">true=软触发, false=连续采集</param>
        bool SetTriggerMode(bool softwareTrigger);

        /// <summary>
        /// 执行软触发
        /// </summary>
        bool ExecuteSoftwareTrigger();
    }
}


