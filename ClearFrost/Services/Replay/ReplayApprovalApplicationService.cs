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
        private readonly IModelApprovalEvidenceStore _evidenceStore;
        private readonly ReplayApprovalEvidenceProductionGate _productionGate;
        private readonly ReplayAcceptancePolicy _policy;
        private readonly OperationAuditService? _auditService;
        private readonly SemaphoreSlim _approvalLock = new SemaphoreSlim(1, 1);

        public ReplayApprovalApplicationService(
            ModelRegistry registry,
            Func<IReadOnlyList<ModelRegistryEntry>> refreshRegistry,
            IModelApprovalEvidenceStore evidenceStore,
            ReplayApprovalEvidenceProductionGate productionGate,
            ReplayAcceptancePolicy policy,
            OperationAuditService? auditService = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _refreshRegistry = refreshRegistry ?? throw new ArgumentNullException(nameof(refreshRegistry));
            _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
            _productionGate = productionGate ?? throw new ArgumentNullException(nameof(productionGate));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _auditService = auditService;
        }

        public async Task<ReplayApprovalResult> ApproveCandidateAsync(
            ReplayApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            await _approvalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AppendAuditAsync(request, OperationAuditStatus.Requested, "Replay approval requested", cancellationToken)
                    .ConfigureAwait(false);

                ReplayApprovalResult precheck = ValidateRequest(request);
                if (!precheck.Succeeded)
                {
                    await AppendAuditAsync(request, OperationAuditStatus.Denied, precheck.Message, cancellationToken)
                        .ConfigureAwait(false);
                    return precheck;
                }

                string manifestPath = request.CandidateEntry.ManifestPath;
                byte[] originalManifest = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                ModelApprovalEvidence? evidence = null;
                var compensationFailures = new List<string>();

                try
                {
                    evidence = _evidenceStore.SaveEvidence(
                        request.Report,
                        request.ApprovedBy,
                        request.DatasetPath,
                        _policy.PolicyHash);

                    ModelPackageManifest manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                        File.ReadAllText(manifestPath),
                        ReplayJson.Options) ?? new ModelPackageManifest();

                    manifest.AcceptanceDataset = evidence.DatasetPath;
                    manifest.AcceptanceMetrics["totalSamples"] = evidence.Metrics.SampleCount;
                    manifest.AcceptanceMetrics["candidateCorrectSamples"] = evidence.Metrics.CandidateCorrectCount;
                    manifest.AcceptanceMetrics["candidateNewMissedDetectionCount"] = evidence.Metrics.CandidateNewMissedDetectionCount;
                    manifest.AcceptanceMetrics["candidateNewFalseRejectCount"] = evidence.Metrics.CandidateNewFalseRejectCount;
                    manifest.AcceptanceMetrics["candidateAccuracy"] = evidence.Metrics.CandidateAccuracy;
                    manifest.AcceptanceMetrics["candidateP95ElapsedMs"] = evidence.Metrics.CandidateP95ElapsedMs;
                    manifest.Approval = new ModelApprovalMetadata
                    {
                        Status = ModelApprovalStatuses.Approved,
                        ApprovedAt = evidence.CreatedAt,
                        ApprovedBy = request.ApprovedBy?.Trim() ?? string.Empty,
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

                    ModelRegistryEntry? refreshedEntry = _registry.Resolve(request.CandidateEntry.ModelPath);
                    if (refreshedEntry == null)
                    {
                        throw new InvalidOperationException("Candidate disappeared from registry after approval manifest update.");
                    }

                    ProductionModelReadinessResult gate = _productionGate.Validate(refreshedEntry);
                    if (!gate.Succeeded)
                    {
                        throw new InvalidOperationException($"{gate.ErrorCode}: {gate.Message}");
                    }

                    await AppendAuditAsync(request, OperationAuditStatus.Succeeded, evidence.EvidenceId, cancellationToken)
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

                    await AppendAuditAsync(
                        request,
                        compensationFailures.Count == 0 ? OperationAuditStatus.Denied : OperationAuditStatus.Failed,
                        ex.Message,
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

        private ReplayApprovalResult ValidateRequest(ReplayApprovalRequest request)
        {
            ReplayRunReport report = request.Report ?? new ReplayRunReport();
            ModelRegistryEntry entry = request.CandidateEntry ?? new ModelRegistryEntry();
            if (!entry.IsPackage || entry.Manifest == null || string.IsNullOrWhiteSpace(entry.ManifestPath))
            {
                return ReplayApprovalResult.Fail("ReplayApprovalManifestMissing", "Candidate package manifest is required.");
            }

            if (!File.Exists(entry.ManifestPath) || !File.Exists(entry.ModelPath))
            {
                return ReplayApprovalResult.Fail("ReplayApprovalPackageMissing", "Candidate manifest/model file is missing.");
            }

            if (!string.Equals(report.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
            {
                return ReplayApprovalResult.Fail("ReplayApprovalRunNotCompleted", "Only completed replay runs can approve a candidate.");
            }

            if (!string.Equals(report.CandidateModel.ModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.CandidateModel.Version, entry.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.CandidateModel.Sha256, entry.ModelHash, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalResult.Fail("ReplayApprovalCandidateMismatch", "Replay report candidate does not match registry candidate.");
            }

            if (!string.Equals(report.ReportHash, SqliteReplayRunStore.ComputeReportHash(report), StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalResult.Fail("ReplayApprovalReportHashMismatch", "Replay report hash is invalid.");
            }

            ReplayApprovalDecision decision = _policy.Evaluate(report);
            if (!decision.Approved)
            {
                return ReplayApprovalResult.Fail("ReplayApprovalPolicyRejected", string.Join("; ", decision.Reasons));
            }

            if (string.IsNullOrWhiteSpace(request.ApprovedBy))
            {
                return ReplayApprovalResult.Fail("ReplayApprovalUserMissing", "Approver id is required.");
            }

            return ReplayApprovalResult.Ok(new ModelApprovalEvidence(), string.Empty);
        }

        private Task AppendAuditAsync(
            ReplayApprovalRequest request,
            OperationAuditStatus status,
            string details,
            CancellationToken cancellationToken)
        {
            return _auditService == null
                ? Task.CompletedTask
                : _auditService.AppendAsync(new OperationAuditRecord
                {
                    Operation = "ReplayApproval",
                    Status = status,
                    OperatorId = request.ApprovedBy,
                    Role = Enum.TryParse(request.ApprovedByRole, ignoreCase: true, out ProductionRole role)
                        ? role
                        : ProductionRole.Engineer,
                    Details = details ?? string.Empty,
                    FailureBlocker = status == OperationAuditStatus.Failed || status == OperationAuditStatus.Denied
                        ? details ?? string.Empty
                        : string.Empty
                }, cancellationToken);
        }
    }
}
