using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Core.Models;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Models;

public class ModelPackageImporterTests
{
    [Fact]
    public void Import_有效Onnx_创建Manifest并发布到Onnx目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string sourcePath = Path.Combine(tempDir, "source.onnx");
            string packageRoot = Path.Combine(tempDir, "models");
            string onnxDir = Path.Combine(tempDir, "ONNX");
            File.WriteAllBytes(sourcePath, new byte[] { 1, 2, 3, 4 });

            ModelPackageImportResult result = ModelPackageImporter.Import(new ModelPackageImportOptions
            {
                SourceModelPath = sourcePath,
                PackageDirectory = packageRoot,
                OnnxDirectory = onnxDir,
                ModelId = "heater-screw",
                Version = "2026.05",
                Labels = new[] { "screw", "nut", "screw" },
                Description = "acceptance test",
                Warmup = (_, _) => true
            });

            result.Success.Should().BeTrue();
            result.RegistryEntry.Should().NotBeNull();
            result.RegistryEntry!.ModelId.Should().Be("heater-screw");
            result.RegistryEntry.Status.Should().Be(ModelRegistryStatus.Ready);
            File.Exists(result.ManifestPath).Should().BeTrue();
            File.Exists(result.ModelPath).Should().BeTrue();
            File.Exists(result.PublishedOnnxPath).Should().BeTrue();

            string expectedHash = ComputeSha256(result.ModelPath);
            ModelPackageManifest manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                File.ReadAllText(result.ManifestPath))!;
            manifest.ModelId.Should().Be("heater-screw");
            manifest.Version.Should().Be("2026.05");
            manifest.ModelHash.Should().Be(expectedHash);
            manifest.Sha256.Should().Be(expectedHash);
            manifest.Labels.Should().Equal("screw", "nut");

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                OnnxDirectory = onnxDir,
                Warmup = (_, _) => true
            });
            registry.Resolve("heater-screw")!.ModelHash.Should().Be(expectedHash);
            registry.Resolve(Path.GetFileName(result.PublishedOnnxPath))!.IsPackage.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Import_目标包已存在且不覆盖_返回失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string sourcePath = Path.Combine(tempDir, "source.onnx");
            string packageRoot = Path.Combine(tempDir, "models");
            Directory.CreateDirectory(Path.Combine(packageRoot, "pkg-a"));
            File.WriteAllBytes(sourcePath, new byte[] { 1 });

            ModelPackageImportResult result = ModelPackageImporter.Import(new ModelPackageImportOptions
            {
                SourceModelPath = sourcePath,
                PackageDirectory = packageRoot,
                ModelId = "pkg-a",
                Version = "1",
                Labels = new[] { "screw" }
            });

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("已存在");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Import_标签为空_返回失败且不创建包()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string sourcePath = Path.Combine(tempDir, "source.onnx");
            string packageRoot = Path.Combine(tempDir, "models");
            File.WriteAllBytes(sourcePath, new byte[] { 1 });

            ModelPackageImportResult result = ModelPackageImporter.Import(new ModelPackageImportOptions
            {
                SourceModelPath = sourcePath,
                PackageDirectory = packageRoot,
                ModelId = "pkg-empty-labels",
                Version = "1",
                Labels = Array.Empty<string>()
            });

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("标签");
            Directory.Exists(Path.Combine(packageRoot, "pkg-empty-labels")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(ModelPackageImporterTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
