using ClearFrost.Config;
using ClearFrost.Core.Rules;
using ClearFrost.Helpers;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Rules;

[Collection("RuntimePaths")]
public class VisionDebugProjectTemplateTests
{
    [Fact]
    public void 项目级模板_默认目标来自项目预设且仍可由配置回退()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(VisionDebugProjectTemplateTests), Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("CLEARFROST_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", root);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimePaths.ProjectPresetsPath)!);
            File.WriteAllText(RuntimePaths.ProjectPresetsPath, """
            {
              "W5_screw": {
                "name": "W5 自定义螺钉",
                "TargetLabel": "bolt",
                "TargetCount": 6
              },
              "N5_remote": {
                "name": "N5 自定义遥控器",
                "TargetLabel": "remote",
                "TargetCount": 1
              }
            }
            """);

            var config = new AppConfig
            {
                TargetLabel = "current_screw",
                TargetCount = 2,
                InspectionRuleSetJson = InspectionRuleSetSerializer.Serialize(
                    InspectionRuleSetSerializer.FromLegacyTarget("current_screw", 2))
            };

            InspectionRuleSet w5 = VisionDebugParameterService.ResolveRuleSet(
                config,
                new VisionDebugRunParameters { TemplateId = InspectionRuleSetTemplateIds.W5ScrewCount },
                out _);
            InspectionRuleSet n5 = VisionDebugParameterService.ResolveRuleSet(
                config,
                new VisionDebugRunParameters { TemplateId = InspectionRuleSetTemplateIds.N5RemoteMissingPart },
                out _);
            InspectionRuleSet fallback = VisionDebugParameterService.ResolveRuleSet(
                config,
                new VisionDebugRunParameters { TemplateId = InspectionRuleSetTemplateIds.ElectricHeatingScrewCount },
                out _);

            w5.Rules.Should().ContainSingle(rule =>
                rule.Type == InspectionRuleTypes.Count &&
                rule.Label == "bolt" &&
                rule.Count == 6);
            n5.Rules.Should().ContainSingle(rule =>
                rule.Type == InspectionRuleTypes.Count &&
                rule.Label == "remote" &&
                rule.Count == 1);
            fallback.Rules.Should().ContainSingle(rule =>
                rule.Type == InspectionRuleTypes.Count &&
                rule.Label == "current_screw" &&
                rule.Count == 2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", previousRoot);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
