using System.Threading.Tasks;

// ============================================================================
// 文件名: IPlcDevice.cs
// 作者: 蘅芜君
// 描述:   PLC 通讯适配器统一接口
// ============================================================================

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 通讯适配器统一接口。
    /// </summary>
    /// <remarks>
    /// 上层业务只依赖该接口读写检测触发、结果回写等字寄存器信号；
    /// 不同厂商协议和第三方驱动库的差异由具体适配器内部处理。
    /// </remarks>
    public interface IPlcDevice
    {
        /// <summary>
        /// 建立与 PLC 的网络连接。
        /// </summary>
        /// <returns>连接成功返回 true，失败时返回 false 并写入 <see cref="LastError"/>。</returns>
        Task<bool> ConnectAsync();

        /// <summary>
        /// 断开当前 PLC 连接并释放底层通讯资源。
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 从指定地址读取一个 16 位有符号整数。
        /// </summary>
        /// <param name="address">已按当前协议规范化后的 PLC 地址。</param>
        /// <returns>读取状态和读取到的数值；失败时 Value 为 0。</returns>
        Task<(bool Success, short Value)> ReadInt16Async(string address);

        /// <summary>
        /// 从指定地址读取连续字节。
        /// </summary>
        /// <param name="address">已按当前协议规范化后的 PLC 起始地址。</param>
        /// <param name="length">需要读取的字节数或驱动约定的读取长度。</param>
        /// <returns>读取状态和字节数组；失败时返回空数组。</returns>
        Task<(bool Success, byte[] Value)> ReadBytesAsync(string address, ushort length);

        /// <summary>
        /// 向指定地址写入一个 16 位有符号整数。
        /// </summary>
        /// <param name="address">已按当前协议规范化后的 PLC 地址。</param>
        /// <param name="value">要写入的数值。</param>
        /// <returns>写入成功返回 true，失败时返回 false 并写入 <see cref="LastError"/>。</returns>
        Task<bool> WriteInt16Async(string address, short value);

        /// <summary>
        /// 最近一次通讯或驱动调用失败时的错误信息。
        /// </summary>
        string LastError { get; }

        /// <summary>
        /// 当前适配器是否认为连接可用。
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 用于日志和前端展示的协议/驱动名称。
        /// </summary>
        string ProtocolName { get; }
    }
}


