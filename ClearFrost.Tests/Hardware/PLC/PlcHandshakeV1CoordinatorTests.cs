using ClearFrost.Core.Inspection;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Hardware.PLC;

#pragma warning disable CS0067
public class PlcHandshakeV1CoordinatorTests
{
    [Fact]
    public async Task AcceptTriggerAsync_写TriggerAck并清触发位()
    {
        var plc = new SimulatedPlcService();
        var coordinator = new PlcHandshakeV1Coordinator(plc);
        PlcHandshakeV1Addresses addresses = CreateAddresses();

        PlcHandshakeV1Result result = await coordinator.AcceptTriggerAsync(
            addresses,
            new PlcTriggerContext
            {
                TriggerAddress = addresses.TriggerAddress,
                TriggerSeq = 42
            });

        result.Succeeded.Should().BeTrue();
        plc.LastValue(addresses.VisionReadyAddress).Should().Be(0);
        plc.LastValue(addresses.VisionBusyAddress).Should().Be(1);
        plc.LastValue(addresses.TriggerAckAddress).Should().Be(42);
        plc.LastValue(addresses.TriggerAddress).Should().Be(0);
        plc.Writes.Should().ContainInOrder(
            (addresses.VisionBusyAddress, (short)1),
            (addresses.TriggerAckAddress, (short)42),
            (addresses.TriggerAddress, (short)0));
    }

    [Fact]
    public async Task AcceptTriggerAsync_写入失败时复位Busy和Ack且不接单()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.FailWrites.Add(addresses.TriggerAckAddress);
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.AcceptTriggerAsync(
            addresses,
            new PlcTriggerContext { TriggerAddress = addresses.TriggerAddress, TriggerSeq = 7 });

