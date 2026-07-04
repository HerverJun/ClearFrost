// ============================================================================
// 文件名: VisionDebugParameterService.cs
// 描述:   视觉算法调试参数解析、试运行与保存辅助
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
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
                InspectionRuleSet template = InspectionRuleSetTemplates.Create(
                    parameters.TemplateId,
                    parameters.Labels,
                    parameters.TargetLabel,
                    parameters.TargetCount ?? 0);
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

        public static void ValidatePreprocessingMode(YoloPreprocessingMode mode)
        {
            if (!Enum.IsDefined(typeof(YoloPreprocessingMode), mode))
            {
                throw new NotSupportedException($"预处理模式 {mode} 不支持当前模型或运行时");
            }
        }
    }
}
