using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class CameraSelectionStateSyncContractTests
{
    [Fact]
    public void 相机切换完成后会把真实相机列表同步回前端()
    {
        string root = FindRepositoryRoot();
        string source = NormalizeLineEndings(File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "主窗口.Init.cs")));

        source.Should().Contain("_uiController.OnSwitchCamera += async");
        source.Should().Contain("_uiController.OnAddCamera += async");
        source.Should().Contain("_uiController.OnDeleteCamera += async");
        CountOccurrences(
                source,
                "finally\n                {\n                    await SendConfiguredCameraListToFrontendAsync();\n                }")
            .Should().BeGreaterThanOrEqualTo(3);
        source.Should().Contain("NormalizeConfiguredActiveCameraId();\n                    if (_appConfig.Save())");
        source.Should().Contain("private Task SendConfiguredCameraListToFrontendAsync()");
        source.Should().Contain("return _uiController.SendCameraList(cameras, ResolveConfiguredActiveCameraId())");
        source.Should().Contain("private string ResolveConfiguredActiveCameraId()");
        source.Should().Contain("_appConfig.Cameras.Any(camera => string.Equals(camera.Id, managerActiveId, StringComparison.Ordinal))");
        source.Should().Contain("return _appConfig.Cameras.FirstOrDefault(camera => camera.IsEnabled)?.Id");
        source.Should().Contain("private void NormalizeConfiguredActiveCameraId()");
        source.Should().Contain("_appConfig.ActiveCameraId = activeCameraId");
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
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
