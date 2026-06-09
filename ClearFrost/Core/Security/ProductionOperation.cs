// ============================================================================
// 文件名: ProductionOperation.cs
// 描述:   生产操作权限点
// ============================================================================

namespace ClearFrost.Core.Security
{
    /// <summary>
    /// 需要后端权限判定的生产操作。
    /// </summary>
    public enum ProductionOperation
    {
        RunInspection = 0,
        ManualRelease = 1,
        EngineeringChange = 2
    }
}
