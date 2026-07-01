using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Services.Replay;
using ClearFrost.Core.Security;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

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
                Path.Combine(tempDir, "review.db"));

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
    public async Task SaveReviewAsync_Revision冲突返回明确响应并写审计()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var audit = new OperationAuditService(Path.Combine(tempDir, "audit"));
            var store = new SqliteManualReviewStore(
                new FakeDatabaseService(CreateRecords()),
                Path.Combine(tempDir, "review.db"),
                audit);

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
                operatorRoleProvider: () => ProductionRole.Operator);

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
            saved.Record.ReviewerRole.Should().Be(ProductionRole.Operator.ToString());
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(path, recursive: true);
        }
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
