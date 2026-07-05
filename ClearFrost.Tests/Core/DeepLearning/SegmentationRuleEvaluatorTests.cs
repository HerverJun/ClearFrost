using ClearFrost.Core.DeepLearning;
using ClearFrost.Core.Rules;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Core.DeepLearning;

public class SegmentationRuleEvaluatorTests
{
    [Fact]
    public void Evaluate_LabelAreaCoverageAndCount满足_ReturnsOk()
    {
        SegmentationResultSummary summary = BuildSummary(maskValue: 1f, label: "glue", confidence: 0.92f);
        InspectionRuleSet ruleSet = RuleSet("glue", minArea: 3, maxArea: 5, minCoverage: 0.70, maxCoverage: 1.0, count: 1);

        InspectionJudgeResult result = SegmentationRuleEvaluator.Evaluate(ruleSet, summary);

        result.IsQualified.Should().BeTrue();
        result.PrimaryReason.Should().Contain("分割规则 OK");
    }

    [Fact]
    public void Evaluate_Label过滤无匹配_ReturnsNg()
    {
        SegmentationResultSummary summary = BuildSummary(maskValue: 1f, label: "glue", confidence: 0.92f);

        InspectionJudgeResult result = SegmentationRuleEvaluator.Evaluate(RuleSet("seal", minArea: 1), summary);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("未找到分割类别 seal");
    }

    [Fact]
    public void Evaluate_MinArea不满足_ReturnsNg()
    {
        SegmentationResultSummary summary = BuildSummary(maskValue: 1f, label: "glue", confidence: 0.92f);

        InspectionJudgeResult result = SegmentationRuleEvaluator.Evaluate(RuleSet("glue", minArea: 10), summary);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("面积不足");
    }

    [Fact]
    public void Evaluate_MaxCoverage不满足_ReturnsNg()
    {
        SegmentationResultSummary summary = BuildSummary(maskValue: 1f, label: "glue", confidence: 0.92f);

        InspectionJudgeResult result = SegmentationRuleEvaluator.Evaluate(RuleSet("glue", maxCoverage: 0.5), summary);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("覆盖率超限");
    }

    [Fact]
    public void Evaluate_无MaskData_ReturnsNgButDoesNotThrow()
    {
        SegmentationResultSummary summary = DeepLearningResultSummarizer.CreateSegmentationSummary(
            new[] { Detection(0, 0.9f, null) },
            new[] { "glue" });

        InspectionJudgeResult result = SegmentationRuleEvaluator.Evaluate(RuleSet("glue", minArea: 1), summary);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("缺少 MaskData");
    }

    [Fact]
    public void Evaluate_空分割结果_ReturnsChineseMessage()
    {
        InspectionJudgeResult result = SegmentationRuleEvaluator.Evaluate(
            RuleSet("glue", minArea: 1),
            new SegmentationResultSummary { Message = "未找到分割结果" });

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("未找到分割结果");
    }

    private static SegmentationResultSummary BuildSummary(string label, float confidence, float maskValue)
    {
        using Mat mask = new Mat(2, 2, MatType.CV_32F, Scalar.All(maskValue));
        YoloResult result = Detection(0, confidence, mask.Clone());
        try
        {
            return DeepLearningResultSummarizer.CreateSegmentationSummary(new[] { result }, new[] { label });
        }
        finally
        {
            result.Dispose();
        }
    }

    private static InspectionRuleSet RuleSet(
        string label,
        double minArea = 0,
        double maxArea = 0,
        double minCoverage = 0,
        double maxCoverage = 0,
        int count = 0)
    {
        return new InspectionRuleSet
        {
            Rules = new List<InspectionRule>
            {
                new InspectionRule
                {
                    Type = InspectionRuleTypes.SegmentationArea,
                    Label = label,
                    Operator = InspectionRuleOperators.Equal,
                    Count = count,
                    MinConfidence = 0.5,
                    MinArea = minArea,
                    MaxArea = maxArea,
                    MinCoverage = minCoverage,
                    MaxCoverage = maxCoverage
                }
            }
        };
    }

    private static YoloResult Detection(int classId, float confidence, Mat? mask)
    {
        var result = new YoloResult();
        result.SetDetectionData(20, 20, 10, 10, confidence, classId);
        result.MaskData = mask;
        return result;
    }
}
