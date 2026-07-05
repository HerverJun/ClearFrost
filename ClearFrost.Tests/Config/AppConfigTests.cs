// ============================================================================
// AppConfigTests.cs - 配置管理单元测试
// ============================================================================
using ClearFrost.Config;
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Helpers;
using FluentAssertions;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClearFrost.Tests.Config;

[Collection("RuntimePaths")]
public class AppConfigTests
{
    [Fact]
    public void 默认配置值正确()
    {
        // Arrange & Act
        var config = new AppConfig();

        // Assert
        config.PlcIp.Should().Be("192.168.250.1");
        config.PlcPort.Should().Be(5999);
        config.PlcTriggerAddress.Should().Be("D555");
        config.PlcResultAddress.Should().Be("D556");
        config.PlcOkValue.Should().Be(1);
        config.PlcNgValue.Should().Be(0);
        config.PlcDriverProvider.Should().Be("HaoCommunication");
        config.PlcProtocolMode.Should().Be(PlcProtocolMode.Legacy);
        config.PlcTriggerSeqAddress.Should().Be("D557");
        config.PlcResultSeqAddress.Should().Be("D558");
        config.PlcVisionBusyAddress.Should().Be("D561");
        config.PlcInspectionDoneAddress.Should().Be("D562");
        config.PlcTraceSavedAddress.Should().Be("D564");
        config.PlcSiemensCpuModel.Should().Be("S1200");
        config.BarcodeEnabled.Should().BeFalse();
        config.BarcodeAddress.Should().Be("D570");
        config.BarcodeWordLength.Should().Be(16);
        config.BarcodeEncoding.Should().Be("ASCII");
        config.BarcodeRequired.Should().BeFalse();
        config.EnableGpu.Should().BeFalse();
        config.IndustrialRenderMode.Should().BeTrue();
        config.Confidence.Should().BeApproximately(0.5f, 0.001f);
        config.IouThreshold.Should().BeApproximately(0.3f, 0.001f);
        config.ModelPackageDirectory.Should().Be("models");
        config.StrictModelPackageMode.Should().BeFalse();
        config.IsDebugMode.Should().BeFalse();
        config.TargetCount.Should().Be(4);
        config.VisionMode.Should().Be(0);
        config.Cameras.Should().ContainSingle();
        config.ActiveCamera.Should().NotBeNull();
        config.ActiveCamera!.Id.Should().Be("legacy_cam");
        config.ActiveCamera.PixelFormat.Should().Be("Mono8");
        config.WireSequenceJudgeEnabled.Should().BeFalse();
        config.WireSequenceExpectedLabels.Should().Be("Wire_Brown,Wire_Black,Wire_Blue");
        config.WireSequenceSortBy.Should().Be("CenterX");
        config.WireSequenceDirection.Should().Be("LeftToRight");
        config.WireSequenceExpectedCount.Should().Be(0);
        config.WireSequenceMinConfidence.Should().Be(0.0);
    }

    [Fact]
    public void PlcConnectionOptions_默认驱动库为信息工程部特调版()
    {
        var options = new PlcConnectionOptions();

        options.DriverProvider.Should().Be("HaoCommunication");
    }

    [Fact]
    public void ActiveCamera_无相机时返回Null()
    {
        var config = new AppConfig();
        config.Cameras.Clear();
        config.ActiveCameraId = "";

        config.ActiveCamera.Should().BeNull();
    }

