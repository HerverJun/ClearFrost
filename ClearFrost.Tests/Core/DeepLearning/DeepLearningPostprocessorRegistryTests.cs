using ClearFrost.Core.DeepLearning;
using ClearFrost.Yolo;
using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClearFrost.Tests.Core.DeepLearning;

public class DeepLearningPostprocessorRegistryTests
{
    [Fact]
    public void Configuration_KnownPostprocessorKeys_IncludeDefaultAliasesAndReservedYoloKeys()
    {
        DeepLearningPostprocessorConfiguration.KnownPostprocessorKeys.Should().Contain(new[]
        {
            "classification",
            "heatmap-anomaly",
            "generic-detection",
            "decoded-detection",
            "semantic-segmentation"
        });
        DeepLearningPostprocessorConfiguration.IsKnownPostprocessorKey("yolo").Should().BeTrue();
        DeepLearningPostprocessorConfiguration.IsKnownPostprocessorKey("yolov8").Should().BeTrue();
        DeepLearningPostprocessorConfiguration.IsKnownPostprocessorKey("missing-head").Should().BeFalse();
        DeepLearningPostprocessorConfiguration.TryParseScoreNormalization("logit-sigmoid", out DeepLearningScoreNormalization normalization)
            .Should()
            .BeTrue();
        normalization.Should().Be(DeepLearningScoreNormalization.Sigmoid);
        DeepLearningPostprocessorConfiguration.TryParseScoreNormalization("1", out normalization)
            .Should()
            .BeFalse();
        normalization.Should().Be(DeepLearningScoreNormalization.None);
        DeepLearningPostprocessorConfiguration.TryParseScoreNormalization("999", out normalization)
            .Should()
            .BeFalse();
        normalization.Should().Be(DeepLearningScoreNormalization.None);
    }

