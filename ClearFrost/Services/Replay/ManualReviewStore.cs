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
        public string ReviewStatus { get; init; } = ManualReviewStatuses.Pending;
        public ReplayManualReviewRecord? Review { get; init; }
    }

    public sealed class ManualReviewSaveRequest
    {
        public string InspectionId { get; init; } = string.Empty;
        public string SampleId { get; init; } = string.Empty;
        public string GroundTruth { get; init; } = ReplayDecisions.OK;
        public string ReviewerId { get; init; } = string.Empty;
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
            return new ManualReviewSaveResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty
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
                        $"Review revision conflict. Expected={request.ExpectedRevision.Value}; Actual={existingRevision}.");
                }

                var record = new ReplayManualReviewRecord
                {
                    SampleId = string.IsNullOrWhiteSpace(request.SampleId)
                        ? request.InspectionId.Trim()
                        : request.SampleId.Trim(),
                    InspectionId = request.InspectionId.Trim(),
                    GroundTruth = ReplayMetrics.Normalize(request.GroundTruth),
                    ReviewerId = request.ReviewerId?.Trim() ?? string.Empty,
                    Revision = existingRevision + 1,
                    ReviewedAt = DateTimeOffset.UtcNow,
                    Notes = request.Notes ?? string.Empty
                };

                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO ManualReviewRecords
                        (InspectionId, SampleId, GroundTruth, ReviewerId, Revision, ReviewedAt, Notes)
                    VALUES
                        ($inspectionId, $sampleId, $groundTruth, $reviewerId, $revision, $reviewedAt, $notes)
                    ON CONFLICT(InspectionId) DO UPDATE SET
                        SampleId = excluded.SampleId,
                        GroundTruth = excluded.GroundTruth,
                        ReviewerId = excluded.ReviewerId,
                        Revision = excluded.Revision,
                        ReviewedAt = excluded.ReviewedAt,
                        Notes = excluded.Notes;
                ";
                command.Parameters.AddWithValue("$inspectionId", record.InspectionId);
                command.Parameters.AddWithValue("$sampleId", record.SampleId);
                command.Parameters.AddWithValue("$groundTruth", record.GroundTruth);
                command.Parameters.AddWithValue("$reviewerId", record.ReviewerId);
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
                Notes = reader.GetString(6)
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
                    ReviewerId TEXT NOT NULL,
                    Revision INTEGER NOT NULL,
                    ReviewedAt TEXT NOT NULL,
                    Notes TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_manual_review_status ON ManualReviewRecords(GroundTruth, ReviewedAt);
            ";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                    Role = ProductionRole.Engineer,
                    InspectionId = request.InspectionId,
                    Details = details
                }, cancellationToken);
        }
    }
}
