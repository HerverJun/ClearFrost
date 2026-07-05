using ClearFrost.Config;
using ClearFrost.Helpers;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace ClearFrost.Tests.Config;

[Collection("RuntimePaths")]
public class ProjectPresetStoreTests
{
    [Fact]
    public void BuiltInPresets_包含现场工位部署元数据()
    {
        string root = FindRepositoryRoot();
        string presetsPath = Path.Combine(root, "ClearFrost", "project-presets.json");
        JsonObject presets = JsonNode.Parse(File.ReadAllText(presetsPath))!.AsObject();

        var expectedPresets = new Dictionary<string, (string Name, string DetectionType, string TargetLabel, int TargetCount)>(StringComparer.Ordinal)
        {
            ["N5_remote"] = ("N5 遥控器漏装", "遥控器漏装", "remote", 1),
            ["N5_screw"] = ("N5 螺钉检测", "螺钉检测", "screw", 1),
            ["N6_remote"] = ("N6 遥控器漏装", "遥控器漏装", "remote", 1),
            ["N6_screw"] = ("N6 螺钉检测", "螺钉检测", "screw", 1),
            ["W5_screw"] = ("W5 螺钉检测", "螺钉检测", "screw", 4),
            ["W6_screw"] = ("W6 螺钉检测", "螺钉检测", "screw", 4),
            ["electric_heating_screw"] = ("电加热螺钉检测", "螺钉检测", "screw", 4),
        };

        foreach (KeyValuePair<string, (string Name, string DetectionType, string TargetLabel, int TargetCount)> entry in expectedPresets)
        {
            string presetId = entry.Key;
            (string name, string detectionType, string targetLabel, int targetCount) = entry.Value;

            presets.ContainsKey(presetId).Should().BeTrue($"内置工位模板 {presetId} 必须存在");
            JsonObject preset = presets[presetId]!.AsObject();

            preset["name"]?.GetValue<string>().Should().Be(name);
            preset["StationName"]?.GetValue<string>().Should().Be(name);
            preset["DetectionType"]?.GetValue<string>().Should().Be(detectionType);
            preset["TriggerSource"]?.GetValue<string>().Should().Be("PLC");
            preset["PlcProtocol"]?.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            preset["TargetLabel"]?.GetValue<string>().Should().Be(targetLabel);
            preset["TargetLabels"]?.AsArray().Select(item => item?.GetValue<string>()).Should().Contain(targetLabel);
            preset["TargetCount"]?.GetValue<int>().Should().Be(targetCount);
            preset["CameraManufacturer"]?.GetValue<string>().Should().Be("Huaray");
            preset["CameraBrand"]?.GetValue<string>().Should().Be("Huaray");
            preset.ContainsKey("CameraSerialNumber").Should().BeTrue();
            preset.ContainsKey("PlcIp").Should().BeTrue();
            preset.ContainsKey("PlcPort").Should().BeTrue();
            preset.ContainsKey("PlcTriggerAddress").Should().BeTrue();
            preset.ContainsKey("PlcResultAddress").Should().BeTrue();
            preset.ContainsKey("RecommendedExposureTime").Should().BeTrue();
            preset.ContainsKey("RecommendedGainRaw").Should().BeTrue();
            preset["BarcodeEnabled"]?.GetValue<bool>().Should().BeFalse();
            preset["EnableMultiModelFallback"]?.GetValue<bool>().Should().BeFalse();
            preset["StoragePath"]?.GetValue<string>().Should().Be("C:\\GreeVisionData");
            preset["DefaultStoragePath"]?.GetValue<string>().Should().Be("C:\\GreeVisionData");
        }
    }

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

    [Fact]
    public void Load_拒绝链接运行时预设文件()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClearFrostTests", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("CLEARFROST_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", root);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimePaths.ProjectPresetsPath)!);
            string externalPreset = Path.Combine(root, "external-project-presets.json");
            File.WriteAllText(externalPreset, "{}");
            if (!TryCreateFileSymbolicLink(RuntimePaths.ProjectPresetsPath, externalPreset))
            {
                return;
            }

            Action act = () => ProjectPresetStore.Load();

            act.Should().Throw<IOException>().WithMessage("*链接文件*");
            File.ReadAllText(externalPreset).Should().Be("{}");
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

    [Fact]
    public void SavePreset_拒绝链接运行时预设目录且不写外部目录()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClearFrostTests", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("CLEARFROST_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", root);

        try
        {
            Directory.CreateDirectory(root);
            string externalDirectory = Path.Combine(root, "external-config");
            Directory.CreateDirectory(externalDirectory);
            string configDirectory = Path.Combine(root, "Config");
            if (!TryCreateDirectorySymbolicLink(configDirectory, externalDirectory))
            {
                return;
            }

            string payload = """
                {
                  "id": "blocked",
                  "name": "Blocked",
                  "preset": {
                    "TargetLabel": "screw"
                  }
                }
                """;

            Action act = () => ProjectPresetStore.SavePreset(payload);

            act.Should().Throw<IOException>().WithMessage("*链接目录*");
            Directory.EnumerateFileSystemEntries(externalDirectory).Should().BeEmpty();
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

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = Directory.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = File.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClearFrost.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ClearFrost.sln.");
    }
}
