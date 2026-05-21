using System;
using System.Globalization;
using ClearFrost.Config;

namespace ClearFrost.Core.Recipes
{
    /// <summary>
    /// Lightweight recipe snapshot derived from AppConfig. AppConfig remains the startup source of truth.
    /// </summary>
    public sealed class Recipe
    {
        public string RecipeId { get; set; } = "default";
        public string Version { get; set; } = "1";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public string TargetLabel { get; set; } = string.Empty;
        public int TargetCount { get; set; }
        public float Confidence { get; set; }
        public float IouThreshold { get; set; }
        public bool EnableGlobalIou { get; set; }
        public string CurrentModelFileName { get; set; } = string.Empty;
        public string Auxiliary1ModelPath { get; set; } = string.Empty;
        public string Auxiliary2ModelPath { get; set; } = string.Empty;
        public bool EnableMultiModelFallback { get; set; }
        public bool EnableGpu { get; set; }
        public int GpuIndex { get; set; }
        public int TaskType { get; set; }
        public bool EnablePreprocessing { get; set; }
        public bool IndustrialRenderMode { get; set; }
        public string InspectionRuleSetJson { get; set; } = string.Empty;
        public int VisionMode { get; set; }
        public string TemplateImagePath { get; set; } = string.Empty;
        public double TemplateThreshold { get; set; }
        public string VisionPipelineJson { get; set; } = "[]";

        public static Recipe FromAppConfig(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return new Recipe
            {
                RecipeId = "default",
                Version = DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                CreatedAt = DateTimeOffset.Now,
                TargetLabel = config.TargetLabel ?? string.Empty,
                TargetCount = config.TargetCount,
                Confidence = config.Confidence,
                IouThreshold = config.IouThreshold,
                EnableGlobalIou = config.EnableGlobalIou,
                CurrentModelFileName = config.CurrentModelFileName ?? string.Empty,
                Auxiliary1ModelPath = config.Auxiliary1ModelPath ?? string.Empty,
                Auxiliary2ModelPath = config.Auxiliary2ModelPath ?? string.Empty,
                EnableMultiModelFallback = config.EnableMultiModelFallback,
                EnableGpu = config.EnableGpu,
                GpuIndex = config.GpuIndex,
                TaskType = config.TaskType,
                EnablePreprocessing = config.EnablePreprocessing,
                IndustrialRenderMode = config.IndustrialRenderMode,
                InspectionRuleSetJson = config.InspectionRuleSetJson ?? string.Empty,
                VisionMode = config.VisionMode,
                TemplateImagePath = config.TemplateImagePath ?? string.Empty,
                TemplateThreshold = config.TemplateThreshold,
                VisionPipelineJson = config.VisionPipelineJson ?? "[]"
            };
        }
    }
}
