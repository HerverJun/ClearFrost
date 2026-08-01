using ClearFrost.Hardware;
using FluentAssertions;
using MVSDK_Net;

namespace ClearFrost.Tests.Hardware.Camera;

public sealed class HuarayEnumerationTests : IDisposable
{
    public HuarayEnumerationTests()
    {
        MyCamera.Reset();
    }

    [Fact]
    public void IMV_EnumDevices_调用方InterfaceType原样透传()
    {
        using var camera = CreateCamera();
        var deviceList = new CameraDeviceList();
        const uint requestedInterfaceType = 0xA5A50001u;

        int result = camera.IMV_EnumDevices(ref deviceList, requestedInterfaceType);

        result.Should().Be(CameraSdk.Ok);
        MyCamera.LastInterfaceType.Should().Be(requestedInterfaceType);
        MyCamera.EnumCallCount.Should().Be(1);
        deviceList.DeviceCount.Should().Be(2);
    }

    [Fact]
    public void EnumerateDevices_默认使用SdkInterfaceTypeAll枚举值()
    {
        using var camera = CreateCamera();

        List<CameraDeviceInfo> devices = camera.EnumerateDevices();

        devices.Should().HaveCount(2);
        MyCamera.LastInterfaceType.Should().Be((uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);
        MyCamera.EnumCallCount.Should().Be(1);
    }

    [Fact]
    public void Open_同一枚举快照解析序列号与索引()
    {
        using var camera = CreateCamera();

        bool opened = camera.Open("SN-002");

        opened.Should().BeTrue();
        MyCamera.EnumCallCount.Should().Be(1);
        MyCamera.CreateHandleCallCount.Should().Be(1);
        MyCamera.CreatedIndex.Should().Be(1);
        MyCamera.OpenCallCount.Should().Be(1);
        camera.CurrentDevice!.SerialNumber.Should().Be("SN-002");
    }

    public void Dispose()
    {
        MyCamera.ReleaseSnapshot();
    }

    private static HuaraySdkCamera CreateCamera()
    {
        var bridge = new HuaraySdkBridge(typeof(MyCamera).Assembly);
        return new HuaraySdkCamera(bridge);
    }
}
