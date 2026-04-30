// ============================================================================
// 文件名: PlcTriggerContext.cs
// 描述:   PLC 触发上下文
// ============================================================================

using System;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 触发事件携带的上下文。
    /// </summary>
    public sealed class PlcTriggerContext
    {
        public string TriggerSource { get; init; } = "PLC";
        public int? TriggerSeq { get; init; }
        public DateTimeOffset TriggerTime { get; init; } = DateTimeOffset.Now;
        public string ProductBarcode { get; init; } = string.Empty;
        public bool BarcodeReadSucceeded { get; init; } = true;
        public string BarcodeError { get; init; } = string.Empty;
    }
}
