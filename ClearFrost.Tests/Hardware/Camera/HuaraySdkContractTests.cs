using System.Reflection;
using ClearFrost.Hardware;
using FluentAssertions;

namespace ClearFrost.Tests.Hardware.Camera;

public sealed class HuaraySdkContractTests
{
    [Fact]
    [Trait("Lane", "ExternalHuaraySdk")]
    public void SuppliedSdk_反射合同通过且不连接相机()
    {
        string? sdkPath = Environment.GetEnvironmentVariable("CLEARFROST_HUARAY_SDK_PATH");
        if (string.IsNullOrWhiteSpace(sdkPath))
        {
            return;
        }

        using var camera = new HuaraySdkCamera();
        FieldInfo bridgeField = typeof(HuaraySdkCamera).GetField(
            "_bridge",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        bridgeField.GetValue(camera).Should().NotBeNull();
        camera.IsConnected.Should().BeFalse();
    }
}
