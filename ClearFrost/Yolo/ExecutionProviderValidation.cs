// ============================================================================
// 文件名: ExecutionProviderValidation.cs
// 描述:   严格执行提供程序验证合同
// ============================================================================

using System;

namespace ClearFrost.Yolo
{
    /// <summary>
    /// requested provider 与实际推理 provider 的验证结果。
    /// </summary>
    public sealed class ExecutionProviderValidationResult
    {
        public string RequestedProvider { get; init; } = string.Empty;

        public string ActualProvider { get; init; } = string.Empty;

        public string Status { get; init; } = "BLOCKED";

        public string FailureReason { get; init; } = string.Empty;

        public bool IsSatisfied => string.Equals(Status, "PASS", StringComparison.Ordinal);
    }

    /// <summary>
    /// 只比较实际 provider，不把 CPU 回退解释成 DirectML 成功。
    /// </summary>
    public static class ExecutionProviderValidation
    {
        public static ExecutionProviderValidationResult Validate(string requestedProvider, string? actualProvider)
        {
            string requested = requestedProvider?.Trim() ?? string.Empty;
            string actual = actualProvider?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(requested))
            {
                throw new ArgumentException("requested provider 不能为空", nameof(requestedProvider));
            }

            if (string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase))
            {
                return new ExecutionProviderValidationResult
                {
                    RequestedProvider = requested,
                    ActualProvider = actual,
                    Status = "PASS"
                };
            }

            string reason = string.IsNullOrWhiteSpace(actual)
                ? $"requested provider '{requested}' produced no actual provider evidence"
                : $"requested provider '{requested}' but actual provider was '{actual}'";

            return new ExecutionProviderValidationResult
            {
                RequestedProvider = requested,
                ActualProvider = actual,
                Status = "BLOCKED",
                FailureReason = reason
            };
        }
    }
}
