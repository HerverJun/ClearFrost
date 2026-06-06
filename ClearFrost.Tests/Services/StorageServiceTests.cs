using System;
using System.IO;
using System.Linq;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class StorageServiceTests
{
    [Fact]
    public void WriteAuditLog_有效操作_写入按小时归档的审计日志()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using var service = new StorageService(tempDir);

            service.WriteAuditLog("Settings", "Save", @"StoragePath=C:\GreeVisionData", success: true);

            string[] files = Directory.GetFiles(
                Path.Combine(tempDir, "Logs", "AuditLogs"),
                "*.txt",
                SearchOption.AllDirectories);
            files.Should().HaveCount(1);

            string content = File.ReadAllText(files[0]);
            content.Should().Contain("时间\t结果\t类别\t操作\t详情\tPrevHash\tHash");
            content.Should().Contain("\t成功\tSettings\tSave\tStoragePath=C:\\GreeVisionData");
            string[] lines = File.ReadAllLines(files[0]);
            string[] parts = lines[1].Split('\t');
            parts.Should().HaveCount(7);
            parts[5].Should().Be(AuditLogIntegrity.GenesisHash);
            parts[6].Should().HaveLength(64);

            var records = AuditLogReader.Read(Path.Combine(tempDir, "Logs"));
            records.Should().ContainSingle();
            records[0].IntegrityStatus.Should().Be(AuditLogIntegrity.ValidStatus);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void WriteAuditLog_字段包含换行和制表符_规范化为单行记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using var service = new StorageService(tempDir);

            service.WriteAuditLog("Model\nPackage", "Import\tONNX", "line1\r\nline2", success: false);

            string file = Directory.GetFiles(
                    Path.Combine(tempDir, "Logs", "AuditLogs"),
                    "*.txt",
                    SearchOption.AllDirectories)
                .Single();
            string[] lines = File.ReadAllLines(file);

            lines.Should().HaveCount(2);
            lines[1].Should().Contain("\t失败\tModel Package\tImport ONNX\tline1  line2");
            lines[1].Split('\t').Should().HaveCount(7);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void CleanOldData_显式手动调用_清理旧数据并写审计()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string oldImageDir = Path.Combine(tempDir, "Images", "Qualified", "2000年01月01日");
            Directory.CreateDirectory(oldImageDir);
            File.WriteAllText(Path.Combine(oldImageDir, "sample.txt"), "old");

            using var service = new StorageService(tempDir);

            service.CleanOldData(30);

            Directory.Exists(oldImageDir).Should().BeFalse();
            var records = AuditLogReader.Read(Path.Combine(tempDir, "Logs"));
            records.Should().Contain(record =>
                record.Category == "DataRetention" &&
                record.Action == "ManualCleanup" &&
                record.Success &&
                record.Detail.Contains("Days=30"));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostStorageTests", Guid.NewGuid().ToString("N"));
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
