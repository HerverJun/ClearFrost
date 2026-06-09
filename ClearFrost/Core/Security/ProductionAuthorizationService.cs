// ============================================================================
// 文件名: ProductionAuthorizationService.cs
// 描述:   生产操作权限判定
// ============================================================================

namespace ClearFrost.Core.Security
{
    /// <summary>
    /// 统一生产权限判定，避免信任前端自报角色。
    /// </summary>
    public static class ProductionAuthorizationService
    {
        public static ProductionRole GetRequiredRole(ProductionOperation operation)
        {
            return operation switch
            {
                ProductionOperation.ManualRelease => ProductionRole.ShiftLead,
                ProductionOperation.EngineeringChange => ProductionRole.Engineer,
                _ => ProductionRole.Operator
            };
        }

        public static bool Authorize(
            ProductionRole currentRole,
            ProductionOperation operation,
            out string denialReason)
        {
            ProductionRole requiredRole = GetRequiredRole(operation);
            if (currentRole < requiredRole)
            {
                denialReason = $"当前角色 {GetDisplayName(currentRole)} 无权限执行该操作，至少需要 {GetDisplayName(requiredRole)}";
                return false;
            }

            denialReason = string.Empty;
            return true;
        }

        public static string GetDisplayName(ProductionRole role)
        {
            return role switch
            {
                ProductionRole.Engineer => "工程师",
                ProductionRole.ShiftLead => "班组长",
                _ => "操作员"
            };
        }
    }
}
