using ClearFrost.Config;
using ClearFrost.Helpers;
using FluentAssertions;

namespace ClearFrost.Tests.Config;

public class ProjectPresetStoreTests
{
    [Fact]
    public void SavePreset_FieldPreset_WritesRuntimePresetFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClearFrostTests", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("CLEARFROST_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", root);

        try
        {
            string payload = """
                {
                  "id": "field_preset",
                  "name": "现场新预设",
                  "preset": {
                    "PlcIp": "192.168.1.10",
                    "PlcPort": 5000,
                    "TargetLabel": "screw"
                  }
                }
                """;

            var saved = ProjectPresetStore.SavePreset(payload);

            saved.Path.Should().Be(RuntimePaths.ProjectPresetsPath);
            File.Exists(saved.Path).Should().BeTrue();
            saved.Presets["field_preset"]?["name"]?.GetValue<string>().Should().Be("现场新预设");
            saved.Presets["field_preset"]?["PlcIp"]?.GetValue<string>().Should().Be("192.168.1.10");

            var reloaded = ProjectPresetStore.Load();
            reloaded.Presets["field_preset"]?["PlcPort"]?.GetValue<int>().Should().Be(5000);

            var deleted = ProjectPresetStore.DeletePreset("field_preset");
            deleted.Presets.ContainsKey("field_preset").Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", previousRoot);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
