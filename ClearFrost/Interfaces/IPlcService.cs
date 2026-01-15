// ============================================================================
// 文件名: IPlcService.cs
// 描述:   PLC 通讯服务接口
//
// 功能:
//   - 定义 PLC 连接、读写和监听的标准接口
//   - 支持多种 PLC 协议 (Mitsubishi, Siemens, Omron, Modbus)
// ============================================================================

using System;
using System.Threading.Tasks;

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
        /// <param name="protocol">协议名称字符串</param>
        /// <param name="ip">IP 地址</param>
        /// <param name="port">端口号</param>
        /// <returns>是否连接成功</returns>
        Task<bool> ConnectAsync(string protocol, string ip, int port);

        /// <summary>
        /// 断开连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 启动触发信号监听
        /// </summary>
        /// <param name="triggerAddress">触发地址</param>
        /// <param name="pollingIntervalMs">轮询间隔 (毫秒)</param>
        void StartMonitoring(short triggerAddress, int pollingIntervalMs = 500);

        /// <summary>
        /// 停止触发信号监听
        /// </summary>
        void StopMonitoring();

        /// <summary>
        /// 写入检测结果到 PLC
        /// </summary>
        /// <param name="resultAddress">结果地址</param>
        /// <param name="isQualified">是否合格</param>
        Task WriteResultAsync(short resultAddress, bool isQualified);

        /// <summary>
        /// 写入放行信号
        /// </summary>
        /// <param name="resultAddress">结果地址</param>
        Task WriteReleaseSignalAsync(short resultAddress);

        #endregion
    }
}
