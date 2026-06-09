// ============================================================================
// 文件名: InspectionRuleModels.cs
// 描述:   检测判定规则模型
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ClearFrost.Core.Rules
{
    public static class InspectionRuleTypes
    {
        public const string Count = "Count";
        public const string OrderedLabels = "OrderedLabels";
        public const string RelativePosition = "RelativePosition";
    }

    public static class InspectionRuleOperators
    {
        public const string Equal = "Equal";
        public const string NotEqual = "NotEqual";
        public const string GreaterThan = "GreaterThan";
        public const string GreaterThanOrEqual = "GreaterThanOrEqual";
        public const string LessThan = "LessThan";
        public const string LessThanOrEqual = "LessThanOrEqual";
    }

    public static class InspectionRuleRelations
    {
        public const string LeftOf = "LeftOf";
        public const string RightOf = "RightOf";
        public const string Above = "Above";
        public const string Below = "Below";
    }

    public sealed class InspectionRuleSet
    {
        public int Version { get; set; } = 1;
        public string Mode { get; set; } = "All";
        public string FallbackTargetLabel { get; set; } = string.Empty;
        public int FallbackTargetCount { get; set; }
        public List<InspectionRule> Rules { get; set; } = new List<InspectionRule>();

        [JsonIgnore]
        public IReadOnlyList<InspectionRule> EnabledRules =>
            (Rules ?? new List<InspectionRule>())
                .Where(rule => rule.Enabled)
                .ToList();
    }

    public sealed class InspectionRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string Type { get; set; } = InspectionRuleTypes.Count;

        public string Label { get; set; } = string.Empty;
        public string Operator { get; set; } = InspectionRuleOperators.Equal;
        public int Count { get; set; }
        public double MinConfidence { get; set; }

        public List<string> ExpectedLabels { get; set; } = new List<string>();
        public string SortBy { get; set; } = "CenterX";
        public string Direction { get; set; } = "LeftToRight";
        public int ExpectedCount { get; set; }
        public bool AllowMissing { get; set; }
        public bool AllowDuplicate { get; set; }

        public string SubjectLabel { get; set; } = string.Empty;
        public string ReferenceLabel { get; set; } = string.Empty;
        public string Relation { get; set; } = InspectionRuleRelations.LeftOf;
        public double MinDistance { get; set; }
        public double MaxDistance { get; set; }
    }

    public sealed class InspectionJudgeResult
    {
        public bool IsQualified { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string PrimaryReason { get; init; } = string.Empty;
        public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
        public IReadOnlyList<InspectionRuleResult> RuleResults { get; init; } = Array.Empty<InspectionRuleResult>();
    }

    public sealed class InspectionRuleResult
    {
        public string RuleId { get; init; } = string.Empty;
        public string RuleName { get; init; } = string.Empty;
        public string RuleType { get; init; } = string.Empty;
        public bool IsMatch { get; init; }
        public string Expected { get; init; } = string.Empty;
        public string Actual { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    public sealed class InspectionFallbackGoal
    {
        public string TargetLabel { get; init; } = string.Empty;
        public int TargetCount { get; init; }
    }
}
