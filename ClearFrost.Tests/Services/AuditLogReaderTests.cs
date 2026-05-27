using System;
using System.IO;
using System.Linq;
using System.Text;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class AuditLogReaderTests
{
    [Fact]
    public void Read_审计日志_按时间倒序返回并跳过表头()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logBasePath = Path.Combine(tempDir, "Logs");
            WriteAuditFile(
                logBasePath,
                "2026年05月04日",
                "2026050414.txt",
                "时间\t结果\t类别\t操作\t详情",
                "2026-05-04 14:00:00.000\t成功\tSettings\tSave\tStoragePath=D:\\Data",
                "2026-05-04 14:10:00.000\t失败\tPermission\tDenied\tOperation=删除相机配置");

            var records = AuditLogReader.Read(logBasePath);

            records.Should().HaveCount(2);
            records[0].Timestamp.Should().Be(new DateTime(2026, 5, 4, 14, 10, 0));
            records[0].Success.Should().BeFalse();
            records[0].Category.Should().Be("Permission");
            records[0].IntegrityStatus.Should().Be(AuditLogIntegrity.LegacyStatus);
            records[1].Action.Should().Be("Save");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Read_带过滤条件_仅返回匹配记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logBasePath = Path.Combine(tempDir, "Logs");
            WriteAuditFile(
                logBasePath,
                "2026年05月04日",
                "2026050415.txt",
                "时间\t结果\t类别\t操作\t详情",
                "2026-05-04 15:00:00.000\t成功\tModelPackage\tImport\tModelId=yolo-a",
                "2026-05-04 15:10:00.000\t失败\tPermission\tDenied\tOperation=导入模型包",
                "2026-05-04 15:20:00.000\t成功\tCamera\tOpen\tSN=123");

            var records = AuditLogReader.Read(logBasePath, new AuditLogQuery
            {
                Success = false,
                SearchText = "导入模型",
                Limit = 10
            });

            records.Should().ContainSingle();
            records[0].Category.Should().Be("Permission");
            records[0].Detail.Should().Contain("导入模型包");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Read_限制数量_只返回最新记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logBasePath = Path.Combine(tempDir, "Logs");
            WriteAuditFile(
                logBasePath,
                "2026年05月04日",
                "2026050416.txt",
                "时间\t结果\t类别\t操作\t详情",
                "2026-05-04 16:00:00.000\t成功\tA\tOne\t1",
                "2026-05-04 16:01:00.000\t成功\tB\tTwo\t2",
                "2026-05-04 16:02:00.000\t成功\tC\tThree\t3");

            var records = AuditLogReader.Read(logBasePath, new AuditLogQuery { Limit = 2 });

            records.Select(r => r.Action).Should().Equal("Three", "Two");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Read_链式审计日志_校验完整性()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logBasePath = Path.Combine(tempDir, "Logs");
            string timestamp = "2026-05-04 17:00:00.000";
            string hash = AuditLogIntegrity.ComputeHash(
                timestamp,
                "成功",
                "Settings",
                "Save",
                "StoragePath=D:\\Data",
                AuditLogIntegrity.GenesisHash);
            WriteAuditFile(
                logBasePath,
                "2026年05月04日",
                "2026050417.txt",
                AuditLogIntegrity.Header,
                $"{timestamp}\t成功\tSettings\tSave\tStoragePath=D:\\Data\t{AuditLogIntegrity.GenesisHash}\t{hash}");

            var records = AuditLogReader.Read(logBasePath);

            records.Should().ContainSingle();
            records[0].IntegrityStatus.Should().Be(AuditLogIntegrity.ValidStatus);
            records[0].Hash.Should().Be(hash);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Read_审计行被修改_标记为异常()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using (var service = new StorageService(tempDir))
            {
                service.WriteAuditLog("Settings", "Save", "StoragePath=D:\\Data", success: true);
            }

            string file = Directory.GetFiles(
                    Path.Combine(tempDir, "Logs", "AuditLogs"),
                    "*.txt",
                    SearchOption.AllDirectories)
                .Single();
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            lines[1] = lines[1].Replace("StoragePath=D:\\Data", "StoragePath=E:\\Tampered", StringComparison.Ordinal);
            File.WriteAllLines(file, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var records = AuditLogReader.Read(Path.Combine(tempDir, "Logs"));

            records.Should().ContainSingle();
            records[0].IntegrityStatus.Should().Be(AuditLogIntegrity.TamperedStatus);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static void WriteAuditFile(string logBasePath, string dateFolder, string fileName, params string[] lines)
    {
        string dir = Path.Combine(logBasePath, "AuditLogs", dateFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, fileName), lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostAuditReaderTests", Guid.NewGuid().ToString("N"));
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
