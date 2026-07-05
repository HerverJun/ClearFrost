// ============================================================================
// 文件名: ClassificationRuleEvaluator.cs
// 描述:   分类任务判定规则
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClearFrost.Core.Rules;

namespace ClearFrost.Core.DeepLearning
{
    public static class ClassificationRuleEvaluator
    {
        public static InspectionJudgeResult Evaluate(
            InspectionRuleSet? ruleSet,
            ClassificationResultSummary summary)
        {
            IEnumerable<InspectionRule> rules = ruleSet?.EnabledRules?
                .Where(rule => IsClassificationRule(rule)) ?? Enumerable.Empty<InspectionRule>();
            List<InspectionRuleResult> ruleResults = rules
                .Select(rule => EvaluateRule(rule, summary))
                .ToList();

            if (ruleResults.Count == 0)
            {
                const string noRuleReason = "未启用分类判定规则，判定 NG";
                return new InspectionJudgeResult
                {
                    IsQualified = false,
                    Summary = noRuleReason,
                    PrimaryReason = noRuleReason,
                    Details = new[] { noRuleReason },
                    RuleResults = Array.Empty<InspectionRuleResult>()
                };
            }

            return BuildJudgeResult(ruleResults, "分类规则判定 OK", "分类规则判定 NG");
        }

        public static InspectionRuleResult EvaluateRule(
            InspectionRule rule,
            ClassificationResultSummary summary)
        {
            summary ??= new ClassificationResultSummary();
            IReadOnlyList<ClassificationTopKItem> topK = summary.TopK ?? Array.Empty<ClassificationTopKItem>();
            if (topK.Count == 0)
            {
                string noResultMessage = string.IsNullOrWhiteSpace(summary.Message) ? "未找到分类结果" : summary.Message;
                return Result(rule, false, BuildExpectedText(rule), "无分类结果", $"分类规则 NG：{noResultMessage}");
            }

            string expectedLabel = rule.ExpectedLabel?.Trim() ?? string.Empty;
            List<string> allowedLabels = NormalizeLabels(rule.AllowedLabels);
            string actualLabel = summary.Top1Label ?? string.Empty;
            float actualConfidence = summary.Top1Confidence;
            double minConfidence = Math.Clamp(rule.MinConfidence, 0.0, 1.0);
            var reasons = new List<string>();

            if (!string.IsNullOrWhiteSpace(expectedLabel) &&
                !string.Equals(expectedLabel, actualLabel, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"分类不匹配：期望 {expectedLabel}，实际 {actualLabel}");
            }

            if (allowedLabels.Count > 0 &&
                !allowedLabels.Any(label => string.Equals(label, actualLabel, StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add($"分类不在允许列表：实际 {actualLabel}，允许 {string.Join(", ", allowedLabels)}");
            }

            if (actualConfidence < minConfidence)
            {
                reasons.Add($"分类置信度不足：{Format(actualConfidence)} < {Format(minConfidence)}");
            }

            bool isMatch = reasons.Count == 0;
            string expected = BuildExpectedText(rule);
            string actual = $"实际 {actualLabel}，置信度 {Format(actualConfidence)}";
            string message = isMatch
                ? BuildOkMessage(expectedLabel, actualLabel, actualConfidence)
                : string.Join("；", reasons);

            return Result(rule, isMatch, expected, actual, message);
        }

        private static bool IsClassificationRule(InspectionRule rule) =>
            string.Equals(rule.Type, InspectionRuleTypes.Classification, StringComparison.OrdinalIgnoreCase);

        private static List<string> NormalizeLabels(IEnumerable<string>? labels)
        {
            return (labels ?? Array.Empty<string>())
                .Select(label => label?.Trim() ?? string.Empty)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
        }

        private static string BuildExpectedText(InspectionRule rule)
        {
            string expectedLabel = rule.ExpectedLabel?.Trim() ?? string.Empty;
            List<string> allowedLabels = NormalizeLabels(rule.AllowedLabels);
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(expectedLabel))
            {
                parts.Add($"期望 {expectedLabel}");
            }

            if (allowedLabels.Count > 0)
            {
                parts.Add($"允许 {string.Join(", ", allowedLabels)}");
            }

            if (rule.MinConfidence > 0)
            {
                parts.Add($"置信度 >= {Format(rule.MinConfidence)}");
            }

            return parts.Count == 0 ? "分类规则未配置" : string.Join("，", parts);
        }

        private static string BuildOkMessage(string expectedLabel, string actualLabel, float confidence)
        {
            string expected = string.IsNullOrWhiteSpace(expectedLabel) ? actualLabel : expectedLabel;
            return $"分类匹配：期望 {expected}，实际 {actualLabel}，置信度 {Format(confidence)}";
        }

        private static InspectionJudgeResult BuildJudgeResult(
            IReadOnlyList<InspectionRuleResult> ruleResults,
            string okSummary,
            string ngSummary)
        {
            bool isQualified = ruleResults.All(result => result.IsMatch);
            List<string> details = ruleResults
                .Select(result => result.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList();
            string primaryReason = ruleResults.FirstOrDefault(result => !result.IsMatch)?.Message
                ?? details.FirstOrDefault()
                ?? (isQualified ? okSummary : ngSummary);
            string summary = isQualified
                ? $"{okSummary}：{string.Join("；", details)}"
                : $"{ngSummary}：{primaryReason}";

            return new InspectionJudgeResult
            {
                IsQualified = isQualified,
                Summary = summary,
                PrimaryReason = primaryReason,
                Details = details,
                RuleResults = ruleResults
            };
        }

        private static InspectionRuleResult Result(
            InspectionRule rule,
            bool isMatch,
            string expected,
            string actual,
            string message)
        {
            return new InspectionRuleResult
            {
                RuleId = rule.Id ?? string.Empty,
                RuleName = string.IsNullOrWhiteSpace(rule.Name) ? "分类规则" : rule.Name,
                RuleType = InspectionRuleTypes.Classification,
                IsMatch = isMatch,
                Expected = expected,
                Actual = actual,
                Reason = message,
                Message = message
            };
        }

        private static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
