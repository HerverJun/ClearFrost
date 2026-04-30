// ============================================================================
// AppConfigTests.cs - 配置管理单元测试
// ============================================================================
using ClearFrost.Config;
using ClearFrost.Hardware;
using FluentAssertions;
using System.Text.Json;

namespace ClearFrost.Tests.Config;

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
        config.PlcDriverProvider.Should().Be("Hsl");
        config.PlcProtocolMode.Should().Be(PlcProtocolMode.Legacy);
        config.PlcTriggerSeqAddress.Should().Be("D557");
        config.PlcResultSeqAddress.Should().Be("D558");
        config.PlcVisionBusyAddress.Should().Be("D561");
        config.PlcInspectionDoneAddress.Should().Be("D562");
        config.PlcTraceSavedAddress.Should().Be("D564");
        config.EnablePlcBarcodeReading.Should().BeFalse();
        config.PlcBarcodeAddress.Should().Be("DB15.DBB2");
        config.PlcBarcodeLength.Should().Be(13);
        config.PlcBarcodeEncoding.Should().Be("ASCII");
        config.PlcBarcodeRequired.Should().BeTrue();
        config.PlcSiemensCpuModel.Should().Be("S1200");
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
        config.EnablePlcBarcodeReading.Should().BeFalse();
        config.PlcBarcodeAddress.Should().Be("DB15.DBB2");
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
        restored.PlcDriverProvider.Should().Be("Hsl");
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
    public void Plc条码配置_Json序列化往返()
    {
        var config = new AppConfig
        {
            PlcProtocol = "Siemens_S7",
            EnablePlcBarcodeReading = true,
            PlcBarcodeAddress = "DB15.DBB2",
            PlcBarcodeLength = 13,
            PlcBarcodeEncoding = "ASCII",
            PlcBarcodeRequired = true
        };

        string json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<AppConfig>(json);

        restored.Should().NotBeNull();
        restored!.EnablePlcBarcodeReading.Should().BeTrue();
        restored.PlcBarcodeAddress.Should().Be("DB15.DBB2");
        restored.PlcBarcodeLength.Should().Be(13);
        restored.PlcBarcodeEncoding.Should().Be("ASCII");
        restored.PlcBarcodeRequired.Should().BeTrue();
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
}
