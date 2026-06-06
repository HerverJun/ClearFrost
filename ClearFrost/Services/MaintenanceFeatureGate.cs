// ============================================================================
// 文件名: MaintenanceFeatureGate.cs
// 描述:   核心收敛版维护入口开关与拒绝审计契约
// ============================================================================

using System;

namespace ClearFrost.Services
{
    internal enum MaintenanceFeature
    {
        ModelPackageImport,
        ConfigVersionRestore,
        AlarmAcknowledge,
        AlarmAcknowledgeAll
    }

    internal sealed class MaintenanceFeatureDecision
    {
        public bool Allowed { get; init; }
        public string AuditCategory { get; init; } = string.Empty;
        public string AuditAction { get; init; } = string.Empty;
        public string AuditDetail { get; init; } = string.Empty;
        public string UserMessage { get; init; } = string.Empty;
        public string LogMessage { get; init; } = string.Empty;
    }

    internal static class MaintenanceFeatureGate
    {
        public static bool ModelPackageImportEnabled => false;
        public static bool ConfigVersionRestoreEnabled => false;
        public static bool AlarmAcknowledgementWorkflowEnabled => false;

        public static MaintenanceFeatureDecision Evaluate(MaintenanceFeature feature, string? targetId = null)
        {
            return feature switch
            {
                MaintenanceFeature.ModelPackageImport when !ModelPackageImportEnabled => Denied(
                    "Model",
                    "ImportModelPackageBlocked",
                    "Reason=ImportUiDisabled",
                    "模型包导入入口已从核心收敛版本隐藏；请走单独维护流程评审。",
                    "模型包导入入口已隐藏，当前未执行导入"),

                MaintenanceFeature.ConfigVersionRestore when !ConfigVersionRestoreEnabled => Denied(
                    "ConfigChange",
                    "RestoreConfigVersionBlocked",
                    $"VersionId={NormalizeAuditValue(targetId)}; Reason=RestoreDisabled",
                    "配置版本恢复默认关闭；当前仅支持保存和查看版本记录。",
                    "配置版本恢复默认关闭，已阻止恢复请求"),

                MaintenanceFeature.AlarmAcknowledge when !AlarmAcknowledgementWorkflowEnabled => Denied(
                    "Alarm",
                    "AcknowledgeBlocked",
                    $"AlarmId={NormalizeAuditValue(targetId)}; Reason=AcknowledgementWorkflowDisabled",
                    "告警确认工作流暂未启用；请按健康摘要现场处理。",
                    "告警确认工作流暂未启用，已阻止确认请求"),

                MaintenanceFeature.AlarmAcknowledgeAll when !AlarmAcknowledgementWorkflowEnabled => Denied(
                    "Alarm",
                    "AcknowledgeAllBlocked",
                    "Reason=AcknowledgementWorkflowDisabled",
                    "告警确认工作流暂未启用；请按健康摘要现场处理。",
                    "告警确认工作流暂未启用，已阻止全部确认请求"),

                _ => new MaintenanceFeatureDecision { Allowed = true }
            };
        }

        private static MaintenanceFeatureDecision Denied(
            string category,
            string action,
            string detail,
            string userMessage,
            string logMessage)
        {
            return new MaintenanceFeatureDecision
            {
                Allowed = false,
                AuditCategory = category,
                AuditAction = action,
                AuditDetail = detail,
                UserMessage = userMessage,
                LogMessage = logMessage
            };
        }

        private static string NormalizeAuditValue(string? value)
        {
            string normalized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Trim();
            return string.IsNullOrWhiteSpace(normalized) ? "-" : normalized;
        }
    }
}
