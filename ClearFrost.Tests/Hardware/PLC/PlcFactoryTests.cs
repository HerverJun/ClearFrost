using System;
using ClearFrost.Hardware;
using FluentAssertions;

namespace ClearFrost.Tests.Hardware.PLC;

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
    [InlineData(PlcProtocolType.Modbus_TCP)]
    [InlineData(PlcProtocolType.Siemens_S7)]
    [InlineData(PlcProtocolType.Omron_Fins)]
    public void Create_McpX驱动_非三菱协议抛异常(PlcProtocolType protocol)
    {
        var action = () => PlcFactory.Create("McpX", protocol, "127.0.0.1", 1234);

        action.Should().Throw<NotSupportedException>();
    }
}
