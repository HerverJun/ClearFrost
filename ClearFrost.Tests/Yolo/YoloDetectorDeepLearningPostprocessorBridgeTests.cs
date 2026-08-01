using System.Reflection;
using System.Runtime.CompilerServices;
using ClearFrost.Core.DeepLearning;
using ClearFrost.Yolo;
using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClearFrost.Tests.Yolo;

public class YoloDetectorDeepLearningPostprocessorBridgeTests
{
    private const float Tolerance = 0.0001f;

    [Fact]
    public void ConfiguredPostprocessor_ClassificationTensor_通过通用Registry输出分类结果()
    {
        YoloDetector detector = CreateDetector("classification", DeepLearningPostprocessorRegistry.CreateDefault());
        var tensor = new DenseTensor<float>(new[] { 1, 3 });
        tensor[0, 0] = 0.15f;
        tensor[0, 1] = 0.92f;
        tensor[0, 2] = 0.61f;

        List<YoloResult> results = detector.PostprocessWithConfiguredDeepLearningProcessor(
            new[] { new DeepLearningOutputTensor("scores", tensor) },
            confidence: 0.5f,
            iouThreshold: 0.3f,
            globalIou: false);

        results.Should().HaveCount(2);
        results[0].DataKind.Should().Be(YoloResultDataKind.Classification);
        results[0].ClassId.Should().Be(1);
        results[0].Confidence.Should().BeApproximately(0.92f, Tolerance);
        results[1].ClassId.Should().Be(2);
    }

    [Fact]
    public void ConfiguredPostprocessor_CustomRegistry_可接入非Yolo输出解释器()
    {
        var registry = DeepLearningPostprocessorRegistry.CreateDefault();
        registry.Register(new FakeHeatmapPostprocessor());
        YoloDetector detector = CreateDetector("heatmap-anomaly", registry);
        var tensor = new DenseTensor<float>(new[] { 1, 2, 2 });

        List<YoloResult> results = detector.PostprocessWithConfiguredDeepLearningProcessor(
            new[] { new DeepLearningOutputTensor("anomaly_heatmap", tensor) },
            confidence: 0.2f,
            iouThreshold: 0.3f,
            globalIou: false);

        results.Should().ContainSingle();
        results[0].DataKind.Should().Be(YoloResultDataKind.Detection);
        results[0].ClassId.Should().Be(9);
        results[0].CenterX.Should().BeApproximately(20, Tolerance);
        results[0].Confidence.Should().BeApproximately(0.97f, Tolerance);
    }

    [Fact]
    public void BoundsFilter_分类结果不会被当作零宽高检测框删除()
    {
        YoloDetector detector = CreateDetector("classification", DeepLearningPostprocessorRegistry.CreateDefault());
        var classification = new YoloResult();
        classification.SetClassificationData(0.88f, 2);
        var results = new List<YoloResult> { classification };

        InvokeRemoveOutOfBounds(detector, results);

        results.Should().ContainSingle();
        results[0].DataKind.Should().Be(YoloResultDataKind.Classification);
        results[0].ClassId.Should().Be(2);
    }

    [Theory]
    [InlineData("yolo")]
    [InlineData("yolov8")]
    [InlineData("YOLOv26")]
    public void NormalizePostprocessorKey_YoloKey_不劫持默认Yolo分支(string key)
    {
        InvokeNormalizePostprocessorKey(key).Should().BeEmpty();
    }

