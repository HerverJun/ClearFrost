using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIControllerDetectionLogTests
{
    [Fact]
    public void ReadDetectionLogTableEntries_拒绝链接检测日志根目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedLogsDir = Path.Combine(tempDir, "Logs", "DetectionLogs");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkedLogsDir)!);
            string externalDateDir = Path.Combine(externalDir, "2026年07月05日");
            Directory.CreateDirectory(externalDateDir);
            File.WriteAllText(
                Path.Combine(externalDateDir, "2026070510.txt"),
                CreateLogEntry("2026-07-05 10:00:00", "不合格", "外部日志"));

            if (!TryCreateDirectorySymbolicLink(linkedLogsDir, externalDir))
            {
                return;
            }

            IReadOnlyList<object> entries = WebUIController.ReadDetectionLogTableEntries(
                Path.Combine(tempDir, "Logs"),
                maxCount: 10);

            entries.Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedLogsDir);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void ReadDetectionLogTableEntries_跳过链接日期目录和链接日志文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDateDir = string.Empty;
        string linkedLogFile = string.Empty;
        try
        {
            string logBasePath = Path.Combine(tempDir, "Logs");
            string logsDir = Path.Combine(logBasePath, "DetectionLogs");
            string safeDateDir = Path.Combine(logsDir, "2026年07月05日");
            Directory.CreateDirectory(safeDateDir);

            File.WriteAllText(
                Path.Combine(safeDateDir, "2026070510.txt"),
                CreateLogEntry("2026-07-05 10:00:00", "合格", "安全日志"));

            string externalLogFile = Path.Combine(externalDir, "linked-log.txt");
            File.WriteAllText(externalLogFile, CreateLogEntry("2026-07-05 11:00:00", "不合格", "链接文件日志"));
            linkedLogFile = Path.Combine(safeDateDir, "2026070511.txt");
            if (!TryCreateFileSymbolicLink(linkedLogFile, externalLogFile))
            {
                return;
            }

            string externalDateDir = Path.Combine(externalDir, "linked-date");
            Directory.CreateDirectory(externalDateDir);
            File.WriteAllText(
                Path.Combine(externalDateDir, "2026070610.txt"),
                CreateLogEntry("2026-07-06 10:00:00", "不合格", "链接目录日志"));
            linkedDateDir = Path.Combine(logsDir, "2026年07月06日");
            if (!TryCreateDirectorySymbolicLink(linkedDateDir, externalDateDir))
            {
                return;
            }

            IReadOnlyList<object> entries = WebUIController.ReadDetectionLogTableEntries(logBasePath, maxCount: 10);

            entries.Should().ContainSingle();
            entries[0].GetPropertyValue("time").Should().Be("2026-07-05 10:00:00");
            entries[0].GetPropertyValue("result").Should().Be("合格");
            entries[0].GetPropertyValue("details").Should().Be("安全日志");
        }
        finally
        {
            TryDeleteFileLink(linkedLogFile);
            TryDeleteDirectoryLink(linkedDateDir);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    private static string CreateLogEntry(string time, string result, string details)
    {
        return $"检测时间: {time}\r\n结果: {result}\r\n{details}\r\n\r\n";
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClearFrostTests",
            nameof(WebUIControllerDetectionLogTests),
            Guid.NewGuid().ToString("N"));

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

    private static void TryDeleteDirectoryLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(path);
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

    private static void TryDeleteFileLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
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
        if (!Directory.Exists(path))
        {
            return;
        }

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
