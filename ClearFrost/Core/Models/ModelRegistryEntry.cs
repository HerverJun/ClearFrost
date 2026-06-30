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
}
