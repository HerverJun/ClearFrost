using ClearFrost.Config;
using ClearFrost.Helpers;
using FluentAssertions;

namespace ClearFrost.Tests.Config;

[Collection("RuntimePaths")]
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
}
