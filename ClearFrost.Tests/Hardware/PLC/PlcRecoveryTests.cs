using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Hardware;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Hardware.PLC;

[Trait("Category", "PLC.AdapterState")]
public class PlcAdapterStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task McpXRead_空连接时标记断开(bool isAscii)
    {
        var device = CreateMcpXDevice(isAscii);
        SetPrivateField(device, "_isConnected", true);

        var result = await device.ReadInt16Async("D0");

        result.Success.Should().BeFalse();
        device.IsConnected.Should().BeFalse();
        device.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task McpXWrite_空连接时标记断开(bool isAscii)
    {
        var device = CreateMcpXDevice(isAscii);
        SetPrivateField(device, "_isConnected", true);

        var success = await device.WriteInt16Async("D0", 1);

        success.Should().BeFalse();
        device.IsConnected.Should().BeFalse();
        device.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HslRead_失败后标记断开()
    {
        var adapter = new MitsubishiMcAsciiAdapter("127.0.0.1", 1);
        SetPrivateField(adapter, "_isConnected", true);

        var result = await adapter.ReadInt16Async("D0");

        result.Success.Should().BeFalse();
        adapter.IsConnected.Should().BeFalse();
        adapter.LastError.Should().NotBeNullOrWhiteSpace();
    }

    private static IPlcDevice CreateMcpXDevice(bool isAscii)
    {
        return isAscii
            ? new McpXMitsubishiMcAsciiAdapter("127.0.0.1", 1234)
            : new McpXMitsubishiMcBinaryAdapter("127.0.0.1", 1234);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = PlcTestReflectionHelper.GetFieldInfo(target.GetType(), fieldName);
        field.Should().NotBeNull($"field '{fieldName}' should exist");
        field!.SetValue(target, value);
    }
}

[Trait("Category", "PLC.ServiceRecovery")]
public class PlcServiceRecoveryTests
{
    [Fact]
    public async Task MonitoringLoop_读失败后断开并清空坏连接()
    {
        var service = new PlcService();
        var cancellationTokenSource = new CancellationTokenSource();
        var device = new FakePlcDevice(
            isConnected: true,
            onRead: () => cancellationTokenSource.Cancel(),
            readResultFactory: address =>
            {
                return (false, 0, $"读取失败: {address}");
            });
        bool? changedState = null;
        service.ConnectionChanged += connected => changedState = connected;

        PlcTestReflectionHelper.SetPrivateField(service, "_plcDevice", device);
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);

        await InvokeMonitoringLoopAsync(service, "D555", 50, 0, cancellationTokenSource.Token);

        PlcTestReflectionHelper.GetPrivateField<IPlcDevice?>(service, "_plcDevice").Should().BeNull();
        device.DisconnectCalled.Should().BeTrue();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().Contain("读取失败");
        changedState.Should().BeFalse();
    }

    [Fact]
    public async Task TryReconnectAsync_连接失败后清空设备引用()
    {
        var service = new PlcService();
        var existingDevice = new FakePlcDevice(isConnected: true);

        PlcTestReflectionHelper.SetPrivateField(service, "_plcDevice", existingDevice);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocol", "Mitsubishi_MC_ASCII");
        PlcTestReflectionHelper.SetPrivateField(service, "_lastDriverProvider", "Hsl");
        PlcTestReflectionHelper.SetPrivateField(service, "_lastIp", "127.0.0.1");
        PlcTestReflectionHelper.SetPrivateField(service, "_lastPort", 1);
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);

        var result = await InvokeTryReconnectAsync(service, CancellationToken.None);

        result.Should().BeFalse();
        existingDevice.DisconnectCalled.Should().BeTrue();
        PlcTestReflectionHelper.GetPrivateField<IPlcDevice?>(service, "_plcDevice").Should().BeNull();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MonitoringLoop_Legacy模式仍触发旧事件()
    {
        var service = new PlcService();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var device = new FakePlcDevice(
            isConnected: true,
            readResultFactory: address => (true, (short)(address == "D555" ? 1 : 0), string.Empty));
        bool legacyTriggered = false;
        bool contextTriggered = false;
        service.TriggerReceived += () =>
        {
            legacyTriggered = true;
            cancellationTokenSource.Cancel();
        };
        service.TriggerContextReceived += _ => contextTriggered = true;

        PlcTestReflectionHelper.SetPrivateField(service, "_plcDevice", device);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocolMode", PlcProtocolMode.Legacy);
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);

        await InvokeMonitoringLoopAsync(service, "D555", 50, 0, cancellationTokenSource.Token);

        legacyTriggered.Should().BeTrue();
        contextTriggered.Should().BeFalse();
        device.Writes.Should().Contain(("D555", (short)0));
    }

    [Fact]
    public async Task MonitoringLoop_HandshakeV1读取TriggerSeq并触发上下文事件()
    {
        var service = new PlcService();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var device = new FakePlcDevice(
            isConnected: true,
            readResultFactory: address =>
            {
                return address switch
                {
                    "D555" => (true, (short)1, string.Empty),
                    "D557" => (true, (short)42, string.Empty),
                    _ => (true, (short)0, string.Empty)
                };
            });
        PlcTriggerContext? receivedContext = null;
        bool legacyTriggered = false;
        service.TriggerReceived += () => legacyTriggered = true;
        service.TriggerContextReceived += context =>
        {
            receivedContext = context;
            cancellationTokenSource.Cancel();
        };

        PlcTestReflectionHelper.SetPrivateField(service, "_plcDevice", device);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocolMode", PlcProtocolMode.HandshakeV1);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastTriggerSeqAddress", "D557");
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);

        await InvokeMonitoringLoopAsync(service, "D555", 50, 0, cancellationTokenSource.Token);

        legacyTriggered.Should().BeFalse();
        receivedContext.Should().NotBeNull();
        receivedContext!.TriggerSource.Should().Be("PLC");
        receivedContext.TriggerSeq.Should().Be(42);
        device.Writes.Should().Contain(("D555", (short)0));
    }

    private static async Task InvokeMonitoringLoopAsync(
        PlcService service,
        string triggerAddress,
        int pollingIntervalMs,
        int triggerDelayMs,
        CancellationToken cancellationToken)
    {
        var method = typeof(PlcService).GetMethod(
            "MonitoringLoop",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = method!.Invoke(
            service,
            new object[] { triggerAddress, pollingIntervalMs, triggerDelayMs, cancellationToken }) as Task;

        task.Should().NotBeNull();
        await task!;
    }

    private static async Task<bool> InvokeTryReconnectAsync(PlcService service, CancellationToken cancellationToken)
    {
        var method = typeof(PlcService).GetMethod(
            "TryReconnectAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = method!.Invoke(service, new object[] { cancellationToken }) as Task<bool>;

        task.Should().NotBeNull();
        return await task!;
    }
}

internal static class PlcTestReflectionHelper
{
    public static T? GetPrivateField<T>(object target, string fieldName)
    {
        var field = GetFieldInfo(target.GetType(), fieldName);
        field.Should().NotBeNull($"field '{fieldName}' should exist");
        return (T?)field!.GetValue(target);
    }

    public static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = GetFieldInfo(target.GetType(), fieldName);
        field.Should().NotBeNull($"field '{fieldName}' should exist");
        field!.SetValue(target, value);
    }

    public static void SetAutoProperty(object target, string propertyName, object? value)
    {
        SetPrivateField(target, $"<{propertyName}>k__BackingField", value);
    }

    public static FieldInfo? GetFieldInfo(Type type, string fieldName)
    {
        Type? currentType = type;
        while (currentType != null)
        {
            var field = currentType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                return field;
            }

            currentType = currentType.BaseType;
        }

        return null;
    }
}

