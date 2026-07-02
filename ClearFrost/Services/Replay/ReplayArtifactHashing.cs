// ============================================================================
// File: ReplayArtifactHashing.cs
// Description: Frozen versioned Replay Report/Evidence/Policy hash contracts
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClearFrost.Services.Replay
{
    internal static class ReplayArtifactHashing
    {
        public static string ComputeReportHash(ReplayRunReport report)
        {
            int version = ValidateReportVersion(report);
            return version switch
            {
                1 => ComputeReportHashV1(report),
                2 => ComputeReportHashV2(report),
                _ => throw UnsupportedVersion(version, "Replay report")
            };
        }

        public static string ComputeReportHashV1(ReplayRunReport report)
        {
            ValidateReportVersion(report, expectedVersion: 1);
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                V1ReportDto.From(report),
                ReplayJson.Options));
        }

        public static string ComputeReportHashV2(ReplayRunReport report)
        {
            ValidateReportVersion(report, expectedVersion: 2);
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                V2ReportDto.From(report),
                ReplayJson.Options));
        }

        public static string ComputeEvidenceHash(ModelApprovalEvidence evidence)
        {
            int version = ValidateEvidenceVersion(evidence);
            return version switch
            {
                1 => ComputeEvidenceHashV1(evidence),
                2 => ComputeEvidenceHashV2(evidence),
                _ => throw UnsupportedVersion(version, "Replay evidence")
            };
        }

        public static string ComputeEvidenceHashV1(ModelApprovalEvidence evidence)
        {
            ValidateEvidenceVersion(evidence, expectedVersion: 1);
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                V1EvidenceDto.From(evidence),
                ReplayJson.Options));
        }

        public static string ComputeEvidenceHashV2(ModelApprovalEvidence evidence)
        {
            ValidateEvidenceVersion(evidence, expectedVersion: 2);
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                V2EvidenceDto.From(evidence),
                ReplayJson.Options));
        }

        public static string ComputePolicyHash(ReplayAcceptancePolicyOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return options.Version switch
            {
                1 => ComputePolicyHashV1(options),
                2 => ComputePolicyHashV2(options),
                _ => throw UnsupportedVersion(options.Version, "Replay policy")
            };
        }

        public static string ComputePolicyHashV1(ReplayAcceptancePolicyOptions options)
        {
            ValidatePolicyVersion(options, expectedVersion: 1);
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                PolicyDto.From(options),
                ReplayJson.Options));
        }

        public static string ComputePolicyHashV2(ReplayAcceptancePolicyOptions options)
        {
            ValidatePolicyVersion(options, expectedVersion: 2);
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                PolicyDto.From(options),
                ReplayJson.Options));
        }

        public static bool TryComputeReportHash(ReplayRunReport report, out string hash, out string error)
        {
            try
            {
                hash = ComputeReportHash(report);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                hash = string.Empty;
                error = ex.Message;
                return false;
            }
        }

        public static bool TryComputeEvidenceHash(ModelApprovalEvidence evidence, out string hash, out string error)
        {
            try
            {
                hash = ComputeEvidenceHash(evidence);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                hash = string.Empty;
                error = ex.Message;
                return false;
            }
        }

        public static bool TryComputePolicyHash(ReplayAcceptancePolicyOptions options, out string hash, out string error)
        {
            try
            {
                hash = ComputePolicyHash(options);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                hash = string.Empty;
                error = ex.Message;
                return false;
            }
        }

        private static int ValidateReportVersion(ReplayRunReport report, int? expectedVersion = null)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            ValidatePolicySnapshot(report.PolicyVersion, report.PolicySnapshot, "Replay report", expectedVersion);
            return report.PolicyVersion;
        }

        private static int ValidateEvidenceVersion(ModelApprovalEvidence evidence, int? expectedVersion = null)
        {
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            ValidatePolicySnapshot(evidence.PolicyVersion, evidence.PolicySnapshot, "Replay evidence", expectedVersion);
            return evidence.PolicyVersion;
        }

        private static void ValidatePolicyVersion(ReplayAcceptancePolicyOptions options, int expectedVersion)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Version != expectedVersion)
            {
                throw new InvalidOperationException($"Replay policy version {options.Version} does not match hash algorithm v{expectedVersion}.");
            }

            if (!ReplayAcceptancePolicyOptions.IsSupportedVersion(options.Version))
            {
                throw UnsupportedVersion(options.Version, "Replay policy");
            }
        }

        private static void ValidatePolicySnapshot(
            int version,
            ReplayAcceptancePolicyOptions? snapshot,
            string artifactName,
            int? expectedVersion)
        {
            if (version <= 0)
            {
                throw new InvalidOperationException($"{artifactName} policy version is missing.");
            }

            if (expectedVersion.HasValue && version != expectedVersion.Value)
            {
                throw new InvalidOperationException($"{artifactName} policy version {version} does not match hash algorithm v{expectedVersion.Value}.");
            }

            if (!ReplayAcceptancePolicyOptions.IsSupportedVersion(version))
            {
                throw UnsupportedVersion(version, artifactName);
            }

            if (snapshot == null)
            {
                throw new InvalidOperationException($"{artifactName} policy snapshot is missing.");
            }

            if (snapshot.Version != version)
            {
                throw new InvalidOperationException($"{artifactName} policy snapshot version {snapshot.Version} does not match policy version {version}.");
            }
        }

        private static InvalidOperationException UnsupportedVersion(int version, string artifactName)
        {
            return new InvalidOperationException($"{artifactName} policy version is not supported: {version}.");
        }

        private static ModelDto ModelFrom(ReplayModelIdentity? model)
        {
            model ??= new ReplayModelIdentity();
            return new ModelDto
            {
                ModelId = model.ModelId,
                Version = model.Version,
                Sha256 = model.Sha256,
                ModelPath = model.ModelPath,
                ManifestPath = model.ManifestPath,
                Labels = model.Labels,
                TaskType = model.TaskType,
                InputWidth = model.InputWidth,
                InputHeight = model.InputHeight,
                ApprovalStatus = model.ApprovalStatus,
                IsPackage = model.IsPackage,
                IdentityKey = model.IdentityKey
            };
        }

        private static PolicyDto PolicyFrom(ReplayAcceptancePolicyOptions? options)
        {
            if (options == null) throw new InvalidOperationException("Replay policy snapshot is missing.");
            return PolicyDto.From(options);
        }

        private static V1MetricsDto V1MetricsFrom(ReplayComparisonMetrics? metrics)
        {
            metrics ??= new ReplayComparisonMetrics();
            return new V1MetricsDto
            {
                SampleCount = metrics.SampleCount,
                CandidateNewMissedDetectionCount = metrics.CandidateNewMissedDetectionCount,
                CandidateFixedMissedDetectionCount = metrics.CandidateFixedMissedDetectionCount,
                CandidateNewFalseRejectCount = metrics.CandidateNewFalseRejectCount,
                CandidateFixedFalseRejectCount = metrics.CandidateFixedFalseRejectCount,
                ChangedDecisionCount = metrics.ChangedDecisionCount,
                InvalidSampleCount = metrics.InvalidSampleCount,
                BaselineCorrectCount = metrics.BaselineCorrectCount,
                CandidateCorrectCount = metrics.CandidateCorrectCount,
                BaselineAccuracy = metrics.BaselineAccuracy,
                CandidateAccuracy = metrics.CandidateAccuracy,
                BaselineP95ElapsedMs = metrics.BaselineP95ElapsedMs,
                CandidateP95ElapsedMs = metrics.CandidateP95ElapsedMs
            };
        }

        private static V2MetricsDto V2MetricsFrom(ReplayComparisonMetrics? metrics)
        {
            metrics ??= new ReplayComparisonMetrics();
            return new V2MetricsDto
            {
                SampleCount = metrics.SampleCount,
                TotalSampleCount = metrics.TotalSampleCount,
                ValidSampleCount = metrics.ValidSampleCount,
                CandidateNewMissedDetectionCount = metrics.CandidateNewMissedDetectionCount,
                CandidateFixedMissedDetectionCount = metrics.CandidateFixedMissedDetectionCount,
                CandidateNewFalseRejectCount = metrics.CandidateNewFalseRejectCount,
                CandidateFixedFalseRejectCount = metrics.CandidateFixedFalseRejectCount,
                BaselineMissedDetectionCount = metrics.BaselineMissedDetectionCount,
                BaselineMissedDetectionRate = metrics.BaselineMissedDetectionRate,
                CandidateMissedDetectionCount = metrics.CandidateMissedDetectionCount,
                CandidateMissedDetectionRate = metrics.CandidateMissedDetectionRate,
                BaselineFalseRejectCount = metrics.BaselineFalseRejectCount,
                BaselineFalseRejectRate = metrics.BaselineFalseRejectRate,
                CandidateFalseRejectCount = metrics.CandidateFalseRejectCount,
                CandidateFalseRejectRate = metrics.CandidateFalseRejectRate,
                FalseRejectRateIncrease = metrics.FalseRejectRateIncrease,
                ChangedDecisionCount = metrics.ChangedDecisionCount,
                InvalidSampleCount = metrics.InvalidSampleCount,
                BaselineCorrectCount = metrics.BaselineCorrectCount,
                CandidateCorrectCount = metrics.CandidateCorrectCount,
                BaselineAccuracy = metrics.BaselineAccuracy,
                CandidateAccuracy = metrics.CandidateAccuracy,
                BaselineP95ElapsedMs = metrics.BaselineP95ElapsedMs,
                CandidateP95ElapsedMs = metrics.CandidateP95ElapsedMs
            };
        }

        private static IReadOnlyList<V1SampleDto>? V1SamplesFrom(IReadOnlyList<ReplaySampleComparison>? samples)
        {
            if (samples == null) return null;
            var list = new List<V1SampleDto>(samples.Count);
            foreach (ReplaySampleComparison sample in samples)
            {
                list.Add(new V1SampleDto
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    GroundTruth = sample.GroundTruth,
                    BaselineDecision = sample.BaselineDecision,
                    CandidateDecision = sample.CandidateDecision,
                    Classification = sample.Classification,
                    DecisionChanged = sample.DecisionChanged,
                    BaselineElapsedMs = sample.BaselineElapsedMs,
                    CandidateElapsedMs = sample.CandidateElapsedMs,
                    BaselineRuleSummary = sample.BaselineRuleSummary,
                    CandidateRuleSummary = sample.CandidateRuleSummary
                });
            }

            return list;
        }

        private static IReadOnlyList<V2SampleDto>? V2SamplesFrom(IReadOnlyList<ReplaySampleComparison>? samples)
        {
            if (samples == null) return null;
            var list = new List<V2SampleDto>(samples.Count);
            foreach (ReplaySampleComparison sample in samples)
            {
                list.Add(new V2SampleDto
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    GroundTruth = sample.GroundTruth,
                    BaselineDecision = sample.BaselineDecision,
                    CandidateDecision = sample.CandidateDecision,
                    Classification = sample.Classification,
                    DecisionChanged = sample.DecisionChanged,
                    BaselineElapsedMs = sample.BaselineElapsedMs,
                    CandidateElapsedMs = sample.CandidateElapsedMs,
                    BaselineRuleSummary = sample.BaselineRuleSummary,
                    CandidateRuleSummary = sample.CandidateRuleSummary,
                    IsValid = sample.IsValid,
                    InvalidReason = sample.InvalidReason
                });
            }

            return list;
        }

        private sealed class ModelDto
        {
            public string ModelId { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string Sha256 { get; set; } = string.Empty;
            public string ModelPath { get; set; } = string.Empty;
            public string ManifestPath { get; set; } = string.Empty;
            public IReadOnlyList<string>? Labels { get; set; }
            public string TaskType { get; set; } = string.Empty;
            public int InputWidth { get; set; }
            public int InputHeight { get; set; }
            public string ApprovalStatus { get; set; } = string.Empty;
            public bool IsPackage { get; set; }
            public string IdentityKey { get; set; } = string.Empty;
        }

        private sealed class PolicyDto
        {
            public int Version { get; set; }
            public int MinimumValidSamples { get; set; }
            public int MaximumInvalidSampleCount { get; set; }
            public double? MaximumInvalidSampleRate { get; set; }
            public int MaximumNewMissedDetections { get; set; }
            public double? MaximumCandidateMissedDetectionRate { get; set; }
            public int? MaximumNewFalseRejects { get; set; }
            public double? MaximumFalseRejectRateIncrease { get; set; }
            public double? MinimumCandidateAccuracy { get; set; }
            public long? MaximumCandidateP95ElapsedMs { get; set; }
            public double? MaximumP95InferenceIncreaseRatio { get; set; }

            public static PolicyDto From(ReplayAcceptancePolicyOptions options)
            {
                return new PolicyDto
                {
                    Version = options.Version,
                    MinimumValidSamples = options.MinimumValidSamples,
                    MaximumInvalidSampleCount = options.MaximumInvalidSampleCount,
                    MaximumInvalidSampleRate = options.MaximumInvalidSampleRate,
                    MaximumNewMissedDetections = options.MaximumNewMissedDetections,
                    MaximumCandidateMissedDetectionRate = options.MaximumCandidateMissedDetectionRate,
                    MaximumNewFalseRejects = options.MaximumNewFalseRejects,
                    MaximumFalseRejectRateIncrease = options.MaximumFalseRejectRateIncrease,
                    MinimumCandidateAccuracy = options.MinimumCandidateAccuracy,
                    MaximumCandidateP95ElapsedMs = options.MaximumCandidateP95ElapsedMs,
                    MaximumP95InferenceIncreaseRatio = options.MaximumP95InferenceIncreaseRatio
                };
            }
        }

        private sealed class V1MetricsDto
        {
            public int SampleCount { get; set; }
            public int CandidateNewMissedDetectionCount { get; set; }
            public int CandidateFixedMissedDetectionCount { get; set; }
            public int CandidateNewFalseRejectCount { get; set; }
            public int CandidateFixedFalseRejectCount { get; set; }
            public int ChangedDecisionCount { get; set; }
            public int InvalidSampleCount { get; set; }
            public int BaselineCorrectCount { get; set; }
            public int CandidateCorrectCount { get; set; }
            public double BaselineAccuracy { get; set; }
            public double CandidateAccuracy { get; set; }
            public long BaselineP95ElapsedMs { get; set; }
            public long CandidateP95ElapsedMs { get; set; }
        }

        private sealed class V2MetricsDto
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

        private sealed class V1SampleDto
        {
            public string SampleId { get; set; } = string.Empty;
            public string InspectionId { get; set; } = string.Empty;
            public string GroundTruth { get; set; } = string.Empty;
            public string BaselineDecision { get; set; } = string.Empty;
            public string CandidateDecision { get; set; } = string.Empty;
            public string Classification { get; set; } = string.Empty;
            public bool DecisionChanged { get; set; }
            public long BaselineElapsedMs { get; set; }
            public long CandidateElapsedMs { get; set; }
            public string BaselineRuleSummary { get; set; } = string.Empty;
            public string CandidateRuleSummary { get; set; } = string.Empty;
        }

        private sealed class V2SampleDto
        {
            public string SampleId { get; set; } = string.Empty;
            public string InspectionId { get; set; } = string.Empty;
            public string GroundTruth { get; set; } = string.Empty;
            public string BaselineDecision { get; set; } = string.Empty;
            public string CandidateDecision { get; set; } = string.Empty;
            public string Classification { get; set; } = string.Empty;
            public bool DecisionChanged { get; set; }
            public long BaselineElapsedMs { get; set; }
            public long CandidateElapsedMs { get; set; }
            public string BaselineRuleSummary { get; set; } = string.Empty;
            public string CandidateRuleSummary { get; set; } = string.Empty;
            public bool IsValid { get; set; }
            public string InvalidReason { get; set; } = string.Empty;
        }

        // v1 canonical schema is frozen to match a1786f86509021035f757263834df50fa933d61a.
        // Future v3 artifacts must add an independent algorithm instead of editing this DTO.
        private sealed class V1ReportDto
        {
            public string RunId { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string DatasetId { get; set; } = string.Empty;
            public string DatasetHash { get; set; } = string.Empty;
            public ModelDto BaselineModel { get; set; } = new ModelDto();
            public ModelDto CandidateModel { get; set; } = new ModelDto();
            public DateTimeOffset StartedAt { get; set; }
            public DateTimeOffset? CompletedAt { get; set; }
            public V1MetricsDto Metrics { get; set; } = new V1MetricsDto();
            public IReadOnlyList<V1SampleDto>? Samples { get; set; }
            public IReadOnlyList<string>? Errors { get; set; }
            public string ReportJsonPath { get; set; } = string.Empty;
            public string ReportCsvPath { get; set; } = string.Empty;
            public string ReportHash { get; set; } = string.Empty;
            public int PolicyVersion { get; set; }
            public string RecipeHash { get; set; } = string.Empty;
            public string RuleSetHash { get; set; } = string.Empty;
            public string PolicyHash { get; set; } = string.Empty;
            public PolicyDto PolicySnapshot { get; set; } = new PolicyDto();
            public string BaselineModelHash { get; set; } = string.Empty;
            public string CandidateModelHash { get; set; } = string.Empty;

            public static V1ReportDto From(ReplayRunReport report)
            {
                return new V1ReportDto
                {
                    RunId = report.RunId,
                    Status = report.Status,
                    DatasetId = report.DatasetId,
                    DatasetHash = report.DatasetHash,
                    BaselineModel = ModelFrom(report.BaselineModel),
                    CandidateModel = ModelFrom(report.CandidateModel),
                    StartedAt = report.StartedAt,
                    CompletedAt = report.CompletedAt,
                    Metrics = V1MetricsFrom(report.Metrics),
                    Samples = V1SamplesFrom(report.Samples),
                    Errors = report.Errors,
                    ReportJsonPath = string.Empty,
                    ReportCsvPath = string.Empty,
                    ReportHash = string.Empty,
                    PolicyVersion = report.PolicyVersion,
                    RecipeHash = report.RecipeHash,
                    RuleSetHash = report.RuleSetHash,
                    PolicyHash = report.PolicyHash,
                    PolicySnapshot = PolicyFrom(report.PolicySnapshot),
                    BaselineModelHash = report.BaselineModelHash,
                    CandidateModelHash = report.CandidateModelHash
                };
            }
        }

        // v2 canonical schema is frozen to match af33c6b3e07aafd9e19f42ea77a252932cee3456.
        // Future v3 artifacts must add an independent algorithm instead of editing this DTO.
        private sealed class V2ReportDto
        {
            public string RunId { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string DatasetId { get; set; } = string.Empty;
            public string DatasetHash { get; set; } = string.Empty;
            public ModelDto BaselineModel { get; set; } = new ModelDto();
            public ModelDto CandidateModel { get; set; } = new ModelDto();
            public DateTimeOffset StartedAt { get; set; }
            public DateTimeOffset? CompletedAt { get; set; }
            public V2MetricsDto Metrics { get; set; } = new V2MetricsDto();
            public IReadOnlyList<V2SampleDto>? Samples { get; set; }
            public IReadOnlyList<string>? Errors { get; set; }
            public string ReportJsonPath { get; set; } = string.Empty;
            public string ReportCsvPath { get; set; } = string.Empty;
            public string ReportHash { get; set; } = string.Empty;
            public int PolicyVersion { get; set; }
            public string RecipeHash { get; set; } = string.Empty;
            public string RuleSetHash { get; set; } = string.Empty;
            public string PolicyHash { get; set; } = string.Empty;
            public PolicyDto PolicySnapshot { get; set; } = new PolicyDto();
            public string BaselineModelHash { get; set; } = string.Empty;
            public string CandidateModelHash { get; set; } = string.Empty;

            public static V2ReportDto From(ReplayRunReport report)
            {
                return new V2ReportDto
                {
                    RunId = report.RunId,
                    Status = report.Status,
                    DatasetId = report.DatasetId,
                    DatasetHash = report.DatasetHash,
                    BaselineModel = ModelFrom(report.BaselineModel),
                    CandidateModel = ModelFrom(report.CandidateModel),
                    StartedAt = report.StartedAt,
                    CompletedAt = report.CompletedAt,
                    Metrics = V2MetricsFrom(report.Metrics),
                    Samples = V2SamplesFrom(report.Samples),
                    Errors = report.Errors,
                    ReportJsonPath = string.Empty,
                    ReportCsvPath = string.Empty,
                    ReportHash = string.Empty,
                    PolicyVersion = report.PolicyVersion,
                    RecipeHash = report.RecipeHash,
                    RuleSetHash = report.RuleSetHash,
                    PolicyHash = report.PolicyHash,
                    PolicySnapshot = PolicyFrom(report.PolicySnapshot),
                    BaselineModelHash = report.BaselineModelHash,
                    CandidateModelHash = report.CandidateModelHash
                };
            }
        }

        // v1 canonical evidence schema is frozen with the v1 report schema.
        private sealed class V1EvidenceDto
        {
            public string EvidenceId { get; set; } = string.Empty;
            public string EvidenceHash { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; }
            public string ApprovedBy { get; set; } = string.Empty;
            public string DatasetId { get; set; } = string.Empty;
            public string DatasetHash { get; set; } = string.Empty;
            public string DatasetPath { get; set; } = string.Empty;
            public string ReplayRunId { get; set; } = string.Empty;
            public string ReplayReportHash { get; set; } = string.Empty;
            public ModelDto BaselineModel { get; set; } = new ModelDto();
            public ModelDto CandidateModel { get; set; } = new ModelDto();
            public V1MetricsDto Metrics { get; set; } = new V1MetricsDto();
            public IReadOnlyList<string>? PolicyReasons { get; set; }
            public string ReplayReportPath { get; set; } = string.Empty;
            public int PolicyVersion { get; set; }
            public string PolicyHash { get; set; } = string.Empty;
            public PolicyDto PolicySnapshot { get; set; } = new PolicyDto();
            public string RecipeHash { get; set; } = string.Empty;
            public string RuleSetHash { get; set; } = string.Empty;
            public string BaselineModelHash { get; set; } = string.Empty;
            public string CandidateModelHash { get; set; } = string.Empty;

            public static V1EvidenceDto From(ModelApprovalEvidence evidence)
            {
                return new V1EvidenceDto
                {
                    EvidenceId = evidence.EvidenceId,
                    EvidenceHash = string.Empty,
                    CreatedAt = evidence.CreatedAt,
                    ApprovedBy = evidence.ApprovedBy,
                    DatasetId = evidence.DatasetId,
                    DatasetHash = evidence.DatasetHash,
                    DatasetPath = evidence.DatasetPath,
                    ReplayRunId = evidence.ReplayRunId,
                    ReplayReportHash = evidence.ReplayReportHash,
                    BaselineModel = ModelFrom(evidence.BaselineModel),
                    CandidateModel = ModelFrom(evidence.CandidateModel),
                    Metrics = V1MetricsFrom(evidence.Metrics),
                    PolicyReasons = evidence.PolicyReasons,
                    ReplayReportPath = evidence.ReplayReportPath,
                    PolicyVersion = evidence.PolicyVersion,
                    PolicyHash = evidence.PolicyHash,
                    PolicySnapshot = PolicyFrom(evidence.PolicySnapshot),
                    RecipeHash = evidence.RecipeHash,
                    RuleSetHash = evidence.RuleSetHash,
                    BaselineModelHash = evidence.BaselineModelHash,
                    CandidateModelHash = evidence.CandidateModelHash
                };
            }
        }

        // v2 canonical evidence schema is frozen with the v2 report schema.
        private sealed class V2EvidenceDto
        {
            public string EvidenceId { get; set; } = string.Empty;
            public string EvidenceHash { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; }
            public string ApprovedBy { get; set; } = string.Empty;
            public string DatasetId { get; set; } = string.Empty;
            public string DatasetHash { get; set; } = string.Empty;
            public string DatasetPath { get; set; } = string.Empty;
            public string ReplayRunId { get; set; } = string.Empty;
            public string ReplayReportHash { get; set; } = string.Empty;
            public ModelDto BaselineModel { get; set; } = new ModelDto();
            public ModelDto CandidateModel { get; set; } = new ModelDto();
            public V2MetricsDto Metrics { get; set; } = new V2MetricsDto();
            public IReadOnlyList<string>? PolicyReasons { get; set; }
            public string ReplayReportPath { get; set; } = string.Empty;
            public int PolicyVersion { get; set; }
            public string PolicyHash { get; set; } = string.Empty;
            public PolicyDto PolicySnapshot { get; set; } = new PolicyDto();
            public string RecipeHash { get; set; } = string.Empty;
            public string RuleSetHash { get; set; } = string.Empty;
            public string BaselineModelHash { get; set; } = string.Empty;
            public string CandidateModelHash { get; set; } = string.Empty;

            public static V2EvidenceDto From(ModelApprovalEvidence evidence)
            {
                return new V2EvidenceDto
                {
                    EvidenceId = evidence.EvidenceId,
                    EvidenceHash = string.Empty,
                    CreatedAt = evidence.CreatedAt,
                    ApprovedBy = evidence.ApprovedBy,
                    DatasetId = evidence.DatasetId,
                    DatasetHash = evidence.DatasetHash,
                    DatasetPath = evidence.DatasetPath,
                    ReplayRunId = evidence.ReplayRunId,
                    ReplayReportHash = evidence.ReplayReportHash,
                    BaselineModel = ModelFrom(evidence.BaselineModel),
                    CandidateModel = ModelFrom(evidence.CandidateModel),
                    Metrics = V2MetricsFrom(evidence.Metrics),
                    PolicyReasons = evidence.PolicyReasons,
                    ReplayReportPath = evidence.ReplayReportPath,
                    PolicyVersion = evidence.PolicyVersion,
                    PolicyHash = evidence.PolicyHash,
                    PolicySnapshot = PolicyFrom(evidence.PolicySnapshot),
                    RecipeHash = evidence.RecipeHash,
                    RuleSetHash = evidence.RuleSetHash,
                    BaselineModelHash = evidence.BaselineModelHash,
                    CandidateModelHash = evidence.CandidateModelHash
                };
            }
        }
    }
}
