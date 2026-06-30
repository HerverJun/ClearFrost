using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Models;
using ClearFrost.Core.Rules;
using ClearFrost.Interfaces;

namespace ClearFrost.Services.Replay
{
    public static class ReplayDecisions
    {
        public const string OK = "OK";
        public const string NG = "NG";
    }

    public static class ReplayRunStatuses
    {
        public const string Pending = "Pending";
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Canceled = "Canceled";
    }

    public sealed class ReplayRecipeSnapshot
    {
        public string RecipeId { get; set; } = "default";
        public string RecipeVersion { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public float IouThreshold { get; set; }
        public float[]? Roi { get; set; }
        public InspectionRuleSet RuleSet { get; set; } = new InspectionRuleSet();
        public string RuleSetJson { get; set; } = string.Empty;

        public InspectionRuleSet GetRuleSet()
        {
            if (RuleSet != null && RuleSet.Rules.Count > 0)
            {
                return RuleSet;
            }

            if (string.IsNullOrWhiteSpace(RuleSetJson))
            {
                return new InspectionRuleSet();
            }

            return JsonSerializer.Deserialize<InspectionRuleSet>(RuleSetJson, ReplayJson.Options) ?? new InspectionRuleSet();
        }
    }

    public sealed class ReplayModelIdentity
    {
        public string ModelId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string ModelPath { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public IReadOnlyList<string> Labels { get; set; } = Array.Empty<string>();
        public string TaskType { get; set; } = string.Empty;
        public int InputWidth { get; set; }
        public int InputHeight { get; set; }
        public string ApprovalStatus { get; set; } = ModelApprovalStatuses.Pending;
        public bool IsPackage { get; set; } = true;

        public static ReplayModelIdentity FromRegistryEntry(ModelRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            return new ReplayModelIdentity
            {
                ModelId = entry.ModelId ?? string.Empty,
                Version = entry.Version ?? string.Empty,
                Sha256 = entry.ModelHash ?? string.Empty,
                ModelPath = entry.ModelPath ?? string.Empty,
                ManifestPath = entry.ManifestPath ?? string.Empty,
                Labels = entry.Labels?.Where(label => !string.IsNullOrWhiteSpace(label)).ToArray() ?? Array.Empty<string>(),
                TaskType = entry.TaskType ?? string.Empty,
                InputWidth = entry.InputWidth,
                InputHeight = entry.InputHeight,
                ApprovalStatus = entry.ApprovalStatus ?? ModelApprovalStatuses.Pending,
                IsPackage = entry.IsPackage
            };
        }

        public string IdentityKey => $"{ModelId}|{Version}|{Sha256}".ToLowerInvariant();
    }

    public sealed class ReplayManualReviewRecord
    {
        public string SampleId { get; set; } = string.Empty;
        public string InspectionId { get; set; } = string.Empty;
        public string GroundTruth { get; set; } = ReplayDecisions.OK;
        public string ReviewerId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public DateTimeOffset ReviewedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Notes { get; set; } = string.Empty;
    }

    public sealed class ReplayDatasetSample
    {
        public string SampleId { get; set; } = string.Empty;
        public long DetectionRecordId { get; set; }
        public string InspectionId { get; set; } = string.Empty;
        public string SourceImagePath { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public string ImageHash { get; set; } = string.Empty;
        public string GroundTruth { get; set; } = ReplayDecisions.OK;
        public string SystemDecision { get; set; } = ReplayDecisions.OK;
        public string RecipeId { get; set; } = string.Empty;
        public string RecipeVersion { get; set; } = string.Empty;
        public long ReviewRevision { get; set; }
        public ReplayManualReviewRecord? ManualReview { get; set; }
        public DetectionRecord Record { get; set; } = new DetectionRecord();
    }

    public sealed class ReplayDatasetSnapshot
    {
        public string DatasetId { get; set; } = string.Empty;
        public string DatasetHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string RootDirectory { get; set; } = string.Empty;
        public ReplayRecipeSnapshot Recipe { get; set; } = new ReplayRecipeSnapshot();
        public ReplayModelIdentity BaselineModel { get; set; } = new ReplayModelIdentity();
        public ReplayModelIdentity CandidateModel { get; set; } = new ReplayModelIdentity();
        public IReadOnlyList<ReplayDatasetSample> Samples { get; set; } = Array.Empty<ReplayDatasetSample>();
    }

