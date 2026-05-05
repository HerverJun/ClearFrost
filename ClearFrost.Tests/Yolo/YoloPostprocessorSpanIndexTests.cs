// ============================================================================
// YoloPostprocessorSpanIndexTests.cs - YOLO 后处理 Span 索引回归测试
// ============================================================================
using ClearFrost.Yolo;
using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClearFrost.Tests.Yolo;

public class YoloPostprocessorSpanIndexTests
{
    private const float Tolerance = 0.0001f;
    private static readonly Type DetectorType = typeof(YoloResult).Assembly.GetType("ClearFrost.Yolo.YoloDetector", throwOnError: true)!;

    [Fact]
    public void Yolo8Detect_MidLayout_按通道和锚点读取坐标类别()
    {
        object detector = CreateDetector();
        DenseTensor<float> tensor = CreateTensor(channelCount: 7, anchorCount: 8);

        SetBox(tensor, anchor: 3, centerX: 10, centerY: 20, width: 30, height: 40);
        tensor[0, 4, 3] = 0.12f;
        tensor[0, 5, 3] = 0.91f;
        tensor[0, 6, 3] = 0.44f;

        List<YoloResult> results = InvokeFilter("FilterConfidence_Yolo8_9_11_Detect", detector, tensor, 0.5f);

        results.Should().ContainSingle();
        results[0].CenterX.Should().BeApproximately(10, Tolerance);
        results[0].CenterY.Should().BeApproximately(20, Tolerance);
        results[0].Width.Should().BeApproximately(30, Tolerance);
        results[0].Height.Should().BeApproximately(40, Tolerance);
        results[0].Confidence.Should().BeApproximately(0.91f, Tolerance);
        results[0].ClassId.Should().Be(1);
    }

    [Fact]
    public void Yolo5Detect_MidLayout_低目标置信度会跳过锚点()
    {
        object detector = CreateDetector();
        DenseTensor<float> tensor = CreateTensor(channelCount: 8, anchorCount: 9);

        SetBox(tensor, anchor: 2, centerX: 11, centerY: 21, width: 31, height: 41);
        tensor[0, 4, 2] = 0.40f;
        tensor[0, 7, 2] = 0.99f;

        SetBox(tensor, anchor: 6, centerX: 12, centerY: 22, width: 32, height: 42);
        tensor[0, 4, 6] = 0.70f;
        tensor[0, 5, 6] = 0.35f;
        tensor[0, 6, 6] = 0.88f;
        tensor[0, 7, 6] = 0.71f;

        List<YoloResult> results = InvokeFilter("FilterConfidence_Yolo5_Detect", detector, tensor, 0.5f);

        results.Should().ContainSingle();
        results[0].CenterX.Should().BeApproximately(12, Tolerance);
        results[0].CenterY.Should().BeApproximately(22, Tolerance);
        results[0].Width.Should().BeApproximately(32, Tolerance);
        results[0].Height.Should().BeApproximately(42, Tolerance);
        results[0].Confidence.Should().BeApproximately(0.88f, Tolerance);
        results[0].ClassId.Should().Be(1);
    }

    [Fact]
    public void Yolo8Segment_MidLayout_按尾部通道读取Mask系数()
    {
        object detector = CreateDetector(segWidth: 32);
        DenseTensor<float> tensor = CreateTensor(channelCount: 38, anchorCount: 40);

        SetBox(tensor, anchor: 7, centerX: 13, centerY: 23, width: 33, height: 43);
        tensor[0, 4, 7] = 0.22f;
        tensor[0, 5, 7] = 0.86f;
        for (int maskIndex = 0; maskIndex < 32; maskIndex++)
        {
            tensor[0, 6 + maskIndex, 7] = 1000 + maskIndex;
        }

        List<YoloResult> results = InvokeFilter("FilterConfidence_Yolo8_11_Segment", detector, tensor, 0.5f);

        try
        {
            results.Should().ContainSingle();
            results[0].ClassId.Should().Be(1);
            results[0].Confidence.Should().BeApproximately(0.86f, Tolerance);
            results[0].MaskData.Should().NotBeNull();
            for (int maskIndex = 0; maskIndex < 32; maskIndex++)
            {
                results[0].MaskData!.At<float>(0, maskIndex).Should().BeApproximately(1000 + maskIndex, Tolerance);
            }
        }
        finally
        {
            DisposeResults(results);
        }
    }

