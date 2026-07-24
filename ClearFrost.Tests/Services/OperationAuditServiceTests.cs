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
            File.ReadAllText(outputPath)
                .Should()
                .Contain("ManualRelease")
                .And.Contain("op01")
                .And.Contain("PreviousRecordSha256")
                .And.Contain("RecordSha256");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyChainAsync_追加记录形成哈希链并能发现篡改()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var service = new OperationAuditService(tempDir);
            await service.AppendAsync(new OperationAuditRecord
            {
                Timestamp = new DateTimeOffset(2026, 7, 5, 8, 0, 0, TimeSpan.Zero),
                Operation = "ManualRelease",
                Status = OperationAuditStatus.Succeeded,
                OperatorId = "op01",
                Role = ProductionRole.Engineer,
                Details = "first"
            });
            await service.AppendAsync(new OperationAuditRecord
            {
                Timestamp = new DateTimeOffset(2026, 7, 5, 8, 1, 0, TimeSpan.Zero),
                Operation = "ConfigSave",
                Status = OperationAuditStatus.Succeeded,
                OperatorId = "op02",
                Role = ProductionRole.ShiftLead,
                Details = "second"
            });

            string auditFile = Directory.GetFiles(tempDir, "operation-audit-*.ndjson").Should().ContainSingle().Subject;
            File.ReadAllBytes(auditFile).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
            OperationAuditRecord[] records = File.ReadAllLines(auditFile)
                .Select(line => JsonSerializer.Deserialize<OperationAuditRecord>(line))
                .Where(record => record != null)
                .Cast<OperationAuditRecord>()
                .ToArray();

            records.Should().HaveCount(2);
            records[0].PreviousRecordSha256.Should().BeEmpty();
            records[0].RecordSha256.Should().NotBeNullOrWhiteSpace();
            records[1].PreviousRecordSha256.Should().Be(records[0].RecordSha256);
            records[1].RecordSha256.Should().NotBeNullOrWhiteSpace();

            OperationAuditChainVerificationResult healthy = await service.VerifyChainAsync();
            healthy.Status.Should().Be("Healthy");
            healthy.TotalRecords.Should().Be(2);
            healthy.VerifiedRecords.Should().Be(2);
            healthy.LastRecordSha256.Should().Be(records[1].RecordSha256);
            healthy.Findings.Should().BeEmpty();

            string[] lines = File.ReadAllLines(auditFile);
            lines[0] = lines[0].Replace("first", "tampered", StringComparison.Ordinal);
            File.WriteAllLines(auditFile, lines);

            OperationAuditChainVerificationResult tampered = await service.VerifyChainAsync();
            tampered.Status.Should().Be("Blocking");
            tampered.Findings.Should().Contain(finding => finding.ErrorCode == "AuditRecordHashMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task QueryAsync_跳过链接和非顶层审计文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var service = new OperationAuditService(tempDir);
            await service.AppendAsync(new OperationAuditRecord
            {
                Timestamp = new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.Zero),
                Operation = "SafeAudit",
                Status = OperationAuditStatus.Succeeded,
                OperatorId = "local",
                Role = ProductionRole.Engineer,
                Details = "local record"
            });

            string externalDir = Path.Combine(tempDir, "..", $"{Path.GetFileName(tempDir)}-external");
            Directory.CreateDirectory(externalDir);
            string externalFile = Path.Combine(externalDir, "operation-audit-20260706.ndjson");
            WriteAuditFile(
                externalDir,
                Path.GetFileName(externalFile),
                new[]
                {
                    new OperationAuditRecord
                    {
                        Timestamp = new DateTimeOffset(2026, 7, 5, 10, 1, 0, TimeSpan.Zero),
                        Operation = "ExternalLeak",
                        Status = OperationAuditStatus.Succeeded,
                        OperatorId = "external-secret",
                        Role = ProductionRole.Engineer
                    }
                });

            var escapedFile = new FileInfo(Path.Combine(tempDir, "..", Path.GetFileName(externalDir), Path.GetFileName(externalFile)));
            OperationAuditService.IsSafeAuditFileForRead(tempDir, escapedFile).Should().BeFalse();

            string nestedDir = Path.Combine(tempDir, "Nested");
            Directory.CreateDirectory(nestedDir);
            string nestedFile = Path.Combine(nestedDir, "operation-audit-20260707.ndjson");
            await File.WriteAllTextAsync(nestedFile, "{}");
            OperationAuditService.IsSafeAuditFileForRead(tempDir, new FileInfo(nestedFile)).Should().BeFalse();

            string linkPath = Path.Combine(tempDir, "operation-audit-20260706.ndjson");
            bool linkCreated = TryCreateFileSymbolicLink(linkPath, externalFile);
            if (linkCreated)
            {
                OperationAuditService.IsSafeAuditFileForRead(tempDir, new FileInfo(linkPath)).Should().BeFalse();

                OperationAuditQueryResult externalQuery = await service.QueryAsync(new OperationAuditQuery
                {
                    Operation = "ExternalLeak",
                    Limit = 5
                });
                externalQuery.Succeeded.Should().BeTrue();
                externalQuery.Records.Should().BeEmpty();

                OperationAuditChainVerificationResult chain = await service.VerifyChainAsync();
                chain.TotalRecords.Should().Be(1);
                chain.VerifiedRecords.Should().Be(1);
                chain.Status.Should().Be("Healthy");
            }

            OperationAuditQueryResult safeQuery = await service.QueryAsync(new OperationAuditQuery
            {
                Operation = "SafeAudit",
                Limit = 5
            });
            safeQuery.Succeeded.Should().BeTrue();
            safeQuery.Records.Should().ContainSingle(record => record.OperatorId == "local");
        }
        finally
        {
            DeleteDirectory(tempDir);
            string externalDir = Path.Combine(
                Path.GetDirectoryName(tempDir) ?? string.Empty,
                $"{Path.GetFileName(tempDir)}-external");
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void ReadSafeAuditLines_读取安全顶层审计文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string auditFile = Path.Combine(tempDir, "operation-audit-20260705.ndjson");
            WriteAuditFile(
                tempDir,
                Path.GetFileName(auditFile),
                new[]
                {
                    new OperationAuditRecord
                    {
                        Timestamp = new DateTimeOffset(2026, 7, 5, 11, 0, 0, TimeSpan.Zero),
                        Operation = "SafeRead",
                        Status = OperationAuditStatus.Succeeded,
                        OperatorId = "local",
                        Role = ProductionRole.Engineer
                    }
                });

            IReadOnlyList<string> lines = OperationAuditService.ReadSafeAuditLines(tempDir, auditFile);

            lines.Should().ContainSingle();
            lines[0].Should().Contain("SafeRead");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ReadSafeAuditLines_拒绝链接审计文件且不读取外部目标()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string externalFile = Path.Combine(externalDir, "operation-audit-20260706.ndjson");
            File.WriteAllText(externalFile, "{\"Operation\":\"ExternalSecret\"}");
            string linkPath = Path.Combine(tempDir, "operation-audit-20260706.ndjson");
            if (!TryCreateFileSymbolicLink(linkPath, externalFile))
            {
                return;
            }

            IReadOnlyList<string> lines = OperationAuditService.ReadSafeAuditLines(tempDir, linkPath);

            lines.Should().BeEmpty();
            File.ReadAllText(externalFile).Should().Contain("ExternalSecret");
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void ReadSafeAuditLines_拒绝非审计文件名()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string localFile = Path.Combine(tempDir, "operator-secret.ndjson");
            File.WriteAllText(localFile, "{\"Operation\":\"ShouldNotRead\"}");

            OperationAuditService.IsSafeAuditFileForRead(tempDir, new FileInfo(localFile)).Should().BeFalse();
            OperationAuditService.ReadSafeAuditLines(tempDir, localFile).Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AppendAsync_拒绝链接Outbox目录且不写外部目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string? linkedOutbox = null;
        try
        {
            linkedOutbox = Path.Combine(tempDir, "linked-outbox");
            if (!TryCreateDirectorySymbolicLink(linkedOutbox, externalDir))
            {
                return;
            }

            var service = new OperationAuditService(linkedOutbox);

            bool appended = await service.AppendAsync(new OperationAuditRecord
            {
                Operation = "LinkedOutboxWrite",
                Status = OperationAuditStatus.Succeeded,
                OperatorId = "op-linked",
                Role = ProductionRole.Engineer
            });
            OperationAuditQueryResult query = await service.QueryAsync(new OperationAuditQuery { Limit = 10 });
            OperationAuditChainVerificationResult chain = await service.VerifyChainAsync();

            appended.Should().BeFalse();
            query.Succeeded.Should().BeTrue();
            query.Records.Should().BeEmpty();
            chain.TotalRecords.Should().Be(0);
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkedOutbox))
            {
                TryDeleteDirectoryLink(linkedOutbox);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task AppendAsync_拒绝链接审计文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string externalFile = Path.Combine(externalDir, "external-audit.ndjson");
            string auditFile = Path.Combine(tempDir, $"operation-audit-{DateTime.Now:yyyyMMdd}.ndjson");
            File.WriteAllText(externalFile, "external");
            if (!TryCreateFileSymbolicLink(auditFile, externalFile))
            {
                return;
            }

            var service = new OperationAuditService(tempDir);

            bool appended = await service.AppendAsync(new OperationAuditRecord
            {
                Operation = "LinkedAuditFileWrite",
                Status = OperationAuditStatus.Succeeded,
                OperatorId = "op-linked-file",
                Role = ProductionRole.Engineer
            });

            appended.Should().BeFalse();
            File.ReadAllText(externalFile).Should().Be("external");
            OperationAuditQueryResult query = await service.QueryAsync(new OperationAuditQuery
            {
                Operation = "LinkedAuditFileWrite",
                Limit = 10
            });
            query.Succeeded.Should().BeTrue();
            query.Records.Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
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

    private static void TryDeleteDirectoryLink(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // 测试清理失败不应覆盖主体断言。
        }
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
