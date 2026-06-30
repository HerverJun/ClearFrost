using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class StartSystemFaultGateTests
{
    [Fact]
    public void StartSystemAsync_生产就绪检查在连接相机和启动触发源之前()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "主窗口.Init.cs"));

        int readinessIndex = source.IndexOf("EnsureReadyForProduction()", StringComparison.Ordinal);
        int cameraIndex = source.IndexOf("启动系统: 正在连接相机", StringComparison.Ordinal);
        int triggerIndex = source.IndexOf("启动系统: 正在启动触发源", StringComparison.Ordinal);

        readinessIndex.Should().BeGreaterThanOrEqualTo(0);
        cameraIndex.Should().BeGreaterThan(readinessIndex);
        triggerIndex.Should().BeGreaterThan(readinessIndex);
        source.Should().Contain("生产模型未就绪");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClearFrost.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ClearFrost.sln.");
    }
}
