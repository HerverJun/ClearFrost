// ============================================================================
// 文件名: InspectionRuleSetTemplates.cs
// 描述:   视觉算法调试场景模板
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClearFrost.Core.Rules
{
    public static class InspectionRuleSetTemplateIds
    {
        public const string ScrewCount = "screw_count";
        public const string RemoteMissingPart = "remote_missing_part";
        public const string WireSequence = "wire_sequence";
        public const string RelativePosition = "relative_position";
    }

    public static class InspectionRuleSetTemplates
    {
        public static InspectionRuleSet Create(
            string? templateId,
            IEnumerable<string>? labels = null,
            string? targetLabel = null,
            int targetCount = 0)
        {
            string id = NormalizeTemplateId(templateId);
            List<string> normalizedLabels = NormalizeLabels(labels);
            InspectionRuleSet ruleSet = id switch
            {
                InspectionRuleSetTemplateIds.RemoteMissingPart => CreateRemoteMissingPart(normalizedLabels),
                InspectionRuleSetTemplateIds.WireSequence => CreateWireSequence(normalizedLabels),
                InspectionRuleSetTemplateIds.RelativePosition => CreateRelativePosition(normalizedLabels),
                _ => CreateScrewCount(targetLabel, targetCount)
            };

            InspectionRuleSetSerializer.Normalize(ruleSet);
            return ruleSet;
        }

        public static IReadOnlyList<object> ListTemplates()
        {
            return new object[]
            {
                new { id = InspectionRuleSetTemplateIds.ScrewCount, name = "螺钉数量检测" },
                new { id = InspectionRuleSetTemplateIds.RemoteMissingPart, name = "遥控器漏装检测" },
                new { id = InspectionRuleSetTemplateIds.WireSequence, name = "线序顺序检测" },
                new { id = InspectionRuleSetTemplateIds.RelativePosition, name = "相对位置检测" }
            };
        }

        private static InspectionRuleSet CreateScrewCount(string? targetLabel, int targetCount)
        {
            string label = string.IsNullOrWhiteSpace(targetLabel) ? "screw" : targetLabel.Trim();
            int count = targetCount > 0 ? targetCount : 4;
            return new InspectionRuleSet
            {
                FallbackTargetLabel = label,
                FallbackTargetCount = count,
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "螺钉数量",
                        Type = InspectionRuleTypes.Count,
                        Label = label,
                        Operator = InspectionRuleOperators.Equal,
                        Count = count,
                        MinConfidence = 0
                    }
                }
            };
        }

        private static InspectionRuleSet CreateRemoteMissingPart(IReadOnlyList<string> labels)
        {
            string[] defaults = { "shell", "button", "battery_cover", "pcb" };
            List<string> expectedLabels = labels.Count > 0
                ? labels.Take(4).ToList()
                : defaults.ToList();

            return new InspectionRuleSet
            {
                FallbackTargetLabel = expectedLabels[0],
                FallbackTargetCount = 1,
                Rules = expectedLabels.Select(label => new InspectionRule
                {
                    Name = $"{label} 漏装",
                    Type = InspectionRuleTypes.Count,
                    Label = label,
                    Operator = InspectionRuleOperators.GreaterThanOrEqual,
                    Count = 1,
                    MinConfidence = 0
                }).ToList()
            };
        }

        private static InspectionRuleSet CreateWireSequence(IReadOnlyList<string> labels)
        {
            List<string> expectedLabels = labels.Count > 0
                ? labels.ToList()
                : new List<string> { "Wire_Brown", "Wire_Black", "Wire_Blue" };

            return new InspectionRuleSet
            {
                FallbackTargetLabel = expectedLabels[0],
                FallbackTargetCount = expectedLabels.Count,
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "线序顺序",
                        Type = InspectionRuleTypes.OrderedLabels,
                        ExpectedLabels = expectedLabels,
                        SortBy = "CenterX",
                        Direction = "LeftToRight",
                        ExpectedCount = expectedLabels.Count,
                        AllowMissing = false,
                        AllowDuplicate = false,
                        MinConfidence = 0
                    }
                }
            };
        }

        private static InspectionRuleSet CreateRelativePosition(IReadOnlyList<string> labels)
        {
            string subject = labels.ElementAtOrDefault(0) ?? "screw";
            string reference = labels.ElementAtOrDefault(1) ?? "body";
            return new InspectionRuleSet
            {
                FallbackTargetLabel = subject,
                FallbackTargetCount = 1,
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "相对位置",
                        Type = InspectionRuleTypes.RelativePosition,
                        SubjectLabel = subject,
                        ReferenceLabel = reference,
                        Relation = InspectionRuleRelations.LeftOf,
                        MinDistance = 0,
                        MaxDistance = 0,
                        MinConfidence = 0
                    }
                }
            };
        }

        private static string NormalizeTemplateId(string? templateId)
        {
            string value = templateId?.Trim() ?? string.Empty;
            if (string.Equals(value, "ScrewCount", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.ScrewCount;
            if (string.Equals(value, "RemoteMissingPart", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.RemoteMissingPart;
            if (string.Equals(value, "WireSequence", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.WireSequence;
            if (string.Equals(value, "RelativePosition", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.RelativePosition;
            return value.ToLowerInvariant() switch
            {
                InspectionRuleSetTemplateIds.RemoteMissingPart => InspectionRuleSetTemplateIds.RemoteMissingPart,
                InspectionRuleSetTemplateIds.WireSequence => InspectionRuleSetTemplateIds.WireSequence,
                InspectionRuleSetTemplateIds.RelativePosition => InspectionRuleSetTemplateIds.RelativePosition,
                _ => InspectionRuleSetTemplateIds.ScrewCount
            };
        }

        private static List<string> NormalizeLabels(IEnumerable<string>? labels)
        {
            return (labels ?? Array.Empty<string>())
                .Select(label => label?.Trim() ?? string.Empty)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
