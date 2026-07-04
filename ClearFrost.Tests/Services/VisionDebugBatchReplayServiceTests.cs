using ClearFrost.Core.Rules;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class VisionDebugBatchReplayServiceTests
{
    [Fact]
    public void BuildSummary_统计变化缺图渲染图和失败原因()
    {
        var items = new[]
        {
            new VisionDebugBatchReplayItem
            {
                RecordId = 1,
                Status = "completed",
                OldIsQualified = false,
                NewIsQualified = true
            },
            new VisionDebugBatchReplayItem
            {
                RecordId = 2,
                Status = "completed",
                OldIsQualified = true,
                NewIsQualified = false,
                UsedRenderedImage = true,
                ImageWarning = "使用了渲染图，结果仅供参考"
            },
            new VisionDebugBatchReplayItem
            {
                RecordId = 3,
                Status = "missingImage",
                ImageMissing = true,
                FailureReason = "原图、追溯图和渲染图均不存在"
            },
            new VisionDebugBatchReplayItem
            {
                RecordId = 4,
                Status = "failed",
                FailureReason = "图片读取失败"
            }
        };

        VisionDebugBatchReplaySummary summary = VisionDebugBatchReplayService.BuildSummary(items, requestedLimit: 80, effectiveLimit: 50);

        summary.TotalRecords.Should().Be(4);
        summary.CompletedCount.Should().Be(2);
        summary.ChangedCount.Should().Be(2);
        summary.NgToOkCount.Should().Be(1);
        summary.OkToNgCount.Should().Be(1);
        summary.MissingImageCount.Should().Be(1);
        summary.FailedCount.Should().Be(1);
        summary.RenderedFallbackCount.Should().Be(1);
        summary.FailureReasonStats.Should().ContainKey("原图、追溯图和渲染图均不存在").WhoseValue.Should().Be(1);
        summary.FailureReasonStats.Should().ContainKey("图片读取失败").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void Resolve_原图缺失时使用渲染图并返回明确提示()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(VisionDebugBatchReplayServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string missingOriginal = Path.Combine(tempDir, "missing.jpg");
        string rendered = Path.Combine(tempDir, "Rendered", "missing_rendered.jpg");
        string traceRendered = Path.Combine(tempDir, "Rendered", "trace_rendered.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(rendered)!);
        File.WriteAllText(rendered, "fake image");
        File.WriteAllText(traceRendered, "fake trace image");

        try
        {
            var record = new DetectionRecord
            {
                ImagePath = missingOriginal,
                RenderedImagePath = rendered
            };

            VisionDebugHistoryImageResolution resolution = VisionDebugHistoryImageResolver.Resolve(
                record,
                path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null);

            resolution.Succeeded.Should().BeTrue();
            resolution.ImagePath.Should().Be(rendered);
            resolution.UsedRenderedImage.Should().BeTrue();
            resolution.SourceKind.Should().Be("Rendered");
            resolution.Warning.Should().Be("使用了渲染图，结果仅供参考");

            VisionDebugHistoryImageResolution traceResolution = VisionDebugHistoryImageResolver.Resolve(
                new DetectionRecord
                {
                    ImagePath = missingOriginal,
                    TraceImagePath = traceRendered
                },
                path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null);

            traceResolution.ImagePath.Should().Be(traceRendered);
            traceResolution.UsedRenderedImage.Should().BeTrue();
            traceResolution.Warning.Should().Be("使用了渲染图，结果仅供参考");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ClampLimit_默认限制不超过50()
    {
        VisionDebugBatchReplayService.ClampLimit(null).Should().Be(20);
        VisionDebugBatchReplayService.ClampLimit(0).Should().Be(1);
        VisionDebugBatchReplayService.ClampLimit(500).Should().Be(50);
    }
}
