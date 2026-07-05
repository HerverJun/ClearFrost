using System.Reflection;
using ClearFrost.Helpers;
using FluentAssertions;

namespace ClearFrost.Tests.Helpers;

public class RuntimePathsTests
{
    [Fact]
    public void ScopedRoot_不同安装目录生成不同运行时目录()
    {
        MethodInfo method = GetScopedRootMethod();

        string rootA = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"C:\Apps\ClearFrost\publish");
        string rootB = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"D:\Backup\ClearFrost\publish");

        rootA.Should().NotBe(rootB);
        rootA.Should().StartWith(@"C:\Users\Test\AppData\Local\ClearFrost");
        rootB.Should().StartWith(@"C:\Users\Test\AppData\Local\ClearFrost");
    }

    [Fact]
    public void ScopedRoot_相同安装目录生成稳定运行时目录()
    {
        MethodInfo method = GetScopedRootMethod();

        string rootA = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"C:\Apps\ClearFrost\publish");
        string rootB = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"C:\Apps\ClearFrost\publish");

        rootA.Should().Be(rootB);
    }

    [Fact]
    public void EnsureWritableDirectory_跳过链接主目录且不写入外部目标()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimePathsTests", Guid.NewGuid().ToString("N"));
        string externalDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimePathsTests", Guid.NewGuid().ToString("N"));
        string linkedRoot = Path.Combine(tempDir, "linked-root");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(externalDir);

        try
        {
            if (!TryCreateDirectorySymbolicLink(linkedRoot, externalDir))
            {
                return;
            }

            MethodInfo method = GetEnsureWritableDirectoryMethod();

            string resolved = InvokeEnsureWritableDirectoryMethod(method, linkedRoot);

            resolved.Should().NotBe(Path.GetFullPath(linkedRoot));
            Directory.Exists(resolved).Should().BeTrue();
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedRoot);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    private static MethodInfo GetScopedRootMethod()
    {
        MethodInfo? method = typeof(RuntimePaths).GetMethod(
            "GetScopedDefaultRootCandidate",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return method!;
    }

    private static MethodInfo GetEnsureWritableDirectoryMethod()
    {
        MethodInfo? method = typeof(RuntimePaths).GetMethod(
            "EnsureWritableDirectory",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return method!;
    }

    private static string InvokeScopedRootMethod(MethodInfo method, string parentRoot, string baseDirectory)
    {
        object? result = method.Invoke(null, new object[] { parentRoot, baseDirectory });
        result.Should().BeOfType<string>();
        return (string)result!;
    }

    private static string InvokeEnsureWritableDirectoryMethod(MethodInfo method, string primaryPath)
    {
        object? result = method.Invoke(null, new object[] { primaryPath });
        result.Should().BeOfType<string>();
        return (string)result!;
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
