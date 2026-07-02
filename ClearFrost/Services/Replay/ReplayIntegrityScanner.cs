// ============================================================================
// 文件名: ReplayIntegrityScanner.cs
// 描述:   Replay Evidence 完整性扫描服务
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Models;
using ClearFrost.Core.Security;

namespace ClearFrost.Services.Replay
{
    public sealed class ReplayIntegrityFinding
    {
        public string Scope { get; init; } = string.Empty;
        public string EntityId { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string DatasetId { get; init; } = string.Empty;
        public string RunId { get; init; } = string.Empty;
        public string EvidenceId { get; init; } = string.Empty;
        public string Severity { get; init; } = "Blocking";
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;
    }

    public sealed class ReplayIntegrityScanResult
    {
        public bool Succeeded => Findings.Count == 0;
        public string Status => Findings.Any(item => string.Equals(item.Severity, "Blocking", StringComparison.OrdinalIgnoreCase))
            ? "Blocking"
            : Findings.Count > 0
                ? "Warning"
                : "Healthy";
        public IReadOnlyList<ReplayIntegrityFinding> Findings { get; init; } = Array.Empty<ReplayIntegrityFinding>();
    }

    internal sealed class ReplayIntegrityScanner
    {
        private readonly ModelRegistry _registry;
        private readonly ReplayApprovalEvidenceProductionGate _gate;
        private readonly IReplayDatasetStore? _datasetStore;
        private readonly IReplayRunStore? _runStore;
        private readonly IModelApprovalEvidenceStore? _evidenceStore;
        private readonly OperationAuditService? _auditService;

        public ReplayIntegrityScanner(
            ModelRegistry registry,
            ReplayApprovalEvidenceProductionGate gate,
            IReplayDatasetStore? datasetStore = null,
            IReplayRunStore? runStore = null,
            IModelApprovalEvidenceStore? evidenceStore = null,
            OperationAuditService? auditService = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _datasetStore = datasetStore;
            _runStore = runStore;
            _evidenceStore = evidenceStore;
            _auditService = auditService;
        }

        public async Task<ReplayIntegrityScanResult> ScanApprovedModelsAsync(
            CancellationToken cancellationToken = default)
        {
            var findings = new List<ReplayIntegrityFinding>();
            await ScanDatasetsAsync(findings, cancellationToken).ConfigureAwait(false);
            await ScanRunsAsync(findings, cancellationToken).ConfigureAwait(false);
            ScanEvidence(findings, cancellationToken);
            ScanApprovedModels(findings, cancellationToken);

            ReplayIntegrityScanResult scan = new ReplayIntegrityScanResult { Findings = findings };
            if (_auditService != null)
            {
                await _auditService.AppendAsync(new OperationAuditRecord
                {
                    Operation = "ReplayIntegrityScan",
                    Status = scan.Succeeded ? OperationAuditStatus.Succeeded : OperationAuditStatus.Failed,
                    OperatorId = "system",
                    Role = ProductionRole.Engineer,
                    Details = scan.Succeeded
                        ? "Replay integrity scan succeeded."
                        : string.Join("; ", findings.Select(item => $"{item.ModelId}/{item.Version}:{item.ErrorCode}")),
                    FailureBlocker = scan.Succeeded
                        ? string.Empty
                        : "Replay evidence integrity"
                }, cancellationToken).ConfigureAwait(false);
            }

            return scan;
        }

        private async Task ScanDatasetsAsync(
            List<ReplayIntegrityFinding> findings,
            CancellationToken cancellationToken)
        {
            if (_datasetStore == null)
            {
                return;
            }

            IReadOnlyList<ReplayDatasetSummary> summaries;
            try
            {
                summaries = await _datasetStore.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFinding(findings, "DatasetStore", "ReplayDatasetStoreUnavailable", ex.Message);
                return;
            }

            foreach (ReplayDatasetSummary summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(summary.Status, "Frozen", StringComparison.OrdinalIgnoreCase))
                {
                    AddFinding(
                        findings,
                        "Dataset",
                        "ReplayDatasetInvalid",
                        $"Replay dataset is not a valid frozen snapshot: {summary.Status}.",
                        datasetId: summary.DatasetId,
                        entityId: summary.DatasetId);
                    continue;
                }

                try
                {
                    string actualHash = await _datasetStore.ComputeSnapshotHashAsync(summary.DatasetId, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(summary.DatasetHash) ||
                        !string.Equals(actualHash, summary.DatasetHash, StringComparison.OrdinalIgnoreCase))
                    {
                        AddFinding(
                            findings,
                            "Dataset",
                            "ReplayDatasetHashMismatch",
                            "Replay dataset manifest hash does not match frozen image content.",
                            datasetId: summary.DatasetId,
                            entityId: summary.DatasetId);
                    }
                }
                catch (Exception ex)
                {
                    AddFinding(
                        findings,
                        "Dataset",
                        "ReplayDatasetIntegrityFailed",
                        ex.Message,
                        datasetId: summary.DatasetId,
                        entityId: summary.DatasetId);
                }
            }

