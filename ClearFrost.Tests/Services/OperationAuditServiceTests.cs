using ClearFrost.Core.Security;
using ClearFrost.Services;
using FluentAssertions;
using System.Text.Json;

namespace ClearFrost.Tests.Services;

public class OperationAuditServiceTests
{
    [Fact]
    public async Task QueryAsync_按筛选条件返回审计记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var service = new OperationAuditService(tempDir);
            await service.AppendAsync(new OperationAuditRecord
            {
                Timestamp = DateTimeOffset.Now.AddMinutes(-2),
                Operation = "ManualRelease",
                Status = OperationAuditStatus.Failed,
                OperatorId = "op01",
                Role = ProductionRole.Engineer,
                Details = "PLC write failed",
                FailureBlocker = "AuditBeforePlcWrite"
            });
            await service.AppendAsync(new OperationAuditRecord
            {
                Timestamp = DateTimeOffset.Now.AddMinutes(-1),
                Operation = "ConfigSave",
                Status = OperationAuditStatus.Succeeded,
                OperatorId = "op02",
                Role = ProductionRole.ShiftLead,
                Details = "saved"
            });

            OperationAuditQueryResult result = await service.QueryAsync(new OperationAuditQuery
            {
                Operation = "Manual",
                OperatorId = "op01",
                Role = "Engineer",
                Status = OperationAuditStatus.Failed,
                FailureReason = "PLC",
                Limit = 10
            });

            result.Succeeded.Should().BeTrue();
            result.Records.Should().ContainSingle();
            result.Records[0].Operation.Should().Be("ManualRelease");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportCsvAsync_导出当前查询结果()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var service = new OperationAuditService(tempDir);
            await service.AppendAsync(new OperationAuditRecord
            {
                Operation = "ManualRelease",
                Status = OperationAuditStatus.Succeeded,
                OperatorId = "op01",
                Role = ProductionRole.Engineer,
                Details = "ok"
            });

            string outputPath = Path.Combine(tempDir, "audit.csv");
            string exported = await service.ExportCsvAsync(new OperationAuditQuery { Operation = "ManualRelease" }, outputPath);

            exported.Should().Be(outputPath);
            File.ReadAllText(outputPath).Should().Contain("ManualRelease").And.Contain("op01");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task QueryAsync_单文件超过Limit时仍返回跨文件全局最新记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            WriteAuditFile(
                tempDir,
                "operation-audit-20260630.ndjson",
                Enumerable.Range(0, 5).Select(index => new OperationAuditRecord
                {
                    Timestamp = new DateTimeOffset(2026, 6, 30, 8, index, 0, TimeSpan.Zero),
                    Operation = "ConfigSave",
                    Status = OperationAuditStatus.Succeeded,
                    OperatorId = $"old{index}",
                    Role = ProductionRole.Engineer
                }),
                includeCorruptLine: true);
            WriteAuditFile(
                tempDir,
                "operation-audit-20260629.ndjson",
                new[]
                {
                    new OperationAuditRecord
                    {
                        Timestamp = new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero),
                        Operation = "ConfigSave",
                        Status = OperationAuditStatus.Succeeded,
                        OperatorId = "newest",
                        Role = ProductionRole.Engineer
                    }
                });
            var service = new OperationAuditService(tempDir);

            OperationAuditQueryResult result = await service.QueryAsync(new OperationAuditQuery
            {
                Operation = "ConfigSave",
                Limit = 3
            });

            result.Succeeded.Should().BeTrue();
            result.Records.Should().HaveCount(3);
            result.Records[0].OperatorId.Should().Be("newest");
            result.Records.Select(record => record.Timestamp).Should().BeInDescendingOrder();

            string csvPath = Path.Combine(tempDir, "audit-cross-file.csv");
            await service.ExportCsvAsync(new OperationAuditQuery { Operation = "ConfigSave", Limit = 3 }, csvPath);
            string csv = File.ReadAllText(csvPath);
            csv.Should().Contain("newest");
            csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostAuditTests", Guid.NewGuid().ToString("N"));
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

    private static void WriteAuditFile(
        string directory,
        string fileName,
        IEnumerable<OperationAuditRecord> records,
        bool includeCorruptLine = false)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        var lines = records.Select(record => JsonSerializer.Serialize(record)).ToList();
        if (includeCorruptLine)
        {
            lines.Insert(1, "{not-json");
        }

        File.WriteAllLines(path, lines);
    }
}
