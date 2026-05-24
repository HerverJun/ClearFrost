using ClearFrost.Config;
using ClearFrost.Core.Rules;
using ClearFrost.Yolo;
using FluentAssertions;
using System.Text.Json;

namespace ClearFrost.Tests.Core.Rules;

public class InspectionRuleEngineTests
{
    private static readonly string[] Labels =
    {
        "screw",
        "Wire_Brown",
        "Wire_Black",
        "Wire_Blue",
        "body",
        "person",
        "dog",
        "backpack",
        "potted plant",
    };

    [Fact]
    public void Evaluate_CountEqual_ReturnsOk()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.Count,
            Label = "screw",
            Operator = InspectionRuleOperators.Equal,
            Count = 2
        });
        var detections = new[]
        {
            Detection(0, 20, 20),
            Detection(0, 80, 20),
            Detection(4, 140, 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeTrue();
        result.RuleResults.Should().ContainSingle().Which.Actual.Should().Be("2");
    }

    [Fact]
    public void Evaluate_CountGreaterThanWithConfidenceFilter_ReturnsNg()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.Count,
            Label = "screw",
            Operator = InspectionRuleOperators.GreaterThanOrEqual,
            Count = 2,
            MinConfidence = 0.8
        });
        var detections = new[]
        {
            Detection(0, 20, 20, 0.95f),
            Detection(0, 80, 20, 0.70f),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.RuleResults[0].Actual.Should().Be("1");
        result.RuleResults[0].Message.Should().Contain("期望 screw 数量 >= 2，实际只检测到 1 个");
    }

    [Fact]
    public void Evaluate_CountEqualMissing_SummaryExplainsExpectedAndActual()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.Count,
            Label = "screw",
            Operator = InspectionRuleOperators.Equal,
            Count = 4
        });
        var detections = new[]
        {
            Detection(0, 20, 20),
            Detection(0, 80, 20),
            Detection(0, 140, 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Be("数量规则 NG：期望 screw 数量 = 4，实际只检测到 3 个");
        result.Summary.Should().Contain("启用规则 1 条，失败 1 条");
        result.Summary.Should().Contain("期望 screw 数量 = 4，实际只检测到 3 个");
        result.Details.Should().ContainSingle().Which.Should().Be(result.PrimaryReason);
    }

    [Fact]
    public void Evaluate_OrderedLabelsByY_ReturnsOk()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.OrderedLabels,
            ExpectedLabels = new List<string> { "Wire_Brown", "Wire_Black", "Wire_Blue" },
            SortBy = "CenterY",
            Direction = "TopToBottom"
        });
        var detections = new[]
        {
            Detection(3, 80, 220),
            Detection(1, 80, 20),
            Detection(2, 80, 120),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeTrue();
        result.RuleResults[0].Actual.Should().Be("Wire_Brown -> Wire_Black -> Wire_Blue");
    }

    [Fact]
    public void Evaluate_OrderedLabelsIgnoresUnexpectedLabelsInRoi_ReturnsOk()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.OrderedLabels,
            ExpectedLabels = new List<string> { "person", "dog" },
            SortBy = "CenterY",
            Direction = "TopToBottom",
            ExpectedCount = 2
        });
        var detections = new[]
        {
            Detection(7, 571, 436, 0.62f, width: 113, height: 162),
            Detection(5, 591, 521, 0.91f, width: 163, height: 475),
            Detection(6, 666, 682, 0.80f, width: 104, height: 218),
            Detection(8, 441, 780, 0.81f, width: 206, height: 239),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeTrue();
        result.RuleResults[0].Actual.Should().Be("person -> dog");
    }

    [Fact]
    public void Evaluate_OrderedLabelsOnlyUnexpectedLabels_ReturnsNg()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.OrderedLabels,
            ExpectedLabels = new List<string> { "person", "dog" },
            SortBy = "CenterY",
            Direction = "TopToBottom",
            ExpectedCount = 2
        });
        var detections = new[]
        {
            Detection(7, 571, 436, 0.62f),
            Detection(8, 441, 780, 0.81f),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.RuleResults[0].Actual.Should().Be("<empty>");
        result.RuleResults[0].Message.Should().Contain("未检测到期望标签");
    }

    [Fact]
    public void Evaluate_OrderedLabelsMismatch_ReturnsNg()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.OrderedLabels,
            ExpectedLabels = new List<string> { "Wire_Brown", "Wire_Black", "Wire_Blue" },
            SortBy = "CenterX",
            Direction = "LeftToRight"
        });
        var detections = new[]
        {
            Detection(2, 20, 20),
            Detection(1, 120, 20),
            Detection(3, 220, 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.RuleResults[0].Message.Should().Contain("顺序不符");
    }

    [Fact]
    public void Evaluate_OrderedLabelsDuplicateAndMissing_ReturnsReadableReasons()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.OrderedLabels,
            ExpectedLabels = new List<string> { "Wire_Brown", "Wire_Black", "Wire_Blue" },
            SortBy = "CenterX",
            Direction = "LeftToRight"
        });
        var detections = new[]
        {
            Detection(1, 20, 20),
            Detection(1, 120, 20),
            Detection(3, 220, 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.RuleResults[0].Message.Should().Contain("缺失 Wire_Black");
        result.RuleResults[0].Message.Should().Contain("重复 Wire_Brown");
        result.RuleResults[0].Message.Should().Contain("顺序不符");
    }

    [Fact]
    public void Evaluate_RelativePositionLeftOf_ReturnsOk()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.RelativePosition,
            SubjectLabel = "screw",
            ReferenceLabel = "body",
            Relation = InspectionRuleRelations.LeftOf,
            MinDistance = 5
        });
        var detections = new[]
        {
            Detection(0, 20, 20, width: 10),
            Detection(4, 80, 20, width: 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RelativePositionWrongSide_ReturnsNg()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.RelativePosition,
            SubjectLabel = "screw",
            ReferenceLabel = "body",
            Relation = InspectionRuleRelations.RightOf
        });
        var detections = new[]
        {
            Detection(0, 20, 20, width: 10),
            Detection(4, 80, 20, width: 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.RuleResults[0].Message.Should().Contain("位置规则 NG");
        result.RuleResults[0].Message.Should().Contain("期望 screw 在 body 右侧");
    }

    [Fact]
    public void Evaluate_MultipleFailures_PrimaryReasonUsesFirstFailureAndDetailsKeepAllFailures()
    {
        var ruleSet = RuleSet(
            new InspectionRule
            {
                Type = InspectionRuleTypes.Count,
                Label = "screw",
                Operator = InspectionRuleOperators.Equal,
                Count = 2
            },
            new InspectionRule
            {
                Type = InspectionRuleTypes.RelativePosition,
                SubjectLabel = "screw",
                ReferenceLabel = "body",
                Relation = InspectionRuleRelations.RightOf
            });
        var detections = new[]
        {
            Detection(0, 20, 20, width: 10),
            Detection(4, 80, 20, width: 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.PrimaryReason.Should().Be("数量规则 NG：期望 screw 数量 = 2，实际只检测到 1 个");
        result.Summary.Should().Contain("启用规则 2 条，失败 2 条");
        result.Details.Should().HaveCount(2);
        result.Details[1].Should().Contain("位置规则 NG");
    }

    [Fact]
    public void Evaluate_NoEnabledRules_ReturnsNg()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.Count,
            Label = "screw",
            Count = 1,
            Enabled = false
        });

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, Array.Empty<YoloResult>(), Labels);

        result.IsQualified.Should().BeFalse();
        result.Summary.Should().Contain("未启用判定规则");
    }

    [Fact]
    public void Evaluate_OrderedLabelsAllowMissingButNoDetections_ReturnsNg()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.OrderedLabels,
            ExpectedLabels = new List<string> { "Wire_Brown", "Wire_Black" },
            AllowMissing = true
        });

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, Array.Empty<YoloResult>(), Labels);

        result.IsQualified.Should().BeFalse();
        result.RuleResults[0].Message.Should().Contain("未检测到期望标签");
    }

    [Fact]
    public void Evaluate_RelativePositionRequiresEverySubjectToMatch_ReturnsNg()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.RelativePosition,
            SubjectLabel = "screw",
            ReferenceLabel = "body",
            Relation = InspectionRuleRelations.LeftOf
        });
        var detections = new[]
        {
            Detection(0, 20, 20, width: 10),
            Detection(0, 120, 20, width: 10),
            Detection(4, 80, 20, width: 20),
        };

        InspectionJudgeResult result = InspectionRuleEngine.Evaluate(ruleSet, detections, Labels);

        result.IsQualified.Should().BeFalse();
        result.RuleResults[0].Actual.Should().Contain("主目标 2 个");
    }

    [Fact]
    public void GetFallbackGoal_UsesFirstEqualCountRule()
    {
        var ruleSet = RuleSet(
            new InspectionRule
            {
                Type = InspectionRuleTypes.OrderedLabels,
                ExpectedLabels = new List<string> { "Wire_Brown", "Wire_Black" }
            },
            new InspectionRule
            {
                Type = InspectionRuleTypes.Count,
                Label = "screw",
                Count = 4
            });

        InspectionFallbackGoal? goal = InspectionRuleEngine.GetFallbackGoal(ruleSet);

        goal.Should().NotBeNull();
        goal!.TargetLabel.Should().Be("screw");
        goal.TargetCount.Should().Be(4);
    }

    [Fact]
    public void GetFallbackGoal_UsesRuleSetFallbackWhenNoEqualCountRule()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.OrderedLabels,
            ExpectedLabels = new List<string> { "Wire_Brown", "Wire_Black" }
        });
        ruleSet.FallbackTargetLabel = "legacy_screw";
        ruleSet.FallbackTargetCount = 7;

        InspectionFallbackGoal? goal = InspectionRuleEngine.GetFallbackGoal(ruleSet);

        goal.Should().NotBeNull();
        goal!.TargetLabel.Should().Be("legacy_screw");
        goal.TargetCount.Should().Be(7);
    }

    [Fact]
    public void GetFallbackGoal_CountRuleOverridesLegacyFallback()
    {
        var ruleSet = RuleSet(
            new InspectionRule
            {
                Type = InspectionRuleTypes.Count,
                Label = "active_goal",
                Count = 2
            });
        ruleSet.FallbackTargetLabel = "legacy_goal";
        ruleSet.FallbackTargetCount = 9;

        InspectionFallbackGoal? goal = InspectionRuleEngine.GetFallbackGoal(ruleSet);

        goal.Should().NotBeNull();
        goal!.TargetLabel.Should().Be("active_goal");
        goal.TargetCount.Should().Be(2);
    }

    [Fact]
    public void Serialize_DoesNotPersistDerivedEnabledRules()
    {
        var ruleSet = RuleSet(new InspectionRule
        {
            Type = InspectionRuleTypes.Count,
            Label = "screw",
            Count = 4
        });

        string json = InspectionRuleSetSerializer.Serialize(ruleSet);

        json.Should().NotContain("EnabledRules");
    }

    [Fact]
    public void AppConfig_LegacyTarget_MigratesToRuleSetJson()
    {
        const string json = """
        {
          "TargetLabel": "screw",
          "TargetCount": 3,
          "WireSequenceJudgeEnabled": false
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        InspectionRuleSet ruleSet = config!.GetInspectionRuleSet();

        ruleSet.Rules.Should().ContainSingle();
        ruleSet.Rules[0].Type.Should().Be(InspectionRuleTypes.Count);
        ruleSet.Rules[0].Label.Should().Be("screw");
        ruleSet.Rules[0].Count.Should().Be(3);
    }

    [Fact]
    public void AppConfig_LegacyWireSequence_MigratesToOrderedRule()
    {
        const string json = """
        {
          "TargetLabel": "legacy_screw",
          "TargetCount": 6,
          "WireSequenceJudgeEnabled": true,
          "WireSequenceExpectedLabels": "Wire_Brown,Wire_Black",
          "WireSequenceSortBy": "CenterY",
          "WireSequenceDirection": "TopToBottom"
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        InspectionRuleSet ruleSet = config!.GetInspectionRuleSet();

        ruleSet.Rules.Should().ContainSingle();
        ruleSet.Rules[0].Type.Should().Be(InspectionRuleTypes.OrderedLabels);
        ruleSet.Rules[0].ExpectedLabels.Should().Equal("Wire_Brown", "Wire_Black");
        ruleSet.Rules[0].Direction.Should().Be("TopToBottom");

        InspectionFallbackGoal? goal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
        goal.Should().NotBeNull();
        goal!.TargetLabel.Should().Be("legacy_screw");
        goal.TargetCount.Should().Be(6);
    }

    private static InspectionRuleSet RuleSet(params InspectionRule[] rules)
    {
        return new InspectionRuleSet
        {
            Rules = rules.ToList()
        };
    }

    private static YoloResult Detection(
        int classId,
        float centerX,
        float centerY,
        float confidence = 0.95f,
        float width = 18,
        float height = 40)
    {
        return new YoloResult
        {
            ClassId = classId,
            CenterX = centerX,
            CenterY = centerY,
            Width = width,
            Height = height,
            Confidence = confidence,
        };
    }
}
