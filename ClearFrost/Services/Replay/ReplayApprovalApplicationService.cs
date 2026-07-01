// ============================================================================
// 文件名: ReplayApprovalApplicationService.cs
// 描述:   Replay Evidence-backed 模型批准应用服务
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Models;
using ClearFrost.Core.Security;
using ClearFrost.Helpers;

namespace ClearFrost.Services.Replay
{
    internal sealed class ReplayApprovalApplicationService
    {
        private readonly ModelRegistry _registry;
        private readonly Func<IReadOnlyList<ModelRegistryEntry>> _refreshRegistry;
        private readonly IReplayRunStore _runStore;
        private readonly IReplayDatasetStore _datasetStore;
        private readonly IModelApprovalEvidenceStore _evidenceStore;
        private readonly ReplayApprovalEvidenceProductionGate _productionGate;
        private readonly ReplayAcceptancePolicy _policy;
        private readonly OperationAuditService? _auditService;
        private readonly Func<string>? _approverIdProvider;
        private readonly Func<ProductionRole>? _approverRoleProvider;
        private readonly SemaphoreSlim _approvalLock = new SemaphoreSlim(1, 1);

        public ReplayApprovalApplicationService(
            ModelRegistry registry,
            Func<IReadOnlyList<ModelRegistryEntry>> refreshRegistry,
            IReplayRunStore runStore,
            IReplayDatasetStore datasetStore,
            IModelApprovalEvidenceStore evidenceStore,
            ReplayApprovalEvidenceProductionGate productionGate,
            ReplayAcceptancePolicy policy,
            OperationAuditService? auditService = null,
            Func<string>? approverIdProvider = null,
            Func<ProductionRole>? approverRoleProvider = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _refreshRegistry = refreshRegistry ?? throw new ArgumentNullException(nameof(refreshRegistry));
            _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
            _productionGate = productionGate ?? throw new ArgumentNullException(nameof(productionGate));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _auditService = auditService;
            _approverIdProvider = approverIdProvider;
            _approverRoleProvider = approverRoleProvider;
        }

        public async Task<ReplayApprovalResult> ApproveCandidateAsync(
            ReplayApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!TryResolveAuthorizedApprover(
                    out string approverId,
                    out ProductionRole approverRole,
                    out string authorizationErrorCode,
                    out string authorizationMessage))
            {
                await AppendAuditAsync(
                    OperationAuditStatus.Denied,
                    authorizationMessage,
                    approverId,
                    approverRole,
                    cancellationToken).ConfigureAwait(false);
                return ReplayApprovalResult.Fail(authorizationErrorCode, authorizationMessage);
            }

            await _approvalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AppendAuditAsync(
                    OperationAuditStatus.Requested,
                    "Replay approval requested",
                    approverId,
                    approverRole,
                    cancellationToken)
                    .ConfigureAwait(false);

                ReplayApprovalContext context = await BuildApprovalContextAsync(request, cancellationToken).ConfigureAwait(false);
                if (!context.Result.Succeeded)
                {
                    await AppendAuditAsync(
                        OperationAuditStatus.Denied,
                        context.Result.Message,
                        approverId,
                        approverRole,
                        cancellationToken)
                        .ConfigureAwait(false);
                    return context.Result;
                }

                string manifestPath = context.CandidateEntry.ManifestPath;
                byte[] originalManifest = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                ModelApprovalEvidence? evidence = null;
                var compensationFailures = new List<string>();

                try
                {
                    evidence = _evidenceStore.SaveEvidence(
                        context.Report,
                        approverId,
                        context.Dataset.RootDirectory,
                        context.Report.PolicyHash);

                    ModelPackageManifest manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                        File.ReadAllText(manifestPath),
                        ReplayJson.Options) ?? new ModelPackageManifest();

