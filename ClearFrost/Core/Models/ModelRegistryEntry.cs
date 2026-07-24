using System.Collections.Generic;

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
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public string ApprovalStatus { get; init; } = ModelApprovalStatuses.Pending;
        public bool ApprovedForProduction { get; init; }
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
