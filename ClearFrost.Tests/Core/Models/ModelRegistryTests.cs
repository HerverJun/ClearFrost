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
    public void Scan_模型包和裸Onnx链接路径会阻断()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            Directory.CreateDirectory(packageRoot);
            string externalRoot = Path.Combine(tempDir, "external");
            Directory.CreateDirectory(externalRoot);

            string externalPackage = Path.Combine(externalRoot, "external-package");
            Directory.CreateDirectory(externalPackage);
            string externalPackageModel = Path.Combine(externalPackage, "model.onnx");
            File.WriteAllBytes(externalPackageModel, new byte[] { 3, 3, 3 });
            WriteApprovedManifest(externalPackage, "external-package", externalPackageModel);

            string linkedPackage = Path.Combine(packageRoot, "linked-package");
            bool packageLinkCreated = TryCreateDirectorySymbolicLink(linkedPackage, externalPackage);
            if (packageLinkCreated)
            {
                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions
                {
                    PackageDirectory = packageRoot,
                    RequireProductionApproval = true,
                    Warmup = (_, _) => true
                });

                registry.Entries.Should().Contain(entry =>
                    entry.ModelId == "linked-package" &&
                    entry.Status == ModelRegistryStatus.Blocked &&
                    entry.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase) &&
                    !entry.ApprovedForProduction);
            }

            string manifestLinkedPackage = Path.Combine(packageRoot, "manifest-linked-package");
            Directory.CreateDirectory(manifestLinkedPackage);
            string manifestLinkedModel = Path.Combine(manifestLinkedPackage, "model.onnx");
            File.WriteAllBytes(manifestLinkedModel, new byte[] { 4, 4, 4 });
            string externalManifest = Path.Combine(externalRoot, "external-manifest.json");
            File.WriteAllText(
                externalManifest,
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "manifest-linked-package",
                    Version = "1",
                    ModelHash = ComputeSha256(manifestLinkedModel),
                    Labels = new List<string> { "screw" },
                    TaskType = "Detect",
                    InputWidth = 640,
                    InputHeight = 640,
                    Approval = new ModelApprovalMetadata { Status = ModelApprovalStatuses.Approved }
                }));
            bool manifestLinkCreated = TryCreateFileSymbolicLink(
                Path.Combine(manifestLinkedPackage, "manifest.json"),
                externalManifest);
            if (manifestLinkCreated)
            {
                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions
                {
                    PackageDirectory = packageRoot,
                    RequireProductionApproval = true,
                    Warmup = (_, _) => true
                });

                registry.Entries.Should().Contain(entry =>
                    entry.ModelId == "manifest-linked-package" &&
                    entry.Status == ModelRegistryStatus.Blocked &&
                    entry.Message.Contains("Manifest file is a reparse point", StringComparison.OrdinalIgnoreCase) &&
                    !entry.ApprovedForProduction);
            }

            string modelLinkedPackage = Path.Combine(packageRoot, "model-linked-package");
            Directory.CreateDirectory(modelLinkedPackage);
            string externalModel = Path.Combine(externalRoot, "external-model.onnx");
            File.WriteAllBytes(externalModel, new byte[] { 5, 5, 5 });
            bool modelLinkCreated = TryCreateFileSymbolicLink(
                Path.Combine(modelLinkedPackage, "model.onnx"),
                externalModel);
            if (modelLinkCreated)
            {
                WriteApprovedManifest(modelLinkedPackage, "model-linked-package", externalModel);

                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions
                {
                    PackageDirectory = packageRoot,
                    RequireProductionApproval = true,
                    Warmup = (_, _) => true
                });

                registry.Entries.Should().Contain(entry =>
                    entry.ModelId == "model-linked-package" &&
                    entry.Status == ModelRegistryStatus.Blocked &&
                    entry.Message.Contains("Model file is a reparse point", StringComparison.OrdinalIgnoreCase) &&
                    entry.ApprovalStatus == ModelApprovalStatuses.Approved &&
                    !entry.ApprovedForProduction);
            }

            string linkedSubdirectoryPackage = Path.Combine(packageRoot, "linked-subdir-package");
            Directory.CreateDirectory(linkedSubdirectoryPackage);
            string linkedSubdirectoryTarget = Path.Combine(externalRoot, "linked-subdir-target");
            Directory.CreateDirectory(linkedSubdirectoryTarget);
            string linkedSubdirectoryModel = Path.Combine(linkedSubdirectoryTarget, "model.onnx");
            File.WriteAllBytes(linkedSubdirectoryModel, new byte[] { 8, 8, 8 });
            bool subdirectoryLinkCreated = TryCreateDirectorySymbolicLink(
                Path.Combine(linkedSubdirectoryPackage, "weights"),
                linkedSubdirectoryTarget);
            if (subdirectoryLinkCreated)
            {
                WriteApprovedManifest(
                    linkedSubdirectoryPackage,
                    "linked-subdir-package",
                    linkedSubdirectoryModel,
                    Path.Combine("weights", "model.onnx"));

                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions
                {
                    PackageDirectory = packageRoot,
                    RequireProductionApproval = true,
                    Warmup = (_, _) => true
                });

                registry.Entries.Should().Contain(entry =>
                    entry.ModelId == "linked-subdir-package" &&
                    entry.Status == ModelRegistryStatus.Blocked &&
                    entry.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase) &&
                    entry.ApprovalStatus == ModelApprovalStatuses.Approved &&
                    !entry.ApprovedForProduction);
            }

            string onnxRoot = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(onnxRoot);
            string externalBareOnnx = Path.Combine(externalRoot, "external-bare.onnx");
            File.WriteAllBytes(externalBareOnnx, new byte[] { 6, 6, 6 });
            bool bareLinkCreated = TryCreateFileSymbolicLink(
                Path.Combine(onnxRoot, "linked-bare.onnx"),
                externalBareOnnx);
            if (bareLinkCreated)
            {
                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions { OnnxDirectory = onnxRoot });

                registry.Entries.Should().Contain(entry =>
                    entry.ModelId == "linked-bare" &&
                    entry.Status == ModelRegistryStatus.Blocked &&
                    entry.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_拒绝链接父目录下的模型根()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalRoot = Path.Combine(tempDir, "external-root");
            string externalPackageRoot = Path.Combine(externalRoot, "models");
            string externalPackage = Path.Combine(externalPackageRoot, "external-package");
            Directory.CreateDirectory(externalPackage);
            string externalPackageModel = Path.Combine(externalPackage, "model.onnx");
            File.WriteAllBytes(externalPackageModel, new byte[] { 1, 9, 9 });
            WriteApprovedManifest(externalPackage, "external-package", externalPackageModel);

            string externalOnnxRoot = Path.Combine(externalRoot, "ONNX");
            Directory.CreateDirectory(externalOnnxRoot);
            File.WriteAllBytes(Path.Combine(externalOnnxRoot, "external-bare.onnx"), new byte[] { 2, 9, 9 });

            string linkedParent = Path.Combine(tempDir, "linked-parent");
            if (!TryCreateDirectorySymbolicLink(linkedParent, externalRoot))
            {
                return;
            }

            var packageRegistry = new ModelRegistry();
            packageRegistry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = Path.Combine(linkedParent, "models"),
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });

            packageRegistry.Entries.Should().ContainSingle(entry =>
                entry.Status == ModelRegistryStatus.Blocked &&
                entry.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase) &&
                !entry.ApprovedForProduction);

            var bareRegistry = new ModelRegistry();
            bareRegistry.Scan(new ModelRegistryScanOptions
            {
                OnnxDirectory = Path.Combine(linkedParent, "ONNX")
            });

            bareRegistry.Entries.Should().ContainSingle(entry =>
                entry.Status == ModelRegistryStatus.Blocked &&
                entry.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase) &&
                !entry.ApprovedForProduction);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ValidateForProductionActivation_扫描后模型文件被替换为链接会阻断()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-link-after-scan");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 7, 7, 7 });
            WriteApprovedManifest(packageDir, "pkg-link-after-scan", modelPath);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            registry.Entries.Should().ContainSingle(entry => entry.Status == ModelRegistryStatus.Ready);

            string externalModel = Path.Combine(tempDir, "external-model.onnx");
            File.Copy(modelPath, externalModel);
            File.Delete(modelPath);
            if (!TryCreateFileSymbolicLink(modelPath, externalModel))
            {
                return;
            }

            ModelProductionValidationResult result = registry.ValidateForProductionActivation(modelPath);

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ProductionModelPathUnsafe");
            File.Exists(externalModel).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_模型包ModelFileName逃逸包目录会阻断()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string externalRoot = Path.Combine(tempDir, "external");
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(externalRoot);

            string escapedModel = Path.Combine(externalRoot, "escaped.onnx");
            string absoluteModel = Path.Combine(externalRoot, "absolute.onnx");
            File.WriteAllBytes(escapedModel, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(absoluteModel, new byte[] { 4, 5, 6 });

            string escapedPackage = Path.Combine(packageRoot, "pkg-escaped");
            Directory.CreateDirectory(escapedPackage);
            WriteApprovedManifest(
                escapedPackage,
                "pkg-escaped",
                escapedModel,
                Path.Combine("..", "..", "external", "escaped.onnx"));

            string absolutePackage = Path.Combine(packageRoot, "pkg-absolute");
            Directory.CreateDirectory(absolutePackage);
            WriteApprovedManifest(
                absolutePackage,
                "pkg-absolute",
                absoluteModel,
                absoluteModel);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });

            registry.Entries.Should().Contain(entry =>
                entry.ModelId == "pkg-escaped" &&
                entry.Status == ModelRegistryStatus.Blocked &&
                entry.Message.Contains("Model file path", StringComparison.OrdinalIgnoreCase) &&
                entry.ApprovalStatus == ModelApprovalStatuses.Approved &&
                !entry.ApprovedForProduction);
            registry.Entries.Should().Contain(entry =>
                entry.ModelId == "pkg-absolute" &&
                entry.Status == ModelRegistryStatus.Blocked &&
                entry.Message.Contains("relative to package", StringComparison.OrdinalIgnoreCase) &&
                entry.ApprovalStatus == ModelApprovalStatuses.Approved &&
                !entry.ApprovedForProduction);
            registry.GetProductionSelectionOptions(requireProductionApproval: true).Should().BeEmpty();
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
    public void ResolveReference_批准模型选择重启后按同一身份解析()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-stable");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "stable.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3, 4 });
            WriteApprovedManifest(packageDir, "pkg-stable", modelPath, "stable.onnx");

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(registry.Entries[0]);

            var reloadedRegistry = new ModelRegistry();
            reloadedRegistry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            ProductionModelResolutionResult resolved = reloadedRegistry.ResolveReference(reference, requireProductionApproval: true);

            resolved.Succeeded.Should().BeTrue();
            resolved.Reference.IdentityEquals(reference).Should().BeTrue();
            resolved.ModelPath.Should().Be(Path.GetFullPath(modelPath));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ResolveReference_重复批准身份会FailClosed()
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
            byte[] bytes = new byte[] { 7, 7, 7 };
            File.WriteAllBytes(modelA, bytes);
            File.WriteAllBytes(modelB, bytes);
            WriteApprovedManifest(packageA, "same-id", modelA);
            WriteApprovedManifest(packageB, "same-id", modelB);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(registry.Entries[0]);

            ProductionModelResolutionResult resolved = registry.ResolveReference(reference, requireProductionApproval: true);

            resolved.Succeeded.Should().BeFalse();
            resolved.ErrorCode.Should().Be("ApprovedModelIdentityDuplicate");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ResolveReference_批准模型Hash变化会FailClosed()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-hash");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
            WriteApprovedManifest(packageDir, "pkg-hash", modelPath);

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(registry.Entries[0]);
            File.WriteAllBytes(modelPath, new byte[] { 9, 9, 9 });

            ProductionModelResolutionResult resolved = registry.ResolveReference(reference, requireProductionApproval: true);

            resolved.Succeeded.Should().BeFalse();
            resolved.ErrorCode.Should().Be("ApprovedModelHashMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void MigrateLegacyReference_准入开启时旧文件名匹配多个批准包会阻止生产()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageA = Path.Combine(packageRoot, "pkg-a");
            string packageB = Path.Combine(packageRoot, "pkg-b");
            Directory.CreateDirectory(packageA);
            Directory.CreateDirectory(packageB);
            string modelA = Path.Combine(packageA, "same.onnx");
            string modelB = Path.Combine(packageB, "same.onnx");
            File.WriteAllBytes(modelA, new byte[] { 1 });
            File.WriteAllBytes(modelB, new byte[] { 2 });
            WriteApprovedManifest(packageA, "pkg-a", modelA, "same.onnx");
            WriteApprovedManifest(packageB, "pkg-b", modelB, "same.onnx");

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });

            ProductionModelResolutionResult migrated = registry.MigrateLegacyReference("same.onnx", requireProductionApproval: true);

            migrated.Succeeded.Should().BeFalse();
            migrated.ErrorCode.Should().Be("LegacyModelApprovedMappingAmbiguous");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ResolveReference_准入关闭时LegacyOnnx兼容裸模型()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string onnxDir = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(onnxDir);
            string modelPath = Path.Combine(onnxDir, "legacy.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 5, 6, 7 });

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions { OnnxDirectory = onnxDir });
            ProductionModelReference reference = ProductionModelReference.FromLegacyOnnx("legacy.onnx", ComputeSha256(modelPath));

            ProductionModelResolutionResult resolved = registry.ResolveReference(reference, requireProductionApproval: false);

            resolved.Succeeded.Should().BeTrue();
            resolved.ModelPath.Should().Be(Path.GetFullPath(modelPath));
            resolved.Reference.Type.Should().Be(ProductionModelReferenceType.LegacyOnnx);
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
            registry.Entries[0].ApprovalStatus.Should().Be(ModelApprovalStatuses.Approved);
            registry.Entries[0].ApprovedForProduction.Should().BeFalse();
            registry.IsApprovedForProduction("pkg-metadata").Should().BeFalse();
            registry.ValidateForProductionActivation(modelPath).ErrorCode.Should().Be("ProductionModelRegistryBlocked");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Scan_Manifest批准但RegistryWarning时不视为生产批准()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-hash-warning");
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-hash-warning",
                    Version = "1",
                    ModelHash = "bad-hash",
                    Labels = new List<string> { "screw" },
                    TaskType = "Detect",
                    InputWidth = 640,
                    InputHeight = 640,
                    Approval = new ModelApprovalMetadata { Status = ModelApprovalStatuses.Approved }
                }));

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                RequireProductionApproval = true,
                Warmup = (_, _) => true
            });

            registry.Entries.Should().ContainSingle();
            registry.Entries[0].Status.Should().Be(ModelRegistryStatus.Warning);
            registry.Entries[0].ApprovalStatus.Should().Be(ModelApprovalStatuses.Approved);
            registry.Entries[0].ApprovedForProduction.Should().BeFalse();
            registry.IsApprovedForProduction("pkg-hash-warning").Should().BeFalse();
            registry.ValidateForProductionActivation(modelPath).ErrorCode.Should().Be("ProductionModelRegistryBlocked");
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
