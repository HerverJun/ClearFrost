using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        public SqliteManualReviewStore(
            IDatabaseService databaseService,
            string dbPath,
            OperationAuditService? auditService = null)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _dbPath = string.IsNullOrWhiteSpace(dbPath)
                ? throw new ArgumentException("Manual review database path is required.", nameof(dbPath))
                : dbPath;
            _auditService = auditService;
        }

        public async Task<IReadOnlyList<ManualReviewTraceItem>> QueryAsync(
            ManualReviewQuery query,
            CancellationToken cancellationToken = default)
        {
            query ??= new ManualReviewQuery();
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            List<DetectionRecord> records = await _databaseService.GetReplayRecordsAsync(query.ReplayQuery)
                .ConfigureAwait(false);
            Dictionary<string, ReplayManualReviewRecord> reviews = await LoadReviewsAsync(
                records.Select(record => record.InspectionId),
                cancellationToken).ConfigureAwait(false);

            IEnumerable<ManualReviewTraceItem> items = records.Select(record =>
            {
                reviews.TryGetValue(record.InspectionId ?? string.Empty, out ReplayManualReviewRecord? review);
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
            if (string.IsNullOrWhiteSpace(request.InspectionId))
            {
                return ManualReviewSaveResult.Fail("ManualReviewInspectionIdMissing", "InspectionId is required.");
            }

            DetectionRecord? detectionRecord = await FindDetectionRecordAsync(request.InspectionId, cancellationToken)
                .ConfigureAwait(false);
            if (detectionRecord == null)
            {
                return ManualReviewSaveResult.Fail(
                    "ManualReviewDetectionRecordMissing",
                    $"Detection record is required for manual review: {request.InspectionId}.");
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
                    request.InspectionId,
                    cancellationToken).ConfigureAwait(false);
                long existingRevision = existing?.Revision ?? 0;
                if (request.ExpectedRevision.HasValue && request.ExpectedRevision.Value != existingRevision)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    await AppendAuditAsync(request, OperationAuditStatus.Denied, "Review revision conflict", cancellationToken)
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
                    ReviewerId = request.ReviewerId?.Trim() ?? string.Empty,
                    ReviewerRole = string.IsNullOrWhiteSpace(request.ReviewerRole)
                        ? ProductionRole.Engineer.ToString()
                        : request.ReviewerRole.Trim(),
                    Revision = existingRevision + 1,
                    ReviewedAt = DateTimeOffset.UtcNow,
                    Notes = request.Notes ?? string.Empty
                };

                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO ManualReviewRecords
                        (InspectionId, SampleId, GroundTruth, SystemDecision, Disposition, ReviewerId, ReviewerRole, Revision, ReviewedAt, Notes)
                    VALUES
                        ($inspectionId, $sampleId, $groundTruth, $systemDecision, $disposition, $reviewerId, $reviewerRole, $revision, $reviewedAt, $notes)
                    ON CONFLICT(InspectionId) DO UPDATE SET
                        SampleId = excluded.SampleId,
                        GroundTruth = excluded.GroundTruth,
                        SystemDecision = excluded.SystemDecision,
                        Disposition = excluded.Disposition,
                        ReviewerId = excluded.ReviewerId,
                        ReviewerRole = excluded.ReviewerRole,
                        Revision = excluded.Revision,
                        ReviewedAt = excluded.ReviewedAt,
                        Notes = excluded.Notes;
                ";
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

                await AppendAuditAsync(request, OperationAuditStatus.Succeeded, "Manual review saved", cancellationToken)
                    .ConfigureAwait(false);
                return ManualReviewSaveResult.Ok(record);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await AppendAuditAsync(request, OperationAuditStatus.Failed, ex.Message, CancellationToken.None)
                    .ConfigureAwait(false);
                return ManualReviewSaveResult.Fail("ManualReviewSaveFailed", ex.Message);
            }
        }

        private async Task<Dictionary<string, ReplayManualReviewRecord>> LoadReviewsAsync(
            IEnumerable<string> inspectionIds,
            CancellationToken cancellationToken)
        {
            var ids = inspectionIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var result = new Dictionary<string, ReplayManualReviewRecord>(StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0)
            {
                return result;
            }

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            foreach (string id in ids)
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
            string inspectionId,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT InspectionId, SampleId, GroundTruth, ReviewerId, Revision, ReviewedAt, Notes
                , SystemDecision, Disposition, ReviewerRole
                FROM ManualReviewRecords
                WHERE InspectionId = $inspectionId
                LIMIT 1;
            ";
            command.Parameters.AddWithValue("$inspectionId", inspectionId);
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
                    InspectionId TEXT PRIMARY KEY,
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
                CREATE INDEX IF NOT EXISTS idx_manual_review_status ON ManualReviewRecords(GroundTruth, ReviewedAt);
            ";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "ManualReviewRecords", "SystemDecision", "TEXT NOT NULL DEFAULT ''", cancellationToken)
                .ConfigureAwait(false);
            await EnsureColumnAsync(connection, "ManualReviewRecords", "Disposition", "TEXT NOT NULL DEFAULT 'Invalid'", cancellationToken)
                .ConfigureAwait(false);
            await EnsureColumnAsync(connection, "ManualReviewRecords", "ReviewerRole", "TEXT NOT NULL DEFAULT ''", cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<DetectionRecord?> FindDetectionRecordAsync(
            string inspectionId,
            CancellationToken cancellationToken)
        {
            string normalized = inspectionId.Trim();
            List<DetectionRecord> replayRecords = await _databaseService.GetReplayRecordsAsync(new DetectionReplayQuery { Limit = 10000 })
                .ConfigureAwait(false);
            DetectionRecord? match = replayRecords.FirstOrDefault(record =>
                string.Equals(record.InspectionId, normalized, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }

            List<DetectionRecord> records = await _databaseService.GetRecordsAsync(limit: 10000).ConfigureAwait(false);
            return records.FirstOrDefault(record =>
                string.Equals(record.InspectionId, normalized, StringComparison.OrdinalIgnoreCase));
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
            if (!ReplayReviewDispositions.IsDatasetEligible(disposition))
            {
                error = $"Disposition must be Confirmed, FalseReject, or MissedDetection. Actual={disposition}.";
                return false;
            }

            if (!ReplayMetrics.TryNormalizeDecision(request.GroundTruth, out string normalizedTruth))
            {
                error = $"Ground truth must be OK or NG. Actual={request.GroundTruth}.";
                return false;
            }

            string expected = string.Equals(disposition, ReplayReviewDispositions.Confirmed, StringComparison.OrdinalIgnoreCase)
                ? systemDecision
                : string.Equals(disposition, ReplayReviewDispositions.FalseReject, StringComparison.OrdinalIgnoreCase)
                    ? ReplayDecisions.OK
                    : ReplayDecisions.NG;
            if (!string.Equals(expected, normalizedTruth, StringComparison.Ordinal))
            {
                error = $"Disposition {disposition} is inconsistent with system decision {systemDecision} and ground truth {normalizedTruth}.";
                return false;
            }

            groundTruth = normalizedTruth;
            return true;
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
            CancellationToken cancellationToken)
        {
            return _auditService == null
                ? Task.CompletedTask
                : _auditService.AppendAsync(new OperationAuditRecord
                {
                    Operation = "ManualReview",
                    Status = status,
                    OperatorId = request.ReviewerId,
                    Role = Enum.TryParse(request.ReviewerRole, ignoreCase: true, out ProductionRole role)
                        ? role
                        : ProductionRole.Engineer,
                    InspectionId = request.InspectionId,
                    Details = details
                }, cancellationToken);
        }
    }
}
