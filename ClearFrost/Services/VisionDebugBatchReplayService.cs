// ============================================================================
// 文件名: VisionDebugBatchReplayService.cs
// 描述:   视觉算法调试批量历史样本回放统计辅助
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using ClearFrost.Core.Rules;

namespace ClearFrost.Services
{
    internal static class VisionDebugBatchReplayService
    {
        public const int DefaultLimit = 20;
        public const int MaxLimit = 50;

        public static int ClampLimit(int? limit)
        {
            return Math.Clamp(limit.GetValueOrDefault(DefaultLimit), 1, MaxLimit);
        }

        public static bool? ParseResultFilter(string? value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(normalized, "OK", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Pass", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Qualified", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(normalized, "NG", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Fail", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Unqualified", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return null;
        }

        public static VisionDebugBatchReplaySummary BuildSummary(
            IEnumerable<VisionDebugBatchReplayItem> items,
            int requestedLimit,
            int effectiveLimit)
        {
            List<VisionDebugBatchReplayItem> itemList = (items ?? Array.Empty<VisionDebugBatchReplayItem>()).ToList();
            var completed = itemList
                .Where(item => string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                    item.OldIsQualified.HasValue &&
                    item.NewIsQualified.HasValue)
                .ToList();

            Dictionary<string, int> failureStats = itemList
                .Where(item => !string.IsNullOrWhiteSpace(item.FailureReason))
                .GroupBy(item => item.FailureReason.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            return new VisionDebugBatchReplaySummary
            {
                RequestedLimit = requestedLimit,
                EffectiveLimit = effectiveLimit,
                TotalRecords = itemList.Count,
                CompletedCount = completed.Count,
                OldOkCount = completed.Count(item => item.OldIsQualified == true),
                OldNgCount = completed.Count(item => item.OldIsQualified == false),
                NewOkCount = completed.Count(item => item.NewIsQualified == true),
                NewNgCount = completed.Count(item => item.NewIsQualified == false),
                ChangedCount = completed.Count(item => item.OldIsQualified != item.NewIsQualified),
                NgToOkCount = completed.Count(item => item.OldIsQualified == false && item.NewIsQualified == true),
                OkToNgCount = completed.Count(item => item.OldIsQualified == true && item.NewIsQualified == false),
                MissingImageCount = itemList.Count(item => item.ImageMissing),
                FailedCount = itemList.Count(item => string.Equals(item.Status, "failed", StringComparison.OrdinalIgnoreCase)),
                RenderedFallbackCount = itemList.Count(item => item.UsedRenderedImage),
                FailureReasonStats = failureStats,
                Items = itemList
            };
        }
    }
}
