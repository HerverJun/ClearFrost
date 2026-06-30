using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
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

    private static ProductionModelActivationService CreateService(
        AppConfig config,
        ModelRegistry registry,
        RecipeManager recipeManager,
        FakeDetectionService detection,
        string packageRoot,
        Func<bool>? saveConfig = null)
    {
        return new ProductionModelActivationService(
            config,
            registry,
            recipeManager,
            detection,
            () => registry.Scan(ScanOptions(packageRoot)),
            saveConfig ?? (() => true),
            () => null,
            () => "op",
            () => "Engineer");
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

    private sealed class FakeDetectionService : IDetectionService
    {
        private string _primaryPath = string.Empty;
        private string _aux1Path = string.Empty;
        private string _aux2Path = string.Empty;

        public event Action<DetectionResultData>? DetectionCompleted;
        public event Action<string>? ModelLoaded;
        public event Action<string>? ErrorOccurred;

        public HashSet<string> FailLoadPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            string fullPath = Path.GetFullPath(modelPath);
            if (FailLoadPaths.Contains(fullPath))
            {
                return Task.FromResult(false);
            }

            _primaryPath = fullPath;
            return Task.FromResult(true);
        }

        public Task<bool> LoadAuxiliary1ModelAsync(string modelPath)
        {
            string fullPath = Path.GetFullPath(modelPath);
            if (FailLoadPaths.Contains(fullPath)) return Task.FromResult(false);
            _aux1Path = fullPath;
            return Task.FromResult(true);
        }

        public Task<bool> LoadAuxiliary2ModelAsync(string modelPath)
        {
            string fullPath = Path.GetFullPath(modelPath);
            if (FailLoadPaths.Contains(fullPath)) return Task.FromResult(false);
            _aux2Path = fullPath;
            return Task.FromResult(true);
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
    }
}
#pragma warning restore CS0067
