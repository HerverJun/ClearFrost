using ClearFrost.Services;
using FluentAssertions;

using System.Drawing;

namespace ClearFrost.Tests.Services;

public class StorageServiceTests
{
    [Fact]
    public void EnsureDirectoriesExist_创建关键证据目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using var storage = new StorageService(tempDir);

            Directory.Exists(storage.ImageBasePath).Should().BeTrue();
            Directory.Exists(storage.LogBasePath).Should().BeTrue();
            Directory.Exists(storage.SystemPath).Should().BeTrue();
            Directory.Exists(Path.Combine(storage.LogBasePath, "Outbox")).Should().BeTrue();
            Directory.Exists(Path.Combine(storage.LogBasePath, "Diagnostics")).Should().BeTrue();
            Directory.Exists(Path.Combine(storage.LogBasePath, "HandoffReports")).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void SaveDetectionImage_拒绝链接存储根且不写入外部目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedStoragePath = Path.Combine(tempDir, "linked-storage");
        try
        {
            if (!TryCreateDirectorySymbolicLink(linkedStoragePath, externalDir))
            {
                return;
            }

            using var storage = new StorageService(linkedStoragePath);
            using var bitmap = new Bitmap(8, 8);

            storage.SaveDetectionImage(bitmap, isQualified: true);

            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedStoragePath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void WriteDetectionLog_拒绝运行中被替换为链接的日志目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedLogPath = string.Empty;
        try
        {
            using var storage = new StorageService(tempDir);
            linkedLogPath = storage.LogBasePath;
            Directory.Delete(linkedLogPath, recursive: true);
            if (!TryCreateDirectorySymbolicLink(linkedLogPath, externalDir))
            {
                return;
            }

            storage.WriteDetectionLog("linked log", isQualified: false);

            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedLogPath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void PerformEmergencyCleanup_跳过目录外路径和链接旧图片目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            using var storage = new StorageService(tempDir);
            string oldDate = DateTime.Now.AddDays(-3).ToString("yyyyMMdd");
            string oldQualifiedParent = Path.Combine(storage.ImageBasePath, "Qualified");
            string linkPath = Path.Combine(oldQualifiedParent, oldDate);
            string externalFile = Path.Combine(externalDir, "external.txt");
            string outboxFile = Path.Combine(storage.LogBasePath, "Outbox", "operation-audit-20260101.ndjson");

            Directory.CreateDirectory(oldQualifiedParent);
            File.WriteAllText(externalFile, "external");
            File.WriteAllText(outboxFile, "audit");

            storage.IsSafeCleanupDirectoryPath(externalDir).Should().BeFalse();
            storage.IsSafeCleanupFilePath(outboxFile).Should().BeFalse();

            bool linkCreated = TryCreateDirectorySymbolicLink(linkPath, externalDir);
            if (linkCreated)
            {
                storage.IsSafeCleanupDirectoryPath(linkPath).Should().BeFalse();

                storage.PerformEmergencyCleanup();

                Directory.Exists(linkPath).Should().BeTrue();
                File.Exists(externalFile).Should().BeTrue();
            }
        }
        finally
        {
            TryDeleteDirectoryLink(Path.Combine(
                tempDir,
                "Images",
                "Qualified",
                DateTime.Now.AddDays(-3).ToString("yyyyMMdd")));
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void PerformEmergencyCleanup_保留证据目录并删除旧检测图片()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using var storage = new StorageService(tempDir);
            string oldDate = DateTime.Now.AddDays(-3).ToString("yyyyMMdd");
            string today = DateTime.Now.ToString("yyyy年MM月dd日");
            string oldQualifiedDir = Path.Combine(storage.ImageBasePath, "Qualified", oldDate);
            string todayQualifiedDir = Path.Combine(storage.ImageBasePath, "Qualified", today);
            string outboxFile = Path.Combine(storage.LogBasePath, "Outbox", "operation-audit-20260101.ndjson");
            string packageFile = Path.Combine(storage.LogBasePath, "Diagnostics", "ClearFrost_Diagnostics_20260101_000000_000_legacy.zip");
            string handoffFile = Path.Combine(storage.LogBasePath, "HandoffReports", "handoff-legacy.md");
            string systemEvidenceFile = Path.Combine(storage.SystemPath, "ReplayEvidence", "approval.json");

            Directory.CreateDirectory(oldQualifiedDir);
            Directory.CreateDirectory(todayQualifiedDir);
            Directory.CreateDirectory(Path.GetDirectoryName(systemEvidenceFile)!);
            File.WriteAllText(Path.Combine(oldQualifiedDir, "old.jpg"), "old image");
            File.WriteAllText(Path.Combine(todayQualifiedDir, "today.jpg"), "today image");
            File.WriteAllText(outboxFile, "audit");
            File.WriteAllText(packageFile, "diagnostics");
            File.WriteAllText(handoffFile, "handoff");
            File.WriteAllText(systemEvidenceFile, "evidence");

            storage.PerformEmergencyCleanup();

            Directory.Exists(oldQualifiedDir).Should().BeFalse();
            Directory.Exists(todayQualifiedDir).Should().BeTrue();
            File.Exists(outboxFile).Should().BeTrue();
            File.Exists(packageFile).Should().BeTrue();
            File.Exists(handoffFile).Should().BeTrue();
            File.Exists(systemEvidenceFile).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void PerformEmergencyCleanup_跳过包含链接子目录的旧图片目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string? linkPath = null;
        try
        {
            using var storage = new StorageService(tempDir);
            string oldDate = DateTime.Now.AddDays(-3).ToString("yyyyMMdd");
            string oldQualifiedDir = Path.Combine(storage.ImageBasePath, "Qualified", oldDate);
            linkPath = Path.Combine(oldQualifiedDir, "linked-external");
            string externalFile = Path.Combine(externalDir, "external.txt");

            Directory.CreateDirectory(oldQualifiedDir);
            File.WriteAllText(Path.Combine(oldQualifiedDir, "old.jpg"), "old image");
            File.WriteAllText(externalFile, "external");
            if (!TryCreateDirectorySymbolicLink(linkPath, externalDir))
            {
                return;
            }

            storage.IsSafeCleanupDirectoryPath(oldQualifiedDir).Should().BeFalse();

            storage.PerformEmergencyCleanup();

            Directory.Exists(oldQualifiedDir).Should().BeTrue();
            Directory.Exists(linkPath).Should().BeTrue();
            File.Exists(externalFile).Should().BeTrue();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkPath))
            {
                TryDeleteDirectoryLink(linkPath);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void CleanOldData_跳过运行中被替换为链接的图片父目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string? linkedQualifiedParent = null;
        try
        {
            using var storage = new StorageService(tempDir);
            string oldDate = DateTime.Now.AddDays(-3).ToString("yyyyMMdd");
            linkedQualifiedParent = Path.Combine(storage.ImageBasePath, "Qualified");
            string externalOldDir = Path.Combine(externalDir, oldDate);
            string externalFile = Path.Combine(externalOldDir, "external-old.jpg");

            if (Directory.Exists(linkedQualifiedParent))
            {
                Directory.Delete(linkedQualifiedParent, recursive: true);
            }

            Directory.CreateDirectory(externalOldDir);
            File.WriteAllText(externalFile, "external image");
            if (!TryCreateDirectorySymbolicLink(linkedQualifiedParent, externalDir))
            {
                return;
            }

            string linkedOldDir = Path.Combine(linkedQualifiedParent, oldDate);
            storage.IsSafeCleanupDirectoryPath(linkedOldDir).Should().BeFalse();

            storage.CleanOldData(retainDays: 1);

            Directory.Exists(externalOldDir).Should().BeTrue();
            File.Exists(externalFile).Should().BeTrue();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkedQualifiedParent))
            {
                TryDeleteDirectoryLink(linkedQualifiedParent);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void IsSafeCleanupFilePath_拒绝链接父目录下的错误日志()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string? linkedLogPath = null;
        try
        {
            using var storage = new StorageService(tempDir);
            linkedLogPath = storage.LogBasePath;
            Directory.Delete(linkedLogPath, recursive: true);
            string externalLog = Path.Combine(externalDir, "ErrorLog_20260101.txt");
            File.WriteAllText(externalLog, "external error log");
            if (!TryCreateDirectorySymbolicLink(linkedLogPath, externalDir))
            {
                return;
            }

            string linkedLog = Path.Combine(linkedLogPath, Path.GetFileName(externalLog));

            storage.IsSafeCleanupFilePath(linkedLog).Should().BeFalse();
            File.ReadAllText(externalLog).Should().Be("external error log");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkedLogPath))
            {
                TryDeleteDirectoryLink(linkedLogPath);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostStorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
}