    [Fact]
    public void DefaultRegistry_ClassificationLogits_输出Classification结果并按置信度排序()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 4 });
        tensor[0, 0] = 0.12f;
        tensor[0, 1] = 0.91f;
        tensor[0, 2] = 0.72f;
        tensor[0, 3] = 0.42f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "efficientnet",
            TaskKey = "classification",
            Outputs = new[] { new DeepLearningOutputTensor("probabilities", tensor) },
            ConfidenceThreshold = 0.5f
        });

        results.Should().HaveCount(2);
        results[0].DataKind.Should().Be(YoloResultDataKind.Classification);
        results[0].ClassId.Should().Be(1);
        results[0].Confidence.Should().BeApproximately(0.91f, 0.0001f);
        results[1].ClassId.Should().Be(2);
    }

    [Fact]
    public void DefaultRegistry_ClassificationLogits_支持Rank1SoftmaxLogits()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 3 });
        tensor[0] = 0.1f;
        tensor[1] = 2.5f;
        tensor[2] = -0.3f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            TaskKey = "image-classification",
            Outputs = new[] { new DeepLearningOutputTensor("logits", tensor) },
            ConfidenceThreshold = 0.5f,
            ScoreNormalization = DeepLearningScoreNormalization.Softmax
        });

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(1);
        results[0].Confidence.Should().BeGreaterThan(0.8f);
    }

    [Fact]
    public void DefaultRegistry_ClassificationLogits_支持SigmoidMultiLabelLogits()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 4 });
        tensor[0, 0] = -3.0f;
        tensor[0, 1] = 0.2f;
        tensor[0, 2] = 2.2f;
        tensor[0, 3] = 1.1f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "classification",
            Outputs = new[] { new DeepLearningOutputTensor("logits", tensor) },
            ConfidenceThreshold = 0.7f,
            ScoreNormalization = DeepLearningScoreNormalization.Sigmoid
        });

        results.Should().HaveCount(2);
        results[0].ClassId.Should().Be(2);
        results[0].Confidence.Should().BeGreaterThan(0.89f);
        results[1].ClassId.Should().Be(3);
        results[1].Confidence.Should().BeGreaterThan(0.74f);
    }

    [Fact]
    public void DefaultRegistry_ClassificationLogits_PostprocessOptionsTopK_限制分类返回数量()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 5 });
        tensor[0, 0] = 0.91f;
        tensor[0, 1] = 0.77f;
        tensor[0, 2] = 0.66f;
        tensor[0, 3] = 0.95f;
        tensor[0, 4] = 0.72f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "classification",
            Outputs = new[] { new DeepLearningOutputTensor("probabilities", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["top_k"] = "2"
            },
            ConfidenceThreshold = 0.5f
        });

        results.Should().HaveCount(2);
        results[0].ClassId.Should().Be(3);
        results[0].Confidence.Should().BeApproximately(0.95f, 0.0001f);
        results[1].ClassId.Should().Be(0);
        results[1].Confidence.Should().BeApproximately(0.91f, 0.0001f);
    }

    [Fact]
    public void Registry_可注册非Yolo算法后处理器()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        registry.Register(new FakeHeatmapPostprocessor());
        var tensor = new DenseTensor<float>(new[] { 1, 2, 2 });

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "heatmap-anomaly",
            TaskKey = "anomaly",
            Outputs = new[] { new DeepLearningOutputTensor("heatmap", tensor) },
            ConfidenceThreshold = 0.1f
        });

        results.Should().ContainSingle();
        results[0].DataKind.Should().Be(YoloResultDataKind.Detection);
        results[0].ClassId.Should().Be(7);
        results[0].Confidence.Should().BeApproximately(0.99f, 0.0001f);
    }

    [Fact]
    public void Registry_未知算法给出可诊断错误()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 4, 8 });

        Action act = () => registry.Resolve(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "unknown-detector",
            TaskKey = "detect",
            Outputs = new[] { new DeepLearningOutputTensor("output0", tensor) }
        });

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*No registered deep learning postprocessor*unknown-detector*primary_shape=[1, 4, 8]*output_shapes=[output0=[1, 4, 8]]*Known postprocessor keys:*classification*semantic-segmentation*");
    }

    [Fact]
    public void Registry_ConfiguredPostprocessorShapeMismatch_DoesNotFallbackByTask()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 2, 2, 2 });

        Action act = () => registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "classification",
            TaskKey = "semantic-segmentation",
            Outputs = new[] { new DeepLearningOutputTensor("segmentation", tensor) }
        });

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Configured deep learning postprocessor 'classification'*resolved to 'classification'*primary_shape=[1, 2, 2, 2]*output_shapes=[segmentation=[1, 2, 2, 2]]*PostprocessorKey/PostprocessOptions*Known postprocessor keys:*semantic-segmentation*");
    }

    [Fact]
    public void Registry_MetadataPostprocessorShapeMismatch_DoesNotFallbackByTask()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 2, 2, 2 });

        Action act = () => registry.Process(new DeepLearningPostprocessRequest
        {
            TaskKey = "semantic-segmentation",
            Outputs = new[] { new DeepLearningOutputTensor("segmentation", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["postprocessor_key"] = " classification "
            }
        });

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Configured deep learning postprocessor 'classification'*resolved to 'classification'*primary_shape=[1, 2, 2, 2]*output_shapes=[segmentation=[1, 2, 2, 2]]*PostprocessorKey/PostprocessOptions*Known postprocessor keys:*semantic-segmentation*");
    }

    [Fact]
    public void Registry_UnknownMetadataPostprocessor_DoesNotFallbackByTask()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 2 });
        tensor[0, 0] = 0.1f;
        tensor[0, 1] = 0.9f;

        Action act = () => registry.Process(new DeepLearningPostprocessRequest
        {
            TaskKey = "classification",
            Outputs = new[] { new DeepLearningOutputTensor("probabilities", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["postprocessor_key"] = "vendor-custom-head"
            }
        });

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Configured deep learning postprocessor 'vendor-custom-head' is not registered*task=classification*primary_shape=[1, 2]*output_shapes=[probabilities=[1, 2]]*Known postprocessor keys:*classification*");
    }

    [Fact]
    public void Registry_UnknownAlgorithmName_CanStillFallbackByTask()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 2 });
        tensor[0, 0] = 0.1f;
        tensor[0, 1] = 0.9f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "vendor-net-v1",
            TaskKey = "classification",
            Outputs = new[] { new DeepLearningOutputTensor("probabilities", tensor) },
            ConfidenceThreshold = 0.5f
        });

        results.Should().ContainSingle();
        results[0].DataKind.Should().Be(YoloResultDataKind.Classification);
        results[0].ClassId.Should().Be(1);
    }

    [Fact]
    public void Registry_UnsupportedMultiOutputModel_DiagnosticIncludesEveryOutputShape()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var boxes = new DenseTensor<float>(new[] { 1, 5, 4 });
        var logits = new DenseTensor<float>(new[] { 1, 5, 3 });

        Action act = () => registry.Resolve(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "unknown-transformer",
            TaskKey = "unknown",
            Outputs = new[]
            {
                new DeepLearningOutputTensor("pred_boxes", boxes),
                new DeepLearningOutputTensor("class_logits", logits)
            }
        });

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*primary_shape=[1, 5, 4]*output_shapes=[pred_boxes=[1, 5, 4]; class_logits=[1, 5, 3]]*Known postprocessor keys:*");
    }

    [Fact]
    public void DefaultRegistry_HeatmapAnomaly_OutputsMaskAndScaledBoundingBox()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 1, 3, 4 });
        tensor[0, 0, 1, 1] = 0.70f;
        tensor[0, 0, 1, 2] = 0.90f;
        tensor[0, 0, 2, 2] = 0.60f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "heatmap-anomaly",
            TaskKey = "anomaly",
            Outputs = new[] { new DeepLearningOutputTensor("anomaly_map", tensor) },
            Metadata = new Dictionary<string, string> { ["class_id"] = "3" },
            ConfidenceThreshold = 0.5f,
            InputWidth = 40,
            InputHeight = 30
        });

        results.Should().ContainSingle();
        YoloResult result = results[0];
        result.DataKind.Should().Be(YoloResultDataKind.Detection);
        result.ClassId.Should().Be(3);
        result.Confidence.Should().BeApproximately(0.90f, 0.0001f);
        result.CenterX.Should().BeApproximately(20f, 0.0001f);
        result.CenterY.Should().BeApproximately(20f, 0.0001f);
        result.Width.Should().BeApproximately(20f, 0.0001f);
        result.Height.Should().BeApproximately(20f, 0.0001f);
        result.MaskData.Should().NotBeNull();
        result.MaskData!.Rows.Should().Be(3);
        result.MaskData.Cols.Should().Be(4);
        DeepLearningResultSummarizer.MeasureMask(result.MaskData, threshold: 0.5f).Area.Should().Be(3);
    }

    [Fact]
    public void DefaultRegistry_HeatmapAnomaly_ApplySigmoidToLogitHeatmap()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 2, 2 });
        tensor[0, 0] = -4f;
        tensor[0, 1] = 0f;
        tensor[1, 0] = 2f;
        tensor[1, 1] = -2f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "binary-segmentation",
            Outputs = new[] { new DeepLearningOutputTensor("logits", tensor) },
            ConfidenceThreshold = 0.7f,
            ScoreNormalization = DeepLearningScoreNormalization.Sigmoid,
            InputWidth = 20,
            InputHeight = 20
        });

        results.Should().ContainSingle();
        results[0].Confidence.Should().BeGreaterThan(0.88f);
        results[0].CenterX.Should().BeApproximately(5f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(15f, 0.0001f);
        results[0].Width.Should().BeApproximately(10f, 0.0001f);
        results[0].Height.Should().BeApproximately(10f, 0.0001f);
    }

    [Fact]
    public void DefaultRegistry_SemanticSegmentation_NchwProbabilities_OutputsPerClassMasksAndBoxes()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 3, 2, 3 });
        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                tensor[0, 0, row, col] = 0.9f;
                tensor[0, 1, row, col] = 0.05f;
                tensor[0, 2, row, col] = 0.05f;
            }
        }

        tensor[0, 0, 0, 0] = 0.1f;
        tensor[0, 1, 0, 0] = 0.8f;
        tensor[0, 2, 0, 0] = 0.1f;
        tensor[0, 0, 0, 1] = 0.1f;
        tensor[0, 1, 0, 1] = 0.7f;
        tensor[0, 2, 0, 1] = 0.2f;
        tensor[0, 0, 1, 2] = 0.02f;
        tensor[0, 1, 1, 2] = 0.03f;
        tensor[0, 2, 1, 2] = 0.95f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "semantic-segmentation",
            Outputs = new[] { new DeepLearningOutputTensor("segmentation", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["background_class_id"] = "0"
            },
            ConfidenceThreshold = 0.6f,
            InputWidth = 30,
            InputHeight = 20
        });

        results.Should().HaveCount(2);
        results[0].ClassId.Should().Be(2);
        results[0].Confidence.Should().BeApproximately(0.95f, 0.0001f);
        results[0].CenterX.Should().BeApproximately(25f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(15f, 0.0001f);
        results[0].Width.Should().BeApproximately(10f, 0.0001f);
        results[0].Height.Should().BeApproximately(10f, 0.0001f);
        DeepLearningResultSummarizer.MeasureMask(results[0].MaskData, threshold: 0.5f).Area.Should().Be(1);

        results[1].ClassId.Should().Be(1);
        results[1].Confidence.Should().BeApproximately(0.8f, 0.0001f);
        results[1].CenterX.Should().BeApproximately(10f, 0.0001f);
        results[1].CenterY.Should().BeApproximately(5f, 0.0001f);
        results[1].Width.Should().BeApproximately(20f, 0.0001f);
        results[1].Height.Should().BeApproximately(10f, 0.0001f);
        DeepLearningResultSummarizer.MeasureMask(results[1].MaskData, threshold: 0.5f).Area.Should().Be(2);
    }

    [Fact]
    public void DefaultRegistry_SemanticSegmentation_LabelMap_OutputsPerClassMasksAndBoxes()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 2, 3 });
        tensor[0, 0] = 0f;
        tensor[0, 1] = 1f;
        tensor[0, 2] = 1f;
        tensor[1, 0] = 2f;
        tensor[1, 1] = 2f;
        tensor[1, 2] = 0f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "semantic-segmentation",
            Outputs = new[] { new DeepLearningOutputTensor("label_map", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["background_class_id"] = "0"
            },
            ConfidenceThreshold = 0.5f,
            InputWidth = 30,
            InputHeight = 20
        });

        results.Should().HaveCount(2);
        results[0].ClassId.Should().Be(1);
        results[0].Confidence.Should().BeApproximately(1f, 0.0001f);
        results[0].CenterX.Should().BeApproximately(20f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(5f, 0.0001f);
        results[0].Width.Should().BeApproximately(20f, 0.0001f);
        results[0].Height.Should().BeApproximately(10f, 0.0001f);
        DeepLearningResultSummarizer.MeasureMask(results[0].MaskData, threshold: 0.5f).Area.Should().Be(2);

        results[1].ClassId.Should().Be(2);
        results[1].Confidence.Should().BeApproximately(1f, 0.0001f);
        results[1].CenterX.Should().BeApproximately(10f, 0.0001f);
        results[1].CenterY.Should().BeApproximately(15f, 0.0001f);
        results[1].Width.Should().BeApproximately(20f, 0.0001f);
        results[1].Height.Should().BeApproximately(10f, 0.0001f);
        DeepLearningResultSummarizer.MeasureMask(results[1].MaskData, threshold: 0.5f).Area.Should().Be(2);
    }

    [Fact]
    public void DefaultRegistry_SemanticSegmentation_OutputTypeLabelMapHintWithoutAlgorithmKey_ProcessesLabelMap()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 2, 2 });
        tensor[0, 0] = 0f;
        tensor[0, 1] = 3f;
        tensor[1, 0] = 3f;
        tensor[1, 1] = 0f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            Outputs = new[] { new DeepLearningOutputTensor("output", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["output_type"] = "class-map",
                ["background_class_id"] = "0"
            },
            ConfidenceThreshold = 0.5f,
            InputWidth = 20,
            InputHeight = 20
        });

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(3);
        results[0].CenterX.Should().BeApproximately(10f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(10f, 0.0001f);
        results[0].Width.Should().BeApproximately(20f, 0.0001f);
        results[0].Height.Should().BeApproximately(20f, 0.0001f);
        DeepLearningResultSummarizer.MeasureMask(results[0].MaskData, threshold: 0.5f).Area.Should().Be(2);
    }

    [Fact]
    public void DefaultRegistry_DecodedDetection_XyxyTensor_ReturnsSortedDetections()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 3, 6 });
        tensor[0, 0] = 10f;
        tensor[0, 1] = 20f;
        tensor[0, 2] = 50f;
        tensor[0, 3] = 70f;
        tensor[0, 4] = 0.90f;
        tensor[0, 5] = 2f;
        tensor[1, 0] = 5f;
        tensor[1, 1] = 5f;
        tensor[1, 2] = 15f;
        tensor[1, 3] = 15f;
        tensor[1, 4] = 0.40f;
        tensor[1, 5] = 1f;
        tensor[2, 0] = 0f;
        tensor[2, 1] = 0f;
        tensor[2, 2] = 10f;
        tensor[2, 3] = 10f;
        tensor[2, 4] = 0.95f;
        tensor[2, 5] = 1f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "decoded-detection",
            Outputs = new[] { new DeepLearningOutputTensor("detections", tensor) },
            ConfidenceThreshold = 0.5f
        });

        results.Should().HaveCount(2);
        results[0].ClassId.Should().Be(1);
        results[0].Confidence.Should().BeApproximately(0.95f, 0.0001f);
        results[0].CenterX.Should().BeApproximately(5f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(5f, 0.0001f);
        results[1].ClassId.Should().Be(2);
        results[1].CenterX.Should().BeApproximately(30f, 0.0001f);
        results[1].CenterY.Should().BeApproximately(45f, 0.0001f);
        results[1].Width.Should().BeApproximately(40f, 0.0001f);
        results[1].Height.Should().BeApproximately(50f, 0.0001f);
    }

    [Fact]
    public void DefaultRegistry_DecodedDetection_NormalizedXywhLogits_ApplySigmoidScaleAndNms()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 3, 6 });
        tensor[0, 0, 0] = 0.50f;
        tensor[0, 0, 1] = 0.50f;
        tensor[0, 0, 2] = 0.40f;
        tensor[0, 0, 3] = 0.40f;
        tensor[0, 0, 4] = 3.00f;
        tensor[0, 0, 5] = 4f;
        tensor[0, 1, 0] = 0.51f;
        tensor[0, 1, 1] = 0.51f;
        tensor[0, 1, 2] = 0.40f;
        tensor[0, 1, 3] = 0.40f;
        tensor[0, 1, 4] = 2.50f;
        tensor[0, 1, 5] = 4f;
        tensor[0, 2, 0] = 0.20f;
        tensor[0, 2, 1] = 0.20f;
        tensor[0, 2, 2] = 0.10f;
        tensor[0, 2, 3] = 0.10f;
        tensor[0, 2, 4] = 2.00f;
        tensor[0, 2, 5] = 5f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "generic-detection",
            Outputs = new[] { new DeepLearningOutputTensor("detections", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["box_format"] = "xywh",
                ["normalized_boxes"] = "true",
                ["apply_nms"] = "true"
            },
            ConfidenceThreshold = 0.5f,
            IouThreshold = 0.3f,
            ScoreNormalization = DeepLearningScoreNormalization.Sigmoid,
            InputWidth = 100,
            InputHeight = 80
        });

        results.Should().HaveCount(2);
        results[0].ClassId.Should().Be(4);
        results[0].Confidence.Should().BeGreaterThan(0.95f);
        results[0].CenterX.Should().BeApproximately(50f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(40f, 0.0001f);
        results[0].Width.Should().BeApproximately(40f, 0.0001f);
        results[0].Height.Should().BeApproximately(32f, 0.0001f);
        results[1].ClassId.Should().Be(5);
        results[1].CenterX.Should().BeApproximately(20f, 0.0001f);
        results[1].CenterY.Should().BeApproximately(16f, 0.0001f);
    }

    [Fact]
    public void DefaultRegistry_DecodedDetection_RelativeCoordinateUnits_ScaleBoxes()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var tensor = new DenseTensor<float>(new[] { 1, 1, 6 });
        tensor[0, 0, 0] = 0.10f;
        tensor[0, 0, 1] = 0.20f;
        tensor[0, 0, 2] = 0.30f;
        tensor[0, 0, 3] = 0.60f;
        tensor[0, 0, 4] = 0.90f;
        tensor[0, 0, 5] = 2f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "decoded-detection",
            Outputs = new[] { new DeepLearningOutputTensor("detections", tensor) },
            Metadata = new Dictionary<string, string>
            {
                ["box_units"] = "relative"
            },
            ConfidenceThreshold = 0.5f,
            InputWidth = 200,
            InputHeight = 100
        });

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(2);
        results[0].CenterX.Should().BeApproximately(40f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(40f, 0.0001f);
        results[0].Width.Should().BeApproximately(40f, 0.0001f);
        results[0].Height.Should().BeApproximately(40f, 0.0001f);
    }

    [Fact]
    public void DefaultRegistry_DecodedDetection_MultiOutputBoxesScoresClasses_UsesOutputNames()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var boxes = new DenseTensor<float>(new[] { 1, 2, 4 });
        boxes[0, 0, 0] = 0.10f;
        boxes[0, 0, 1] = 0.20f;
        boxes[0, 0, 2] = 0.50f;
        boxes[0, 0, 3] = 0.60f;
        boxes[0, 1, 0] = 0.40f;
        boxes[0, 1, 1] = 0.40f;
        boxes[0, 1, 2] = 0.80f;
        boxes[0, 1, 3] = 0.80f;
        var scores = new DenseTensor<float>(new[] { 1, 2 });
        scores[0, 0] = 0.82f;
        scores[0, 1] = 0.30f;
        var classes = new DenseTensor<float>(new[] { 1, 2 });
        classes[0, 0] = 3f;
        classes[0, 1] = 4f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "ssd",
            Outputs = new[]
            {
                new DeepLearningOutputTensor("detection_scores", scores),
                new DeepLearningOutputTensor("detection_classes", classes),
                new DeepLearningOutputTensor("detection_boxes", boxes)
            },
            Metadata = new Dictionary<string, string>
            {
                ["box_format"] = "yxyx",
                ["normalized_boxes"] = "true"
            },
            ConfidenceThreshold = 0.5f,
            InputWidth = 200,
            InputHeight = 100
        });

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(3);
        results[0].Confidence.Should().BeApproximately(0.82f, 0.0001f);
        results[0].CenterX.Should().BeApproximately(80f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(30f, 0.0001f);
        results[0].Width.Should().BeApproximately(80f, 0.0001f);
        results[0].Height.Should().BeApproximately(40f, 0.0001f);
    }

    [Fact]
    public void DefaultRegistry_DecodedDetection_MultiOutputScoreMatrix_SelectsBestForegroundClass()
    {
        DeepLearningPostprocessorRegistry registry = DeepLearningPostprocessorRegistry.CreateDefault();
        var boxes = new DenseTensor<float>(new[] { 2, 4 });
        boxes[0, 0] = 10f;
        boxes[0, 1] = 20f;
        boxes[0, 2] = 50f;
        boxes[0, 3] = 60f;
        boxes[1, 0] = 70f;
        boxes[1, 1] = 80f;
        boxes[1, 2] = 100f;
        boxes[1, 3] = 120f;
        var logits = new DenseTensor<float>(new[] { 1, 2, 4 });
        logits[0, 0, 0] = 0f;
        logits[0, 0, 1] = 1f;
        logits[0, 0, 2] = 5f;
        logits[0, 0, 3] = 2f;
        logits[0, 1, 0] = 0f;
        logits[0, 1, 1] = 0.2f;
        logits[0, 1, 2] = 0.1f;
        logits[0, 1, 3] = 0.3f;

        IReadOnlyList<YoloResult> results = registry.Process(new DeepLearningPostprocessRequest
        {
            AlgorithmKey = "detr",
            Outputs = new[]
            {
                new DeepLearningOutputTensor("class_logits", logits),
                new DeepLearningOutputTensor("pred_boxes", boxes)
            },
            Metadata = new Dictionary<string, string>
            {
                ["scores_output"] = "class_logits",
                ["box_format"] = "xyxy",
                ["background_class_id"] = "0"
            },
            ConfidenceThreshold = 0.6f,
            ScoreNormalization = DeepLearningScoreNormalization.Softmax
        });

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(2);
        results[0].Confidence.Should().BeGreaterThan(0.92f);
        results[0].CenterX.Should().BeApproximately(30f, 0.0001f);
        results[0].CenterY.Should().BeApproximately(40f, 0.0001f);
        results[0].Width.Should().BeApproximately(40f, 0.0001f);
        results[0].Height.Should().BeApproximately(40f, 0.0001f);
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
            result.SetDetectionData(centerX: 12, centerY: 16, width: 8, height: 6, confidence: 0.99f, classId: 7);
            return new[] { result };
        }
    }
}
