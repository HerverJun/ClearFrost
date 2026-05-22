// ============================================================================
// 文件名: InspectionRuleEngine.cs
// 描述:   检测判定规则引擎
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClearFrost.Yolo;

namespace ClearFrost.Core.Rules
{
    public static class InspectionRuleEngine
    {
        public static InspectionJudgeResult Evaluate(
            InspectionRuleSet? ruleSet,
            IReadOnlyList<YoloResult>? detections,
            IReadOnlyList<string>? labels)
        {
            List<YoloResult> resultList = detections?.ToList() ?? new List<YoloResult>();
            string[] labelArray = labels?.ToArray() ?? Array.Empty<string>();
            List<InspectionRule> rules = ruleSet?.EnabledRules.ToList() ?? new List<InspectionRule>();

            if (rules.Count == 0)
            {
                return new InspectionJudgeResult
                {
                    IsQualified = false,
                    Summary = "未启用判定规则，判定 NG",
                    RuleResults = Array.Empty<InspectionRuleResult>()
                };
            }

            List<InspectionRuleResult> ruleResults = rules
                .Select(rule => EvaluateRule(rule, resultList, labelArray))
                .ToList();

            bool isQualified = ruleResults.All(result => result.IsMatch);
            string summary = string.Join("; ", ruleResults.Select(result =>
                $"{DisplayName(result)} {(result.IsMatch ? "OK" : "NG")}: {result.Actual}"));

            return new InspectionJudgeResult
            {
                IsQualified = isQualified,
                Summary = summary,
                RuleResults = ruleResults
            };
        }

        public static InspectionFallbackGoal? GetFallbackGoal(InspectionRuleSet? ruleSet)
        {
            InspectionRule? countRule = ruleSet?.EnabledRules
                .FirstOrDefault(rule =>
                    IsType(rule, InspectionRuleTypes.Count) &&
                    !string.IsNullOrWhiteSpace(rule.Label) &&
                    IsOperator(rule.Operator, InspectionRuleOperators.Equal));

            if (countRule != null)
            {
                return new InspectionFallbackGoal
                {
                    TargetLabel = countRule.Label.Trim(),
                    TargetCount = Math.Max(0, countRule.Count)
                };
            }

            if (!string.IsNullOrWhiteSpace(ruleSet?.FallbackTargetLabel))
            {
                return new InspectionFallbackGoal
                {
                    TargetLabel = ruleSet.FallbackTargetLabel.Trim(),
                    TargetCount = Math.Max(0, ruleSet.FallbackTargetCount)
                };
            }

            return null;
        }

        private static InspectionRuleResult EvaluateRule(
            InspectionRule rule,
            IReadOnlyList<YoloResult> detections,
            IReadOnlyList<string> labels)
        {
            if (IsType(rule, InspectionRuleTypes.OrderedLabels))
            {
                return EvaluateOrderedLabels(rule, detections, labels);
            }

            if (IsType(rule, InspectionRuleTypes.RelativePosition))
            {
                return EvaluateRelativePosition(rule, detections, labels);
            }

            return EvaluateCount(rule, detections, labels);
        }

        private static InspectionRuleResult EvaluateCount(
            InspectionRule rule,
            IReadOnlyList<YoloResult> detections,
            IReadOnlyList<string> labels)
        {
            string targetLabel = rule.Label?.Trim() ?? string.Empty;
            double minConfidence = NormalizeConfidence(rule.MinConfidence);
            int actualCount = detections.Count(detection =>
                detection.Confidence >= minConfidence &&
                (string.IsNullOrWhiteSpace(targetLabel) ||
                 string.Equals(ResolveLabel(detection, labels), targetLabel, StringComparison.OrdinalIgnoreCase)));

            int expectedCount = Math.Max(0, rule.Count);
            bool isMatch = Compare(actualCount, expectedCount, NormalizeOperator(rule.Operator));
            string expected = $"{targetLabelOrAll(targetLabel)} {OperatorText(rule.Operator)} {expectedCount}";
            string actual = actualCount.ToString(CultureInfo.InvariantCulture);

            return Result(
                rule,
                isMatch,
                expected,
                actual,
                isMatch
                    ? $"数量满足: {actualCount}"
                    : $"数量不满足: 期望 {expected}, 实际 {actualCount}");
        }

