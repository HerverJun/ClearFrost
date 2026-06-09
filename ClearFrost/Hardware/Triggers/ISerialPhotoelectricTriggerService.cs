// ============================================================================
// 文件名: ISerialPhotoelectricTriggerService.cs
// 描述:   串口光电触发服务接口
//
// 功能:
//   - 串口光电触发监听与生命周期管理
//   - 连接状态与错误事件
// ============================================================================

using System;
using System.Threading.Tasks;

namespace ClearFrost.Hardware.Triggers
{
    /// <summary>
    /// 可选串口信息。
    /// </summary>
    public sealed class SerialPhotoelectricPortInfo
    {
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsPreferred { get; init; }
    }

    /// <summary>
    /// 串口光电触发服务接口
    /// </summary>
    public interface ISerialPhotoelectricTriggerService : IDisposable
    {
        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        event Action<bool>? ConnectionChanged;

        /// <summary>
        /// 收到有效触发事件（01 11 边沿，且未处于 blocked 状态）
        /// </summary>
        event Action? TriggerReceived;

        /// <summary>
        /// 错误事件（连接失败、断开异常等）
        /// </summary>
        event Action<string>? ErrorOccurred;

        /// <summary>
        /// 当前是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 最近一次错误信息
        /// </summary>
        string? LastError { get; }

        /// <summary>
        /// 异步打开串口并开始监听
        /// </summary>
        Task<bool> StartAsync(string portName, int baudRate, int debounceMs = 50, int timeoutMs = 1000);

        /// <summary>
        /// 停止监听并关闭串口
        /// </summary>
        void Stop();

        /// <summary>
        /// 发送测试帧（手动模拟 01 11 触发）
        /// </summary>
        Task<bool> SendTestTriggerAsync();

        /// <summary>
        /// 获取可用串口列表（带友好名称）
        /// </summary>
        Task<SerialPhotoelectricPortInfo[]> GetAvailablePortsAsync();
    }
}
