using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class DataRetentionServiceTests
{
    [Fact]
    public async Task CleanupAsync_按策略清理图片日志报表和追溯记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string oldImageDir = CreateDatedDirectory(tempDir, "Images", "Qualified", "2026年04月01日");
            string newImageDir = CreateDatedDirectory(tempDir, "Images", "Unqualified", "2026年05月26日");
            string oldDetectionLogDir = CreateDatedDirectory(tempDir, "Logs", "DetectionLogs", "20260401");
            string oldAuditLogDir = CreateDatedDirectory(tempDir, "Logs", "AuditLogs", "2024年05月01日");
            string reportsDir = Path.Combine(tempDir, "Logs", "Reports");
            Directory.CreateDirectory(reportsDir);
            string oldReport = Path.Combine(reportsDir, "ClearFrost_NG_Trace_20250101_all_120000.csv");
            File.WriteAllText(oldReport, "old");
            string newReport = Path.Combine(reportsDir, "ClearFrost_NG_Trace_20260526_all_120000.csv");
            File.WriteAllText(newReport, "new");
            var database = new RecordingDatabaseService(recordsDeleted: 12);

            var service = new DataRetentionService(tempDir, () => new DateTime(2026, 5, 27, 10, 0, 0));
            DataRetentionCleanupSummary summary = await service.CleanupAsync(new DataRetentionPolicy
            {
                ImageRetentionDays = 30,
                LogRetentionDays = 30,
                AuditLogRetentionDays = 365,
                ReportRetentionDays = 365,
                TraceRecordRetentionDays = 90
            }, database);

            Directory.Exists(oldImageDir).Should().BeFalse();
            Directory.Exists(newImageDir).Should().BeTrue();
            Directory.Exists(oldDetectionLogDir).Should().BeFalse();
            Directory.Exists(oldAuditLogDir).Should().BeFalse();
            File.Exists(oldReport).Should().BeFalse();
            File.Exists(newReport).Should().BeTrue();
            database.LastCleanupDays.Should().Be(90);
            summary.ImageDirectoriesDeleted.Should().Be(1);
            summary.LogDirectoriesDeleted.Should().Be(2);
            summary.ReportFilesDeleted.Should().Be(1);
            summary.TraceRecordsDeleted.Should().Be(12);
            summary.ErrorCount.Should().Be(0);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task CleanupAsync_策略禁用时不删除文件且不清数据库()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string oldImageDir = CreateDatedDirectory(tempDir, "Images", "Qualified", "2026年04月01日");
            var database = new RecordingDatabaseService(recordsDeleted: 12);
            var service = new DataRetentionService(tempDir, () => new DateTime(2026, 5, 27, 10, 0, 0));

            DataRetentionCleanupSummary summary = await service.CleanupAsync(new DataRetentionPolicy
            {
                Enabled = false,
                ImageRetentionDays = 1,
                TraceRecordRetentionDays = 1
            }, database);

            Directory.Exists(oldImageDir).Should().BeTrue();
            database.LastCleanupDays.Should().BeNull();
            summary.TotalDeletedItems.Should().Be(0);
            summary.ErrorCount.Should().Be(0);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateDatedDirectory(string root, params string[] parts)
    {
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "sample.txt"), "data");
        return path;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostRetentionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingDatabaseService : IDatabaseService
    {
        private readonly int _recordsDeleted;

        public RecordingDatabaseService(int recordsDeleted)
        {
            _recordsDeleted = recordsDeleted;
        }

        public int? LastCleanupDays { get; private set; }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task SaveDetectionRecordAsync(DetectionRecord record) => Task.CompletedTask;

        public Task<List<DetectionRecord>> GetRecordsAsync(
            DateTime? startDate = null,
            DateTime? endDate = null,
            bool? isQualified = null,
            int limit = 100) => Task.FromResult(new List<DetectionRecord>());

        public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
            => Task.FromResult(new List<DetectionTraceRecord>());

        public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
            => Task.FromResult(new DetectionTracePage());

        public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
            => Task.FromResult(new List<string>());

        public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
            => Task.FromResult((0, 0, 0));

        public Task<int> CleanupOldRecordsAsync(int daysToKeep)
        {
            LastCleanupDays = daysToKeep;
            return Task.FromResult(_recordsDeleted);
        }

        public void Dispose()
        {
        }
    }
}
