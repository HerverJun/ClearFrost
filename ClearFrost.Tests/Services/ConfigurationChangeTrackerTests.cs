using ClearFrost.Config;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class ConfigurationChangeTrackerTests
{
    [Fact]
    public void CompareTo_关键配置变化_返回字段级差异()
    {
        var beforeConfig = new AppConfig
        {
            StoragePath = @"D:\LineA",
            CurrentModelFileName = "model-a.onnx",
            Confidence = 0.5f,
            PlcIp = "192.168.1.10",
            PlcPort = 6000,
            OperatorSessionMaxHours = 8
        };
        beforeConfig.Cameras.Clear();
        beforeConfig.Cameras.Add(new CameraConfig
        {
            Id = "cam-1",
            DisplayName = "主相机",
            SerialNumber = "SN-001",
            Manufacturer = "Huaray",
            ExposureTime = 10000,
            Gain = 1.5,
            PixelFormat = "Mono8",
            IsEnabled = true
        });
        beforeConfig.ActiveCameraId = "cam-1";

        var afterConfig = new AppConfig
        {
            StoragePath = @"D:\LineB",
            CurrentModelFileName = "model-b.onnx",
            Confidence = 0.75f,
            PlcIp = "192.168.1.11",
            PlcPort = 6000,
            OperatorSessionMaxHours = 12
        };
        afterConfig.Cameras.Clear();
        afterConfig.Cameras.Add(new CameraConfig
        {
            Id = "cam-1",
            DisplayName = "主相机",
            SerialNumber = "SN-001",
            Manufacturer = "Huaray",
            ExposureTime = 12000,
            Gain = 1.5,
            PixelFormat = "Mono8",
            IsEnabled = true
        });
        afterConfig.ActiveCameraId = "cam-1";

        ConfigurationSnapshot before = ConfigurationChangeTracker.Capture(beforeConfig);
        ConfigurationSnapshot after = ConfigurationChangeTracker.Capture(afterConfig);

        var changes = before.CompareTo(after);

        changes.Should().Contain(c => c.Key == "Storage.Path" && c.Before == @"D:\LineA" && c.After == @"D:\LineB");
        changes.Should().Contain(c => c.Key == "Model.Current" && c.Before == "model-a.onnx" && c.After == "model-b.onnx");
        changes.Should().Contain(c => c.Key == "Model.Confidence" && c.Before == "0.5" && c.After == "0.75");
        changes.Should().Contain(c => c.Key == "PLC.Endpoint" && c.Before == "192.168.1.10:6000" && c.After == "192.168.1.11:6000");
        changes.Should().Contain(c => c.Key == "Camera.ExposureTime" && c.Before == "10000" && c.After == "12000");
        changes.Should().Contain(c => c.Key == "Production.OperatorSessionMaxHours" && c.Before == "8" && c.After == "12");
    }

    [Fact]
    public void FormatChanges_无变化_返回零变更()
    {
        string summary = ConfigurationChangeTracker.FormatChanges([]);

        summary.Should().Be("Changes=0");
    }

    [Fact]
    public void FormatChanges_字段包含换行_压缩为单行审计摘要()
    {
        var changes = new[]
        {
            new ConfigurationChange
            {
                Key = "Vision.Rule",
                Before = "A\r\nB",
                After = "C\tD"
            }
        };

        string summary = ConfigurationChangeTracker.FormatChanges(changes);

        summary.Should().Be("Changes=1; Vision.Rule: 'A  B' -> 'C D'");
    }
}
