// ============================================================================
// YoloResultTests.cs - YoloResult 数据类型单元测试
// ============================================================================
using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class YoloResultTests
{
    [Fact]
    public void BoundingBox_计算正确()
    {
        // Arrange
        var result = new YoloResult
        {
            CenterX = 100,
            CenterY = 100,
            Width = 50,
            Height = 40
        };

        // Act & Assert
        result.Left.Should().Be(75);  // 100 - 50/2
        result.Top.Should().Be(80);   // 100 - 40/2
        result.Right.Should().Be(125); // 100 + 50/2
        result.Bottom.Should().Be(120); // 100 + 40/2
    }

    [Fact]
    public void Area_计算正确()
    {
        var result = new YoloResult
        {
            Width = 100,
            Height = 50
        };

        result.Area.Should().Be(5000);
    }

    [Fact]
    public void BasicData_设置后属性正确更新()
    {
        // Arrange
        var result = new YoloResult();

        // Act
        result.BasicData = new float[] { 50, 60, 100, 80, 0.95f, 2 };

        // Assert
        result.CenterX.Should().Be(50);
        result.CenterY.Should().Be(60);
        result.Width.Should().Be(100);
        result.Height.Should().Be(80);
        result.Confidence.Should().BeApproximately(0.95f, 0.001f);
        result.ClassId.Should().Be(2);
    }

    [Fact]
    public void BasicData_包含OBB角度时正确解析()
    {
        var result = new YoloResult();

        result.BasicData = new float[] { 50, 60, 100, 80, 0.9f, 1, 45.5f };

        result.Angle.Should().BeApproximately(45.5f, 0.001f);
    }

    [Fact]
    public void Dispose_正确释放资源()
    {
        var result = new YoloResult();

        // 模拟有 MaskData (实际使用中会是 OpenCV Mat)
        // result.MaskData = new OpenCvSharp.Mat();

        // Act
        result.Dispose();

        // Assert - 多次 Dispose 不应抛异常
        var act = () => result.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 0, 100, 100, 10000)] // 正常情况 (100*100=10000)
    [InlineData(50, 50, 0, 0, 0)]       // 零尺寸
    [InlineData(-10, -10, 20, 30, 600)] // 负坐标（允许）
    public void Area_各种输入情况(float cx, float cy, float w, float h, float expectedArea)
    {
        var result = new YoloResult
        {
            CenterX = cx,
            CenterY = cy,
            Width = w,
            Height = h
        };

        result.Area.Should().Be(expectedArea);
    }
}
