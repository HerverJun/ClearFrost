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
        private readonly ReplayAssetChangeCoordinator _assetCoordinator;

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
            Func<ProductionRole>? approverRoleProvider = null,
            ReplayAssetChangeCoordinator? assetCoordinator = null)
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
            _assetCoordinator = assetCoordinator ?? new ReplayAssetChangeCoordinator();
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

            return await _assetCoordinator.RunAsync(
                token => ApproveCandidateUnderAssetLockAsync(request, approverId, approverRole, token),
                cancellationToken).ConfigureAwait(false);
        }

        public IReadOnlyList<ModelApprovalEvidence> ListEvidence()
        {
            return _evidenceStore.ListEvidence();
        }

        private async Task<ReplayApprovalResult> ApproveCandidateUnderAssetLockAsync(
            ReplayApprovalRequest request,
            string approverId,
            ProductionRole approverRole,
            CancellationToken cancellationToken)
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

            if (!TryValidateCandidateAssets(
                    context.CandidateEntry,
                    out string assetErrorCode,
                    out string assetErrorMessage))
            {
                await AppendAuditAsync(
                    OperationAuditStatus.Denied,
                    assetErrorMessage,
                    approverId,
                    approverRole,
                    cancellationToken)
                    .ConfigureAwait(false);
                return ReplayApprovalResult.Fail(assetErrorCode, assetErrorMessage);
            }

            if (!TryResolveCandidateAssetPaths(
                    context.CandidateEntry,
                    out string manifestPath,
                    out _,
                    out _,
                    out string readErrorCode,
                    out string readErrorMessage))
            {
                await AppendAuditAsync(
                    OperationAuditStatus.Denied,
                    readErrorMessage,
                    approverId,
                    approverRole,
                    cancellationToken)
                    .ConfigureAwait(false);
                return ReplayApprovalResult.Fail(readErrorCode, readErrorMessage);
            }

            byte[] originalManifest = await ReadCandidateManifestBytesAsync(
                context.CandidateEntry,
                cancellationToken).ConfigureAwait(false);
            ModelApprovalEvidence? evidence = null;
            var compensationFailures = new List<string>();

            try
            {
                evidence = _evidenceStore.SaveEvidence(
                    context.Report,
                    approverId,
                    context.Dataset.RootDirectory,
                    context.Report.PolicyHash);

                ModelPackageManifest manifest = ReadCandidateManifest(context.CandidateEntry);

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

                ProductionModelReadinessResult gate = _productionGate.ValidateEvidenceBacked(refreshedEntry);
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

            if (!ReplayArtifactHashing.TryComputeReportHash(report, out string reportHash, out string reportHashError))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalReportHashVersionInvalid", reportHashError);
            }

            if (!string.Equals(report.ReportHash, reportHash, StringComparison.OrdinalIgnoreCase))
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

            if (!IsCandidateRegistryStateEligibleForApproval(entry))
            {
                return ReplayApprovalContext.Fail(
                    "ReplayApprovalCandidateRegistryBlocked",
                    string.IsNullOrWhiteSpace(entry.Message)
                        ? "Candidate package is blocked by model registry validation."
                        : entry.Message);
            }

            if (!File.Exists(entry.ManifestPath) || !File.Exists(entry.ModelPath))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalPackageMissing", "Candidate manifest/model file is missing.");
            }

            if (!TryValidateCandidateAssets(entry, out string assetErrorCode, out string assetErrorMessage))
            {
                return ReplayApprovalContext.Fail(assetErrorCode, assetErrorMessage);
            }

            string currentModelHash = FileReplayDatasetStore.ComputeSha256(entry.ModelPath);
            if (!string.Equals(currentModelHash, report.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalContext.Fail("ReplayApprovalCurrentModelHashMismatch", "Current candidate model file no longer matches replay report.");
            }

            ReplayAcceptancePolicyOptions? policySnapshot = report.PolicySnapshot;
            if (policySnapshot == null ||
                !ReplayAcceptancePolicyOptions.IsSupportedVersion(policySnapshot.Version) ||
                report.PolicyVersion != policySnapshot.Version)
            {
                return ReplayApprovalContext.Fail(
                    "ReplayApprovalPolicySnapshotInvalid",
                    "Replay report policy snapshot is invalid or unsupported.");
            }

            if (!ReplayArtifactHashing.TryComputePolicyHash(policySnapshot, out string policyHash, out string policyHashError) ||
                !string.Equals(report.PolicyHash, policyHash, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayApprovalContext.Fail(
                    "ReplayApprovalPolicySnapshotInvalid",
                    string.IsNullOrWhiteSpace(policyHashError)
                        ? "Replay report policy hash does not match policy snapshot."
                        : policyHashError);
            }

            ReplayApprovalDecision decision = _policy.Evaluate(report, policySnapshot);
            if (!decision.Approved)
            {
                return ReplayApprovalContext.Fail("ReplayApprovalPolicyRejected", string.Join("; ", decision.Reasons));
            }

            return ReplayApprovalContext.Ok(report, dataset, entry);
        }

        private static bool TryValidateCandidateAssets(
            ModelRegistryEntry entry,
            out string errorCode,
            out string message)
        {
            errorCode = string.Empty;
            message = string.Empty;

            if (!TryResolveCandidateAssetPaths(
                    entry,
                    out string manifestPath,
                    out string modelPath,
                    out string packageDirectory,
                    out errorCode,
                    out message))
            {
                return false;
            }

            ModelPackageManifest manifest;
            try
            {
                manifest = ReadCandidateManifest(entry);
            }
            catch (Exception ex)
            {
                errorCode = "ReplayApprovalManifestParseFailed";
                message = ex.Message;
                return false;
            }

            if (!string.Equals(manifest.ModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Version, entry.Version, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "ReplayApprovalManifestIdentityMismatch";
                message = "Current candidate manifest identity no longer matches the registry entry.";
                return false;
            }

            string expectedHash = manifest.EffectiveHash;
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !string.Equals(expectedHash, entry.ModelHash, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "ReplayApprovalManifestHashMismatch";
                message = "Current candidate manifest hash metadata no longer matches the registry entry.";
                return false;
            }

            if (!ModelContractMatchesRegistry(entry, manifest))
            {
                errorCode = "ReplayApprovalManifestContractMismatch";
                message = $"Current candidate manifest model contract no longer matches the registry entry: {DescribeModelContractMismatch(entry, manifest)}";
                return false;
            }

            string modelFileName = string.IsNullOrWhiteSpace(manifest.ModelFileName)
                ? "model.onnx"
                : manifest.ModelFileName.Trim();
            if (!ModelPackagePathGuard.TryResolveModelPath(
                    packageDirectory,
                    modelFileName,
                    out string declaredModelPath,
                    out string pathError,
                    "Manifest ModelFileName"))
            {
                errorCode = "ReplayApprovalManifestModelPathInvalid";
                message = pathError;
                return false;
            }

            string actualModelPath = ModelPackagePathGuard.GetFullPathSafe(modelPath);
            if (!string.Equals(declaredModelPath, actualModelPath, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "ReplayApprovalManifestModelPathMismatch";
                message = "Current candidate model path no longer matches manifest ModelFileName.";
                return false;
            }

            if (ModelPackagePathGuard.ModelPathHasReparsePoint(packageDirectory, declaredModelPath))
            {
                errorCode = "ReplayApprovalModelPathReparsePoint";
                message = "Candidate model file path contains a reparse point.";
                return false;
            }

            return true;
        }

        private static async Task<byte[]> ReadCandidateManifestBytesAsync(
            ModelRegistryEntry entry,
            CancellationToken cancellationToken)
        {
            if (!TryResolveCandidateAssetPaths(
                    entry,
                    out string manifestPath,
                    out _,
                    out _,
                    out _,
                    out string message))
            {
                throw new IOException(message);
            }

            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (!TryResolveCandidateAssetPaths(
                    entry,
                    out _,
                    out _,
                    out _,
                    out _,
                    out message))
            {
                throw new IOException(message);
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (!TryResolveCandidateAssetPaths(
                    entry,
                    out _,
                    out _,
                    out _,
                    out _,
                    out message))
            {
                throw new IOException(message);
            }

            return buffer.ToArray();
        }

        private static ModelPackageManifest ReadCandidateManifest(ModelRegistryEntry entry)
        {
            if (!TryResolveCandidateAssetPaths(
                    entry,
                    out string manifestPath,
                    out _,
                    out _,
                    out _,
                    out string message))
            {
                throw new IOException(message);
            }

            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (!TryResolveCandidateAssetPaths(
                    entry,
                    out _,
                    out _,
                    out _,
                    out _,
                    out message))
            {
                throw new IOException(message);
            }

            ModelPackageManifest manifest =
                JsonSerializer.Deserialize<ModelPackageManifest>(stream, ReplayJson.Options) ??
                new ModelPackageManifest();

            if (!TryResolveCandidateAssetPaths(
                    entry,
                    out _,
                    out _,
                    out _,
                    out _,
                    out message))
            {
                throw new IOException(message);
            }

            return manifest;
        }

        private static bool TryResolveCandidateAssetPaths(
            ModelRegistryEntry entry,
            out string manifestPath,
            out string modelPath,
            out string packageDirectory,
            out string errorCode,
            out string message)
        {
            manifestPath = string.Empty;
            modelPath = string.Empty;
            packageDirectory = string.Empty;
            errorCode = string.Empty;
            message = string.Empty;

            if (string.IsNullOrWhiteSpace(entry.ManifestPath) ||
                string.IsNullOrWhiteSpace(entry.ModelPath))
            {
                errorCode = "ReplayApprovalPackageMissing";
                message = "Candidate manifest/model file is missing.";
                return false;
            }

            try
            {
                manifestPath = Path.GetFullPath(entry.ManifestPath);
                modelPath = Path.GetFullPath(entry.ModelPath);
                packageDirectory = Path.GetDirectoryName(manifestPath) ?? string.Empty;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                errorCode = "ReplayApprovalPackagePathInvalid";
                message = $"Candidate manifest/model path is invalid: {ex.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                errorCode = "ReplayApprovalPackageDirectoryInvalid";
                message = "Candidate package directory is invalid.";
                return false;
            }

            if (!File.Exists(manifestPath) || !File.Exists(modelPath))
            {
                errorCode = "ReplayApprovalPackageMissing";
                message = "Candidate manifest/model file is missing.";
                return false;
            }

            if (!string.Equals(Path.GetFileName(manifestPath), "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "ReplayApprovalManifestPathInvalid";
                message = "Candidate manifest file name is invalid.";
                return false;
            }

            if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(packageDirectory))
            {
                errorCode = "ReplayApprovalPackageDirectoryReparsePoint";
                message = "Candidate package directory is a reparse point.";
                return false;
            }

            string modelDirectory = Path.GetDirectoryName(modelPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelDirectory) ||
                ModelPackagePathGuard.DirectoryPathHasReparsePoint(modelDirectory))
            {
                errorCode = "ReplayApprovalModelPathReparsePoint";
                message = "Candidate model file path contains a reparse point.";
                return false;
            }

            if (ModelPackagePathGuard.HasReparsePoint(new FileInfo(manifestPath)))
            {
                errorCode = "ReplayApprovalManifestReparsePoint";
                message = "Candidate manifest file is a reparse point.";
                return false;
            }

            if (ModelPackagePathGuard.HasReparsePoint(new FileInfo(modelPath)))
            {
                errorCode = "ReplayApprovalModelReparsePoint";
                message = "Candidate model file is a reparse point.";
                return false;
            }

            return true;
        }

        private static bool ModelContractMatchesRegistry(ModelRegistryEntry entry, ModelPackageManifest manifest)
        {
            IReadOnlyList<string> manifestLabels = manifest.Labels != null
                ? manifest.Labels
                : Array.Empty<string>();
            IReadOnlyList<string> entryLabels = ResolveEffectiveEntryLabels(entry);
            int entryInputWidth = ResolveEffectiveEntryInputWidth(entry);
            int entryInputHeight = ResolveEffectiveEntryInputHeight(entry);
            string entryTaskType = ResolveEffectiveEntryTaskType(entry);
            string entryPostprocessorKey = ResolveEffectiveEntryPostprocessorKey(entry);
            string entryScoreNormalization = ResolveEffectiveEntryScoreNormalization(entry);
            IReadOnlyDictionary<string, string>? entryPostprocessOptions = ResolveEffectiveEntryPostprocessOptions(entry);
            return manifest.InputWidth == entryInputWidth &&
                   manifest.InputHeight == entryInputHeight &&
                   string.Equals(manifest.TaskType ?? string.Empty, entryTaskType, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(manifest.PostprocessorKey ?? string.Empty, entryPostprocessorKey, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(manifest.ScoreNormalization ?? string.Empty, entryScoreNormalization, StringComparison.OrdinalIgnoreCase) &&
                   DictionaryMatches(manifest.PostprocessOptions, entryPostprocessOptions) &&
                   manifestLabels.Count == entryLabels.Count &&
                   manifestLabels.Zip(entryLabels, (left, right) => string.Equals(left, right, StringComparison.Ordinal)).All(match => match);
        }

        private static string DescribeModelContractMismatch(ModelRegistryEntry entry, ModelPackageManifest manifest)
        {
            var mismatches = new List<string>();
            int entryInputWidth = ResolveEffectiveEntryInputWidth(entry);
            int entryInputHeight = ResolveEffectiveEntryInputHeight(entry);
            if (manifest.InputWidth != entryInputWidth || manifest.InputHeight != entryInputHeight)
            {
                mismatches.Add($"InputSize manifest={manifest.InputWidth}x{manifest.InputHeight}, registry={entryInputWidth}x{entryInputHeight}");
            }

            AddStringMismatch(mismatches, "TaskType", manifest.TaskType, ResolveEffectiveEntryTaskType(entry));
            AddStringMismatch(mismatches, "PostprocessorKey", manifest.PostprocessorKey, ResolveEffectiveEntryPostprocessorKey(entry));
            AddStringMismatch(mismatches, "ScoreNormalization", manifest.ScoreNormalization, ResolveEffectiveEntryScoreNormalization(entry));

            IReadOnlyDictionary<string, string> manifestOptions = NormalizeDictionary(manifest.PostprocessOptions);
            IReadOnlyDictionary<string, string> entryOptions = NormalizeDictionary(ResolveEffectiveEntryPostprocessOptions(entry));
            if (!DictionaryMatches(manifestOptions, entryOptions))
            {
                mismatches.Add(DescribeDictionaryMismatch("PostprocessOptions", manifestOptions, entryOptions));
            }

            IReadOnlyList<string> manifestLabels = manifest.Labels != null
                ? manifest.Labels
                : Array.Empty<string>();
            IReadOnlyList<string> entryLabels = ResolveEffectiveEntryLabels(entry);
            if (manifestLabels.Count != entryLabels.Count)
            {
                mismatches.Add($"Labels count manifest={manifestLabels.Count}, registry={entryLabels.Count}");
            }
            else
            {
                for (int index = 0; index < manifestLabels.Count; index++)
                {
                    if (!string.Equals(manifestLabels[index], entryLabels[index], StringComparison.Ordinal))
                    {
                        mismatches.Add($"Labels[{index}] manifest={manifestLabels[index]}, registry={entryLabels[index]}");
                        break;
                    }
                }
            }

            return mismatches.Count == 0
                ? "unknown contract field"
                : string.Join("; ", mismatches);
        }

        private static string ResolveEffectiveEntryTaskType(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveTaskType();
        }

        private static string ResolveEffectiveEntryPostprocessorKey(ModelRegistryEntry entry)
        {
            return entry.GetEffectivePostprocessorKey();
        }

        private static string ResolveEffectiveEntryScoreNormalization(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveScoreNormalization();
        }

        private static int ResolveEffectiveEntryInputWidth(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveInputWidth();
        }

        private static int ResolveEffectiveEntryInputHeight(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveInputHeight();
        }

        private static IReadOnlyList<string> ResolveEffectiveEntryLabels(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveLabels();
        }

        private static IReadOnlyDictionary<string, string>? ResolveEffectiveEntryPostprocessOptions(ModelRegistryEntry entry)
        {
            return entry.GetEffectivePostprocessOptions();
        }

        private static void AddStringMismatch(List<string> mismatches, string fieldName, string? manifestValue, string? entryValue)
        {
            string left = manifestValue ?? string.Empty;
            string right = entryValue ?? string.Empty;
            if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{fieldName} manifest={left}, registry={right}");
            }
        }

        private static string DescribeDictionaryMismatch(
            string fieldName,
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
        {
            foreach (KeyValuePair<string, string> pair in left)
            {
                if (!right.TryGetValue(pair.Key, out string? rightValue))
                {
                    return $"{fieldName} missing registry key={pair.Key}";
                }

                if (!string.Equals(pair.Value ?? string.Empty, rightValue ?? string.Empty, StringComparison.Ordinal))
                {
                    return $"{fieldName}[{pair.Key}] manifest={pair.Value ?? string.Empty}, registry={rightValue ?? string.Empty}";
                }
            }

            foreach (KeyValuePair<string, string> pair in right)
            {
                if (!left.ContainsKey(pair.Key))
                {
                    return $"{fieldName} unexpected registry key={pair.Key}";
                }
            }

            return $"{fieldName} differs";
        }

        private static bool DictionaryMatches(
            IReadOnlyDictionary<string, string>? left,
            IReadOnlyDictionary<string, string>? right)
        {
            IReadOnlyDictionary<string, string> normalizedLeft = NormalizeDictionary(left);
            IReadOnlyDictionary<string, string> normalizedRight = NormalizeDictionary(right);
            if (normalizedLeft.Count != normalizedRight.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> pair in normalizedLeft)
            {
                if (!normalizedRight.TryGetValue(pair.Key, out string? rightValue) ||
                    !string.Equals(pair.Value ?? string.Empty, rightValue ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyDictionary<string, string> NormalizeDictionary(IReadOnlyDictionary<string, string>? value)
        {
            if (value == null || value.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in value)
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || normalized.ContainsKey(key))
                {
                    continue;
                }

                normalized[key] = pair.Value ?? string.Empty;
            }

            return normalized;
        }

        private static bool IsCandidateRegistryStateEligibleForApproval(ModelRegistryEntry entry)
        {
            if (entry.Status == ModelRegistryStatus.Ready)
            {
                return true;
            }

            if (entry.Status != ModelRegistryStatus.Blocked)
            {
                return false;
            }

            return string.Equals(
                       entry.ApprovalStatus,
                       ModelApprovalStatuses.Pending,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       entry.Message?.Trim(),
                       "Model is not approved for production.",
                       StringComparison.OrdinalIgnoreCase);
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
