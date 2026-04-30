// ============================================================================
// 文件名: PlcProtocolMode.cs
// 描述:   PLC 业务协议模式
// ============================================================================

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 业务握手模式。默认 Legacy，确保旧现场配置行为不变。
    /// </summary>
    public enum PlcProtocolMode
    {
        Legacy,
        HandshakeV1
    }
}
