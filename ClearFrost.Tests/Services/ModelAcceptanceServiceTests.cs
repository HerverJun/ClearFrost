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
    public void ApprovePackage_旧验收入口只返回ReplayEvidenceRequired且不写Approved()
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

            approval.Succeeded.Should().BeFalse();
            approval.ErrorCode.Should().Be("ReplayEvidenceRequired");

            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            second = registry.Resolve("pkg-b")!;
            second.ApprovedForProduction.Should().BeFalse();
            second.ApprovalStatus.Should().Be(ModelApprovalStatuses.Pending);

            service.EnableApprovedModel(second).Succeeded.Should().BeFalse();
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

    [Fact]
    public void LoadState_拒绝链接生产状态文件且不加载外部内容()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string statePath = Path.Combine(tempDir, "state.json");
        try
        {
            string externalStatePath = Path.Combine(externalDir, "external-state.json");
            File.WriteAllText(
                externalStatePath,
                JsonSerializer.Serialize(new ModelProductionState
                {
                    CurrentModelId = "external-model",
                    CurrentVersion = "external-v1",
                    CurrentModelPath = Path.Combine(externalDir, "external.onnx")
                }));
            if (!TryCreateFileSymbolicLink(statePath, externalStatePath))
            {
                return;
            }

            var service = new ModelAcceptanceService(statePath);

            ModelProductionState state = service.LoadState();

            state.CurrentModelId.Should().BeEmpty();
            state.CurrentVersion.Should().BeEmpty();
            state.CurrentModelPath.Should().BeEmpty();
            File.ReadAllText(externalStatePath).Should().Contain("external-model");
        }
        finally
        {
            TryDeleteFileLink(statePath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void LoadState_拒绝链接父目录下的生产状态文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedStateDir = Path.Combine(tempDir, "linked-state");
        try
        {
            string externalStatePath = Path.Combine(externalDir, "state.json");
            File.WriteAllText(
                externalStatePath,
                JsonSerializer.Serialize(new ModelProductionState
                {
                    CurrentModelId = "external-parent",
                    CurrentVersion = "external-v2",
                    CurrentModelPath = Path.Combine(externalDir, "external.onnx")
                }));
            if (!TryCreateDirectorySymbolicLink(linkedStateDir, externalDir))
            {
                return;
            }

            var service = new ModelAcceptanceService(Path.Combine(linkedStateDir, "state.json"));

            ModelProductionState state = service.LoadState();

            state.CurrentModelId.Should().BeEmpty();
            state.CurrentVersion.Should().BeEmpty();
            state.CurrentModelPath.Should().BeEmpty();
            File.ReadAllText(externalStatePath).Should().Contain("external-parent");
        }
        finally
        {
            TryDeleteDirectoryLink(linkedStateDir);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void EnableApprovedModel_扫描后模型文件被替换为链接时拒绝()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = CreatePackage(packageRoot, "pkg-linked-after-scan", approved: true);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            ModelRegistryEntry entry = registry.Resolve("pkg-linked-after-scan")!;
            string externalModel = Path.Combine(externalDir, "external-model.onnx");
            File.Copy(modelPath, externalModel);
            File.Delete(modelPath);
            if (!TryCreateFileSymbolicLink(modelPath, externalModel))
            {
                return;
            }

            var service = new ModelAcceptanceService(Path.Combine(tempDir, "state.json"));
            ModelAcceptanceResult result = service.EnableApprovedModel(entry);

            result.Succeeded.Should().BeFalse();
            result.Message.Should().Contain("链接");
            File.Exists(externalModel).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
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

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = File.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
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

    private static void TryDeleteFileLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new FileInfo(linkPath);
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
        if (Directory.Exists(path))
        {
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
}
