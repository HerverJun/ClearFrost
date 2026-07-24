using ClearFrost.Helpers;
using FluentAssertions;

namespace ClearFrost.Tests.Helpers;

public class AtomicFileWriterTests
{
    [Fact]
    public void WriteAllText_拒绝链接目标文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalFile = Path.Combine(tempDir, "external.txt");
            string linkFile = Path.Combine(tempDir, "target.txt");
            File.WriteAllText(externalFile, "external");
            if (!TryCreateFileSymbolicLink(linkFile, externalFile))
            {
                return;
            }

            Action act = () => AtomicFileWriter.WriteAllText(linkFile, "changed");

            act.Should().Throw<IOException>().WithMessage("*链接文件*");
            File.ReadAllText(externalFile).Should().Be("external");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void RestoreAllBytes_拒绝链接目标文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalFile = Path.Combine(tempDir, "external.bin");
            string linkFile = Path.Combine(tempDir, "target.bin");
            File.WriteAllBytes(externalFile, new byte[] { 1, 2, 3 });
            if (!TryCreateFileSymbolicLink(linkFile, externalFile))
            {
                return;
            }

            Action act = () => AtomicFileWriter.RestoreAllBytes(linkFile, new byte[] { 9, 9, 9 });

            act.Should().Throw<IOException>().WithMessage("*链接文件*");
            File.ReadAllBytes(externalFile).Should().Equal(new byte[] { 1, 2, 3 });
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void WriteAllText_拒绝链接Backup文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string targetFile = Path.Combine(tempDir, "target.txt");
            string backupLink = targetFile + ".bak";
            string externalFile = Path.Combine(tempDir, "external-backup.txt");
            File.WriteAllText(targetFile, "original");
            File.WriteAllText(externalFile, "external");
            if (!TryCreateFileSymbolicLink(backupLink, externalFile))
            {
                return;
            }

            Action act = () => AtomicFileWriter.WriteAllText(targetFile, "changed");

            act.Should().Throw<IOException>().WithMessage("*备份文件*链接文件*");
            File.ReadAllText(targetFile).Should().Be("original");
            File.ReadAllText(externalFile).Should().Be("external");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void WriteAllText_拒绝Backup路径是目录且不修改目标文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string targetFile = Path.Combine(tempDir, "target.txt");
            string backupDirectory = targetFile + ".bak";
            File.WriteAllText(targetFile, "original");
            Directory.CreateDirectory(backupDirectory);

            Action act = () => AtomicFileWriter.WriteAllText(targetFile, "changed");

            act.Should().Throw<IOException>().WithMessage("*备份文件*目录*");
            File.ReadAllText(targetFile).Should().Be("original");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void WriteAllText_拒绝链接父目录且不写入外部目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalDir = Path.Combine(tempDir, "external");
            string linkDir = Path.Combine(tempDir, "linked");
            Directory.CreateDirectory(externalDir);
            if (!TryCreateDirectorySymbolicLink(linkDir, externalDir))
            {
                return;
            }

            string targetFile = Path.Combine(linkDir, "config.json");

            Action act = () => AtomicFileWriter.WriteAllText(targetFile, "{}");

            act.Should().Throw<IOException>().WithMessage("*链接目录*");
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void WriteAllText_拒绝链接父目录下缺失子目录且不创建外部目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalDir = Path.Combine(tempDir, "external");
            string linkDir = Path.Combine(tempDir, "linked");
            Directory.CreateDirectory(externalDir);
            if (!TryCreateDirectorySymbolicLink(linkDir, externalDir))
            {
                return;
            }

            string targetFile = Path.Combine(linkDir, "nested", "config.json");

            Action act = () => AtomicFileWriter.WriteAllText(targetFile, "{}");

            act.Should().Throw<IOException>().WithMessage("*链接目录*");
            Directory.Exists(Path.Combine(externalDir, "nested")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void WriteAllText_正常文件保持Utf8Bom原子写入()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string targetFile = Path.Combine(tempDir, "normal", "config.json");

            AtomicFileWriter.WriteAllText(targetFile, "{\"ok\":true}");

            byte[] bytes = File.ReadAllBytes(targetFile);
            bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF });
            File.ReadAllText(targetFile).Should().Contain("\"ok\":true");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(AtomicFileWriterTests), Guid.NewGuid().ToString("N"));
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
