// ============================================================================
// AppConfigTests.cs - 配置管理单元测试
// ============================================================================
using ClearFrost.Config;
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
        config.PlcIp.Should().Be("192.168.22.44");
        config.PlcPort.Should().Be(4999);
        config.Confidence.Should().BeApproximately(0.5f, 0.001f);
        config.IouThreshold.Should().BeApproximately(0.3f, 0.001f);
        config.TargetCount.Should().Be(4);
        config.VisionMode.Should().Be(0);
        config.Cameras.Should().BeEmpty();
    }

    [Fact]
    public void ActiveCamera_无相机时返回Null()
    {
        var config = new AppConfig();

        config.ActiveCamera.Should().BeNull();
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
        restored.Confidence.Should().BeApproximately(0.75f, 0.001f);
        restored.TargetLabel.Should().Be("test_label");
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
}
