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
}
