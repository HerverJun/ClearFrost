using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class YoloDirectMlProfilePathTests
{
    [Fact]
    public void CreateDirectMlProfileOutputPathPrefix_创建专属安全目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string profileDirectory = YoloDetector.GetDirectMlProfileDirectory(tempDir);

            string prefix = YoloDetector.CreateDirectMlProfileOutputPathPrefix(tempDir);

            Directory.Exists(profileDirectory).Should().BeTrue();
            prefix.Should().StartWith(profileDirectory + Path.DirectorySeparatorChar);
            Path.GetFileName(prefix).Should().StartWith("clearfrost-dml-");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void CreateDirectMlProfileOutputPathPrefix_拒绝链接Profile目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedProfileDir = YoloDetector.GetDirectMlProfileDirectory(tempDir);
        try
        {
            if (!TryCreateDirectorySymbolicLink(linkedProfileDir, externalDir))
            {
                return;
            }

            Action act = () => YoloDetector.CreateDirectMlProfileOutputPathPrefix(tempDir);

            act.Should().Throw<IOException>().WithMessage("*profiling 输出目录不安全*");
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedProfileDir);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void DirectMlProfileFile_安全文件可以读取并删除()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string profileDirectory = YoloDetector.GetDirectMlProfileDirectory(tempDir);
            Directory.CreateDirectory(profileDirectory);
            string profilePath = Path.Combine(profileDirectory, "profile.json");
            File.WriteAllText(profilePath, "{\"provider\":\"DmlExecutionProvider\"}");

            bool read = YoloDetector.TryReadDirectMlProfileText(profilePath, profileDirectory, out string profileText);
            bool deleted = YoloDetector.TryDeleteDirectMlProfileFile(profilePath, profileDirectory);

            read.Should().BeTrue();
            profileText.Should().Contain("DmlExecutionProvider");
            deleted.Should().BeTrue();
            File.Exists(profilePath).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void DirectMlProfileFile_拒绝链接Profile文件且不删除外部目标()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedProfileFile = string.Empty;
        try
        {
            string profileDirectory = YoloDetector.GetDirectMlProfileDirectory(tempDir);
            Directory.CreateDirectory(profileDirectory);
            string externalProfileFile = Path.Combine(externalDir, "profile.json");
            File.WriteAllText(externalProfileFile, "external profile");
            linkedProfileFile = Path.Combine(profileDirectory, "profile.json");
            if (!TryCreateFileSymbolicLink(linkedProfileFile, externalProfileFile))
            {
                return;
            }

            YoloDetector.IsSafeDirectMlProfileFile(linkedProfileFile, profileDirectory).Should().BeFalse();
            YoloDetector.TryReadDirectMlProfileText(linkedProfileFile, profileDirectory, out string profileText).Should().BeFalse();
            YoloDetector.TryDeleteDirectMlProfileFile(linkedProfileFile, profileDirectory).Should().BeFalse();

            profileText.Should().BeEmpty();
            File.ReadAllText(externalProfileFile).Should().Be("external profile");
        }
        finally
        {
            TryDeleteFileLink(linkedProfileFile);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void DirectMlProfileFile_拒绝目录外文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string profileDirectory = YoloDetector.GetDirectMlProfileDirectory(tempDir);
            Directory.CreateDirectory(profileDirectory);
            string externalProfileFile = Path.Combine(externalDir, "profile.json");
            File.WriteAllText(externalProfileFile, "external profile");

            YoloDetector.IsSafeDirectMlProfileFile(externalProfileFile, profileDirectory).Should().BeFalse();
            YoloDetector.TryReadDirectMlProfileText(externalProfileFile, profileDirectory, out _).Should().BeFalse();
            YoloDetector.TryDeleteDirectMlProfileFile(externalProfileFile, profileDirectory).Should().BeFalse();

            File.Exists(externalProfileFile).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClearFrostTests",
            nameof(YoloDirectMlProfilePathTests),
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
