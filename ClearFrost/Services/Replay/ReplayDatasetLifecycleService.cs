// ============================================================================
// File: ReplayDatasetLifecycleService.cs
// Description: Replay Dataset lifecycle application service
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Security;

namespace ClearFrost.Services.Replay
{
    internal sealed class ReplayDatasetLifecycleService
    {
        private static readonly HashSet<string> BlockingRunStatuses = new HashSet<string>(
            new[]
            {
                ReplayRunStatuses.Preparing,
                ReplayRunStatuses.Running,
                ReplayRunStatuses.BaselineRunning,
                ReplayRunStatuses.CandidateRunning,
                ReplayRunStatuses.Reporting,
                ReplayRunStatuses.CancelRequested,
                ReplayRunStatuses.Completed
            },
            StringComparer.Ordinal);

        private readonly IReplayDatasetStore _datasetStore;
        private readonly IReplayRunStore _runStore;
        private readonly IModelApprovalEvidenceStore _evidenceStore;
        private readonly ReplayAssetChangeCoordinator _assetCoordinator;
        private readonly OperationAuditService? _auditService;
        private readonly Func<string>? _operatorIdProvider;
        private readonly Func<ProductionRole>? _operatorRoleProvider;
        private readonly Func<string, bool>? _activeReplayDatasetPredicate;

        public ReplayDatasetLifecycleService(
            IReplayDatasetStore datasetStore,
            IReplayRunStore runStore,
            IModelApprovalEvidenceStore evidenceStore,
            ReplayAssetChangeCoordinator assetCoordinator,
            OperationAuditService? auditService = null,
            Func<string>? operatorIdProvider = null,
            Func<ProductionRole>? operatorRoleProvider = null,
            Func<string, bool>? activeReplayDatasetPredicate = null)
        {
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
            _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
            _assetCoordinator = assetCoordinator ?? throw new ArgumentNullException(nameof(assetCoordinator));
            _auditService = auditService;
            _operatorIdProvider = operatorIdProvider;
            _operatorRoleProvider = operatorRoleProvider;
            _activeReplayDatasetPredicate = activeReplayDatasetPredicate;
        }

        public Task<ReplayDatasetArchiveResult> ArchiveSnapshotAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            return _assetCoordinator.RunAsync(
                token => ArchiveSnapshotUnderLockAsync(datasetId, token),
                cancellationToken);
        }

        private async Task<ReplayDatasetArchiveResult> ArchiveSnapshotUnderLockAsync(
            string datasetId,
            CancellationToken cancellationToken)
        {
            if (!TryAuthorize(out string operatorId, out ProductionRole role, out ReplayDatasetArchiveResult authorizationFailure))
            {
                await AppendAuditAsync(OperationAuditStatus.Denied, authorizationFailure.Message, operatorId, role, cancellationToken)
                    .ConfigureAwait(false);
                return authorizationFailure;
            }

            string normalizedDatasetId = (datasetId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedDatasetId))
            {
                ReplayDatasetArchiveResult failure = ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetMissing",
                    "Replay dataset id is required.");
                await AppendAuditAsync(OperationAuditStatus.Denied, failure.Message, operatorId, role, cancellationToken)
                    .ConfigureAwait(false);
                return failure;
            }

            await AppendAuditAsync(OperationAuditStatus.Requested, $"Archive dataset {normalizedDatasetId}", operatorId, role, cancellationToken)
                .ConfigureAwait(false);

            ReplayDatasetArchiveResult? checkFailure = await ValidateArchiveAllowedAsync(normalizedDatasetId, cancellationToken)
                .ConfigureAwait(false);
            if (checkFailure != null)
            {
                await AppendAuditAsync(OperationAuditStatus.Denied, checkFailure.Message, operatorId, role, cancellationToken)
                    .ConfigureAwait(false);
                return checkFailure;
            }

            ReplayDatasetArchiveResult result = await _datasetStore.ArchiveSnapshotAsync(normalizedDatasetId, cancellationToken)
                .ConfigureAwait(false);
            await AppendAuditAsync(
                result.Succeeded ? OperationAuditStatus.Succeeded : OperationAuditStatus.Denied,
                result.Succeeded ? result.ArchivePath : result.Message,
                operatorId,
                role,
                cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task<ReplayDatasetArchiveResult?> ValidateArchiveAllowedAsync(
            string datasetId,
            CancellationToken cancellationToken)
        {
            try
            {
                await _datasetStore.LoadSnapshotAsync(datasetId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ReplayDatasetArchiveResult.Fail("ReplayDatasetMissing", ex.Message);
            }

            if (_activeReplayDatasetPredicate?.Invoke(datasetId) == true)
            {
                return ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetRunActive",
                    "Replay dataset is referenced by the current active replay run.");
            }

            if (_evidenceStore.ListEvidence()
                .Any(evidence => string.Equals(evidence.DatasetId, datasetId, StringComparison.OrdinalIgnoreCase)))
            {
                return ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetEvidenceReferenced",
                    "Replay dataset is referenced by approval evidence.");
            }

            IReadOnlyList<ReplayRunRecord> runs = await _runStore.ListRunRecordsAsync(1000, cancellationToken)
                .ConfigureAwait(false);
            ReplayRunRecord? blockingRun = runs.FirstOrDefault(run =>
                string.Equals(run.DatasetId, datasetId, StringComparison.OrdinalIgnoreCase) &&
                BlockingRunStatuses.Contains(run.Status));
            if (blockingRun != null)
            {
                return ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetRunReferenced",
                    $"Replay dataset is referenced by replay run {blockingRun.RunId} in status {blockingRun.Status}.");
            }

            if (_datasetStore is FileReplayDatasetStore fileStore &&
                fileStore.HasStagingDirectory(datasetId))
            {
                return ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetStagingPresent",
                    "Replay dataset has a staging directory and cannot be archived safely.");
            }

            return null;
        }

        private bool TryAuthorize(
            out string operatorId,
            out ProductionRole role,
            out ReplayDatasetArchiveResult failure)
        {
            operatorId = string.Empty;
            role = ProductionRole.Operator;
            failure = ReplayDatasetArchiveResult.Ok(string.Empty);

            if (_operatorIdProvider == null || _operatorRoleProvider == null)
            {
                failure = ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetArchiveAuthorizationProviderMissing",
                    "Replay dataset archive requires backend operator and role providers.");
                return false;
            }

            operatorId = (_operatorIdProvider.Invoke() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(operatorId))
            {
                failure = ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetArchiveOperatorMissing",
                    "Replay dataset archive operator id is required.");
                return false;
            }

            role = _operatorRoleProvider.Invoke();
            if (!ProductionAuthorizationService.Authorize(
                    role,
                    ProductionOperation.EngineeringChange,
                    out string denialReason))
            {
                failure = ReplayDatasetArchiveResult.Fail("ReplayDatasetArchiveUnauthorized", denialReason);
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
                    Operation = "ReplayDatasetArchive",
                    Status = status,
                    OperatorId = operatorId,
                    Role = role,
                    Details = details ?? string.Empty,
                    FailureBlocker = status == OperationAuditStatus.Denied || status == OperationAuditStatus.Failed
                        ? details ?? string.Empty
                        : string.Empty
                }, cancellationToken);
        }
    }
}
