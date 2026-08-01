// ============================================================================
// YoloContractUpgradeTests.cs - 标准 YOLO 导出契约升级回归测试
// ============================================================================
using ClearFrost.Yolo;
using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClearFrost.Tests.Yolo;

public class YoloContractUpgradeTests
{
    private const float Tolerance = 0.0001f;
    private static readonly Type DetectorType = typeof(YoloResult).Assembly.GetType("ClearFrost.Yolo.YoloDetector", throwOnError: true)!;

    [Fact]
    public void ParseLabelNames_UltralyticsPythonDict_按索引稳定解析()
    {
        string[] labels = YoloModelContractResolver.ParseLabelNames("{1: 'seal', 0: 'wire', 2: \"cap\"}");

        labels.Should().Equal("wire", "seal", "cap");
    }

    [Fact]
    public void ResolveOutputLayout_Yolo5Objectness_识别为ObjectnessHead()
    {
        YoloOutputLayout layout = YoloModelContractResolver.ResolveOutputLayout(
            new[] { 1, 8, 8400 },
            labelCount: 3,
            task: YoloModelTask.Detect,
            majorVersion: 5);

        layout.Should().Be(YoloOutputLayout.RawYoloObjectness);
    }

    [Theory]
    [MemberData(nameof(DecodedOutputShapeCases))]
    public void ResolveOutputLayout_DecodedDynamicOrBatchless_识别为Decoded(int[] dimensions)
    {
        YoloOutputLayout layout = YoloModelContractResolver.ResolveOutputLayout(
            dimensions,
            labelCount: 80,
            task: YoloModelTask.Detect,
            majorVersion: 8);

        layout.Should().Be(YoloOutputLayout.DecodedXyxy);
    }

    [Fact]
    public void ResolveOutputLayout_ClassifySixClasses_保留Classification()
    {
        YoloOutputLayout layout = YoloModelContractResolver.ResolveOutputLayout(
            new[] { 1, 6 },
            labelCount: 6,
            task: YoloModelTask.Classify,
            majorVersion: 8);

        layout.Should().Be(YoloOutputLayout.Classification);
    }

    [Fact]
    public void CreateDescriptor_请求不兼容任务模式_回落到模型默认模式()
    {
        YoloModelDescriptor descriptor = YoloModelContractResolver.CreateDescriptor(
            modelPath: "detect.onnx",
            inputName: "images",
            inputDimensions: new[] { 1, 3, 640, 640 },
            outputs: new[]
            {
                new YoloOutputDescriptor
                {
                    Name = "output0",
                    Dimensions = new[] { 1, 5, 8400 }
                }
            },
            metadata: new Dictionary<string, string>
            {
                ["task"] = "detect",
                ["names"] = "{0: 'part'}",
                ["version"] = "8.0.0"
            },
            requestedYoloVersion: 0,
            preprocessingMode: YoloPreprocessingMode.StandardLetterBox,
            requestedTaskMode: YoloTaskType.PoseWithKeypoints);

        descriptor.TaskType.Should().Be(YoloModelTask.Detect);
        descriptor.ExecutionTaskMode.Should().Be(YoloTaskType.Detect);
        descriptor.PostprocessProfile.SupportsPose.Should().BeFalse();
    }

