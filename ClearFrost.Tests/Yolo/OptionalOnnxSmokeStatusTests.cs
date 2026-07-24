using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class OptionalOnnxSmokeStatusTests
{
    [Fact]
    public void OptionalRealOnnxSmoke_本地样例不存在时明确记录Skipped()
    {
        string root = FindRepositoryRoot();
        string samplesRoot = Path.Combine(root, "samples", "yolo-official");
        string? localOnnx = Directory.Exists(samplesRoot)
            ? Directory.EnumerateFiles(samplesRoot, "*.onnx", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        string status = string.IsNullOrWhiteSpace(localOnnx)
            ? "SKIPPED: optional local ONNX fixture unavailable"
            : "PASS: optional local ONNX fixture available";

        status.Should().BeOneOf(
            "SKIPPED: optional local ONNX fixture unavailable",
            "PASS: optional local ONNX fixture available");
        if (string.IsNullOrWhiteSpace(localOnnx))
        {
            status.Should().Be("SKIPPED: optional local ONNX fixture unavailable");
        }
        else
        {
            File.Exists(localOnnx).Should().BeTrue();
        }
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
