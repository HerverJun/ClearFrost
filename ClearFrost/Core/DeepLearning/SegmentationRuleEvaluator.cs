// ============================================================================
// 文件名: SegmentationRuleEvaluator.cs
// 描述:   分割任务面积/覆盖率判定规则
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClearFrost.Core.Rules;

namespace ClearFrost.Core.DeepLearning
{
    public static class SegmentationRuleEvaluator
    {
        public static InspectionJudgeResult Evaluate(
            InspectionRuleSet? ruleSet,
            SegmentationResultSummary summary)
        {
            IEnumerable<InspectionRule> rules = ruleSet?.EnabledRules?
                .Where(rule => IsSegmentationRule(rule)) ?? Enumerable.Empty<InspectionRule>();
            List<InspectionRuleResult> ruleResults = rules
                .Select(rule => EvaluateRule(rule, summary))
                .ToList();

            if (ruleResults.Count == 0)
            {
                const string noRuleReason = "未启用分割判定规则，判定 NG";
                return new InspectionJudgeResult
                {
                    IsQualified = false,
                    Summary = noRuleReason,
                    PrimaryReason = noRuleReason,
                    Details = new[] { noRuleReason },
                    RuleResults = Array.Empty<InspectionRuleResult>()
                };
            }

            bool isQualified = ruleResults.All(result => result.IsMatch);
            List<string> details = ruleResults
                .Select(result => result.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList();
            string primaryReason = ruleResults.FirstOrDefault(result => !result.IsMatch)?.Message
                ?? details.FirstOrDefault()
                ?? (isQualified ? "分割规则判定 OK" : "分割规则判定 NG");
            return new InspectionJudgeResult
            {
                IsQualified = isQualified,
                Summary = isQualified
                    ? $"分割规则判定 OK：{string.Join("；", details)}"
                    : $"分割规则判定 NG：{primaryReason}",
                PrimaryReason = primaryReason,
                Details = details,
                RuleResults = ruleResults
            };
        }

        public static InspectionRuleResult EvaluateRule(
            InspectionRule rule,
            SegmentationResultSummary summary)
        {
            summary ??= new SegmentationResultSummary();
            IReadOnlyList<SegmentationInstanceSummary> instances = summary.Instances ?? Array.Empty<SegmentationInstanceSummary>();
            string targetLabel = rule.Label?.Trim() ?? string.Empty;
            double minConfidence = Math.Clamp(rule.MinConfidence, 0.0, 1.0);
            List<SegmentationInstanceSummary> matched = instances
                .Where(instance =>
                    instance.Confidence >= minConfidence &&
                    (string.IsNullOrWhiteSpace(targetLabel) ||
                     string.Equals(instance.Label, targetLabel, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var reasons = new List<string>();
            if (instances.Count == 0)
            {
                reasons.Add(string.IsNullOrWhiteSpace(summary.Message) ? "未找到分割结果" : summary.Message);
            }

            if (!string.IsNullOrWhiteSpace(targetLabel) && matched.Count == 0)
            {
                reasons.Add($"未找到分割类别 {targetLabel}");
            }

            int expectedCount = rule.ExpectedCount > 0 ? rule.ExpectedCount : rule.Count;
            bool hasCountExpectation = expectedCount > 0;
            if (hasCountExpectation && !Compare(matched.Count, expectedCount, NormalizeOperator(rule.Operator)))
            {
                reasons.Add($"分割数量不符：期望 {OperatorText(rule.Operator)} {expectedCount}，实际 {matched.Count}");
            }

            foreach (SegmentationInstanceSummary instance in matched)
            {
                if (!instance.HasMask)
                {
                    reasons.Add($"#{instance.Index} {instance.Label} 缺少 MaskData");
                    continue;
                }

                if (rule.MinArea > 0 && instance.MaskArea < rule.MinArea)
                {
                    reasons.Add($"#{instance.Index} 面积不足：{Format(instance.MaskArea)} < {Format(rule.MinArea)}");
                }

                if (rule.MaxArea > 0 && instance.MaskArea > rule.MaxArea)
                {
                    reasons.Add($"#{instance.Index} 面积超限：{Format(instance.MaskArea)} > {Format(rule.MaxArea)}");
                }

                if (rule.MinCoverage > 0 && instance.MaskCoverage < rule.MinCoverage)
                {
                    reasons.Add($"#{instance.Index} 覆盖率不足：{Format(instance.MaskCoverage)} < {Format(rule.MinCoverage)}");
                }

                if (rule.MaxCoverage > 0 && instance.MaskCoverage > rule.MaxCoverage)
                {
                    reasons.Add($"#{instance.Index} 覆盖率超限：{Format(instance.MaskCoverage)} > {Format(rule.MaxCoverage)}");
                }
            }

            bool isMatch = reasons.Count == 0;
            string expected = BuildExpectedText(rule, targetLabel, expectedCount, hasCountExpectation);
            string actual = matched.Count == 0
                ? "无匹配分割目标"
                : string.Join("; ", matched.Select(instance => $"#{instance.Index} {instance.Label} area={Format(instance.MaskArea)} coverage={Format(instance.MaskCoverage)}"));
            string message = isMatch
                ? $"分割规则 OK：{targetLabelOrAll(targetLabel)} 数量 {matched.Count}，面积/覆盖率满足要求"
                : $"分割规则 NG：{string.Join("；", reasons)}";

            return new InspectionRuleResult
            {
                RuleId = rule.Id ?? string.Empty,
                RuleName = string.IsNullOrWhiteSpace(rule.Name) ? "分割面积规则" : rule.Name,
                RuleType = InspectionRuleTypes.SegmentationArea,
                IsMatch = isMatch,
                Expected = expected,
                Actual = actual,
                Reason = message,
                Message = message,
                AssociatedBoxIndexes = matched.Select(instance => instance.Index).ToArray(),
                AssociationSummary = matched.Count == 0
                    ? "关联分割目标: 无"
                    : $"关联分割目标: {string.Join(", ", matched.Select(instance => $"#{instance.Index}"))}"
            };
        }

        private static bool IsSegmentationRule(InspectionRule rule) =>
            string.Equals(rule.Type, InspectionRuleTypes.SegmentationArea, StringComparison.OrdinalIgnoreCase);

        private static string BuildExpectedText(
            InspectionRule rule,
            string targetLabel,
            int expectedCount,
            bool hasCountExpectation)
        {
            var parts = new List<string> { targetLabelOrAll(targetLabel) };
            if (hasCountExpectation)
            {
                parts.Add($"数量 {OperatorText(rule.Operator)} {expectedCount}");
            }

            if (rule.MinConfidence > 0) parts.Add($"置信度 >= {Format(rule.MinConfidence)}");
            if (rule.MinArea > 0) parts.Add($"面积 >= {Format(rule.MinArea)}");
            if (rule.MaxArea > 0) parts.Add($"面积 <= {Format(rule.MaxArea)}");
            if (rule.MinCoverage > 0) parts.Add($"覆盖率 >= {Format(rule.MinCoverage)}");
            if (rule.MaxCoverage > 0) parts.Add($"覆盖率 <= {Format(rule.MaxCoverage)}");
            return string.Join("，", parts);
        }

        private static bool Compare(int actual, int expected, string op)
        {
            return op switch
            {
                InspectionRuleOperators.NotEqual => actual != expected,
                InspectionRuleOperators.GreaterThan => actual > expected,
                InspectionRuleOperators.GreaterThanOrEqual => actual >= expected,
                InspectionRuleOperators.LessThan => actual < expected,
                InspectionRuleOperators.LessThanOrEqual => actual <= expected,
                _ => actual == expected
            };
        }

        private static string NormalizeOperator(string? op)
        {
            if (string.Equals(op, InspectionRuleOperators.NotEqual, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.NotEqual;
            if (string.Equals(op, InspectionRuleOperators.GreaterThan, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.GreaterThan;
            if (string.Equals(op, InspectionRuleOperators.GreaterThanOrEqual, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.GreaterThanOrEqual;
            if (string.Equals(op, InspectionRuleOperators.LessThan, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.LessThan;
            if (string.Equals(op, InspectionRuleOperators.LessThanOrEqual, StringComparison.OrdinalIgnoreCase)) return InspectionRuleOperators.LessThanOrEqual;
            return InspectionRuleOperators.Equal;
        }

        private static string OperatorText(string? op)
        {
            return NormalizeOperator(op) switch
            {
                InspectionRuleOperators.NotEqual => "!=",
                InspectionRuleOperators.GreaterThan => ">",
                InspectionRuleOperators.GreaterThanOrEqual => ">=",
                InspectionRuleOperators.LessThan => "<",
                InspectionRuleOperators.LessThanOrEqual => "<=",
                _ => "="
            };
        }

        private static string targetLabelOrAll(string label) =>
            string.IsNullOrWhiteSpace(label) ? "全部分割目标" : label;

        private static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