    [Fact]
    public void Pose_MidLayout_按姿态通道读取关键点()
    {
        object detector = CreateDetector(poseWidth: 6);
        DenseTensor<float> tensor = CreateTensor(channelCount: 11, anchorCount: 12);

        SetBox(tensor, anchor: 4, centerX: 14, centerY: 24, width: 34, height: 44);
        tensor[0, 4, 4] = 0.93f;
        tensor[0, 5, 4] = 101;
        tensor[0, 6, 4] = 201;
        tensor[0, 7, 4] = 0.81f;
        tensor[0, 8, 4] = 102;
        tensor[0, 9, 4] = 202;
        tensor[0, 10, 4] = 0.82f;

        List<YoloResult> results = InvokeFilter("FilterConfidence_Pose", detector, tensor, 0.5f);

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(0);
        results[0].KeyPoints.Should().HaveCount(2);
        results[0].KeyPoints[0].X.Should().BeApproximately(101, Tolerance);
        results[0].KeyPoints[0].Y.Should().BeApproximately(201, Tolerance);
        results[0].KeyPoints[0].Score.Should().BeApproximately(0.81f, Tolerance);
        results[0].KeyPoints[1].X.Should().BeApproximately(102, Tolerance);
        results[0].KeyPoints[1].Y.Should().BeApproximately(202, Tolerance);
        results[0].KeyPoints[1].Score.Should().BeApproximately(0.82f, Tolerance);
    }

    [Fact]
    public void Obb_MidLayout_按最后通道读取角度()
    {
        object detector = CreateDetector();
        DenseTensor<float> tensor = CreateTensor(channelCount: 8, anchorCount: 10);

        SetBox(tensor, anchor: 5, centerX: 15, centerY: 25, width: 35, height: 45);
        tensor[0, 4, 5] = 0.30f;
        tensor[0, 5, 5] = 0.94f;
        tensor[0, 6, 5] = 0.41f;
        tensor[0, 7, 5] = 1.57f;

        List<YoloResult> results = InvokeFilter("FilterConfidence_Obb", detector, tensor, 0.5f);

        results.Should().ContainSingle();
        results[0].ClassId.Should().Be(1);
        results[0].Confidence.Should().BeApproximately(0.94f, Tolerance);
        results[0].Angle.Should().BeApproximately(1.57f, Tolerance);
    }

    private static object CreateDetector(int segWidth = 0, int poseWidth = 0)
    {
        object detector = RuntimeHelpers.GetUninitializedObject(DetectorType);
        SetPrivateField(detector, "_segWidth", segWidth);
        SetPrivateField(detector, "_poseWidth", poseWidth);
        return detector;
    }

    private static DenseTensor<float> CreateTensor(int channelCount, int anchorCount)
    {
        return new DenseTensor<float>(new[] { 1, channelCount, anchorCount });
    }

    private static void SetBox(DenseTensor<float> tensor, int anchor, float centerX, float centerY, float width, float height)
    {
        tensor[0, 0, anchor] = centerX;
        tensor[0, 1, anchor] = centerY;
        tensor[0, 2, anchor] = width;
        tensor[0, 3, anchor] = height;
    }

    private static List<YoloResult> InvokeFilter(string methodName, object detector, Tensor<float> tensor, float confidence)
    {
        MethodInfo method = DetectorType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(DetectorType.FullName, methodName);

        return (List<YoloResult>)method.Invoke(detector, new object[] { tensor, confidence })!;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = DetectorType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(DetectorType.FullName, fieldName);

        field.SetValue(target, value);
    }

    private static void DisposeResults(IEnumerable<YoloResult> results)
    {
        foreach (YoloResult result in results)
        {
            result.Dispose();
        }
    }
}