    [Fact]
    public void CreateDescriptor_DuplicateCaseMetadataKeys_KeepsFirstValidEntry()
    {
        YoloModelDescriptor descriptor = YoloModelContractResolver.CreateDescriptor(
            modelPath: "detect.onnx",
            inputName: "images",
            inputDimensions: new[] { 1, 3, 640, 640 },
            outputs: new[]
            {
                new YoloOutputDescriptor
                {
                    Name = "output0",
                    Dimensions = new[] { 1, 5, 8400 }
                }
            },
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" names "] = "{0: 'first'}",
                ["NAMES"] = "{0: 'second'}",
                [" "] = "ignored",
                ["version"] = "8.0.0"
            },
            requestedYoloVersion: 0,
            preprocessingMode: YoloPreprocessingMode.StandardLetterBox,
            requestedTaskMode: YoloTaskType.Auto);

        descriptor.Labels.Should().Equal("first");
        descriptor.Metadata.Should().ContainKey("names").WhoseValue.Should().Be("{0: 'first'}");
        descriptor.Metadata.Should().NotContainKey(" ");
    }

    [Fact]
    public void StandardLetterBoxMat_居中填充114并记录ScalePad()
    {
        object detector = CreateDetector(tensorWidth: 4, tensorHeight: 4, imageWidth: 4, imageHeight: 2);
        using Mat image = new Mat(2, 4, MatType.CV_8UC3, new Scalar(255, 255, 255));

        using Mat letterboxed = (Mat)InvokePrivateMethod(detector, "LetterboxResizeMat", image)!;

        letterboxed.Width.Should().Be(4);
        letterboxed.Height.Should().Be(4);
        GetPrivateField<float>(detector, "_scale").Should().BeApproximately(1f, Tolerance);
        GetPrivateField<int>(detector, "_padLeft").Should().Be(0);
        GetPrivateField<int>(detector, "_padTop").Should().Be(1);
        letterboxed.At<Vec3b>(0, 0).Item0.Should().Be(114);
        letterboxed.At<Vec3b>(1, 0).Item0.Should().Be(255);
        letterboxed.At<Vec3b>(3, 0).Item0.Should().Be(114);
    }

    [Fact]
    public void DecodedEndToEndOutput_契约确认后跳过应用层Nms()
    {
        object detector = CreateDetector();
        SetPrivateField(detector, "_yoloVersion", 26);
        SetModelDescriptor(detector, new YoloModelDescriptor
        {
            PostprocessProfile = new YoloPostprocessProfile
            {
                Layout = YoloOutputLayout.DecodedXyxy,
                RequiresApplicationNms = false,
                UsesDecodedBoxes = true
            },
            IsEndToEndNmsFree = true
        });

        var tensor = new DenseTensor<float>(new[] { 1, 3, 6 });
        SetDecodedRow(tensor, index: 0, x1: 10, y1: 10, x2: 110, y2: 110, confidence: 0.90f, classId: 0);
        SetDecodedRow(tensor, index: 1, x1: 12, y1: 12, x2: 112, y2: 112, confidence: 0.80f, classId: 0);
        SetDecodedRow(tensor, index: 2, x1: 200, y1: 200, x2: 260, y2: 260, confidence: 0.70f, classId: 0);

        List<YoloResult> results = InvokePostprocessDetectionOutput(detector, tensor, 0.5f, 0.3f, globalIou: false);

        results.Should().HaveCount(3);
        results.Select(result => result.Confidence).Should().Equal(0.90f, 0.80f, 0.70f);
    }

    [Fact]
    public void ObbNms_旋转IoU低于阈值时不按Aabb误删()
    {
        object detector = CreateDetector();
        var results = new List<YoloResult>
        {
            Obb(centerX: 100, centerY: 100, width: 120, height: 20, confidence: 0.90f, classId: 0, angle: 0),
            Obb(centerX: 100, centerY: 100, width: 120, height: 20, confidence: 0.80f, classId: 0, angle: (float)(Math.PI / 2))
        };

        List<YoloResult> kept = InvokeNms(detector, results, 0.3f, globalIou: false);

        kept.Should().HaveCount(2);
    }

    public static IEnumerable<object[]> DecodedOutputShapeCases()
    {
        yield return new object[] { new[] { 1, -1, 6 } };
        yield return new object[] { new[] { -1, 6 } };
    }

    private static object CreateDetector(int tensorWidth = 640, int tensorHeight = 640, int imageWidth = 640, int imageHeight = 640)
    {
        object detector = RuntimeHelpers.GetUninitializedObject(DetectorType);
        SetPrivateField(detector, "_inputTensorInfo", new[] { 1, 3, tensorHeight, tensorWidth });
        SetPrivateField(detector, "_tensorWidth", tensorWidth);
        SetPrivateField(detector, "_tensorHeight", tensorHeight);
        SetPrivateField(detector, "_inferenceImageWidth", imageWidth);
        SetPrivateField(detector, "_inferenceImageHeight", imageHeight);
        SetPrivateField(detector, "_scale", 1f);
        SetPrivateField(detector, "_padLeft", 0);
        SetPrivateField(detector, "_padTop", 0);
        SetModelDescriptor(detector, new YoloModelDescriptor());
        return detector;
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = DetectorType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(DetectorType.FullName, methodName);

        return method.Invoke(target, arguments);
    }

    private static List<YoloResult> InvokePostprocessDetectionOutput(
        object detector,
        DenseTensor<float> tensor,
        float confidence,
        float iouThreshold,
        bool globalIou)
    {
        return (List<YoloResult>)InvokePrivateMethod(
            detector,
            "PostprocessDetectionOutput",
            tensor,
            confidence,
            iouThreshold,
            globalIou)!;
    }

    private static List<YoloResult> InvokeNms(object detector, List<YoloResult> results, float iouThreshold, bool globalIou)
    {
        return (List<YoloResult>)InvokePrivateMethod(detector, "NmsFilter", results, iouThreshold, globalIou)!;
    }

    private static void SetDecodedRow(
        DenseTensor<float> tensor,
        int index,
        float x1,
        float y1,
        float x2,
        float y2,
        float confidence,
        int classId)
    {
        tensor[0, index, 0] = x1;
        tensor[0, index, 1] = y1;
        tensor[0, index, 2] = x2;
        tensor[0, index, 3] = y2;
        tensor[0, index, 4] = confidence;
        tensor[0, index, 5] = classId;
    }

    private static YoloResult Obb(float centerX, float centerY, float width, float height, float confidence, int classId, float angle)
    {
        var result = new YoloResult();
        result.SetObbData(centerX, centerY, width, height, confidence, classId, angle);
        return result;
    }

    private static void SetModelDescriptor(object target, YoloModelDescriptor descriptor)
    {
        FieldInfo field = DetectorType.GetField("<ModelDescriptor>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(DetectorType.FullName, "ModelDescriptor backing field");

        field.SetValue(target, descriptor);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = DetectorType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(DetectorType.FullName, fieldName);

        return (T)field.GetValue(target)!;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = DetectorType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(DetectorType.FullName, fieldName);

        field.SetValue(target, value);
    }
}
