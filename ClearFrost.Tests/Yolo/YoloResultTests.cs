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
        var result = new YoloResult
        {
            CenterX = 100,
            CenterY = 100,
            Width = 50,
            Height = 40
        };

        result.Left.Should().Be(75);
        result.Top.Should().Be(80);
        result.Right.Should().Be(125);
        result.Bottom.Should().Be(120);
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
    public void BasicData_设置检测数据后字段正确()
    {
        var result = new YoloResult();

        result.BasicData = new float[] { 50, 60, 100, 80, 0.95f, 2 };

        result.DataKind.Should().Be(YoloResultDataKind.Detection);
        result.CenterX.Should().Be(50);
        result.CenterY.Should().Be(60);
        result.Width.Should().Be(100);
        result.Height.Should().Be(80);
        result.Confidence.Should().BeApproximately(0.95f, 0.001f);
        result.ClassId.Should().Be(2);
    }

    [Fact]
    public void BasicData_包含Obb角度时正确解析()
    {
        var result = new YoloResult();

        result.BasicData = new float[] { 50, 60, 100, 80, 0.9f, 1, 45.5f };

        result.DataKind.Should().Be(YoloResultDataKind.Obb);
        result.Angle.Should().BeApproximately(45.5f, 0.001f);
    }

    [Fact]
    public void BasicData_分类路径_正确写入与取回()
    {
        var result = new YoloResult();

        result.BasicData = new float[] { 0.91f, 3 };

        result.DataKind.Should().Be(YoloResultDataKind.Classification);
        result.Confidence.Should().BeApproximately(0.91f, 0.001f);
        result.ClassId.Should().Be(3);
        result.BasicData.Should().Equal(new float[] { 0.91f, 3f });
    }

    [Fact]
    public void SetDetectionData_兼容属性_返回6元素()
    {
        var result = new YoloResult();

        result.SetDetectionData(10, 20, 30, 40, 0.8f, 5);

        result.DataKind.Should().Be(YoloResultDataKind.Detection);
        result.BasicData.Should().Equal(new float[] { 10f, 20f, 30f, 40f, 0.8f, 5f });
    }

    [Fact]
    public void SetObbData_兼容属性_返回7元素()
    {
        var result = new YoloResult();

        result.SetObbData(10, 20, 30, 40, 0.8f, 5, 1.57f);

        result.DataKind.Should().Be(YoloResultDataKind.Obb);
        result.BasicData.Should().Equal(new float[] { 10f, 20f, 30f, 40f, 0.8f, 5f, 1.57f });
    }

    [Fact]
    public void Dispose_正确释放资源()
    {
        var result = new YoloResult();

        result.Dispose();

        var act = () => result.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 0, 100, 100, 10000)]
    [InlineData(50, 50, 0, 0, 0)]
    [InlineData(-10, -10, 20, 30, 600)]
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
