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

    [Fact]
    public void InspectionDecisionEvaluator_Detect按Roi过滤()
    {
        var evaluator = new InspectionDecisionEvaluator();

        InspectionDecisionResult result = evaluator.Evaluate(new InspectionDecisionRequest
        {
            RuleSet = CountRuleSet("part", 1),
            Detections = new[]
            {
                Detection(0, 20, 20),
                Detection(0, 90, 90)
            },
            Labels = new[] { "part" },
            ImageWidth = 100,
            ImageHeight = 100,
            Roi = new[] { 0f, 0f, 0.5f, 0.5f }
        });

        result.Succeeded.Should().BeTrue();
        result.FilteredDetections.Should().ContainSingle()
            .Which.CenterX.Should().Be(20);
        result.JudgeResult.IsQualified.Should().BeTrue();
    }

    [Fact]
    public void InspectionDecisionEvaluator_Multitask坐标结果按Roi过滤但分类保留()
    {
        var evaluator = new InspectionDecisionEvaluator();
        YoloResult segmentation = Detection(2, 90, 90);
        segmentation.MaskData = new OpenCvSharp.Mat(1, 1, OpenCvSharp.MatType.CV_32F, OpenCvSharp.Scalar.All(1));
        var obb = new YoloResult();
        obb.SetObbData(90, 90, 10, 10, 0.95f, 3, 15);
        YoloResult pose = Detection(4, 90, 90);
        pose.KeyPoints = new[] { new PosePoint { X = 90, Y = 90, Score = 0.9f } };

        try
        {
            InspectionDecisionResult result = evaluator.Evaluate(new InspectionDecisionRequest
            {
                RuleSet = CountRuleSet("part", 1),
                Detections = new[]
                {
                    Classification(0.93f, 0),
                    Detection(1, 20, 20),
                    Detection(1, 90, 90),
                    segmentation,
                    obb,
                    pose
                },
                Labels = new[] { "OK", "part", "glue", "screw", "person" },
                ImageWidth = 100,
                ImageHeight = 100,
                Roi = new[] { 0f, 0f, 0.5f, 0.5f }
            });

            result.Succeeded.Should().BeTrue();
            result.FilteredDetections.Should().HaveCount(2);
            result.FilteredDetections.Should().Contain(item => item.DataKind == YoloResultDataKind.Classification);
            result.FilteredDetections.Should().Contain(item => item.ClassId == 1 && item.CenterX == 20);
            result.JudgeResult.IsQualified.Should().BeTrue();
        }
        finally
        {
            segmentation.Dispose();
        }
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

    private static InspectionRuleSet CountRuleSet(string label, int count)
    {
        return new InspectionRuleSet
        {
            Rules = new List<InspectionRule>
            {
                new InspectionRule
                {
                    Type = InspectionRuleTypes.Count,
                    Label = label,
                    Count = count
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

    private static YoloResult Detection(int classId, float centerX, float centerY)
    {
        var result = new YoloResult();
        result.SetDetectionData(centerX, centerY, 10, 10, 0.95f, classId);
        return result;
    }
}
