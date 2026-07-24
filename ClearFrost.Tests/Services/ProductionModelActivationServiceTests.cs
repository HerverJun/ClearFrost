using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

#pragma warning disable CS0067
public class ProductionModelActivationServiceTests
{
    [Fact]
    public void GetSelectionOptions_运行中新加入Onnx会被刷新发现()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string onnxDir = Path.Combine(tempDir, "onnx");
            Directory.CreateDirectory(onnxDir);
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = false
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                Path.Combine(tempDir, "packages"),
                refreshRegistry: () => registry.Scan(new ModelRegistryScanOptions
                {
                    OnnxDirectory = onnxDir,
                    RequireProductionApproval = false
                }));

            service.GetSelectionOptions().Should().BeEmpty();

            string modelPath = Path.Combine(onnxDir, "runtime-new.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 3, 1, 4, 1, 5, 9 });
            string expectedHash = ComputeSha256(modelPath);

            IReadOnlyList<ProductionModelSelectionOption> options = service.GetSelectionOptions();

            options.Should().ContainSingle(option =>
                option.FileName == "runtime-new.onnx" &&
                option.ModelId == "runtime-new" &&
                option.Version == "legacy" &&
                option.Sha256 == expectedHash &&
                !option.IsApprovedPackage);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_批准模型选择会保存引用和Recipe快照()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string modelPath = CreatePackage(packageRoot, "pkg-main", "1", "main.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(registry.Entries[0]);

            ProductionModelActivationResult result = await service.ActivatePrimaryAsync(
                reference.ToSelectionValue(),
                "主模型切换",
                useGpu: false,
                gpuIndex: 0);

            result.Succeeded.Should().BeTrue();
            config.CurrentModelReference.IdentityEquals(reference).Should().BeTrue();
            config.CurrentModelFileName.Should().Be("main.onnx");
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(reference).Should().BeTrue();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(modelPath));
            service.EnsureReadyForProduction().Succeeded.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_审批开启但Gate缺失时激活和Ready都拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string modelPath = CreatePackage(packageRoot, "pkg-main", "1", "main.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                packageRoot,
                omitApprovalEvidenceValidator: true);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(registry.Resolve(modelPath)!);

            ProductionModelActivationResult activation = await service.ActivatePrimaryAsync(
                reference.ToSelectionValue(),
                "missing gate",
                useGpu: false,
                gpuIndex: 0);

            activation.Succeeded.Should().BeFalse();
            activation.ErrorCode.Should().Be("ReplayEvidenceGateMissing");
            detection.RuntimeModelSnapshot.Primary.IsLoaded.Should().BeFalse();

            config.CurrentModelReference = reference;
            config.CurrentModelFileName = Path.GetFileName(modelPath);
            (await detection.LoadModelAsync(modelPath, false, 0)).Should().BeTrue();
            ProductionModelReadinessResult ready = service.EnsureReadyForProduction();
            ready.Succeeded.Should().BeFalse();
            ready.ErrorCode.Should().Be("ReplayEvidenceGateMissing");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_LoadModel失败不改变旧运行时配置或Recipe()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "初始模型", false, 0)).Succeeded.Should().BeTrue();

            detection.FailLoadPaths.Add(Path.GetFullPath(newPath));
            ProductionModelActivationResult result = await service.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "主模型切换",
                false,
                0);

            result.Succeeded.Should().BeFalse();
            config.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(oldPath));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_配置提交失败会恢复旧Config和旧运行时()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "初始模型", false, 0)).Succeeded.Should().BeTrue();

            int saveCalls = 0;
            ProductionModelActivationService failingSaveService = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                packageRoot,
                saveConfig: () => ++saveCalls != 1);

            ProductionModelActivationResult result = await failingSaveService.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "主模型切换",
                false,
                0);

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeFalse();
            config.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(oldPath));
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_补偿失败后进入Faulted并阻断生产()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "初始模型", false, 0)).Succeeded.Should().BeTrue();

            ProductionModelActivationService failingSaveService = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                packageRoot,
                saveConfig: () => false);

            ProductionModelActivationResult result = await failingSaveService.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "主模型切换",
                false,
                0);

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeTrue();
            result.CompensationFailures.Should().NotBeEmpty();
            failingSaveService.EnsureReadyForProduction().ErrorCode.Should().Be("ModelActivationFaulted");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LoadConfiguredModelsAsync_ResolveAux1Failure_DoesNotMutateLiveState()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string runtimePath = CreatePackage(packageRoot, "pkg-runtime", "1", "runtime.onnx");
            CreatePackage(packageRoot, "pkg-main", "1", "main.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true,
                CurrentModelFileName = "main.onnx",
                Auxiliary1ModelPath = "missing.onnx"
            };
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(packageRoot));
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            Recipe recipeSnapshot = recipeManager.CaptureCurrentSnapshot();
            var detection = new FakeDetectionService();
            detection.SetRuntime(primary: runtimePath);
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            string configJson = config.ToPortableJson();

            ProductionModelActivationResult result = await service.LoadConfiguredModelsAsync("startup", false, 0);

            result.Succeeded.Should().BeFalse();
            config.ToPortableJson().Should().Be(configJson);
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(recipeSnapshot.CurrentModelReference).Should().BeTrue();
            recipeManager.CurrentRecipe.Auxiliary1ModelReference.IdentityEquals(recipeSnapshot.Auxiliary1ModelReference).Should().BeTrue();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(runtimePath));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LoadConfiguredModelsAsync_OldPrimaryEmpty_AuxFailureUnloadsNewPrimary()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string mainPath = CreatePackage(packageRoot, "pkg-main", "1", "main.onnx");
            string aux1Path = CreatePackage(packageRoot, "pkg-aux1", "1", "aux1.onnx");
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(packageRoot));
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true,
                CurrentModelReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(mainPath)!),
                Auxiliary1ModelReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(aux1Path)!)
            };
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            detection.FailLoadPaths.Add(Path.GetFullPath(aux1Path));
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);

            ProductionModelActivationResult result = await service.LoadConfiguredModelsAsync("startup", false, 0);

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeFalse();
            detection.RuntimeModelSnapshot.Primary.IsLoaded.Should().BeFalse();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().BeEmpty();
            config.CurrentModelReference.IdentityEquals(ProductionModelReference.FromApprovedPackage(registry.Resolve(mainPath)!)).Should().BeTrue();
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(config.CurrentModelReference).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LoadConfiguredModelsAsync_LaterSlotFailureRestoresExactOldRuntimePaths()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPrimary = CreatePackage(packageRoot, "old-main", "1", "old-main.onnx");
            string oldAux1 = CreatePackage(packageRoot, "old-aux1", "1", "old-aux1.onnx");
            string oldAux2 = CreatePackage(packageRoot, "old-aux2", "1", "old-aux2.onnx");
            string newPrimary = CreatePackage(packageRoot, "new-main", "1", "new-main.onnx");
            string newAux1 = CreatePackage(packageRoot, "new-aux1", "1", "new-aux1.onnx");
            string newAux2 = CreatePackage(packageRoot, "new-aux2", "1", "new-aux2.onnx");
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(packageRoot));
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true,
                CurrentModelReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPrimary)!),
                Auxiliary1ModelReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newAux1)!),
                Auxiliary2ModelReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newAux2)!)
            };
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            detection.SetRuntime(oldPrimary, oldAux1, oldAux2);
            detection.FailLoadPaths.Add(Path.GetFullPath(newAux1));
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);

            ProductionModelActivationResult result = await service.LoadConfiguredModelsAsync("startup", false, 0);

            result.Succeeded.Should().BeFalse();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(oldPrimary));
            detection.RuntimeModelSnapshot.Auxiliary1.ModelPath.Should().Be(Path.GetFullPath(oldAux1));
            detection.RuntimeModelSnapshot.Auxiliary2.ModelPath.Should().Be(Path.GetFullPath(oldAux2));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_RecipeCommitFailureRestoresRuntimeConfigAndRecipe()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string recipePath = Path.Combine(tempDir, "recipe.json");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            int failingRecipeWrites = 0;
            var recipeManager = new RecipeManager(recipePath, (path, content) =>
            {
                if (failingRecipeWrites > 0 &&
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(recipePath), StringComparison.OrdinalIgnoreCase))
                {
                    failingRecipeWrites--;
                    throw new IOException("recipe commit failed");
                }

                File.WriteAllText(path, content);
            });
            recipeManager.LoadOrCreateDefault(config);
            var registry = new ModelRegistry();
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();
            Recipe oldRecipe = recipeManager.CaptureCurrentSnapshot();

            failingRecipeWrites = 1;
            ProductionModelActivationResult result = await service.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "switch",
                false,
                0);

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeFalse();
            config.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(oldPath));
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(oldRecipe.CurrentModelReference).Should().BeTrue();
            ReadRecipe(recipePath).CurrentModelReference.IdentityEquals(oldRecipe.CurrentModelReference).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task FaultedState_InvalidRecoveryAttemptDoesNotClearFault()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            bool allowSave = true;
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                packageRoot,
                saveConfig: () => allowSave);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            allowSave = false;
            (await service.ActivatePrimaryAsync(newReference.ToSelectionValue(), "fault", false, 0)).IsFaulted.Should().BeTrue();

            ProductionModelActivationResult invalid = await service.ActivatePrimaryAsync("missing.onnx", "invalid", false, 0);

            invalid.Succeeded.Should().BeFalse();
            invalid.IsFaulted.Should().BeTrue();
            service.EnsureReadyForProduction().ErrorCode.Should().Be("ModelActivationFaulted");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task FaultedState_CompleteRecoverySuccessClearsFault()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            bool allowSave = true;
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                packageRoot,
                saveConfig: () => allowSave);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            allowSave = false;
            (await service.ActivatePrimaryAsync(newReference.ToSelectionValue(), "fault", false, 0)).IsFaulted.Should().BeTrue();
            service.EnsureReadyForProduction().ErrorCode.Should().Be("ModelActivationFaulted");

            allowSave = true;
            ProductionModelActivationResult recovery = await service.ActivatePrimaryAsync(newReference.ToSelectionValue(), "recover", false, 0);

            recovery.Succeeded.Should().BeTrue();
            service.EnsureReadyForProduction().Succeeded.Should().BeTrue();
            service.IsFaulted.Should().BeFalse();
            config.CurrentModelReference.IdentityEquals(newReference).Should().BeTrue();
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(newReference).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_轻量模式LegacyOnnx缺少EvidenceGate仍可上线()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string onnxDir = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(onnxDir);
            string modelPath = Path.Combine(onnxDir, "legacy-field.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 8, 6, 7, 5, 3, 0, 9 });
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = false
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                Path.Combine(tempDir, "packages"),
                refreshRegistry: () => registry.Scan(new ModelRegistryScanOptions
                {
                    OnnxDirectory = onnxDir,
                    RequireProductionApproval = false
                }),
                omitApprovalEvidenceValidator: true);

            IReadOnlyList<ProductionModelSelectionOption> options = service.GetSelectionOptions();
            options.Should().ContainSingle();
            ProductionModelSelectionOption option = options[0];

            ProductionModelActivationResult activation = await service.ActivatePrimaryAsync(
                option.Value,
                "field lightweight",
                useGpu: false,
                gpuIndex: 0);

            activation.Succeeded.Should().BeTrue();
            activation.ErrorCode.Should().BeEmpty();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(modelPath));
            service.EnsureReadyForProduction().Succeeded.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_严格模式LegacyOnnx仍被拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string onnxDir = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(onnxDir);
            string modelPath = Path.Combine(onnxDir, "legacy-strict.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 1, 2, 3, 5, 8 });
            string modelHash = ComputeSha256(modelPath);
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                Path.Combine(tempDir, "packages"),
                refreshRegistry: () => registry.Scan(new ModelRegistryScanOptions
                {
                    OnnxDirectory = onnxDir,
                    RequireProductionApproval = true
                }));
            service.GetSelectionOptions().Should().BeEmpty();
            ProductionModelReference legacyReference = ProductionModelReference.FromLegacyOnnx("legacy-strict.onnx", modelHash);

            ProductionModelActivationResult activation = await service.ActivatePrimaryAsync(
                legacyReference.ToSelectionValue(),
                "strict legacy",
                useGpu: false,
                gpuIndex: 0);

            activation.Succeeded.Should().BeFalse();
            activation.ErrorCode.Should().Be("LegacyModelNotAllowed");
            detection.RuntimeModelSnapshot.Primary.IsLoaded.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnsureReadyForProduction_持久化配方为链接文件时阻断生产()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string modelPath = CreatePackage(packageRoot, "pkg-main", "1", "main.onnx");
            string recipePath = Path.Combine(tempDir, "recipe.json");
            string externalRecipePath = Path.Combine(tempDir, "external-recipe.json");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(recipePath);
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(registry.Resolve(modelPath)!);
            (await service.ActivatePrimaryAsync(reference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            string trustedRecipeJson = File.ReadAllText(recipePath);
            File.WriteAllText(externalRecipePath, trustedRecipeJson);
            File.Delete(recipePath);
            if (!TryCreateFileSymbolicLink(recipePath, externalRecipePath))
            {
                File.WriteAllText(recipePath, trustedRecipeJson);
                return;
            }

            ProductionModelReadinessResult result = service.EnsureReadyForProduction();

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("RecipePersistenceUnavailable");
            result.Message.Should().Contain("linked");
            File.ReadAllText(externalRecipePath).Should().Be(trustedRecipeJson);
        }
        finally
        {
            TryDeleteFileLink(Path.Combine(tempDir, "recipe.json"));
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task EnsureReadyForProduction_持久化配方父目录为链接时阻断生产()
    {
        string tempDir = CreateTempDirectory();
        string? linkedRecipeDir = null;
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string modelPath = CreatePackage(packageRoot, "pkg-main", "1", "main.onnx");
            string recipeDir = Path.Combine(tempDir, "recipes");
            linkedRecipeDir = recipeDir;
            string recipePath = Path.Combine(recipeDir, "recipe.json");
            string externalRecipeDir = Path.Combine(tempDir, "external-recipes");
            string externalRecipePath = Path.Combine(externalRecipeDir, "recipe.json");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(recipePath);
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(registry.Resolve(modelPath)!);
            (await service.ActivatePrimaryAsync(reference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            string trustedRecipeJson = File.ReadAllText(recipePath);
            Directory.CreateDirectory(externalRecipeDir);
            File.WriteAllText(externalRecipePath, trustedRecipeJson);
            Directory.Delete(recipeDir, recursive: true);
            if (!TryCreateDirectorySymbolicLink(recipeDir, externalRecipeDir))
            {
                Directory.CreateDirectory(recipeDir);
                File.WriteAllText(recipePath, trustedRecipeJson);
                return;
            }

            ProductionModelReadinessResult result = service.EnsureReadyForProduction();

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("RecipePersistenceUnavailable");
            result.Message.Should().Contain("linked path segments");
            File.ReadAllText(externalRecipePath).Should().Be(trustedRecipeJson);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkedRecipeDir))
            {
                TryDeleteDirectoryLink(linkedRecipeDir);
            }

            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ConcurrentActivation_PrimaryAndAuxiliary_RunOneCompleteTransactionAtATime()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            string auxPath = CreatePackage(packageRoot, "pkg-aux", "1", "aux.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            ProductionModelReference auxReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(auxPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            var primaryLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePrimaryLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var auxiliaryLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            detection.BeforeLoadAsync = async (role, path) =>
            {
                if (role == ModelRole.Primary && string.Equals(path, Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                {
                    primaryLoadStarted.TrySetResult();
                    await releasePrimaryLoad.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                if (role == ModelRole.Auxiliary1 && string.Equals(path, Path.GetFullPath(auxPath), StringComparison.OrdinalIgnoreCase))
                {
                    config.CurrentModelReference.IdentityEquals(newReference).Should().BeTrue();
                    recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(newReference).Should().BeTrue();
                    auxiliaryLoadStarted.TrySetResult();
                }
            };

            Task<ProductionModelActivationResult> primaryTask = service.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "A",
                false,
                0);
            await primaryLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task<ProductionModelActivationResult> auxiliaryTask = service.ActivateAuxiliaryAsync(
                1,
                auxReference.ToSelectionValue(),
                "B",
                false,
                0);

            await Task.Delay(100);
            auxiliaryLoadStarted.Task.IsCompleted.Should().BeFalse();

            releasePrimaryLoad.TrySetResult();
            ProductionModelActivationResult primary = await primaryTask.WaitAsync(TimeSpan.FromSeconds(5));
            primary.Succeeded.Should().BeTrue();

            await auxiliaryLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ProductionModelActivationResult auxiliary = await auxiliaryTask.WaitAsync(TimeSpan.FromSeconds(5));
            auxiliary.Succeeded.Should().BeTrue();
            detection.MaxConcurrentLoads.Should().Be(1);
            config.CurrentModelReference.IdentityEquals(newReference).Should().BeTrue();
            config.Auxiliary1ModelReference.IdentityEquals(auxReference).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ConcurrentActivation_FailedTransactionCompensatesBeforeNextTransactionStarts()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            string auxPath = CreatePackage(packageRoot, "pkg-aux", "1", "aux.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService initialService = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            ProductionModelReference auxReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(auxPath)!);
            (await initialService.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            int saveCalls = 0;
            bool compensationCompleted = false;
            bool bCommitObservedCompensation = false;
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                packageRoot,
                saveConfig: () =>
                {
                    int call = Interlocked.Increment(ref saveCalls);
                    if (call == 1)
                    {
                        return false;
                    }

                    if (call == 2)
                    {
                        compensationCompleted = true;
                        return true;
                    }

                    bCommitObservedCompensation = compensationCompleted;
                    return true;
                });

            var primaryLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePrimaryLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var auxiliaryLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            detection.BeforeLoadAsync = async (role, path) =>
            {
                if (role == ModelRole.Primary && string.Equals(path, Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                {
                    primaryLoadStarted.TrySetResult();
                    await releasePrimaryLoad.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                if (role == ModelRole.Auxiliary1 && string.Equals(path, Path.GetFullPath(auxPath), StringComparison.OrdinalIgnoreCase))
                {
                    compensationCompleted.Should().BeTrue();
                    auxiliaryLoadStarted.TrySetResult();
                }
            };

            Task<ProductionModelActivationResult> failedTask = service.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "A-fails",
                false,
                0);
            await primaryLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task<ProductionModelActivationResult> succeedingTask = service.ActivateAuxiliaryAsync(
                1,
                auxReference.ToSelectionValue(),
                "B-succeeds",
                false,
                0);

            await Task.Delay(100);
            auxiliaryLoadStarted.Task.IsCompleted.Should().BeFalse();

            releasePrimaryLoad.TrySetResult();
            ProductionModelActivationResult failed = await failedTask.WaitAsync(TimeSpan.FromSeconds(5));
            failed.Succeeded.Should().BeFalse();
            failed.IsFaulted.Should().BeFalse();

            await auxiliaryLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ProductionModelActivationResult succeeded = await succeedingTask.WaitAsync(TimeSpan.FromSeconds(5));
            succeeded.Succeeded.Should().BeTrue();
            bCommitObservedCompensation.Should().BeTrue();
            config.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            config.Auxiliary1ModelReference.IdentityEquals(auxReference).Should().BeTrue();
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(Path.GetFullPath(oldPath));
            detection.RuntimeModelSnapshot.Auxiliary1.ModelPath.Should().Be(Path.GetFullPath(auxPath));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ConcurrentActivation_CancelWhileWaitingForGate_DoesNotRefreshOrMutateState()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            string auxPath = CreatePackage(packageRoot, "pkg-aux", "1", "aux.onnx");
            string recipePath = Path.Combine(tempDir, "recipe.json");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            var recipeManager = new RecipeManager(recipePath);
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            ProductionModelActivationService initialService = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            ProductionModelReference auxReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(auxPath)!);
            (await initialService.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            string configBefore = config.ToPortableJson();
            FileState recipeBefore = CaptureFileState(recipePath);
            string runtimeBefore = detection.RuntimeModelSnapshot.Primary.ModelPath;
            var refreshEntered = new ManualResetEventSlim(false);
            var releaseRefresh = new ManualResetEventSlim(false);
            int refreshCalls = 0;
            ProductionModelActivationService service = CreateService(
                config,
                registry,
                recipeManager,
                detection,
                packageRoot,
                refreshRegistry: () =>
                {
                    if (Interlocked.Increment(ref refreshCalls) == 1)
                    {
                        refreshEntered.Set();
                        if (!releaseRefresh.Wait(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("refresh release timed out");
                        }
                    }

                    return registry.Scan(ScanOptions(packageRoot));
                });

            Task<ProductionModelActivationResult> holdingTask = Task.Run(() => service.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "holding",
                false,
                0));
            refreshEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            using var cts = new CancellationTokenSource();
            Task<ProductionModelActivationResult> canceledTask = service.ActivateAuxiliaryAsync(
                1,
                auxReference.ToSelectionValue(),
                "canceled",
                false,
                0,
                cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledTask);
            refreshCalls.Should().Be(1);
            config.ToPortableJson().Should().Be(configBefore);
            AssertFileState(recipePath, recipeBefore);
            detection.RuntimeModelSnapshot.Primary.ModelPath.Should().Be(runtimeBefore);

            releaseRefresh.Set();
            (await holdingTask.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_FinalConsistencyFailure_RestoresRecipeFilesHistoryBackupsAndVersionSnapshots()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string recipePath = Path.Combine(tempDir, "recipe.json");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            FakeDetectionService? detection = null;
            bool sabotageAfterRecipeCommit = false;
            bool recordVersionWrites = false;
            var transactionVersionWrites = new List<string>();
            var recipeManager = new RecipeManager(recipePath, (path, content) =>
            {
                AtomicFileWriter.WriteAllText(path, content);
                if (recordVersionWrites &&
                    path.Contains($"{Path.DirectorySeparatorChar}Versions{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    transactionVersionWrites.Add(Path.GetFullPath(path));
                }

                if (sabotageAfterRecipeCommit &&
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(tempDir, "recipe_versions.json")), StringComparison.OrdinalIgnoreCase))
                {
                    sabotageAfterRecipeCommit = false;
                    detection!.SetRuntime(primary: oldPath);
                }
            });
            recipeManager.LoadOrCreateDefault(config);
            detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            FileState recipeBefore = CaptureFileState(recipePath);
            FileState recipeBackupBefore = CaptureFileState(recipeManager.BackupPath);
            FileState historyBefore = CaptureFileState(recipeManager.HistoryPath);
            FileState historyBackupBefore = CaptureFileState(recipeManager.HistoryPath + ".bak");
            Dictionary<string, string> versionHashesBefore = CaptureVersionHashes(recipeManager.VersionsDirectory);

            recordVersionWrites = true;
            sabotageAfterRecipeCommit = true;
            ProductionModelActivationResult result = await service.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "switch-final-check-fails",
                false,
                0);

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeFalse();
            config.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            recipeManager.CurrentRecipe.CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            ReadRecipe(recipePath).CurrentModelReference.IdentityEquals(oldReference).Should().BeTrue();
            AssertFileState(recipePath, recipeBefore);
            AssertFileState(recipeManager.BackupPath, recipeBackupBefore);
            AssertFileState(recipeManager.HistoryPath, historyBefore);
            AssertFileState(recipeManager.HistoryPath + ".bak", historyBackupBefore);
            CaptureVersionHashes(recipeManager.VersionsDirectory).Should().BeEquivalentTo(versionHashesBefore);
            recipeManager.GetVersionHistory(20).Should().NotContain(item => item.ChangeSummary == "switch-final-check-fails");
            transactionVersionWrites.Should().NotBeEmpty();
            transactionVersionWrites
                .Where(path => !versionHashesBefore.ContainsKey(GetRelativeVersionPath(recipeManager.VersionsDirectory, path)))
                .Should()
                .OnlyContain(path => !File.Exists(path));
            FindRecoveryArtifacts(tempDir).Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_RecipeRestoreWriteFailure_LatchesModelActivationFaulted()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string recipePath = Path.Combine(tempDir, "recipe.json");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            FakeDetectionService? detection = null;
            bool sabotageAfterRecipeCommit = false;
            bool failRecipeRestore = false;
            var recipeManager = new RecipeManager(
                recipePath,
                (path, content) =>
                {
                    AtomicFileWriter.WriteAllText(path, content);
                    if (sabotageAfterRecipeCommit &&
                        string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(tempDir, "recipe_versions.json")), StringComparison.OrdinalIgnoreCase))
                    {
                        sabotageAfterRecipeCommit = false;
                        detection!.SetRuntime(primary: oldPath);
                    }
                },
                (path, bytes) =>
                {
                    if (failRecipeRestore &&
                        string.Equals(Path.GetFullPath(path), Path.GetFullPath(recipePath), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("recipe restore failed");
                    }

                    AtomicFileWriter.RestoreAllBytes(path, bytes);
                });
            recipeManager.LoadOrCreateDefault(config);
            detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            var sabotagingManager = recipeManager;
            sabotageAfterRecipeCommit = true;
            failRecipeRestore = true;
            var writerBackedService = CreateService(config, registry, sabotagingManager, detection, packageRoot);
            ProductionModelActivationResult result = await writerBackedService.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "faulted-restore",
                false,
                0);

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeTrue();
            result.CompensationFailures.Should().Contain(item => item.Contains("Recipe restore failed", StringComparison.OrdinalIgnoreCase));
            writerBackedService.EnsureReadyForProduction().ErrorCode.Should().Be("ModelActivationFaulted");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ActivatePrimaryAsync_TransactionVersionDeleteFailure_LatchesModelActivationFaulted()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string recipePath = Path.Combine(tempDir, "recipe.json");
            string oldPath = CreatePackage(packageRoot, "pkg-old", "1", "old.onnx");
            string newPath = CreatePackage(packageRoot, "pkg-new", "1", "new.onnx");
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var registry = new ModelRegistry();
            FakeDetectionService? detection = null;
            bool sabotageAfterRecipeCommit = false;
            bool failVersionDelete = false;
            var recipeManager = new RecipeManager(
                recipePath,
                (path, content) =>
                {
                    AtomicFileWriter.WriteAllText(path, content);
                    if (sabotageAfterRecipeCommit &&
                        string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(tempDir, "recipe_versions.json")), StringComparison.OrdinalIgnoreCase))
                    {
                        sabotageAfterRecipeCommit = false;
                        detection!.SetRuntime(primary: oldPath);
                    }
                },
                AtomicFileWriter.RestoreAllBytes,
                path =>
                {
                    if (failVersionDelete &&
                        path.Contains($"{Path.DirectorySeparatorChar}Versions{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("version snapshot delete failed");
                    }

                    File.Delete(path);
                });
            recipeManager.LoadOrCreateDefault(config);
            detection = new FakeDetectionService();
            ProductionModelActivationService service = CreateService(config, registry, recipeManager, detection, packageRoot);
            registry.Scan(ScanOptions(packageRoot));
            ProductionModelReference oldReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(oldPath)!);
            ProductionModelReference newReference = ProductionModelReference.FromApprovedPackage(registry.Resolve(newPath)!);
            (await service.ActivatePrimaryAsync(oldReference.ToSelectionValue(), "initial", false, 0)).Succeeded.Should().BeTrue();

            sabotageAfterRecipeCommit = true;
            failVersionDelete = true;
            ProductionModelActivationResult result = await service.ActivatePrimaryAsync(
                newReference.ToSelectionValue(),
                "faulted-version-delete",
                false,
                0);

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeTrue();
            result.CompensationFailures.Should().Contain(item => item.Contains("version snapshot delete failed", StringComparison.OrdinalIgnoreCase));
            service.EnsureReadyForProduction().ErrorCode.Should().Be("ModelActivationFaulted");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static ProductionModelActivationService CreateService(
        AppConfig config,
        ModelRegistry registry,
        RecipeManager recipeManager,
        FakeDetectionService detection,
        string packageRoot,
        Func<bool>? saveConfig = null,
        Func<IReadOnlyList<ModelRegistryEntry>>? refreshRegistry = null,
        bool omitApprovalEvidenceValidator = false)
    {
        return new ProductionModelActivationService(
            config,
            registry,
            recipeManager,
            detection,
            refreshRegistry ?? (() => registry.Scan(ScanOptions(packageRoot))),
            saveConfig ?? (() => true),
            () => null,
            () => "op",
            () => "Engineer",
            omitApprovalEvidenceValidator ? null : (_, _, _) => ProductionModelReadinessResult.Ok());
    }

    private static ModelRegistryScanOptions ScanOptions(string packageRoot)
    {
        return new ModelRegistryScanOptions
        {
            PackageDirectory = packageRoot,
            RequireProductionApproval = true,
            Warmup = (_, _) => true
        };
    }

    private static string CreatePackage(string packageRoot, string modelId, string version, string fileName)
    {
        string packageDir = Path.Combine(packageRoot, modelId);
        Directory.CreateDirectory(packageDir);
        string modelPath = Path.Combine(packageDir, fileName);
        File.WriteAllBytes(modelPath, new byte[] { (byte)modelId.Length, (byte)version.Length, (byte)fileName.Length });
        File.WriteAllText(
            Path.Combine(packageDir, "manifest.json"),
            JsonSerializer.Serialize(new ModelPackageManifest
            {
                ModelId = modelId,
                Version = version,
                ModelFileName = fileName,
                ModelHash = ComputeSha256(modelPath),
                Labels = new List<string> { "part" },
                TaskType = "Detect",
                InputWidth = 640,
                InputHeight = 640,
                Approval = new ModelApprovalMetadata
                {
                    Status = ModelApprovalStatuses.Approved,
                    ApprovedBy = "qa",
                    ApprovedAt = DateTimeOffset.Now
                }
            }));
        return modelPath;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static FileState CaptureFileState(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? new FileState(fullPath, true, File.ReadAllBytes(fullPath))
            : new FileState(fullPath, false, Array.Empty<byte>());
    }

    private static void AssertFileState(string path, FileState expected)
    {
        Path.GetFullPath(path).Should().Be(expected.Path);
        bool exists = File.Exists(path);
        exists.Should().Be(expected.Exists, $"file state should match for {path}");
        if (expected.Exists)
        {
            File.ReadAllBytes(path).Should().Equal(expected.Content);
        }
    }

    private static Dictionary<string, string> CaptureVersionHashes(string versionsDirectory)
    {
        if (!Directory.Exists(versionsDirectory))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(versionsDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => GetRelativeVersionPath(versionsDirectory, path),
                path => ComputeSha256(File.ReadAllBytes(path)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string GetRelativeVersionPath(string versionsDirectory, string path)
    {
        return Path.GetRelativePath(Path.GetFullPath(versionsDirectory), Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static IReadOnlyList<string> FindRecoveryArtifacts(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                string fileName = Path.GetFileName(path);
                return fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".bak.bak", StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Recipe ReadRecipe(string path)
    {
        return JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path)) ?? new Recipe();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(ProductionModelActivationServiceTests), Guid.NewGuid().ToString("N"));
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

    private sealed record FileState(string Path, bool Exists, byte[] Content);

    private sealed class FakeDetectionService : IDetectionService
    {
        private string _primaryPath = string.Empty;
        private string _aux1Path = string.Empty;
        private string _aux2Path = string.Empty;
        private int _activeLoads;
        private int _maxConcurrentLoads;

        public event Action<DetectionResultData>? DetectionCompleted;
        public event Action<string>? ModelLoaded;
        public event Action<string>? ErrorOccurred;

        public HashSet<string> FailLoadPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Func<ModelRole, string, Task>? BeforeLoadAsync { get; set; }
        public int MaxConcurrentLoads => _maxConcurrentLoads;
        public bool IsModelLoaded => !string.IsNullOrWhiteSpace(_primaryPath);
        public string CurrentModelName => Path.GetFileNameWithoutExtension(_primaryPath);
        public IReadOnlyList<string> AvailableModels => Array.Empty<string>();
        public long LastInferenceMs => 0;
        public DetectionRuntimeStatus RuntimeStatus { get; } = new DetectionRuntimeStatus();
        public DetectionRuntimeModelSnapshot RuntimeModelSnapshot => new DetectionRuntimeModelSnapshot
        {
            Primary = new DetectionModelSlotSnapshot
            {
                Role = ModelRole.Primary,
                IsLoaded = !string.IsNullOrWhiteSpace(_primaryPath),
                ModelPath = _primaryPath
            },
            Auxiliary1 = new DetectionModelSlotSnapshot
            {
                Role = ModelRole.Auxiliary1,
                IsLoaded = !string.IsNullOrWhiteSpace(_aux1Path),
                ModelPath = _aux1Path
            },
            Auxiliary2 = new DetectionModelSlotSnapshot
            {
                Role = ModelRole.Auxiliary2,
                IsLoaded = !string.IsNullOrWhiteSpace(_aux2Path),
                ModelPath = _aux2Path
            }
        };

        public void SetRuntime(string? primary = null, string? auxiliary1 = null, string? auxiliary2 = null)
        {
            _primaryPath = string.IsNullOrWhiteSpace(primary) ? string.Empty : Path.GetFullPath(primary);
            _aux1Path = string.IsNullOrWhiteSpace(auxiliary1) ? string.Empty : Path.GetFullPath(auxiliary1);
            _aux2Path = string.IsNullOrWhiteSpace(auxiliary2) ? string.Empty : Path.GetFullPath(auxiliary2);
        }

        public Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0)
        {
            return LoadSlotAsync(ModelRole.Primary, modelPath, fullPath => _primaryPath = fullPath);
        }

        public Task<bool> LoadAuxiliary1ModelAsync(string modelPath)
        {
            return LoadSlotAsync(ModelRole.Auxiliary1, modelPath, fullPath => _aux1Path = fullPath);
        }

        public Task<bool> LoadAuxiliary2ModelAsync(string modelPath)
        {
            return LoadSlotAsync(ModelRole.Auxiliary2, modelPath, fullPath => _aux2Path = fullPath);
        }

        public void UnloadPrimaryModel() => _primaryPath = string.Empty;
        public void UnloadAuxiliary1Model() => _aux1Path = string.Empty;
        public void UnloadAuxiliary2Model() => _aux2Path = string.Empty;
        public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(false);
        public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(false);
        public Task<DetectionResultData> DetectAsync(Mat image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null) => Task.FromResult(new DetectionResultData());
        public Task<DetectionResultData> DetectAsync(System.Drawing.Bitmap image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null) => Task.FromResult(new DetectionResultData());
        public System.Drawing.Bitmap GenerateResultImage(System.Drawing.Bitmap original, List<YoloResult> results, string[] labels) => new System.Drawing.Bitmap(original);
        public void SetTaskMode(int taskType) { }
        public void SetEnableFallback(bool enabled) { }
        public string[] GetLabels() => Array.Empty<string>();
        public object? GetLastMetrics() => null;
        public void Dispose() { }

        private async Task<bool> LoadSlotAsync(ModelRole role, string modelPath, Action<string> assign)
        {
            string fullPath = Path.GetFullPath(modelPath);
            int active = Interlocked.Increment(ref _activeLoads);
            UpdateMaxConcurrentLoads(active);
            try
            {
                if (BeforeLoadAsync != null)
                {
                    await BeforeLoadAsync(role, fullPath);
                }

                if (FailLoadPaths.Contains(fullPath))
                {
                    return false;
                }

                assign(fullPath);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeLoads);
            }
        }

        private void UpdateMaxConcurrentLoads(int active)
        {
            while (true)
            {
                int current = _maxConcurrentLoads;
                if (active <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxConcurrentLoads, active, current) == current)
                {
                    return;
                }
            }
        }
    }
}
#pragma warning restore CS0067
