using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Services.Replay;
using ClearFrost.Core.Security;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

[Collection(global::ClearFrost.Tests.TestCollections.SqliteGlobalPool)]
public class ManualReviewStoreTests
{
    [Fact]
    public async Task QueryAsync_支持全部系统OK系统NG和复核状态()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var records = CreateRecords();
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(records),
                Path.Combine(tempDir, "review.db"),
                operatorIdProvider: () => "qa01",
                operatorRoleProvider: () => ProductionRole.Engineer);

            (await store.QueryAsync(new ManualReviewQuery()))
                .Should().HaveCount(4).And.OnlyContain(item => item.ReviewStatus == ManualReviewStatuses.Pending);
            (await store.QueryAsync(new ManualReviewQuery { ReplayQuery = new DetectionReplayQuery { IsQualified = true } }))
                .Should().HaveCount(2).And.OnlyContain(item => item.SystemIsQualified);
            (await store.QueryAsync(new ManualReviewQuery { ReplayQuery = new DetectionReplayQuery { IsQualified = false } }))
                .Should().HaveCount(2).And.OnlyContain(item => !item.SystemIsQualified);

            ManualReviewSaveResult saved = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 3,
                InspectionId = "INS-NG-1",
                SampleId = "S-NG-1",
                GroundTruth = ReplayDecisions.NG,
                Disposition = ReplayReviewDispositions.Confirmed,
                ReviewerId = "qa01",
                ReviewerRole = "Engineer",
                ExpectedRevision = 0
            });

            saved.Succeeded.Should().BeTrue();
            saved.Record!.Revision.Should().Be(1);
            IReadOnlyList<ManualReviewTraceItem> reviewed = await store.QueryAsync(new ManualReviewQuery
            {
                ReviewStatus = ManualReviewStatuses.Reviewed
            });
            reviewed.Should().ContainSingle();
            reviewed[0].Review!.GroundTruth.Should().Be(ReplayDecisions.NG);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Constructor_拒绝链接复核数据库文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDbPath = string.Empty;
        try
        {
            linkedDbPath = Path.Combine(tempDir, "review.db");
            string externalDbPath = Path.Combine(externalDir, "external-review.db");
            File.WriteAllText(externalDbPath, "external review database");
            if (!TryCreateFileSymbolicLink(linkedDbPath, externalDbPath))
            {
                return;
            }

            Action act = () => _ = new SqliteManualReviewStore(
                new FakeDatabaseService(CreateRecords()),
                linkedDbPath);

            act.Should().Throw<IOException>().WithMessage("*人工复核数据库文件*链接文件*");
            File.ReadAllText(externalDbPath).Should().Be("external review database");
        }
        finally
        {
            TryDeleteFileLink(linkedDbPath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void Constructor_拒绝链接复核数据库目录且不写入外部目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDirectory = string.Empty;
        try
        {
            linkedDirectory = Path.Combine(tempDir, "review-root");
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, externalDir))
            {
                return;
            }

            string dbPath = Path.Combine(linkedDirectory, "review.db");

            Action act = () => _ = new SqliteManualReviewStore(
                new FakeDatabaseService(CreateRecords()),
                dbPath);

            act.Should().Throw<IOException>().WithMessage("*人工复核数据库目录*链接目录*");
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedDirectory);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task QueryAsync_拒绝运行中被替换为链接复核数据库文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDbPath = string.Empty;
        try
        {
            linkedDbPath = Path.Combine(tempDir, "review.db");
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(CreateRecords()),
                linkedDbPath);

            string externalDbPath = Path.Combine(externalDir, "external-review.db");
            File.WriteAllText(externalDbPath, "external review database");
            if (!TryCreateFileSymbolicLink(linkedDbPath, externalDbPath))
            {
                return;
            }

            Func<Task> act = () => store.QueryAsync(new ManualReviewQuery());

            await act.Should().ThrowAsync<IOException>().WithMessage("*人工复核数据库文件*链接文件*");
            File.ReadAllText(externalDbPath).Should().Be("external review database");
        }
        finally
        {
            TryDeleteFileLink(linkedDbPath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task SaveReviewAsync_Revision冲突返回明确响应并写审计()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var audit = new OperationAuditService(Path.Combine(tempDir, "audit"));
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(CreateRecords()),
                Path.Combine(tempDir, "review.db"),
                audit,
                operatorIdProvider: () => "qa01",
                operatorRoleProvider: () => ProductionRole.Engineer);

            ManualReviewSaveResult first = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 1,
                InspectionId = "INS-OK-1",
                SampleId = "S-OK-1",
                GroundTruth = ReplayDecisions.OK,
                Disposition = ReplayReviewDispositions.Confirmed,
                ReviewerId = "qa01",
                ReviewerRole = "Engineer",
                ExpectedRevision = 0
            });
            ManualReviewSaveResult conflict = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 1,
                InspectionId = "INS-OK-1",
                SampleId = "S-OK-1",
                GroundTruth = ReplayDecisions.NG,
                Disposition = ReplayReviewDispositions.MissedDetection,
                ReviewerId = "qa02",
                ReviewerRole = "Engineer",
                ExpectedRevision = 0
            });
            ManualReviewSaveResult update = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 1,
                InspectionId = "INS-OK-1",
                SampleId = "S-OK-1",
                GroundTruth = ReplayDecisions.NG,
                Disposition = ReplayReviewDispositions.MissedDetection,
                ReviewerId = "qa02",
                ReviewerRole = "Engineer",
                ExpectedRevision = 1
            });

            first.Succeeded.Should().BeTrue();
            conflict.Succeeded.Should().BeFalse();
            conflict.ErrorCode.Should().Be("ReviewRevisionConflict");
            update.Succeeded.Should().BeTrue();
            update.Record!.Revision.Should().Be(2);
            update.Record.GroundTruth.Should().Be(ReplayDecisions.NG);

            OperationAuditQueryResult auditResult = await audit.QueryAsync(new OperationAuditQuery
            {
                Operation = "ManualReview",
                Limit = 10
            });
            auditResult.Succeeded.Should().BeTrue();
            auditResult.Records.Should().HaveCount(3);
            auditResult.Records.Should().Contain(record => record.Status == OperationAuditStatus.Denied);
            auditResult.Records.Should().Contain(record => record.Status == OperationAuditStatus.Succeeded);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task SaveReviewAsync_以检测记录为权威并拒绝身份和绑定篡改()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var records = CreateRecords();
            records.Add(new DetectionRecord { Id = 5, InspectionId = "INS-DUP", Timestamp = DateTime.UtcNow, IsQualified = true });
            records.Add(new DetectionRecord { Id = 6, InspectionId = "INS-DUP", Timestamp = DateTime.UtcNow.AddSeconds(1), IsQualified = true });
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(records),
                Path.Combine(tempDir, "review.db"),
                operatorIdProvider: () => "trusted-operator",
                operatorRoleProvider: () => ProductionRole.Engineer);

            ManualReviewSaveResult mismatch = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 1,
                InspectionId = "INS-NG-1",
                GroundTruth = ReplayDecisions.OK,
                Disposition = ReplayReviewDispositions.Confirmed,
                ExpectedRevision = 0
            });
            ManualReviewSaveResult saved = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 5,
                InspectionId = "INS-DUP",
                SampleId = "client-sample",
                GroundTruth = ReplayDecisions.OK,
                Disposition = ReplayReviewDispositions.Confirmed,
                ReviewerId = "spoofed",
                ReviewerRole = "Administrator",
                ExpectedRevision = 0
            });
            ManualReviewSaveResult invalidSample = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 6,
                InspectionId = "INS-DUP",
                GroundTruth = ReplayDecisions.OK,
                Disposition = ReplayReviewDispositions.InvalidSample,
                ReviewerId = "spoofed",
                ReviewerRole = "Administrator",
                ExpectedRevision = 0
            });

            mismatch.Succeeded.Should().BeFalse();
            mismatch.ErrorCode.Should().Be("ManualReviewRecordBindingMismatch");
            saved.Succeeded.Should().BeTrue();
            saved.Record!.ReviewerId.Should().Be("trusted-operator");
            saved.Record.ReviewerRole.Should().Be(ProductionRole.Engineer.ToString());
            invalidSample.Succeeded.Should().BeTrue();
            invalidSample.Record!.Disposition.Should().Be(ReplayReviewDispositions.InvalidSample);

            IReadOnlyList<ManualReviewTraceItem> items = await store.QueryAsync(new ManualReviewQuery());
            items.Where(item => item.InspectionId == "INS-DUP").Should().HaveCount(2);
            items.Should().ContainSingle(item =>
                item.DetectionRecordId == 5 &&
                item.ReviewStatus == ManualReviewStatuses.Reviewed &&
                item.Review!.SampleId == "client-sample");
            items.Should().ContainSingle(item =>
                item.DetectionRecordId == 6 &&
                item.ReviewStatus == ManualReviewStatuses.Reviewed &&
                item.Review!.Disposition == ReplayReviewDispositions.InvalidSample);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task SaveReviewAsync_Operator被拒绝且不产生复核记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(CreateRecords()),
                Path.Combine(tempDir, "review.db"),
                operatorIdProvider: () => "operator01",
                operatorRoleProvider: () => ProductionRole.Operator);

            ManualReviewSaveResult result = await store.SaveReviewAsync(new ManualReviewSaveRequest
            {
                DetectionRecordId = 1,
                InspectionId = "INS-OK-1",
                GroundTruth = ReplayDecisions.OK,
                Disposition = ReplayReviewDispositions.Confirmed,
                ExpectedRevision = 0
            });

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ManualReviewUnauthorized");
            IReadOnlyList<ManualReviewTraceItem> reviewed = await store.QueryAsync(new ManualReviewQuery
            {
                ReviewStatus = ManualReviewStatuses.Reviewed
            });
            reviewed.Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LegacyMigration_唯一Inspection匹配真实DetectionRecordId且幂等()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string dbPath = Path.Combine(tempDir, "legacy-review.db");
            await CreateLegacyManualReviewRowAsync(
                dbPath,
                "LEG-UNIQUE",
                "S-LEG-1",
                ReplayDecisions.OK,
                ReplayDecisions.OK,
                ReplayReviewDispositions.Confirmed);

            var records = new List<DetectionRecord>
            {
                new DetectionRecord { Id = 42, InspectionId = "LEG-UNIQUE", Timestamp = DateTime.UtcNow, IsQualified = true }
            };
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(records),
                dbPath,
                operatorIdProvider: () => "qa01",
                operatorRoleProvider: () => ProductionRole.Engineer);

            IReadOnlyList<ManualReviewTraceItem> first = await store.QueryAsync(new ManualReviewQuery());
            IReadOnlyList<ManualReviewTraceItem> second = await store.QueryAsync(new ManualReviewQuery());

            first.Should().ContainSingle(item =>
                item.DetectionRecordId == 42 &&
                item.ReviewStatus == ManualReviewStatuses.Reviewed &&
                item.Review!.SampleId == "S-LEG-1");
            second.Should().ContainSingle(item =>
                item.DetectionRecordId == 42 &&
                item.ReviewStatus == ManualReviewStatuses.Reviewed);
            (await CountRowsAsync(dbPath, "ManualReviewRecords")).Should().Be(1);
            (await CountRowsAsync(dbPath, "ManualReviewMigrationQuarantine")).Should().Be(0);
            (await TableExistsAsync(dbPath, "ManualReviewRecords_legacy")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LegacyMigration_零匹配和多匹配进入Quarantine且重复执行不重复()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string dbPath = Path.Combine(tempDir, "legacy-review.db");
            await CreateLegacyManualReviewRowAsync(
                dbPath,
                "LEG-MISSING",
                "S-MISSING",
                ReplayDecisions.OK,
                ReplayDecisions.OK,
                ReplayReviewDispositions.Confirmed);
            await InsertLegacyManualReviewRowAsync(
                dbPath,
                "LEG-DUP",
                "S-DUP",
                ReplayDecisions.OK,
                ReplayDecisions.OK,
                ReplayReviewDispositions.Confirmed);

            var records = new List<DetectionRecord>
            {
                new DetectionRecord { Id = 10, InspectionId = "LEG-DUP", Timestamp = DateTime.UtcNow, IsQualified = true },
                new DetectionRecord { Id = 11, InspectionId = "LEG-DUP", Timestamp = DateTime.UtcNow.AddSeconds(1), IsQualified = true }
            };
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(records),
                dbPath,
                operatorIdProvider: () => "qa01",
                operatorRoleProvider: () => ProductionRole.Engineer);

            await store.QueryAsync(new ManualReviewQuery());
            await store.QueryAsync(new ManualReviewQuery());

            (await CountRowsAsync(dbPath, "ManualReviewRecords")).Should().Be(0);
            IReadOnlyList<string> reasons = await ReadQuarantineReasonsAsync(dbPath);
            reasons.Should().BeEquivalentTo(
                new[]
                {
                    "ManualReviewLegacyInspectionMissing",
                    "ManualReviewLegacyInspectionAmbiguous"
                });
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static List<DetectionRecord> CreateRecords()
    {
        return new List<DetectionRecord>
        {
            new DetectionRecord { Id = 1, InspectionId = "INS-OK-1", Timestamp = DateTime.UtcNow.AddMinutes(-4), IsQualified = true },
            new DetectionRecord { Id = 2, InspectionId = "INS-OK-2", Timestamp = DateTime.UtcNow.AddMinutes(-3), IsQualified = true },
            new DetectionRecord { Id = 3, InspectionId = "INS-NG-1", Timestamp = DateTime.UtcNow.AddMinutes(-2), IsQualified = false },
            new DetectionRecord { Id = 4, InspectionId = "INS-NG-2", Timestamp = DateTime.UtcNow.AddMinutes(-1), IsQualified = false }
        };
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(ManualReviewStoreTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = File.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = Directory.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteFileLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new FileInfo(linkPath);
            info.Refresh();
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
        }
    }

    private static void TryDeleteDirectoryLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(linkPath);
            info.Refresh();
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var info = new DirectoryInfo(path);
            info.Refresh();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
                return;
            }

            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task CreateLegacyManualReviewRowAsync(
        string dbPath,
        string inspectionId,
        string sampleId,
        string groundTruth,
        string systemDecision,
        string disposition)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using (Microsoft.Data.Sqlite.SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = @"
                CREATE TABLE ManualReviewRecords (
                    InspectionId TEXT PRIMARY KEY,
                    SampleId TEXT NOT NULL,
                    GroundTruth TEXT NOT NULL,
                    SystemDecision TEXT NOT NULL DEFAULT '',
                    Disposition TEXT NOT NULL DEFAULT 'InvalidSample',
                    ReviewerId TEXT NOT NULL,
                    ReviewerRole TEXT NOT NULL DEFAULT '',
                    Revision INTEGER NOT NULL,
                    ReviewedAt TEXT NOT NULL,
                    Notes TEXT
                );";
            await create.ExecuteNonQueryAsync();
        }

        await InsertLegacyManualReviewRowAsync(
            dbPath,
            inspectionId,
            sampleId,
            groundTruth,
            systemDecision,
            disposition);
    }

    private static async Task InsertLegacyManualReviewRowAsync(
        string dbPath,
        string inspectionId,
        string sampleId,
        string groundTruth,
        string systemDecision,
        string disposition)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO ManualReviewRecords
                (InspectionId, SampleId, GroundTruth, SystemDecision, Disposition, ReviewerId, ReviewerRole, Revision, ReviewedAt, Notes)
            VALUES
                ($inspectionId, $sampleId, $groundTruth, $systemDecision, $disposition, 'legacy-reviewer', 'Engineer', 7, $reviewedAt, 'legacy note');";
        insert.Parameters.AddWithValue("$inspectionId", inspectionId);
        insert.Parameters.AddWithValue("$sampleId", sampleId);
        insert.Parameters.AddWithValue("$groundTruth", groundTruth);
        insert.Parameters.AddWithValue("$systemDecision", systemDecision);
        insert.Parameters.AddWithValue("$disposition", disposition);
        insert.Parameters.AddWithValue("$reviewedAt", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountRowsAsync(string dbPath, string tableName)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(string dbPath, string tableNamePrefix)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE $name;";
        command.Parameters.AddWithValue("$name", $"{tableNamePrefix}%");
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<IReadOnlyList<string>> ReadQuarantineReasonsAsync(string dbPath)
    {
        var reasons = new List<string>();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Reason FROM ManualReviewMigrationQuarantine ORDER BY Id ASC;";
        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            reasons.Add(reader.GetString(0));
        }

        return reasons;
    }

    private sealed class FakeDatabaseService : IDatabaseService
    {
        private readonly List<DetectionRecord> _records;

        public FakeDatabaseService(List<DetectionRecord> records)
        {
            _records = records;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task SaveDetectionRecordAsync(DetectionRecord record) => Task.CompletedTask;
        public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
            => Task.FromResult(_records.ToList());
        public Task<DetectionRecord?> GetDetectionRecordByIdAsync(long id)
            => Task.FromResult(_records.FirstOrDefault(record => record.Id == id));
        public Task<List<DetectionRecord>> GetDetectionRecordsByInspectionIdAsync(string inspectionId)
            => Task.FromResult(_records
                .Where(record => string.Equals(record.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase))
                .ToList());
        public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
            => Task.FromResult(new List<DetectionTraceRecord>());
        public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
            => Task.FromResult(new DetectionTracePage());
        public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query)
        {
            IEnumerable<DetectionRecord> result = _records;
            if (query.IsQualified.HasValue)
            {
                result = result.Where(record => record.IsQualified == query.IsQualified.Value);
            }

            return Task.FromResult(result.Take(query.Limit <= 0 ? 100 : query.Limit).ToList());
        }

        public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
            => Task.FromResult(new List<string>());
        public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
            => Task.FromResult(new List<string>());
        public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
            => Task.FromResult((_records.Count, _records.Count(r => r.IsQualified), _records.Count(r => !r.IsQualified)));
        public Task<int> CleanupOldRecordsAsync(int daysToKeep) => Task.FromResult(0);
        public void Dispose() { }
    }
}