        private static InspectionRuleResult EvaluateOrderedLabels(
            InspectionRule rule,
            IReadOnlyList<YoloResult> detections,
            IReadOnlyList<string> labels)
        {
            List<string> expectedLabels = NormalizeLabels(rule.ExpectedLabels);
            double minConfidence = NormalizeConfidence(rule.MinConfidence);
            List<YoloResult> ordered = SortDetections(
                detections.Where(detection => detection.Confidence >= minConfidence),
                rule.SortBy,
                rule.Direction);

            List<string> actualOrder = ordered
                .Select(detection => ResolveLabel(detection, labels))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();

            int expectedCount = rule.ExpectedCount > 0 ? rule.ExpectedCount : expectedLabels.Count;
            List<string> missingLabels = ComputeMissingLabels(expectedLabels, actualOrder);
            List<string> duplicateLabels = actualOrder
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            var reasons = new List<string>();
            if (expectedLabels.Count == 0)
            {
                reasons.Add("未配置期望顺序");
            }
            else if (actualOrder.Count == 0)
            {
                reasons.Add("未检测到期望标签");
            }

            if (!rule.AllowMissing && missingLabels.Count > 0)
            {
                reasons.Add($"缺失: {string.Join(", ", missingLabels)}");
            }

            if (!rule.AllowDuplicate && duplicateLabels.Count > 0)
            {
                reasons.Add($"重复: {string.Join(", ", duplicateLabels)}");
            }

            if (expectedCount > 0 && IsCountMismatch(actualOrder.Count, expectedCount, rule.AllowMissing, rule.AllowDuplicate))
            {
                reasons.Add($"数量不符: 期望 {expectedCount}, 实际 {actualOrder.Count}");
            }

            if (expectedLabels.Count > 0 &&
                !MatchesExpectedOrder(expectedLabels, actualOrder, rule.AllowMissing, rule.AllowDuplicate))
            {
                reasons.Add($"顺序不符: {FormatLabels(actualOrder)}");
            }

            bool isMatch = reasons.Count == 0;
            return Result(
                rule,
                isMatch,
                FormatLabels(expectedLabels),
                FormatLabels(actualOrder),
                isMatch ? $"顺序满足: {FormatLabels(actualOrder)}" : string.Join("; ", reasons));
        }

        private static InspectionRuleResult EvaluateRelativePosition(
            InspectionRule rule,
            IReadOnlyList<YoloResult> detections,
            IReadOnlyList<string> labels)
        {
            string subjectLabel = rule.SubjectLabel?.Trim() ?? string.Empty;
            string referenceLabel = rule.ReferenceLabel?.Trim() ?? string.Empty;
            double minConfidence = NormalizeConfidence(rule.MinConfidence);

            if (string.IsNullOrWhiteSpace(subjectLabel) || string.IsNullOrWhiteSpace(referenceLabel))
            {
                return Result(rule, false, "主标签和参考标签必须配置", "未配置", "位置规则缺少标签");
            }

            List<YoloResult> subjects = FindDetections(detections, labels, subjectLabel, minConfidence);
            List<YoloResult> references = FindDetections(detections, labels, referenceLabel, minConfidence);

            if (subjects.Count == 0 || references.Count == 0)
            {
                string actual = subjects.Count == 0 && references.Count == 0
                    ? "主标签和参考标签均缺失"
                    : subjects.Count == 0 ? "主标签缺失" : "参考标签缺失";
                return Result(rule, false, RelativeExpected(rule), actual, actual);
            }

            var subjectResults = subjects
                .Select(subject =>
                {
                    var distances = references
                        .Select(reference =>
                        {
                            double distance = GetEdgeDistance(subject, reference, rule.Relation);
                            bool matched = IsRelativeDirectionMatched(subject, reference, rule.Relation, rule.MinDistance) &&
                                           (rule.MaxDistance <= 0 || distance <= rule.MaxDistance);

                            return new
                            {
                                Distance = distance,
                                Matched = matched
                            };
                        })
                        .ToList();
                    double bestDistance = distances.Any(item => item.Matched)
                        ? distances.Where(item => item.Matched).Min(item => item.Distance)
                        : distances.OrderBy(item => Math.Abs(item.Distance)).First().Distance;

                    return new
                    {
                        BestDistance = bestDistance,
                        Matched = distances.Any(item => item.Matched)
                    };
                })
                .ToList();

            bool isMatch = subjectResults.All(item => item.Matched);
            double displayDistance = subjectResults
                .Select(item => item.BestDistance)
                .DefaultIfEmpty(0)
                .Min();
            string actualText = $"{RelativeActual(rule.Relation)} 间距 {displayDistance:F1}px, 主目标 {subjects.Count} 个, 参考 {references.Count} 个";

            return Result(
                rule,
                isMatch,
                RelativeExpected(rule),
                actualText,
                isMatch ? $"位置满足: {actualText}" : $"位置不满足: {actualText}");
        }

