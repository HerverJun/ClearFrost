using System;

// ============================================================================
// 文件名: PlcConnectionOptions.cs
// 作者: 蘅芜君
// 描述:   PLC 连接参数模型
// ============================================================================

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 连接参数。
    /// </summary>
    public sealed class PlcConnectionOptions
    {
        /// <summary>
        /// 协议名称，保存为字符串是为了兼容旧版配置文件。
        /// </summary>
        public string Protocol { get; set; } = "Mitsubishi_MC_ASCII";

        /// <summary>
        /// 驱动提供方，例如 Hsl、McpX 或 HaoCommunication。
        /// </summary>
        public string DriverProvider { get; set; } = "Hsl";

        /// <summary>
        /// PLC IP 地址。
        /// </summary>
        public string Ip { get; set; } = "127.0.0.1";

        /// <summary>
        /// PLC 通讯端口；各协议默认端口由界面或配置层写入。
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 西门子 CPU 型号，用于选择 S7Net 的底层 PLC 类型。
        /// </summary>
        public string SiemensCpuModel { get; set; } = "S1200";

        /// <summary>
        /// 西门子 S300/S400 机架号。
        /// </summary>
        public int SiemensRack { get; set; }

        /// <summary>
        /// 西门子 S300/S400 槽位号。
        /// </summary>
        public int SiemensSlot { get; set; } = 2;

        /// <summary>
        /// 当前配置的触发地址，用于连接后的连通性探测。
        /// </summary>
        public string TriggerAddress { get; set; } = string.Empty;

        /// <summary>
        /// 将字符串协议解析为强类型枚举。
        /// </summary>
        public PlcProtocolType ProtocolType => PlcFactory.ParseProtocol(Protocol);

        /// <summary>
        /// 当前协议是否属于三菱 MC 系列。
        /// </summary>
        public bool IsMitsubishiProtocol =>
            ProtocolType == PlcProtocolType.Mitsubishi_MC_ASCII ||
            ProtocolType == PlcProtocolType.Mitsubishi_MC_Binary;

        /// <summary>
        /// 创建配置副本，避免 UI 临时编辑直接污染运行中的连接配置。
        /// </summary>
        public PlcConnectionOptions Clone()
        {
            return new PlcConnectionOptions
            {
                Protocol = Protocol,
                DriverProvider = DriverProvider,
                Ip = Ip,
                Port = Port,
                SiemensCpuModel = SiemensCpuModel,
                SiemensRack = SiemensRack,
                SiemensSlot = SiemensSlot,
                TriggerAddress = TriggerAddress
            };
        }
    }
}
