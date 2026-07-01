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
        public const string Preparing = "Preparing";
        public const string BaselineRunning = "BaselineRunning";
        public const string CandidateRunning = "CandidateRunning";
        public const string Reporting = "Reporting";
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Canceled = "Canceled";
        public const string Interrupted = "Interrupted";
    }

    public static class ReplayReviewDispositions
    {
        public const string Confirmed = "Confirmed";
        public const string FalseReject = "FalseReject";
        public const string MissedDetection = "MissedDetection";
        public const string Pending = "Pending";
        public const string InvalidSample = "InvalidSample";
        public const string Invalid = InvalidSample;

        public static bool IsDatasetEligible(string value)
        {
            return string.Equals(value, Confirmed, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, FalseReject, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, MissedDetection, StringComparison.OrdinalIgnoreCase);
        }
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
        public string SystemDecision { get; set; } = ReplayDecisions.OK;
        public string Disposition { get; set; } = ReplayReviewDispositions.Pending;
        public string ReviewerId { get; set; } = string.Empty;
        public string ReviewerRole { get; set; } = string.Empty;
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
        public string SourceRecordHash { get; set; } = string.Empty;
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
        public IReadOnlyDictionary<long, ReplayManualReviewRecord> ManualReviewsByDetectionRecordId { get; init; } =
            new Dictionary<long, ReplayManualReviewRecord>();
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
        public long BaselineElapsedMs { get; set; }
        public long CandidateElapsedMs { get; set; }
        public string BaselineRuleSummary { get; set; } = string.Empty;
        public string CandidateRuleSummary { get; set; } = string.Empty;
        public bool IsValid { get; set; } = true;
        public string InvalidReason { get; set; } = string.Empty;
    }

    public sealed class ReplayComparisonMetrics
    {
        public int SampleCount { get; set; }
        public int TotalSampleCount { get; set; }
        public int ValidSampleCount { get; set; }
        public int CandidateNewMissedDetectionCount { get; set; }
        public int CandidateFixedMissedDetectionCount { get; set; }
        public int CandidateNewFalseRejectCount { get; set; }
        public int CandidateFixedFalseRejectCount { get; set; }
        public int BaselineMissedDetectionCount { get; set; }
        public double BaselineMissedDetectionRate { get; set; }
        public int CandidateMissedDetectionCount { get; set; }
        public double CandidateMissedDetectionRate { get; set; }
        public int BaselineFalseRejectCount { get; set; }
        public double BaselineFalseRejectRate { get; set; }
        public int CandidateFalseRejectCount { get; set; }
        public double CandidateFalseRejectRate { get; set; }
        public double FalseRejectRateIncrease { get; set; }
        public int ChangedDecisionCount { get; set; }
        public int InvalidSampleCount { get; set; }
        public int BaselineCorrectCount { get; set; }
        public int CandidateCorrectCount { get; set; }
        public double BaselineAccuracy { get; set; }
        public double CandidateAccuracy { get; set; }
        public long BaselineP95ElapsedMs { get; set; }
        public long CandidateP95ElapsedMs { get; set; }
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
        public string ReportHash { get; set; } = string.Empty;
        public int PolicyVersion { get; set; }
        public string RecipeHash { get; set; } = string.Empty;
        public string RuleSetHash { get; set; } = string.Empty;
        public string PolicyHash { get; set; } = string.Empty;
        public ReplayAcceptancePolicyOptions PolicySnapshot { get; set; } = ReplayAcceptancePolicyOptions.ProductionDefault();
        public string BaselineModelHash { get; set; } = string.Empty;
        public string CandidateModelHash { get; set; } = string.Empty;
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
        public string ReplayReportPath { get; set; } = string.Empty;
        public int PolicyVersion { get; set; }
        public string PolicyHash { get; set; } = string.Empty;
        public ReplayAcceptancePolicyOptions PolicySnapshot { get; set; } = ReplayAcceptancePolicyOptions.ProductionDefault();
        public string RecipeHash { get; set; } = string.Empty;
        public string RuleSetHash { get; set; } = string.Empty;
        public string BaselineModelHash { get; set; } = string.Empty;
        public string CandidateModelHash { get; set; } = string.Empty;
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

        Task<IReadOnlyList<ReplayDatasetSummary>> ListSnapshotsAsync(
            CancellationToken cancellationToken = default);

        Task<ReplayDatasetArchiveResult> ArchiveSnapshotAsync(
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

        Task<ReplayRunReport> LoadReportAsync(
            string runId,
            CancellationToken cancellationToken = default);

        Task<ReplayRunRecord?> LoadRunRecordAsync(
            string runId,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ReplayRunRecord>> ListRunRecordsAsync(
            int limit = 100,
            CancellationToken cancellationToken = default);

        Task RecordRunFailedAsync(
            string runId,
            string message,
            CancellationToken cancellationToken = default);

        Task RecordRunCanceledAsync(
            string runId,
            CancellationToken cancellationToken = default);

        Task MarkNonTerminalRunsInterruptedAsync(
            string stationId,
            CancellationToken cancellationToken = default);
    }

    public interface IModelApprovalEvidenceStore
    {
        ModelApprovalEvidence SaveEvidence(
            ReplayRunReport report,
            string approvedBy,
            string datasetPath,
            string policyHash);

        ModelApprovalEvidenceValidationResult ValidateEvidence(
            ReplayModelIdentity candidate,
            string evidenceId,
            string expectedEvidenceHash,
            IReplayDatasetStore datasetStore,
            IReplayRunStore runStore);

        ModelApprovalEvidence? LoadEvidence(string evidenceId);

        IReadOnlyList<ModelApprovalEvidence> ListEvidence();
    }

    public sealed class ReplayApprovalRequest
    {
        public string RunId { get; init; } = string.Empty;
        public string CandidateModelId { get; init; } = string.Empty;
        public string CandidateVersion { get; init; } = string.Empty;
        public string CandidateSha256 { get; init; } = string.Empty;
        public ReplayRunReport Report { get; init; } = new ReplayRunReport();
        public ModelRegistryEntry CandidateEntry { get; init; } = new ModelRegistryEntry();
        public string ApprovedBy { get; init; } = string.Empty;
        public string ApprovedByRole { get; init; } = string.Empty;
        public string DatasetPath { get; init; } = string.Empty;
    }

    public sealed class ReplayApprovalResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public bool IsFaulted { get; init; }
        public ModelApprovalEvidence? Evidence { get; init; }
        public IReadOnlyList<string> CompensationFailures { get; init; } = Array.Empty<string>();

        public static ReplayApprovalResult Ok(ModelApprovalEvidence evidence, string message)
        {
            return new ReplayApprovalResult
            {
                Succeeded = true,
                Evidence = evidence,
                Message = message ?? string.Empty
            };
        }

        public static ReplayApprovalResult Fail(
            string errorCode,
            string message,
            bool isFaulted = false,
            IReadOnlyList<string>? compensationFailures = null)
        {
            return new ReplayApprovalResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty,
                IsFaulted = isFaulted,
                CompensationFailures = compensationFailures ?? Array.Empty<string>()
            };
        }
    }

    internal static class ReplayMetrics
    {
        public static ReplayComparisonMetrics Compute(IReadOnlyList<ReplaySampleComparison> samples)
        {
            samples ??= Array.Empty<ReplaySampleComparison>();
            List<ReplaySampleComparison> validSamples = samples
                .Where(sample => sample.IsValid)
                .ToList();
            int groundTruthNgCount = validSamples.Count(sample => IsNg(sample.GroundTruth));
            int groundTruthOkCount = validSamples.Count(sample => IsOk(sample.GroundTruth));
            int baselineMissedDetectionCount = validSamples.Count(sample => IsNg(sample.GroundTruth) && IsOk(sample.BaselineDecision));
            int candidateMissedDetectionCount = validSamples.Count(sample => IsNg(sample.GroundTruth) && IsOk(sample.CandidateDecision));
            int baselineFalseRejectCount = validSamples.Count(sample => IsOk(sample.GroundTruth) && IsNg(sample.BaselineDecision));
            int candidateFalseRejectCount = validSamples.Count(sample => IsOk(sample.GroundTruth) && IsNg(sample.CandidateDecision));

            return new ReplayComparisonMetrics
            {
                SampleCount = validSamples.Count,
                TotalSampleCount = samples.Count,
                ValidSampleCount = validSamples.Count,
                CandidateNewMissedDetectionCount = validSamples.Count(sample => IsNewMissedDetection(sample)),
                CandidateFixedMissedDetectionCount = validSamples.Count(sample => IsFixedMissedDetection(sample)),
                CandidateNewFalseRejectCount = validSamples.Count(sample => IsNewFalseReject(sample)),
                CandidateFixedFalseRejectCount = validSamples.Count(sample => IsFixedFalseReject(sample)),
                BaselineMissedDetectionCount = baselineMissedDetectionCount,
                BaselineMissedDetectionRate = Rate(baselineMissedDetectionCount, groundTruthNgCount),
                CandidateMissedDetectionCount = candidateMissedDetectionCount,
                CandidateMissedDetectionRate = Rate(candidateMissedDetectionCount, groundTruthNgCount),
                BaselineFalseRejectCount = baselineFalseRejectCount,
                BaselineFalseRejectRate = Rate(baselineFalseRejectCount, groundTruthOkCount),
                CandidateFalseRejectCount = candidateFalseRejectCount,
                CandidateFalseRejectRate = Rate(candidateFalseRejectCount, groundTruthOkCount),
                FalseRejectRateIncrease = Rate(candidateFalseRejectCount, groundTruthOkCount) - Rate(baselineFalseRejectCount, groundTruthOkCount),
                ChangedDecisionCount = validSamples.Count(sample => sample.DecisionChanged),
                InvalidSampleCount = samples.Count(sample => !sample.IsValid),
                BaselineCorrectCount = validSamples.Count(sample => IsCorrect(sample.GroundTruth, sample.BaselineDecision)),
                CandidateCorrectCount = validSamples.Count(sample => IsCorrect(sample.GroundTruth, sample.CandidateDecision)),
                BaselineAccuracy = validSamples.Count == 0 ? 0 : validSamples.Count(sample => IsCorrect(sample.GroundTruth, sample.BaselineDecision)) / (double)validSamples.Count,
                CandidateAccuracy = validSamples.Count == 0 ? 0 : validSamples.Count(sample => IsCorrect(sample.GroundTruth, sample.CandidateDecision)) / (double)validSamples.Count,
                BaselineP95ElapsedMs = Percentile95(validSamples.Select(sample => sample.BaselineElapsedMs)),
                CandidateP95ElapsedMs = Percentile95(validSamples.Select(sample => sample.CandidateElapsedMs))
            };
        }

        public static string Classify(ReplaySampleComparison sample)
        {
            if (!sample.IsValid) return "InvalidSample";
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

        private static double Rate(int numerator, int denominator)
        {
            return denominator <= 0 ? 0 : numerator / (double)denominator;
        }

        internal static bool TryNormalizeDecision(string value, out string normalized)
        {
            if (string.Equals(value, ReplayDecisions.OK, StringComparison.OrdinalIgnoreCase))
            {
                normalized = ReplayDecisions.OK;
                return true;
            }

            if (string.Equals(value, ReplayDecisions.NG, StringComparison.OrdinalIgnoreCase))
            {
                normalized = ReplayDecisions.NG;
                return true;
            }

            normalized = string.Empty;
            return false;
        }

        internal static string Normalize(string value)
        {
            return TryNormalizeDecision(value, out string normalized)
                ? normalized
                : throw new InvalidOperationException($"Unknown replay decision: {value}");
        }

        private static long Percentile95(IEnumerable<long> values)
        {
            List<long> ordered = values
                .Where(value => value >= 0)
                .OrderBy(value => value)
                .ToList();
            if (ordered.Count == 0)
            {
                return 0;
            }

            int index = (int)Math.Ceiling(ordered.Count * 0.95d) - 1;
            index = Math.Clamp(index, 0, ordered.Count - 1);
            return ordered[index];
        }
    }

    public sealed class ReplayDatasetSummary
    {
        public string DatasetId { get; init; } = string.Empty;
        public string DatasetHash { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public int SampleCount { get; init; }
        public string RootDirectory { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }

    public sealed class ReplayDatasetArchiveResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string ArchivePath { get; init; } = string.Empty;

        public static ReplayDatasetArchiveResult Ok(string archivePath)
        {
            return new ReplayDatasetArchiveResult
            {
                Succeeded = true,
                Message = "Replay dataset archived.",
                ArchivePath = archivePath ?? string.Empty
            };
        }

        public static ReplayDatasetArchiveResult Fail(string errorCode, string message)
        {
            return new ReplayDatasetArchiveResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty
            };
        }
    }

    public sealed class ReplayRunRecord
    {
        public string RunId { get; init; } = string.Empty;
        public string DatasetId { get; init; } = string.Empty;
        public string DatasetHash { get; init; } = string.Empty;
        public string BaselineModelId { get; init; } = string.Empty;
        public string CandidateModelId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string Message { get; init; } = string.Empty;
        public string ReportJsonPath { get; init; } = string.Empty;
        public string ReportCsvPath { get; init; } = string.Empty;
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
