using System;
using ClearFrost.Hardware;
using FluentAssertions;

namespace ClearFrost.Tests.Hardware.PLC;

[Trait("Category", "PLC.Factory")]
public class PlcFactoryTests
{
    [Theory]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII)]
    [InlineData(PlcProtocolType.Mitsubishi_MC_Binary)]
    [InlineData(PlcProtocolType.Modbus_TCP)]
    [InlineData(PlcProtocolType.Siemens_S7)]
    [InlineData(PlcProtocolType.Omron_Fins)]
    public void Create_Hsl驱动_五种协议均创建成功(PlcProtocolType protocol)
    {
        var device = PlcFactory.Create("Hsl", protocol, "127.0.0.1", 1234);

        device.Should().NotBeNull();
        device.Should().BeAssignableTo<IPlcDevice>();
    }

    [Fact]
    public void Create_McpX驱动_三菱Binary创建成功()
    {
        var device = PlcFactory.Create("McpX", PlcProtocolType.Mitsubishi_MC_Binary, "127.0.0.1", 1234);

        device.Should().BeOfType<McpXMitsubishiMcBinaryAdapter>();
    }

    [Fact]
    public void Create_McpX驱动_三菱ASCII创建成功()
    {
        var device = PlcFactory.Create("McpX", PlcProtocolType.Mitsubishi_MC_ASCII, "127.0.0.1", 1234);

        device.Should().BeOfType<McpXMitsubishiMcAsciiAdapter>();
    }

    [Theory]
    [InlineData(PlcProtocolType.Mitsubishi_MC_ASCII, typeof(HaoMitsubishiMcAsciiAdapter))]
    [InlineData(PlcProtocolType.Mitsubishi_MC_Binary, typeof(HaoMitsubishiMcBinaryAdapter))]
    [InlineData(PlcProtocolType.Modbus_TCP, typeof(HaoModbusTcpAdapter))]
    [InlineData(PlcProtocolType.Siemens_S7, typeof(HaoSiemensS7Adapter))]
    [InlineData(PlcProtocolType.Omron_Fins, typeof(HaoOmronFinsAdapter))]
    public void Create_信息部特调版驱动_五种协议均创建成功(PlcProtocolType protocol, Type expectedType)
    {
        var device = PlcFactory.Create("HaoCommunication", protocol, "127.0.0.1", 1234);

        device.Should().BeOfType(expectedType);
        device.ProtocolName.Should().Contain("信息部特调版");
    }

    [Theory]
    [InlineData(PlcProtocolType.Modbus_TCP)]
    [InlineData(PlcProtocolType.Siemens_S7)]
    [InlineData(PlcProtocolType.Omron_Fins)]
    public void Create_McpX驱动_非三菱协议抛异常(PlcProtocolType protocol)
    {
        var action = () => PlcFactory.Create("McpX", protocol, "127.0.0.1", 1234);

        action.Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("Mitsubishi_MC_ASCII", PlcProtocolType.Mitsubishi_MC_ASCII)]
    [InlineData("mitsubishi_mc_binary", PlcProtocolType.Mitsubishi_MC_Binary)]
    [InlineData(" Modbus_TCP ", PlcProtocolType.Modbus_TCP)]
    [InlineData("siemens_s7", PlcProtocolType.Siemens_S7)]
    [InlineData("Omron_Fins", PlcProtocolType.Omron_Fins)]
    public void TryParseProtocol_合法名称返回协议枚举(string raw, PlcProtocolType expected)
    {
        bool success = PlcFactory.TryParseProtocol(raw, out PlcProtocolType actual);

        success.Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mitsubishi")]
    [InlineData("1")]
    public void TryParseProtocol_非法名称返回False(string? raw)
    {
        bool success = PlcFactory.TryParseProtocol(raw, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void ParseProtocol_非法名称仍保持旧配置兼容回退()
    {
        PlcFactory.ParseProtocol("bad-protocol").Should().Be(PlcProtocolType.Mitsubishi_MC_ASCII);
    }

    [Theory]
    [InlineData("hsl", "Hsl")]
    [InlineData(" HaoCommunication ", "HaoCommunication")]
    [InlineData("mcpx", "McpX")]
    public void TryNormalizeDriverProvider_合法名称返回规范写法(string raw, string expected)
    {
        bool success = PlcFactory.TryNormalizeDriverProvider(raw, out string normalized);

        success.Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HaoCommunicaton")]
    [InlineData("Unknown")]
    public void TryNormalizeDriverProvider_非法名称返回False(string? raw)
    {
        bool success = PlcFactory.TryNormalizeDriverProvider(raw, out string normalized);

        success.Should().BeFalse();
        normalized.Should().BeEmpty();
    }
}
