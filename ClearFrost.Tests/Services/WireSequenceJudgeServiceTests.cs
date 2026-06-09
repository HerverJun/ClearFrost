using ClearFrost.Config;
using ClearFrost.Services;
using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class WireSequenceJudgeServiceTests
{
    private static readonly string[] Labels =
    {
        "Wire_Brown",
        "Wire_Black",
        "Wire_Blue",
        "backpack",
    };

    [Fact]
    public void Evaluate_左到右顺序匹配_返回OK()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "Wire_Brown,Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterX",
            WireSequenceDirection = "LeftToRight",
            WireSequenceMinConfidence = 0.5,
        };
        var detections = new[]
        {
            Detection(1, 120, 20, 0.96f),
            Detection(2, 220, 20, 0.94f),
            Detection(0, 20, 20, 0.98f),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeTrue();
        result.ActualOrder.Should().Equal("Wire_Brown", "Wire_Black", "Wire_Blue");
        result.SortedCount.Should().Be(3);
    }

    [Fact]
    public void Evaluate_顺序不一致_返回NG()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "Wire_Brown,Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterX",
            WireSequenceDirection = "LeftToRight",
        };
        var detections = new[]
        {
            Detection(1, 20, 20),
            Detection(0, 120, 20),
            Detection(2, 220, 20),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeFalse();
        result.ActualOrder.Should().Equal("Wire_Black", "Wire_Brown", "Wire_Blue");
        result.Message.Should().Contain("Order mismatch");
    }

    [Fact]
    public void Evaluate_缺失标签且不允许缺失_返回NG()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "Wire_Brown,Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterX",
            WireSequenceDirection = "LeftToRight",
        };
        var detections = new[]
        {
            Detection(0, 20, 20),
            Detection(2, 220, 20),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeFalse();
        result.MissingLabels.Should().Equal("Wire_Black");
        result.Message.Should().Contain("Missing labels");
    }

    [Fact]
    public void Evaluate_重复标签且不允许重复_返回NG()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "Wire_Brown,Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterX",
            WireSequenceDirection = "LeftToRight",
        };
        var detections = new[]
        {
            Detection(0, 20, 20),
            Detection(0, 80, 20),
            Detection(1, 140, 20),
            Detection(2, 220, 20),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeFalse();
        result.DuplicateLabels.Should().Equal("Wire_Brown");
        result.Message.Should().Contain("Duplicate labels");
    }

    [Fact]
    public void Evaluate_从上到下排序_按CenterY比较()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "Wire_Brown,Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterY",
            WireSequenceDirection = "TopToBottom",
        };
        var detections = new[]
        {
            Detection(2, 80, 220),
            Detection(0, 80, 20),
            Detection(1, 80, 120),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeTrue();
        result.ActualOrder.Should().Equal("Wire_Brown", "Wire_Black", "Wire_Blue");
    }

    [Fact]
    public void Evaluate_顺序规则忽略非期望标签_返回OK()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "Wire_Brown,Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterX",
            WireSequenceDirection = "LeftToRight",
        };
        var detections = new[]
        {
            Detection(0, 20, 20),
            Detection(3, 80, 20),
            Detection(1, 120, 20),
            Detection(2, 220, 20),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeTrue();
        result.FilteredCount.Should().Be(4);
        result.SortedCount.Should().Be(3);
        result.ActualOrder.Should().Equal("Wire_Brown", "Wire_Black", "Wire_Blue");
    }

    [Fact]
    public void Evaluate_置信度过滤后数量不足_返回NG()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "Wire_Brown,Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterX",
            WireSequenceDirection = "LeftToRight",
            WireSequenceMinConfidence = 0.8,
        };
        var detections = new[]
        {
            Detection(0, 20, 20, 0.95f),
            Detection(1, 120, 20, 0.70f),
            Detection(2, 220, 20, 0.94f),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeFalse();
        result.FilteredCount.Should().Be(2);
        result.MissingLabels.Should().Equal("Wire_Black");
    }

    [Fact]
    public void Evaluate_期望标签为空_返回NG()
    {
        var config = new AppConfig
        {
            WireSequenceExpectedLabels = "",
            WireSequenceSortBy = "CenterX",
            WireSequenceDirection = "LeftToRight",
        };
        var detections = new[]
        {
            Detection(0, 20, 20),
        };

        WireSequenceJudgeResult result = WireSequenceJudgeService.Evaluate(detections, Labels, config);

        result.IsMatch.Should().BeFalse();
        result.Message.Should().Contain("Expected labels");
    }

    private static YoloResult Detection(int classId, float centerX, float centerY, float confidence = 0.95f)
    {
        return new YoloResult
        {
            ClassId = classId,
            CenterX = centerX,
            CenterY = centerY,
            Width = 18,
            Height = 40,
            Confidence = confidence,
        };
    }
}
