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
}
