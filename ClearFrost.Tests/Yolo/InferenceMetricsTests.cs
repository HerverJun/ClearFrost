// ============================================================================
// InferenceMetricsTests.cs - 推理性能指标单元测试
// ============================================================================
using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class InferenceMetricsTests
{
    [Fact]
    public void TotalMs_正确计算各阶段总和()
    {
        // Arrange
        var metrics = new InferenceMetrics
        {
            PreprocessMs = 5.5,
            InferenceMs = 20.3,
            PostprocessMs = 3.2
        };

        // Act & Assert
        metrics.TotalMs.Should().BeApproximately(29.0, 0.001);
    }

    [Fact]
    public void FPS_从总时间正确计算()
    {
        var metrics = new InferenceMetrics
        {
            PreprocessMs = 10,
            InferenceMs = 30,
            PostprocessMs = 10
        };

        // Total = 50ms → FPS = 1000/50 = 20
        metrics.FPS.Should().BeApproximately(20.0, 0.001);
    }

    [Fact]
    public void FPS_总时间为零时返回零()
    {
        var metrics = new InferenceMetrics
        {
            PreprocessMs = 0,
            InferenceMs = 0,
            PostprocessMs = 0
        };

        metrics.FPS.Should().Be(0);
    }

    [Fact]
    public void ToString_包含所有关键信息()
    {
        var metrics = new InferenceMetrics
        {
            PreprocessMs = 5.12,
            InferenceMs = 25.34,
            PostprocessMs = 2.56,
            DetectionCount = 4
        };

        var str = metrics.ToString();

        str.Should().Contain("Preprocess:");
        str.Should().Contain("Inference:");
        str.Should().Contain("Postprocess:");
        str.Should().Contain("Total:");
        str.Should().Contain("FPS:");
        str.Should().Contain("Detections: 4");
    }

    [Theory]
    [InlineData(100, 10)]   // 100ms → 10 FPS
    [InlineData(50, 20)]    // 50ms → 20 FPS
    [InlineData(16.67, 60)] // ~16.67ms → ~60 FPS
    public void FPS_不同总时间场景(double totalMs, double expectedFps)
    {
        var metrics = new InferenceMetrics
        {
            InferenceMs = totalMs  // 只设置推理时间
        };

        metrics.FPS.Should().BeApproximately(expectedFps, 0.5);
    }
}