    [Fact]
    public void CopyPostprocessOptions_DuplicateCaseKeys_KeepsFirstValidOption()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" top_k "] = "2",
            ["TOP_K"] = "3",
            [" "] = "ignored"
        };

        IReadOnlyDictionary<string, string> copied = InvokeCopyPostprocessOptions(options);

        copied.Should().ContainSingle();
        copied.Should().ContainKey("top_k").WhoseValue.Should().Be("2");
    }

    [Fact]
    public void CopyMetadata_DuplicateCaseKeys_KeepsFirstValidEntry()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" names "] = "{0: 'first'}",
            ["NAMES"] = "{0: 'second'}",
            [" "] = "ignored"
        };

        IReadOnlyDictionary<string, string> copied = InvokeCopyMetadata(metadata);

        copied.Should().ContainSingle();
        copied.Should().ContainKey("names").WhoseValue.Should().Be("{0: 'first'}");
    }

    [Fact]
    public void ResolveMetadataPostprocessorKey_ExplicitUnknownPostprocessor_ReturnsValueForDiagnostic()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["postprocessor_key"] = " vendor-custom-head "
        };

        InvokeResolveMetadataPostprocessorKey(metadata).Should().Be("vendor-custom-head");
    }

    [Fact]
    public void ResolveMetadataPostprocessorKey_GenericAlgorithmName_DoesNotForceConfiguredPostprocessor()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["algorithm"] = "vendor-net-v1"
        };

        InvokeResolveMetadataPostprocessorKey(metadata).Should().BeEmpty();
    }

    [Fact]
    public void ResolveMetadataPostprocessorKey_KnownAlgorithmAlias_ReturnsPostprocessorKey()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["algorithm_key"] = " semantic-segmentation "
        };

        InvokeResolveMetadataPostprocessorKey(metadata).Should().Be("semantic-segmentation");
    }

    [Fact]
    public void ResolveMetadataScoreNormalization_ExplicitAlias_ParsesKnownValue()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" scoreNormalization "] = " logit-sigmoid "
        };

        InvokeResolveMetadataScoreNormalization(metadata).Should().Be(DeepLearningScoreNormalization.Sigmoid);
    }

    [Fact]
    public void ResolveMetadataScoreNormalization_InvalidExplicitValue_ThrowsActionableError()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["score_normalization"] = "softmxx"
        };

        Action act = () => InvokeResolveMetadataScoreNormalization(metadata);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Unknown score normalization metadata value: softmxx*Known score normalizations:*logit-sigmoid*softmax*");
    }

    [Fact]
    public void ResolveMetadataScoreNormalization_GenericUnknownNormalization_DoesNotFail()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["normalization"] = "imagenet-mean-std",
            ["activation"] = "relu"
        };

        InvokeResolveMetadataScoreNormalization(metadata).Should().Be(DeepLearningScoreNormalization.None);
    }

    [Fact]
    public void ConfiguredPostprocessor_DefaultHeatmap_UsesModelInputSizeForScaledBox()
    {
        YoloDetector detector = CreateDetector("heatmap-anomaly", DeepLearningPostprocessorRegistry.CreateDefault());
        var tensor = new DenseTensor<float>(new[] { 1, 1, 2, 4 });
        tensor[0, 0, 0, 1] = 0.80f;
        tensor[0, 0, 0, 2] = 0.90f;
        tensor[0, 0, 1, 2] = 0.70f;

        List<YoloResult> results = detector.PostprocessWithConfiguredDeepLearningProcessor(
            new[] { new DeepLearningOutputTensor("anomaly_map", tensor) },
            confidence: 0.5f,
            iouThreshold: 0.3f,
            globalIou: false);

        results.Should().ContainSingle();
        results[0].DataKind.Should().Be(YoloResultDataKind.Detection);
        results[0].CenterX.Should().BeApproximately(40f, Tolerance);
        results[0].CenterY.Should().BeApproximately(20f, Tolerance);
        results[0].Width.Should().BeApproximately(40f, Tolerance);
        results[0].Height.Should().BeApproximately(40f, Tolerance);
        results[0].Confidence.Should().BeApproximately(0.90f, Tolerance);
        results[0].MaskData.Should().NotBeNull();
    }

    [Fact]
    public void ConfiguredPostprocessor_ManifestPostprocessOptions_ArePassedAsRequestMetadata()
    {
        YoloDetector detector = CreateDetector(
            "generic-detection",
            DeepLearningPostprocessorRegistry.CreateDefault(),
            new Dictionary<string, string>
            {
                ["box_format"] = "xywh",
                ["normalized_boxes"] = "true",
                ["apply_nms"] = "true"
            });
        var tensor = new DenseTensor<float>(new[] { 1, 2, 6 });
        tensor[0, 0, 0] = 0.50f;
        tensor[0, 0, 1] = 0.50f;
        tensor[0, 0, 2] = 0.50f;
        tensor[0, 0, 3] = 0.50f;
        tensor[0, 0, 4] = 0.90f;
        tensor[0, 0, 5] = 1f;
        tensor[0, 1, 0] = 0.52f;
        tensor[0, 1, 1] = 0.52f;
        tensor[0, 1, 2] = 0.50f;
        tensor[0, 1, 3] = 0.50f;
        tensor[0, 1, 4] = 0.80f;
        tensor[0, 1, 5] = 1f;

        List<YoloResult> results = detector.PostprocessWithConfiguredDeepLearningProcessor(
            new[] { new DeepLearningOutputTensor("detections", tensor) },
            confidence: 0.5f,
            iouThreshold: 0.3f,
            globalIou: false);

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(1);
        results[0].CenterX.Should().BeApproximately(40f, Tolerance);
        results[0].CenterY.Should().BeApproximately(20f, Tolerance);
        results[0].Width.Should().BeApproximately(40f, Tolerance);
        results[0].Height.Should().BeApproximately(20f, Tolerance);
        results[0].Confidence.Should().BeApproximately(0.90f, Tolerance);
    }

    private static YoloDetector CreateDetector(string postprocessorKey, DeepLearningPostprocessorRegistry registry)
    {
        return CreateDetector(postprocessorKey, registry, null);
    }

    private static YoloDetector CreateDetector(
        string postprocessorKey,
        DeepLearningPostprocessorRegistry registry,
        IReadOnlyDictionary<string, string>? postprocessOptions)
    {
        var detector = (YoloDetector)RuntimeHelpers.GetUninitializedObject(typeof(YoloDetector));
        SetPrivateField(detector, "_postprocessorAlgorithmKey", postprocessorKey);
        SetPrivateField(detector, "_deepLearningPostprocessorRegistry", registry);
        SetPrivateField(detector, "_scoreNormalization", DeepLearningScoreNormalization.None);
        SetPrivateField(detector, "_taskType", postprocessorKey);
        SetPrivateField(detector, "_modelMetadata", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        SetPrivateField(
            detector,
            "_configuredPostprocessOptions",
            postprocessOptions != null
                ? new Dictionary<string, string>(postprocessOptions, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        SetPrivateField(detector, "_inferenceImageWidth", 640);
        SetPrivateField(detector, "_inferenceImageHeight", 480);
        SetPrivateField(detector, "_tensorWidth", 80);
        SetPrivateField(detector, "_tensorHeight", 40);
        SetPrivateField(detector, "_scale", 1f);
        detector.Labels = Array.Empty<string>();
        return detector;
    }

    private static void InvokeRemoveOutOfBounds(YoloDetector detector, List<YoloResult> results)
    {
        MethodInfo method = typeof(YoloDetector).GetMethod("RemoveOutOfBoundsCoordinates", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(YoloDetector).FullName, "RemoveOutOfBoundsCoordinates");

        object[] args = { results };
        method.Invoke(detector, args);
    }

    private static string InvokeNormalizePostprocessorKey(string key)
    {
        MethodInfo method = typeof(YoloDetector).GetMethod("NormalizePostprocessorKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(YoloDetector).FullName, "NormalizePostprocessorKey");

        return (string)method.Invoke(null, new object[] { key })!;
    }

    private static IReadOnlyDictionary<string, string> InvokeCopyPostprocessOptions(IReadOnlyDictionary<string, string> options)
    {
        MethodInfo method = typeof(YoloDetector).GetMethod("CopyPostprocessOptions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(YoloDetector).FullName, "CopyPostprocessOptions");

        return (IReadOnlyDictionary<string, string>)method.Invoke(null, new object[] { options })!;
    }

    private static IReadOnlyDictionary<string, string> InvokeCopyMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        MethodInfo method = typeof(YoloDetector).GetMethod("CopyMetadata", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(YoloDetector).FullName, "CopyMetadata");

        return (IReadOnlyDictionary<string, string>)method.Invoke(null, new object[] { metadata })!;
    }

    private static string InvokeResolveMetadataPostprocessorKey(IReadOnlyDictionary<string, string> metadata)
    {
        MethodInfo method = typeof(YoloDetector).GetMethod("ResolveMetadataPostprocessorKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(YoloDetector).FullName, "ResolveMetadataPostprocessorKey");

        return (string)method.Invoke(null, new object[] { metadata })!;
    }

    private static DeepLearningScoreNormalization InvokeResolveMetadataScoreNormalization(IReadOnlyDictionary<string, string> metadata)
    {
        MethodInfo method = typeof(YoloDetector).GetMethod("ResolveMetadataScoreNormalization", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(YoloDetector).FullName, "ResolveMetadataScoreNormalization");

        try
        {
            return (DeepLearningScoreNormalization)method.Invoke(null, new object[] { metadata })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static void SetPrivateField(YoloDetector target, string fieldName, object value)
    {
        FieldInfo field = typeof(YoloDetector).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(YoloDetector).FullName, fieldName);

        field.SetValue(target, value);
    }

    private sealed class FakeHeatmapPostprocessor : IDeepLearningPostprocessor
    {
        public string Key => "heatmap-anomaly";

        public bool CanProcess(DeepLearningPostprocessRequest request)
        {
            return string.Equals(request.AlgorithmKey, Key, StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<YoloResult> Process(DeepLearningPostprocessRequest request)
        {
            var result = new YoloResult();
            result.SetDetectionData(centerX: 20, centerY: 24, width: 8, height: 10, confidence: 0.97f, classId: 9);
            return new[] { result };
        }
    }
}
