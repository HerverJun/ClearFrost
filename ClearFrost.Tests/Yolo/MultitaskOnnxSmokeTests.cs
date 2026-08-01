using ClearFrost.Core.DeepLearning;
using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class MultitaskOnnxSmokeTests
{
    private const long MaxSmokeModelBytes = 1_000_000;

    public static IEnumerable<object[]> DescriptorCases()
    {
        yield return new object[]
        {
            "classification_smoke.onnx",
            YoloModelTask.Classify,
            YoloOutputLayout.Classification,
            new[] { new YoloOutputDescriptor { Name = "output0", Dimensions = new[] { 1, 2 } } },
            false,
            false,
            false,
            "图像分类"
        };
        yield return new object[]
        {
            "segmentation_smoke.onnx",
            YoloModelTask.Segment,
            YoloOutputLayout.SegmentRaw,
            new[]
            {
                new YoloOutputDescriptor { Name = "output0", Dimensions = new[] { 1, 38, 1 } },
                new YoloOutputDescriptor { Name = "output1", Dimensions = new[] { 1, 32, 4, 4 } }
            },
            true,
            false,
            false,
            "分割检测"
        };
        yield return new object[]
        {
            "obb_smoke.onnx",
            YoloModelTask.Obb,
            YoloOutputLayout.ObbRaw,
            new[] { new YoloOutputDescriptor { Name = "output0", Dimensions = new[] { 1, 7, 1 } } },
            false,
            false,
            true,
            "旋转框检测"
        };
        yield return new object[]
        {
            "pose_smoke.onnx",
            YoloModelTask.Pose,
            YoloOutputLayout.PoseRaw,
            new[] { new YoloOutputDescriptor { Name = "output0", Dimensions = new[] { 1, 11, 1 } } },
            false,
            true,
            false,
            "姿态/关键点"
        };
    }

    [Theory]
    [MemberData(nameof(DescriptorCases))]
    public void SyntheticProbeDescriptors_覆盖ClassificationSegmentationObbPose(
        string modelName,
        YoloModelTask expectedTask,
        YoloOutputLayout expectedLayout,
        IReadOnlyList<YoloOutputDescriptor> outputs,
        bool supportsMask,
        bool supportsPose,
        bool supportsObb,
        string expectedTaskText)
    {
        YoloModelDescriptor descriptor = YoloModelContractResolver.CreateDescriptor(
            modelPath: modelName,
            inputName: "images",
            inputDimensions: new[] { 1, 3, 8, 8 },
            outputs: outputs,
            metadata: new Dictionary<string, string>
            {
                ["task"] = expectedTask switch
                {
                    YoloModelTask.Classify => "classify",
                    YoloModelTask.Segment => "segment",
                    YoloModelTask.Obb => "obb",
                    YoloModelTask.Pose => "pose",
                    _ => "detect"
                },
                ["names"] = "{0: 'OK', 1: 'NG'}",
                ["version"] = "8.0.0"
            },
            requestedYoloVersion: 0,
            preprocessingMode: YoloPreprocessingMode.StandardLetterBox,
            requestedTaskMode: YoloTaskType.Auto);

        descriptor.IsSupported.Should().BeTrue();
        descriptor.TaskType.Should().Be(expectedTask);
        descriptor.PostprocessProfile.Layout.Should().Be(expectedLayout);
        descriptor.PostprocessProfile.SupportsMask.Should().Be(supportsMask);
        descriptor.PostprocessProfile.SupportsPose.Should().Be(supportsPose);
        descriptor.PostprocessProfile.SupportsObb.Should().Be(supportsObb);
        DeepLearningModelTaskSummary.FromDescriptor(descriptor).TaskTypeText.Should().Be(expectedTaskText);
    }

    [Fact(Skip = "NOT_VERIFIED: generated multitask ONNX fixtures are unavailable.")]
    [Trait("Lane", "ExternalModel")]
    public void OptionalGeneratedOnnxProbeSmoke_模型缺失时记录Skipped()
    {
        string root = FindRepositoryRoot();
        string modelDir = Path.Combine(root, "ClearFrost.Tests", "TestAssets", "Models");
        string skippedPath = Path.Combine(modelDir, "ONNX_GENERATION_SKIPPED.txt");
        var expected = new Dictionary<string, (YoloModelTask Task, YoloOutputLayout Layout)>
        {
            ["classification_smoke.onnx"] = (YoloModelTask.Classify, YoloOutputLayout.Classification),
            ["segmentation_smoke.onnx"] = (YoloModelTask.Segment, YoloOutputLayout.SegmentRaw),
            ["obb_smoke.onnx"] = (YoloModelTask.Obb, YoloOutputLayout.ObbRaw),
            ["pose_smoke.onnx"] = (YoloModelTask.Pose, YoloOutputLayout.PoseRaw)
        };

        bool anyModelExists = expected.Keys.Any(name => File.Exists(Path.Combine(modelDir, name)));
        if (!anyModelExists)
        {
            File.Exists(skippedPath).Should().BeTrue("本机缺少 onnx 包时应保留可审计的跳过标记");
            File.ReadAllText(skippedPath).Should().Contain("ONNX_GENERATION_SKIPPED");
            return;
        }

        foreach (KeyValuePair<string, (YoloModelTask Task, YoloOutputLayout Layout)> item in expected)
        {
            string path = Path.Combine(modelDir, item.Key);
            File.Exists(path).Should().BeTrue($"{item.Key} 应由 tools/create_multitask_smoke_models.py 生成");
            new FileInfo(path).Length.Should().BeLessThan(MaxSmokeModelBytes);

            YoloExportProbeReport report = YoloExportProbe.Inspect(path);
            report.Descriptor.TaskType.Should().Be(item.Value.Task);
            report.Descriptor.PostprocessProfile.Layout.Should().Be(item.Value.Layout);
            report.Descriptor.IsSupported.Should().BeTrue();
        }
    }

    [Fact(Skip = "NOT_VERIFIED: generated multitask ONNX fixtures are unavailable.")]
    [Trait("Lane", "ExternalModel")]
    public void SmokeModelGenerator_缺少Onnx包时保留跳过记录()
    {
        string root = FindRepositoryRoot();
        string scriptPath = Path.Combine(root, "tools", "create_multitask_smoke_models.py");
        string modelDir = Path.Combine(root, "ClearFrost.Tests", "TestAssets", "Models");
        string skippedPath = Path.Combine(modelDir, "ONNX_GENERATION_SKIPPED.txt");
        File.Exists(scriptPath).Should().BeTrue();

        string[] modelFiles = Directory.Exists(modelDir)
            ? Directory.GetFiles(modelDir, "*_smoke.onnx")
            : Array.Empty<string>();

        if (modelFiles.Length == 0)
        {
            File.Exists(skippedPath).Should().BeTrue();
            File.ReadAllText(skippedPath).Should().Contain("No internet install attempted.");
            return;
        }

        modelFiles.Should().OnlyContain(path => new FileInfo(path).Length < MaxSmokeModelBytes);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClearFrost.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ClearFrost.sln.");
    }
}
