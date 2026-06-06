// ============================================================================
// 文件名: IPlcService.cs
// 描述:   PLC 通讯服务接口
//
// 功能:
//   - 定义 PLC 连接、读写和监听的标准接口
//   - 支持多种 PLC 协议 (Mitsubishi, Siemens, Omron, Modbus)
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Hardware;

namespace ClearFrost.Interfaces
{
    /// <summary>
    /// PLC 通讯服务接口
    /// </summary>
    public interface IPlcService : IDisposable
    {
        #region 事件

        /// <summary>
        /// 连接状态变化事件
        /// </summary>
        event Action<bool>? ConnectionChanged;

        /// <summary>
        /// 收到触发信号事件
        /// </summary>
        event Action? TriggerReceived;

        /// <summary>
        /// 收到带上下文的触发信号事件。
        /// </summary>
        event Action<PlcTriggerContext>? TriggerContextReceived;

        /// <summary>
        /// 错误发生事件
        /// </summary>
        event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 当前协议名称
        /// </summary>
        string ProtocolName { get; }

        /// <summary>
        /// 最后一次错误信息
        /// </summary>
        string? LastError { get; }

        #endregion

        #region 方法

        /// <summary>
        /// 异步连接到 PLC
        /// </summary>
        Task<bool> ConnectAsync(PlcConnectionOptions options);

        /// <summary>
        /// 断开连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 启动触发信号监听
        /// </summary>
        /// <param name="triggerAddress">触发地址</param>
        /// <param name="pollingIntervalMs">轮询间隔 (毫秒)</param>
        /// <param name="triggerDelayMs">触发后延迟 (毫秒)</param>
        bool StartMonitoring(
            string triggerAddress,
            int pollingIntervalMs = 500,
            int triggerDelayMs = 800,
            PlcMonitoringOptions? options = null);

        /// <summary>
        /// 停止触发信号监听
        /// </summary>
        void StopMonitoring();

        /// <summary>
        /// 停止触发信号监听并等待后台轮询退出。
        /// </summary>
        Task StopMonitoringAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 写入检测结果到 PLC
        /// </summary>
        /// <param name="resultAddress">结果地址</param>
        /// <param name="isQualified">是否合格</param>
        Task<bool> WriteResultAsync(string resultAddress, bool isQualified);

        /// <summary>
        /// 写入指定值到 PLC 结果地址
        /// </summary>
        /// <param name="resultAddress">结果地址</param>
        /// <param name="valueToWrite">要写入的值</param>
        Task<bool> WriteResultAsync(string resultAddress, short valueToWrite);

        /// <summary>
        /// 写入放行信号
        /// </summary>
        /// <param name="resultAddress">结果地址</param>
        Task<bool> WriteReleaseSignalAsync(string resultAddress);

        /// <summary>
        /// 从 PLC 连续字地址读取字符串，用于轻量条码追溯。
        /// </summary>
        Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName);

        #endregion
    }
}
