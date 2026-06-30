using ClearFrost.Services;
using ClearFrost.Tests.Yolo;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

[Collection(OnnxRuntimeCollection.Name)]
public class DetectionServiceTests
{
    [Fact]
    public void Constructor_DefaultsToCpu()
    {
        using var service = new DetectionService();

        service.RuntimeStatus.GpuRequested.Should().BeFalse();
        service.RuntimeStatus.GpuActive.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAsync_ModelNotLoaded_ReturnsQualifiedFailure()
    {
        using var image = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(128));
        using var service = new DetectionService(useGpu: false);

        var result = await service.DetectAsync(image, 0.5f, 0.3f);

        result.HasError.Should().BeTrue();
        result.IsQualified.Should().BeFalse();
        result.ErrorMessage.Should().Contain("模型未加载");
        result.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectAsync_EmptyImage_ReturnsQualifiedFailure()
    {
        using var image = new Mat();
        using var service = new DetectionService(useGpu: false);

        var result = await service.DetectAsync(image, 0.5f, 0.3f);

        result.HasError.Should().BeTrue();
        result.IsQualified.Should().BeFalse();
        result.ErrorMessage.Should().Contain("输入图像为空");
    }

    [Fact]
    public async Task LoadModelAsync_GpuRebuildFailure_PreservesExistingAuxiliarySlots()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string sourceModel = GetSampleOnnxPath();
            string primaryPath = CopyModel(sourceModel, tempDir, "primary.onnx");
            string aux1Path = CopyModel(sourceModel, tempDir, "aux1.onnx");
            string aux2Path = CopyModel(sourceModel, tempDir, "aux2.onnx");
            string brokenPath = Path.Combine(tempDir, "broken.onnx");
            File.WriteAllBytes(brokenPath, new byte[] { 1, 2, 3, 4 });

            using var service = new DetectionService(useGpu: false);

            bool loaded = await service.LoadModelAsync(primaryPath, useGpu: false);
            loaded.Should().BeTrue();

            (await service.LoadAuxiliary1ModelAsync(aux1Path)).Should().BeTrue();
            (await service.LoadAuxiliary2ModelAsync(aux2Path)).Should().BeTrue();

            var initialManager = GetModelManager(service);
            initialManager.Should().NotBeNull();
            initialManager!.IsPrimaryLoaded.Should().BeTrue();
            initialManager.Auxiliary1ModelPath.Should().Be(aux1Path);
            initialManager.Auxiliary2ModelPath.Should().Be(aux2Path);

            bool switched = await service.LoadModelAsync(brokenPath, useGpu: true);
            switched.Should().BeFalse();

            var preservedManager = GetModelManager(service);
            preservedManager.Should().NotBeNull();
            preservedManager!.IsPrimaryLoaded.Should().BeTrue();
            preservedManager.PrimaryModelPath.Should().Be(primaryPath);
            preservedManager.Auxiliary1ModelPath.Should().Be(aux1Path);
            preservedManager.Auxiliary2ModelPath.Should().Be(aux2Path);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LoadModelAsync_GpuRebuildAuxFailure_StillRaisesModelLoaded()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string sourceModel = GetSampleOnnxPath();
            string primaryPath = CopyModel(sourceModel, tempDir, "primary.onnx");
            string aux1Path = CopyModel(sourceModel, tempDir, "aux1.onnx");
            string reloadedPath = CopyModel(sourceModel, tempDir, "reload.onnx");
            string brokenAuxPath = Path.Combine(tempDir, "broken-aux.onnx");
            File.WriteAllBytes(brokenAuxPath, new byte[] { 9, 8, 7, 6 });

            using var service = new DetectionService(useGpu: false);
            int modelLoadedCount = 0;
            string? lastLoadedModel = null;
            service.ModelLoaded += modelName =>
            {
                modelLoadedCount++;
                lastLoadedModel = modelName;
            };

            (await service.LoadModelAsync(primaryPath, useGpu: false)).Should().BeTrue();
            (await service.LoadAuxiliary1ModelAsync(aux1Path)).Should().BeTrue();

            var manager = GetModelManager(service);
            manager.Should().NotBeNull();
            SetPrivateField(manager!, "_auxiliary1ModelPath", brokenAuxPath);

            bool reloaded = await service.LoadModelAsync(reloadedPath, useGpu: true);
            reloaded.Should().BeTrue();

            modelLoadedCount.Should().Be(2);
            lastLoadedModel.Should().Be(Path.GetFileNameWithoutExtension(reloadedPath));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LoadModelAsync_GpuIndex_RecordsRequestedDevice()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string modelPath = CopyModel(GetSampleOnnxPath(), tempDir, "primary.onnx");
            using var service = new DetectionService(useGpu: true, gpuDeviceId: 2);

            bool loaded = await service.LoadModelAsync(modelPath, useGpu: true, gpuDeviceId: 2);

            loaded.Should().BeTrue();
            service.RuntimeStatus.GpuRequested.Should().BeTrue();
            service.RuntimeStatus.GpuDeviceId.Should().Be(2);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task LoadModelAsync_DirectMlFailure_FallsBackToCpuAndRecordsReason()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string modelPath = CopyModel(GetSampleOnnxPath(), tempDir, "primary.onnx");
            using var service = new DetectionService(useGpu: true, gpuDeviceId: 99);

            bool loaded = await service.LoadModelAsync(modelPath, useGpu: true, gpuDeviceId: 99);

            loaded.Should().BeTrue();
            service.RuntimeStatus.GpuRequested.Should().BeTrue();
            service.RuntimeStatus.GpuActive.Should().BeFalse();
            service.RuntimeStatus.ExecutionProvider.Should().Be("CPUExecutionProvider");
            service.RuntimeStatus.GpuFailureReason.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task UnloadPrimaryModel_清理主运行时和兼容缓存()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string modelPath = CopyModel(GetSampleOnnxPath(), tempDir, "primary.onnx");
            using var service = new DetectionService(useGpu: false);

            (await service.LoadModelAsync(modelPath, useGpu: false)).Should().BeTrue();
            service.RuntimeModelSnapshot.Primary.IsLoaded.Should().BeTrue();

            service.UnloadPrimaryModel();

            service.IsModelLoaded.Should().BeFalse();
            service.CurrentModelName.Should().Be("未加载");
            service.RuntimeModelSnapshot.Primary.IsLoaded.Should().BeFalse();
            service.RuntimeModelSnapshot.Primary.ModelPath.Should().BeEmpty();
            service.GetLabels().Should().BeEmpty();
            service.GetLastMetrics().Should().BeNull();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task SetEnableFallback_返回前同步更新多模型管理器()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string modelPath = CopyModel(GetSampleOnnxPath(), tempDir, "primary.onnx");
            using var service = new DetectionService(useGpu: false);

            (await service.LoadModelAsync(modelPath, useGpu: false)).Should().BeTrue();

            service.SetEnableFallback(true);
            GetModelManager(service)!.EnableFallback.Should().BeTrue();

            service.SetEnableFallback(false);
            GetModelManager(service)!.EnableFallback.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void GetLabels_返回缓存副本避免外部改写()
    {
        using var service = new DetectionService(useGpu: false);
        SetPrivateField(service, "_cachedLabels", new[] { "ok", "ng" });

        string[] labels = service.GetLabels();
        labels[0] = "mutated";

        service.GetLabels()[0].Should().Be("ok");
    }

    private static MultiModelManager? GetModelManager(DetectionService service)
    {
        var field = typeof(DetectionService).GetField(
            "_modelManager",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        return (MultiModelManager?)field?.GetValue(service);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        field.Should().NotBeNull($"field {fieldName} should exist");
        field!.SetValue(target, value);
    }

    private static string GetSampleOnnxPath()
    {
        string onnxDir = Path.Combine(AppContext.BaseDirectory, "ONNX");
        string? sample = Directory.GetFiles(onnxDir, "*.onnx")
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        sample.Should().NotBeNullOrWhiteSpace("test output should contain a copied ONNX model");
        return sample!;
    }

    private static string CopyModel(string sourcePath, string targetDirectory, string fileName)
    {
        string targetPath = Path.Combine(targetDirectory, fileName);
        File.Copy(sourcePath, targetPath, true);
        return targetPath;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClearFrostTests",
            nameof(DetectionServiceTests),
            Guid.NewGuid().ToString("N"));

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
