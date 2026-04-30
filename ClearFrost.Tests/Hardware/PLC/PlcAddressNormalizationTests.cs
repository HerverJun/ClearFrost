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
    [InlineData(PlcProtocolType.Mitsubishi_MC_Binary, "555", "D555")]
    [InlineData(PlcProtocolType.Omron_Fins, "100", "D100")]
    [InlineData(PlcProtocolType.Omron_Fins, "D100", "D100")]
    [InlineData(PlcProtocolType.Modbus_TCP, "100", "100")]
    [InlineData(PlcProtocolType.Siemens_S7, "DB100.0", "DB100.0")]
    [InlineData(PlcProtocolType.Siemens_S7, "DB100.1", "DB100.1")]
    [InlineData(PlcProtocolType.Siemens_S7, "db100.2", "DB100.2")]
    public void Normalize_有效地址返回规范化结果(PlcProtocolType protocolType, string rawAddress, string expected)
    {
        var normalized = PlcAddressNormalizer.NormalizeOrThrow(rawAddress, protocolType);

        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("555")]
    [InlineData("D555")]
    [InlineData("M100")]
    [InlineData("DB10.0.1")]
    public void Normalize_Siemens非法地址抛异常(string rawAddress)
    {
        var action = () => PlcAddressNormalizer.NormalizeOrThrow(rawAddress, PlcProtocolType.Siemens_S7);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("DB15.DBB2", "DB15.DBB2", "DB15.2")]
    [InlineData("DB15.2", "DB15.2", "DB15.2")]
    [InlineData("db15.dbb2", "DB15.DBB2", "DB15.2")]
    public void NormalizeByteAddress_Siemens条码地址支持Dbb写法(
        string rawAddress,
        string expectedNormalized,
        string expectedHslAddress)
    {
        string normalized = PlcAddressNormalizer.NormalizeByteAddressOrThrow(rawAddress, PlcProtocolType.Siemens_S7);
        string hslAddress = PlcAddressNormalizer.ToHslByteReadAddress(rawAddress, PlcProtocolType.Siemens_S7);

        normalized.Should().Be(expectedNormalized);
        hslAddress.Should().Be(expectedHslAddress);
    }

    [Theory]
    [InlineData("M100")]
    [InlineData("DB15.DBX2.0")]
    [InlineData("DB15.DBB")]
    public void NormalizeByteAddress_Siemens非法条码地址抛异常(string rawAddress)
    {
        var action = () => PlcAddressNormalizer.NormalizeByteAddressOrThrow(rawAddress, PlcProtocolType.Siemens_S7);

        action.Should().Throw<ArgumentException>();
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