internal sealed class FakePlcDevice : IPlcDevice
{
    private readonly Action? _onRead;
    private readonly Func<string, (bool Success, short Value, string Error)>? _readResultFactory;

    public FakePlcDevice(
        bool isConnected,
        Action? onRead = null,
        Func<string, (bool Success, short Value, string Error)>? readResultFactory = null)
    {
        IsConnected = isConnected;
        _onRead = onRead;
        _readResultFactory = readResultFactory;
    }

    public bool DisconnectCalled { get; private set; }

    public List<(string Address, short Value)> Writes { get; } = new List<(string Address, short Value)>();

    public string LastError { get; private set; } = string.Empty;

    public bool IsConnected { get; private set; }

    public string ProtocolName => "Fake PLC";

    public Task<bool> ConnectAsync()
    {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public void Disconnect()
    {
        DisconnectCalled = true;
        IsConnected = false;
    }

    public Task<(bool Success, short Value)> ReadInt16Async(string address)
    {
        _onRead?.Invoke();

        var result = _readResultFactory?.Invoke(address) ?? (true, (short)0, string.Empty);
        LastError = result.Error;
        if (!result.Success)
        {
            IsConnected = false;
        }

        return Task.FromResult((result.Success, result.Value));
    }

    public Task<(bool Success, byte[] Value)> ReadBytesAsync(string address, ushort length)
    {
        _onRead?.Invoke();
        return Task.FromResult((true, Array.Empty<byte>()));
    }

    public Task<bool> WriteInt16Async(string address, short value)
    {
        Writes.Add((address, value));
        return Task.FromResult(true);
    }
}
