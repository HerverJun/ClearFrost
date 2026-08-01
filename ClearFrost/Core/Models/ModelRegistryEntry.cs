using System;
using System.Collections.Generic;
using System.Linq;

namespace ClearFrost.Core.Models
{
    public enum ModelRegistryStatus
    {
        Ready,
        Warning,
        Blocked
    }

    public sealed class ModelRegistryEntry
    {
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ModelHash { get; init; } = string.Empty;
        public string UsedModelName { get; init; } = string.Empty;
        public string ModelPath { get; init; } = string.Empty;
        public string ManifestPath { get; init; } = string.Empty;
        public bool IsPackage { get; init; }
        public ModelRegistryStatus Status { get; init; } = ModelRegistryStatus.Warning;
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<string> Labels { get; init; } = new List<string>();
        public ModelPackageManifest? Manifest { get; init; }
        public string TaskType { get; init; } = string.Empty;
        public string PostprocessorKey { get; init; } = string.Empty;
        public string ScoreNormalization { get; init; } = string.Empty;
        public IReadOnlyDictionary<string, string> PostprocessOptions { get; init; } = new Dictionary<string, string>();
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public string ApprovalStatus { get; init; } = ModelApprovalStatuses.Pending;
        public bool ApprovedForProduction { get; init; }

        public string GetEffectiveTaskType()
        {
            return !string.IsNullOrWhiteSpace(TaskType)
                ? TaskType
                : Manifest?.TaskType ?? string.Empty;
        }

        public string GetEffectivePostprocessorKey()
        {
            return !string.IsNullOrWhiteSpace(PostprocessorKey)
                ? PostprocessorKey
                : Manifest?.PostprocessorKey ?? string.Empty;
        }

        public string GetEffectiveScoreNormalization()
        {
            return !string.IsNullOrWhiteSpace(ScoreNormalization)
                ? ScoreNormalization
                : Manifest?.ScoreNormalization ?? string.Empty;
        }

        public IReadOnlyList<string> GetEffectiveLabels()
        {
            if (Labels != null && Labels.Any(label => !string.IsNullOrWhiteSpace(label)))
            {
                return Labels;
            }

            return Manifest?.Labels != null
                ? Manifest.Labels
                : Array.Empty<string>();
        }

        public int GetEffectiveInputWidth()
        {
            return InputWidth > 0
                ? InputWidth
                : Manifest?.InputWidth ?? 0;
        }

        public int GetEffectiveInputHeight()
        {
            return InputHeight > 0
                ? InputHeight
                : Manifest?.InputHeight ?? 0;
        }

        public IReadOnlyDictionary<string, string>? GetEffectivePostprocessOptions()
        {
            return PostprocessOptions != null && PostprocessOptions.Count > 0
                ? PostprocessOptions
                : Manifest?.PostprocessOptions;
        }
    }

    public sealed class ModelProductionValidationResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public ModelRegistryEntry? Entry { get; init; }
        public string NormalizedModelPath { get; init; } = string.Empty;
        public string ActualSha256 { get; init; } = string.Empty;

        public static ModelProductionValidationResult Ok(
            ModelRegistryEntry entry,
            string normalizedModelPath,
            string actualSha256)
        {
            return new ModelProductionValidationResult
            {
                Succeeded = true,
                Entry = entry,
                NormalizedModelPath = normalizedModelPath ?? string.Empty,
                ActualSha256 = actualSha256 ?? string.Empty
            };
        }

        public static ModelProductionValidationResult Fail(string errorCode, string message)
        {
            return new ModelProductionValidationResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty
            };
        }
    }
}
