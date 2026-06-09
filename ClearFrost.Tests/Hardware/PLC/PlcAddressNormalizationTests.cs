using System.Reflection;
using ClearFrost.Hardware;
using ClearFrost.Services;
using FluentAssertions;
using HslCommunication.Profinet.Siemens;

namespace ClearFrost.Tests.Hardware.PLC;

[Trait("Category", "PLC.Address")]
public class PlcAddressNormalizerTests
{
    [Theory]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "555", "D555")]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "D555", "D555")]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "m100", "M100")]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "X1A", "X1A")]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "y10", "Y10")]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "C20", "C20")]
    [InlineData(PlcProtocolType.Mitsubishi_MC_Binary, "555", "D555")]
    [InlineData(PlcProtocolType.Omron_Fins, "100", "D100")]
    [InlineData(PlcProtocolType.Omron_Fins, "D100", "D100")]
    [InlineData(PlcProtocolType.Omron_Fins, "CIO100", "C100")]
    [InlineData(PlcProtocolType.Omron_Fins, "c100", "C100")]
    [InlineData(PlcProtocolType.Omron_Fins, "W100", "W100")]
    [InlineData(PlcProtocolType.Omron_Fins, "H100", "H100")]
    [InlineData(PlcProtocolType.Omron_Fins, "A100", "A100")]
    [InlineData(PlcProtocolType.Modbus_TCP, "100", "100")]
    [InlineData(PlcProtocolType.Siemens_S7, "DB100.0", "DB100.0")]
    [InlineData(PlcProtocolType.Siemens_S7, "DB100.1", "DB100.1")]
    [InlineData(PlcProtocolType.Siemens_S7, "db100.2", "DB100.2")]
    [InlineData(PlcProtocolType.Siemens_S7, "DB100.DBW0", "DB100.0")]
    [InlineData(PlcProtocolType.Siemens_S7, "DB100.DBB2", "DB100.2")]
    [InlineData(PlcProtocolType.Siemens_S7, "DB100.DBD4", "DB100.4")]
    [InlineData(PlcProtocolType.Siemens_S7, " DB 100 . DBW 6 ", "DB100.6")]
    public void Normalize_有效地址返回规范化结果(PlcProtocolType protocolType, string rawAddress, string expected)
    {
        var normalized = PlcAddressNormalizer.NormalizeOrThrow(rawAddress, protocolType);

        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(PlcProtocolType.Siemens_S7, "M0", "M0")]
    [InlineData(PlcProtocolType.Siemens_S7, "m100", "M100")]
    [InlineData(PlcProtocolType.Siemens_S7, "I0", "I0")]
    [InlineData(PlcProtocolType.Siemens_S7, "Q0", "Q0")]
    [InlineData(PlcProtocolType.Siemens_S7, "AI0", "AI0")]
    [InlineData(PlcProtocolType.Siemens_S7, "aq2", "AQ2")]
    public void Normalize_西门子MIQ地址返回规范化结果(PlcProtocolType protocolType, string rawAddress, string expected)
    {
        var normalized = PlcAddressNormalizer.NormalizeOrThrow(rawAddress, protocolType);

        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("555")]
    [InlineData("D555")]
    [InlineData("DB0.0")]
    [InlineData("DB10.0.1")]
    [InlineData("DB10.DBX0.0")]
    [InlineData("M0.0")]
    public void Normalize_Siemens非法地址抛异常(string rawAddress)
    {
        var action = () => PlcAddressNormalizer.NormalizeOrThrow(rawAddress, PlcProtocolType.Siemens_S7);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "D100.0")]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, "Q100")]
    [InlineData(PlcProtocolType.Omron_Fins, "D100.0")]
    [InlineData(PlcProtocolType.Omron_Fins, "M100")]
    [InlineData(PlcProtocolType.Modbus_TCP, "D100")]
    public void Normalize_其他协议非法地址抛异常(PlcProtocolType protocolType, string rawAddress)
    {
        var action = () => PlcAddressNormalizer.NormalizeOrThrow(rawAddress, protocolType);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("D100", true)]
    [InlineData("M100", false)]
    [InlineData("X10", false)]
    public void IsSupportedByDriver_McpX仅允许三菱D区(string normalizedAddress, bool expected)
    {
        bool supported = PlcAddressNormalizer.IsSupportedByDriver(
            normalizedAddress,
            PlcProtocolType.Mitsubishi_MC_Binary,
            "McpX",
            out _);

        supported.Should().Be(expected);
    }
}

[Trait("Category", "PLC.Factory")]
public class PlcFactorySiemensOptionsTests
{
    [Fact]
    public void Create_SiemensHsl_显式S1500时创建适配器()
    {
        var device = PlcFactory.Create(new PlcConnectionOptions
        {
            Protocol = "Siemens_S7",
            DriverProvider = "Hsl",
            Ip = "127.0.0.1",
            Port = 102,
            SiemensCpuModel = "S1500"
        });

        device.Should().BeOfType<SiemensS7Adapter>();
    }

    [Fact]
    public void ParseSiemensCpuModel_空值默认回退到S1200()
    {
        PlcFactory.ParseSiemensCpuModel(null).Should().Be(SiemensPLCS.S1200);
        PlcFactory.ParseSiemensCpuModel(string.Empty).Should().Be(SiemensPLCS.S1200);
    }

    [Fact]
    public void Create_SiemensS300_应用RackSlot设置()
    {
        var device = PlcFactory.Create(new PlcConnectionOptions
        {
            Protocol = "Siemens_S7",
            DriverProvider = "Hsl",
            Ip = "127.0.0.1",
            Port = 102,
            SiemensCpuModel = "S300",
            SiemensRack = 1,
            SiemensSlot = 3
        });

        var plcField = device.GetType().GetField("_plc", BindingFlags.Instance | BindingFlags.NonPublic);
        plcField.Should().NotBeNull();

        var innerPlc = plcField!.GetValue(device);
        innerPlc.Should().NotBeNull();

        PropertyInfo? rackProperty = innerPlc!.GetType().GetProperty("Rack");
        PropertyInfo? slotProperty = innerPlc.GetType().GetProperty("Slot");

        rackProperty.Should().NotBeNull();
        slotProperty.Should().NotBeNull();
        rackProperty!.GetValue(innerPlc).Should().Be((byte)1);
        slotProperty!.GetValue(innerPlc).Should().Be((byte)3);
    }
}

[Trait("Category", "PLC.ServiceRecovery")]
public class PlcServiceProbeAddressTests
{
    [Fact]
    public void GetConnectivityProbeAddress_优先使用当前配置触发地址()
    {
        var method = typeof(PlcService).GetMethod(
            "GetConnectivityProbeAddress",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var result = method!.Invoke(null, new object[] { PlcProtocolType.Siemens_S7, "DB100.0" });

        result.Should().Be("DB100.0");
    }
}