                    manifest.AcceptanceDataset = evidence.DatasetPath;
                    manifest.AcceptanceMetrics["totalSamples"] = evidence.Metrics.TotalSampleCount > 0
                        ? evidence.Metrics.TotalSampleCount
                        : evidence.Metrics.SampleCount;
                    manifest.AcceptanceMetrics["validSamples"] = evidence.Metrics.ValidSampleCount > 0
                        ? evidence.Metrics.ValidSampleCount
                        : evidence.Metrics.SampleCount;
                    manifest.AcceptanceMetrics["invalidSamples"] = evidence.Metrics.InvalidSampleCount;
                    manifest.AcceptanceMetrics["candidateCorrectSamples"] = evidence.Metrics.CandidateCorrectCount;
                    manifest.AcceptanceMetrics["candidateNewMissedDetectionCount"] = evidence.Metrics.CandidateNewMissedDetectionCount;
                    manifest.AcceptanceMetrics["candidateMissedDetectionCount"] = evidence.Metrics.CandidateMissedDetectionCount;
                    manifest.AcceptanceMetrics["candidateMissedDetectionRate"] = evidence.Metrics.CandidateMissedDetectionRate;
                    manifest.AcceptanceMetrics["candidateNewFalseRejectCount"] = evidence.Metrics.CandidateNewFalseRejectCount;
                    manifest.AcceptanceMetrics["falseRejectRateIncrease"] = evidence.Metrics.FalseRejectRateIncrease;
                    manifest.AcceptanceMetrics["candidateAccuracy"] = evidence.Metrics.CandidateAccuracy;
                    manifest.AcceptanceMetrics["candidateP95ElapsedMs"] = evidence.Metrics.CandidateP95ElapsedMs;
                    manifest.Approval = new ModelApprovalMetadata
                    {
                        Status = ModelApprovalStatuses.Approved,
                        ApprovedAt = evidence.CreatedAt,
                        ApprovedBy = approverId,
                        Summary = $"Replay evidence {evidence.EvidenceId}",
                        GoldenDatasetPath = evidence.DatasetPath,
                        ActualPassRate = evidence.Metrics.CandidateAccuracy,
                        ReplayEvidenceId = evidence.EvidenceId,
                        ReplayEvidenceHash = evidence.EvidenceHash,
                        ReplayDatasetHash = evidence.DatasetHash,
                        ReplayRunId = evidence.ReplayRunId
                    };

