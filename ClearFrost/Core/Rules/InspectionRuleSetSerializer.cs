// ============================================================================
// 文件名: InspectionRuleSetSerializer.cs
// 描述:   检测规则集序列化与旧配置迁移
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClearFrost.Core.Rules
{
    public static class InspectionRuleSetSerializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public static InspectionRuleSet DeserializeOrDefault(string? json)
        {
            return TryDeserialize(json, out InspectionRuleSet ruleSet, out _)
                ? ruleSet
                : new InspectionRuleSet();
        }

        public static bool TryDeserialize(string? json, out InspectionRuleSet ruleSet, out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                ruleSet = new InspectionRuleSet();
                errorMessage = string.Empty;
                return true;
            }

            try
            {
                ruleSet = JsonSerializer.Deserialize<InspectionRuleSet>(json, JsonOptions) ?? new InspectionRuleSet();
                Normalize(ruleSet);
                errorMessage = string.Empty;
                return true;
            }
            catch (JsonException ex)
            {
                ruleSet = new InspectionRuleSet();
                errorMessage = ex.Message;
                return false;
            }
        }

        public static string Serialize(InspectionRuleSet? ruleSet)
        {
            var normalized = ruleSet ?? new InspectionRuleSet();
            Normalize(normalized);
            return JsonSerializer.Serialize(normalized, JsonOptions);
        }

        public static InspectionRuleSet FromLegacyTarget(string? targetLabel, int targetCount)
        {
            var ruleSet = new InspectionRuleSet();
            string label = targetLabel?.Trim() ?? string.Empty;
            int count = Math.Max(0, targetCount);
            ruleSet.FallbackTargetLabel = label;
            ruleSet.FallbackTargetCount = count;
            if (string.IsNullOrWhiteSpace(label))
            {
                ruleSet.Rules.Add(new InspectionRule
                {
                    Name = "默认无目标",
                    Type = InspectionRuleTypes.Count,
                    Label = string.Empty,
                    Operator = InspectionRuleOperators.Equal,
                    Count = 0
                });
                return ruleSet;
            }

            ruleSet.Rules.Add(new InspectionRule
            {
                Name = $"{label} 数量",
                Type = InspectionRuleTypes.Count,
                Label = label,
                Operator = InspectionRuleOperators.Equal,
                Count = count
            });
            return ruleSet;
        }

        public static InspectionRuleSet FromLegacyWireSequence(
            string? expectedLabels,
            string? sortBy,
            string? direction,
            int expectedCount,
            double minConfidence,
            bool allowMissing,
            bool allowDuplicate,
            string? fallbackTargetLabel = null,
            int fallbackTargetCount = 0)
        {
            List<string> labels = ParseLabels(expectedLabels);
            var ruleSet = new InspectionRuleSet
            {
                FallbackTargetLabel = fallbackTargetLabel?.Trim() ?? string.Empty,
                FallbackTargetCount = Math.Max(0, fallbackTargetCount)
            };
            ruleSet.Rules.Add(new InspectionRule
            {
                Name = "端子线序",
                Type = InspectionRuleTypes.OrderedLabels,
                ExpectedLabels = labels,
                SortBy = string.IsNullOrWhiteSpace(sortBy) ? "CenterX" : sortBy.Trim(),
                Direction = string.IsNullOrWhiteSpace(direction) ? "LeftToRight" : direction.Trim(),
                ExpectedCount = Math.Clamp(expectedCount, 0, 256),
                MinConfidence = Math.Clamp(minConfidence, 0.0, 1.0),
                AllowMissing = allowMissing,
                AllowDuplicate = allowDuplicate
            });
            return ruleSet;
        }

        public static void Normalize(InspectionRuleSet ruleSet)
        {
            ruleSet.Version = ruleSet.Version <= 0 ? 1 : ruleSet.Version;
            ruleSet.Mode = "All";
            ruleSet.Rules ??= new List<InspectionRule>();
            ruleSet.Rules = ruleSet.Rules.Where(rule => rule != null).ToList();
            ruleSet.FallbackTargetLabel = ruleSet.FallbackTargetLabel?.Trim() ?? string.Empty;
            ruleSet.FallbackTargetCount = Math.Max(0, ruleSet.FallbackTargetCount);

            foreach (InspectionRule rule in ruleSet.Rules)
            {
                rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim();
                rule.Type = NormalizeType(rule.Type);
                rule.Name = rule.Name?.Trim() ?? string.Empty;
                rule.Label = rule.Label?.Trim() ?? string.Empty;
                rule.Operator = NormalizeOperator(rule.Operator);
                rule.Count = Math.Max(0, rule.Count);
                rule.MinConfidence = Math.Clamp(rule.MinConfidence, 0.0, 1.0);
                rule.ExpectedLabel = rule.ExpectedLabel?.Trim() ?? string.Empty;
                rule.AllowedLabels = ParseLabels(rule.AllowedLabels);
                rule.MinArea = Math.Max(0, rule.MinArea);
                rule.MaxArea = Math.Max(0, rule.MaxArea);
                rule.MinCoverage = Math.Clamp(rule.MinCoverage, 0.0, 1.0);
                rule.MaxCoverage = Math.Clamp(rule.MaxCoverage, 0.0, 1.0);
                rule.MinAngle = ClampAngle(rule.MinAngle);
                rule.MaxAngle = ClampAngle(rule.MaxAngle);
                rule.MinKeyPointConfidence = Math.Clamp(rule.MinKeyPointConfidence, 0.0, 1.0);
                rule.ExpectedLabels = ParseLabels(rule.ExpectedLabels);
                rule.SortBy = string.IsNullOrWhiteSpace(rule.SortBy) ? "CenterX" : rule.SortBy.Trim();
                rule.Direction = string.IsNullOrWhiteSpace(rule.Direction) ? "LeftToRight" : rule.Direction.Trim();
                rule.ExpectedCount = Math.Clamp(rule.ExpectedCount, 0, 256);
                rule.SubjectLabel = rule.SubjectLabel?.Trim() ?? string.Empty;
                rule.ReferenceLabel = rule.ReferenceLabel?.Trim() ?? string.Empty;
                rule.Relation = NormalizeRelation(rule.Relation);
                rule.MinDistance = Math.Max(0, rule.MinDistance);
                rule.MaxDistance = Math.Max(0, rule.MaxDistance);
            }
        }

        private static List<string> ParseLabels(string? labels)
        {
            return (labels ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
        }

        private static List<string> ParseLabels(IEnumerable<string>? labels)
        {
            return (labels ?? Array.Empty<string>())
                .Select(label => label?.Trim() ?? string.Empty)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
        }

        private static string NormalizeType(string? value)
        {
            if (string.Equals(value, InspectionRuleTypes.OrderedLabels, StringComparison.OrdinalIgnoreCase)) return InspectionRuleTypes.OrderedLabels;
            if (string.Equals(value, InspectionRuleTypes.RelativePosition, StringComparison.OrdinalIgnoreCase)) return InspectionRuleTypes.RelativePosition;
            if (string.Equals(value, InspectionRuleTypes.Classification, StringComparison.OrdinalIgnoreCase)) return InspectionRuleTypes.Classification;
            if (string.Equals(value, InspectionRuleTypes.SegmentationArea, StringComparison.OrdinalIgnoreCase)) return InspectionRuleTypes.SegmentationArea;
            if (string.Equals(value, InspectionRuleTypes.ObbAngle, StringComparison.OrdinalIgnoreCase)) return InspectionRuleTypes.ObbAngle;
            if (string.Equals(value, InspectionRuleTypes.PoseKeypoints, StringComparison.OrdinalIgnoreCase)) return InspectionRuleTypes.PoseKeypoints;
            return InspectionRuleTypes.Count;
        }

        private static string NormalizeOperator(string? value)
        {
            if (string.Equals(value, InspectionRuleOperators.NotEqual, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.NotEqual;
            if (string.Equals(value, InspectionRuleOperators.GreaterThan, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.GreaterThan;
            if (string.Equals(value, InspectionRuleOperators.GreaterThanOrEqual, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.GreaterThanOrEqual;
            if (string.Equals(value, InspectionRuleOperators.LessThan, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.LessThan;
            if (string.Equals(value, InspectionRuleOperators.LessThanOrEqual, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.LessThanOrEqual;
            return InspectionRuleOperators.Equal;
        }

        private static string NormalizeRelation(string? value)
        {
            if (string.Equals(value, InspectionRuleRelations.RightOf, StringComparison.OrdinalIgnoreCase)) return InspectionRuleRelations.RightOf;
            if (string.Equals(value, InspectionRuleRelations.Above, StringComparison.OrdinalIgnoreCase)) return InspectionRuleRelations.Above;
            if (string.Equals(value, InspectionRuleRelations.Below, StringComparison.OrdinalIgnoreCase)) return InspectionRuleRelations.Below;
            return InspectionRuleRelations.LeftOf;
        }

        private static double ClampAngle(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }

            return Math.Clamp(value, -360.0, 360.0);
        }
    }
}
