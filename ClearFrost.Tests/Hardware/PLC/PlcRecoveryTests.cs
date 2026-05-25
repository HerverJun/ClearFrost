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

    [Fact]
    public async Task MonitoringLoop_触发复位失败时不发送触发事件并断开()
    {
        var service = new PlcService();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var device = new FakePlcDevice(
            isConnected: true,
            readResultFactory: address => (true, (short)(address == "D555" ? 1 : 0), string.Empty),
            writeResultFactory: (address, value) =>
            {
                cancellationTokenSource.Cancel();
                return (false, $"复位失败: {address}");
            });
        bool triggered = false;
        service.TriggerReceived += () => triggered = true;

        PlcTestReflectionHelper.SetPrivateField(service, "_plcDevice", device);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocolMode", PlcProtocolMode.Legacy);
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);

        await InvokeMonitoringLoopAsync(service, "D555", 50, 0, cancellationTokenSource.Token);

        triggered.Should().BeFalse();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().Contain("复位失败");
        device.DisconnectCalled.Should().BeTrue();
        PlcTestReflectionHelper.GetPrivateField<IPlcDevice?>(service, "_plcDevice").Should().BeNull();
    }

    [Fact]
    public async Task MonitoringLoop_HandshakeV1读取TriggerSeq失败时不复位也不触发()
    {
        var service = new PlcService();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var device = new FakePlcDevice(
            isConnected: true,
            readResultFactory: address =>
            {
                if (address == "D557")
                {
                    cancellationTokenSource.Cancel();
                    return (false, (short)0, "TriggerSeq读取失败");
                }

                return (true, (short)(address == "D555" ? 1 : 0), string.Empty);
            });
        bool legacyTriggered = false;
        bool contextTriggered = false;
        service.TriggerReceived += () => legacyTriggered = true;
        service.TriggerContextReceived += _ => contextTriggered = true;

        PlcTestReflectionHelper.SetPrivateField(service, "_plcDevice", device);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocolMode", PlcProtocolMode.HandshakeV1);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastTriggerSeqAddress", "D557");
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);

        await InvokeMonitoringLoopAsync(service, "D555", 50, 0, cancellationTokenSource.Token);

        legacyTriggered.Should().BeFalse();
        contextTriggered.Should().BeFalse();
        device.Writes.Should().BeEmpty();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().Contain("TriggerSeq读取失败");
        device.DisconnectCalled.Should().BeTrue();
        PlcTestReflectionHelper.GetPrivateField<IPlcDevice?>(service, "_plcDevice").Should().BeNull();
    }

    [Fact]
    public async Task MonitoringLoop_正常停止不标记PLC断开()
    {
        var service = new PlcService();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        PlcTestReflectionHelper.SetPrivateField(service, "_monitoringStopRequested", true);
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);

        await InvokeMonitoringLoopAsync(service, "D555", 50, 0, cancellationTokenSource.Token);

        service.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task WriteResultAsync_底层写入失败后同步断开状态()
    {
        var service = CreateConnectedService(new FakePlcDevice(
            isConnected: true,
            writeResultFactory: (address, value) => (false, $"写入失败: {address}")));
        bool? changedState = null;
        service.ConnectionChanged += connected => changedState = connected;

        bool result = await service.WriteResultAsync("D100", (short)1);

        result.Should().BeFalse();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().Contain("写入失败");
        changedState.Should().BeFalse();
    }

    [Fact]
    public async Task WriteResultAsync_服务状态未同步但底层已断开时立即同步断开()
    {
        var service = CreateConnectedService(new FakePlcDevice(isConnected: false));
        bool? changedState = null;
        service.ConnectionChanged += connected => changedState = connected;

        bool result = await service.WriteResultAsync("D100", (short)1);

        result.Should().BeFalse();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().Contain("PLC 未连接");
        changedState.Should().BeFalse();
    }

    [Fact]
    public async Task WriteReleaseSignalAsync_底层写入失败后同步断开状态()
    {
        var service = CreateConnectedService(new FakePlcDevice(
            isConnected: true,
            writeResultFactory: (address, value) => (false, $"放行失败: {address}")));
        bool? changedState = null;
        service.ConnectionChanged += connected => changedState = connected;

        bool result = await service.WriteReleaseSignalAsync("D100");

        result.Should().BeFalse();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().Contain("放行失败");
        changedState.Should().BeFalse();
    }

    [Fact]
    public async Task ReadStringAsync_底层读取失败后同步断开状态()
    {
        var service = CreateConnectedService(new FakePlcDevice(
            isConnected: true,
            readBytesResultFactory: (address, length) => (false, Array.Empty<byte>(), $"读取条码失败: {address}")));
        bool? changedState = null;
        service.ConnectionChanged += connected => changedState = connected;

        var result = await service.ReadStringAsync("D570", 16, "ASCII");

        result.Success.Should().BeFalse();
        result.Value.Should().BeEmpty();
        service.IsConnected.Should().BeFalse();
        service.LastError.Should().Contain("读取条码失败");
        changedState.Should().BeFalse();
    }

    [Theory]
    [InlineData("Modbus_TCP", 16, 16)]
    [InlineData("Siemens_S7", 16, 32)]
    [InlineData("Mitsubishi_MC_ASCII", 16, 32)]
    [InlineData("Mitsubishi_MC_Binary", 16, 32)]
    [InlineData("Omron_Fins", 16, 32)]
    public void GetStringReadLength_按协议换算条码字长(string protocol, int wordLength, ushort expected)
    {
        var service = new PlcService();
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocol", protocol);

        ushort readLength = InvokeGetStringReadLength(service, wordLength);

        readLength.Should().Be(expected);
    }

    [Fact]
    public async Task ConnectAsync_地址无效后不会卡住连接中状态()
    {
        var service = new PlcService();

        bool firstResult = await service.ConnectAsync(new PlcConnectionOptions
        {
            Protocol = PlcProtocolType.Mitsubishi_MC_Binary.ToString(),
            DriverProvider = "McpX",
            Ip = "127.0.0.1",
            Port = 1,
            TriggerAddress = "M100"
        });

        bool secondResult = await service.ConnectAsync(new PlcConnectionOptions
        {
            Protocol = PlcProtocolType.Mitsubishi_MC_Binary.ToString(),
            DriverProvider = "McpX",
            Ip = "127.0.0.1",
            Port = 1,
            TriggerAddress = "M100"
        });

        firstResult.Should().BeFalse();
        secondResult.Should().BeFalse();
        PlcTestReflectionHelper.GetPrivateField<bool>(service, "_isConnecting").Should().BeFalse();
        service.LastError.Should().Contain("McpX");
    }

    [Fact]
    public void StartMonitoring_地址无效时记录错误且不抛异常()
    {
        var service = new PlcService();
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocol", PlcProtocolType.Mitsubishi_MC_Binary.ToString());
        PlcTestReflectionHelper.SetPrivateField(service, "_lastDriverProvider", "McpX");

        Action action = () => service.StartMonitoring("M100");

        action.Should().NotThrow();
        service.LastError.Should().Contain("McpX");
        PlcTestReflectionHelper.GetPrivateField<Task?>(service, "_monitoringTask").Should().BeNull();
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

    private static ushort InvokeGetStringReadLength(PlcService service, int safeWordLength)
    {
        var method = typeof(PlcService).GetMethod(
            "GetStringReadLength",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var result = method!.Invoke(service, new object[] { safeWordLength });

        result.Should().BeOfType<ushort>();
        return (ushort)result!;
    }

    private static PlcService CreateConnectedService(IPlcDevice device)
    {
        var service = new PlcService();
        PlcTestReflectionHelper.SetPrivateField(service, "_plcDevice", device);
        PlcTestReflectionHelper.SetPrivateField(service, "_lastProtocol", PlcProtocolType.Mitsubishi_MC_ASCII.ToString());
        PlcTestReflectionHelper.SetPrivateField(service, "_lastDriverProvider", "Hsl");
        PlcTestReflectionHelper.SetAutoProperty(service, "IsConnected", true);
        return service;
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
    private readonly Func<string, ushort, (bool Success, byte[] Value, string Error)>? _readBytesResultFactory;
    private readonly Func<string, short, (bool Success, string Error)>? _writeResultFactory;

    public FakePlcDevice(
        bool isConnected,
        Action? onRead = null,
        Func<string, (bool Success, short Value, string Error)>? readResultFactory = null,
        Func<string, ushort, (bool Success, byte[] Value, string Error)>? readBytesResultFactory = null,
        Func<string, short, (bool Success, string Error)>? writeResultFactory = null)
    {
        IsConnected = isConnected;
        _onRead = onRead;
        _readResultFactory = readResultFactory;
        _readBytesResultFactory = readBytesResultFactory;
        _writeResultFactory = writeResultFactory;
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

        var result = _readBytesResultFactory?.Invoke(address, length) ?? (true, Array.Empty<byte>(), string.Empty);
        LastError = result.Error;
        if (!result.Success)
        {
            IsConnected = false;
        }

        return Task.FromResult((result.Success, result.Value));
    }

    public Task<bool> WriteInt16Async(string address, short value)
    {
        Writes.Add((address, value));

        var result = _writeResultFactory?.Invoke(address, value) ?? (true, string.Empty);
        LastError = result.Error;
        if (!result.Success)
        {
            IsConnected = false;
        }

        return Task.FromResult(result.Success);
    }
}
