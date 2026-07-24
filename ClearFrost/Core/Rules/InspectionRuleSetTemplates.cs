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
        public const string TargetCount = "target_count";
        public const string ClassificationJudge = "classification_judge";
        public const string SegmentationArea = "segmentation_area";
        public const string ObbAngle = "obb_angle";
        public const string PoseKeypoints = "pose_keypoints";
        public const string ScrewCount = "screw_count";
        public const string RemoteMissingPart = "remote_missing_part";
        public const string WireSequence = "wire_sequence";
        public const string RelativePosition = "relative_position";
        public const string W5ScrewCount = "w5_screw_count";
        public const string W6ScrewCount = "w6_screw_count";
        public const string N5RemoteMissingPart = "n5_remote_missing_part";
        public const string N6RemoteMissingPart = "n6_remote_missing_part";
        public const string ElectricHeatingScrewCount = "electric_heating_screw_count";
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
                InspectionRuleSetTemplateIds.TargetCount => CreateScrewCount(targetLabel, targetCount, "目标数量检测"),
                InspectionRuleSetTemplateIds.ClassificationJudge => CreateClassificationJudge(normalizedLabels, targetLabel),
                InspectionRuleSetTemplateIds.SegmentationArea => CreateSegmentationArea(normalizedLabels, targetLabel),
                InspectionRuleSetTemplateIds.ObbAngle => CreateObbAngle(normalizedLabels, targetLabel),
                InspectionRuleSetTemplateIds.PoseKeypoints => CreatePoseKeypoints(normalizedLabels, targetLabel),
                InspectionRuleSetTemplateIds.RemoteMissingPart => CreateRemoteMissingPart(normalizedLabels),
                InspectionRuleSetTemplateIds.N5RemoteMissingPart => CreateRemoteMissingPart(normalizedLabels, targetLabel),
                InspectionRuleSetTemplateIds.N6RemoteMissingPart => CreateRemoteMissingPart(normalizedLabels, targetLabel),
                InspectionRuleSetTemplateIds.WireSequence => CreateWireSequence(normalizedLabels),
                InspectionRuleSetTemplateIds.RelativePosition => CreateRelativePosition(normalizedLabels),
                InspectionRuleSetTemplateIds.W5ScrewCount => CreateScrewCount(targetLabel, targetCount),
                InspectionRuleSetTemplateIds.W6ScrewCount => CreateScrewCount(targetLabel, targetCount),
                InspectionRuleSetTemplateIds.ElectricHeatingScrewCount => CreateScrewCount(targetLabel, targetCount, "电加热螺钉数量"),
                _ => CreateScrewCount(targetLabel, targetCount)
            };

            InspectionRuleSetSerializer.Normalize(ruleSet);
            return ruleSet;
        }

        public static IReadOnlyList<object> ListTemplates()
        {
            return new object[]
            {
                new { id = InspectionRuleSetTemplateIds.TargetCount, name = "目标数量检测" },
                new { id = InspectionRuleSetTemplateIds.ClassificationJudge, name = "分类判定" },
                new { id = InspectionRuleSetTemplateIds.SegmentationArea, name = "分割面积判定" },
                new { id = InspectionRuleSetTemplateIds.ObbAngle, name = "OBB 角度判定（预留）" },
                new { id = InspectionRuleSetTemplateIds.PoseKeypoints, name = "姿态关键点判定（预留）" },
                new { id = InspectionRuleSetTemplateIds.ScrewCount, name = "螺钉数量检测" },
                new { id = InspectionRuleSetTemplateIds.RemoteMissingPart, name = "遥控器漏装检测" },
                new { id = InspectionRuleSetTemplateIds.W5ScrewCount, name = "W5 螺钉数量检测" },
                new { id = InspectionRuleSetTemplateIds.W6ScrewCount, name = "W6 螺钉数量检测" },
                new { id = InspectionRuleSetTemplateIds.N5RemoteMissingPart, name = "N5 遥控器漏装" },
                new { id = InspectionRuleSetTemplateIds.N6RemoteMissingPart, name = "N6 遥控器漏装" },
                new { id = InspectionRuleSetTemplateIds.ElectricHeatingScrewCount, name = "电加热螺钉检测" },
                new { id = InspectionRuleSetTemplateIds.WireSequence, name = "线序顺序检测" },
                new { id = InspectionRuleSetTemplateIds.RelativePosition, name = "相对位置检测" }
            };
        }

        private static InspectionRuleSet CreateClassificationJudge(IReadOnlyList<string> labels, string? targetLabel)
        {
            string expected = !string.IsNullOrWhiteSpace(targetLabel)
                ? targetLabel.Trim()
                : labels.FirstOrDefault(label => string.Equals(label, "OK", StringComparison.OrdinalIgnoreCase))
                  ?? labels.FirstOrDefault()
                  ?? "OK";
            return new InspectionRuleSet
            {
                FallbackTargetLabel = expected,
                FallbackTargetCount = 1,
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "分类判定",
                        Type = InspectionRuleTypes.Classification,
                        ExpectedLabel = expected,
                        AllowedLabels = new List<string> { expected },
                        MinConfidence = 0.8
                    }
                }
            };
        }

        private static InspectionRuleSet CreateSegmentationArea(IReadOnlyList<string> labels, string? targetLabel)
        {
            string label = !string.IsNullOrWhiteSpace(targetLabel)
                ? targetLabel.Trim()
                : labels.FirstOrDefault() ?? "glue";
            return new InspectionRuleSet
            {
                FallbackTargetLabel = label,
                FallbackTargetCount = 1,
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "分割面积判定",
                        Type = InspectionRuleTypes.SegmentationArea,
                        Label = label,
                        Operator = InspectionRuleOperators.GreaterThanOrEqual,
                        Count = 1,
                        MinConfidence = 0.5,
                        MinArea = 1,
                        MinCoverage = 0.01
                    }
                }
            };
        }

        private static InspectionRuleSet CreateObbAngle(IReadOnlyList<string> labels, string? targetLabel)
        {
            string label = !string.IsNullOrWhiteSpace(targetLabel)
                ? targetLabel.Trim()
                : labels.FirstOrDefault() ?? "screw";
            return new InspectionRuleSet
            {
                FallbackTargetLabel = label,
                FallbackTargetCount = 1,
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "OBB 角度判定（预留）",
                        Type = InspectionRuleTypes.ObbAngle,
                        Label = label,
                        MinConfidence = 0.5,
                        MinAngle = -180,
                        MaxAngle = 180
                    }
                }
            };
        }

        private static InspectionRuleSet CreatePoseKeypoints(IReadOnlyList<string> labels, string? targetLabel)
        {
            string label = !string.IsNullOrWhiteSpace(targetLabel)
                ? targetLabel.Trim()
                : labels.FirstOrDefault() ?? "person";
            return new InspectionRuleSet
            {
                FallbackTargetLabel = label,
                FallbackTargetCount = 1,
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "姿态关键点判定（预留）",
                        Type = InspectionRuleTypes.PoseKeypoints,
                        Label = label,
                        ExpectedCount = 1,
                        MinConfidence = 0.5,
                        MinKeyPointConfidence = 0.3
                    }
                }
            };
        }

        private static InspectionRuleSet CreateScrewCount(string? targetLabel, int targetCount, string ruleName = "螺钉数量")
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
                        Name = ruleName,
                        Type = InspectionRuleTypes.Count,
                        Label = label,
                        Operator = InspectionRuleOperators.Equal,
                        Count = count,
                        MinConfidence = 0
                    }
                }
            };
        }

        private static InspectionRuleSet CreateRemoteMissingPart(IReadOnlyList<string> labels, string? targetLabel = null)
        {
            string[] defaults = { "shell", "button", "battery_cover", "pcb" };
            List<string> expectedLabels = labels.Count > 0
                ? labels.Take(4).ToList()
                : !string.IsNullOrWhiteSpace(targetLabel)
                    ? new List<string> { targetLabel.Trim() }
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
            if (string.Equals(value, "TargetCount", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.TargetCount;
            if (string.Equals(value, "ClassificationJudge", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.ClassificationJudge;
            if (string.Equals(value, "SegmentationArea", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.SegmentationArea;
            if (string.Equals(value, "ObbAngle", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.ObbAngle;
            if (string.Equals(value, "PoseKeypoints", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.PoseKeypoints;
            if (string.Equals(value, "RemoteMissingPart", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.RemoteMissingPart;
            if (string.Equals(value, "WireSequence", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.WireSequence;
            if (string.Equals(value, "RelativePosition", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.RelativePosition;
            if (string.Equals(value, "W5ScrewCount", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.W5ScrewCount;
            if (string.Equals(value, "W6ScrewCount", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.W6ScrewCount;
            if (string.Equals(value, "N5RemoteMissingPart", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.N5RemoteMissingPart;
            if (string.Equals(value, "N6RemoteMissingPart", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.N6RemoteMissingPart;
            if (string.Equals(value, "ElectricHeatingScrewCount", StringComparison.OrdinalIgnoreCase)) return InspectionRuleSetTemplateIds.ElectricHeatingScrewCount;
            return value.ToLowerInvariant() switch
            {
                InspectionRuleSetTemplateIds.TargetCount => InspectionRuleSetTemplateIds.TargetCount,
                InspectionRuleSetTemplateIds.ClassificationJudge => InspectionRuleSetTemplateIds.ClassificationJudge,
                InspectionRuleSetTemplateIds.SegmentationArea => InspectionRuleSetTemplateIds.SegmentationArea,
                InspectionRuleSetTemplateIds.ObbAngle => InspectionRuleSetTemplateIds.ObbAngle,
                InspectionRuleSetTemplateIds.PoseKeypoints => InspectionRuleSetTemplateIds.PoseKeypoints,
                InspectionRuleSetTemplateIds.RemoteMissingPart => InspectionRuleSetTemplateIds.RemoteMissingPart,
                InspectionRuleSetTemplateIds.WireSequence => InspectionRuleSetTemplateIds.WireSequence,
                InspectionRuleSetTemplateIds.RelativePosition => InspectionRuleSetTemplateIds.RelativePosition,
                InspectionRuleSetTemplateIds.W5ScrewCount => InspectionRuleSetTemplateIds.W5ScrewCount,
                InspectionRuleSetTemplateIds.W6ScrewCount => InspectionRuleSetTemplateIds.W6ScrewCount,
                InspectionRuleSetTemplateIds.N5RemoteMissingPart => InspectionRuleSetTemplateIds.N5RemoteMissingPart,
                InspectionRuleSetTemplateIds.N6RemoteMissingPart => InspectionRuleSetTemplateIds.N6RemoteMissingPart,
                InspectionRuleSetTemplateIds.ElectricHeatingScrewCount => InspectionRuleSetTemplateIds.ElectricHeatingScrewCount,
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
