// ============================================================================
// 文件名: ICameraService.cs
// 描述:   相机服务接口
//
// 功能:
//   - 定义相机控制的标准接口
//   - 支持多品牌相机 (MindVision, Hikvision)
// ============================================================================

using System;
using OpenCvSharp;

namespace ClearFrost.Interfaces
{
    /// <summary>
    /// 相机服务接口
    /// </summary>
    public interface ICameraService : IDisposable
    {
        #region 事件

        /// <summary>
        /// 帧捕获事件
        /// </summary>
        event Action<Mat>? FrameCaptured;

        /// <summary>
        /// 连接状态变化事件
        /// </summary>
        event Action<bool>? ConnectionChanged;

        /// <summary>
        /// 错误发生事件
        /// </summary>
        event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        /// <summary>
        /// 是否已打开
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// 当前相机名称
        /// </summary>
        string CameraName { get; }

        /// <summary>
        /// 最后一帧图像
        /// </summary>
        Mat? LastFrame { get; }

        #endregion

        #region 方法

        /// <summary>
        /// 打开相机
        /// </summary>
        /// <param name="serialNumber">相机序列号</param>
        /// <param name="manufacturer">制造商 (MindVision/Hikvision)</param>
        /// <returns>是否成功</returns>
        bool Open(string serialNumber, string manufacturer);

        /// <summary>
        /// 关闭相机
        /// </summary>
        void Close();

        /// <summary>
        /// 开始采集
        /// </summary>
        void StartCapture();

        /// <summary>
        /// 停止采集
        /// </summary>
        void StopCapture();

        /// <summary>
        /// 触发采集 (软触发模式)
        /// </summary>
        void TriggerOnce();

        /// <summary>
        /// 设置曝光时间
        /// </summary>
        void SetExposure(double exposureUs);

        /// <summary>
        /// 设置增益
        /// </summary>
        void SetGain(double gain);

        #endregion
    }
}

