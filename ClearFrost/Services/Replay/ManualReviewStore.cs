using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Security;
using ClearFrost.Interfaces;
using Microsoft.Data.Sqlite;

namespace ClearFrost.Services.Replay
{
    public static class ManualReviewStatuses
    {
        public const string Pending = "Pending";
        public const string Reviewed = "Reviewed";
    }

    public sealed class ManualReviewQuery
    {
        public DetectionReplayQuery ReplayQuery { get; init; } = new DetectionReplayQuery();
        public string ReviewStatus { get; init; } = string.Empty;
    }

    public sealed class ManualReviewTraceItem
    {
        public long DetectionRecordId { get; init; }
        public string InspectionId { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public bool SystemIsQualified { get; init; }
        public string SystemDecision => SystemIsQualified ? ReplayDecisions.OK : ReplayDecisions.NG;
        public string ReviewStatus { get; init; } = ManualReviewStatuses.Pending;
        public ReplayManualReviewRecord? Review { get; init; }
    }

    public sealed class ManualReviewSaveRequest
    {
        public long DetectionRecordId { get; init; }
        public string InspectionId { get; init; } = string.Empty;
        public string SampleId { get; init; } = string.Empty;
        public string GroundTruth { get; init; } = ReplayDecisions.OK;
        public string Disposition { get; init; } = ReplayReviewDispositions.Pending;
        public string ReviewerId { get; init; } = string.Empty;
        public string ReviewerRole { get; init; } = ProductionRole.Engineer.ToString();
        public long? ExpectedRevision { get; init; }
        public string Notes { get; init; } = string.Empty;
    }

    public sealed class ManualReviewSaveResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public ReplayManualReviewRecord? Record { get; init; }

        public static ManualReviewSaveResult Ok(ReplayManualReviewRecord record)
        {
            return new ManualReviewSaveResult
            {
                Succeeded = true,
                Record = record
            };
        }

        public static ManualReviewSaveResult Fail(string errorCode, string message)
        {
            return Fail(errorCode, message, null);
        }

