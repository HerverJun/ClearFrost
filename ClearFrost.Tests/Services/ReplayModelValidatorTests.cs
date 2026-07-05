using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Core.Models;
using ClearFrost.Services.Replay;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class ReplayModelValidatorTests
{
    [Fact]
    public async Task ValidateAsync_CandidatePending可用于Replay但不可作为已批准生产模型()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayModelIdentity model = CreateIdentity(tempDir, approved: false);
            var validator = new ReplayModelValidator(warmup: (_, _) => true);

            ReplayModelValidationResult replayResult = await validator.ValidateAsync(
                model,
                new ReplayModelValidationOptions { AllowPendingApproval = true });
            ReplayModelValidationResult productionResult = await validator.ValidateAsync(
                model,
                new ReplayModelValidationOptions { AllowPendingApproval = false });

            replayResult.Succeeded.Should().BeTrue();
            productionResult.Succeeded.Should().BeFalse();
            productionResult.ErrorCode.Should().Be("ReplayModelNotApproved");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("labels")]
    [InlineData("task")]
    [InlineData("input")]
    [InlineData("warmup")]
    public async Task ValidateAsync_不放宽生产模型包校验(string brokenPart)
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayModelIdentity model = CreateIdentity(tempDir, approved: true, brokenPart);
            var validator = new ReplayModelValidator(warmup: (_, _) => brokenPart != "warmup");

            ReplayModelValidationResult result = await validator.ValidateAsync(
                model,
                new ReplayModelValidationOptions { AllowPendingApproval = false });

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ValidateAsync_拒绝Manifest模型路径逃逸包目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageDir = Path.Combine(tempDir, "pkg");
            string externalDir = Path.Combine(tempDir, "external");
            Directory.CreateDirectory(packageDir);
            Directory.CreateDirectory(externalDir);
            string externalModel = Path.Combine(externalDir, "model.onnx");
            File.WriteAllBytes(externalModel, new byte[] { 7, 7, 7 });
            string hash = ComputeSha256(externalModel);
            var manifest = new ModelPackageManifest
            {
                ModelId = "pkg",
                Version = "1",
                ModelFileName = Path.Combine("..", "external", "model.onnx"),
                ModelHash = hash,
                Labels = new List<string> { "part" },
                TaskType = "Detect",
                InputWidth = 640,
                InputHeight = 640,
                Approval = new ModelApprovalMetadata { Status = ModelApprovalStatuses.Approved }
            };
            string manifestPath = Path.Combine(packageDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            var model = CreateIdentity("pkg", externalModel, manifestPath, hash);
            var validator = new ReplayModelValidator(warmup: (_, _) => true);

            ReplayModelValidationResult result = await validator.ValidateAsync(
                model,
                new ReplayModelValidationOptions { AllowPendingApproval = false });

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ReplayModelManifestPathInvalid");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ValidateAsync_拒绝Manifest声明模型与实际模型路径不一致()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayModelIdentity model = CreateIdentity(tempDir, approved: true);
            string packageDir = Path.GetDirectoryName(model.ManifestPath)!;
            string otherModel = Path.Combine(packageDir, "other.onnx");
            File.Copy(model.ModelPath, otherModel);
            model.ModelPath = otherModel;
            model.Sha256 = ComputeSha256(otherModel);
            var validator = new ReplayModelValidator(warmup: (_, _) => true);

            ReplayModelValidationResult result = await validator.ValidateAsync(
                model,
                new ReplayModelValidationOptions { AllowPendingApproval = false });

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ReplayModelManifestPathMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ValidateAsync_拒绝链接Manifest和链接模型路径()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayModelIdentity manifestModel = CreateIdentity(Path.Combine(tempDir, "manifest-case"), approved: true);
            string manifestPackageDir = Path.GetDirectoryName(manifestModel.ManifestPath)!;
            string externalManifest = Path.Combine(tempDir, "external-manifest.json");
            File.Copy(manifestModel.ManifestPath, externalManifest);
            File.Delete(manifestModel.ManifestPath);
            bool manifestLinkCreated = TryCreateFileSymbolicLink(manifestModel.ManifestPath, externalManifest);
            if (manifestLinkCreated)
            {
                var validator = new ReplayModelValidator(warmup: (_, _) => true);

                ReplayModelValidationResult result = await validator.ValidateAsync(
                    manifestModel,
                    new ReplayModelValidationOptions { AllowPendingApproval = false });

                result.Succeeded.Should().BeFalse();
                result.ErrorCode.Should().Be("ReplayModelManifestReparsePoint");
            }

            ReplayModelIdentity subdirModel = CreateIdentity(Path.Combine(tempDir, "subdir-case"), approved: true);
            string subdirPackageDir = Path.GetDirectoryName(subdirModel.ManifestPath)!;
            string externalWeights = Path.Combine(tempDir, "external-weights");
            Directory.CreateDirectory(externalWeights);
            string externalModel = Path.Combine(externalWeights, "model.onnx");
            File.WriteAllBytes(externalModel, new byte[] { 8, 8, 8 });
            string linkedWeights = Path.Combine(subdirPackageDir, "weights");
            bool subdirLinkCreated = TryCreateDirectorySymbolicLink(linkedWeights, externalWeights);
            if (subdirLinkCreated)
            {
                string hash = ComputeSha256(externalModel);
                var manifest = new ModelPackageManifest
                {
                    ModelId = "pkg",
                    Version = "1",
                    ModelFileName = Path.Combine("weights", "model.onnx"),
                    ModelHash = hash,
                    Labels = new List<string> { "part" },
                    TaskType = "Detect",
                    InputWidth = 640,
                    InputHeight = 640,
                    Approval = new ModelApprovalMetadata { Status = ModelApprovalStatuses.Approved }
                };
                File.WriteAllText(subdirModel.ManifestPath, JsonSerializer.Serialize(manifest));
                subdirModel.ModelPath = Path.Combine(linkedWeights, "model.onnx");
                subdirModel.Sha256 = hash;
                var validator = new ReplayModelValidator(warmup: (_, _) => true);

                ReplayModelValidationResult result = await validator.ValidateAsync(
                    subdirModel,
                    new ReplayModelValidationOptions { AllowPendingApproval = false });

                result.Succeeded.Should().BeFalse();
                result.ErrorCode.Should().Be("ReplayModelPathReparsePoint");
            }
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ValidateAsync_拒绝链接父目录下的模型包()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalRoot = Path.Combine(tempDir, "external-root");
            ReplayModelIdentity externalModel = CreateIdentity(externalRoot, approved: true);
            string linkedRoot = Path.Combine(tempDir, "linked-root");
            if (!TryCreateDirectorySymbolicLink(linkedRoot, externalRoot))
            {
                return;
            }

            var linkedModel = new ReplayModelIdentity
            {
                ModelId = externalModel.ModelId,
                Version = externalModel.Version,
                Sha256 = externalModel.Sha256,
                ModelPath = Path.Combine(linkedRoot, "pkg", "model.onnx"),
                ManifestPath = Path.Combine(linkedRoot, "pkg", "manifest.json"),
                Labels = externalModel.Labels,
                TaskType = externalModel.TaskType,
                InputWidth = externalModel.InputWidth,
                InputHeight = externalModel.InputHeight,
                ApprovalStatus = externalModel.ApprovalStatus,
                IsPackage = true
            };
            var validator = new ReplayModelValidator(warmup: (_, _) => true);

            ReplayModelValidationResult result = await validator.ValidateAsync(
                linkedModel,
                new ReplayModelValidationOptions { AllowPendingApproval = false });

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ReplayModelManifestReparsePoint");
            File.Exists(externalModel.ModelPath).Should().BeTrue();
            File.Exists(externalModel.ManifestPath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static ReplayModelIdentity CreateIdentity(string root, bool approved, string brokenPart = "")
    {
        string packageDir = Path.Combine(root, "pkg");
        Directory.CreateDirectory(packageDir);
        string modelPath = Path.Combine(packageDir, "model.onnx");
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3, 4 });
        string actualHash = ComputeSha256(modelPath);
        string manifestHash = brokenPart == "hash" ? new string('0', 64) : actualHash;
        var manifest = new ModelPackageManifest
        {
            ModelId = "pkg",
            Version = "1",
            ModelFileName = "model.onnx",
            ModelHash = manifestHash,
            Labels = brokenPart == "labels" ? new List<string>() : new List<string> { "part" },
            TaskType = brokenPart == "task" ? "" : "Detect",
            InputWidth = brokenPart == "input" ? 0 : 640,
            InputHeight = brokenPart == "input" ? 0 : 640,
            Approval = new ModelApprovalMetadata
            {
                Status = approved ? ModelApprovalStatuses.Approved : ModelApprovalStatuses.Pending,
                ApprovedBy = approved ? "qa" : string.Empty,
                ApprovedAt = approved ? DateTimeOffset.UtcNow : null
            }
        };
        string manifestPath = Path.Combine(packageDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        return new ReplayModelIdentity
        {
            ModelId = "pkg",
            Version = "1",
            Sha256 = actualHash,
            ModelPath = modelPath,
            ManifestPath = manifestPath,
            Labels = manifest.Labels,
            TaskType = manifest.TaskType,
            InputWidth = manifest.InputWidth,
            InputHeight = manifest.InputHeight,
            ApprovalStatus = manifest.Approval.Status,
            IsPackage = true
        };
    }

    private static ReplayModelIdentity CreateIdentity(
        string modelId,
        string modelPath,
        string manifestPath,
        string sha256)
    {
        return new ReplayModelIdentity
        {
            ModelId = modelId,
            Version = "1",
            Sha256 = sha256,
            ModelPath = modelPath,
            ManifestPath = manifestPath,
            Labels = new List<string> { "part" },
            TaskType = "Detect",
            InputWidth = 640,
            InputHeight = 640,
            ApprovalStatus = ModelApprovalStatuses.Approved,
            IsPackage = true
        };
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(ReplayModelValidatorTests), Guid.NewGuid().ToString("N"));
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
}
