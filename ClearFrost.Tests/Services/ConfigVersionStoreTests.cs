using ClearFrost.Config;
using ClearFrost.Helpers;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

[Collection("RuntimePaths")]
public class ConfigVersionStoreTests
{
    [Fact]
    public void SaveVersion_写入版本文件并按时间倒序返回()
    {
        WithRuntimeRoot(root =>
        {
            var store = new ConfigVersionStore(Path.Combine(root, "System"));
            var older = store.SaveVersion(
                CreateConfig("older.onnx", 0.4f, "SN-OLD"),
                new ConfigVersionCreateOptions
                {
                    Reason = "SaveSettings",
                    OperatorName = "张工",
                    OperatorRole = "Engineer",
                    ShiftName = "白班",
                    ChangeSummary = "Changes=1",
                    CreatedAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero)
                });
            var newer = store.SaveVersion(
                CreateConfig("newer.onnx", 0.8f, "SN-NEW"),
                new ConfigVersionCreateOptions
                {
                    Reason = "ChangeModel",
                    OperatorName = "李工",
                    OperatorRole = "Engineer",
                    ChangeSummary = "Changes=2",
                    CreatedAt = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero)
                });

            var versions = store.ListVersions(10);

            versions.Select(v => v.VersionId).Should().Equal(newer.VersionId, older.VersionId);
            versions[0].Reason.Should().Be("ChangeModel");
            versions[1].OperatorName.Should().Be("张工");
            File.Exists(newer.ConfigPath).Should().BeTrue();
            File.Exists(newer.MetadataPath).Should().BeTrue();
            newer.ConfigHash.Should().HaveLength(64);
        });
    }

    [Fact]
    public void RestoreVersion_恢复目标配置并写入运行时Config()
    {
        WithRuntimeRoot(root =>
        {
            var store = new ConfigVersionStore(Path.Combine(root, "System"));
            var source = CreateConfig("restored.onnx", 0.76f, "SN-RESTORE");
            ConfigVersionEntry version = store.SaveVersion(
                source,
                new ConfigVersionCreateOptions
                {
                    Reason = "SaveSettings",
                    ChangeSummary = "Changes=3",
                    CreatedAt = new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero)
                });
            var target = CreateConfig("current.onnx", 0.22f, "SN-CURRENT");
            target.Save().Should().BeTrue();

            ConfigVersionRestoreResult result = store.RestoreVersion(version.VersionId, target);

            result.Version.VersionId.Should().Be(version.VersionId);
            result.RestoredConfigPath.Should().Be(RuntimePaths.ConfigPath);
            target.CurrentModelFileName.Should().Be("restored.onnx");
            target.Confidence.Should().Be(0.76f);
            target.ActiveCamera!.SerialNumber.Should().Be("SN-RESTORE");
            AppConfig.Load().CurrentModelFileName.Should().Be("restored.onnx");
        });
    }

    [Fact]
    public void EnsureBaseline_已有版本时不重复创建()
    {
        WithRuntimeRoot(root =>
        {
            var store = new ConfigVersionStore(Path.Combine(root, "System"));
            var config = CreateConfig("baseline.onnx", 0.5f, "SN-BASE");

            ConfigVersionEntry first = store.EnsureBaseline(config);
            ConfigVersionEntry second = store.EnsureBaseline(config);

            second.VersionId.Should().Be(first.VersionId);
            store.ListVersions(10).Should().ContainSingle();
        });
    }

    [Fact]
    public void LoadConfig_版本文件被篡改时拒绝加载()
    {
        WithRuntimeRoot(root =>
        {
            var store = new ConfigVersionStore(Path.Combine(root, "System"));
            ConfigVersionEntry version = store.SaveVersion(
                CreateConfig("stable.onnx", 0.5f, "SN-STABLE"),
                new ConfigVersionCreateOptions { Reason = "SaveSettings", ChangeSummary = "Changes=1" });
            File.AppendAllText(version.ConfigPath, "\n ");

            Action act = () => store.LoadConfig(version.VersionId);

            act.Should().Throw<InvalidOperationException>().WithMessage("*校验失败*");
        });
    }

    private static AppConfig CreateConfig(string modelName, float confidence, string serialNumber)
    {
#pragma warning disable CS0618
        var config = new AppConfig
        {
            CurrentModelFileName = modelName,
            Confidence = confidence,
            CameraSerialNumber = serialNumber,
            CameraName = "主相机",
            CameraManufacturer = "Huaray",
            ExposureTime = 12345,
            GainRaw = 1.5
        };
#pragma warning restore CS0618

        config.Cameras.Clear();
        config.ActiveCameraId = string.Empty;
        config.EnsureActiveCameraConfigFromLegacy();
        return config;
    }

    private static void WithRuntimeRoot(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClearFrostConfigVersionTests", Guid.NewGuid().ToString("N"));
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