        internal static List<YoloResult> SortDetections(
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

        private static List<YoloResult> FindDetections(
            IReadOnlyList<YoloResult> detections,
            IReadOnlyList<string> labels,
            string targetLabel,
            double minConfidence)
        {
            return detections
                .Where(detection =>
                    detection.Confidence >= minConfidence &&
                    string.Equals(ResolveLabel(detection, labels), targetLabel, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(detection => detection.Confidence)
                .ThenByDescending(detection => detection.Area)
                .ToList();
        }

        private static bool IsRelativeDirectionMatched(
            YoloResult subject,
            YoloResult reference,
            string? relation,
            double minDistance)
        {
            minDistance = Math.Max(0, minDistance);
            return NormalizeRelation(relation) switch
            {
                InspectionRuleRelations.RightOf => subject.Left >= reference.Right + minDistance,
                InspectionRuleRelations.Above => subject.Bottom + minDistance <= reference.Top,
                InspectionRuleRelations.Below => subject.Top >= reference.Bottom + minDistance,
                _ => subject.Right + minDistance <= reference.Left
            };
        }

        private static double GetEdgeDistance(YoloResult subject, YoloResult reference, string? relation)
        {
            return NormalizeRelation(relation) switch
            {
                InspectionRuleRelations.RightOf => subject.Left - reference.Right,
                InspectionRuleRelations.Above => reference.Top - subject.Bottom,
                InspectionRuleRelations.Below => subject.Top - reference.Bottom,
                _ => reference.Left - subject.Right
            };
        }

        private static List<string> NormalizeLabels(IEnumerable<string>? labels)
        {
            return (labels ?? Array.Empty<string>())
                .Select(label => label?.Trim() ?? string.Empty)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
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
            if (IsOperator(op, InspectionRuleOperators.NotEqual)) return InspectionRuleOperators.NotEqual;
            if (IsOperator(op, InspectionRuleOperators.GreaterThan)) return InspectionRuleOperators.GreaterThan;
            if (IsOperator(op, InspectionRuleOperators.GreaterThanOrEqual)) return InspectionRuleOperators.GreaterThanOrEqual;
            if (IsOperator(op, InspectionRuleOperators.LessThan)) return InspectionRuleOperators.LessThan;
            if (IsOperator(op, InspectionRuleOperators.LessThanOrEqual)) return InspectionRuleOperators.LessThanOrEqual;
            return InspectionRuleOperators.Equal;
        }

        private static string NormalizeRelation(string? relation)
        {
            if (string.Equals(relation, InspectionRuleRelations.RightOf, StringComparison.OrdinalIgnoreCase)) return InspectionRuleRelations.RightOf;
            if (string.Equals(relation, InspectionRuleRelations.Above, StringComparison.OrdinalIgnoreCase)) return InspectionRuleRelations.Above;
            if (string.Equals(relation, InspectionRuleRelations.Below, StringComparison.OrdinalIgnoreCase)) return InspectionRuleRelations.Below;
            return InspectionRuleRelations.LeftOf;
        }

        private static bool IsType(InspectionRule rule, string type) =>
            string.Equals(rule.Type, type, StringComparison.OrdinalIgnoreCase);

        private static bool IsOperator(string? value, string op) =>
            string.Equals(value, op, StringComparison.OrdinalIgnoreCase);

        private static double NormalizeConfidence(double value) => Math.Clamp(value, 0.0, 1.0);

        private static string FormatLabels(IReadOnlyCollection<string> labels) =>
            labels.Count == 0 ? "<empty>" : string.Join(" -> ", labels);

        private static string DisplayName(InspectionRuleResult result) =>
            string.IsNullOrWhiteSpace(result.RuleName) ? result.RuleType : result.RuleName;

        private static string targetLabelOrAll(string label) =>
            string.IsNullOrWhiteSpace(label) ? "全部目标" : label;

        private static string OperatorText(string? op)
        {
            return NormalizeOperator(op) switch
            {
                InspectionRuleOperators.NotEqual => "!=",
                InspectionRuleOperators.GreaterThan => ">",
                InspectionRuleOperators.GreaterThanOrEqual => ">=",
                InspectionRuleOperators.LessThan => "<",
                InspectionRuleOperators.LessThanOrEqual => "<=",
                _ => "=="
            };
        }

        private static string RelationText(string? relation)
        {
            return NormalizeRelation(relation) switch
            {
                InspectionRuleRelations.RightOf => "右侧",
                InspectionRuleRelations.Above => "上方",
                InspectionRuleRelations.Below => "下方",
                _ => "左侧"
            };
        }

        private static string RelativeExpected(InspectionRule rule)
        {
            string baseText = $"{rule.SubjectLabel} 在 {rule.ReferenceLabel} {RelationText(rule.Relation)}";
            if (rule.MinDistance > 0)
            {
                baseText += $", 最小间距 {rule.MinDistance:F1}px";
            }

            if (rule.MaxDistance > 0)
            {
                baseText += $", 最大间距 {rule.MaxDistance:F1}px";
            }

            return baseText;
        }

        private static string RelativeActual(string? relation) => $"实际{RelationText(relation)}";

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
                RuleName = string.IsNullOrWhiteSpace(rule.Name) ? DefaultRuleName(rule) : rule.Name,
                RuleType = rule.Type ?? string.Empty,
                IsMatch = isMatch,
                Expected = expected,
                Actual = actual,
                Message = message
            };
        }

        private static string DefaultRuleName(InspectionRule rule)
        {
            if (IsType(rule, InspectionRuleTypes.OrderedLabels)) return "顺序规则";
            if (IsType(rule, InspectionRuleTypes.RelativePosition)) return "位置规则";
            return "数量规则";
        }
    }
}
