// ============================================================================
// 文件名: InspectionStage.cs
// 描述:   检测追溯阶段枚举
// ============================================================================

namespace ClearFrost.Core.Inspection
{
    /// <summary>
    /// 单次检测在主流程中的当前阶段。
    /// </summary>
    public enum InspectionStage
    {
        Unknown,
        Triggered,
        Barcode,
        Capture,
        Inference,
        RoiFilter,
        PlcWrite,
        RenderToUi,
        SaveImage,
        SaveRecord,
        Completed,
        Failed
    }
}
