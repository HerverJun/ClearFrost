using System.Threading.Tasks;

namespace YOLO
{
    /// <summary>
    /// PLC设备通用接口，支持多种协议适配
    /// </summary>
    public interface IPlcDevice
    {
        /// <summary>
        /// 异步连接到PLC
        /// </summary>
        /// <returns>是否连接成功</returns>
        Task<bool> ConnectAsync();

        /// <summary>
        /// 断开PLC连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 异步读取Int16值
        /// </summary>
        /// <param name="address">寄存器地址（格式取决于协议）</param>
        /// <returns>读取结果元组：(是否成功, 读取值)</returns>
        Task<(bool Success, short Value)> ReadInt16Async(string address);

        /// <summary>
        /// 异步写入Int16值
        /// </summary>
        /// <param name="address">寄存器地址</param>
        /// <param name="value">要写入的值</param>
        /// <returns>是否写入成功</returns>
        Task<bool> WriteInt16Async(string address, short value);

        /// <summary>
        /// 最后一次操作的错误信息
        /// </summary>
        string LastError { get; }

        /// <summary>
        /// 当前是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 协议名称（用于日志显示）
        /// </summary>
        string ProtocolName { get; }
    }
}
