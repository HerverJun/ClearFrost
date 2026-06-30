using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Core.Models;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Models;

public class ModelRegistryTests
{
    [Fact]
    public void Scan_旧裸Onnx会保留为兼容Warning()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string onnxDir = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(onnxDir);
            string modelPath = Path.Combine(onnxDir, "legacy.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions { OnnxDirectory = onnxDir });

            registry.Entries.Should().ContainSingle();
            ModelRegistryEntry entry = registry.Entries[0];
            entry.Status.Should().Be(ModelRegistryStatus.Warning);
            entry.ModelId.Should().Be("legacy");
            entry.ModelHash.Should().Be(ComputeSha256(modelPath));
            entry.ApprovedForProduction.Should().BeFalse();
            registry.Resolve("legacy.onnx").Should().BeSameAs(entry);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_生产准入开启时裸Onnx被阻断()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string onnxDir = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(onnxDir);
            string modelPath = Path.Combine(onnxDir, "legacy.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                OnnxDirectory = onnxDir,
                RequireProductionApproval = true
            });

            registry.Entries.Should().ContainSingle();
            registry.Entries[0].Status.Should().Be(ModelRegistryStatus.Blocked);
            registry.Entries[0].ApprovedForProduction.Should().BeFalse();
            registry.ValidateForProductionActivation(modelPath).Succeeded.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_返回快照不会被后续扫描清空()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string onnxDir = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(onnxDir);
            string modelPath = Path.Combine(onnxDir, "legacy.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });

            var registry = new ModelRegistry();
            IReadOnlyList<ModelRegistryEntry> firstSnapshot = registry.Scan(new ModelRegistryScanOptions { OnnxDirectory = onnxDir });

            firstSnapshot.Should().ContainSingle();

            File.Delete(modelPath);
            IReadOnlyList<ModelRegistryEntry> secondSnapshot = registry.Scan(new ModelRegistryScanOptions { OnnxDirectory = onnxDir });

            secondSnapshot.Should().BeEmpty();
            firstSnapshot.Should().ContainSingle(e => e.UsedModelName == "legacy.onnx");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_严格模型包校验HashLabels和Warmup()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-a");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 8, 9, 10 });
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-a",
                    Version = "2026.04",
                    ModelFileName = "model.onnx",
                    ModelHash = ComputeSha256(modelPath),
                    Labels = new List<string> { "screw" }
                }));

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                StrictPackageMode = true,
                Warmup = (_, _) => true
            });

            registry.HasBlockingErrors.Should().BeFalse();
            registry.Entries.Should().ContainSingle(e =>
                e.ModelId == "pkg-a" &&
                e.Status == ModelRegistryStatus.Ready &&
                e.ModelHash == ComputeSha256(modelPath));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_严格模型包Hash错误会阻塞Ready()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-b");
            Directory.CreateDirectory(packageDir);
            File.WriteAllBytes(Path.Combine(packageDir, "model.onnx"), new byte[] { 1 });
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-b",
                    Version = "1",
                    ModelHash = "bad-hash",
                    Labels = new List<string> { "screw" }
                }));

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                StrictPackageMode = true
            });

            registry.HasBlockingErrors.Should().BeTrue();
            registry.Entries[0].Status.Should().Be(ModelRegistryStatus.Blocked);
            registry.Entries[0].Message.Should().Contain("hash");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_严格模型包默认Warmup会拦截无效Onnx()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-invalid");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-invalid",
                    Version = "1",
                    ModelHash = ComputeSha256(modelPath),
                    Labels = new List<string> { "screw" }
                }));

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                StrictPackageMode = true
            });

            registry.HasBlockingErrors.Should().BeTrue();
            registry.Entries[0].Status.Should().Be(ModelRegistryStatus.Blocked);
            registry.Entries[0].Message.Should().Contain("warmup");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Resolve_同名模型存在歧义时优先返回Package条目()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageA = Path.Combine(packageRoot, "pkg-a");
            string packageB = Path.Combine(packageRoot, "pkg-b");
            Directory.CreateDirectory(packageA);
            Directory.CreateDirectory(packageB);
            string modelA = Path.Combine(packageA, "model.onnx");
            string modelB = Path.Combine(packageB, "model.onnx");
            File.WriteAllBytes(modelA, new byte[] { 1 });
            File.WriteAllBytes(modelB, new byte[] { 2 });
            WriteManifest(packageA, "pkg-a", modelA);
            WriteManifest(packageB, "pkg-b", modelB);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                Warmup = (_, _) => true
            });

            ModelRegistryEntry? resolved = registry.Resolve("model.onnx");
            resolved.Should().NotBeNull();
            resolved!.IsPackage.Should().BeTrue();
            registry.Resolve("pkg-a").Should().NotBeNull();
            registry.Resolve(modelB).Should().NotBeNull();
            registry.Resolve(modelB)!.ModelId.Should().Be("pkg-b");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ValidateForProductionActivation_未注册模型被拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string modelPath = Path.Combine(tempDir, "unregistered.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
            var registry = new ModelRegistry();

            ModelProductionValidationResult result = registry.ValidateForProductionActivation(modelPath);

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ProductionModelNotRegistered");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ValidateForProductionActivation_同名不同路径被拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageA = Path.Combine(packageRoot, "pkg-a");
            string packageB = Path.Combine(packageRoot, "pkg-b");
            Directory.CreateDirectory(packageA);
            Directory.CreateDirectory(packageB);
            string modelA = Path.Combine(packageA, "model.onnx");
            string modelB = Path.Combine(packageB, "model.onnx");
            File.WriteAllBytes(modelA, new byte[] { 1 });
            File.WriteAllBytes(modelB, new byte[] { 2 });
            WriteApprovedManifest(packageA, "pkg-a", modelA);
            WriteApprovedManifest(packageB, "pkg-b", modelB);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });

            ModelProductionValidationResult result = registry.ValidateForProductionActivation(modelA);

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ProductionModelNameAmbiguous");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ValidateForProductionActivation_批准后文件篡改被拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-a");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "approved.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
            WriteApprovedManifest(packageDir, "pkg-a", modelPath, "approved.onnx");

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            File.WriteAllBytes(modelPath, new byte[] { 9, 9, 9 });

            ModelProductionValidationResult result = registry.ValidateForProductionActivation(modelPath);

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ProductionModelHashMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_生产准入开启时类别输入尺寸任务类型缺失会阻断()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-metadata");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-metadata",
                    Version = "1",
                    ModelHash = ComputeSha256(modelPath),
                    Approval = new ModelApprovalMetadata { Status = ModelApprovalStatuses.Approved }
                }));

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });

            registry.Entries[0].Status.Should().Be(ModelRegistryStatus.Blocked);
            registry.Entries[0].Message.Should().Contain("Labels").And.Contain("input size").And.Contain("task type");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_严格模型包Labels缺失会阻塞Ready()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-c");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1 });
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-c",
                    Version = "1",
                    ModelHash = ComputeSha256(modelPath)
                }));

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                StrictPackageMode = true
            });

            registry.Entries[0].Status.Should().Be(ModelRegistryStatus.Blocked);
            registry.Entries[0].Message.Should().Contain("Labels");
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

    private static void WriteManifest(string packageDir, string modelId, string modelPath)
    {
        File.WriteAllText(
            Path.Combine(packageDir, "manifest.json"),
            JsonSerializer.Serialize(new ModelPackageManifest
            {
                ModelId = modelId,
                Version = "1",
                ModelHash = ComputeSha256(modelPath),
                Labels = new List<string> { "screw" }
            }));
    }

    private static void WriteApprovedManifest(
        string packageDir,
        string modelId,
        string modelPath,
        string modelFileName = "model.onnx")
    {
        File.WriteAllText(
            Path.Combine(packageDir, "manifest.json"),
            JsonSerializer.Serialize(new ModelPackageManifest
            {
                ModelId = modelId,
                Version = "1",
                ModelFileName = modelFileName,
                ModelHash = ComputeSha256(modelPath),
                Labels = new List<string> { "screw" },
                TaskType = "Detect",
                InputWidth = 640,
                InputHeight = 640,
                Approval = new ModelApprovalMetadata
                {
                    Status = ModelApprovalStatuses.Approved,
                    ApprovedAt = DateTimeOffset.Now,
                    ApprovedBy = "qa"
                }
            }));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(ModelRegistryTests), Guid.NewGuid().ToString("N"));
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