    public sealed class ReplayDatasetCreateRequest
    {
        public string DatasetId { get; init; } = string.Empty;
        public DetectionReplayQuery Query { get; init; } = new DetectionReplayQuery();
        public ReplayRecipeSnapshot Recipe { get; init; } = new ReplayRecipeSnapshot();
        public ReplayModelIdentity BaselineModel { get; init; } = new ReplayModelIdentity();
        public ReplayModelIdentity CandidateModel { get; init; } = new ReplayModelIdentity();
        public IReadOnlyDictionary<string, ReplayManualReviewRecord> ManualReviewsByInspectionId { get; init; } =
            new Dictionary<string, ReplayManualReviewRecord>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ReplayInferenceOutput
    {
        public string SampleId { get; set; } = string.Empty;
        public string InspectionId { get; set; } = string.Empty;
        public string Decision { get; set; } = ReplayDecisions.OK;
        public float Confidence { get; set; }
        public long ElapsedMs { get; set; }
        public string ModelId { get; set; } = string.Empty;
        public string ModelVersion { get; set; } = string.Empty;
        public string ModelHash { get; set; } = string.Empty;
        public string RuleSummary { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public sealed class ReplaySampleComparison
    {
        public string SampleId { get; set; } = string.Empty;
        public string InspectionId { get; set; } = string.Empty;
        public string GroundTruth { get; set; } = ReplayDecisions.OK;
        public string BaselineDecision { get; set; } = ReplayDecisions.OK;
        public string CandidateDecision { get; set; } = ReplayDecisions.OK;
        public string Classification { get; set; } = string.Empty;
        public bool DecisionChanged { get; set; }
    }

    public sealed class ReplayComparisonMetrics
    {
        public int SampleCount { get; set; }
        public int CandidateNewMissedDetectionCount { get; set; }
        public int CandidateFixedMissedDetectionCount { get; set; }
        public int CandidateNewFalseRejectCount { get; set; }
        public int CandidateFixedFalseRejectCount { get; set; }
        public int ChangedDecisionCount { get; set; }
        public int BaselineCorrectCount { get; set; }
        public int CandidateCorrectCount { get; set; }
    }

    public sealed class ReplayRunReport
    {
        public string RunId { get; set; } = string.Empty;
        public string Status { get; set; } = ReplayRunStatuses.Pending;
        public string DatasetId { get; set; } = string.Empty;
        public string DatasetHash { get; set; } = string.Empty;
        public ReplayModelIdentity BaselineModel { get; set; } = new ReplayModelIdentity();
        public ReplayModelIdentity CandidateModel { get; set; } = new ReplayModelIdentity();
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
        public ReplayComparisonMetrics Metrics { get; set; } = new ReplayComparisonMetrics();
        public IReadOnlyList<ReplaySampleComparison> Samples { get; set; } = Array.Empty<ReplaySampleComparison>();
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
        public string ReportJsonPath { get; set; } = string.Empty;
        public string ReportCsvPath { get; set; } = string.Empty;
    }

    public sealed class ReplayComparisonRequest
    {
        public string RunId { get; init; } = string.Empty;
        public string DatasetId { get; init; } = string.Empty;
        public ReplayModelIdentity BaselineModel { get; init; } = new ReplayModelIdentity();
        public ReplayModelIdentity CandidateModel { get; init; } = new ReplayModelIdentity();
    }

    public sealed class ReplayRunProgress
    {
        public string RunId { get; init; } = string.Empty;
        public string Status { get; init; } = ReplayRunStatuses.Running;
        public string Phase { get; init; } = string.Empty;
        public int CompletedSamples { get; init; }
        public int TotalSamples { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class ReplayModelValidationOptions
    {
        public bool AllowPendingApproval { get; init; }
        public bool RequireWarmup { get; init; } = true;
    }

    public sealed class ReplayModelValidationResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;

        public static ReplayModelValidationResult Ok()
        {
            return new ReplayModelValidationResult { Succeeded = true };
        }

        public static ReplayModelValidationResult Fail(string errorCode, string message)
        {
            return new ReplayModelValidationResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty
            };
        }
    }

    public sealed class ReplayApprovalDecision
    {
        public bool Approved { get; init; }
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    }

    public sealed class ModelApprovalEvidence
    {
        public string EvidenceId { get; set; } = string.Empty;
        public string EvidenceHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string ApprovedBy { get; set; } = string.Empty;
        public string DatasetId { get; set; } = string.Empty;
        public string DatasetHash { get; set; } = string.Empty;
        public string DatasetPath { get; set; } = string.Empty;
        public string ReplayRunId { get; set; } = string.Empty;
        public string ReplayReportHash { get; set; } = string.Empty;
        public ReplayModelIdentity BaselineModel { get; set; } = new ReplayModelIdentity();
        public ReplayModelIdentity CandidateModel { get; set; } = new ReplayModelIdentity();
        public ReplayComparisonMetrics Metrics { get; set; } = new ReplayComparisonMetrics();
        public IReadOnlyList<string> PolicyReasons { get; set; } = Array.Empty<string>();
    }

    public sealed class ModelApprovalEvidenceValidationResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;

        public static ModelApprovalEvidenceValidationResult Ok()
        {
            return new ModelApprovalEvidenceValidationResult { Succeeded = true };
        }

        public static ModelApprovalEvidenceValidationResult Fail(string errorCode, string message)
        {
            return new ModelApprovalEvidenceValidationResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty
            };
        }
    }

    public interface IReplayInferenceRunner
    {
        Task<IReplayInferenceSession> CreateSessionAsync(
            ReplayModelIdentity model,
            ReplayRecipeSnapshot recipe,
            CancellationToken cancellationToken = default);
    }

    public interface IReplayInferenceSession : IAsyncDisposable
    {
        ReplayModelIdentity Model { get; }

        Task<ReplayInferenceOutput> RunAsync(
            ReplayDatasetSample sample,
            CancellationToken cancellationToken = default);
    }