        result.Succeeded.Should().BeFalse();
        plc.LastValue(addresses.VisionBusyAddress).Should().Be(0);
        plc.LastValue(addresses.TriggerAckAddress).Should().Be(0);
        plc.Writes.Should().NotContain(write => write.Address == addresses.TriggerAddress && write.Value == 0);
    }

    [Fact]
    public async Task RejectTriggerAsync_拒绝接单时不残留Busy和Ack()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.RejectTriggerAsync(
            addresses,
            new PlcTriggerContext { TriggerAddress = addresses.TriggerAddress, TriggerSeq = 8 },
            errorCode: 91,
            clearTrigger: true);

        result.Succeeded.Should().BeTrue();
        plc.LastValue(addresses.VisionBusyAddress).Should().Be(0);
        plc.LastValue(addresses.TriggerAckAddress).Should().Be(0);
        plc.LastValue(addresses.ErrorCodeAddress).Should().Be(91);
        plc.LastValue(addresses.TriggerAddress).Should().Be(0);
    }

    [Fact]
    public async Task RejectTriggerAsync_清触发位失败时返回失败并保持Busy为0()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.FailWrites.Add(addresses.TriggerAddress);
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.RejectTriggerAsync(
            addresses,
            new PlcTriggerContext { TriggerAddress = addresses.TriggerAddress },
            errorCode: 92,
            clearTrigger: true);

        result.Succeeded.Should().BeFalse();
        plc.LastValue(addresses.VisionBusyAddress).Should().Be(0);
        plc.LastValue(addresses.TriggerAckAddress).Should().Be(0);
    }

    [Fact]
    public async Task CompleteInspectionAsync_ResultAck确认后才恢复Ready并清除结果信号()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadValues[addresses.ResultAckAddress] = 1;
        var coordinator = new PlcHandshakeV1Coordinator(plc);
        var context = new InspectionContext
        {
            InspectionId = "CF-HS-001",
            TriggerSeq = 5,
            PlcTriggerAccepted = true,
            TraceStatus = TraceStatus.Queued
        };

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(addresses, context, isQualified: true);

        result.Succeeded.Should().BeTrue();
        plc.LastValue(addresses.ResultSeqAddress).Should().Be(5);
        plc.LastValue(addresses.ResultValidAddress).Should().Be(0);
        plc.LastValue(addresses.InspectionDoneAddress).Should().Be(0);
        plc.LastValue(addresses.TriggerAckAddress).Should().Be(0);
        plc.LastValue(addresses.VisionReadyAddress).Should().Be(1);

        int readyIndex = plc.Writes.FindLastIndex(write => write.Address == addresses.VisionReadyAddress && write.Value == 1);
        int validIndex = plc.Writes.FindIndex(write => write.Address == addresses.ResultValidAddress && write.Value == 1);
        readyIndex.Should().BeGreaterThan(validIndex);
    }

    [Fact]
    public async Task CompleteInspectionAsync_ResultAck超时时不恢复Ready()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses(resultAckTimeoutMs: 1);
        plc.ReadValues[addresses.ResultAckAddress] = 0;
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext { InspectionId = "CF-HS-TIMEOUT", TraceStatus = TraceStatus.Partial },
            isQualified: false);

        result.Succeeded.Should().BeFalse();
        plc.Writes.Should().NotContain(write => write.Address == addresses.VisionReadyAddress && write.Value == 1);
        plc.LastValue(addresses.VisionBusyAddress).Should().Be(0);
    }

    private static PlcHandshakeV1Addresses CreateAddresses(int resultAckTimeoutMs = 50)
    {
        return new PlcHandshakeV1Addresses
        {
            TriggerAddress = "D555",
            TriggerAckAddress = "D567",
            ResultSeqAddress = "D558",
            VisionOnlineAddress = "D559",
            VisionReadyAddress = "D560",
            VisionBusyAddress = "D561",
            InspectionDoneAddress = "D562",
            ErrorCodeAddress = "D563",
            TraceSavedAddress = "D564",
            HeartbeatAddress = "D565",
            ResultValidAddress = "D568",
            ResultAckAddress = "D569",
            ResultAckTimeoutMs = resultAckTimeoutMs
        };
    }

    private sealed class SimulatedPlcService : IPlcService
    {
        public event Action<bool>? ConnectionChanged;
        public event Action? TriggerReceived;
        public event Action<PlcTriggerContext>? TriggerContextReceived;
        public event Action<string>? ErrorOccurred;

        public bool IsConnected => true;
        public string ProtocolName => "Simulated";
        public string? LastError { get; private set; }
        public List<(string Address, short Value)> Writes { get; } = new List<(string Address, short Value)>();
        public HashSet<string> FailWrites { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, short> ReadValues { get; } = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);

        public short LastValue(string address)
        {
            return Writes.Last(write => string.Equals(write.Address, address, StringComparison.OrdinalIgnoreCase)).Value;
        }

        public Task<bool> ConnectAsync(PlcConnectionOptions options) => Task.FromResult(true);
        public void Disconnect() { }
        public bool StartMonitoring(string triggerAddress, int pollingIntervalMs = 500, int triggerDelayMs = 800, PlcMonitoringOptions? options = null) => true;
        public void StopMonitoring() { }
        public Task<bool> WriteResultAsync(string resultAddress, bool isQualified) => WriteResultAsync(resultAddress, (short)(isQualified ? 1 : 0));
        public Task<bool> WriteResultAsync(string resultAddress, short valueToWrite)
        {
            Writes.Add((resultAddress, valueToWrite));
            if (FailWrites.Contains(resultAddress))
            {
                LastError = $"write failed: {resultAddress}";
                return Task.FromResult(false);
            }

            LastError = null;
            return Task.FromResult(true);
        }
        public Task<(bool Success, short Value)> ReadWordAsync(string address)
        {
            return Task.FromResult((true, ReadValues.TryGetValue(address, out short value) ? value : (short)0));
        }
        public Task<bool> WriteReleaseSignalAsync(string resultAddress) => WriteResultAsync(resultAddress, (short)1);
        public Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName)
            => Task.FromResult((true, string.Empty));
        public void Dispose() { }
    }
}
#pragma warning restore CS0067
