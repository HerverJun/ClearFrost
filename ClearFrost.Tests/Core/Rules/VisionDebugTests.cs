using ClearFrost.Config;
using ClearFrost.Core.Rules;
using ClearFrost.Services;
using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Rules;

public class VisionDebugTests
{
    private static readonly string[] Labels =
    {
        "screw",
        "body",
        "Wire_Brown",
        "Wire_Black",
        "part"
    };

    [Fact]
    public void EvaluateWithDebug_Roi返回过滤前过滤后和过滤掉的框()
    {
        var evaluator = new InspectionDecisionEvaluator();
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.Count,
            Label = "part",
            Count = 2
        });

        VisionDebugSnapshot snapshot = evaluator.EvaluateWithDebug(new InspectionDecisionRequest
        {
            RuleSet = ruleSet,
            Detections = new[]
            {
                Detection(4, 20, 20),
                Detection(4, 40, 60),
                Detection(4, 80, 20)
            },
            Labels = Labels,
            ImageWidth = 100,
            ImageHeight = 100,
            Roi = new[] { 0f, 0f, 0.5f, 1f },
            ModelName = "debug.onnx",
            Confidence = 0.5f,
            IouThreshold = 0.3f
        });

        snapshot.AllDetections.Should().HaveCount(3);
        snapshot.RoiIncludedDetections.Should().HaveCount(2);
        snapshot.RoiExcludedDetections.Should().ContainSingle()
            .Which.CenterX.Should().Be(80);
        snapshot.CategoryStats.Should().ContainSingle(stat => stat.Label == "part")
            .Which.RoiExcludedCount.Should().Be(1);
        snapshot.FinalOk.Should().BeTrue();
    }

    [Fact]
    public void EvaluateWithDebug_规则解释包含数量顺序位置的期望实际原因()
    {
        var evaluator = new InspectionDecisionEvaluator();
        var ruleSet = RuleSet(
            new InspectionRule
            {
                Name = "数量",
                Type = InspectionRuleTypes.Count,
                Label = "screw",
                Count = 2
            },
            new InspectionRule
            {
                Name = "线序",
                Type = InspectionRuleTypes.OrderedLabels,
                ExpectedLabels = new List<string> { "Wire_Brown", "Wire_Black" },
                Direction = "LeftToRight",
                ExpectedCount = 2
            },
            new InspectionRule
            {
                Name = "位置",
                Type = InspectionRuleTypes.RelativePosition,
                SubjectLabel = "screw",
                ReferenceLabel = "body",
                Relation = InspectionRuleRelations.RightOf
            });

        VisionDebugSnapshot snapshot = evaluator.Explain(new InspectionDecisionRequest
        {
            RuleSet = ruleSet,
            Detections = new[]
            {
                Detection(0, 20, 20),
                Detection(1, 80, 20),
                Detection(2, 120, 20)
            },
            Labels = Labels,
            ImageWidth = 200,
            ImageHeight = 100
        });

        snapshot.FinalOk.Should().BeFalse();
        snapshot.RuleResults.Should().HaveCount(3);
        snapshot.RuleResults.Should().Contain(result =>
            result.RuleType == InspectionRuleTypes.Count &&
            result.Expected.Contains("screw") &&
            result.Actual == "1" &&
            result.Reason.Contains("数量规则 NG") &&
            result.AssociatedBoxIndexes.Contains(1));
        snapshot.RuleResults.Should().Contain(result =>
            result.RuleType == InspectionRuleTypes.OrderedLabels &&
            result.Expected.Contains("Wire_Brown") &&
            result.Actual.Contains("Wire_Brown") &&
            result.Reason.Contains("缺失 Wire_Black") &&
            result.AssociationSummary.Contains("目标序号"));
        snapshot.RuleResults.Should().Contain(result =>
            result.RuleType == InspectionRuleTypes.RelativePosition &&
            result.Expected.Contains("screw 在 body 右侧") &&
            result.Actual.Contains("间距") &&
            result.Reason.Contains("位置规则 NG") &&
            result.AssociationSummary.Contains("最佳匹配"));
        snapshot.PrimaryFailureReason.Should().Contain("数量规则 NG");
    }

    [Fact]
    public void 调试参数未保存时不污染AppConfig_保存时才写入()
    {
        var config = new AppConfig
        {
            Confidence = 0.4f,
            IouThreshold = 0.2f,
            TargetLabel = "part",
            TargetCount = 1,
            InspectionRuleSetJson = InspectionRuleSetSerializer.Serialize(
                InspectionRuleSetSerializer.FromLegacyTarget("part", 1))
        };
        var parameters = new VisionDebugRunParameters
        {
            Confidence = 0.9f,
            IouThreshold = 0.7f,
            TargetLabel = "screw",
            TargetCount = 4
        };

        VisionDebugParameterService.ResolveRuleSet(config, parameters, out string trialRuleSetJson);

        config.Confidence.Should().Be(0.4f);
        config.IouThreshold.Should().Be(0.2f);
        config.TargetLabel.Should().Be("part");
        config.TargetCount.Should().Be(1);
        config.InspectionRuleSetJson.Should().NotBe(trialRuleSetJson);

        VisionDebugParameterService.ApplySavedParameters(config, parameters);

        config.Confidence.Should().Be(0.9f);
        config.IouThreshold.Should().Be(0.7f);
        config.TargetLabel.Should().Be("screw");
        config.TargetCount.Should().Be(4);
        config.GetInspectionRuleSet().Rules.Should().ContainSingle()
            .Which.Label.Should().Be("screw");
    }

    [Fact]
    public void 场景模板生成正确规则()
    {
        InspectionRuleSet screw = InspectionRuleSetTemplates.Create(InspectionRuleSetTemplateIds.ScrewCount);
        screw.Rules.Should().ContainSingle(rule =>
            rule.Type == InspectionRuleTypes.Count &&
            rule.Label == "screw" &&
            rule.Count == 4);

        InspectionRuleSet classification = InspectionRuleSetTemplates.Create(InspectionRuleSetTemplateIds.ClassificationJudge, new[] { "OK", "NG" });
        classification.Rules.Should().ContainSingle(rule =>
            rule.Type == InspectionRuleTypes.Classification &&
            rule.ExpectedLabel == "OK" &&
            rule.MinConfidence == 0.8);

        InspectionRuleSet segmentation = InspectionRuleSetTemplates.Create(InspectionRuleSetTemplateIds.SegmentationArea, new[] { "glue" });
        segmentation.Rules.Should().ContainSingle(rule =>
            rule.Type == InspectionRuleTypes.SegmentationArea &&
            rule.Label == "glue" &&
            rule.MinArea > 0);

        InspectionRuleSet remote = InspectionRuleSetTemplates.Create(InspectionRuleSetTemplateIds.RemoteMissingPart);
        remote.Rules.Should().OnlyContain(rule =>
            rule.Type == InspectionRuleTypes.Count &&
            rule.Operator == InspectionRuleOperators.GreaterThanOrEqual &&
            rule.Count == 1);

        InspectionRuleSet wire = InspectionRuleSetTemplates.Create(InspectionRuleSetTemplateIds.WireSequence);
        wire.Rules.Should().ContainSingle(rule =>
            rule.Type == InspectionRuleTypes.OrderedLabels &&
            rule.ExpectedLabels.SequenceEqual(new[] { "Wire_Brown", "Wire_Black", "Wire_Blue" }));

        InspectionRuleSet relative = InspectionRuleSetTemplates.Create(
            InspectionRuleSetTemplateIds.RelativePosition,
            new[] { "cap", "body" });
        relative.Rules.Should().ContainSingle(rule =>
            rule.Type == InspectionRuleTypes.RelativePosition &&
            rule.SubjectLabel == "cap" &&
            rule.ReferenceLabel == "body");
    }

    [Fact]
    public void 参数对比摘要_列出生产与试运行差异()
    {
        var config = new AppConfig
        {
            Confidence = 0.45f,
            IouThreshold = 0.25f,
            TargetLabel = "screw",
            TargetCount = 4,
            InspectionRuleSetJson = InspectionRuleSetSerializer.Serialize(
                InspectionRuleSetSerializer.FromLegacyTarget("screw", 4))
        };
        var parameters = new VisionDebugRunParameters
        {
            Confidence = 0.72f,
            IouThreshold = 0.35f,
            TargetLabel = "screw",
            TargetCount = 3,
            RoiEnabled = false,
            PreprocessingMode = YoloPreprocessingMode.IndustrialFast
        };
        InspectionRuleSet trialRuleSet = VisionDebugParameterService.ResolveRuleSet(config, parameters, out string trialRuleSetJson);

        VisionDebugParameterComparison comparison = VisionDebugParameterService.BuildParameterComparison(
            config,
            parameters,
            trialRuleSetJson,
            productionRoiEnabled: true);

        trialRuleSet.Rules.Should().ContainSingle().Which.Count.Should().Be(3);
        comparison.HasDifferences.Should().BeTrue();
        comparison.Items.Should().Contain(item => item.Field == "confidence" && item.ProductionValue == "0.45" && item.TrialValue == "0.72" && item.IsDifferent);
        comparison.Items.Should().Contain(item => item.Field == "iou" && item.IsDifferent);
        comparison.Items.Should().Contain(item => item.Field == "targetCount" && item.ProductionValue == "4" && item.TrialValue == "3" && item.IsDifferent);
        comparison.Items.Should().Contain(item => item.Field == "preprocessingMode" && item.TrialValue == "IndustrialFast" && item.IsDifferent);
        comparison.Items.Should().Contain(item => item.Field == "roiEnabled" && item.ProductionValue == "启用" && item.TrialValue == "关闭" && item.IsDifferent);
    }

    private static InspectionRuleSet RuleSet(params InspectionRule[] rules)
    {
        return new InspectionRuleSet
        {
            Rules = rules.ToList()
        };
    }

    private static YoloResult Detection(int classId, float centerX, float centerY)
    {
        return new YoloResult
        {
            ClassId = classId,
            CenterX = centerX,
            CenterY = centerY,
            Width = 10,
            Height = 10,
            Confidence = 0.95f
        };
    }
}
