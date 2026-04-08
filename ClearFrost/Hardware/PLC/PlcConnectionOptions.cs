using System;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 连接参数。
    /// </summary>
    public sealed class PlcConnectionOptions
    {
        public string Protocol { get; set; } = "Mitsubishi_MC_ASCII";

        public string DriverProvider { get; set; } = "Hsl";

        public string Ip { get; set; } = "127.0.0.1";

        public int Port { get; set; }

        public string SiemensCpuModel { get; set; } = "S1200";

        public int SiemensRack { get; set; }

        public int SiemensSlot { get; set; } = 2;

        /// <summary>
        /// 当前配置的触发地址，用于连接后的连通性探测。
        /// </summary>
        public string TriggerAddress { get; set; } = string.Empty;

        public PlcProtocolType ProtocolType => PlcFactory.ParseProtocol(Protocol);

        public bool IsMitsubishiProtocol =>
            ProtocolType == PlcProtocolType.Mitsubishi_MC_ASCII ||
            ProtocolType == PlcProtocolType.Mitsubishi_MC_Binary;

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
