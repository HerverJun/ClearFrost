using System;
using System.Collections.Generic;

namespace ClearFrost.Core.Models
{
    public sealed class ModelPackageManifest
    {
        public string ModelId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = "model.onnx";
        public string ModelHash { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new List<string>();
        public string TaskType { get; set; } = string.Empty;
        public int InputWidth { get; set; }
        public int InputHeight { get; set; }
        public string AcceptanceDataset { get; set; } = string.Empty;
        public Dictionary<string, double> AcceptanceMetrics { get; set; } = new Dictionary<string, double>();
        public ModelApprovalMetadata Approval { get; set; } = new ModelApprovalMetadata();
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public string Description { get; set; } = string.Empty;

        public string EffectiveHash =>
            !string.IsNullOrWhiteSpace(ModelHash) ? ModelHash.Trim() : Sha256.Trim();
    }

    public sealed class ModelApprovalMetadata
    {
        public string Status { get; set; } = ModelApprovalStatuses.Pending;
        public DateTimeOffset? ApprovedAt { get; set; }
        public string ApprovedBy { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string GoldenDatasetPath { get; set; } = string.Empty;
        public double MinimumPassRate { get; set; }
        public double ActualPassRate { get; set; }
        public string ReplayEvidenceId { get; set; } = string.Empty;
        public string ReplayEvidenceHash { get; set; } = string.Empty;
        public string ReplayDatasetHash { get; set; } = string.Empty;
        public string ReplayRunId { get; set; } = string.Empty;
        public ModelApprovalLegacyMigration? LegacyMigration { get; set; }
    }

    public sealed class ModelApprovalLegacyMigration
    {
        public string MigrationId { get; set; } = string.Empty;
        public string ModelRole { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ModelHash { get; set; } = string.Empty;
        public string ManifestHash { get; set; } = string.Empty;
        public string ConfigReference { get; set; } = string.Empty;
        public DateTimeOffset MigratedAt { get; set; }
    }

    public static class ModelApprovalStatuses
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string Legacy = "Legacy";
    }
}
