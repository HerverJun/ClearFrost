// ============================================================================
// 文件名: WireSequenceJudgeService.cs
// 描述:   端子线序判定服务
//
// 功能:
//   - 参照 ClearVision DetectionSequenceJudge 的单行排序与顺序比对逻辑
//   - 支持置信度过滤、缺失/重复检测、数量校验和顺序诊断
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using ClearFrost.Config;
using ClearFrost.Yolo;

namespace ClearFrost.Services
{
    public sealed class WireSequenceJudgeResult
    {
        public bool IsMatch { get; init; }
        public IReadOnlyList<string> ExpectedLabels { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ActualOrder { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MissingLabels { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> DuplicateLabels { get; init; } = Array.Empty<string>();
        public int ReceivedCount { get; init; }
        public int FilteredCount { get; init; }
        public int SortedCount { get; init; }
        public int ExpectedCount { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public static class WireSequenceJudgeService
    {
        public static WireSequenceJudgeResult Evaluate(
            IEnumerable<YoloResult> detections,
            IReadOnlyList<string> labels,
            AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            List<YoloResult> rawDetections = detections?.ToList() ?? new List<YoloResult>();
            List<string> expectedLabels = ParseLabels(config.WireSequenceExpectedLabels);
            double minConfidence = Math.Clamp(config.WireSequenceMinConfidence, 0.0, 1.0);

            List<YoloResult> filteredDetections = rawDetections
                .Where(detection => detection.Confidence >= minConfidence)
                .ToList();
            var expectedLabelSet = new HashSet<string>(expectedLabels, StringComparer.OrdinalIgnoreCase);
            IEnumerable<YoloResult> sequenceCandidates = filteredDetections;
            if (expectedLabelSet.Count > 0)
            {
                sequenceCandidates = sequenceCandidates
                    .Where(detection => expectedLabelSet.Contains(ResolveLabel(detection, labels)));
            }

            List<YoloResult> orderedDetections = SortDetections(
                sequenceCandidates,
                config.WireSequenceSortBy,
                config.WireSequenceDirection);

            List<string> actualOrder = orderedDetections
                .Select(detection => ResolveLabel(detection, labels))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();

            int expectedCount = config.WireSequenceExpectedCount > 0
                ? config.WireSequenceExpectedCount
                : expectedLabels.Count;
            List<string> missingLabels = ComputeMissingLabels(expectedLabels, actualOrder);
            List<string> duplicateLabels = actualOrder
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            var reasons = new List<string>();
            if (expectedLabels.Count == 0)
            {
                reasons.Add("Expected labels are not configured.");
            }

            if (rawDetections.Count == 0)
            {
                reasons.Add("No detections received.");
            }
            else if (filteredDetections.Count == 0)
            {
                reasons.Add("All detections were filtered by MinConfidence.");
            }

            if (!config.WireSequenceAllowMissing && missingLabels.Count > 0)
            {
                reasons.Add($"Missing labels: {string.Join(", ", missingLabels)}.");
            }

            if (!config.WireSequenceAllowDuplicate && duplicateLabels.Count > 0)
            {
                reasons.Add($"Duplicate labels: {string.Join(", ", duplicateLabels)}.");
            }

            if (expectedCount > 0 &&
                IsCountMismatch(orderedDetections.Count, expectedCount, config.WireSequenceAllowMissing, config.WireSequenceAllowDuplicate))
            {
                reasons.Add($"Expected {expectedCount} detections but got {orderedDetections.Count}.");
            }

            if (expectedLabels.Count > 0 &&
                !MatchesExpectedOrder(expectedLabels, actualOrder, config.WireSequenceAllowMissing, config.WireSequenceAllowDuplicate))
            {
                reasons.Add($"Order mismatch. Actual: {FormatLabels(actualOrder)}.");
            }

            bool isMatch = reasons.Count == 0;
            string message = isMatch
                ? $"Sequence matched: {FormatLabels(actualOrder)}."
                : string.Join(" ", reasons);

            return new WireSequenceJudgeResult
            {
                IsMatch = isMatch,
                ExpectedLabels = expectedLabels,
                ActualOrder = actualOrder,
                MissingLabels = missingLabels,
                DuplicateLabels = duplicateLabels,
                ReceivedCount = rawDetections.Count,
                FilteredCount = filteredDetections.Count,
                SortedCount = orderedDetections.Count,
                ExpectedCount = expectedCount,
                Message = message
            };
        }

        public static List<YoloResult> SortDetections(
            IEnumerable<YoloResult> detections,
            string? sortBy,
            string? direction)
        {
            string normalizedSortBy = NormalizeSortBy(sortBy, direction);
            bool descending = IsDescending(direction);
            IEnumerable<YoloResult> ordered = normalizedSortBy switch
            {
                "TopY" => descending
                    ? detections.OrderByDescending(d => d.Top).ThenBy(d => d.CenterX)
                    : detections.OrderBy(d => d.Top).ThenBy(d => d.CenterX),
                "CenterY" => descending
                    ? detections.OrderByDescending(d => d.CenterY).ThenBy(d => d.CenterX)
                    : detections.OrderBy(d => d.CenterY).ThenBy(d => d.CenterX),
                "Confidence" => descending
                    ? detections.OrderByDescending(d => d.Confidence).ThenBy(d => d.CenterX)
                    : detections.OrderBy(d => d.Confidence).ThenBy(d => d.CenterX),
                "Area" => descending
                    ? detections.OrderByDescending(d => d.Area).ThenBy(d => d.CenterX)
                    : detections.OrderBy(d => d.Area).ThenBy(d => d.CenterX),
                _ => descending
                    ? detections.OrderByDescending(d => d.CenterX).ThenBy(d => d.CenterY)
                    : detections.OrderBy(d => d.CenterX).ThenBy(d => d.CenterY)
            };

            return ordered.ToList();
        }

        private static string ResolveLabel(YoloResult detection, IReadOnlyList<string> labels)
        {
            if (detection.ClassId >= 0 && detection.ClassId < labels.Count)
            {
                return labels[detection.ClassId];
            }

            return $"Class_{detection.ClassId}";
        }

        private static string NormalizeSortBy(string? sortBy, string? direction)
        {
            if (string.Equals(direction, "TopToBottom", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(direction, "BottomToTop", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(sortBy, "TopY", StringComparison.OrdinalIgnoreCase)
                    ? "TopY"
                    : "CenterY";
            }

            if (string.Equals(sortBy, "CenterY", StringComparison.OrdinalIgnoreCase)) return "CenterY";
            if (string.Equals(sortBy, "TopY", StringComparison.OrdinalIgnoreCase)) return "TopY";
            if (string.Equals(sortBy, "Confidence", StringComparison.OrdinalIgnoreCase)) return "Confidence";
            if (string.Equals(sortBy, "Area", StringComparison.OrdinalIgnoreCase)) return "Area";
            return "CenterX";
        }

        private static bool IsDescending(string? direction)
        {
            return string.Equals(direction, "Descending", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(direction, "RightToLeft", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(direction, "BottomToTop", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ParseLabels(string rawLabels)
        {
            return (rawLabels ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
        }

        private static List<string> ComputeMissingLabels(IReadOnlyList<string> expectedLabels, IReadOnlyList<string> actualOrder)
        {
            var counts = actualOrder
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();
            foreach (string label in expectedLabels)
            {
                if (!counts.TryGetValue(label, out int count) || count == 0)
                {
                    missing.Add(label);
                    continue;
                }

                counts[label] = count - 1;
            }

            return missing;
        }

        private static bool IsCountMismatch(int actualCount, int expectedCount, bool allowMissing, bool allowDuplicate)
        {
            return (actualCount < expectedCount && !allowMissing) ||
                   (actualCount > expectedCount && !allowDuplicate);
        }

        private static bool MatchesExpectedOrder(
            IReadOnlyList<string> expectedLabels,
            IReadOnlyList<string> actualOrder,
            bool allowMissing,
            bool allowDuplicate)
        {
            if (!allowMissing && !allowDuplicate)
            {
                return expectedLabels.SequenceEqual(actualOrder, StringComparer.OrdinalIgnoreCase);
            }

            int expectedIndex = 0;
            foreach (string actualLabel in actualOrder)
            {
                if (allowDuplicate && ContainsLabel(expectedLabels, actualLabel, expectedIndex))
                {
                    continue;
                }

                bool matched = false;
                while (expectedIndex < expectedLabels.Count)
                {
                    if (string.Equals(expectedLabels[expectedIndex], actualLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        expectedIndex++;
                        break;
                    }

                    if (!allowMissing)
                    {
                        return false;
                    }

                    expectedIndex++;
                }

                if (!matched)
                {
                    return false;
                }
            }

            return allowMissing || expectedIndex >= expectedLabels.Count;
        }

        private static bool ContainsLabel(IReadOnlyList<string> labels, string label, int endExclusive)
        {
            for (int i = 0; i < Math.Min(endExclusive, labels.Count); i++)
            {
                if (string.Equals(labels[i], label, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatLabels(IReadOnlyCollection<string> labels)
        {
            return labels.Count == 0 ? "<empty>" : string.Join(" -> ", labels);
        }
    }
}
