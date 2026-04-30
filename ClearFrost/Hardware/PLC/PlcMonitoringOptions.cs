// ============================================================================
// 文件名: PlcMonitoringOptions.cs
// 描述:   PLC 监听业务选项
// ============================================================================

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 触发监听的业务协议选项。
    /// </summary>
    public sealed class PlcMonitoringOptions
    {
        public PlcProtocolMode ProtocolMode { get; init; } = PlcProtocolMode.Legacy;
        public string TriggerSeqAddress { get; init; } = string.Empty;
        public bool EnableBarcodeReading { get; init; }
        public string BarcodeAddress { get; init; } = string.Empty;
        public int BarcodeLength { get; init; } = 13;
        public string BarcodeEncoding { get; init; } = "ASCII";
        public bool BarcodeRequired { get; init; } = true;
    }
}
