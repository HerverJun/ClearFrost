using ClearFrost.Core.DeepLearning;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Core.DeepLearning;

public class DeepLearningResultSummarizerTests
{
    [Fact]
    public void ModelTaskSummary_覆盖DetectClassificationSegmentPoseObb()
    {
        DeepLearningModelTaskSummary detect = DeepLearningModelTaskSummary.FromDescriptor(Descriptor(YoloModelTask.Detect, YoloOutputLayout.RawYoloNoObjectness));
        DeepLearningModelTaskSummary classify = DeepLearningModelTaskSummary.FromDescriptor(Descriptor(YoloModelTask.Classify, YoloOutputLayout.Classification));
        DeepLearningModelTaskSummary segment = DeepLearningModelTaskSummary.FromDescriptor(Descriptor(YoloModelTask.Segment, YoloOutputLayout.SegmentRaw));
        DeepLearningModelTaskSummary pose = DeepLearningModelTaskSummary.FromDescriptor(Descriptor(YoloModelTask.Pose, YoloOutputLayout.PoseRaw));
        DeepLearningModelTaskSummary obb = DeepLearningModelTaskSummary.FromDescriptor(Descriptor(YoloModelTask.Obb, YoloOutputLayout.ObbRaw));

        detect.TaskTypeText.Should().Be("目标检测");
        classify.TaskTypeText.Should().Be("图像分类");
        segment.SupportsMask.Should().BeTrue();
        pose.SupportsPose.Should().BeTrue();
        obb.SupportsObb.Should().BeTrue();
        classify.LabelCount.Should().Be(3);
        classify.RequiresApplicationNms.Should().BeFalse();
    }

    [Fact]
    public void ModelTaskSummary_不支持布局返回中文提示()
    {
        DeepLearningModelTaskSummary summary = DeepLearningModelTaskSummary.FromDescriptor(new YoloModelDescriptor
        {
            ModelPath = "bad.onnx",
            TaskType = YoloModelTask.Detect,
            ExecutionTaskMode = YoloTaskType.Detect,
            PostprocessProfile = new YoloPostprocessProfile { Layout = YoloOutputLayout.Unknown },
            IsSupported = false
        });

        summary.IsSupported.Should().BeFalse();
        summary.SupportMessage.Should().Be(DeepLearningTaskText.UnsupportedModelMessage);
    }

    [Fact]
    public void ClassificationSummary_TopK按置信度排序并使用Labels()
    {
        var results = new[]
        {
            Classification(0.62f, 0),
            Classification(0.93f, 1),
            Classification(0.81f, 2)
        };

        ClassificationResultSummary summary = DeepLearningResultSummarizer.CreateClassificationSummary(results, new[] { "NG", "OK", "REWORK" }, topK: 2);

        summary.Top1Label.Should().Be("OK");
        summary.Top1Confidence.Should().BeApproximately(0.93f, 0.001f);
        summary.TopK.Select(item => item.Label).Should().Equal("OK", "REWORK");
    }

    [Fact]
    public void ClassificationSummary_缺少Labels时使用ClassIdFallback()
    {
        ClassificationResultSummary summary = DeepLearningResultSummarizer.CreateClassificationSummary(
            new[] { Classification(0.77f, 5) },
            Array.Empty<string>());

        summary.Top1Label.Should().Be("Class_5");
    }

    [Fact]
    public void ClassificationSummary_空结果返回中文提示()
    {
        ClassificationResultSummary summary = DeepLearningResultSummarizer.CreateClassificationSummary(Array.Empty<YoloResult>(), Array.Empty<string>());

        summary.TopK.Should().BeEmpty();
        summary.Message.Should().Contain("未找到分类结果");
    }

    [Fact]
    public void SegmentationSummary_统计Mask面积和局部覆盖率()
    {
        using Mat mask = new Mat(2, 3, MatType.CV_32F, new float[]
        {
            0.6f, 0.2f, 0.7f,
            1.0f, 0.0f, 0.51f
        });
        var result = Detection(0, confidence: 0.9f);
        result.MaskData = mask.Clone();

        SegmentationResultSummary summary = DeepLearningResultSummarizer.CreateSegmentationSummary(new[] { result }, new[] { "glue" });

        summary.InstanceCount.Should().Be(1);
        summary.Instances[0].HasMask.Should().BeTrue();
        summary.Instances[0].MaskArea.Should().Be(4);
        summary.Instances[0].MaskCoverage.Should().BeApproximately(4d / 6d, 0.0001);
        summary.CoverageBasis.Should().Be("MaskDataPixels");
        result.Dispose();
    }

    [Fact]
    public void SegmentationSummary_无MaskData不崩溃()
    {
        SegmentationResultSummary summary = DeepLearningResultSummarizer.CreateSegmentationSummary(
            new[] { Detection(0, confidence: 0.8f) },
            new[] { "glue" });

        summary.Instances.Should().ContainSingle();
        summary.Instances[0].HasMask.Should().BeFalse();
        summary.Instances[0].MaskArea.Should().Be(0);
    }

