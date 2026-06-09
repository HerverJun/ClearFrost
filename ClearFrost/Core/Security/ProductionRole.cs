// ============================================================================
// 文件名: ProductionRole.cs
// 描述:   生产操作权限角色
// ============================================================================

namespace ClearFrost.Core.Security
{
    /// <summary>
    /// 现场操作角色。数值越大权限越高。
    /// </summary>
    public enum ProductionRole
    {
        Operator = 0,
        ShiftLead = 1,
        Engineer = 2
    }
}
