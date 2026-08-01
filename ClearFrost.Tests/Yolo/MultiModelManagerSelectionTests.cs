using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Yolo;

[Collection(OnnxRuntimeCollection.Name)]
public class MultiModelManagerSelectionTests
{
    [Fact]
    public void IsTargetSatisfied_TargetCountRequiresExactMatch()
    {
        var labels = new[] { "screw", "body" };
        var results = new List<YoloResult>
        {
            Detection(0),
            Detection(0),
            Detection(1)
        };

        MultiModelManager.CountTargetLabelHits(results, labels, "screw").Should().Be(2);
        MultiModelManager.IsTargetSatisfied(results, labels, "screw", 2).Should().BeTrue();
        MultiModelManager.IsTargetSatisfied(results, labels, "screw", 4).Should().BeFalse();
    }

    [Fact]
    public void IsTargetSatisfied_ExpectedZeroAllowsNoTargetHits()
    {
        var labels = new[] { "screw", "body" };
        var results = new List<YoloResult>
        {
            Detection(1)
        };

        MultiModelManager.CountTargetLabelHits(results, labels, "screw").Should().Be(0);
        MultiModelManager.IsTargetSatisfied(results, labels, "screw", 0).Should().BeTrue();
    }

    [Fact]
    public void CountTargetLabelHits_IgnoresOutOfRangeClassIds()
    {
        var labels = new[] { "screw" };
        var results = new List<YoloResult>
        {
            Detection(0),
            Detection(-1),
            Detection(9)
        };

        MultiModelManager.CountTargetLabelHits(results, labels, "screw").Should().Be(1);
    }

    [Fact]
    public void InferenceWithFallback_NoHitResetsLastUsedModel()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string modelPath = CopySampleOnnx(tempDir, "primary.onnx");
            using var manager = new MultiModelManager(useGpu: false);
            manager.LoadPrimaryModel(modelPath);

            SetLastUsedModel(manager, ModelRole.Auxiliary2);

            using var image = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(0));
            var result = manager.InferenceWithFallback(image, confidence: 1f, targetLabel: "screw", targetCount: 1);

            result.Results.Should().BeEmpty();
            manager.LastUsedModel.Should().Be(ModelRole.None);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void InferenceWithFallback_FallbackDisabledRecordsSingleAttemptAndReason()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string primaryPath = CopySampleOnnx(tempDir, "primary.onnx");
            string auxiliaryPath = CopySampleOnnx(tempDir, "auxiliary1.onnx");
            using var manager = new MultiModelManager(useGpu: false);
            manager.LoadPrimaryModel(primaryPath);
            manager.LoadAuxiliary1Model(auxiliaryPath);
            manager.EnableFallback = false;

            using var image = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(0));
            var result = manager.InferenceWithFallback(
                image,
                confidence: 1f,
                targetLabel: "__missing_label__",
                targetCount: 1);

            result.FallbackAttemptCount.Should().Be(1);
            result.FallbackSkippedReason.Should().Be("FallbackDisabled");
            result.WasFallback.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static YoloResult Detection(int classId)
    {
        var result = new YoloResult();
        result.SetDetectionData(10, 10, 5, 5, 0.9f, classId);
        return result;
    }

    private static string CopySampleOnnx(string targetDirectory, string fileName)
    {
        string source = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ONNX"), "*.onnx")
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .First();
        string target = Path.Combine(targetDirectory, fileName);
        File.Copy(source, target, true);
        return target;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(MultiModelManagerSelectionTests), Guid.NewGuid().ToString("N"));
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

    private static void SetLastUsedModel(MultiModelManager manager, ModelRole role)
    {
        var property = typeof(MultiModelManager).GetProperty("LastUsedModel");
        property.Should().NotBeNull();
        var backingField = typeof(MultiModelManager).GetField("<LastUsedModel>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        backingField.Should().NotBeNull();
        backingField!.SetValue(manager, role);
    }

    [Fact]
    public void InferenceWithFallback_WhenPrimaryFails_AndAux1Fails_TriggersAux2Unsupervised()
    {
        using var manager = new MultiModelManager(useGpu: false);

        var primaryMock = new MockVisionModel
        {
            Labels = new[] { "defect" },
            OnInferenceMat = (img, conf, iou, global, mode) => new ModelResult
            {
                IsQualified = true,
                Results = new List<YoloResult>()
            }
        };

        var aux1Mock = new MockVisionModel
        {
            Labels = new[] { "defect" },
            OnInferenceMat = (img, conf, iou, global, mode) => new ModelResult
            {
                IsQualified = true,
                Results = new List<YoloResult>()
            }
        };

        var unsupervisedMock = new MockVisionModel
        {
            Labels = new[] { "anomaly" },
            OnInferenceMat = (img, conf, iou, global, mode) =>
            {
                var fakeResult = new YoloResult();
                fakeResult.SetDetectionData(32, 32, 64, 64, 0.85f, 0);
                return new ModelResult
                {
                    IsQualified = false,
                    AnomalyScore = 0.85f,
                    Results = new List<YoloResult> { fakeResult }
                };
            }
        };

        typeof(MultiModelManager).GetField("_primaryModel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(manager, primaryMock);
        typeof(MultiModelManager).GetField("_auxiliary1Model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(manager, aux1Mock);
        typeof(MultiModelManager).GetField("_auxiliary2Model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(manager, unsupervisedMock);

        manager.EnableFallback = true;

        using var image = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(0));
        var result = manager.InferenceWithFallback(
            image,
            confidence: 0.5f,
            targetLabel: "defect",
            targetCount: 1);

        result.WasFallback.Should().BeTrue();
        result.UsedModel.Should().Be(ModelRole.Auxiliary2);
        result.Results.Should().HaveCount(1);
        result.Results[0].Confidence.Should().Be(0.85f);
        result.Results[0].Width.Should().Be(64);
    }
}

public class MockVisionModel : IVisionModel
{
    public string[] Labels { get; set; } = Array.Empty<string>();
    public InferenceMetrics? LastMetrics { get; set; }
    public bool RequestedGpu => false;
    public bool GpuActive => false;
    public int GpuDeviceId => 0;
    public string ExecutionProvider => "CPUExecutionProvider";
    public string GpuFailureReason => string.Empty;

    public Func<Mat, float, float, bool, int, ModelResult>? OnInferenceMat { get; set; }

    public ModelResult Inference(Bitmap image, float confidence = 0.5F, float iouThreshold = 0.3F, bool globalIou = false, int preprocessingMode = -1)
    {
        using var mat = OpenCvSharp.Extensions.BitmapConverter.ToMat(image);
        return Inference(mat, confidence, iouThreshold, globalIou, preprocessingMode);
    }

    public ModelResult Inference(Mat image, float confidence = 0.5F, float iouThreshold = 0.3F, bool globalIou = false, int preprocessingMode = -1)
    {
        return OnInferenceMat?.Invoke(image, confidence, iouThreshold, globalIou, preprocessingMode)
            ?? new ModelResult { IsQualified = true };
    }

    public void Dispose() { }
}
