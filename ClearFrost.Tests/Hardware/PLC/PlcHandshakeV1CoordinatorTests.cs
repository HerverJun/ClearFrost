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
    public async Task AcceptTriggerAsync_任一关键写入失败都不清触发位()
    {
        PlcHandshakeV1Addresses template = CreateAddresses();
        string[] criticalAddresses =
        {
            template.VisionOnlineAddress,
            template.VisionReadyAddress,
            template.VisionBusyAddress,
            template.InspectionDoneAddress,
            template.ResultValidAddress,
            template.TraceSavedAddress,
            template.TriggerAckAddress
        };

        foreach (string failAddress in criticalAddresses)
        {
            var plc = new SimulatedPlcService();
            PlcHandshakeV1Addresses addresses = CreateAddresses();
            plc.FailWrites.Add(failAddress);
            var coordinator = new PlcHandshakeV1Coordinator(plc);

            PlcHandshakeV1Result result = await coordinator.AcceptTriggerAsync(
                addresses,
                new PlcTriggerContext { TriggerAddress = addresses.TriggerAddress, TriggerSeq = 7 });

            result.Succeeded.Should().BeFalse(failAddress);
            result.ErrorCode.Should().NotBeEmpty(failAddress);
            plc.Writes.Should().NotContain(write => write.Address == addresses.TriggerAddress && write.Value == 0, failAddress);
        }
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
    public async Task RejectTriggerAsync_任一关键写入失败都不继续清触发位()
    {
        PlcHandshakeV1Addresses template = CreateAddresses();
        string[] criticalAddresses =
        {
            template.VisionOnlineAddress,
            template.VisionReadyAddress,
            template.VisionBusyAddress,
            template.TriggerAckAddress,
            template.ErrorCodeAddress,
            template.ResultValidAddress,
            template.InspectionDoneAddress,
            template.TraceSavedAddress,
            template.HeartbeatAddress
        };

        foreach (string failAddress in criticalAddresses)
        {
            var plc = new SimulatedPlcService();
            PlcHandshakeV1Addresses addresses = CreateAddresses();
            plc.FailWrites.Add(failAddress);
            var coordinator = new PlcHandshakeV1Coordinator(plc);

            PlcHandshakeV1Result result = await coordinator.RejectTriggerAsync(
                addresses,
                new PlcTriggerContext { TriggerAddress = addresses.TriggerAddress },
                errorCode: 92,
                clearTrigger: true);

            result.Succeeded.Should().BeFalse(failAddress);
            plc.Writes.Should().NotContain(write => write.Address == addresses.TriggerAddress && write.Value == 0, failAddress);
        }
    }

    [Fact]
    public async Task CompleteInspectionAsync_ResultAck确认后才恢复Ready并清除结果信号()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadSequence.Enqueue((true, 0));
        plc.ReadSequence.Enqueue((true, 1));
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
        plc.LastValue(addresses.ResultAddress).Should().Be(addresses.OkValue);
        plc.LastValue(addresses.ResultSeqAddress).Should().Be(5);
        plc.LastValue(addresses.TraceSavedAddress).Should().Be(0);
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

    [Fact]
    public async Task CompleteInspectionAsync_Ack超时后进入安全态且补偿失败不覆盖主错误()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses(resultAckTimeoutMs: 1);
        plc.ReadValues[addresses.ResultAckAddress] = 0;
        plc.FailWriteOnAttempt[addresses.TraceSavedAddress] = 2;
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext
            {
                InspectionId = "CF-HS-SAFE",
                PlcTriggerAccepted = true,
                TriggerSeq = 9,
                TraceStatus = TraceStatus.Partial
            },
            isQualified: true);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("HandshakeV1.AckTimeout");
        result.CompensationFailures.Should().ContainSingle(f => f.SignalName == "Safety.TraceSaved");
        plc.Writes.Should().NotContain(write => write.Address == addresses.VisionReadyAddress && write.Value == 1);
        plc.LastValue(addresses.VisionReadyAddress).Should().Be(0);
        plc.LastValue(addresses.VisionBusyAddress).Should().Be(0);
        plc.LastValue(addresses.ResultValidAddress).Should().Be(0);
        plc.LastValue(addresses.InspectionDoneAddress).Should().Be(0);
        plc.LastValue(addresses.TriggerAckAddress).Should().Be(0);
    }

    [Fact]
    public async Task CompleteInspectionAsync_TraceFull才写TraceSaved为1()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadSequence.Enqueue((true, 0));
        plc.ReadSequence.Enqueue((true, 1));
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext { InspectionId = "CF-HS-FULL", TraceStatus = TraceStatus.Full },
            isQualified: false);

        result.Succeeded.Should().BeTrue();
        plc.LastValue(addresses.TraceSavedAddress).Should().Be(1);
        plc.LastValue(addresses.ResultAddress).Should().Be(addresses.NgValue);
    }

    [Fact]
    public async Task CompleteInspectionAsync_ResultAck残留非零时不发布结果()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadSequence.Enqueue((true, 7));
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext { InspectionId = "CF-HS-STALE", TraceStatus = TraceStatus.Full },
            isQualified: true);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("HandshakeV1.AckStale");
        result.SignalName.Should().Be("ResultAck");
        result.Address.Should().Be(addresses.ResultAckAddress);
        plc.Writes.Should().NotContain(write => write.Address == addresses.ResultAddress);
        plc.Writes.Should().NotContain(write => write.Address == addresses.VisionReadyAddress && write.Value == 1);
    }

    [Fact]
    public async Task CompleteInspectionAsync_ResultAck读取失败时FailClosed()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadSequence.Enqueue((false, 0));
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext { InspectionId = "CF-HS-READ-FAIL" },
            isQualified: true);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("HandshakeV1.AckReadFailed");
        plc.Writes.Should().NotContain(write => write.Address == addresses.ResultAddress);
    }

    [Fact]
    public async Task CompleteInspectionAsync_Result载荷写失败时不置Valid且不恢复Ready()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadSequence.Enqueue((true, 0));
        plc.FailWrites.Add(addresses.ResultAddress);
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext { InspectionId = "CF-HS-PAYLOAD" },
            isQualified: true);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("HandshakeV1.WriteFailed");
        result.SignalName.Should().Be("Result");
        plc.Writes.Should().NotContain(write => write.Address == addresses.ResultValidAddress && write.Value == 1);
        plc.Writes.Should().NotContain(write => write.Address == addresses.VisionReadyAddress && write.Value == 1);
    }

    [Fact]
    public async Task CompleteInspectionAsync_结果信号复位失败时不恢复Ready()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadSequence.Enqueue((true, 0));
        plc.ReadSequence.Enqueue((true, 1));
        plc.FailWriteOnAttempt[addresses.ResultValidAddress] = 2;
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext { InspectionId = "CF-HS-RESET", TraceStatus = TraceStatus.Full },
            isQualified: true);

        result.Succeeded.Should().BeFalse();
        result.ResultAckReceived.Should().BeTrue();
        result.SignalName.Should().Be("ResultValid.Reset");
        plc.Writes.Should().NotContain(write => write.Address == addresses.VisionReadyAddress && write.Value == 1);
    }

    [Fact]
    public async Task CompleteInspectionAsync_Ready写失败时整体失败()
    {
        var plc = new SimulatedPlcService();
        PlcHandshakeV1Addresses addresses = CreateAddresses();
        plc.ReadSequence.Enqueue((true, 0));
        plc.ReadSequence.Enqueue((true, 1));
        plc.FailWrites.Add(addresses.VisionReadyAddress);
        var coordinator = new PlcHandshakeV1Coordinator(plc);

        PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
            addresses,
            new InspectionContext { InspectionId = "CF-HS-READY", TraceStatus = TraceStatus.Full },
            isQualified: true);

        result.Succeeded.Should().BeFalse();
        result.ResultAckReceived.Should().BeTrue();
        result.SignalName.Should().Be("VisionReady");
    }

    private static PlcHandshakeV1Addresses CreateAddresses(int resultAckTimeoutMs = 50)
    {
        return new PlcHandshakeV1Addresses
        {
            TriggerAddress = "D555",
            ResultAddress = "D556",
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
            ResultAckTimeoutMs = resultAckTimeoutMs,
            OkValue = 1,
            NgValue = 0
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
        public Dictionary<string, int> FailWriteOnAttempt { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, short> ReadValues { get; } = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);
        public Queue<(bool Success, short Value)> ReadSequence { get; } = new Queue<(bool Success, short Value)>();
        private readonly Dictionary<string, int> _writeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
            _writeCounts.TryGetValue(resultAddress, out int count);
            count++;
            _writeCounts[resultAddress] = count;
            if (FailWrites.Contains(resultAddress) ||
                (FailWriteOnAttempt.TryGetValue(resultAddress, out int failAttempt) && failAttempt == count))
            {
                LastError = $"write failed: {resultAddress}";
                return Task.FromResult(false);
            }

            LastError = null;
            return Task.FromResult(true);
        }
        public Task<(bool Success, short Value)> ReadWordAsync(string address)
        {
            if (ReadSequence.Count > 0)
            {
                var next = ReadSequence.Dequeue();
                if (!next.Success)
                {
                    LastError = $"read failed: {address}";
                }

                return Task.FromResult(next);
            }

            return Task.FromResult((true, ReadValues.TryGetValue(address, out short value) ? value : (short)0));
        }
        public Task<bool> WriteReleaseSignalAsync(string resultAddress) => WriteResultAsync(resultAddress, (short)1);
        public Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName)
            => Task.FromResult((true, string.Empty));
        public void Dispose() { }
    }
}
#pragma warning restore CS0067
