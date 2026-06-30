using ClearFrost.Core.Security;
using ClearFrost.Services;
using FluentAssertions;

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
}
