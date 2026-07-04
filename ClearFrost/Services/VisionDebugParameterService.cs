// ============================================================================
// 文件名: VisionDebugParameterService.cs
// 描述:   视觉算法调试参数解析、试运行与保存辅助
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClearFrost.Config;
using ClearFrost.Core.Rules;
using ClearFrost.Yolo;

namespace ClearFrost.Services
{
    public sealed class VisionDebugRunParameters
    {
        public float? Confidence { get; set; }
        public float? IouThreshold { get; set; }
        public string TargetLabel { get; set; } = string.Empty;
        public int? TargetCount { get; set; }
        public string RuleSetJson { get; set; } = string.Empty;
        public bool RoiEnabled { get; set; } = true;
        public long? RecordId { get; set; }
        public string TemplateId { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new List<string>();
        public int? BatchLimit { get; set; }
        public string BatchResult { get; set; } = "All";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public YoloPreprocessingMode PreprocessingMode { get; set; } = YoloPreprocessingMode.StandardLetterBox;
    }

    public static class VisionDebugParameterService
    {
        public static float ResolveConfidence(AppConfig config, VisionDebugRunParameters? parameters)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return (float)Math.Clamp(parameters?.Confidence ?? config.Confidence, 0d, 1d);
        }

        public static float ResolveIou(AppConfig config, VisionDebugRunParameters? parameters)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return (float)Math.Clamp(parameters?.IouThreshold ?? config.IouThreshold, 0d, 1d);
        }

        public static InspectionRuleSet ResolveRuleSet(
            AppConfig config,
            VisionDebugRunParameters? parameters,
            out string normalizedJson)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            parameters ??= new VisionDebugRunParameters();

            if (!string.IsNullOrWhiteSpace(parameters.TemplateId))
            {
                TemplateGoal goal = ResolveTemplateGoal(config, parameters);
                IEnumerable<string> templateLabels = ResolveTemplateLabels(parameters, goal);
                InspectionRuleSet template = InspectionRuleSetTemplates.Create(
                    parameters.TemplateId,
                    templateLabels,
                    goal.TargetLabel,
                    goal.TargetCount);
                normalizedJson = InspectionRuleSetSerializer.Serialize(template);
                return template;
            }

            if (!string.IsNullOrWhiteSpace(parameters.RuleSetJson))
            {
                if (!InspectionRuleSetSerializer.TryDeserialize(parameters.RuleSetJson, out InspectionRuleSet ruleSet, out string errorMessage))
                {
                    throw new InvalidOperationException($"判定规则配置无效: {errorMessage}");
                }

                ApplyTargetOverrides(ruleSet, parameters, config);
                normalizedJson = InspectionRuleSetSerializer.Serialize(ruleSet);
                return ruleSet;
            }

            if (!string.IsNullOrWhiteSpace(parameters.TargetLabel) || parameters.TargetCount.HasValue)
            {
                InspectionRuleSet ruleSet = InspectionRuleSetSerializer.FromLegacyTarget(
                    parameters.TargetLabel,
                    parameters.TargetCount ?? Math.Max(0, config.TargetCount));
                normalizedJson = InspectionRuleSetSerializer.Serialize(ruleSet);
                return ruleSet;
            }

