using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIVisionDebugCoordinateMappingTests
{
    [Fact]
    public void CoordinateMappingJs_覆盖常见纵横比和Letterbox映射()
    {
        string root = FindRepositoryRoot();
        string mappingPath = Path.Combine(root, "ClearFrost", "html", "js", "coordinate-mapping.js");
        string script =
            "const api = require(" + JsonSerializer.Serialize(mappingPath) + ");" +
            "const result = api.runCoordinateMappingSelfTests();" +
            "if (!result.ok || result.count !== 4) {" +
            "throw new Error('unexpected self-test result: ' + JSON.stringify(result));" +
            "}";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);

        process.Start().Should().BeTrue();
        process.WaitForExit(10_000).Should().BeTrue();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.ExitCode.Should().Be(0, $"Node stdout: {output}; stderr: {error}");
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