    [Fact]
    public void ObbSummary_保留Angle()
    {
        var result = new YoloResult();
        result.SetObbData(10, 20, 30, 40, 0.91f, 0, 12.5f);

        ObbResultSummary summary = DeepLearningResultSummarizer.CreateObbSummary(new[] { result }, new[] { "screw" });

        summary.Instances.Should().ContainSingle();
        summary.Instances[0].Angle.Should().Be(12.5f);
        summary.Instances[0].Message.Should().Contain("角度 12.5");
    }

    [Fact]
    public void PoseSummary_统计关键点置信度()
    {
        var result = Detection(0, confidence: 0.88f);
        result.KeyPoints = new[]
        {
            new PosePoint { X = 1, Y = 2, Score = 0.9f },
            new PosePoint { X = 3, Y = 4, Score = 0.2f },
            new PosePoint { X = 5, Y = 6, Score = 0.7f }
        };

        PoseResultSummary summary = DeepLearningResultSummarizer.CreatePoseSummary(new[] { result }, new[] { "person" }, lowConfidenceThreshold: 0.5f);

        summary.InstanceCount.Should().Be(1);
        summary.TotalKeyPointCount.Should().Be(3);
        summary.MaxKeyPointConfidence.Should().BeApproximately(0.9f, 0.001f);
        summary.MinKeyPointConfidence.Should().BeApproximately(0.2f, 0.001f);
        summary.LowConfidenceKeyPointCount.Should().Be(1);
    }

    [Fact]
    public void TaskAwareLogSummary_Classification使用Top1而不是Found()
    {
        string summary = DeepLearningResultSummarizer.CreateTaskAwareLogSummary(
            new[] { Classification(0.93f, 0) },
            new[] { "OK" },
            isQualified: true,
            judgementReason: "分类匹配");

        summary.Should().Contain("分类结果");
        summary.Should().Contain("Top1=OK");
        summary.Should().Contain("判定=OK");
        summary.Should().NotContain("Found");
    }

    [Fact]
    public void TaskAwareLogSummary_Segmentation显示面积覆盖率()
    {
        using Mat mask = new Mat(2, 2, MatType.CV_32F, new float[]
        {
            1.0f, 0.7f,
            0.0f, 0.1f
        });
        var result = Detection(0, confidence: 0.91f);
        result.MaskData = mask.Clone();

        string summary = DeepLearningResultSummarizer.CreateTaskAwareLogSummary(
            new[] { result },
            new[] { "glue" },
            isQualified: true);

        summary.Should().Contain("分割结果");
        summary.Should().Contain("面积 2");
        summary.Should().Contain("覆盖率 50.0%");
        result.Dispose();
    }

    [Fact]
    public void TaskAwareLogSummary_Obb显示角度()
    {
        var result = new YoloResult();
        result.SetObbData(10, 20, 30, 40, 0.91f, 0, 12.5f);

        string summary = DeepLearningResultSummarizer.CreateTaskAwareLogSummary(
            new[] { result },
            new[] { "screw" },
            isQualified: true);

        summary.Should().Contain("旋转框结果");
        summary.Should().Contain("角度 12.5°");
    }

    [Fact]
    public void TaskAwareLogSummary_Pose显示关键点统计()
    {
        var result = Detection(0, confidence: 0.88f);
        result.KeyPoints = new[]
        {
            new PosePoint { X = 1, Y = 2, Score = 0.9f },
            new PosePoint { X = 3, Y = 4, Score = 0.2f }
        };

        string summary = DeepLearningResultSummarizer.CreateTaskAwareLogSummary(
            new[] { result },
            new[] { "person" },
            isQualified: false,
            judgementReason: "低置信度");

        summary.Should().Contain("姿态结果");
        summary.Should().Contain("关键点 2 个");
        summary.Should().Contain("低置信度 1 个");
        summary.Should().Contain("判定=NG");
    }

    private static YoloModelDescriptor Descriptor(YoloModelTask task, YoloOutputLayout layout)
    {
        return new YoloModelDescriptor
        {
            ModelPath = $"{task}.onnx",
            Labels = new[] { "OK", "NG", "REWORK" },
            TaskType = task,
            ExecutionTaskMode = task switch
            {
                YoloModelTask.Classify => YoloTaskType.Classify,
                YoloModelTask.Segment => YoloTaskType.SegmentWithMask,
                YoloModelTask.Pose => YoloTaskType.PoseWithKeypoints,
                YoloModelTask.Obb => YoloTaskType.Obb,
                _ => YoloTaskType.Detect
            },
            PreprocessProfile = new YoloPreprocessProfile
            {
                InputWidth = 320,
                InputHeight = 320,
                Mode = YoloPreprocessingMode.StandardLetterBox
            },
            PostprocessProfile = new YoloPostprocessProfile
            {
                Layout = layout,
                RequiresApplicationNms = task != YoloModelTask.Classify,
                SupportsMask = task == YoloModelTask.Segment,
                SupportsPose = task == YoloModelTask.Pose,
                SupportsObb = task == YoloModelTask.Obb
            },
            IsSupported = true
        };
    }

    private static YoloResult Classification(float confidence, int classId)
    {
        var result = new YoloResult();
        result.SetClassificationData(confidence, classId);
        return result;
    }

    private static YoloResult Detection(int classId, float confidence)
    {
        var result = new YoloResult();
        result.SetDetectionData(10, 10, 8, 8, confidence, classId);
        return result;
    }
}
