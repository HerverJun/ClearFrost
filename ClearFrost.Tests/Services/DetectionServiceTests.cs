using ClearFrost.Services;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

public class DetectionServiceTests
{
    [Fact]
    public async Task DetectAsync_模型未加载_返回显式错误且不合格()
    {
        using var image = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(128));
        using var service = new DetectionService(useGpu: false);

        var result = await service.DetectAsync(image, 0.5f, 0.3f);

        result.HasError.Should().BeTrue();
        result.IsQualified.Should().BeFalse();
        result.ErrorMessage.Should().Contain("模型未加载");
        result.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectAsync_空图像_返回显式错误且不合格()
    {
        using var image = new Mat();
        using var service = new DetectionService(useGpu: false);

        var result = await service.DetectAsync(image, 0.5f, 0.3f);

        result.HasError.Should().BeTrue();
        result.IsQualified.Should().BeFalse();
        result.ErrorMessage.Should().Contain("输入图像为空");
    }
}
