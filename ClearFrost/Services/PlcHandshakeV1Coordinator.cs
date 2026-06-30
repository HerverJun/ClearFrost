using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Inspection;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    internal sealed class PlcHandshakeV1Addresses
    {
        public string TriggerAddress { get; init; } = string.Empty;
        public string TriggerAckAddress { get; init; } = string.Empty;
        public string TriggerSeqAddress { get; init; } = string.Empty;
        public string ResultSeqAddress { get; init; } = string.Empty;
        public string VisionOnlineAddress { get; init; } = string.Empty;
        public string VisionReadyAddress { get; init; } = string.Empty;
        public string VisionBusyAddress { get; init; } = string.Empty;
        public string InspectionDoneAddress { get; init; } = string.Empty;
        public string ErrorCodeAddress { get; init; } = string.Empty;
        public string TraceSavedAddress { get; init; } = string.Empty;
        public string HeartbeatAddress { get; init; } = string.Empty;
        public string ResultValidAddress { get; init; } = string.Empty;
        public string ResultAckAddress { get; init; } = string.Empty;
        public int ResultAckTimeoutMs { get; init; }

        public static PlcHandshakeV1Addresses FromConfig(AppConfig config, string? triggerAddress = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return new PlcHandshakeV1Addresses
            {
                TriggerAddress = string.IsNullOrWhiteSpace(triggerAddress) ? config.PlcTriggerAddress : triggerAddress,
                TriggerAckAddress = config.PlcTriggerAckAddress,
                TriggerSeqAddress = config.PlcTriggerSeqAddress,
                ResultSeqAddress = config.PlcResultSeqAddress,
                VisionOnlineAddress = config.PlcVisionOnlineAddress,
                VisionReadyAddress = config.PlcVisionReadyAddress,
                VisionBusyAddress = config.PlcVisionBusyAddress,
                InspectionDoneAddress = config.PlcInspectionDoneAddress,
                ErrorCodeAddress = config.PlcErrorCodeAddress,
                TraceSavedAddress = config.PlcTraceSavedAddress,
                HeartbeatAddress = config.PlcHeartbeatAddress,
                ResultValidAddress = config.PlcResultValidAddress,
                ResultAckAddress = config.PlcResultAckAddress,
                ResultAckTimeoutMs = config.PlcResultAckTimeoutMs
            };
        }
    }

    internal sealed class PlcHandshakeV1Result
    {
        public bool Succeeded { get; init; }
        public string Message { get; init; } = string.Empty;
        public long ElapsedMs { get; init; }
        public bool ResultAckReceived { get; init; }
    }

    internal sealed class PlcHandshakeV1Coordinator
    {
        private readonly IPlcService _plcService;
        private readonly Action<string>? _log;

        public PlcHandshakeV1Coordinator(IPlcService plcService, Action<string>? log = null)
        {
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _log = log;
        }

        public async Task<PlcHandshakeV1Result> AcceptTriggerAsync(
            PlcHandshakeV1Addresses addresses,
            PlcTriggerContext triggerContext,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            string triggerAddress = string.IsNullOrWhiteSpace(triggerContext.TriggerAddress)
                ? addresses.TriggerAddress
                : triggerContext.TriggerAddress;

            bool success =
                await WriteWordAsync(addresses.VisionOnlineAddress, 1, "VisionOnline", cancellationToken).ConfigureAwait(false) &&
                await WriteWordAsync(addresses.VisionReadyAddress, 0, "VisionReady", cancellationToken).ConfigureAwait(false) &&
                await WriteWordAsync(addresses.VisionBusyAddress, 1, "VisionBusy", cancellationToken).ConfigureAwait(false) &&
                await WriteWordAsync(addresses.InspectionDoneAddress, 0, "InspectionDone", cancellationToken).ConfigureAwait(false) &&
                await WriteWordAsync(addresses.ResultValidAddress, 0, "ResultValid", cancellationToken).ConfigureAwait(false) &&
                await WriteWordAsync(addresses.ErrorCodeAddress, 0, "ErrorCode", cancellationToken).ConfigureAwait(false) &&
                await WriteWordAsync(
                    addresses.TriggerAckAddress,
                    triggerContext.TriggerSeq.HasValue ? ClampIntToShort(triggerContext.TriggerSeq.Value) : (short)1,
                    "TriggerAck",
                    cancellationToken).ConfigureAwait(false);

            if (!success)
            {
                await ResetAcceptedAsync(addresses, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return Fail("TriggerAck write failed.", sw.ElapsedMilliseconds);
            }

            if (string.IsNullOrWhiteSpace(triggerAddress) ||
                !await WriteWordAsync(triggerAddress, 0, "Trigger.Clear", cancellationToken).ConfigureAwait(false))
            {
                await ResetAcceptedAsync(addresses, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return Fail("Trigger clear failed.", sw.ElapsedMilliseconds);
            }

            sw.Stop();
            return Ok("Trigger accepted.", sw.ElapsedMilliseconds);
        }

        public async Task<PlcHandshakeV1Result> RejectTriggerAsync(
            PlcHandshakeV1Addresses addresses,
            PlcTriggerContext triggerContext,
            short errorCode,
            bool clearTrigger,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            await WriteWordAsync(addresses.VisionOnlineAddress, 1, "VisionOnline", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.VisionReadyAddress, 0, "VisionReady", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.VisionBusyAddress, 0, "VisionBusy", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.TriggerAckAddress, 0, "TriggerAck.Reset", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.ErrorCodeAddress, errorCode, "ErrorCode", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.ResultValidAddress, 0, "ResultValid", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.InspectionDoneAddress, 0, "InspectionDone", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.HeartbeatAddress, 1, "Heartbeat", cancellationToken).ConfigureAwait(false);

            if (clearTrigger)
            {
                string triggerAddress = string.IsNullOrWhiteSpace(triggerContext.TriggerAddress)
                    ? addresses.TriggerAddress
                    : triggerContext.TriggerAddress;
                if (string.IsNullOrWhiteSpace(triggerAddress) ||
                    !await WriteWordAsync(triggerAddress, 0, "Trigger.ClearRejected", cancellationToken).ConfigureAwait(false))
                {
                    sw.Stop();
                    return Fail("Rejected trigger clear failed.", sw.ElapsedMilliseconds);
                }
            }

            sw.Stop();
            return Ok("Trigger rejected.", sw.ElapsedMilliseconds);
        }

        public async Task<PlcHandshakeV1Result> CompleteInspectionAsync(
            PlcHandshakeV1Addresses addresses,
            InspectionContext context,
            bool isQualified,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            if (!context.ResultSeq.HasValue && context.TriggerSeq.HasValue)
            {
                context.ResultSeq = context.TriggerSeq;
            }

            short errorCode = MapHandshakeErrorCode(context);
            short traceSaved = context.TraceStatus is TraceStatus.Queued or TraceStatus.Full ? (short)1 : (short)0;

            await WriteWordAsync(addresses.VisionBusyAddress, 0, "VisionBusy", cancellationToken).ConfigureAwait(false);
            if (context.ResultSeq.HasValue)
            {
                await WriteWordAsync(addresses.ResultSeqAddress, ClampIntToShort(context.ResultSeq.Value), "ResultSeq", cancellationToken).ConfigureAwait(false);
            }

            await WriteWordAsync(addresses.ErrorCodeAddress, errorCode, "ErrorCode", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.TraceSavedAddress, traceSaved, "TraceSaved", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.ResultValidAddress, 1, "ResultValid", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.InspectionDoneAddress, 1, "InspectionDone", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.HeartbeatAddress, 1, "Heartbeat", cancellationToken).ConfigureAwait(false);

            bool ackReceived = await WaitForResultAckAsync(addresses, cancellationToken).ConfigureAwait(false);
            if (ackReceived)
            {
                await WriteWordAsync(addresses.ResultValidAddress, 0, "ResultValid.Reset", cancellationToken).ConfigureAwait(false);
                await WriteWordAsync(addresses.InspectionDoneAddress, 0, "InspectionDone.Reset", cancellationToken).ConfigureAwait(false);
                if (context.PlcTriggerAccepted)
                {
                    await WriteWordAsync(addresses.TriggerAckAddress, 0, "TriggerAck.Reset", cancellationToken).ConfigureAwait(false);
                }

                await WriteWordAsync(addresses.VisionReadyAddress, 1, "VisionReady", cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            string message = ackReceived
                ? $"Inspection completed: {(isQualified ? "OK" : "NG")}"
                : "ResultAck timeout; VisionReady remains low.";
            return new PlcHandshakeV1Result
            {
                Succeeded = ackReceived,
                Message = message,
                ElapsedMs = sw.ElapsedMilliseconds,
                ResultAckReceived = ackReceived
            };
        }

        public async Task ResetAcceptedAsync(
            PlcHandshakeV1Addresses addresses,
            CancellationToken cancellationToken = default)
        {
            await WriteWordAsync(addresses.VisionBusyAddress, 0, "VisionBusy.Reset", cancellationToken).ConfigureAwait(false);
            await WriteWordAsync(addresses.TriggerAckAddress, 0, "TriggerAck.Reset", cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> WaitForResultAckAsync(
            PlcHandshakeV1Addresses addresses,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(addresses.ResultAckAddress) || addresses.ResultAckTimeoutMs <= 0)
            {
                return true;
            }

            var sw = Stopwatch.StartNew();
            int timeoutMs = Math.Clamp(addresses.ResultAckTimeoutMs, 0, 30000);
            while (sw.ElapsedMilliseconds <= timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (success, value) = await _plcService.ReadWordAsync(addresses.ResultAckAddress).ConfigureAwait(false);
                if (success && value != 0)
                {
                    return true;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        private async Task<bool> WriteWordAsync(
            string address,
            short value,
            string signalName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                _log?.Invoke($"HandshakeV1 address is empty: {signalName}");
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool success = await _plcService.WriteResultAsync(address, value).ConfigureAwait(false);
            if (!success)
            {
                _log?.Invoke($"HandshakeV1 write failed: {signalName}@{address}={value}, {_plcService.LastError ?? "unknown"}");
            }

            return success;
        }

        private static PlcHandshakeV1Result Ok(string message, long elapsedMs)
        {
            return new PlcHandshakeV1Result
            {
                Succeeded = true,
                Message = message,
                ElapsedMs = elapsedMs
            };
        }

        private static PlcHandshakeV1Result Fail(string message, long elapsedMs)
        {
            return new PlcHandshakeV1Result
            {
                Succeeded = false,
                Message = message,
                ElapsedMs = elapsedMs
            };
        }

        private static short MapHandshakeErrorCode(InspectionContext context)
        {
            if (string.IsNullOrWhiteSpace(context.ErrorCode))
            {
                return 0;
            }

            string stage = context.ErrorStage ?? string.Empty;
            if (stage.Contains(nameof(InspectionStage.Capture), StringComparison.OrdinalIgnoreCase)) return 100;
            if (stage.Contains(nameof(InspectionStage.Barcode), StringComparison.OrdinalIgnoreCase)) return 150;
            if (stage.Contains(nameof(InspectionStage.Inference), StringComparison.OrdinalIgnoreCase)) return 200;
            if (stage.Contains(nameof(InspectionStage.RoiFilter), StringComparison.OrdinalIgnoreCase)) return 250;
            if (stage.Contains(nameof(InspectionStage.PlcWrite), StringComparison.OrdinalIgnoreCase)) return 300;
            if (stage.Contains(nameof(InspectionStage.SaveImage), StringComparison.OrdinalIgnoreCase)) return 400;
            if (stage.Contains(nameof(InspectionStage.SaveRecord), StringComparison.OrdinalIgnoreCase)) return 500;
            return 900;
        }

        private static short ClampIntToShort(int value)
        {
            if (value > short.MaxValue) return short.MaxValue;
            if (value < short.MinValue) return short.MinValue;
            return (short)value;
        }
    }
}