    public interface IReplayModelValidator
    {
        Task<ReplayModelValidationResult> ValidateAsync(
            ReplayModelIdentity model,
            ReplayModelValidationOptions options,
            CancellationToken cancellationToken = default);
    }

    public interface IReplayDatasetStore
    {
        Task<ReplayDatasetSnapshot> CreateSnapshotAsync(
            ReplayDatasetCreateRequest request,
            CancellationToken cancellationToken = default);

        Task<ReplayDatasetSnapshot> LoadSnapshotAsync(
            string datasetId,
            CancellationToken cancellationToken = default);

        Task<string> ComputeSnapshotHashAsync(
            string datasetId,
            CancellationToken cancellationToken = default);
    }

    public interface IReplayRunStore
    {
        Task RecordRunStartedAsync(
            ReplayRunReport report,
            CancellationToken cancellationToken = default);

        Task RecordRunProgressAsync(
            ReplayRunProgress progress,
            CancellationToken cancellationToken = default);

        Task<ReplayRunReport> SaveReportAsync(
            ReplayRunReport report,
            CancellationToken cancellationToken = default);

        Task RecordRunFailedAsync(
            string runId,
            string message,
            CancellationToken cancellationToken = default);

        Task RecordRunCanceledAsync(
            string runId,
            CancellationToken cancellationToken = default);
    }

    public interface IModelApprovalEvidenceStore
    {
        ModelApprovalEvidence SaveEvidence(
            ReplayRunReport report,
            string approvedBy,
            string datasetPath);

        ModelApprovalEvidenceValidationResult ValidateEvidence(
            ReplayModelIdentity candidate,
            string evidenceId,
            string expectedEvidenceHash,
            IReplayDatasetStore datasetStore);
    }

    internal static class ReplayMetrics
    {
        public static ReplayComparisonMetrics Compute(IReadOnlyList<ReplaySampleComparison> samples)
        {
            return new ReplayComparisonMetrics
            {
                SampleCount = samples.Count,
                CandidateNewMissedDetectionCount = samples.Count(sample => IsNewMissedDetection(sample)),
                CandidateFixedMissedDetectionCount = samples.Count(sample => IsFixedMissedDetection(sample)),
                CandidateNewFalseRejectCount = samples.Count(sample => IsNewFalseReject(sample)),
                CandidateFixedFalseRejectCount = samples.Count(sample => IsFixedFalseReject(sample)),
                ChangedDecisionCount = samples.Count(sample => sample.DecisionChanged),
                BaselineCorrectCount = samples.Count(sample => IsCorrect(sample.GroundTruth, sample.BaselineDecision)),
                CandidateCorrectCount = samples.Count(sample => IsCorrect(sample.GroundTruth, sample.CandidateDecision))
            };
        }

        public static string Classify(ReplaySampleComparison sample)
        {
            if (IsNewMissedDetection(sample)) return "CandidateNewMissedDetection";
            if (IsFixedMissedDetection(sample)) return "CandidateFixedMissedDetection";
            if (IsNewFalseReject(sample)) return "CandidateNewFalseReject";
            if (IsFixedFalseReject(sample)) return "CandidateFixedFalseReject";
            if (IsCorrect(sample.GroundTruth, sample.BaselineDecision) &&
                IsCorrect(sample.GroundTruth, sample.CandidateDecision))
            {
                return "BothCorrect";
            }

            return sample.DecisionChanged ? "DecisionChanged" : "BothSame";
        }

        private static bool IsNewMissedDetection(ReplaySampleComparison sample)
        {
            return IsNg(sample.GroundTruth) && IsNg(sample.BaselineDecision) && IsOk(sample.CandidateDecision);
        }

        private static bool IsFixedMissedDetection(ReplaySampleComparison sample)
        {
            return IsNg(sample.GroundTruth) && IsOk(sample.BaselineDecision) && IsNg(sample.CandidateDecision);
        }

        private static bool IsNewFalseReject(ReplaySampleComparison sample)
        {
            return IsOk(sample.GroundTruth) && IsOk(sample.BaselineDecision) && IsNg(sample.CandidateDecision);
        }

        private static bool IsFixedFalseReject(ReplaySampleComparison sample)
        {
            return IsOk(sample.GroundTruth) && IsNg(sample.BaselineDecision) && IsOk(sample.CandidateDecision);
        }

        private static bool IsCorrect(string groundTruth, string decision)
        {
            return string.Equals(Normalize(groundTruth), Normalize(decision), StringComparison.Ordinal);
        }

        private static bool IsOk(string value)
        {
            return string.Equals(Normalize(value), ReplayDecisions.OK, StringComparison.Ordinal);
        }

        private static bool IsNg(string value)
        {
            return string.Equals(Normalize(value), ReplayDecisions.NG, StringComparison.Ordinal);
        }

        internal static string Normalize(string value)
        {
            return string.Equals(value, ReplayDecisions.NG, StringComparison.OrdinalIgnoreCase)
                ? ReplayDecisions.NG
                : ReplayDecisions.OK;
        }
    }

    internal static class ReplayJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }
}
