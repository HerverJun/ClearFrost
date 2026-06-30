using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Core.Models;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class ModelAcceptanceServiceTests
{
    [Fact]
    public void Scan_要求生产批准时未批准模型包被阻塞()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = CreatePackage(packageRoot, "pkg-pending", approved: false);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });

            registry.Entries.Should().ContainSingle();
            registry.Entries[0].Status.Should().Be(ModelRegistryStatus.Blocked);
            registry.Entries[0].ApprovedForProduction.Should().BeFalse();
            registry.Entries[0].Message.Should().Contain("approved");
            File.Exists(Path.Combine(packageDir, "manifest.json")).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ApprovePackage_通过验收后启用只做校验不写第二生产状态()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            CreatePackage(packageRoot, "pkg-a", approved: true);
            CreatePackage(packageRoot, "pkg-b", approved: false);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                Warmup = (_, _) => true
            });

            ModelRegistryEntry first = registry.Resolve("pkg-a")!;
            ModelRegistryEntry second = registry.Resolve("pkg-b")!;
            var service = new ModelAcceptanceService(Path.Combine(tempDir, "state.json"));

            service.EnableApprovedModel(first).Succeeded.Should().BeTrue();
            ModelAcceptanceResult approval = service.ApprovePackage(second, new ModelAcceptanceRequest
            {
                OperatorId = "qa01",
                GoldenDatasetPath = Path.Combine(tempDir, "golden"),
                TotalSamples = 100,
                PassedSamples = 99,
                MinimumPassRate = 0.98,
                Summary = "golden replay passed"
            });

            approval.Succeeded.Should().BeTrue();

            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            second = registry.Resolve("pkg-b")!;
            second.ApprovedForProduction.Should().BeTrue();
            second.ApprovalStatus.Should().Be(ModelApprovalStatuses.Approved);

            service.EnableApprovedModel(second).Succeeded.Should().BeTrue();
            File.Exists(Path.Combine(tempDir, "state.json")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void EnableApprovedModel_模型文件缺失时不写生产状态()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = CreatePackage(packageRoot, "pkg-missing", approved: true);
            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            ModelRegistryEntry entry = registry.Resolve("pkg-missing")!;
            File.Delete(Path.Combine(packageDir, "model.onnx"));

            var service = new ModelAcceptanceService(Path.Combine(tempDir, "state.json"));
            ModelAcceptanceResult result = service.EnableApprovedModel(entry);

            result.Succeeded.Should().BeFalse();
            File.Exists(Path.Combine(tempDir, "state.json")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreatePackage(string packageRoot, string modelId, bool approved)
    {
        string packageDir = Path.Combine(packageRoot, modelId);
        Directory.CreateDirectory(packageDir);
        string modelPath = Path.Combine(packageDir, "model.onnx");
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3, (byte)modelId.Length });
        var manifest = new ModelPackageManifest
        {
            ModelId = modelId,
            Version = "1",
            ModelFileName = "model.onnx",
            ModelHash = ComputeSha256(modelPath),
            Labels = new List<string> { "part" },
            TaskType = "Detect",
            InputWidth = 640,
            InputHeight = 640,
            Approval = new ModelApprovalMetadata
            {
                Status = approved ? ModelApprovalStatuses.Approved : ModelApprovalStatuses.Pending,
                ApprovedBy = approved ? "qa" : string.Empty,
                ApprovedAt = approved ? DateTimeOffset.Now : null
            }
        };
        File.WriteAllText(Path.Combine(packageDir, "manifest.json"), JsonSerializer.Serialize(manifest));
        return packageDir;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostModelAcceptanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