            ScanDatasetStorageResidue(findings, cancellationToken);
        }

        private void ScanDatasetStorageResidue(
            List<ReplayIntegrityFinding> findings,
            CancellationToken cancellationToken)
        {
            if (_datasetStore is not FileReplayDatasetStore fileStore)
            {
                return;
            }

            string rootDirectory = fileStore.RootDirectory;
            if (!Directory.Exists(rootDirectory))
            {
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(rootDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(directory);
                if (!name.StartsWith(".", StringComparison.Ordinal) ||
                    !name.Contains(".staging-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddFinding(
                    findings,
                    "DatasetStorage",
                    "ReplayDatasetStagingOrphan",
                    "Replay dataset staging directory was left behind by an interrupted freeze operation.",
                    entityId: name,
                    severity: "Warning",
                    recommendation: "Inspect and archive or delete the staging directory after confirming no freeze operation is active.");
            }
        }

        private async Task ScanRunsAsync(
            List<ReplayIntegrityFinding> findings,
            CancellationToken cancellationToken)
        {
            if (_runStore == null)
            {
                return;
            }

            IReadOnlyList<ReplayRunRecord> records;
            try
            {
                records = await _runStore.ListRunRecordsAsync(1000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddFinding(findings, "RunStore", "ReplayRunStoreUnavailable", ex.Message);
                return;
            }

            foreach (ReplayRunRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(record.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
                {
                    if (string.Equals(record.Status, ReplayRunStatuses.Interrupted, StringComparison.Ordinal))
                    {
                        AddFinding(
                            findings,
                            "Run",
                            "ReplayRunInterruptedResidue",
                            "Replay run was interrupted before reaching a completed terminal report.",
                            datasetId: record.DatasetId,
                            runId: record.RunId,
                            entityId: record.RunId,
                            severity: "Warning",
                            recommendation: "Review the interrupted run and start a fresh Replay run before using it for approval.");
                    }
                    else if (!IsTerminalRunStatus(record.Status))
                    {
                        AddFinding(
                            findings,
                            "Run",
                            "ReplayRunNonTerminalResidue",
                            $"Replay run remains in a non-terminal status: {record.Status}.",
                            datasetId: record.DatasetId,
                            runId: record.RunId,
                            entityId: record.RunId,
                            recommendation: "Let startup recovery mark stale runs interrupted, or cancel the active run through the Replay coordinator.");
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(record.ReportJsonPath) || !File.Exists(record.ReportJsonPath))
                {
                    AddFinding(
                        findings,
                        "Run",
                        "ReplayRunReportMissing",
                        "Completed replay run report is missing.",
                        datasetId: record.DatasetId,
                        runId: record.RunId,
                        entityId: record.RunId);
                    continue;
                }

                try
                {
                    ReplayRunReport report = await _runStore.LoadReportAsync(record.RunId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(report.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal) ||
                        !string.Equals(report.ReportHash, SqliteReplayRunStore.ComputeReportHash(report), StringComparison.OrdinalIgnoreCase) ||
                        report.PolicyVersion == 0 ||
                        report.PolicySnapshot == null)
                    {
                        AddFinding(
                            findings,
                            "Run",
                            report.PolicyVersion == 0 || report.PolicySnapshot == null
                                ? "ReplayRunPolicySnapshotMissing"
                                : "ReplayRunReportHashMismatch",
                            "Completed replay run report no longer matches its persisted authority metadata.",
                            datasetId: record.DatasetId,
                            runId: record.RunId,
                            entityId: record.RunId);
                    }
                }
                catch (Exception ex)
                {
                    AddFinding(
                        findings,
                        "Run",
                        "ReplayRunReportInvalid",
                        ex.Message,
                        datasetId: record.DatasetId,
                        runId: record.RunId,
                        entityId: record.RunId);
                }
            }
        }

        private void ScanEvidence(
            List<ReplayIntegrityFinding> findings,
            CancellationToken cancellationToken)
        {
            if (_evidenceStore == null || _datasetStore == null || _runStore == null)
            {
                return;
            }

            ScanEvidenceStorageResidue(findings, cancellationToken);

            IReadOnlyList<ModelApprovalEvidence> evidenceRecords;
            try
            {
                evidenceRecords = _evidenceStore.ListEvidence();
            }
            catch (Exception ex)
            {
                AddFinding(findings, "EvidenceStore", "ReplayEvidenceStoreUnavailable", ex.Message);
                return;
            }

            HashSet<string> publishedEvidenceIds = GetPublishedEvidenceIds();
            foreach (ModelApprovalEvidence evidence in evidenceRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
                    !publishedEvidenceIds.Contains(evidence.EvidenceId))
                {
                    AddFinding(
                        findings,
                        "Evidence",
                        "ReplayEvidenceUnpublished",
                        "Replay approval evidence file is not referenced by any approved production manifest.",
                        modelId: evidence.CandidateModel.ModelId,
                        version: evidence.CandidateModel.Version,
                        datasetId: evidence.DatasetId,
                        runId: evidence.ReplayRunId,
                        evidenceId: evidence.EvidenceId,
                        entityId: string.IsNullOrWhiteSpace(evidence.EvidenceId)
                            ? "evidence:missing-id"
                            : evidence.EvidenceId,
                        severity: "Warning",
                        recommendation: "Keep the file for audit history or archive it after confirming no manifest rollback needs it.");
                }

                if (evidence.PolicyVersion == 0 || evidence.PolicySnapshot == null)
                {
                    AddFinding(
                        findings,
                        "Evidence",
                        "ReplayEvidencePolicySnapshotMissing",
                        "Replay approval evidence was created before policy snapshots were required.",
                        modelId: evidence.CandidateModel.ModelId,
                        version: evidence.CandidateModel.Version,
                        datasetId: evidence.DatasetId,
                        runId: evidence.ReplayRunId,
                        evidenceId: evidence.EvidenceId,
                        entityId: evidence.EvidenceId);
                    continue;
                }

                ModelRegistryEntry? candidateEntry = ResolveEntryForIdentity(evidence.CandidateModel);
                if (candidateEntry == null)
                {
                    AddFinding(
                        findings,
                        "Evidence",
                        "ReplayEvidenceCandidateMissing",
                        "Replay approval evidence candidate package is not present in the model registry.",
                        modelId: evidence.CandidateModel.ModelId,
                        version: evidence.CandidateModel.Version,
                        datasetId: evidence.DatasetId,
                        runId: evidence.ReplayRunId,
                        evidenceId: evidence.EvidenceId,
                        entityId: evidence.EvidenceId);
                    continue;
                }

                ModelApprovalEvidenceValidationResult result = _evidenceStore.ValidateEvidence(
                    ReplayModelIdentity.FromRegistryEntry(candidateEntry),
                    evidence.EvidenceId,
                    evidence.EvidenceHash,
                    _datasetStore,
                    _runStore);
                if (!result.Succeeded)
                {
                    AddFinding(
                        findings,
                        "Evidence",
                        result.ErrorCode,
                        result.Message,
                        modelId: evidence.CandidateModel.ModelId,
                        version: evidence.CandidateModel.Version,
                        datasetId: evidence.DatasetId,
                        runId: evidence.ReplayRunId,
                        evidenceId: evidence.EvidenceId,
                        entityId: evidence.EvidenceId);
                }
            }
        }

        private void ScanEvidenceStorageResidue(
            List<ReplayIntegrityFinding> findings,
            CancellationToken cancellationToken)
        {
            if (_evidenceStore is not FileModelApprovalEvidenceStore fileStore)
            {
                return;
            }

            string rootDirectory = fileStore.RootDirectory;
            if (!Directory.Exists(rootDirectory))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(rootDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ModelApprovalEvidence? evidence = JsonSerializer.Deserialize<ModelApprovalEvidence>(
                        File.ReadAllText(path),
                        ReplayJson.Options);
                    if (evidence == null || string.IsNullOrWhiteSpace(evidence.EvidenceId))
                    {
                        AddFinding(
                            findings,
                            "EvidenceStorage",
                            "ReplayEvidenceParseFailed",
                            "Replay approval evidence file is empty or missing its evidence id.",
                            evidenceId: Path.GetFileNameWithoutExtension(path),
                            entityId: Path.GetFileName(path),
                            recommendation: "Archive the malformed evidence file and recreate approval through a completed Replay run.");
                    }
                }
                catch (Exception ex)
                {
                    AddFinding(
                        findings,
                        "EvidenceStorage",
                        "ReplayEvidenceParseFailed",
                        ex.Message,
                        evidenceId: Path.GetFileNameWithoutExtension(path),
                        entityId: Path.GetFileName(path),
                        recommendation: "Archive the malformed evidence file and recreate approval through a completed Replay run.");
                }
            }
        }

        private void ScanApprovedModels(
            List<ReplayIntegrityFinding> findings,
            CancellationToken cancellationToken)
        {
            foreach (ModelRegistryEntry entry in _registry.Entries.Where(item => item.IsPackage && item.ApprovedForProduction))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Manifest?.Approval?.LegacyMigration != null &&
                    string.IsNullOrWhiteSpace(entry.Manifest.Approval.ReplayEvidenceId))
                {
                    AddFinding(
                        findings,
                        "ApprovedModel",
                        "ReplayLegacyApprovalActive",
                        "Approved model is running under one-time legacy migration without Replay evidence.",
                        modelId: entry.ModelId,
                        version: entry.Version,
                        entityId: $"{entry.ModelId}/{entry.Version}",
                        severity: "Warning",
                        recommendation: "Run Replay approval for this model to replace legacy compatibility with EvidenceApproved authority.");
                    continue;
                }

                ProductionModelReadinessResult result = _gate.ValidateEvidenceBacked(entry);
                if (!result.Succeeded)
                {
                    AddFinding(
                        findings,
                        "ApprovedModel",
                        result.ErrorCode,
                        result.Message,
                        modelId: entry.ModelId,
                        version: entry.Version,
                        entityId: $"{entry.ModelId}/{entry.Version}");
                    continue;
                }
            }
        }

        private HashSet<string> GetPublishedEvidenceIds()
        {
            return _registry.Entries
                .Where(item => item.IsPackage && item.ApprovedForProduction)
                .Select(item => item.Manifest?.Approval?.ReplayEvidenceId ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private ModelRegistryEntry? ResolveEntryForIdentity(ReplayModelIdentity identity)
        {
            return _registry.Entries.FirstOrDefault(entry =>
                entry.IsPackage &&
                string.Equals(entry.ModelId, identity.ModelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Version, identity.Version, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.ModelHash, identity.Sha256, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTerminalRunStatus(string status)
        {
            return string.Equals(status, ReplayRunStatuses.Completed, StringComparison.Ordinal) ||
                   string.Equals(status, ReplayRunStatuses.Failed, StringComparison.Ordinal) ||
                   string.Equals(status, ReplayRunStatuses.Canceled, StringComparison.Ordinal) ||
                   string.Equals(status, ReplayRunStatuses.Interrupted, StringComparison.Ordinal);
        }

        private static void AddFinding(
            List<ReplayIntegrityFinding> findings,
            string scope,
            string errorCode,
            string message,
            string modelId = "",
            string version = "",
            string datasetId = "",
            string runId = "",
            string evidenceId = "",
            string entityId = "",
            string severity = "Blocking",
            string recommendation = "")
        {
            findings.Add(new ReplayIntegrityFinding
            {
                Scope = scope ?? string.Empty,
                EntityId = string.IsNullOrWhiteSpace(entityId) ? $"{scope}:{errorCode}" : entityId,
                ModelId = modelId ?? string.Empty,
                Version = version ?? string.Empty,
                DatasetId = datasetId ?? string.Empty,
                RunId = runId ?? string.Empty,
                EvidenceId = evidenceId ?? string.Empty,
                Severity = severity ?? "Blocking",
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty,
                Recommendation = recommendation ?? string.Empty
            });
        }
    }
}
