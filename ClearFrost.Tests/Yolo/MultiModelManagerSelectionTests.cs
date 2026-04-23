using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class MultiModelManagerSelectionTests
{
    [Fact]
    public void IsTargetSatisfied_TargetCountRequiresExactMatch()
    {
        var labels = new[] { "screw", "body" };
        var results = new List<YoloResult>
        {
            Detection(0),
            Detection(0),
            Detection(1)
        };

        MultiModelManager.CountTargetLabelHits(results, labels, "screw").Should().Be(2);
        MultiModelManager.IsTargetSatisfied(results, labels, "screw", 2).Should().BeTrue();
        MultiModelManager.IsTargetSatisfied(results, labels, "screw", 4).Should().BeFalse();
    }

    [Fact]
    public void IsTargetSatisfied_ExpectedZeroAllowsNoTargetHits()
    {
        var labels = new[] { "screw", "body" };
        var results = new List<YoloResult>
        {
            Detection(1)
        };

        MultiModelManager.CountTargetLabelHits(results, labels, "screw").Should().Be(0);
        MultiModelManager.IsTargetSatisfied(results, labels, "screw", 0).Should().BeTrue();
    }

    [Fact]
    public void CountTargetLabelHits_IgnoresOutOfRangeClassIds()
    {
        var labels = new[] { "screw" };
        var results = new List<YoloResult>
        {
            Detection(0),
            Detection(-1),
            Detection(9)
        };

        MultiModelManager.CountTargetLabelHits(results, labels, "screw").Should().Be(1);
    }

    private static YoloResult Detection(int classId)
    {
        var result = new YoloResult();
        result.SetDetectionData(10, 10, 5, 5, 0.9f, classId);
        return result;
    }
}
