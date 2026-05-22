using System.Text.Json.Nodes;
using ClearFrost.Config;
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Helpers;
using FluentAssertions;

namespace ClearFrost.Tests.Config;

[Collection("RuntimePaths")]
public class ConfigMigrationServiceTests
{
    [Fact]
    public void Export_迁移包包含配置和预设且不增加大文件节点()
    {
        WithRuntimeRoot(root =>
        {
            ProjectPresetStore.SavePreset("""
                {
                  "id": "field_line",
                  "name": "现场线体",
                  "preset": {
                    "PlcIp": "192.168.10.20",
                    "PlcNgValue": 9
                  }
                }
                """);
            var config = CreateFieldConfig(plcNgValue: 17, serialNumber: "SN-EXPORT", exposure: 12345);
            string targetPath = Path.Combine(root, "ClearFrost_Config.clearfrost-config.json");

            ConfigMigrationExportResult result = ConfigMigrationService.Export(config, targetPath, "9.8.7");

            result.Path.Should().Be(targetPath);
            result.PresetCount.Should().BeGreaterThanOrEqualTo(1);
            JsonObject rootObject = JsonNode.Parse(File.ReadAllText(targetPath))!.AsObject();
            rootObject["schema"]!.GetValue<string>().Should().Be(ConfigMigrationService.Schema);
            rootObject["appVersion"]!.GetValue<string>().Should().Be("9.8.7");
            rootObject["config"].Should().BeOfType<JsonObject>();
            rootObject["projectPresets"].Should().BeOfType<JsonObject>();
            rootObject["projectPresets"]!.AsObject().ContainsKey("field_line").Should().BeTrue();
            rootObject.ContainsKey("models").Should().BeFalse();
            rootObject.ContainsKey("images").Should().BeFalse();
            rootObject.ContainsKey("logs").Should().BeFalse();
        });
    }

    [Fact]
    public void Import_迁移包覆盖运行配置并合并预设()
    {
        WithRuntimeRoot(root =>
        {
            ProjectPresetStore.SavePreset("""
                {
                  "id": "shared",
                  "name": "本地旧预设",
                  "preset": {
                    "PlcIp": "10.0.0.1"
                  }
                }
                """);

            var source = CreateFieldConfig(plcNgValue: 21, serialNumber: "SN-IMPORT", exposure: 24680);
            source.TriggerSource = TriggerSource.SerialPhotoelectric;
            source.SerialPhotoelectricPortName = "COM7";
            source.TargetLabel = "terminal";
            source.TargetCount = 6;
            string packagePath = Path.Combine(root, "migration.clearfrost-config.json");
            ConfigMigrationService.Export(source, packagePath, "1.0.0");

            var current = CreateFieldConfig(plcNgValue: 0, serialNumber: "SN-OLD", exposure: 1000);

            ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(packagePath, current);

            result.Kind.Should().Be(ConfigMigrationImportKind.MigrationPackage);
            result.HasConfig.Should().BeTrue();
            result.HasPresets.Should().BeTrue();
            current.PlcNgValue.Should().Be(21);
            current.TriggerSource.Should().Be(TriggerSource.SerialPhotoelectric);
            current.SerialPhotoelectricPortName.Should().Be("COM7");
            current.ActiveCamera.Should().NotBeNull();
            current.ActiveCamera!.SerialNumber.Should().Be("SN-IMPORT");
            current.ActiveCamera.ExposureTime.Should().Be(24680);
            InspectionRuleSet ruleSet = current.GetInspectionRuleSet();
            ruleSet.FallbackTargetLabel.Should().Be("terminal");
            ruleSet.FallbackTargetCount.Should().Be(6);

            AppConfig reloaded = AppConfig.Load();
            reloaded.PlcNgValue.Should().Be(21);
            reloaded.ActiveCamera!.SerialNumber.Should().Be("SN-IMPORT");
            ProjectPresetStore.Load().Presets.ContainsKey("shared").Should().BeTrue();
        });
    }

    [Fact]
    public void Import_普通ConfigJson迁移到运行时配置()
    {
        WithRuntimeRoot(root =>
        {
            var source = CreateFieldConfig(plcNgValue: 8, serialNumber: "SN-CONFIG", exposure: 33333);
            source.TriggerSource = TriggerSource.SerialPhotoelectric;
            source.SerialPhotoelectricPortName = "COM12";
            string configPath = Path.Combine(root, "config.json");
            File.WriteAllText(configPath, source.ToPortableJson());
            var current = CreateFieldConfig(plcNgValue: 0, serialNumber: "SN-OLD", exposure: 1000);

            ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(configPath, current);

            result.Kind.Should().Be(ConfigMigrationImportKind.AppConfig);
            result.HasConfig.Should().BeTrue();
            result.HasPresets.Should().BeFalse();
            current.PlcNgValue.Should().Be(8);
            current.SerialPhotoelectricPortName.Should().Be("COM12");
            current.ActiveCamera!.SerialNumber.Should().Be("SN-CONFIG");
            File.Exists(RuntimePaths.ConfigPath).Should().BeTrue();
            AppConfig.Load().ActiveCamera!.ExposureTime.Should().Be(33333);
        });
    }

    [Fact]
    public void Import_普通ConfigJson字段大小写不敏感()
    {
        WithRuntimeRoot(root =>
        {
            string configPath = Path.Combine(root, "camel-config.json");
            File.WriteAllText(configPath, """
                {
                  "plcNgValue": 12,
                  "plcIp": "10.10.10.12"
                }
                """);
            var current = CreateFieldConfig(plcNgValue: 0, serialNumber: "SN-OLD", exposure: 1000);

            ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(configPath, current);

            result.Kind.Should().Be(ConfigMigrationImportKind.AppConfig);
            current.PlcNgValue.Should().Be(12);
            current.PlcIp.Should().Be("10.10.10.12");
            AppConfig.Load().PlcNgValue.Should().Be(12);
        });
    }

