using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class AsyncDiagnosticLoggerTests
{
    [Fact]
    public async Task DisposeAsync_拒绝链接日志目录且不写入外部目标()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDirectory = Path.Combine(tempDir, "linked-logs");
        try
        {
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, externalDir))
            {
                return;
            }

            var logger = new AsyncDiagnosticLogger(Path.Combine(linkedDirectory, "diagnostic.log"));

            logger.Enqueue("unsafe linked directory").Should().BeTrue();
            await logger.DisposeAsync();

            logger.FailedCount.Should().BeGreaterThan(0);
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
    public async Task DisposeAsync_拒绝链接日志文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedLogPath = Path.Combine(tempDir, "diagnostic.log");
        try
        {
            string externalLogPath = Path.Combine(externalDir, "external.log");
            File.WriteAllText(externalLogPath, "external log");
            if (!TryCreateFileSymbolicLink(linkedLogPath, externalLogPath))
            {
                return;
            }

            var logger = new AsyncDiagnosticLogger(linkedLogPath);

            logger.Enqueue("unsafe linked file").Should().BeTrue();
            await logger.DisposeAsync();

            logger.FailedCount.Should().BeGreaterThan(0);
            File.ReadAllText(externalLogPath).Should().Be("external log");
        }
        finally
        {
            TryDeleteFileLink(linkedLogPath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostAsyncDiagnosticLoggerTests", Guid.NewGuid().ToString("N"));
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
