// ============================================================================
// 文件名: 主窗口.Diagnostics.cs
// 描述:   现场诊断中心与单步调试命令处理
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ClearFrost.Services;
using ClearFrost.Yolo;
using OpenCvSharp;

namespace ClearFrost
{
    public partial class 主窗口
    {
        private async Task ExportDiagnosticPackageFromWebAsync(WebUiCommandEventArgs args)
        {
            try
            {
                string outputDirectory = Path.Combine(_storageService.LogBasePath, "Diagnostics");
                string path = await _appRuntime.ExportDiagnosticPackageAsync(
                    outputDirectory,
                    _appShutdownCts.Token).ConfigureAwait(false);

                await _uiController.SendDiagnosticPackageExportResult(new
                {
                    succeeded = true,
                    path,
                    message = $"诊断包已导出: {path}"
                }, args.RequestId).ConfigureAwait(false);
                await _uiController.LogToFrontend($"诊断包已导出: {path}", "success").ConfigureAwait(false);
                await _uiController.SendUiCommand("toast", new
                {
                    message = "诊断包已导出",
                    type = "success",
                    durationMs = 1800
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordHealthError("Diagnostics.Export", $"诊断包导出失败: {ex.Message}");
                await _uiController.SendDiagnosticPackageExportResult(new
                {
                    succeeded = false,
                    path = string.Empty,
                    message = $"诊断包导出失败: {ex.Message}"
                }, args.RequestId).ConfigureAwait(false);
                await _uiController.LogToFrontend($"诊断包导出失败: {ex.Message}", "error").ConfigureAwait(false);
            }
            finally
            {
                await SendHealthSnapshotToFrontendAsync().ConfigureAwait(false);
            }
        }

        private async Task HandleFieldDebugCommandAsync(WebUiCommandEventArgs args)
        {
            switch (args.Command)
            {
                case "field_debug_step_capture":
                    await RunFieldDebugStepCaptureAsync(args).ConfigureAwait(false);
                    break;
                case "field_debug_step_infer":
                    await RunFieldDebugStepInferenceAsync(args).ConfigureAwait(false);
                    break;
                case "field_debug_plc_write_test":
                    await RunFieldDebugPlcWriteTestAsync(args).ConfigureAwait(false);
                    break;
                case "field_debug_barcode_read_test":
                    await RunFieldDebugBarcodeReadTestAsync(args).ConfigureAwait(false);
                    break;
                case "field_debug_simulate_trigger":
                    await RunFieldDebugSimulateTriggerAsync(args).ConfigureAwait(false);
                    break;
                default:
                    await SendFieldDebugResultAsync(
                        args,
                        false,
                        $"未实现的现场调试命令: {args.Command}",
                        "NotImplemented").ConfigureAwait(false);
                    break;
            }
        }

        private async Task RunFieldDebugStepCaptureAsync(WebUiCommandEventArgs args)
        {
            var sw = Stopwatch.StartNew();
            using Mat? frame = await CaptureFieldDebugFrameAsync("手动单步取图", pushPreview: true).ConfigureAwait(false);
            sw.Stop();

            if (frame == null)
            {
                await SendFieldDebugResultAsync(args, false, "单步取图失败: 相机未返回图像", "CaptureFrameFailed").ConfigureAwait(false);
                return;
            }

            await SendFieldDebugResultAsync(args, true, "单步取图成功", details: new
            {
                elapsedMs = sw.ElapsedMilliseconds,
                width = frame.Width,
                height = frame.Height,
                channels = frame.Channels()
            }).ConfigureAwait(false);
        }

        private async Task RunFieldDebugStepInferenceAsync(WebUiCommandEventArgs args)
        {
            if (!_detectionService.IsModelLoaded)
            {
                await SendFieldDebugResultAsync(args, false, "单步推理失败: YOLO 模型未加载", "ModelNotLoaded").ConfigureAwait(false);
                return;
            }

            using Mat? frame = await CaptureFieldDebugFrameAsync("手动单步推理", pushPreview: false).ConfigureAwait(false);
            if (frame == null)
            {
                await SendFieldDebugResultAsync(args, false, "单步推理失败: 相机未返回图像", "CaptureFrameFailed").ConfigureAwait(false);
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _detectionService.DetectAsync(
                    frame,
                    _appConfig.Confidence,
                    _appConfig.IouThreshold).ConfigureAwait(false);
                sw.Stop();

                var results = result.Results ?? new List<YoloResult>();
                string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                using Mat? rendered = TryRenderDetectionMat(frame, results, labels);
                bool success = !result.HasError;
                string summary = success
                    ? $"单步推理完成: {GetDetailedDetectionLog(results, labels)}，耗时 {sw.ElapsedMilliseconds}ms"
                    : $"单步推理失败: {result.ErrorMessage}";

                await _uiController.SendDetectionFrame(
                    rendered ?? frame,
                    success,
                    _statisticsService.Current,
                    summary,
                    success ? "success" : "error",
                    _detectionService.GetLastMetrics(),
                    actualCount: results.Count,
                    usedModelName: result.UsedModelName ?? _detectionService.CurrentModelName,
                    wasFallback: result.WasFallback,
                    totalMs: sw.ElapsedMilliseconds,
                    sourceLabel: "单步推理",
                    fallbackAttemptCount: result.FallbackAttemptCount,
                    fallbackSkippedReason: result.FallbackSkippedReason,
                    inferenceMs: result.ElapsedMs > 0 ? result.ElapsedMs : sw.ElapsedMilliseconds).ConfigureAwait(false);

                await SendFieldDebugResultAsync(args, success, summary, success ? string.Empty : "InferenceFailed", new
                {
                    elapsedMs = sw.ElapsedMilliseconds,
                    inferenceMs = result.ElapsedMs,
                    actualCount = results.Count,
                    model = result.UsedModelName ?? _detectionService.CurrentModelName,
                    result.HasError,
                    result.ErrorMessage
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordHealthError("FieldDebug.Inference", $"单步推理异常: {ex.Message}");
                await SendFieldDebugResultAsync(args, false, $"单步推理异常: {ex.Message}", "InferenceException").ConfigureAwait(false);
            }
        }

        private async Task RunFieldDebugPlcWriteTestAsync(WebUiCommandEventArgs args)
        {
            FieldDebugPayload payload = FieldDebugPayload.Parse(args.PayloadJson);
            string address = string.IsNullOrWhiteSpace(payload.Address)
                ? _appConfig.PlcResultAddress
                : payload.Address.Trim();
            short value = payload.Value ?? _appConfig.PlcOkValue;

            if (string.IsNullOrWhiteSpace(address))
            {
                await SendFieldDebugResultAsync(args, false, "PLC 写入测试失败: 结果地址为空", "PlcAddressEmpty").ConfigureAwait(false);
                return;
            }

            if (!_plcService.IsConnected)
            {
                await SendFieldDebugResultAsync(args, false, "PLC 写入测试失败: PLC 未连接", "PlcNotConnected").ConfigureAwait(false);
                return;
            }

            await AuditFieldDebugPlcWriteAsync(args, OperationAuditStatus.Requested, address, value, "准备执行 PLC 写入测试").ConfigureAwait(false);
            try
            {
                bool success = await _plcService.WriteResultAsync(address, value).ConfigureAwait(false);
                string message = success
                    ? $"PLC 写入测试成功: {address} = {value}"
                    : $"PLC 写入测试失败: {_plcService.LastError ?? "写入未成功"}";

                await AuditFieldDebugPlcWriteAsync(
                    args,
                    success ? OperationAuditStatus.Succeeded : OperationAuditStatus.Failed,
                    address,
                    value,
                    message).ConfigureAwait(false);
                await SendFieldDebugResultAsync(args, success, message, success ? string.Empty : "PlcWriteFailed", new
                {
                    address,
                    value
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await AuditFieldDebugPlcWriteAsync(args, OperationAuditStatus.Failed, address, value, ex.Message).ConfigureAwait(false);
                RecordHealthError("FieldDebug.PLC", $"PLC 写入测试异常: {ex.Message}");
                await SendFieldDebugResultAsync(args, false, $"PLC 写入测试异常: {ex.Message}", "PlcWriteException").ConfigureAwait(false);
            }
        }

        private async Task RunFieldDebugBarcodeReadTestAsync(WebUiCommandEventArgs args)
        {
            FieldDebugPayload payload = FieldDebugPayload.Parse(args.PayloadJson);
            string address = string.IsNullOrWhiteSpace(payload.Address)
                ? _appConfig.BarcodeAddress
                : payload.Address.Trim();
            int wordLength = payload.WordLength ?? _appConfig.BarcodeWordLength;
            string encodingName = string.IsNullOrWhiteSpace(payload.Encoding)
                ? _appConfig.BarcodeEncoding
                : payload.Encoding.Trim();

            if (string.IsNullOrWhiteSpace(address))
            {
                await SendFieldDebugResultAsync(args, false, "条码读取测试失败: 条码地址为空", "BarcodeAddressEmpty").ConfigureAwait(false);
                return;
            }

            if (!_plcService.IsConnected)
            {
                await SendFieldDebugResultAsync(args, false, "条码读取测试失败: PLC 未连接", "PlcNotConnected").ConfigureAwait(false);
                return;
            }

            try
            {
                var (success, value) = await _plcService.ReadStringAsync(
                    address,
                    Math.Clamp(wordLength, 1, 64),
                    string.IsNullOrWhiteSpace(encodingName) ? "ASCII" : encodingName).ConfigureAwait(false);
                string barcode = value?.Trim() ?? string.Empty;
                string message = success
                    ? (string.IsNullOrWhiteSpace(barcode) ? "条码读取成功，但 PLC 条码为空" : $"条码读取成功: {barcode}")
                    : $"条码读取失败: {_plcService.LastError ?? "PLC 返回失败"}";

                await SendFieldDebugResultAsync(args, success, message, success ? string.Empty : "BarcodeReadFailed", new
                {
                    address,
                    wordLength,
                    encoding = string.IsNullOrWhiteSpace(encodingName) ? "ASCII" : encodingName,
                    barcode
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordHealthError("FieldDebug.Barcode", $"条码读取测试异常: {ex.Message}");
                await SendFieldDebugResultAsync(args, false, $"条码读取测试异常: {ex.Message}", "BarcodeReadException").ConfigureAwait(false);
            }
        }

        private async Task RunFieldDebugSimulateTriggerAsync(WebUiCommandEventArgs args)
        {
            await SendFieldDebugResultAsync(args, true, "触发模拟已开始，将按一次现场触发执行完整检测", details: new
            {
                triggerSource = "调试触发模拟"
            }).ConfigureAwait(false);

            try
            {
                await btnCapture_LogicAsync("调试触发模拟").ConfigureAwait(false);
                await SendFieldDebugResultAsync(args, true, "触发模拟已完成").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordHealthError("FieldDebug.Trigger", $"触发模拟异常: {ex.Message}");
                await SendFieldDebugResultAsync(args, false, $"触发模拟异常: {ex.Message}", "TriggerSimulationException").ConfigureAwait(false);
            }
        }

        private async Task<Mat?> CaptureFieldDebugFrameAsync(string triggerSource, bool pushPreview)
        {
            if (!await EnsureCameraReadyForManualInspectionAsync(triggerSource).ConfigureAwait(false))
            {
                return null;
            }

            try
            {
                Mat? frame = _cameraService.CaptureFrame(3000);
                if (frame == null || frame.Empty())
                {
                    frame?.Dispose();
                    RecordHealthError("FieldDebug.Camera", "单步取图失败: 相机未返回图像");
                    return null;
                }

                if (pushPreview)
                {
                    await _uiController.UpdateImage(frame).ConfigureAwait(false);
                }

                return frame;
            }
            catch (Exception ex)
            {
                RecordHealthError("FieldDebug.Camera", $"单步取图异常: {ex.Message}");
                return null;
            }
        }

        private async Task SendFieldDebugResultAsync(
            WebUiCommandEventArgs args,
            bool succeeded,
            string message,
            string errorCode = "",
            object? details = null)
        {
            await _uiController.SendFieldDebugResult(new
            {
                command = args.Command,
                succeeded,
                errorCode,
                message,
                details,
                updatedAt = DateTimeOffset.Now
            }, args.RequestId).ConfigureAwait(false);

            await _uiController.LogToFrontend(message, succeeded ? "info" : "error").ConfigureAwait(false);
            await SendHealthSnapshotToFrontendAsync().ConfigureAwait(false);
        }

        private Task<bool> AuditFieldDebugPlcWriteAsync(
            WebUiCommandEventArgs args,
            OperationAuditStatus status,
            string address,
            short value,
            string details)
        {
            return _operationAuditService.AppendAsync(new OperationAuditRecord
            {
                CorrelationId = string.IsNullOrWhiteSpace(args.RequestId) ? Guid.NewGuid().ToString("N") : args.RequestId,
                Operation = "FieldDebugPlcWriteTest",
                Status = status,
                OperatorId = ResolveCurrentOperatorId(),
                Role = _appConfig.CurrentOperatorRole,
                Reason = "现场调试 PLC 写入测试",
                Details = $"{details}; Address={address}; Value={value}",
                FailureBlocker = status == OperationAuditStatus.Requested ? "ConfirmBeforePlcWriteTest" : string.Empty
            });
        }

        private sealed class FieldDebugPayload
        {
            public string Address { get; init; } = string.Empty;
            public short? Value { get; init; }
            public int? WordLength { get; init; }
            public string Encoding { get; init; } = string.Empty;

            public static FieldDebugPayload Parse(string payloadJson)
            {
                if (string.IsNullOrWhiteSpace(payloadJson))
                {
                    return new FieldDebugPayload();
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(payloadJson);
                    JsonElement root = document.RootElement;
                    return new FieldDebugPayload
                    {
                        Address = ReadString(root, "address", "Address"),
                        Value = ReadInt16(root, "value", "Value"),
                        WordLength = ReadInt32(root, "wordLength", "WordLength"),
                        Encoding = ReadString(root, "encoding", "Encoding")
                    };
                }
                catch
                {
                    return new FieldDebugPayload();
                }
            }

            private static string ReadString(JsonElement root, params string[] names)
            {
                foreach (string name in names)
                {
                    if (root.TryGetProperty(name, out JsonElement element) &&
                        element.ValueKind == JsonValueKind.String)
                    {
                        return element.GetString() ?? string.Empty;
                    }
                }

                return string.Empty;
            }

            private static short? ReadInt16(JsonElement root, params string[] names)
            {
                int? value = ReadInt32(root, names);
                if (!value.HasValue)
                {
                    return null;
                }

                return (short)Math.Clamp(value.Value, short.MinValue, short.MaxValue);
            }

            private static int? ReadInt32(JsonElement root, params string[] names)
            {
                foreach (string name in names)
                {
                    if (!root.TryGetProperty(name, out JsonElement element))
                    {
                        continue;
                    }

                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number))
                    {
                        return number;
                    }

                    if (element.ValueKind == JsonValueKind.String &&
                        int.TryParse(element.GetString(), out int parsed))
                    {
                        return parsed;
                    }
                }

                return null;
            }
        }
    }
}
