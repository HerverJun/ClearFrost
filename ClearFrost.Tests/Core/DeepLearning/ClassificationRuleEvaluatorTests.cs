using ClearFrost.Core.DeepLearning;
using ClearFrost.Core.Rules;
using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Core.DeepLearning;

public class ClassificationRuleEvaluatorTests
{
    [Fact]
    public void Evaluate_Top1匹配且置信度足够_ReturnsOk()
    {
        ClassificationResultSummary summary = DeepLearningResultSummarizer.CreateClassificationSummary(
            new[] { Classification(0.93f, 0) },
            new[] { "OK" });

        InspectionJudgeResult result = ClassificationRuleEvaluator.Evaluate(RuleSet("OK", 0.8), summary);

        result.IsQualified.Should().BeTrue();
        result.PrimaryReason.Should().Contain("分类匹配");
    }

    [Fact]
    public void Evaluate_Top1不匹配_ReturnsNg()
    {
        ClassificationResultSummary summary = DeepLearningResultSummarizer.CreateClassificationSummary(
            new[] { Classification(0.93f, 1) },
            new[] { "OK", "NG" });

        InspectionJudgeResult result = ClassificationRuleEvaluator.Evaluate(RuleSet("OK", 0.8), summary);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("分类不匹配");
    }

    [Fact]
    public void Evaluate_置信度不足_ReturnsNg()
    {
        ClassificationResultSummary summary = DeepLearningResultSummarizer.CreateClassificationSummary(
            new[] { Classification(0.62f, 0) },
            new[] { "OK" });

        InspectionJudgeResult result = ClassificationRuleEvaluator.Evaluate(RuleSet("OK", 0.8), summary);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("分类置信度不足");
    }

    [Fact]
    public void Evaluate_空分类结果_ReturnsChineseMessage()
    {
        InspectionJudgeResult result = ClassificationRuleEvaluator.Evaluate(
            RuleSet("OK", 0.8),
            new ClassificationResultSummary { Message = "未找到分类结果" });

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Contain("未找到分类结果");
    }

    [Fact]
    public void InspectionDecisionEvaluator_Classification不被Roi过滤()
    {
        var evaluator = new InspectionDecisionEvaluator();
        var ruleSet = RuleSet("OK", 0.8);

        InspectionDecisionResult result = evaluator.Evaluate(new InspectionDecisionRequest
        {
            RuleSet = ruleSet,
            Detections = new[] { Classification(0.93f, 0) },
            Labels = new[] { "OK" },
            ImageWidth = 100,
            ImageHeight = 100,
            Roi = new[] { 0.5f, 0.5f, 0.4f, 0.4f }
        });

        result.Succeeded.Should().BeTrue();
        result.FilteredDetections.Should().ContainSingle();
        result.JudgeResult.IsQualified.Should().BeTrue();
    }

    private static InspectionRuleSet RuleSet(string expectedLabel, double minConfidence)
    {
        return new InspectionRuleSet
        {
            Rules = new List<InspectionRule>
            {
                new InspectionRule
                {
                    Type = InspectionRuleTypes.Classification,
                    ExpectedLabel = expectedLabel,
                    AllowedLabels = new List<string> { expectedLabel },
                    MinConfidence = minConfidence
                }
            }
        };
    }

    private static YoloResult Classification(float confidence, int classId)
    {
        var result = new YoloResult();
        result.SetClassificationData(confidence, classId);
        return result;
    }
}
