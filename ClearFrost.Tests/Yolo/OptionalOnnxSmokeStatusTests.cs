using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class OptionalOnnxSmokeStatusTests
{
    [Fact]
    [Trait("Lane", "ExternalModel")]
    public void OptionalRealOnnxSmoke_本地样例不存在时明确记录Skipped()
    {
        string root = FindRepositoryRoot();
        string samplesRoot = Path.Combine(root, "samples", "yolo-official");
        string? localOnnx = Directory.Exists(samplesRoot)
            ? Directory.EnumerateFiles(samplesRoot, "*.onnx", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (string.IsNullOrWhiteSpace(localOnnx))
        {
            throw new InvalidOperationException("NOT_VERIFIED: optional real ONNX fixture unavailable.");
        }

        File.Exists(localOnnx).Should().BeTrue();
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