    [Fact]
    public void Import_预设文件只合并预设不改当前配置()
    {
        WithRuntimeRoot(root =>
        {
            var current = CreateFieldConfig(plcNgValue: 4, serialNumber: "SN-LOCAL", exposure: 1000);
            current.Save().Should().BeTrue();
            ProjectPresetStore.SavePreset("""
                {
                  "id": "local_keep",
                  "name": "本地保留",
                  "preset": {
                    "PlcIp": "10.0.0.2"
                  }
                }
                """);
            ProjectPresetStore.SavePreset("""
                {
                  "id": "shared",
                  "name": "本地旧共享",
                  "preset": {
                    "PlcIp": "10.0.0.3"
                  }
                }
                """);
            string presetsPath = Path.Combine(root, "project-presets.json");
            File.WriteAllText(presetsPath, """
                {
                  "presets": {
                    "shared": {
                      "name": "导入共享",
                      "PlcIp": "10.0.0.99"
                    },
                    "imported": {
                      "name": "导入新增",
                      "PlcNgValue": 15
                    }
                  }
                }
                """);

            ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(presetsPath, current);

            result.Kind.Should().Be(ConfigMigrationImportKind.ProjectPresets);
            result.HasConfig.Should().BeFalse();
            current.PlcNgValue.Should().Be(4);
            AppConfig.Load().PlcNgValue.Should().Be(4);

            JsonObject presets = ProjectPresetStore.Load().Presets;
            presets.ContainsKey("local_keep").Should().BeTrue();
            presets.ContainsKey("imported").Should().BeTrue();
            presets["shared"]!["name"]!.GetValue<string>().Should().Be("导入共享");
            presets["shared"]!["PlcIp"]!.GetValue<string>().Should().Be("10.0.0.99");
        });
    }

    [Fact]
    public void Import_预设写入失败时回滚已保存的运行配置()
    {
        WithRuntimeRoot(root =>
        {
            var current = CreateFieldConfig(plcNgValue: 3, serialNumber: "SN-STABLE", exposure: 1000);
            current.Save().Should().BeTrue();
            string originalConfigJson = File.ReadAllText(RuntimePaths.ConfigPath);
            ProjectPresetStore.SavePreset("""
                {
                  "id": "locked",
                  "name": "锁定预设",
                  "preset": {
                    "PlcIp": "10.0.0.4"
                  }
                }
                """);

            var source = CreateFieldConfig(plcNgValue: 29, serialNumber: "SN-ROLLBACK", exposure: 9999);
            string packagePath = Path.Combine(root, "rollback.clearfrost-config.json");
            JsonObject package = new()
            {
                ["schema"] = ConfigMigrationService.Schema,
                ["config"] = JsonNode.Parse(source.ToPortableJson()),
                ["projectPresets"] = new JsonObject
                {
                    ["imported"] = new JsonObject
                    {
                        ["name"] = "导入预设",
                        ["PlcIp"] = "10.0.0.99"
                    }
                }
            };
            File.WriteAllText(packagePath, package.ToJsonString());

            using FileStream lockedPresetFile = new(
                RuntimePaths.ProjectPresetsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            Action act = () => ConfigMigrationService.ImportFromFile(packagePath, current);

            act.Should().Throw<Exception>();
            File.ReadAllText(RuntimePaths.ConfigPath).Should().Be(originalConfigJson);
            current.PlcNgValue.Should().Be(3);
            current.ActiveCamera!.SerialNumber.Should().Be("SN-STABLE");
        });
    }

    [Fact]
    public void Import_缺失Config对象的迁移包不改写运行时配置()
    {
        WithRuntimeRoot(root =>
        {
            var current = CreateFieldConfig(plcNgValue: 3, serialNumber: "SN-STABLE", exposure: 1000);
            current.Save().Should().BeTrue();
            string originalConfigJson = File.ReadAllText(RuntimePaths.ConfigPath);
            string badPackagePath = Path.Combine(root, "bad.clearfrost-config.json");
            File.WriteAllText(badPackagePath, $$"""
                {
                  "schema": "{{ConfigMigrationService.Schema}}",
                  "projectPresets": {
                    "should_not_apply": {
                      "name": "不应导入"
                    }
                  }
                }
                """);

            Action act = () => ConfigMigrationService.ImportFromFile(badPackagePath, current);

            act.Should().Throw<InvalidOperationException>().WithMessage("*缺少 config 对象*");
            File.ReadAllText(RuntimePaths.ConfigPath).Should().Be(originalConfigJson);
            current.PlcNgValue.Should().Be(3);
            ProjectPresetStore.Load().Presets.ContainsKey("should_not_apply").Should().BeFalse();
        });
    }

    private static AppConfig CreateFieldConfig(short plcNgValue, string serialNumber, double exposure)
    {
#pragma warning disable CS0618
        var config = new AppConfig
        {
            PlcNgValue = plcNgValue,
            PlcIp = "192.168.100.8",
            CameraName = "现场相机",
            CameraSerialNumber = serialNumber,
            CameraManufacturer = "Huaray",
            ExposureTime = exposure,
            GainRaw = 2.5,
            TargetLabel = "screw",
            TargetCount = 4
        };
#pragma warning restore CS0618

        config.Cameras.Clear();
        config.ActiveCameraId = "";
        config.EnsureActiveCameraConfigFromLegacy();
        return config;
    }

    private static void WithRuntimeRoot(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClearFrostTests", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("CLEARFROST_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", root);

        try
        {
            Directory.CreateDirectory(root);
            test(root);
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