        public static ManualReviewSaveResult Fail(string errorCode, string message, ReplayManualReviewRecord? currentRecord)
        {
            return new ManualReviewSaveResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty,
                Record = currentRecord
            };
        }
    }

    public interface IManualReviewStore
    {
        Task<IReadOnlyList<ManualReviewTraceItem>> QueryAsync(
            ManualReviewQuery query,
            CancellationToken cancellationToken = default);

        Task<ManualReviewSaveResult> SaveReviewAsync(
            ManualReviewSaveRequest request,
            CancellationToken cancellationToken = default);
    }

    internal sealed class SqliteManualReviewStore : IManualReviewStore
    {
        private const int BusyTimeoutMs = 5000;
        private readonly IDatabaseService _databaseService;
        private readonly string _dbPath;
        private readonly OperationAuditService? _auditService;
        private readonly Func<string>? _operatorIdProvider;
        private readonly Func<ProductionRole>? _operatorRoleProvider;

        public SqliteManualReviewStore(
            IDatabaseService databaseService,
            string dbPath,
            OperationAuditService? auditService = null,
            Func<string>? operatorIdProvider = null,
            Func<ProductionRole>? operatorRoleProvider = null)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _dbPath = string.IsNullOrWhiteSpace(dbPath)
                ? throw new ArgumentException("Manual review database path is required.", nameof(dbPath))
                : dbPath;
            _auditService = auditService;
            _operatorIdProvider = operatorIdProvider;
            _operatorRoleProvider = operatorRoleProvider;
        }

        public async Task<IReadOnlyList<ManualReviewTraceItem>> QueryAsync(
            ManualReviewQuery query,
            CancellationToken cancellationToken = default)
        {
            query ??= new ManualReviewQuery();
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            List<DetectionRecord> records = await _databaseService.GetReplayRecordsAsync(query.ReplayQuery)
                .ConfigureAwait(false);
            Dictionary<long, ReplayManualReviewRecord> reviews = await LoadReviewsAsync(
                records.Select(record => record.Id),
                cancellationToken).ConfigureAwait(false);

            IEnumerable<ManualReviewTraceItem> items = records.Select(record =>
            {
                reviews.TryGetValue(record.Id, out ReplayManualReviewRecord? review);
                return new ManualReviewTraceItem
                {
                    DetectionRecordId = record.Id,
                    InspectionId = record.InspectionId ?? string.Empty,
                    Timestamp = record.Timestamp,
                    SystemIsQualified = record.IsQualified,
                    ReviewStatus = review == null ? ManualReviewStatuses.Pending : ManualReviewStatuses.Reviewed,
                    Review = review
                };
            });

            if (!string.IsNullOrWhiteSpace(query.ReviewStatus))
            {
                items = items.Where(item => string.Equals(item.ReviewStatus, query.ReviewStatus, StringComparison.OrdinalIgnoreCase));
            }

            return items.ToList();
        }

        public async Task<ManualReviewSaveResult> SaveReviewAsync(
            ManualReviewSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.DetectionRecordId <= 0)
            {
                return ManualReviewSaveResult.Fail("ManualReviewDetectionRecordIdMissing", "DetectionRecordId is required.");
            }

            if (string.IsNullOrWhiteSpace(request.InspectionId))
            {
                return ManualReviewSaveResult.Fail("ManualReviewInspectionIdMissing", "InspectionId is required.");
            }

            if (!TryResolveAuthorizedReviewer(
                    out string reviewerId,
                    out ProductionRole reviewerRole,
                    out string authorizationErrorCode,
                    out string authorizationMessage))
            {
                await AppendAuditAsync(
                    request,
                    OperationAuditStatus.Denied,
                    authorizationMessage,
                    reviewerId,
                    reviewerRole,
                    cancellationToken).ConfigureAwait(false);
                return ManualReviewSaveResult.Fail(authorizationErrorCode, authorizationMessage);
            }

            DetectionRecord? detectionRecord = await _databaseService.GetDetectionRecordByIdAsync(request.DetectionRecordId)
                .ConfigureAwait(false);
            if (detectionRecord == null)
            {
                return ManualReviewSaveResult.Fail(
                    "ManualReviewDetectionRecordMissing",
                    $"Detection record is required for manual review: {request.DetectionRecordId}.");
            }

            if (!string.Equals(detectionRecord.InspectionId, request.InspectionId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return ManualReviewSaveResult.Fail(
                    "ManualReviewRecordBindingMismatch",
                    $"DetectionRecordId/InspectionId mismatch. Id={request.DetectionRecordId}; InspectionId={request.InspectionId}.");
            }

            string systemDecision = detectionRecord.IsQualified ? ReplayDecisions.OK : ReplayDecisions.NG;
            if (!TryNormalizeReview(
                    request,
                    systemDecision,
                    out string disposition,
                    out string groundTruth,
                    out string validationError))
            {
                return ManualReviewSaveResult.Fail("ManualReviewDispositionInvalid", validationError);
            }

            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                ReplayManualReviewRecord? existing = await LoadReviewAsync(
                    connection,
                    transaction,
                    request.DetectionRecordId,
                    cancellationToken).ConfigureAwait(false);
                long existingRevision = existing?.Revision ?? 0;
                if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != existingRevision)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    await AppendAuditAsync(
                        request,
                        OperationAuditStatus.Denied,
                        "Review revision conflict",
                        reviewerId,
                        reviewerRole,
                        cancellationToken)
                        .ConfigureAwait(false);
                    return ManualReviewSaveResult.Fail(
                        "ReviewRevisionConflict",
                        $"Review revision conflict. Expected={request.ExpectedRevision.Value}; Actual={existingRevision}.",
                        existing);
                }

                var record = new ReplayManualReviewRecord
                {
                    SampleId = string.IsNullOrWhiteSpace(request.SampleId)
                        ? request.InspectionId.Trim()
                        : request.SampleId.Trim(),
                    InspectionId = request.InspectionId.Trim(),
                    GroundTruth = groundTruth,
                    SystemDecision = systemDecision,
                    Disposition = disposition,
                    ReviewerId = reviewerId,
                    ReviewerRole = reviewerRole.ToString(),
                    Revision = existingRevision + 1,
                    ReviewedAt = DateTimeOffset.UtcNow,
                    Notes = request.Notes ?? string.Empty
                };

                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO ManualReviewRecords
                        (DetectionRecordId, InspectionId, SampleId, GroundTruth, SystemDecision, Disposition, ReviewerId, ReviewerRole, Revision, ReviewedAt, Notes)
                    VALUES
                        ($detectionRecordId, $inspectionId, $sampleId, $groundTruth, $systemDecision, $disposition, $reviewerId, $reviewerRole, $revision, $reviewedAt, $notes)
                    ON CONFLICT(DetectionRecordId) DO UPDATE SET
                        SampleId = excluded.SampleId,
                        InspectionId = excluded.InspectionId,
                        GroundTruth = excluded.GroundTruth,
                        SystemDecision = excluded.SystemDecision,
                        Disposition = excluded.Disposition,
                        ReviewerId = excluded.ReviewerId,
                        ReviewerRole = excluded.ReviewerRole,
                        Revision = excluded.Revision,
                        ReviewedAt = excluded.ReviewedAt,
                        Notes = excluded.Notes;
                ";
                command.Parameters.AddWithValue("$detectionRecordId", request.DetectionRecordId);
                command.Parameters.AddWithValue("$inspectionId", record.InspectionId);
                command.Parameters.AddWithValue("$sampleId", record.SampleId);
                command.Parameters.AddWithValue("$groundTruth", record.GroundTruth);
                command.Parameters.AddWithValue("$systemDecision", record.SystemDecision);
                command.Parameters.AddWithValue("$disposition", record.Disposition);
                command.Parameters.AddWithValue("$reviewerId", record.ReviewerId);
                command.Parameters.AddWithValue("$reviewerRole", record.ReviewerRole);
                command.Parameters.AddWithValue("$revision", record.Revision);
                command.Parameters.AddWithValue("$reviewedAt", record.ReviewedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$notes", record.Notes);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                await AppendAuditAsync(
                    request,
                    OperationAuditStatus.Succeeded,
                    "Manual review saved",
                    reviewerId,
                    reviewerRole,
                    cancellationToken)
                    .ConfigureAwait(false);
                return ManualReviewSaveResult.Ok(record);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await AppendAuditAsync(
                    request,
                    OperationAuditStatus.Failed,
                    ex.Message,
                    reviewerId,
                    reviewerRole,
                    CancellationToken.None)
                    .ConfigureAwait(false);
                return ManualReviewSaveResult.Fail("ManualReviewSaveFailed", ex.Message);
            }
        }

        private async Task<Dictionary<long, ReplayManualReviewRecord>> LoadReviewsAsync(
            IEnumerable<long> detectionRecordIds,
            CancellationToken cancellationToken)
        {
            var ids = detectionRecordIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var result = new Dictionary<long, ReplayManualReviewRecord>();
            if (ids.Count == 0)
            {
                return result;
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            foreach (long id in ids)
            {
                ReplayManualReviewRecord? record = await LoadReviewAsync(connection, null, id, cancellationToken)
                    .ConfigureAwait(false);
                if (record != null)
                {
                    result[id] = record;
                }
            }

            return result;
        }

        private static async Task<ReplayManualReviewRecord?> LoadReviewAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            long detectionRecordId,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT InspectionId, SampleId, GroundTruth, ReviewerId, Revision, ReviewedAt, Notes
                , SystemDecision, Disposition, ReviewerRole
                FROM ManualReviewRecords
                WHERE DetectionRecordId = $detectionRecordId
                LIMIT 1;
            ";
            command.Parameters.AddWithValue("$detectionRecordId", detectionRecordId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            DateTimeOffset reviewedAt = DateTimeOffset.TryParse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            return new ReplayManualReviewRecord
            {
                InspectionId = reader.GetString(0),
                SampleId = reader.GetString(1),
                GroundTruth = reader.GetString(2),
                ReviewerId = reader.GetString(3),
                Revision = reader.GetInt64(4),
                ReviewedAt = reviewedAt,
                Notes = reader.GetString(6),
                SystemDecision = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                Disposition = reader.IsDBNull(8) ? ReplayReviewDispositions.Invalid : reader.GetString(8),
                ReviewerRole = reader.IsDBNull(9) ? string.Empty : reader.GetString(9)
            };
        }

        private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            string directory = System.IO.Path.GetDirectoryName(_dbPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ManualReviewRecords (
                    DetectionRecordId INTEGER PRIMARY KEY,
                    InspectionId TEXT NOT NULL,
                    SampleId TEXT NOT NULL,
                    GroundTruth TEXT NOT NULL,
                    SystemDecision TEXT NOT NULL DEFAULT '',
                    Disposition TEXT NOT NULL DEFAULT 'Invalid',
                    ReviewerId TEXT NOT NULL,
                    ReviewerRole TEXT NOT NULL DEFAULT '',
                    Revision INTEGER NOT NULL,
                    ReviewedAt TEXT NOT NULL,
                    Notes TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_manual_review_inspection ON ManualReviewRecords(InspectionId);
                CREATE INDEX IF NOT EXISTS idx_manual_review_status ON ManualReviewRecords(GroundTruth, ReviewedAt);
                CREATE TABLE IF NOT EXISTS ManualReviewMigrationQuarantine (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourceRowId INTEGER NOT NULL,
                    SourceDetectionRecordId INTEGER NOT NULL DEFAULT 0,
                    InspectionId TEXT NOT NULL,
                    SampleId TEXT NOT NULL,
                    GroundTruth TEXT NOT NULL,
                    SystemDecision TEXT NOT NULL DEFAULT '',
                    Disposition TEXT NOT NULL DEFAULT '',
                    ReviewerId TEXT NOT NULL DEFAULT '',
                    ReviewerRole TEXT NOT NULL DEFAULT '',
                    Revision INTEGER NOT NULL DEFAULT 0,
                    ReviewedAt TEXT NOT NULL DEFAULT '',
                    Notes TEXT,
                    Reason TEXT NOT NULL,
                    MigratedAt TEXT NOT NULL,
                    SourcePayloadJson TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_manual_review_quarantine_inspection ON ManualReviewMigrationQuarantine(InspectionId);
                CREATE INDEX IF NOT EXISTS idx_manual_review_quarantine_reason ON ManualReviewMigrationQuarantine(Reason);
            ";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "ManualReviewRecords", "SystemDecision", "TEXT NOT NULL DEFAULT ''", cancellationToken)
                .ConfigureAwait(false);
            await EnsureColumnAsync(connection, "ManualReviewRecords", "Disposition", "TEXT NOT NULL DEFAULT 'Invalid'", cancellationToken)
                .ConfigureAwait(false);
            await EnsureColumnAsync(connection, "ManualReviewRecords", "ReviewerRole", "TEXT NOT NULL DEFAULT ''", cancellationToken)
                .ConfigureAwait(false);
            await EnsureColumnAsync(connection, "ManualReviewRecords", "DetectionRecordId", "INTEGER NOT NULL DEFAULT 0", cancellationToken)
                .ConfigureAwait(false);
            await EnsureManualReviewPrimaryKeyAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        private static bool TryNormalizeReview(
            ManualReviewSaveRequest request,
            string systemDecision,
            out string disposition,
            out string groundTruth,
            out string error)
        {
            disposition = request.Disposition?.Trim() ?? string.Empty;
            groundTruth = string.Empty;
            error = string.Empty;
            if (!IsKnownDisposition(disposition))
            {
                error = $"Disposition is unknown. Actual={disposition}.";
                return false;
            }

            if (!ReplayMetrics.TryNormalizeDecision(request.GroundTruth, out string normalizedTruth))
            {
                error = $"Ground truth must be OK or NG. Actual={request.GroundTruth}.";
                return false;
            }

            if (string.Equals(disposition, ReplayReviewDispositions.Confirmed, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(systemDecision, normalizedTruth, StringComparison.Ordinal))
                {
                    error = $"Confirmed disposition requires ground truth {systemDecision}. Actual={normalizedTruth}.";
                    return false;
                }
            }
            else if (string.Equals(disposition, ReplayReviewDispositions.FalseReject, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(systemDecision, ReplayDecisions.NG, StringComparison.Ordinal) ||
                    !string.Equals(normalizedTruth, ReplayDecisions.OK, StringComparison.Ordinal))
                {
                    error = $"FalseReject requires system NG and ground truth OK. System={systemDecision}; Truth={normalizedTruth}.";
                    return false;
                }
            }
            else if (string.Equals(disposition, ReplayReviewDispositions.MissedDetection, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(systemDecision, ReplayDecisions.OK, StringComparison.Ordinal) ||
                    !string.Equals(normalizedTruth, ReplayDecisions.NG, StringComparison.Ordinal))
                {
                    error = $"MissedDetection requires system OK and ground truth NG. System={systemDecision}; Truth={normalizedTruth}.";
                    return false;
                }
            }

            groundTruth = normalizedTruth;
            return true;
        }

        private static bool IsKnownDisposition(string disposition)
        {
            return string.Equals(disposition, ReplayReviewDispositions.Pending, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(disposition, ReplayReviewDispositions.Confirmed, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(disposition, ReplayReviewDispositions.FalseReject, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(disposition, ReplayReviewDispositions.MissedDetection, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(disposition, ReplayReviewDispositions.InvalidSample, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryResolveAuthorizedReviewer(
            out string reviewerId,
            out ProductionRole reviewerRole,
            out string errorCode,
            out string message)
        {
            reviewerId = string.Empty;
            reviewerRole = ProductionRole.Operator;
            errorCode = string.Empty;
            message = string.Empty;

            if (_operatorIdProvider == null)
            {
                errorCode = "ManualReviewOperatorProviderMissing";
                message = "Manual review operator provider is required.";
                return false;
            }

            try
            {
                reviewerId = (_operatorIdProvider.Invoke() ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                errorCode = "ManualReviewOperatorProviderFailed";
                message = $"Manual review operator provider failed: {ex.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(reviewerId))
            {
                errorCode = "ManualReviewOperatorMissing";
                message = "Manual review operator id is required.";
                return false;
            }

            if (_operatorRoleProvider == null)
            {
                errorCode = "ManualReviewOperatorRoleProviderMissing";
                message = "Manual review operator role provider is required.";
                return false;
            }

            try
            {
                reviewerRole = _operatorRoleProvider.Invoke();
            }
            catch (Exception ex)
            {
                errorCode = "ManualReviewOperatorRoleProviderFailed";
                message = $"Manual review operator role provider failed: {ex.Message}";
                return false;
            }

            if (!ProductionAuthorizationService.Authorize(
                    reviewerRole,
                    ProductionOperation.EngineeringChange,
                    out string denialReason))
            {
                errorCode = "ManualReviewUnauthorized";
                message = denialReason;
                return false;
            }

            return true;
        }

        private async Task EnsureManualReviewPrimaryKeyAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
        {
            bool hasDetectionRecordId = false;
            bool detectionRecordIsPrimaryKey = false;
            await using (SqliteCommand info = connection.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(ManualReviewRecords);";
                await using SqliteDataReader reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string columnName = reader.GetString(1);
                    if (string.Equals(columnName, "DetectionRecordId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDetectionRecordId = true;
                        detectionRecordIsPrimaryKey = reader.GetInt32(5) > 0;
                    }
                }
            }

            if (hasDetectionRecordId && detectionRecordIsPrimaryKey)
            {
                return;
            }

            string legacyTable = $"ManualReviewRecords_legacy_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            string migratedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (SqliteCommand rename = connection.CreateCommand())
                {
                    rename.Transaction = transaction;
                    rename.CommandText = $"ALTER TABLE ManualReviewRecords RENAME TO {legacyTable};";
                    await rename.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (SqliteCommand create = connection.CreateCommand())
                {
                    create.Transaction = transaction;
                    create.CommandText = @"
                        CREATE TABLE ManualReviewRecords (
                            DetectionRecordId INTEGER PRIMARY KEY,
                            InspectionId TEXT NOT NULL,
                            SampleId TEXT NOT NULL,
                            GroundTruth TEXT NOT NULL,
                            SystemDecision TEXT NOT NULL DEFAULT '',
                            Disposition TEXT NOT NULL DEFAULT 'InvalidSample',
                            ReviewerId TEXT NOT NULL,
                            ReviewerRole TEXT NOT NULL DEFAULT '',
                            Revision INTEGER NOT NULL,
                            ReviewedAt TEXT NOT NULL,
                            Notes TEXT
                        );
                        CREATE INDEX IF NOT EXISTS idx_manual_review_inspection ON ManualReviewRecords(InspectionId);
                        CREATE INDEX IF NOT EXISTS idx_manual_review_status ON ManualReviewRecords(GroundTruth, ReviewedAt);
                    ";
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                IReadOnlyList<LegacyManualReviewRow> legacyRows = await LoadLegacyManualReviewsAsync(
                    connection,
                    transaction,
                    legacyTable,
                    hasDetectionRecordId,
                    cancellationToken).ConfigureAwait(false);
                var migratedDetectionRecordIds = new HashSet<long>();
                foreach (LegacyManualReviewRow row in legacyRows)
                {
                    List<DetectionRecord> matches = await _databaseService
                        .GetDetectionRecordsByInspectionIdAsync(row.InspectionId)
                        .ConfigureAwait(false);
                    if (matches.Count != 1)
                    {
                        string reason = matches.Count == 0
                            ? "ManualReviewLegacyInspectionMissing"
                            : "ManualReviewLegacyInspectionAmbiguous";
                        await InsertLegacyQuarantineAsync(
                            connection,
                            transaction,
                            row,
                            reason,
                            migratedAt,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    long detectionRecordId = matches[0].Id;
                    if (detectionRecordId <= 0 || !migratedDetectionRecordIds.Add(detectionRecordId))
                    {
                        await InsertLegacyQuarantineAsync(
                            connection,
                            transaction,
                            row,
                            detectionRecordId <= 0
                                ? "ManualReviewLegacyDetectionRecordInvalid"
                                : "ManualReviewLegacyDetectionRecordDuplicate",
                            migratedAt,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await InsertMigratedReviewAsync(
                        connection,
                        transaction,
                        row,
                        detectionRecordId,
                        cancellationToken).ConfigureAwait(false);
                }

                await using (SqliteCommand drop = connection.CreateCommand())
                {
                    drop.Transaction = transaction;
                    drop.CommandText = $"DROP TABLE {legacyTable};";
                    await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        private static async Task<IReadOnlyList<LegacyManualReviewRow>> LoadLegacyManualReviewsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string legacyTable,
            bool hasDetectionRecordId,
            CancellationToken cancellationToken)
        {
            var rows = new List<LegacyManualReviewRow>();
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            string detectionRecordColumn = hasDetectionRecordId
                ? "DetectionRecordId"
                : "0 AS DetectionRecordId";
            command.CommandText = $@"
                SELECT
                    rowid,
                    {detectionRecordColumn},
                    InspectionId,
                    SampleId,
                    GroundTruth,
                    SystemDecision,
                    CASE WHEN Disposition = 'Invalid' THEN 'InvalidSample' ELSE Disposition END AS Disposition,
                    ReviewerId,
                    ReviewerRole,
                    Revision,
                    ReviewedAt,
                    Notes
                FROM {legacyTable}
                ORDER BY rowid ASC;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new LegacyManualReviewRow
                {
                    SourceRowId = reader.GetInt64(0),
                    SourceDetectionRecordId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    InspectionId = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    SampleId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    GroundTruth = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    SystemDecision = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    Disposition = reader.IsDBNull(6) ? ReplayReviewDispositions.InvalidSample : reader.GetString(6),
                    ReviewerId = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    ReviewerRole = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    Revision = reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                    ReviewedAt = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    Notes = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
                });
            }

            return rows;
        }

        private static async Task InsertMigratedReviewAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            LegacyManualReviewRow row,
            long detectionRecordId,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO ManualReviewRecords
                    (DetectionRecordId, InspectionId, SampleId, GroundTruth, SystemDecision, Disposition, ReviewerId, ReviewerRole, Revision, ReviewedAt, Notes)
                VALUES
                    ($detectionRecordId, $inspectionId, $sampleId, $groundTruth, $systemDecision, $disposition, $reviewerId, $reviewerRole, $revision, $reviewedAt, $notes);";
            command.Parameters.AddWithValue("$detectionRecordId", detectionRecordId);
            command.Parameters.AddWithValue("$inspectionId", row.InspectionId);
            command.Parameters.AddWithValue("$sampleId", row.SampleId);
            command.Parameters.AddWithValue("$groundTruth", row.GroundTruth);
            command.Parameters.AddWithValue("$systemDecision", row.SystemDecision);
            command.Parameters.AddWithValue("$disposition", row.Disposition);
            command.Parameters.AddWithValue("$reviewerId", row.ReviewerId);
            command.Parameters.AddWithValue("$reviewerRole", row.ReviewerRole);
            command.Parameters.AddWithValue("$revision", row.Revision);
            command.Parameters.AddWithValue("$reviewedAt", row.ReviewedAt);
            command.Parameters.AddWithValue("$notes", row.Notes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task InsertLegacyQuarantineAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            LegacyManualReviewRow row,
            string reason,
            string migratedAt,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO ManualReviewMigrationQuarantine
                    (SourceRowId, SourceDetectionRecordId, InspectionId, SampleId, GroundTruth, SystemDecision, Disposition, ReviewerId, ReviewerRole, Revision, ReviewedAt, Notes, Reason, MigratedAt, SourcePayloadJson)
                VALUES
                    ($sourceRowId, $sourceDetectionRecordId, $inspectionId, $sampleId, $groundTruth, $systemDecision, $disposition, $reviewerId, $reviewerRole, $revision, $reviewedAt, $notes, $reason, $migratedAt, $payload);";
            command.Parameters.AddWithValue("$sourceRowId", row.SourceRowId);
            command.Parameters.AddWithValue("$sourceDetectionRecordId", row.SourceDetectionRecordId);
            command.Parameters.AddWithValue("$inspectionId", row.InspectionId);
            command.Parameters.AddWithValue("$sampleId", row.SampleId);
            command.Parameters.AddWithValue("$groundTruth", row.GroundTruth);
            command.Parameters.AddWithValue("$systemDecision", row.SystemDecision);
            command.Parameters.AddWithValue("$disposition", row.Disposition);
            command.Parameters.AddWithValue("$reviewerId", row.ReviewerId);
            command.Parameters.AddWithValue("$reviewerRole", row.ReviewerRole);
            command.Parameters.AddWithValue("$revision", row.Revision);
            command.Parameters.AddWithValue("$reviewedAt", row.ReviewedAt);
            command.Parameters.AddWithValue("$notes", row.Notes);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$migratedAt", migratedAt);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(row, ReplayJson.Options));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task EnsureColumnAsync(
            SqliteConnection connection,
            string tableName,
            string columnName,
            string definition,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand check = connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info({tableName});";
            await using SqliteDataReader reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared;Pooling=True;Default Timeout={BusyTimeoutMs / 1000}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $@"
                PRAGMA busy_timeout = {BusyTimeoutMs};
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
            ";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private Task AppendAuditAsync(
            ManualReviewSaveRequest request,
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
                    Operation = "ManualReview",
                    Status = status,
                    OperatorId = operatorId ?? string.Empty,
                    Role = role,
                    InspectionId = request.InspectionId,
                    Details = details
                }, cancellationToken);
        }

        private sealed class LegacyManualReviewRow
        {
            public long SourceRowId { get; init; }
            public long SourceDetectionRecordId { get; init; }
            public string InspectionId { get; init; } = string.Empty;
            public string SampleId { get; init; } = string.Empty;
            public string GroundTruth { get; init; } = string.Empty;
            public string SystemDecision { get; init; } = string.Empty;
            public string Disposition { get; init; } = ReplayReviewDispositions.InvalidSample;
            public string ReviewerId { get; init; } = string.Empty;
            public string ReviewerRole { get; init; } = string.Empty;
            public long Revision { get; init; }
            public string ReviewedAt { get; init; } = string.Empty;
            public string Notes { get; init; } = string.Empty;
        }
    }
}