            InspectionRuleSet current = config.GetInspectionRuleSet();
            normalizedJson = InspectionRuleSetSerializer.Serialize(current);
            return current;
        }

        private static void ApplyTargetOverrides(
            InspectionRuleSet ruleSet,
            VisionDebugRunParameters parameters,
            AppConfig config)
        {
            string targetLabel = !string.IsNullOrWhiteSpace(parameters.TargetLabel)
                ? parameters.TargetLabel.Trim()
                : config.TargetLabel ?? string.Empty;
            int targetCount = parameters.TargetCount ?? Math.Max(0, config.TargetCount);
            if (string.IsNullOrWhiteSpace(targetLabel) && !parameters.TargetCount.HasValue)
            {
                return;
            }

            ruleSet.FallbackTargetLabel = targetLabel;
            ruleSet.FallbackTargetCount = Math.Max(0, targetCount);
            InspectionRule? countRule = ruleSet.Rules
                .FirstOrDefault(rule => string.Equals(rule.Type, InspectionRuleTypes.Count, StringComparison.OrdinalIgnoreCase));
            if (countRule == null)
            {
                return;
            }

            countRule.Label = targetLabel;
            countRule.Operator = InspectionRuleOperators.Equal;
            countRule.Count = Math.Max(0, targetCount);
        }

        public static void ApplySavedParameters(AppConfig config, VisionDebugRunParameters parameters)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            config.Confidence = ResolveConfidence(config, parameters);
            config.IouThreshold = ResolveIou(config, parameters);

            InspectionRuleSet ruleSet = ResolveRuleSet(config, parameters, out string normalizedJson);
            config.InspectionRuleSetJson = normalizedJson;

            InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
            string targetLabel = !string.IsNullOrWhiteSpace(parameters.TargetLabel)
                ? parameters.TargetLabel.Trim()
                : fallbackGoal?.TargetLabel ?? config.TargetLabel ?? string.Empty;
            int targetCount = parameters.TargetCount ?? fallbackGoal?.TargetCount ?? config.TargetCount;

            config.TargetLabel = targetLabel;
            config.TargetCount = Math.Max(0, targetCount);
        }

        public static VisionDebugParameterComparison BuildParameterComparison(
            AppConfig config,
            VisionDebugRunParameters? parameters,
            string trialRuleSetJson,
            bool productionRoiEnabled)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            parameters ??= new VisionDebugRunParameters();

            InspectionRuleSet productionRuleSet = config.GetInspectionRuleSet();
            InspectionRuleSet trialRuleSet = InspectionRuleSetSerializer.DeserializeOrDefault(trialRuleSetJson);
            InspectionFallbackGoal? productionGoal = InspectionRuleEngine.GetFallbackGoal(productionRuleSet);
            InspectionFallbackGoal? trialGoal = InspectionRuleEngine.GetFallbackGoal(trialRuleSet);
            string productionRuleSetJson = InspectionRuleSetSerializer.Serialize(productionRuleSet);
            string normalizedTrialRuleSetJson = InspectionRuleSetSerializer.Serialize(trialRuleSet);

            float trialConfidence = ResolveConfidence(config, parameters);
            float trialIou = ResolveIou(config, parameters);
            string productionTargetLabel = productionGoal?.TargetLabel ?? config.TargetLabel ?? string.Empty;
            int productionTargetCount = productionGoal?.TargetCount ?? Math.Max(0, config.TargetCount);
            string trialTargetLabel = trialGoal?.TargetLabel ?? parameters.TargetLabel ?? string.Empty;
            int trialTargetCount = trialGoal?.TargetCount ?? parameters.TargetCount ?? 0;

            var comparison = new VisionDebugParameterComparison();
            AddDiff(
                comparison,
                "confidence",
                "confidence",
                FormatFloat(config.Confidence),
                FormatFloat(trialConfidence),
                Math.Abs(config.Confidence - trialConfidence) > 0.0001f);
            AddDiff(
                comparison,
                "iou",
                "iou",
                FormatFloat(config.IouThreshold),
                FormatFloat(trialIou),
                Math.Abs(config.IouThreshold - trialIou) > 0.0001f);
            AddDiff(
                comparison,
                "targetLabel",
                "targetLabel",
                EmptyText(productionTargetLabel),
                EmptyText(trialTargetLabel),
                !string.Equals(productionTargetLabel, trialTargetLabel, StringComparison.OrdinalIgnoreCase));
            AddDiff(
                comparison,
                "targetCount",
                "targetCount",
                productionTargetCount.ToString(CultureInfo.InvariantCulture),
                trialTargetCount.ToString(CultureInfo.InvariantCulture),
                productionTargetCount != trialTargetCount);
            AddDiff(
                comparison,
                "ruleSet",
                "ruleSet",
                FormatRuleSetSummary(productionRuleSet),
                FormatRuleSetSummary(trialRuleSet),
                !string.Equals(productionRuleSetJson, normalizedTrialRuleSetJson, StringComparison.Ordinal));
            AddDiff(
                comparison,
                "preprocessingMode",
                "preprocessingMode",
                YoloPreprocessingMode.StandardLetterBox.ToString(),
                parameters.PreprocessingMode.ToString(),
                parameters.PreprocessingMode != YoloPreprocessingMode.StandardLetterBox);
            AddDiff(
                comparison,
                "roiEnabled",
                "ROI 开关",
                productionRoiEnabled ? "启用" : "关闭",
                parameters.RoiEnabled ? "启用" : "关闭",
                productionRoiEnabled != parameters.RoiEnabled);

            return comparison;
        }

        public static void ValidatePreprocessingMode(YoloPreprocessingMode mode)
        {
            if (!Enum.IsDefined(typeof(YoloPreprocessingMode), mode))
            {
                throw new NotSupportedException($"预处理模式 {mode} 不支持当前模型或运行时");
            }
        }

        private static IEnumerable<string> ResolveTemplateLabels(
            VisionDebugRunParameters parameters,
            TemplateGoal goal)
        {
            if (parameters.Labels != null && parameters.Labels.Count > 0)
            {
                return parameters.Labels;
            }

            return string.IsNullOrWhiteSpace(goal.TargetLabel)
                ? Array.Empty<string>()
                : new[] { goal.TargetLabel };
        }

        private static TemplateGoal ResolveTemplateGoal(AppConfig config, VisionDebugRunParameters parameters)
        {
            if (TryResolveProjectPresetGoal(parameters.TemplateId, out TemplateGoal presetGoal))
            {
                return presetGoal;
            }

            string parameterLabel = parameters.TargetLabel?.Trim() ?? string.Empty;
            int parameterCount = parameters.TargetCount ?? 0;
            if (!string.IsNullOrWhiteSpace(parameterLabel) || parameterCount > 0)
            {
                return new TemplateGoal(parameterLabel, Math.Max(0, parameterCount));
            }

            InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(config.GetInspectionRuleSet());
            return new TemplateGoal(
                fallbackGoal?.TargetLabel ?? config.TargetLabel ?? string.Empty,
                fallbackGoal?.TargetCount ?? Math.Max(0, config.TargetCount));
        }

        private static bool TryResolveProjectPresetGoal(string templateId, out TemplateGoal goal)
        {
            goal = default;
            string presetId = ResolvePresetIdForTemplate(templateId);
            if (string.IsNullOrWhiteSpace(presetId))
            {
                return false;
            }

            try
            {
                JsonObject presets = ProjectPresetStore.Load().Presets;
                if (presets[presetId] is not JsonObject preset)
                {
                    return false;
                }

                string label = preset["TargetLabel"]?.GetValue<string>()?.Trim() ?? string.Empty;
                int count = preset["TargetCount"]?.GetValue<int>() ?? 0;
                if (string.IsNullOrWhiteSpace(label) && count <= 0)
                {
                    return false;
                }

                goal = new TemplateGoal(label, Math.Max(0, count));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolvePresetIdForTemplate(string templateId)
        {
            string normalized = templateId?.Trim().ToLowerInvariant() ?? string.Empty;
            return normalized switch
            {
                InspectionRuleSetTemplateIds.W5ScrewCount => "W5_screw",
                InspectionRuleSetTemplateIds.W6ScrewCount => "W6_screw",
                InspectionRuleSetTemplateIds.N5RemoteMissingPart => "N5_remote",
                InspectionRuleSetTemplateIds.N6RemoteMissingPart => "N6_remote",
                InspectionRuleSetTemplateIds.ElectricHeatingScrewCount => "W6_screw",
                _ => string.Empty
            };
        }

        private static void AddDiff(
            VisionDebugParameterComparison comparison,
            string field,
            string displayName,
            string productionValue,
            string trialValue,
            bool isDifferent)
        {
            comparison.Items.Add(new VisionDebugParameterDiff
            {
                Field = field,
                DisplayName = displayName,
                ProductionValue = productionValue,
                TrialValue = trialValue,
                IsDifferent = isDifferent
            });
        }

        private static string FormatFloat(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private static string EmptyText(string value) => string.IsNullOrWhiteSpace(value) ? "(未设置)" : value.Trim();

        private static string FormatRuleSetSummary(InspectionRuleSet ruleSet)
        {
            InspectionFallbackGoal? goal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
            string target = goal == null
                ? string.Empty
                : $"；目标 {EmptyText(goal.TargetLabel)} x{goal.TargetCount}";
            return $"规则 {ruleSet.Rules.Count(rule => rule.Enabled)} 条{target}";
        }

        private readonly record struct TemplateGoal(string TargetLabel, int TargetCount);
    }
}
