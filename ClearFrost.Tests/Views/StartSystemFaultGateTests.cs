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

    [Fact]
    public void StartTriggerSourceAsync_手动检测不会启动自动生产触发源()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "主窗口.Init.cs"));
        int methodIndex = source.IndexOf("private async Task<bool> StartTriggerSourceAsync()", StringComparison.Ordinal);
        methodIndex.Should().BeGreaterThanOrEqualTo(0);
        string method = source[methodIndex..];

        int manualIndex = method.IndexOf("TriggerSource.Manual", StringComparison.Ordinal);
        int serialIndex = method.IndexOf("TriggerSource.SerialPhotoelectric", StringComparison.Ordinal);
        int plcIndex = method.IndexOf("StartPlcTriggerMonitoringIfReadyAsync", StringComparison.Ordinal);

        manualIndex.Should().BeGreaterThanOrEqualTo(0);
        serialIndex.Should().BeGreaterThan(manualIndex);
        plcIndex.Should().BeGreaterThan(manualIndex);
        method.Should().Contain("手动检测模式已启用：自动生产触发未启动");
        method.Should().Contain("_plcService.StopMonitoring();");
        method.Should().Contain("_serialTriggerService.Stop();");
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