    [Fact]
    public void 旧配置未包含PlcProtocolMode时默认Legacy()
    {
        string json = """
        {
          "PlcProtocol": "Mitsubishi_MC_ASCII",
          "PlcTriggerAddress": "D555",
          "PlcResultAddress": "D556"
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);
        config!.OnDeserialized();

        config.PlcProtocolMode.Should().Be(PlcProtocolMode.Legacy);
        config.PlcTriggerSeqAddress.Should().Be("D557");
        config.PlcVisionBusyAddress.Should().Be("D561");
        config.BarcodeAddress.Should().Be("D570");
        config.BarcodeWordLength.Should().Be(16);
    }

    [Fact]
    public void EnsureActiveCameraConfigFromLegacy_相机列表为空时用设置页序列号创建活动相机()
    {
#pragma warning disable CS0618
        var config = new AppConfig
        {
            CameraName = "现场相机",
            CameraSerialNumber = "SN-FIELD-001",
            CameraManufacturer = "Huaray",
            ExposureTime = 12000,
            GainRaw = 2.0
        };
#pragma warning restore CS0618
        config.Cameras.Clear();
        config.ActiveCameraId = "";

        var activeCamera = config.EnsureActiveCameraConfigFromLegacy();

        activeCamera.Should().NotBeNull();
        activeCamera!.SerialNumber.Should().Be("SN-FIELD-001");
        activeCamera.DisplayName.Should().Be("现场相机");
        activeCamera.IsEnabled.Should().BeTrue();
        config.ActiveCameraId.Should().Be(activeCamera.Id);
    }

    [Fact]
    public void EnsureActiveCameraConfigFromLegacy_同步设置页序列号到当前活动相机()
    {
        var config = new AppConfig();
        var activeCamera = config.ActiveCamera;
        activeCamera.Should().NotBeNull();

#pragma warning disable CS0618
        config.CameraSerialNumber = "SN-UPDATED-001";
        config.CameraManufacturer = "Hikvision";
#pragma warning restore CS0618

        config.EnsureActiveCameraConfigFromLegacy();

        activeCamera!.SerialNumber.Should().Be("SN-UPDATED-001");
        activeCamera.Manufacturer.Should().Be("Hikvision");
    }

    [Fact]
    public void ActiveCamera_返回匹配ActiveCameraId的相机()
    {
        // Arrange
        var config = new AppConfig();
        var cam1 = new CameraConfig { Id = "cam1", DisplayName = "相机1" };
        var cam2 = new CameraConfig { Id = "cam2", DisplayName = "相机2" };
        config.Cameras.Add(cam1);
        config.Cameras.Add(cam2);
        config.ActiveCameraId = "cam2";

        // Act & Assert
        config.ActiveCamera.Should().NotBeNull();
        config.ActiveCamera!.DisplayName.Should().Be("相机2");
    }

    [Fact]
    public void ActiveCamera_找不到Id时返回第一个启用的相机()
    {
        var config = new AppConfig();
        config.Cameras.Clear();
        config.ActiveCameraId = "";

        var cam1 = new CameraConfig { Id = "cam1", DisplayName = "相机1", IsEnabled = false };
        var cam2 = new CameraConfig { Id = "cam2", DisplayName = "相机2", IsEnabled = true };
        config.Cameras.Add(cam1);
        config.Cameras.Add(cam2);
        config.ActiveCameraId = "nonexistent";

        config.ActiveCamera.Should().NotBeNull();
        config.ActiveCamera!.DisplayName.Should().Be("相机2");
    }

    [Fact]
    public void Json序列化_保留所有属性()
    {
        // Arrange
        var config = new AppConfig
        {
            PlcIp = "10.0.0.1",
            Confidence = 0.75f,
            TargetLabel = "test_label"
        };

        // Act
        string json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<AppConfig>(json);

        // Assert
        restored.Should().NotBeNull();
        restored!.PlcIp.Should().Be("10.0.0.1");
        restored.PlcDriverProvider.Should().Be("HaoCommunication");
        restored.PlcTriggerAddress.Should().Be("D555");
        restored.Confidence.Should().BeApproximately(0.75f, 0.001f);
        restored.TargetLabel.Should().Be("test_label");
    }

    [Fact]
    public void PlcDriverProvider_Json序列化往返()
    {
        var config = new AppConfig
        {
            PlcDriverProvider = "McpX"
        };

        string json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<AppConfig>(json);

        restored.Should().NotBeNull();
        restored!.PlcDriverProvider.Should().Be("McpX");
    }

    [Fact]
    public void CameraConfig_Clone正确复制所有属性()
    {
        // Arrange
        var original = new CameraConfig
        {
            Id = "test_id",
            SerialNumber = "SN123456",
            DisplayName = "测试相机",
            Manufacturer = "Hikvision",
            ExposureTime = 25000,
            Gain = 2.5,
            IsEnabled = true
        };

        // Act
        var clone = original.Clone();

        // Assert
        clone.Id.Should().Be(original.Id);
        clone.SerialNumber.Should().Be(original.SerialNumber);
        clone.DisplayName.Should().Be(original.DisplayName);
        clone.Manufacturer.Should().Be(original.Manufacturer);
        clone.ExposureTime.Should().Be(original.ExposureTime);
        clone.Gain.Should().Be(original.Gain);
        clone.IsEnabled.Should().Be(original.IsEnabled);

        // 确保是深拷贝
        clone.Should().NotBeSameAs(original);
    }

    [Theory]
    [InlineData(0.0f, 0.0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1.0f, 1.0f)]
    public void Confidence_各种有效值(float input, float expected)
    {
        var config = new AppConfig { Confidence = input };
        config.Confidence.Should().BeApproximately(expected, 0.001f);
    }

    [Theory]
    [InlineData("Mitsubishi_MC_ASCII")]
    [InlineData("Mitsubishi_MC_Binary")]
    [InlineData("Modbus_TCP")]
    public void PlcProtocol_支持各种协议类型(string protocol)
    {
        var config = new AppConfig { PlcProtocol = protocol };
        config.PlcProtocol.Should().Be(protocol);
    }

    [Fact]
    public void Plc地址_兼容旧版数字配置()
    {
        const string json = """
        {
          "PlcProtocol": "Mitsubishi_MC_ASCII",
          "PlcTriggerAddress": 555,
          "PlcResultAddress": 556
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.Should().NotBeNull();
        config!.PlcTriggerAddress.Should().Be("D555");
        config.PlcResultAddress.Should().Be("D556");
    }

    [Fact]
    public void Plc地址_Siemens旧版数字配置迁移到Db1()
    {
        const string json = """
        {
          "PlcProtocol": "Siemens_S7",
          "PlcTriggerAddress": 555,
          "PlcResultAddress": 556
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.Should().NotBeNull();
        config!.PlcTriggerAddress.Should().Be("DB1.555");
        config.PlcResultAddress.Should().Be("DB1.556");
        config.PlcTriggerSeqAddress.Should().Be("DB1.557");
        config.PlcResultSeqAddress.Should().Be("DB1.558");
        config.BarcodeAddress.Should().Be("DB1.570");
    }

    [Fact]
    public void Plc地址_字符串配置序列化往返()
    {
        var config = new AppConfig
        {
            PlcProtocol = "Siemens_S7",
            PlcTriggerAddress = "DB100.0",
            PlcResultAddress = "DB100.2",
            PlcSiemensCpuModel = "S1500"
        };

        string json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<AppConfig>(json);

        restored.Should().NotBeNull();
        restored!.PlcTriggerAddress.Should().Be("DB100.0");
        restored.PlcResultAddress.Should().Be("DB100.2");
        restored.PlcSiemensCpuModel.Should().Be("S1500");
    }

    [Fact]
    public void Plc配置_旧版缺少Cpu型号时兼容为S1200()
    {
        const string json = """
        {
          "PlcProtocol": "Siemens_S7",
          "PlcTriggerAddress": "DB1.555",
          "PlcResultAddress": "DB1.556"
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.Should().NotBeNull();
        config!.PlcSiemensCpuModel.Should().Be("S1200");
    }

    [Fact]
    public void Plc地址_非法字符串配置回退到协议默认值()
    {
        const string json = """
        {
          "PlcProtocol": "Mitsubishi_MC_ASCII",
          "PlcTriggerAddress": "D555X",
          "PlcResultAddress": "INVALID"
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.Should().NotBeNull();
        config!.PlcTriggerAddress.Should().Be("D555");
        config.PlcResultAddress.Should().Be("D556");
    }

    [Fact]
    public void 串口光电_默认配置值正确()
    {
        var config = new AppConfig();

        config.TriggerSource.Should().Be(ClearFrost.Hardware.TriggerSource.PLC);
        config.SerialPhotoelectricPortName.Should().Be("");
        config.SerialPhotoelectricBaudRate.Should().Be(9600);
        config.SerialPhotoelectricDebounceMs.Should().Be(50);
        config.SerialPhotoelectricTimeoutMs.Should().Be(1000);
    }

    [Fact]
    public void 串口光电_Json序列化往返()
    {
        var config = new AppConfig
        {
            TriggerSource = ClearFrost.Hardware.TriggerSource.SerialPhotoelectric,
            SerialPhotoelectricPortName = "COM3",
            SerialPhotoelectricBaudRate = 115200,
            SerialPhotoelectricDebounceMs = 100,
            SerialPhotoelectricTimeoutMs = 2000,
        };

        string json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<AppConfig>(json);

        restored.Should().NotBeNull();
        restored!.TriggerSource.Should().Be(ClearFrost.Hardware.TriggerSource.SerialPhotoelectric);
        restored.SerialPhotoelectricPortName.Should().Be("COM3");
        restored.SerialPhotoelectricBaudRate.Should().Be(115200);
        restored.SerialPhotoelectricDebounceMs.Should().Be(100);
        restored.SerialPhotoelectricTimeoutMs.Should().Be(2000);
    }

    [Fact]
    public void 线序判定_Json序列化往返()
    {
        var config = new AppConfig
        {
            WireSequenceJudgeEnabled = true,
            WireSequenceExpectedLabels = "Wire_Black,Wire_Blue",
            WireSequenceSortBy = "CenterY",
            WireSequenceDirection = "TopToBottom",
            WireSequenceExpectedCount = 2,
            WireSequenceMinConfidence = 0.62,
            WireSequenceAllowMissing = true,
            WireSequenceAllowDuplicate = true,
        };

        string json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<AppConfig>(json);

        restored.Should().NotBeNull();
        restored!.WireSequenceJudgeEnabled.Should().BeTrue();
        restored.WireSequenceExpectedLabels.Should().Be("Wire_Black,Wire_Blue");
        restored.WireSequenceSortBy.Should().Be("CenterY");
        restored.WireSequenceDirection.Should().Be("TopToBottom");
        restored.WireSequenceExpectedCount.Should().Be(2);
        restored.WireSequenceMinConfidence.Should().BeApproximately(0.62, 0.001);
        restored.WireSequenceAllowMissing.Should().BeTrue();
        restored.WireSequenceAllowDuplicate.Should().BeTrue();
    }

    [Fact]
    public void 保存配置Json_移除旧判定字段并保留规则集()
    {
        var config = new AppConfig
        {
            TargetLabel = "legacy",
            TargetCount = 9,
            WireSequenceJudgeEnabled = true,
            WireSequenceExpectedLabels = "A,B"
        };
        config.GetInspectionRuleSet();

        var method = typeof(AppConfig).GetMethod(
            "SerializeForSave",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull();
        string json = (string)method!.Invoke(config, Array.Empty<object>())!;
        JsonObject rootObject = JsonNode.Parse(json)!.AsObject();

        rootObject.ContainsKey("InspectionRuleSetJson").Should().BeTrue();
        rootObject.ContainsKey("TargetLabel").Should().BeFalse();
        rootObject.ContainsKey("TargetCount").Should().BeFalse();
        rootObject.ContainsKey("WireSequenceJudgeEnabled").Should().BeFalse();
        rootObject.ContainsKey("WireSequenceExpectedLabels").Should().BeFalse();
    }

    [Fact]
    public void 保存配置Json_未提前读取规则集_仍先迁移再移除旧判定字段()
    {
        var config = new AppConfig
        {
            TargetLabel = "legacy_screw",
            TargetCount = 9,
            InspectionRuleSetJson = string.Empty
        };

        var method = typeof(AppConfig).GetMethod(
            "SerializeForSave",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull();
        string json = (string)method!.Invoke(config, Array.Empty<object>())!;
        JsonObject rootObject = JsonNode.Parse(json)!.AsObject();
        rootObject.ContainsKey("TargetLabel").Should().BeFalse();
        rootObject.ContainsKey("TargetCount").Should().BeFalse();

        string ruleSetJson = rootObject["InspectionRuleSetJson"]!.GetValue<string>();
        InspectionRuleSet ruleSet = InspectionRuleSetSerializer.DeserializeOrDefault(ruleSetJson);
        ruleSet.Rules.Should().ContainSingle();
        ruleSet.Rules[0].Label.Should().Be("legacy_screw");
        ruleSet.Rules[0].Count.Should().Be(9);
        ruleSet.FallbackTargetLabel.Should().Be("legacy_screw");
        ruleSet.FallbackTargetCount.Should().Be(9);
    }

    [Fact]
    public void 保存配置Json_规则Json无效_抛出错误且不静默覆盖()
    {
        var config = new AppConfig
        {
            InspectionRuleSetJson = "{ bad json"
        };
        var method = typeof(AppConfig).GetMethod(
            "SerializeForSave",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull();
        Action act = () => method!.Invoke(config, Array.Empty<object>());

        act.Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*判定规则配置 JSON 无效*");
    }

    [Fact]
    public void 保存配置文件_拒绝链接目标且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalConfig = Path.Combine(tempDir, "external.json");
            string linkedConfig = Path.Combine(tempDir, "config.json");
            File.WriteAllText(externalConfig, "{\"external\":true}");
            if (!TryCreateFileSymbolicLink(linkedConfig, externalConfig))
            {
                return;
            }

            Action act = () => InvokeWriteConfigAtomically(linkedConfig, "{\"changed\":true}");

            act.Should()
                .Throw<TargetInvocationException>()
                .WithInnerException<IOException>()
                .WithMessage("*链接文件*");
            File.ReadAllText(externalConfig).Should().Be("{\"external\":true}");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void 保存配置文件_拒绝链接父目录且不写入外部目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalDirectory = Path.Combine(tempDir, "external");
            string linkedDirectory = Path.Combine(tempDir, "linked");
            Directory.CreateDirectory(externalDirectory);
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, externalDirectory))
            {
                return;
            }

            string targetPath = Path.Combine(linkedDirectory, "config.json");

            Action act = () => InvokeWriteConfigAtomically(targetPath, "{\"changed\":true}");

            act.Should()
                .Throw<TargetInvocationException>()
                .WithInnerException<IOException>()
                .WithMessage("*链接目录*");
            Directory.EnumerateFileSystemEntries(externalDirectory).Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Load_跳过链接运行配置并使用备份配置()
    {
        string tempDir = CreateTempDirectory();
        string? previousRoot = Environment.GetEnvironmentVariable("CLEARFROST_APPDATA_ROOT");
        Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", tempDir);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimePaths.ConfigPath)!);
            string externalConfig = Path.Combine(tempDir, "external-config.json");
            File.WriteAllText(externalConfig, new AppConfig { PlcNgValue = 77 }.ToPortableJson());
            if (!TryCreateFileSymbolicLink(RuntimePaths.ConfigPath, externalConfig))
            {
                return;
            }

            File.WriteAllText(RuntimePaths.ConfigPath + ".bak", new AppConfig { PlcNgValue = 12 }.ToPortableJson());

            AppConfig loaded = AppConfig.Load();

            loaded.PlcNgValue.Should().Be(12);
            AppConfig.FromJson(File.ReadAllText(externalConfig)).PlcNgValue.Should().Be(77);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", previousRoot);
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void 相机像素格式_旧Mono8配置保持显式黑白()
    {
        const string json = """
        {
          "Cameras": [
            {
              "Id": "cam1",
              "SerialNumber": "SN001",
              "DisplayName": "现场相机",
              "PixelFormat": "Mono8",
              "IsEnabled": true
            }
          ],
          "ActiveCameraId": "cam1"
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.Should().NotBeNull();
        config!.ActiveCamera.Should().NotBeNull();
        config.ActiveCamera!.PixelFormat.Should().Be("Mono8");
    }

    [Fact]
    public void 相机像素格式_空配置迁移为Auto()
    {
        const string json = """
        {
          "Cameras": [
            {
              "Id": "cam1",
              "SerialNumber": "SN001",
              "DisplayName": "现场相机",
              "PixelFormat": "",
              "IsEnabled": true
            }
          ],
          "ActiveCameraId": "cam1"
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.Should().NotBeNull();
        config!.ActiveCamera.Should().NotBeNull();
        config.ActiveCamera!.PixelFormat.Should().Be("Auto");
    }

    [Theory]
    [InlineData("BGR", "BGR8")]
    [InlineData("Color", "BGR8")]
    [InlineData("RGB", "RGB8")]
    [InlineData("Bayer-RG", "BayerRG8")]
    [InlineData("UnknownFormat", "Auto")]
    public void 相机像素格式_旧别名归一为可设格式(string input, string expected)
    {
        string json = $$"""
        {
          "Cameras": [
            {
              "Id": "cam1",
              "SerialNumber": "SN001",
              "DisplayName": "现场相机",
              "PixelFormat": "{{input}}",
              "IsEnabled": true
            }
          ],
          "ActiveCameraId": "cam1"
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json);

        config.Should().NotBeNull();
        config!.ActiveCamera.Should().NotBeNull();
        config.ActiveCamera!.PixelFormat.Should().Be(expected);
    }

    private static void InvokeWriteConfigAtomically(string targetPath, string json)
    {
        MethodInfo? method = typeof(AppConfig).GetMethod(
            "WriteConfigAtomically",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        method!.Invoke(null, new object[] { targetPath, json });
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(AppConfigTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