                    AtomicFileWriter.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ReplayJson.Options));
                    _refreshRegistry();

                    ModelRegistryEntry? refreshedEntry = _registry.Resolve(context.CandidateEntry.ModelPath);
                    if (refreshedEntry == null)
                    {
                        throw new InvalidOperationException("Candidate disappeared from registry after approval manifest update.");
                    }

                    ProductionModelReadinessResult gate = _productionGate.Validate(refreshedEntry);
                    if (!gate.Succeeded)
                    {
                        throw new InvalidOperationException($"{gate.ErrorCode}: {gate.Message}");
                    }

                    await AppendAuditAsync(
                        OperationAuditStatus.Succeeded,
                        evidence.EvidenceId,
                        approverId,
                        approverRole,
                        cancellationToken)
                        .ConfigureAwait(false);
                    return ReplayApprovalResult.Ok(evidence, "Replay approval evidence bound to candidate model.");
                }
                catch (Exception ex)
                {
                    try
                    {
                        AtomicFileWriter.RestoreAllBytes(manifestPath, originalManifest);
                    }
                    catch (Exception restoreEx)
                    {
                        compensationFailures.Add($"Manifest restore failed: {restoreEx.Message}");
                    }

                    try
                    {
                        _refreshRegistry();
                    }
                    catch (Exception refreshEx)
                    {
                        compensationFailures.Add($"Registry refresh failed: {refreshEx.Message}");
                    }

                    if (evidence != null &&
                        _evidenceStore is FileModelApprovalEvidenceStore fileEvidenceStore &&
                        !fileEvidenceStore.TryDeleteUnpublishedEvidence(evidence.EvidenceId, out string evidenceDeleteError))
                    {
                        compensationFailures.Add($"Unpublished evidence cleanup failed: {evidenceDeleteError}");
                    }

                    await AppendAuditAsync(
                        compensationFailures.Count == 0 ? OperationAuditStatus.Denied : OperationAuditStatus.Failed,
                        ex.Message,
                        approverId,
                        approverRole,
                        CancellationToken.None).ConfigureAwait(false);

                    return ReplayApprovalResult.Fail(
                        compensationFailures.Count == 0 ? "ReplayApprovalRejected" : "ReplayApprovalFaulted",
                        compensationFailures.Count == 0
                            ? $"Replay approval failed; manifest restored: {ex.Message}"
                            : $"Replay approval failed and compensation faulted: {ex.Message}",
                        compensationFailures.Count > 0,
                        compensationFailures);
                }
            }
            finally
            {
                _approvalLock.Release();
            }
        }

        private async Task<ReplayApprovalContext> BuildApprovalContextAsync(
            ReplayApprovalRequest request,
            CancellationToken cancellationToken)
        {
            string runId = !string.IsNullOrWhiteSpace(request.RunId)
                ? request.RunId.Trim()
                : request.Report?.RunId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(runId))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalRunIdMissing", "Replay run id is required.");
            }

            ReplayRunRecord? runRecord = await _runStore.LoadRunRecordAsync(runId, cancellationToken).ConfigureAwait(false);
            if (runRecord == null)
            {
                return ReplayApprovalContext.Fail("ReplayApprovalRunMissing", "Replay run is missing from DB.");
            }

            if (!string.Equals(runRecord.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalRunNotCompleted", $"Replay run DB status is {runRecord.Status}.");
            }

            ReplayRunReport report;
            try
            {
                report = await _runStore.LoadReportAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ReplayApprovalContext.Fail("ReplayApprovalReportMissing", ex.Message);
            }

            if (!SamePath(report.ReportJsonPath, runRecord.ReportJsonPath))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalRunPathMismatch", "Replay run DB report path does not match report.json.");
            }

            if (!string.Equals(report.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalRunNotCompleted", "Only completed replay runs can approve a candidate.");
            }

            if (!string.Equals(report.ReportHash, SqliteReplayRunStore.ComputeReportHash(report), StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalReportHashMismatch", "Replay report hash is invalid.");
            }

            ReplayDatasetSnapshot dataset;
            try
            {
                dataset = await _datasetStore.LoadSnapshotAsync(report.DatasetId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ReplayApprovalContext.Fail("ReplayApprovalDatasetMissing", ex.Message);
            }

            if (!string.Equals(dataset.DatasetHash, report.DatasetHash, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalDatasetHashMismatch", "Replay report dataset hash does not match frozen dataset.");
            }

            ReplayModelIdentity requestedCandidate = ResolveRequestedCandidate(request, report);
            if (!string.Equals(requestedCandidate.ModelId, report.CandidateModel.ModelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requestedCandidate.Version, report.CandidateModel.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requestedCandidate.Sha256, report.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalCandidateMismatch", "Requested candidate identity does not match replay report.");
            }

            _refreshRegistry();
            ModelRegistryEntry? entry = _registry.Entries.FirstOrDefault(item =>
                item.IsPackage &&
                string.Equals(item.ModelId, report.CandidateModel.ModelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Version, report.CandidateModel.Version, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ModelHash, report.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return ReplayApprovalContext.Fail("ReplayApprovalCandidateNotFound", "Candidate package is not present in the model registry.");
            }

            if (!entry.IsPackage || entry.Manifest == null || string.IsNullOrWhiteSpace(entry.ManifestPath))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalManifestMissing", "Candidate package manifest is required.");
            }

            if (!File.Exists(entry.ManifestPath) || !File.Exists(entry.ModelPath))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalPackageMissing", "Candidate manifest/model file is missing.");
            }

            string currentModelHash = FileReplayDatasetStore.ComputeSha256(entry.ModelPath);
            if (!string.Equals(currentModelHash, report.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalCurrentModelHashMismatch", "Current candidate model file no longer matches replay report.");
            }

            ReplayAcceptancePolicyOptions policySnapshot = report.PolicySnapshot ?? ReplayAcceptancePolicyOptions.ProductionDefault();
            if (!ReplayAcceptancePolicyOptions.IsSupportedVersion(policySnapshot.Version) ||
                report.PolicyVersion != policySnapshot.Version ||
                !string.Equals(report.PolicyHash, ReplayAcceptancePolicy.ComputePolicyHash(policySnapshot), StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalPolicySnapshotInvalid", "Replay report policy snapshot is invalid or unsupported.");
            }

            ReplayApprovalDecision decision = _policy.Evaluate(report, policySnapshot);
            if (!decision.Approved)
            {
                return ReplayApprovalContext.Fail("ReplayApprovalPolicyRejected", string.Join("; ", decision.Reasons));
            }

            return ReplayApprovalContext.Ok(report, dataset, entry);
        }

        private static ReplayModelIdentity ResolveRequestedCandidate(ReplayApprovalRequest request, ReplayRunReport report)
        {
            string modelId = request.CandidateModelId;
            string version = request.CandidateVersion;
            string sha = request.CandidateSha256;
            if (string.IsNullOrWhiteSpace(modelId) && request.CandidateEntry != null)
            {
                modelId = request.CandidateEntry.ModelId;
                version = request.CandidateEntry.Version;
                sha = request.CandidateEntry.ModelHash;
            }

            return string.IsNullOrWhiteSpace(modelId)
                ? report.CandidateModel
                : new ReplayModelIdentity
                {
                    ModelId = modelId.Trim(),
                    Version = version?.Trim() ?? string.Empty,
                    Sha256 = sha?.Trim() ?? string.Empty
                };
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private bool TryResolveAuthorizedApprover(
            out string approverId,
            out ProductionRole approverRole,
            out string errorCode,
            out string message)
        {
            approverId = string.Empty;
            approverRole = ProductionRole.Operator;
            errorCode = string.Empty;
            message = string.Empty;

            if (_approverIdProvider == null)
            {
                errorCode = "ReplayApprovalAuthorizationProviderMissing";
                message = "Replay approval operator provider is required.";
                return false;
            }

            try
            {
                approverId = (_approverIdProvider.Invoke() ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                errorCode = "ReplayApprovalAuthorizationProviderFailed";
                message = $"Replay approval operator provider failed: {ex.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(approverId))
            {
                errorCode = "ReplayApprovalOperatorMissing";
                message = "Replay approval operator id is required.";
                return false;
            }

            if (_approverRoleProvider == null)
            {
                errorCode = "ReplayApprovalAuthorizationProviderMissing";
                message = "Replay approval role provider is required.";
                return false;
            }

            try
            {
                approverRole = _approverRoleProvider.Invoke();
            }
            catch (Exception ex)
            {
                errorCode = "ReplayApprovalAuthorizationProviderFailed";
                message = $"Replay approval role provider failed: {ex.Message}";
                return false;
            }

            if (!ProductionAuthorizationService.Authorize(
                    approverRole,
                    ProductionOperation.EngineeringChange,
                    out string denialReason))
            {
                errorCode = "ReplayApprovalUnauthorized";
                message = denialReason;
                return false;
            }

            return true;
        }

        private Task AppendAuditAsync(
            OperationAuditStatus status,
            string details,
            string operatorId,
            ProductionRole role,
            CancellationToken cancellationToken)
        {
            return _auditService == null
                ? Task.CompletedTask
                : _auditService.AppendAsync(new OperationAuditRecord
                {
                    Operation = "ReplayApproval",
                    Status = status,
                    OperatorId = operatorId,
                    Role = role,
                    Details = details ?? string.Empty,
                    FailureBlocker = status == OperationAuditStatus.Failed || status == OperationAuditStatus.Denied
                        ? details ?? string.Empty
                        : string.Empty
                }, cancellationToken);
        }

        private sealed class ReplayApprovalContext
        {
            private ReplayApprovalContext() { }

            public ReplayApprovalResult Result { get; private init; } = ReplayApprovalResult.Fail(string.Empty, string.Empty);
            public ReplayRunReport Report { get; private init; } = new ReplayRunReport();
            public ReplayDatasetSnapshot Dataset { get; private init; } = new ReplayDatasetSnapshot();
            public ModelRegistryEntry CandidateEntry { get; private init; } = new ModelRegistryEntry();

            public static ReplayApprovalContext Ok(
                ReplayRunReport report,
                ReplayDatasetSnapshot dataset,
                ModelRegistryEntry candidateEntry)
            {
                return new ReplayApprovalContext
                {
                    Result = ReplayApprovalResult.Ok(new ModelApprovalEvidence(), string.Empty),
                    Report = report,
                    Dataset = dataset,
                    CandidateEntry = candidateEntry
                };
            }

            public static ReplayApprovalContext Fail(string errorCode, string message)
            {
                return new ReplayApprovalContext
                {
                    Result = ReplayApprovalResult.Fail(errorCode, message)
                };
            }
        }
    }
}
