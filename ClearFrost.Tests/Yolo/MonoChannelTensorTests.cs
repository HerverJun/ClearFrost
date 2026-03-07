// ============================================================================
// MonoChannelTensorTests.cs - 单通道 Mat Tensor 填充回归测试
// ============================================================================
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClearFrost.Tests.Yolo;

public class MonoChannelTensorTests
{
    private const float Tolerance = 0.004f;
    private static readonly Type DetectorType = typeof(YoloResult).Assembly.GetType("ClearFrost.Yolo.YoloDetector", throwOnError: true)!;

    [Fact]
    public void MonoGrayMat_AllThreeChannels_FilledEqually()
    {
        byte[,] pixels =
        {
            { 128, 128 },
            { 128, 128 }
        };

        using Mat image = CreateMonoMat(pixels);

        float[] noInterpolationBuffer = InvokeMatTensorMethod("MatToTensor_NoInterpolation", image);
        float[] parallelBuffer = InvokeMatTensorMethod("MatToTensor_Parallel", image);

        AssertAllChannelsFilledEqually(noInterpolationBuffer, pixels, image.Rows, image.Cols);
        AssertAllChannelsFilledEqually(parallelBuffer, pixels, image.Rows, image.Cols);
    }

    [Fact]
    public void MonoGrayMat_MatchesBitmapPath_Output()
    {
        byte[,] pixels =
        {
            { 0, 64 },
            { 128, 255 }
        };

        using Mat matImage = CreateMonoMat(pixels);
        using Bitmap bitmapImage = CreateRgbBitmap(pixels);

        float[] matNoInterpolation = InvokeMatTensorMethod("MatToTensor_NoInterpolation", matImage);
        float[] bitmapNoInterpolation = InvokeBitmapTensorMethod("ImageToTensor_NoInterpolation", bitmapImage);
        float[] matParallel = InvokeMatTensorMethod("MatToTensor_Parallel", matImage);
        float[] bitmapParallel = InvokeBitmapTensorMethod("ImageToTensor_Parallel", bitmapImage);

        matNoInterpolation.Should().HaveSameCount(bitmapNoInterpolation);
        matParallel.Should().HaveSameCount(bitmapParallel);

        for (int index = 0; index < matNoInterpolation.Length; index++)
        {
            matNoInterpolation[index].Should().BeApproximately(bitmapNoInterpolation[index], Tolerance);
        }

        for (int index = 0; index < matParallel.Length; index++)
        {
            matParallel[index].Should().BeApproximately(bitmapParallel[index], Tolerance);
        }
    }

    private static void AssertAllChannelsFilledEqually(float[] buffer, byte[,] pixels, int height, int width)
    {
        int channelSize = height * width;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                float expected = pixels[y, x] / 255f;

                buffer[0 * channelSize + index].Should().BeApproximately(expected, Tolerance);
                buffer[1 * channelSize + index].Should().BeApproximately(expected, Tolerance);
                buffer[2 * channelSize + index].Should().BeApproximately(expected, Tolerance);
            }
        }
    }

    private static float[] InvokeMatTensorMethod(string methodName, Mat image)
    {
        object detector = CreateDetector(image.Width, image.Height);
        float[] buffer = new float[3 * image.Width * image.Height];

        InvokePrivateMethod(detector, methodName, image, buffer);

        return buffer;
    }

    private static float[] InvokeBitmapTensorMethod(string methodName, Bitmap image)
    {
        object detector = CreateDetector(image.Width, image.Height);
        float[] buffer = new float[3 * image.Width * image.Height];

        InvokePrivateMethod(detector, methodName, image, buffer);

        return buffer;
    }

    private static object CreateDetector(int width, int height)
    {
        object detector = RuntimeHelpers.GetUninitializedObject(DetectorType);

        SetPrivateField(detector, "_inputTensorInfo", new[] { 1, 3, height, width });
        SetPrivateField(detector, "_tensorWidth", width);
        SetPrivateField(detector, "_tensorHeight", height);
        SetPrivateField(detector, "_inferenceImageWidth", width);
        SetPrivateField(detector, "_inferenceImageHeight", height);
        SetPrivateField(detector, "_scale", 1f);

        return detector;
    }

    private static void InvokePrivateMethod(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = DetectorType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(DetectorType.FullName, methodName);

        method.Invoke(target, arguments);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = DetectorType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(DetectorType.FullName, fieldName);

        field.SetValue(target, value);
    }

    private static Mat CreateMonoMat(byte[,] pixels)
    {
        int height = pixels.GetLength(0);
        int width = pixels.GetLength(1);
        Mat image = new Mat(height, width, MatType.CV_8UC1);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.Set(y, x, pixels[y, x]);
            }
        }

        return image;
    }

    private static Bitmap CreateRgbBitmap(byte[,] pixels)
    {
        int height = pixels.GetLength(0);
        int width = pixels.GetLength(1);
        Bitmap image = new Bitmap(width, height, PixelFormat.Format24bppRgb);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = pixels[y, x];
                image.SetPixel(x, y, Color.FromArgb(value, value, value));
            }
        }

        return image;
    }
}
