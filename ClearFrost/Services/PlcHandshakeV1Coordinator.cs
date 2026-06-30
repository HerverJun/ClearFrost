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
        public string ResultAddress { get; init; } = string.Empty;
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
        public short OkValue { get; init; } = 1;
        public short NgValue { get; init; } = 0;

        public static PlcHandshakeV1Addresses FromConfig(AppConfig config, string? triggerAddress = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return new PlcHandshakeV1Addresses
            {
                TriggerAddress = string.IsNullOrWhiteSpace(triggerAddress) ? config.PlcTriggerAddress : triggerAddress,
                ResultAddress = config.PlcResultAddress,
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
                ResultAckTimeoutMs = config.PlcResultAckTimeoutMs,
                OkValue = config.PlcOkValue,
                NgValue = config.PlcNgValue
            };
        }
    }

    internal sealed class PlcHandshakeV1Result
    {
        public bool Succeeded { get; init; }
        public string Message { get; init; } = string.Empty;
        public long ElapsedMs { get; init; }
        public bool ResultAckReceived { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string SignalName { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
    }

    internal sealed class PlcHandshakeV1Failure
    {
        public string ErrorCode { get; init; } = string.Empty;
        public string SignalName { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
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

            PlcHandshakeV1Failure? failure =
                await TryWriteWordAsync(addresses.VisionOnlineAddress, 1, "VisionOnline", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.VisionReadyAddress, 0, "VisionReady", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.VisionBusyAddress, 1, "VisionBusy", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.InspectionDoneAddress, 0, "InspectionDone", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.ResultValidAddress, 0, "ResultValid", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.TraceSavedAddress, 0, "TraceSaved", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.ErrorCodeAddress, 0, "ErrorCode", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(
                    addresses.TriggerAckAddress,
                    triggerContext.TriggerSeq.HasValue ? ClampIntToShort(triggerContext.TriggerSeq.Value) : (short)1,
                    "TriggerAck",
                    cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(triggerAddress, 0, "Trigger.Clear", cancellationToken).ConfigureAwait(false);

            if (failure != null)
            {
                await ResetAcceptedAsync(addresses, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return Fail(failure, sw.ElapsedMilliseconds);
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
            PlcHandshakeV1Failure? failure =
                await TryWriteWordAsync(addresses.VisionOnlineAddress, 1, "VisionOnline", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.VisionReadyAddress, 0, "VisionReady", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.VisionBusyAddress, 0, "VisionBusy", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.TriggerAckAddress, 0, "TriggerAck.Reset", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.ErrorCodeAddress, errorCode, "ErrorCode", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.ResultValidAddress, 0, "ResultValid", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.InspectionDoneAddress, 0, "InspectionDone", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.TraceSavedAddress, 0, "TraceSaved", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.HeartbeatAddress, 1, "Heartbeat", cancellationToken).ConfigureAwait(false);

            if (failure == null && clearTrigger)
            {
                string triggerAddress = string.IsNullOrWhiteSpace(triggerContext.TriggerAddress)
                    ? addresses.TriggerAddress
                    : triggerContext.TriggerAddress;
                failure = await TryWriteWordAsync(triggerAddress, 0, "Trigger.ClearRejected", cancellationToken).ConfigureAwait(false);
            }

            if (failure != null)
            {
                sw.Stop();
                return Fail(failure, sw.ElapsedMilliseconds);
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
            short traceSaved = context.TraceStatus == TraceStatus.Full ? (short)1 : (short)0;
            short resultValue = isQualified ? addresses.OkValue : addresses.NgValue;

            PlcHandshakeV1Failure? ackPreconditionFailure = await EnsureResultAckIsZeroAsync(addresses, cancellationToken).ConfigureAwait(false);
            if (ackPreconditionFailure != null)
            {
                sw.Stop();
                return Fail(ackPreconditionFailure, sw.ElapsedMilliseconds);
            }

            PlcHandshakeV1Failure? failure =
                await TryWriteWordAsync(addresses.VisionBusyAddress, 0, "VisionBusy", cancellationToken).ConfigureAwait(false);
            if (context.ResultSeq.HasValue)
            {
                failure ??= await TryWriteWordAsync(addresses.ResultSeqAddress, ClampIntToShort(context.ResultSeq.Value), "ResultSeq", cancellationToken).ConfigureAwait(false);
            }

            failure ??= await TryWriteWordAsync(addresses.ResultAddress, resultValue, "Result", cancellationToken).ConfigureAwait(false);
            failure ??= await TryWriteWordAsync(addresses.ErrorCodeAddress, errorCode, "ErrorCode", cancellationToken).ConfigureAwait(false);
            failure ??= await TryWriteWordAsync(addresses.TraceSavedAddress, traceSaved, "TraceSaved", cancellationToken).ConfigureAwait(false);
            failure ??= await TryWriteWordAsync(addresses.ResultValidAddress, 1, "ResultValid", cancellationToken).ConfigureAwait(false);
            failure ??= await TryWriteWordAsync(addresses.InspectionDoneAddress, 1, "InspectionDone", cancellationToken).ConfigureAwait(false);
            failure ??= await TryWriteWordAsync(addresses.HeartbeatAddress, 1, "Heartbeat", cancellationToken).ConfigureAwait(false);
            if (failure != null)
            {
                sw.Stop();
                return Fail(failure, sw.ElapsedMilliseconds);
            }

            PlcHandshakeV1Failure? ackFailure = await WaitForResultAckAsync(addresses, cancellationToken).ConfigureAwait(false);
            if (ackFailure != null)
            {
                sw.Stop();
                return Fail(ackFailure, sw.ElapsedMilliseconds);
            }

            failure =
                await TryWriteWordAsync(addresses.ResultValidAddress, 0, "ResultValid.Reset", cancellationToken).ConfigureAwait(false) ??
                await TryWriteWordAsync(addresses.InspectionDoneAddress, 0, "InspectionDone.Reset", cancellationToken).ConfigureAwait(false);
            if (failure == null && context.PlcTriggerAccepted)
            {
                failure = await TryWriteWordAsync(addresses.TriggerAckAddress, 0, "TriggerAck.Reset", cancellationToken).ConfigureAwait(false);
            }

            failure ??= await TryWriteWordAsync(addresses.VisionReadyAddress, 1, "VisionReady", cancellationToken).ConfigureAwait(false);
            if (failure != null)
            {
                sw.Stop();
                return Fail(failure, sw.ElapsedMilliseconds, resultAckReceived: true);
            }

            sw.Stop();
            return new PlcHandshakeV1Result
            {
                Succeeded = true,
                Message = $"Inspection completed: {(isQualified ? "OK" : "NG")}",
                ElapsedMs = sw.ElapsedMilliseconds,
                ResultAckReceived = true
            };
        }

        public async Task ResetAcceptedAsync(
            PlcHandshakeV1Addresses addresses,
            CancellationToken cancellationToken = default)
        {
            await TryWriteWordAsync(addresses.VisionBusyAddress, 0, "VisionBusy.Reset", cancellationToken).ConfigureAwait(false);
            await TryWriteWordAsync(addresses.TriggerAckAddress, 0, "TriggerAck.Reset", cancellationToken).ConfigureAwait(false);
        }

        private async Task<PlcHandshakeV1Failure?> EnsureResultAckIsZeroAsync(
            PlcHandshakeV1Addresses addresses,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(addresses.ResultAckAddress) || addresses.ResultAckTimeoutMs <= 0)
            {
                return BuildFailure(
                    "HandshakeV1.AckConfigInvalid",
                    "ResultAck",
                    addresses.ResultAckAddress,
                    "ResultAck address or timeout is invalid.");
            }

            var (failure, value) = await TryReadWordAsync(addresses.ResultAckAddress, "ResultAck.Precheck", cancellationToken).ConfigureAwait(false);
            if (failure != null)
            {
                return failure;
            }

            if (value != 0)
            {
                return BuildFailure(
                    "HandshakeV1.AckStale",
                    "ResultAck",
                    addresses.ResultAckAddress,
                    $"ResultAck is already non-zero before result publish: {value}.");
            }

            return null;
        }

        private async Task<PlcHandshakeV1Failure?> WaitForResultAckAsync(
            PlcHandshakeV1Addresses addresses,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            int timeoutMs = Math.Clamp(addresses.ResultAckTimeoutMs, 0, 30000);
            while (sw.ElapsedMilliseconds <= timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (failure, value) = await TryReadWordAsync(addresses.ResultAckAddress, "ResultAck", cancellationToken).ConfigureAwait(false);
                if (failure != null)
                {
                    return failure;
                }

                if (value != 0)
                {
                    return null;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return BuildFailure(
                "HandshakeV1.AckTimeout",
                "ResultAck",
                addresses.ResultAckAddress,
                $"ResultAck timeout after {timeoutMs}ms.");
        }

        private async Task<PlcHandshakeV1Failure?> TryWriteWordAsync(
            string address,
            short value,
            string signalName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return BuildFailure(
                    "HandshakeV1.AddressEmpty",
                    signalName,
                    address,
                    $"HandshakeV1 address is empty: {signalName}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool success = await _plcService.WriteResultAsync(address, value).ConfigureAwait(false);
                if (!success)
                {
                    return BuildFailure(
                        "HandshakeV1.WriteFailed",
                        signalName,
                        address,
                        $"HandshakeV1 write failed: {signalName}@{address}={value}, {_plcService.LastError ?? "unknown"}");
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return BuildFailure(
                    "HandshakeV1.WriteException",
                    signalName,
                    address,
                    $"HandshakeV1 write exception: {signalName}@{address}={value}, {ex.Message}");
            }
        }

        private async Task<(PlcHandshakeV1Failure? Failure, short Value)> TryReadWordAsync(
            string address,
            string signalName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return (
                    BuildFailure(
                        "HandshakeV1.AddressEmpty",
                        signalName,
                        address,
                        $"HandshakeV1 address is empty: {signalName}"),
                    0);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (success, value) = await _plcService.ReadWordAsync(address).ConfigureAwait(false);
                if (!success)
                {
                    return (
                        BuildFailure(
                            "HandshakeV1.AckReadFailed",
                            signalName,
                            address,
                            $"HandshakeV1 read failed: {signalName}@{address}, {_plcService.LastError ?? "unknown"}"),
                        0);
                }

                return (null, value);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (
                    BuildFailure(
                        "HandshakeV1.AckReadException",
                        signalName,
                        address,
                        $"HandshakeV1 read exception: {signalName}@{address}, {ex.Message}"),
                    0);
            }
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

        private PlcHandshakeV1Failure BuildFailure(
            string errorCode,
            string signalName,
            string address,
            string message)
        {
            var failure = new PlcHandshakeV1Failure
            {
                ErrorCode = errorCode ?? string.Empty,
                SignalName = signalName ?? string.Empty,
                Address = address ?? string.Empty,
                Message = message ?? string.Empty
            };
            _log?.Invoke($"{failure.ErrorCode}: {failure.Message}");
            return failure;
        }

        private static PlcHandshakeV1Result Fail(
            PlcHandshakeV1Failure failure,
            long elapsedMs,
            bool resultAckReceived = false)
        {
            return new PlcHandshakeV1Result
            {
                Succeeded = false,
                Message = failure.Message,
                ElapsedMs = elapsedMs,
                ResultAckReceived = resultAckReceived,
                ErrorCode = failure.ErrorCode,
                SignalName = failure.SignalName,
                Address = failure.Address
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
