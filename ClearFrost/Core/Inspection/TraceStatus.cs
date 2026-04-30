// ============================================================================
// 文件名: TraceStatus.cs
// 描述:   检测追溯落盘状态
// ============================================================================

namespace ClearFrost.Core.Inspection
{
    /// <summary>
    /// 追溯链路的最小闭环状态。
    /// </summary>
    public enum TraceStatus
    {
        Unknown,
        Queued,
        Full,
        Partial,
        Failed,
        Disabled
    }
}
