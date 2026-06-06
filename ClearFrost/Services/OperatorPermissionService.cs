// ============================================================================
// 文件名: OperatorPermissionService.cs
// 描述:   操作员角色权限校验
// ============================================================================

using System;

namespace ClearFrost.Services
{
    public enum OperatorPermission
    {
        BasicOperation,
        RunManualInspection,
        ManualRelease,
        OperateProductionHardware,
        ChangeInspectionParameters,
        ChangeModel,
        ImportModelPackage,
        ManageSettings,
        ManageCamera,
        ManageProjectPreset,
        ManageStatistics,
        ManageStorage,
        ImportConfiguration,
        ExportDiagnostics
    }

    public sealed class OperatorPermissionDecision
    {
        public bool Allowed { get; init; }
        public string Operation { get; init; } = string.Empty;
        public string RequiredRole { get; init; } = string.Empty;
        public string OperatorName { get; init; } = string.Empty;
        public string OperatorRole { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    public static class OperatorPermissionService
    {
        public const string RoleOperator = "Operator";
        public const string RoleTechnician = "Technician";
        public const string RoleEngineer = "Engineer";
        public const string RoleAdministrator = "Administrator";

        public static OperatorPermissionDecision Authorize(
            OperatorSession? session,
            OperatorPermission permission,
            string operation)
        {
            string requiredRole = GetRequiredRole(permission);
            string operatorName = session?.OperatorName ?? OperatorSession.DefaultOperatorName;
            string operatorRole = NormalizeRole(session?.Role);

            if (!IsEnforcedPermission(permission))
            {
                return new OperatorPermissionDecision
                {
                    Allowed = true,
                    Operation = operation,
                    RequiredRole = RoleOperator,
                    OperatorName = operatorName,
                    OperatorRole = operatorRole,
                    Message = $"{operation} 已记录操作员身份，不作为权限边界拦截"
                };
            }

            if (session?.IsSignedIn != true)
            {
                return Denied(operation, requiredRole, operatorName, operatorRole, "请先登录操作员");
            }

            if (GetRoleRank(operatorRole) < GetRoleRank(requiredRole))
            {
                return Denied(
                    operation,
                    requiredRole,
                    operatorName,
                    operatorRole,
                    $"{operation} 需要 {requiredRole} 或更高权限，当前角色为 {operatorRole}");
            }

            return new OperatorPermissionDecision
            {
                Allowed = true,
                Operation = operation,
                RequiredRole = requiredRole,
                OperatorName = operatorName,
                OperatorRole = operatorRole,
                Message = $"{operation} 权限校验通过"
            };
        }

        public static OperatorPermissionDecision AuthorizeRoleGrant(
            OperatorSession? session,
            string requestedRole,
            bool isTrustedSystemPrincipal,
            string operation)
        {
            string normalizedRequestedRole = NormalizeRole(requestedRole);
            string operatorName = session?.OperatorName ?? OperatorSession.DefaultOperatorName;
            string operatorRole = NormalizeRole(session?.Role);

            if (GetRoleRank(normalizedRequestedRole) <= GetRoleRank(RoleOperator))
            {
                return new OperatorPermissionDecision
                {
                    Allowed = true,
                    Operation = operation,
                    RequiredRole = RoleOperator,
                    OperatorName = operatorName,
                    OperatorRole = operatorRole,
                    Message = $"{operation} 权限校验通过"
                };
            }

            if (isTrustedSystemPrincipal ||
                session?.IsSignedIn == true &&
                GetRoleRank(operatorRole) >= GetRoleRank(normalizedRequestedRole))
            {
                return new OperatorPermissionDecision
                {
                    Allowed = true,
                    Operation = operation,
                    RequiredRole = normalizedRequestedRole,
                    OperatorName = operatorName,
                    OperatorRole = operatorRole,
                    Message = $"{operation} 权限校验通过"
                };
            }

            return Denied(
                operation,
                normalizedRequestedRole,
                operatorName,
                operatorRole,
                $"{operation} 到 {normalizedRequestedRole} 需要当前 {normalizedRequestedRole} 或更高权限确认");
        }

        public static string NormalizeRole(string? role)
        {
            string value = (role ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return RoleOperator;
            }

            string lower = value.ToLowerInvariant();
            return lower switch
            {
                "admin" or "administrator" or "管理员" => RoleAdministrator,
                "engineer" or "engineering" or "工程师" => RoleEngineer,
                "technician" or "tech" or "maintenance" or "维护" or "技术员" => RoleTechnician,
                "operator" or "操作员" => RoleOperator,
                _ => value
            };
        }

        public static int GetRoleRank(string? role)
        {
            return NormalizeRole(role) switch
            {
                RoleAdministrator => 3,
                RoleEngineer => 2,
                RoleTechnician => 1,
                _ => 0
            };
        }

        public static string GetRequiredRole(OperatorPermission permission)
        {
            return permission switch
            {
                OperatorPermission.ManualRelease => RoleTechnician,
                OperatorPermission.ImportModelPackage => RoleEngineer,
                OperatorPermission.ImportConfiguration => RoleEngineer,
                _ => RoleOperator
            };
        }

        public static bool IsEnforcedPermission(OperatorPermission permission)
        {
            return permission is
                OperatorPermission.ManualRelease or
                OperatorPermission.ImportModelPackage or
                OperatorPermission.ImportConfiguration;
        }

        private static OperatorPermissionDecision Denied(
            string operation,
            string requiredRole,
            string operatorName,
            string operatorRole,
            string message)
        {
            return new OperatorPermissionDecision
            {
                Allowed = false,
                Operation = operation,
                RequiredRole = requiredRole,
                OperatorName = operatorName,
                OperatorRole = operatorRole,
                Message = message
            };
        }
    }
}
