// ============================================================================
// 文件名: ManualReleaseRequest.cs
// 描述:   手动放行命令解析与权限校验
// ============================================================================

using System;
using System.Text.Json;
using ClearFrost.Core.Security;

namespace ClearFrost.Services
{
    internal sealed class ManualReleaseRequest
    {
        public const string RequiredConfirmationToken = "CONFIRM_MANUAL_RELEASE";

        public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
        public string OperatorId { get; init; } = string.Empty;
        public ProductionRole Role { get; init; } = ProductionRole.Operator;
        public string Reason { get; init; } = string.Empty;
        public string ConfirmationToken { get; init; } = string.Empty;
        public string InspectionId { get; init; } = string.Empty;

        public static ManualReleaseRequest Parse(
            string? json,
            string defaultOperatorId,
            ProductionRole defaultRole)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ManualReleaseRequest
                {
                    OperatorId = defaultOperatorId,
                    Role = defaultRole
                };
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                return new ManualReleaseRequest
                {
                    RequestId = GetString(root, "requestId", Guid.NewGuid().ToString("N")),
                    OperatorId = defaultOperatorId,
                    Role = defaultRole,
                    Reason = GetString(root, "reason", string.Empty),
                    ConfirmationToken = GetString(root, "confirmationToken", GetString(root, "confirmToken", string.Empty)),
                    InspectionId = GetString(root, "inspectionId", string.Empty)
                };
            }
            catch
            {
                return new ManualReleaseRequest
                {
                    OperatorId = defaultOperatorId,
                    Role = defaultRole
                };
            }
        }

        public bool TryAuthorize(out string denialReason)
        {
            if (!ProductionAuthorizationService.Authorize(Role, ProductionOperation.ManualRelease, out denialReason))
            {
                return false;
            }

            if (!string.Equals(ConfirmationToken, RequiredConfirmationToken, StringComparison.Ordinal))
            {
                denialReason = "手动放行缺少有效确认令牌";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Reason) || Reason.Trim().Length < 6)
            {
                denialReason = "手动放行必须记录不少于 6 个字符的原因";
                return false;
            }

            denialReason = string.Empty;
            return true;
        }

        private static string GetString(JsonElement root, string name, string fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
            {
                return fallback;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? fallback
                : value.ToString()?.Trim() ?? fallback;
        }

    }
}
