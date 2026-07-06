// ============================================================================
// 文件名: InspectionPipelineService.cs
// 描述:   单次检测管线服务
//
// 功能:
//   - 执行条码、取图、推理、ROI、规则、PLC、保存和追溯记录
//   - 返回阶段化检测结果，窗口层只负责 UI 呈现和按钮事件
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.DeepLearning;
using ClearFrost.Core.Inspection;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Yolo;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace ClearFrost.Services
{
    internal readonly record struct InspectionPipelineRequest(
        string TriggerSource,
        string InspectionId,
        int? TriggerSeq,
        InspectionContext Context);

    internal sealed class InspectionPipelineProgress
    {
        public InspectionContext Context { get; init; } = new InspectionContext();
        public InspectionPipelineProgressKind Kind { get; init; }
        public string Message { get; init; } = string.Empty;
        public string Level { get; init; } = "info";
        public bool? IsOk { get; init; }
        public int? ActualCount { get; init; }
        public string? UsedModelName { get; init; }
        public bool WasFallback { get; init; }
        public bool BarcodeEnabled { get; init; }
        public string? ProductBarcode { get; init; }
        public bool? BarcodeReadSucceeded { get; init; }
        public string? BarcodeError { get; init; }
    }

    internal enum InspectionPipelineProgressKind
    {
        Log,
        InspectionUpdate
    }

    internal sealed class InspectionPipelineStageResult
    {
        public InspectionStage Stage { get; init; }
        public bool Succeeded { get; init; }
        public long ElapsedMs { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? ErrorCode { get; init; }
    }

    internal sealed class InspectionPipelineTimings
    {
        public long CaptureMs { get; set; }
        public long InferenceMs { get; set; }
        public long RoiFilterMs { get; set; }
        public long PlcWriteMs { get; set; }
        public long RenderToUiMs { get; set; }
        public long SaveQueueMs { get; set; }
        public long DbWriteMs { get; set; }
        public long HandshakeStartMs { get; set; }
        public long PlcResultWriteMs { get; set; }
        public long HandshakeCompleteMs { get; set; }
    }

    internal sealed class InspectionPipelineResult : IDisposable
    {
        private bool _disposed;

        public InspectionPipelineResult(InspectionContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public InspectionContext Context { get; }
        public InspectionPipelineTimings Timings { get; } = new InspectionPipelineTimings();
        public List<InspectionPipelineStageResult> Stages { get; } = new List<InspectionPipelineStageResult>();
        public bool FinalQualified { get; set; }
        public int FinalResultCount { get; set; }
        public int AttemptCount { get; set; } = 1;
        public string? UsedModelName { get; set; }
        public bool WasFallback { get; set; }
        public bool BarcodeEnabled { get; set; }
        public string? ProductBarcode { get; set; }
        public bool? BarcodeReadSucceeded { get; set; }
        public string? BarcodeError { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public string StatusLevel { get; set; } = "info";
        public int FallbackAttemptCount { get; set; }
        public string FallbackSkippedReason { get; set; } = string.Empty;
        public Mat? Frame { get; set; }
        public Mat? RenderedFrame { get; set; }
        public bool DetectionFailed { get; set; }
        public bool ProductFlowSucceeded { get; set; }
        public object? DetectionMetrics { get; set; }
        public InspectionJudgeResult? JudgeResult { get; set; }
        public DetectionPersistencePayload? PendingRecordPayload { get; set; }
        public bool PendingRecordImageQueued { get; set; }

        public bool HasFrame => Frame != null && !Frame.Empty();

        public void AddStage(
            InspectionStage stage,
            bool succeeded,
            long elapsedMs,
            string message,
            string? errorCode = null)
        {
            Stages.Add(new InspectionPipelineStageResult
            {
                Stage = stage,
                Succeeded = succeeded,
                ElapsedMs = elapsedMs,
                Message = message ?? string.Empty,
                ErrorCode = errorCode
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RenderedFrame?.Dispose();
            Frame?.Dispose();
        }
    }

    internal sealed class InspectionPipelineService
    {
        private readonly AppConfig _appConfig;
        private readonly ICameraService _cameraService;
        private readonly IDetectionService _detectionService;
        private readonly IPlcService _plcService;
        private readonly IStorageService _storageService;
        private readonly IStatisticsService _statisticsService;
        private readonly ImageSaveQueue _imageSaveQueue;
        private readonly DetectionRecordQueue _detectionRecordQueue;
        private readonly RecipeManager _recipeManager;
        private readonly ModelRegistry _modelRegistry;
        private readonly HealthMonitor _healthMonitor;
        private readonly Func<float[]?> _roiSnapshotProvider;
        private readonly Func<string> _activeCameraIdProvider;
        private readonly IInspectionDecisionEvaluator _decisionEvaluator;
        private readonly Action<string>? _diagLog;
        private const int RuntimeCameraRecoverySettleMs = 150;
        private const int RuntimeCameraReconnectSettleMs = 250;
        private const int ShortFrameQuickRetryCount = 2;
        private const int ShortFrameQuickRetryDelayMs = 30;

        public InspectionPipelineService(
            AppConfig appConfig,
            ICameraService cameraService,
            IDetectionService detectionService,
            IPlcService plcService,
            IStorageService storageService,
            IStatisticsService statisticsService,
            ImageSaveQueue imageSaveQueue,
            DetectionRecordQueue detectionRecordQueue,
            RecipeManager recipeManager,
            ModelRegistry modelRegistry,
            HealthMonitor healthMonitor,
            Func<float[]?> roiSnapshotProvider,
            Func<string> activeCameraIdProvider,
            IInspectionDecisionEvaluator? decisionEvaluator = null,
            Action<string>? diagLog = null)
        {
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
            _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));
            _imageSaveQueue = imageSaveQueue ?? throw new ArgumentNullException(nameof(imageSaveQueue));
            _detectionRecordQueue = detectionRecordQueue ?? throw new ArgumentNullException(nameof(detectionRecordQueue));
            _recipeManager = recipeManager ?? throw new ArgumentNullException(nameof(recipeManager));
            _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
            _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
            _roiSnapshotProvider = roiSnapshotProvider ?? throw new ArgumentNullException(nameof(roiSnapshotProvider));
            _activeCameraIdProvider = activeCameraIdProvider ?? throw new ArgumentNullException(nameof(activeCameraIdProvider));
            _decisionEvaluator = decisionEvaluator ?? new InspectionDecisionEvaluator();
            _diagLog = diagLog;
        }

        public async Task<InspectionPipelineResult> ExecuteAsync(
            InspectionPipelineRequest request,
            CancellationToken cancellationToken,
            Func<InspectionPipelineProgress, Task>? progressAsync = null)
        {
            InspectionContext context = request.Context;
            bool plcIoEnabled = ShouldUsePlcIo();
            bool barcodeEnabled = plcIoEnabled && _appConfig.BarcodeEnabled;
            var pipelineResult = new InspectionPipelineResult(context)
            {
                BarcodeEnabled = barcodeEnabled,
                UsedModelName = _detectionService.CurrentModelName
            };

            bool finalHandshakeWritten = false;
            async Task CompleteTerminalHandshakeOnceAsync(bool isQualified)
            {
                if (finalHandshakeWritten || !ShouldWriteTerminalHandshake(context))
                {
                    context.CycleSucceeded = pipelineResult.ProductFlowSucceeded;
                    return;
                }

                finalHandshakeWritten = true;
                PlcHandshakeV1Result result = await WriteHandshakeDetectionCompletedAsync(context, isQualified).ConfigureAwait(false);
                pipelineResult.Timings.HandshakeCompleteMs = context.HandshakeCompleteMs;
                ApplyTerminalHandshakeResult(pipelineResult, result);
            }

            async Task FinalizeTerminalAndRecordAsync()
            {
                await FinalizePipelineAsync(pipelineResult).ConfigureAwait(false);
                await CompleteTerminalHandshakeOnceAsync(pipelineResult.FinalQualified).ConfigureAwait(false);
                pipelineResult.Timings.DbWriteMs = await EnqueuePendingDetectionRecordAsync(
                    pipelineResult,
                    progressAsync).ConfigureAwait(false);
                await FinalizePipelineAsync(pipelineResult).ConfigureAwait(false);
            }

            try
            {
                bool isManualTrigger = IsManualTriggerSource(request.TriggerSource);
                if (isManualTrigger)
                {
                    await PublishLogAsync(
                        progressAsync,
                        context,
                        $"开始检测... ({request.TriggerSource}触发, ID: {request.InspectionId})",
                        "info").ConfigureAwait(false);
                }

                await PublishUpdateAsync(
                    progressAsync,
                    context,
                    message: "检测流程启动",
                    usedModelName: pipelineResult.UsedModelName,
                    barcodeEnabled: barcodeEnabled).ConfigureAwait(false);

                if (barcodeEnabled)
                {
                    bool shouldStop = await ExecuteBarcodeStageAsync(
                        request,
                        pipelineResult,
                        progressAsync).ConfigureAwait(false);
                    if (shouldStop)
                    {
                        await FinalizeTerminalAndRecordAsync().ConfigureAwait(false);
                        return pipelineResult;
                    }
                }

                Mat? frameToProcess = await ExecuteCaptureStageAsync(
                    request,
                    pipelineResult,
                    progressAsync,
                    cancellationToken).ConfigureAwait(false);
                if (frameToProcess == null)
                {
                    await FinalizeTerminalAndRecordAsync().ConfigureAwait(false);
                    return pipelineResult;
                }

                bool keepFrameForUi = false;
                try
                {
                    await ExecuteInferenceAndPersistenceStagesAsync(
                        request,
                        pipelineResult,
                        frameToProcess,
                        isManualTrigger,
                        progressAsync,
                        cancellationToken).ConfigureAwait(false);
                    keepFrameForUi = pipelineResult.HasFrame;
                }
                finally
                {
                    if (!keepFrameForUi)
                    {
                        frameToProcess.Dispose();
                    }
                }

                await FinalizeTerminalAndRecordAsync().ConfigureAwait(false);
                return pipelineResult;
            }
            catch (OperationCanceledException)
            {
                context.MarkFailed(context.CurrentStage, "OperationCanceled", "检测已取消");
                pipelineResult.FinalQualified = false;
                pipelineResult.FinalResultCount = 0;
                pipelineResult.StatusMessage = "检测已取消";
                pipelineResult.StatusLevel = "error";
                pipelineResult.AddStage(context.CurrentStage, false, 0, "检测已取消", "OperationCanceled");
                pipelineResult.PendingRecordPayload ??= BuildDetectionPersistencePayload(
                    context,
                    null,
                    new List<YoloResult>(),
                    0,
                    false,
                    JsonSerializer.Serialize(new
                    {
                        Error = "检测已取消",
                        Stage = context.CurrentStage.ToString(),
                        context.InspectionId
                    }));
                await FinalizeTerminalAndRecordAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                InspectionStage failedStage = context.CurrentStage == InspectionStage.Unknown
                    ? InspectionStage.Inference
                    : context.CurrentStage;
                context.MarkFailed(failedStage, "InspectionPipelineException", ex.Message);
                pipelineResult.FinalQualified = false;
                pipelineResult.FinalResultCount = 0;
                pipelineResult.StatusMessage = ex.Message;
                pipelineResult.StatusLevel = "error";
                pipelineResult.AddStage(failedStage, false, 0, ex.Message, "InspectionPipelineException");
                DiagLog($"检测管线异常[{request.InspectionId}]: {ex.Message}");
                RecordHealthError("InspectionPipeline", ex.Message, request.InspectionId);

                if (!finalHandshakeWritten)
                {
                    pipelineResult.PendingRecordPayload ??= BuildDetectionPersistencePayload(
                        context,
                        null,
                        new List<YoloResult>(),
                        0,
                        false,
                        JsonSerializer.Serialize(new
                        {
                            Error = ex.Message,
                            Stage = failedStage.ToString(),
                            context.InspectionId
                        }));
                    await FinalizeTerminalAndRecordAsync().ConfigureAwait(false);
                }

                await FinalizePipelineAsync(pipelineResult).ConfigureAwait(false);
                return pipelineResult;
            }
        }

        private async Task<bool> ExecuteBarcodeStageAsync(
            InspectionPipelineRequest request,
            InspectionPipelineResult pipelineResult,
            Func<InspectionPipelineProgress, Task>? progressAsync)
        {
            InspectionContext context = request.Context;
            context.CurrentStage = InspectionStage.Barcode;
            await PublishUpdateAsync(
                progressAsync,
                context,
                message: "读取 PLC 条码",
                usedModelName: pipelineResult.UsedModelName,
                barcodeEnabled: true).ConfigureAwait(false);

            var barcodeSw = Stopwatch.StartNew();
            BarcodeReadResult barcode = await ReadBarcodeForInspectionAsync(context).ConfigureAwait(false);
            barcodeSw.Stop();

            pipelineResult.ProductBarcode = barcode.ProductBarcode;
            pipelineResult.BarcodeReadSucceeded = barcode.ReadSucceeded;
            pipelineResult.BarcodeError = barcode.ErrorCode;
            if (pipelineResult.BarcodeReadSucceeded == true && string.IsNullOrWhiteSpace(pipelineResult.ProductBarcode))
            {
                pipelineResult.BarcodeReadSucceeded = false;
                pipelineResult.BarcodeError = "NoBarcode";
            }

            context.ProductBarcode = pipelineResult.ProductBarcode;
            context.BarcodeReadSucceeded = pipelineResult.BarcodeReadSucceeded;
            context.BarcodeError = pipelineResult.BarcodeError;

            bool barcodeFailed = pipelineResult.BarcodeReadSucceeded == false ||
                                 string.IsNullOrWhiteSpace(pipelineResult.ProductBarcode);
            string barcodeMessage = barcodeFailed
                ? (pipelineResult.BarcodeError == "NoBarcode" ? "PLC 条码为空" : barcode.Message ?? "PLC 条码读取失败")
                : "PLC 条码读取成功";
            pipelineResult.AddStage(
                InspectionStage.Barcode,
                !barcodeFailed,
                barcodeSw.ElapsedMilliseconds,
                barcodeMessage,
                barcodeFailed ? pipelineResult.BarcodeError : null);
            await PublishUpdateAsync(
                progressAsync,
                context,
                isOk: barcodeFailed && _appConfig.BarcodeRequired ? false : null,
                message: barcodeMessage,
                usedModelName: pipelineResult.UsedModelName,
                barcodeEnabled: true,
                productBarcode: pipelineResult.ProductBarcode,
                barcodeReadSucceeded: pipelineResult.BarcodeReadSucceeded,
                barcodeError: pipelineResult.BarcodeError).ConfigureAwait(false);

            if (!_appConfig.BarcodeRequired || !barcodeFailed)
            {
                return false;
            }

            string errorCode = pipelineResult.BarcodeError ?? "NoBarcode";
            string detail = errorCode == "NoBarcode"
                ? "PLC 条码为空，已按 NG 处理"
                : "PLC 条码读取失败，已按 NG 处理";
            context.MarkFailed(InspectionStage.Barcode, errorCode, detail);

            await ExecutePlcResultWriteStageAsync(
                pipelineResult,
                false,
                "PLC 已写入 NG",
                "PLC 写入 NG 失败",
                progressAsync).ConfigureAwait(false);

            _statisticsService.RecordDetection(false);
            _storageService.WriteDetectionLog(
                $"InspectionId: {request.InspectionId}{Environment.NewLine}{detail}",
                false);

            context.TotalMs = pipelineResult.Timings.PlcWriteMs;
            DetectionPersistencePayload barcodeFailurePayload = BuildDetectionPersistencePayload(
                context,
                null,
                new List<YoloResult>(),
                0,
                false,
                JsonSerializer.Serialize(new
                {
                    Error = detail,
                    Stage = "Barcode",
                    context.InspectionId,
                    ProductBarcode = pipelineResult.ProductBarcode ?? string.Empty
                }));
            SetPendingDetectionRecord(pipelineResult, barcodeFailurePayload, imageQueued: false);
            context.CurrentStage = InspectionStage.Failed;
            pipelineResult.FinalQualified = false;
            pipelineResult.FinalResultCount = 0;
            pipelineResult.StatusMessage = detail;
            pipelineResult.StatusLevel = "error";
            return true;
        }

        private async Task<Mat?> ExecuteCaptureStageAsync(
            InspectionPipelineRequest request,
            InspectionPipelineResult pipelineResult,
            Func<InspectionPipelineProgress, Task>? progressAsync,
            CancellationToken cancellationToken)
        {
            InspectionContext context = request.Context;
            Mat? frameToProcess = null;
            var captureSw = new Stopwatch();
            try
            {
                context.CurrentStage = InspectionStage.Capture;
                captureSw.Start();
                int maxRetryCount = Math.Clamp(_appConfig.MaxRetryCount, 0, 5);
                int totalAttempts = maxRetryCount + 1;
                int retryDelayMs = Math.Max(0, _appConfig.RetryIntervalMs);

                for (int attempt = 1; attempt <= totalAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pipelineResult.AttemptCount = attempt;
                    frameToProcess = _cameraService.CaptureFrame(3000);
                    DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] CaptureFrame 尝试 {attempt}/{totalAttempts}: {(frameToProcess != null ? "OK" : "FAIL")}");

                    if (frameToProcess != null)
                    {
                        break;
                    }

                    CameraCaptureFailureKind failureKind = GetCameraCaptureFailureKind();
                    if (failureKind == CameraCaptureFailureKind.ShortFrame)
                    {
                        frameToProcess = await TryQuickRetryShortFrameAsync(
                            request,
                            context,
                            progressAsync,
                            cancellationToken).ConfigureAwait(false);
                        if (frameToProcess != null)
                        {
                            break;
                        }

                        failureKind = GetCameraCaptureFailureKind();
                    }

                    bool forceReconnect = failureKind == CameraCaptureFailureKind.ShortFrame;
                    string? recoveryReason = forceReconnect
                        ? GetCameraErrorOrDefault("连续短帧")
                        : null;
                    var recovery = await TryRecoverCameraForCaptureAsync(
                        request,
                        context,
                        progressAsync,
                        cancellationToken,
                        forceReconnect,
                        recoveryReason).ConfigureAwait(false);
                    if (recovery)
                    {
                        frameToProcess = _cameraService.CaptureFrame(3000);
                        DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 相机恢复后取图: {(frameToProcess != null ? "OK" : "FAIL")}");
                        if (frameToProcess != null)
                        {
                            break;
                        }
                    }

                    if (attempt < totalAttempts)
                    {
                        string retryDetail = string.IsNullOrWhiteSpace(_cameraService.LastError)
                            ? "取图失败"
                            : _cameraService.LastError!;
                        await PublishLogAsync(
                            progressAsync,
                            context,
                            $"拍照失败，准备重试 {attempt}/{maxRetryCount}: {retryDetail}",
                            "warning").ConfigureAwait(false);

                        if (retryDelayMs > 0)
                        {
                            await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] CaptureFrame 异常: {ex.Message}");
                Debug.WriteLine($"[InspectionPipeline] 触发拍照失败: {ex.Message}");
            }

            captureSw.Stop();
            pipelineResult.Timings.CaptureMs = captureSw.ElapsedMilliseconds;
            context.CaptureMs = pipelineResult.Timings.CaptureMs;

            if (frameToProcess != null)
            {
                pipelineResult.AddStage(InspectionStage.Capture, true, captureSw.ElapsedMilliseconds, "取图成功");
                return frameToProcess;
            }

            string detail = string.IsNullOrWhiteSpace(_cameraService.LastError)
                ? "无可用图像进行检测，请先打开相机"
                : $"相机拍照失败: {_cameraService.LastError}";
            context.MarkFailed(InspectionStage.Capture, "CaptureFrameFailed", detail);
            pipelineResult.AddStage(InspectionStage.Capture, false, captureSw.ElapsedMilliseconds, detail, "CaptureFrameFailed");
            await PublishLogAsync(
                progressAsync,
                context,
                $"{detail} (ID: {request.InspectionId})",
                "error").ConfigureAwait(false);

            await ExecutePlcResultWriteStageAsync(
                pipelineResult,
                false,
                "PLC 已写入 NG",
                "PLC 写入 NG 失败",
                progressAsync).ConfigureAwait(false);

            _statisticsService.RecordDetection(false);
            _storageService.WriteDetectionLog(
                $"InspectionId: {request.InspectionId}{Environment.NewLine}{detail}",
                false);

            context.TotalMs = pipelineResult.Timings.CaptureMs + pipelineResult.Timings.PlcWriteMs;
            DetectionPersistencePayload captureFailurePayload = BuildDetectionPersistencePayload(
                context,
                null,
                new List<YoloResult>(),
                0,
                false,
                JsonSerializer.Serialize(new
                {
                    Error = detail,
                    Stage = "Capture",
                    context.InspectionId
                }));
            SetPendingDetectionRecord(pipelineResult, captureFailurePayload, imageQueued: false);
            context.CurrentStage = InspectionStage.Failed;
            pipelineResult.FinalQualified = false;
            pipelineResult.FinalResultCount = 0;
            pipelineResult.StatusMessage = detail;
            pipelineResult.StatusLevel = "error";
            return null;
        }

        private async Task<Mat?> TryQuickRetryShortFrameAsync(
            InspectionPipelineRequest request,
            InspectionContext context,
            Func<InspectionPipelineProgress, Task>? progressAsync,
            CancellationToken cancellationToken)
        {
            string firstError = GetCameraErrorOrDefault("SDK 返回短帧");
            DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 相机短帧已丢弃，准备快速补拍: {firstError}");
            await PublishLogAsync(
                progressAsync,
                context,
                $"相机返回短帧，已丢弃并快速补拍: {firstError}",
                "warning").ConfigureAwait(false);

            for (int quickAttempt = 1; quickAttempt <= ShortFrameQuickRetryCount; quickAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShortFrameQuickRetryDelayMs > 0)
                {
                    await Task.Delay(ShortFrameQuickRetryDelayMs, cancellationToken).ConfigureAwait(false);
                }

                Mat? frame = _cameraService.CaptureFrame(3000);
                CameraCaptureFailureKind failureKind = GetCameraCaptureFailureKind();
                DiagLog(
                    $"[{request.TriggerSource}] [{request.InspectionId}] 短帧快速补拍 {quickAttempt}/{ShortFrameQuickRetryCount}: {(frame != null ? "OK" : $"FAIL ({failureKind})")}");

                if (frame != null)
                {
                    await PublishLogAsync(
                        progressAsync,
                        context,
                        "短帧已丢弃，快速补拍成功",
                        "info").ConfigureAwait(false);
                    return frame;
                }

                if (failureKind != CameraCaptureFailureKind.ShortFrame)
                {
                    return null;
                }
            }

            return null;
        }

        private async Task<bool> TryRecoverCameraForCaptureAsync(
            InspectionPipelineRequest request,
            InspectionContext context,
            Func<InspectionPipelineProgress, Task>? progressAsync,
            CancellationToken cancellationToken,
            bool forceReconnect = false,
            string? recoveryReason = null)
        {
            string firstError = string.IsNullOrWhiteSpace(recoveryReason)
                ? GetCameraErrorOrDefault("取图失败")
                : recoveryReason!;
            string recoveryMessage = forceReconnect
                ? $"相机连续返回短帧，正在重连相机: {firstError}"
                : $"相机取图失败，正在自动恢复: {firstError}";
            DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] {recoveryMessage}");
            await PublishLogAsync(
                progressAsync,
                context,
                recoveryMessage,
                "warning").ConfigureAwait(false);

            string restartError = string.Empty;
            if (!forceReconnect && TryRestartCameraCapture(out restartError))
            {
                await Task.Delay(RuntimeCameraRecoverySettleMs, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (!forceReconnect && !string.IsNullOrWhiteSpace(restartError))
            {
                DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 恢复采集失败: {restartError}");
            }

            CameraConfig? activeCamera = _appConfig.ActiveCamera ?? _appConfig.EnsureActiveCameraConfigFromLegacy();
            if (activeCamera == null || string.IsNullOrWhiteSpace(activeCamera.SerialNumber))
            {
                DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 无活动相机配置，无法重连");
                return false;
            }

            try
            {
                await PublishLogAsync(
                    progressAsync,
                    context,
                    forceReconnect
                        ? "连续短帧，正在尝试重连相机"
                        : "相机采集未恢复，正在尝试重连相机",
                    "warning").ConfigureAwait(false);

                if (_cameraService is CameraService concreteCameraService)
                {
                    concreteCameraService.ReleaseCurrentCamera();
                }
                else
                {
                    _cameraService.Close();
                }
                await Task.Delay(RuntimeCameraRecoverySettleMs, cancellationToken).ConfigureAwait(false);

                bool opened = _cameraService.Open(activeCamera.SerialNumber, activeCamera.Manufacturer);
                if (!opened)
                {
                    string openError = GetCameraErrorOrDefault("相机重连失败");
                    DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 相机重连失败: {openError}");
                    return false;
                }

                _cameraService.StartCapture();
                await Task.Delay(RuntimeCameraReconnectSettleMs, cancellationToken).ConfigureAwait(false);
                DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 相机重连完成，准备重新取图");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 相机重连异常: {ex.Message}");
                return false;
            }
        }

        private bool TryRestartCameraCapture(out string error)
        {
            error = string.Empty;

            try
            {
                if (!_cameraService.IsOpen)
                {
                    error = "相机未打开";
                    return false;
                }

                _cameraService.StartCapture();
                if (_cameraService.IsGrabbing)
                {
                    return true;
                }

                error = GetCameraErrorOrDefault("相机未进入采集状态");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private CameraCaptureFailureKind GetCameraCaptureFailureKind()
        {
            if (_cameraService is ICameraCaptureDiagnostics diagnostics)
            {
                return diagnostics.LastCaptureFailureKind;
            }

            return IsShortFrameError(_cameraService.LastError)
                ? CameraCaptureFailureKind.ShortFrame
                : CameraCaptureFailureKind.None;
        }

        private static bool IsShortFrameError(string? message)
        {
            return !string.IsNullOrWhiteSpace(message) &&
                message.TrimStart().StartsWith("SDK 帧长度不足", StringComparison.Ordinal);
        }

        private string GetCameraErrorOrDefault(string fallback)
        {
            return string.IsNullOrWhiteSpace(_cameraService.LastError)
                ? fallback
                : _cameraService.LastError!;
        }

        private async Task ExecuteInferenceAndPersistenceStagesAsync(
            InspectionPipelineRequest request,
            InspectionPipelineResult pipelineResult,
            Mat frameToProcess,
            bool isManualTrigger,
            Func<InspectionPipelineProgress, Task>? progressAsync,
            CancellationToken cancellationToken)
        {
            InspectionContext context = request.Context;
            List<ImageSavePayload>? imagePayloads = null;
            DetectionPersistencePayload? persistencePayload = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.CurrentStage = InspectionStage.Inference;
                var inferSw = Stopwatch.StartNew();
                InspectionRuleSet ruleSet = _appConfig.GetInspectionRuleSet();
                string ruleSetJson = InspectionRuleSetSerializer.Serialize(ruleSet);
                InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
                float[]? roiSnapshot = _roiSnapshotProvider();
                MultiModelCandidateEvaluator candidateEvaluator = _decisionEvaluator.CreateCandidateEvaluator(
                    ruleSet,
                    frameToProcess.Width,
                    frameToProcess.Height,
                    roiSnapshot);
                DetectionResultData result;
                using (await DetectionRuntimeConcurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    result = await _detectionService.DetectAsync(
                        frameToProcess,
                        _appConfig.Confidence,
                        _appConfig.IouThreshold,
                        fallbackGoal,
                        candidateEvaluator).ConfigureAwait(false);
                }
                ApplyRuleTraceSnapshot(result, ruleSetJson, fallbackGoal);
                inferSw.Stop();
                pipelineResult.Timings.InferenceMs = inferSw.ElapsedMilliseconds;
                context.InferenceMs = pipelineResult.Timings.InferenceMs;
                pipelineResult.DetectionMetrics = _detectionService.GetLastMetrics();

                bool isQualified = result.IsQualified;
                List<YoloResult> results = result.Results ?? new List<YoloResult>();
                bool detectionFailed = result.HasError;
                pipelineResult.DetectionFailed = detectionFailed;
                pipelineResult.UsedModelName = string.IsNullOrWhiteSpace(result.UsedModelName)
                    ? _detectionService.CurrentModelName
                    : result.UsedModelName;
                pipelineResult.WasFallback = result.WasFallback;
                pipelineResult.FallbackAttemptCount = result.FallbackAttemptCount;
                pipelineResult.FallbackSkippedReason = result.FallbackSkippedReason ?? string.Empty;
                context.FallbackAttemptCount = result.FallbackAttemptCount;
                context.FallbackSkippedReason = result.FallbackSkippedReason ?? string.Empty;
                pipelineResult.AddStage(
                    InspectionStage.Inference,
                    !detectionFailed,
                    inferSw.ElapsedMilliseconds,
                    detectionFailed ? result.ErrorMessage : "推理完成",
                    detectionFailed ? "DetectionServiceError" : null);

                context.CurrentStage = InspectionStage.RoiFilter;
                var roiSw = Stopwatch.StartNew();
                InspectionDecisionResult decision = _decisionEvaluator.Evaluate(new InspectionDecisionRequest
                {
                    RuleSet = ruleSet,
                    Detections = results,
                    Labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>(),
                    ImageWidth = frameToProcess.Width,
                    ImageHeight = frameToProcess.Height,
                    Roi = roiSnapshot
                });
                results = decision.FilteredDetections.ToList();
                roiSw.Stop();
                pipelineResult.Timings.RoiFilterMs = roiSw.ElapsedMilliseconds;
                context.RoiMs = pipelineResult.Timings.RoiFilterMs;
                pipelineResult.FinalResultCount = results.Count;
                pipelineResult.AddStage(
                    InspectionStage.RoiFilter,
                    decision.Succeeded,
                    roiSw.ElapsedMilliseconds,
                    decision.Succeeded ? $"ROI 后目标数: {results.Count}" : decision.Message,
                    decision.Succeeded ? null : decision.ErrorCode);

                string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                if (!decision.Succeeded)
                {
                    isQualified = false;
                    result.JudgeResult = decision.JudgeResult;
                    pipelineResult.JudgeResult = decision.JudgeResult;
                    result.IsRuleEvaluated = true;
                    result.IsQualified = false;
                    if (string.IsNullOrWhiteSpace(context.ErrorCode))
                    {
                        context.SetError(InspectionStage.RoiFilter, decision.ErrorCode, decision.Message);
                    }

                    await PublishLogAsync(
                        progressAsync,
                        context,
                        $"ROI/规则判定失败({request.InspectionId})，已强制判定为不合格: {decision.Message}",
                        "error").ConfigureAwait(false);
                }
                else if (detectionFailed)
                {
                    isQualified = false;
                    if (string.IsNullOrWhiteSpace(context.ErrorCode))
                    {
                        context.SetError(InspectionStage.Inference, "DetectionServiceError", result.ErrorMessage);
                    }

                    await PublishLogAsync(
                        progressAsync,
                        context,
                        $"检测失败({request.InspectionId})，已强制判定为不合格: {result.ErrorMessage}",
                        "error").ConfigureAwait(false);
                }
                else
                {
                    InspectionJudgeResult judgeResult = decision.JudgeResult;
                    result.JudgeResult = judgeResult;
                    pipelineResult.JudgeResult = judgeResult;
                    result.IsRuleEvaluated = true;
                    result.IsQualified = judgeResult.IsQualified;
                    isQualified = judgeResult.IsQualified;
                    string judgeMessage = $"规则判定({request.InspectionId}): {(judgeResult.IsQualified ? "OK" : "NG")} | {judgeResult.Summary}";
                    DiagLog(judgeMessage);
                    pipelineResult.AddStage(
                        InspectionStage.RoiFilter,
                        judgeResult.IsQualified,
                        0,
                        judgeResult.Summary);
                    if (isManualTrigger)
                    {
                        await PublishLogAsync(
                            progressAsync,
                            context,
                            judgeMessage,
                            judgeResult.IsQualified ? "info" : "warning").ConfigureAwait(false);
                    }
                }

                pipelineResult.FinalQualified = isQualified;
                pipelineResult.ProductFlowSucceeded = !detectionFailed;
                context.ResultSeq = context.TriggerSeq;

                await ExecutePlcResultWriteStageAsync(
                    pipelineResult,
                    isQualified,
                    "PLC 结果写入完成",
                    "PLC 结果写入失败",
                    progressAsync).ConfigureAwait(false);

                Mat? renderedMat = TryRenderDetectionMat(frameToProcess, results, labels);
                _statisticsService.RecordDetection(isQualified);

                string objDesc = GetDetailedDetectionLog(results, labels, result.JudgeResult);
                string modelInfo = BuildFallbackStatus(result);
                string ruleInfo = BuildRuleStatus(result.JudgeResult);
                pipelineResult.StatusMessage = detectionFailed
                    ? $"[{request.TriggerSource}] ID {request.InspectionId} 检测失败，已判定为不合格: {result.ErrorMessage} | {pipelineResult.Timings.InferenceMs}ms"
                    : $"[{request.TriggerSource}] ID {request.InspectionId} 检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc}{ruleInfo} | {pipelineResult.Timings.InferenceMs}ms{modelInfo}";
                pipelineResult.StatusLevel = isQualified && !detectionFailed ? "success" : "error";

                imagePayloads = CreateImageSavePayloads(
                    context,
                    frameToProcess,
                    isQualified,
                    renderedMat);
                context.TotalMs = pipelineResult.Timings.CaptureMs +
                                  pipelineResult.Timings.InferenceMs +
                                  pipelineResult.Timings.RoiFilterMs +
                                  pipelineResult.Timings.PlcWriteMs;
                persistencePayload = BuildDetectionPersistencePayload(context, result, results, pipelineResult.FinalResultCount, isQualified);

                pipelineResult.Frame = frameToProcess;
                pipelineResult.RenderedFrame = renderedMat;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                InspectionStage failedStage = context.CurrentStage == InspectionStage.Unknown
                    ? InspectionStage.Inference
                    : context.CurrentStage;
                context.MarkFailed(failedStage, "DetectionCycleException", ex.Message);
                DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 检测流程异常: {ex.Message}");
                await PublishLogAsync(
                    progressAsync,
                    context,
                    $"检测异常({request.InspectionId}): {ex.Message}",
                    "error").ConfigureAwait(false);

                if (failedStage is InspectionStage.Inference or InspectionStage.RoiFilter)
                {
                    await ExecutePlcResultWriteStageAsync(
                        pipelineResult,
                        false,
                        "PLC 已写入 NG",
                        "PLC 写入 NG 失败",
                        progressAsync).ConfigureAwait(false);
                }

                _statisticsService.RecordDetection(false);
                _storageService.WriteDetectionLog(
                    $"InspectionId: {request.InspectionId}{Environment.NewLine}检测流程异常: {ex.Message}",
                    false);

                imagePayloads = CreateImageSavePayloads(
                    context,
                    frameToProcess,
                    false);
                (bool errorImageQueued, pipelineResult.Timings.SaveQueueMs) = await EnqueueImagePayloadsAsync(
                    context,
                    imagePayloads,
                    progressAsync).ConfigureAwait(false);
                context.TotalMs = pipelineResult.Timings.CaptureMs +
                                  pipelineResult.Timings.InferenceMs +
                                  pipelineResult.Timings.RoiFilterMs +
                                  pipelineResult.Timings.PlcWriteMs +
                                  pipelineResult.Timings.RenderToUiMs +
                                  pipelineResult.Timings.SaveQueueMs;
                DetectionPersistencePayload errorPayload = BuildDetectionPersistencePayload(
                    context,
                    null,
                    new List<YoloResult>(),
                    0,
                    false,
                    JsonSerializer.Serialize(new
                    {
                        Error = ex.Message,
                        Stage = failedStage.ToString(),
                        context.InspectionId
                    }));
                SetPendingDetectionRecord(pipelineResult, errorPayload, errorImageQueued);
                context.CurrentStage = InspectionStage.Failed;
                pipelineResult.FinalQualified = false;
                pipelineResult.FinalResultCount = 0;
                pipelineResult.StatusMessage = ex.Message;
                pipelineResult.StatusLevel = "error";
                pipelineResult.AddStage(failedStage, false, 0, ex.Message, "DetectionCycleException");
                return;
            }

            bool imageQueuedForRecord;
            (imageQueuedForRecord, pipelineResult.Timings.SaveQueueMs) = await EnqueueImagePayloadsAsync(
                context,
                imagePayloads,
                progressAsync).ConfigureAwait(false);

            context.TotalMs = pipelineResult.Timings.CaptureMs +
                              pipelineResult.Timings.InferenceMs +
                              pipelineResult.Timings.RoiFilterMs +
                              pipelineResult.Timings.PlcWriteMs +
                              pipelineResult.Timings.RenderToUiMs +
                              pipelineResult.Timings.SaveQueueMs;
            if (persistencePayload != null)
            {
                persistencePayload.TotalMs = context.TotalMs;
                persistencePayload.SaveImageMs = context.SaveImageMs;
                SetPendingDetectionRecord(pipelineResult, persistencePayload, imageQueuedForRecord);
            }
            else
            {
                context.TraceStatus = ResolveTraceStatus(imageQueuedForRecord, recordQueued: false);
                await PublishLogAsync(
                    progressAsync,
                    context,
                    $"检测记录构造失败({request.InspectionId})",
                    "error").ConfigureAwait(false);
                DiagLog($"[{request.TriggerSource}] [{request.InspectionId}] 检测记录构造失败");
            }

            context.CurrentStage = InspectionStage.Completed;
        }

        private async Task FinalizePipelineAsync(InspectionPipelineResult result)
        {
            result.Timings.HandshakeStartMs = result.Context.HandshakeStartMs;
            result.Timings.PlcResultWriteMs = result.Context.PlcResultWriteMs;
            result.Timings.HandshakeCompleteMs = result.Context.HandshakeCompleteMs;
            result.Context.ImageQueuePending = _imageSaveQueue.PendingCount;
            result.Context.RecordQueuePending = _detectionRecordQueue.PendingCount;
            result.Context.TotalMs = result.Timings.CaptureMs +
                                     result.Timings.InferenceMs +
                                     result.Timings.RoiFilterMs +
                                     result.Timings.PlcWriteMs +
                                     result.Timings.RenderToUiMs +
                                     result.Timings.SaveQueueMs +
                                     result.Timings.DbWriteMs;

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task<BarcodeReadResult> ReadBarcodeForInspectionAsync(InspectionContext context)
        {
            if (!ShouldUsePlcIo())
            {
                return new BarcodeReadResult(null, false, "PlcIoSkipped", "非 PLC 触发模式，已跳过 PLC 条码读取");
            }

            try
            {
                var (success, value) = await _plcService.ReadStringAsync(
                    _appConfig.BarcodeAddress,
                    _appConfig.BarcodeWordLength,
                    _appConfig.BarcodeEncoding).ConfigureAwait(false);
                string barcode = value?.Trim() ?? string.Empty;
                if (!success)
                {
                    string message = string.IsNullOrWhiteSpace(_plcService.LastError)
                        ? "PLC 条码读取失败"
                        : _plcService.LastError!;
                    RecordHealthError("PLC.Barcode", message, context.InspectionId);
                    return new BarcodeReadResult(null, false, "BarcodeReadFailed", message);
                }

                if (string.IsNullOrWhiteSpace(barcode))
                {
                    return new BarcodeReadResult(string.Empty, false, "NoBarcode", "PLC 条码为空");
                }

                return new BarcodeReadResult(barcode, true, null, "PLC 条码读取成功");
            }
            catch (Exception ex)
            {
                RecordHealthError("PLC.Barcode", $"PLC 条码读取异常: {ex.Message}", context.InspectionId);
                return new BarcodeReadResult(null, false, "BarcodeReadFailed", ex.Message);
            }
        }

        private Mat? TryRenderDetectionMat(Mat sourceImage, List<YoloResult> results, string[] labels)
        {
            if (results == null || results.Count == 0)
            {
                return null;
            }

            if (YoloDetector.IndustrialRenderMode)
            {
                Mat? matResult = (_detectionService as DetectionService)?.GenerateResultMat(sourceImage, results, labels);
                if (matResult != null)
                {
                    return matResult;
                }
            }

            using var bitmap = sourceImage.ToBitmap();
            using var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels);
            return BitmapConverter.ToMat(resultImage);
        }

        private List<ImageSavePayload>? CreateImageSavePayloads(
            InspectionContext context,
            Mat image,
            bool isQualified,
            Mat? renderedImage = null)
        {
            var payloads = new List<ImageSavePayload>();
            try
            {
                DateTime now = context.TriggerTime.LocalDateTime;
                string subFolder = isQualified ? "Qualified" : "Unqualified";
                string dateFolder = now.ToString("yyyy年MM月dd日");
                string hourFolder = now.ToString("HH");
                string directory = Path.Combine(Path_Images, subFolder, dateFolder, hourFolder);

                Directory.CreateDirectory(directory);

                string safeInspectionId = string.IsNullOrWhiteSpace(context.InspectionId)
                    ? InspectionIdGenerator.Next(context.TriggerSource)
                    : context.InspectionId;
                string fileName = BuildTraceImageFileName(isQualified, safeInspectionId, context.ProductBarcode);
                string filePath = Path.Combine(directory, fileName);
                context.ImagePath = filePath;
                payloads.Add(ImageSavePayload.CreateReadOnlyView(
                    image,
                    filePath,
                    jpegQuality: 70,
                    purpose: ImageSavePurpose.TraceOriginal));

                if (renderedImage != null && !renderedImage.Empty())
                {
                    string renderedDirectory = Path.Combine(directory, "Rendered");
                    Directory.CreateDirectory(renderedDirectory);

                    string renderedFileName = AddFileNameSuffix(fileName, "_rendered");
                    string renderedPath = Path.Combine(renderedDirectory, renderedFileName);
                    context.RenderedImagePath = renderedPath;
                    payloads.Add(ImageSavePayload.CreateReadOnlyView(
                        renderedImage,
                        renderedPath,
                        jpegQuality: 95,
                        purpose: ImageSavePurpose.TraceRendered));
                }

                return payloads;
            }
            catch (Exception ex)
            {
                foreach (ImageSavePayload payload in payloads)
                {
                    payload.Dispose();
                }

                Debug.WriteLine($"保存检测图像失败: {ex.Message}");
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.SaveImage, "ImagePayloadCreateFailed", ex.Message);
                }

                return null;
            }
        }

        private async Task<(bool Queued, long ElapsedMs)> EnqueueImagePayloadsAsync(
            InspectionContext context,
            List<ImageSavePayload>? payloads,
            Func<InspectionPipelineProgress, Task>? progressAsync)
        {
            context.CurrentStage = InspectionStage.SaveImage;
            var saveSw = Stopwatch.StartNew();

            if (payloads == null || payloads.Count == 0)
            {
                saveSw.Stop();
                context.SaveImageMs = saveSw.ElapsedMilliseconds;
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.SaveImage, "ImagePayloadMissing", "图像保存载荷为空");
                }

                RecordHealthError("ImageSaveQueue", "图像保存入队失败: 载荷为空", context.InspectionId);
                await PublishLogAsync(
                    progressAsync,
                    context,
                    $"图像保存入队失败({context.InspectionId}): 载荷为空",
                    "error").ConfigureAwait(false);
                DiagLog($"[{context.TriggerSource}] [{context.InspectionId}] 图像保存入队失败: 载荷为空");
                return (false, context.SaveImageMs);
            }

            bool imageQueued = true;
            foreach (ImageSavePayload payload in payloads)
            {
                if (!_imageSaveQueue.Enqueue(payload))
                {
                    payload.Dispose();
                    imageQueued = false;
                }
            }

            saveSw.Stop();
            context.SaveImageMs = saveSw.ElapsedMilliseconds;
            context.ImageQueuePending = _imageSaveQueue.PendingCount;

            if (!imageQueued)
            {
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.SaveImage, "ImageQueueFull", "图像保存队列入队失败");
                }

                Debug.WriteLine("[InspectionPipeline] 图像保存入队失败");
                DiagLog($"[{context.TriggerSource}] [{context.InspectionId}] 图像保存入队失败");
                RecordHealthError("ImageSaveQueue", "图像保存队列入队失败", context.InspectionId);
                await PublishLogAsync(
                    progressAsync,
                    context,
                    $"图像保存入队失败({context.InspectionId})",
                    "error").ConfigureAwait(false);
            }

            return (imageQueued, context.SaveImageMs);
        }

        private static void SetPendingDetectionRecord(
            InspectionPipelineResult pipelineResult,
            DetectionPersistencePayload payload,
            bool imageQueued)
        {
            InspectionContext context = pipelineResult.Context;
            pipelineResult.PendingRecordPayload = payload;
            pipelineResult.PendingRecordImageQueued = imageQueued;
            context.TraceStatus = ResolveTraceStatus(imageQueued, recordQueued: false);
            payload.TraceStatus = context.TraceStatus;
            payload.QueueStatus = BuildQueueStatus(context, imageQueued, recordQueued: false);
        }

        private async Task<long> EnqueuePendingDetectionRecordAsync(
            InspectionPipelineResult pipelineResult,
            Func<InspectionPipelineProgress, Task>? progressAsync)
        {
            if (pipelineResult.PendingRecordPayload == null)
            {
                return 0;
            }

            DetectionPersistencePayload payload = pipelineResult.PendingRecordPayload;
            InspectionContext context = pipelineResult.Context;
            payload.TerminalHandshakeAttempted = context.TerminalHandshakeAttempted;
            payload.TerminalHandshakeSucceeded = context.TerminalHandshakeSucceeded;
            payload.TerminalHandshakeErrorCode = context.TerminalHandshakeErrorCode;
            payload.TerminalHandshakeSignalName = context.TerminalHandshakeSignalName;
            payload.TerminalHandshakeAddress = context.TerminalHandshakeAddress;
            payload.TerminalHandshakeMessage = context.TerminalHandshakeMessage;
            payload.CycleSucceeded = context.CycleSucceeded;
            return await EnqueueDetectionRecordAsync(
                context,
                payload,
                pipelineResult.PendingRecordImageQueued,
                progressAsync).ConfigureAwait(false);
        }

        private async Task<long> EnqueueDetectionRecordAsync(
            InspectionContext context,
            DetectionPersistencePayload payload,
            bool imageQueued,
            Func<InspectionPipelineProgress, Task>? progressAsync)
        {
            context.CurrentStage = InspectionStage.SaveRecord;
            payload.ImagePath = context.ImagePath ?? string.Empty;
            payload.RenderedImagePath = context.RenderedImagePath ?? string.Empty;
            payload.TraceImagePath = string.IsNullOrWhiteSpace(context.RenderedImagePath)
                ? context.ImagePath ?? string.Empty
                : context.RenderedImagePath;
            payload.ErrorStage = context.ErrorStage ?? string.Empty;
            payload.ErrorCode = context.ErrorCode ?? string.Empty;
            payload.ErrorMessage = context.ErrorMessage ?? string.Empty;
            payload.TotalMs = context.TotalMs;
            payload.SaveImageMs = context.SaveImageMs;
            payload.TraceStatus = ResolveTraceStatus(imageQueued, recordQueued: true);
            payload.QueueStatus = BuildQueueStatus(context, imageQueued, recordQueued: true);

            var dbSw = Stopwatch.StartNew();
            bool dbQueued = _detectionRecordQueue.Enqueue(payload);
            dbSw.Stop();
            context.SaveRecordMs = dbSw.ElapsedMilliseconds;
            payload.SaveRecordMs = context.SaveRecordMs;
            context.TraceStatus = ResolveTraceStatus(imageQueued, dbQueued);
            payload.QueueStatus = BuildQueueStatus(context, imageQueued, dbQueued);
            context.RecordQueuePending = _detectionRecordQueue.PendingCount;

            if (!dbQueued)
            {
                Debug.WriteLine("[InspectionPipeline] 检测记录入队失败");
                DiagLog($"[{context.TriggerSource}] [{context.InspectionId}] 检测记录入队失败");
                RecordHealthError("DetectionRecordQueue", "检测记录队列入队失败", context.InspectionId);
                await PublishLogAsync(
                    progressAsync,
                    context,
                    $"检测记录入队失败({context.InspectionId})",
                    "error").ConfigureAwait(false);
            }

            return context.SaveRecordMs;
        }

        private DetectionPersistencePayload BuildDetectionPersistencePayload(
            InspectionContext context,
            DetectionResultData? result,
            List<YoloResult> results,
            int actualCount,
            bool isQualified,
            string? resultJsonOverride = null)
        {
            string usedModelName = result?.UsedModelName ?? _detectionService.CurrentModelName;
            ModelRegistryEntry? modelEntry = ResolveRuntimeModelRegistryEntry(
                usedModelName,
                result?.WasFallback ?? false);
            string fallbackModelId = string.IsNullOrWhiteSpace(usedModelName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(usedModelName);
            string recipeId = _recipeManager.CurrentRecipe?.RecipeId ?? "default";
            string recipeVersion = _recipeManager.CurrentRecipe?.Version ?? string.Empty;
            string traceTargetLabel = result?.TargetLabel ?? string.Empty;
            int traceExpectedCount = result?.ExpectedCount ?? 0;
            string ruleSetJson = !string.IsNullOrWhiteSpace(result?.RuleSetJson)
                ? result!.RuleSetJson
                : _appConfig.InspectionRuleSetJson ?? string.Empty;

            return new DetectionPersistencePayload
            {
                Timestamp = context.TriggerTime.LocalDateTime,
                IsQualified = isQualified,
                InspectionId = context.InspectionId,
                TriggerSource = context.TriggerSource,
                TriggerSeq = context.TriggerSeq,
                PlcTriggerSeq = context.TriggerSeq,
                ResultSeq = context.ResultSeq,
                TerminalHandshakeAttempted = context.TerminalHandshakeAttempted,
                TerminalHandshakeSucceeded = context.TerminalHandshakeSucceeded,
                TerminalHandshakeErrorCode = context.TerminalHandshakeErrorCode,
                TerminalHandshakeSignalName = context.TerminalHandshakeSignalName,
                TerminalHandshakeAddress = context.TerminalHandshakeAddress,
                TerminalHandshakeMessage = context.TerminalHandshakeMessage,
                CycleSucceeded = context.CycleSucceeded,
                ProductBarcode = context.ProductBarcode ?? string.Empty,
                Barcode = context.ProductBarcode ?? string.Empty,
                BarcodeReadSucceeded = context.BarcodeReadSucceeded,
                BarcodeError = context.BarcodeError ?? string.Empty,
                TraceStatus = context.TraceStatus,
                QueueStatus = BuildQueueStatus(context, imageQueued: false, recordQueued: false),
                ImagePath = context.ImagePath ?? string.Empty,
                RenderedImagePath = context.RenderedImagePath ?? string.Empty,
                TraceImagePath = string.IsNullOrWhiteSpace(context.RenderedImagePath)
                    ? context.ImagePath ?? string.Empty
                    : context.RenderedImagePath,
                ErrorStage = context.ErrorStage ?? string.Empty,
                ErrorCode = context.ErrorCode ?? string.Empty,
                ErrorMessage = context.ErrorMessage ?? string.Empty,
                TotalMs = context.TotalMs,
                CaptureMs = context.CaptureMs,
                RoiMs = context.RoiMs,
                PlcWriteMs = context.PlcWriteMs,
                SaveImageMs = context.SaveImageMs,
                SaveRecordMs = context.SaveRecordMs,
                RecipeId = recipeId,
                RecipeVersion = recipeVersion,
                ModelId = modelEntry?.ModelId ?? fallbackModelId,
                ModelVersion = modelEntry?.Version ?? _appConfig.ModelVersion.ToString(CultureInfo.InvariantCulture),
                ModelHash = modelEntry?.ModelHash ?? string.Empty,
                WasFallback = result?.WasFallback ?? false,
                UsedModelName = usedModelName,
                ModelName = usedModelName,
                InferenceMs = ClampLongToInt(context.InferenceMs),
                TargetLabel = traceTargetLabel,
                ExpectedCount = traceExpectedCount,
                ActualCount = actualCount,
                CameraId = _activeCameraIdProvider() ?? string.Empty,
                RuleSummary = result?.JudgeResult?.Summary ?? string.Empty,
                RuleResultJson = SerializeRuleResults(result?.JudgeResult),
                RuleSetJson = ruleSetJson,
                ResultJson = resultJsonOverride ?? SerializeDetectionResults(results, result?.UsedModelLabels)
            };
        }

        private ModelRegistryEntry? ResolveRuntimeModelRegistryEntry(string? usedModelName, bool wasFallback)
        {
            foreach (DetectionModelSlotSnapshot slot in EnumerateRuntimeSlots(wasFallback))
            {
                if (!slot.IsLoaded || string.IsNullOrWhiteSpace(slot.ModelPath))
                {
                    continue;
                }

                if (!ModelNameMatchesSlot(usedModelName, slot.ModelPath))
                {
                    continue;
                }

                ModelRegistryEntry? entry = _modelRegistry.Resolve(slot.ModelPath);
                if (entry != null)
                {
                    return entry;
                }
            }

            return _modelRegistry.Resolve(usedModelName);
        }

        private IEnumerable<DetectionModelSlotSnapshot> EnumerateRuntimeSlots(bool wasFallback)
        {
            DetectionRuntimeModelSnapshot snapshot = _detectionService.RuntimeModelSnapshot;
            if (wasFallback)
            {
                yield return snapshot.Auxiliary1;
                yield return snapshot.Auxiliary2;
                yield return snapshot.Primary;
            }
            else
            {
                yield return snapshot.Primary;
                yield return snapshot.Auxiliary1;
                yield return snapshot.Auxiliary2;
            }
        }

        private static bool ModelNameMatchesSlot(string? usedModelName, string modelPath)
        {
            if (string.IsNullOrWhiteSpace(usedModelName) || string.IsNullOrWhiteSpace(modelPath))
            {
                return false;
            }

            string used = usedModelName.Trim();
            if (IsPathLike(used))
            {
                return string.Equals(
                    GetFullPathSafe(used),
                    GetFullPathSafe(modelPath),
                    StringComparison.OrdinalIgnoreCase);
            }

            string usedFileName = Path.GetFileName(used);
            string slotFileName = Path.GetFileName(modelPath);
            return string.Equals(usedFileName, slotFileName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       Path.GetFileNameWithoutExtension(usedFileName),
                       Path.GetFileNameWithoutExtension(slotFileName),
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathLike(string value)
        {
            return Path.IsPathRooted(value) ||
                   value.Contains(Path.DirectorySeparatorChar) ||
                   value.Contains(Path.AltDirectorySeparatorChar);
        }

        private static string GetFullPathSafe(string value)
        {
            try
            {
                return Path.GetFullPath(value);
            }
            catch
            {
                return value;
            }
        }

        private void ApplyTerminalHandshakeResult(
            InspectionPipelineResult pipelineResult,
            PlcHandshakeV1Result result)
        {
            InspectionContext context = pipelineResult.Context;
            context.TerminalHandshakeAttempted = true;
            context.TerminalHandshakeSucceeded = result.Succeeded;
            context.TerminalHandshakeErrorCode = result.ErrorCode ?? string.Empty;
            context.TerminalHandshakeSignalName = result.SignalName ?? string.Empty;
            context.TerminalHandshakeAddress = result.Address ?? string.Empty;
            context.TerminalHandshakeMessage = result.Message ?? string.Empty;
            context.CycleSucceeded = pipelineResult.ProductFlowSucceeded && result.Succeeded;

            if (!result.Succeeded)
            {
                string productJudgement = pipelineResult.FinalQualified ? "OK" : "NG";
                pipelineResult.StatusLevel = "error";
                pipelineResult.StatusMessage =
                    $"产品判定为 {productJudgement}，但 PLC 终态失败: [{result.ErrorCode}] {result.Message}";
            }
        }

        private async Task<PlcHandshakeV1Result> WriteHandshakeDetectionCompletedAsync(InspectionContext context, bool isQualified)
        {
            if (!ShouldWriteTerminalHandshake(context))
            {
                return new PlcHandshakeV1Result
                {
                    Succeeded = true,
                    Message = "HandshakeV1 skipped."
                };
            }

            var coordinator = new PlcHandshakeV1Coordinator(_plcService, DiagLog);
            PlcHandshakeV1Result result = await coordinator.CompleteInspectionAsync(
                PlcHandshakeV1Addresses.FromConfig(_appConfig),
                context,
                isQualified).ConfigureAwait(false);
            context.HandshakeCompleteMs = result.ElapsedMs;
            if (!result.Succeeded)
            {
                RecordHealthError("PLC.HandshakeV1", result.Message, context.InspectionId);
            }

            DiagLog($"HandshakeV1完成[{context.InspectionId}]: Result={(isQualified ? "OK" : "NG")}, ResultSeq={context.ResultSeq?.ToString() ?? "-"}, Ack={result.ResultAckReceived}");
            return result;
        }

        private bool ShouldWriteTerminalHandshake(InspectionContext context)
        {
            return context.PlcTriggerAccepted &&
                   ShouldUsePlcIo() &&
                   _appConfig.PlcProtocolMode == PlcProtocolMode.HandshakeV1;
        }

        private async Task<bool> WriteDetectionResultToPlcAsync(
            bool isQualified,
            InspectionContext context,
            Func<InspectionPipelineProgress, Task>? progressAsync)
        {
            if (!ShouldUsePlcIo())
            {
                DiagLog($"[{context.TriggerSource}] [{context.InspectionId}] 非 PLC 触发模式，跳过 PLC 结果写入");
                return true;
            }

            if (!_plcService.IsConnected)
            {
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.PlcWrite, "PlcNotConnected", "PLC未连接，检测结果未写入");
                }

                RecordHealthError("PLC", "PLC未连接，检测结果未写入", context.InspectionId);
                await PublishLogAsync(
                    progressAsync,
                    context,
                    $"PLC未连接，检测结果未写入({context.InspectionId})",
                    "error").ConfigureAwait(false);
                return false;
            }

            if (_appConfig.PlcProtocolMode == PlcProtocolMode.HandshakeV1)
            {
                DiagLog($"[{context.TriggerSource}] [{context.InspectionId}] HandshakeV1结果写入延迟到终态握手");
                return true;
            }

            try
            {
                short writeValue = isQualified ? _appConfig.PlcOkValue : _appConfig.PlcNgValue;
                bool success = await _plcService.WriteResultAsync(_appConfig.PlcResultAddress, writeValue).ConfigureAwait(false);
                DiagLog($"PLC结果写入[{context.InspectionId}]: 地址={_appConfig.PlcResultAddress}, 值={writeValue}, 判定={(isQualified ? "OK" : "NG")}, 结果={(success ? "成功" : "失败")}");
                if (!success)
                {
                    if (string.IsNullOrWhiteSpace(context.ErrorCode))
                    {
                        context.SetError(InspectionStage.PlcWrite, "PlcWriteFailed", "PLC写入失败: 结果未成功落地");
                    }

                    string message = "PLC写入失败: 结果未成功落地";
                    RecordHealthError("PLC", message, context.InspectionId);
                    await PublishLogAsync(
                        progressAsync,
                        context,
                        $"PLC写入失败({context.InspectionId}): 结果未成功落地",
                        "error").ConfigureAwait(false);
                }

                return success;
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.PlcWrite, "PlcWriteException", ex.Message);
                }

                RecordHealthError("PLC", $"PLC写入异常: {ex.Message}", context.InspectionId);
                await PublishLogAsync(
                    progressAsync,
                    context,
                    $"PLC写入失败({context.InspectionId}): {ex.Message}",
                    "error").ConfigureAwait(false);
                return false;
            }
        }

        private async Task ExecutePlcResultWriteStageAsync(
            InspectionPipelineResult pipelineResult,
            bool isQualified,
            string successMessage,
            string failureMessage,
            Func<InspectionPipelineProgress, Task>? progressAsync)
        {
            if (!ShouldUsePlcIo())
            {
                return;
            }

            InspectionContext context = pipelineResult.Context;
            context.CurrentStage = InspectionStage.PlcWrite;
            var plcSw = Stopwatch.StartNew();
            bool plcWritten = await WriteDetectionResultToPlcAsync(isQualified, context, progressAsync).ConfigureAwait(false);
            plcSw.Stop();
            pipelineResult.Timings.PlcWriteMs = plcSw.ElapsedMilliseconds;
            pipelineResult.Timings.PlcResultWriteMs = pipelineResult.Timings.PlcWriteMs;
            context.PlcWriteMs = pipelineResult.Timings.PlcWriteMs;
            context.PlcResultWriteMs = pipelineResult.Timings.PlcResultWriteMs;
            pipelineResult.AddStage(
                InspectionStage.PlcWrite,
                plcWritten,
                plcSw.ElapsedMilliseconds,
                plcWritten ? successMessage : failureMessage,
                plcWritten ? null : context.ErrorCode);
        }

        private bool ShouldUsePlcIo()
        {
            return _appConfig.TriggerSource == TriggerSource.PLC;
        }

        private void RecordHealthError(string source, string message, string? inspectionId = null)
        {
            try
            {
                _healthMonitor.RecordError(source, message, inspectionId);
                HealthSnapshot snapshot = _healthMonitor.GetSnapshot();
                _storageService.WriteErrorLog(
                    $"HealthMonitor[{source}] InspectionId={inspectionId ?? "-"} Message={message} Snapshot={SerializeHealthSnapshot(snapshot)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InspectionPipeline.HealthMonitor] 记录错误失败: {ex.Message}");
            }
        }

        private async Task PublishLogAsync(
            Func<InspectionPipelineProgress, Task>? progressAsync,
            InspectionContext context,
            string message,
            string level)
        {
            if (progressAsync == null)
            {
                return;
            }

            await progressAsync(new InspectionPipelineProgress
            {
                Context = context,
                Kind = InspectionPipelineProgressKind.Log,
                Message = message,
                Level = level
            }).ConfigureAwait(false);
        }

        private async Task PublishUpdateAsync(
            Func<InspectionPipelineProgress, Task>? progressAsync,
            InspectionContext context,
            bool? isOk = null,
            string? message = null,
            string? usedModelName = null,
            bool wasFallback = false,
            bool barcodeEnabled = false,
            string? productBarcode = null,
            bool? barcodeReadSucceeded = null,
            string? barcodeError = null)
        {
            if (progressAsync == null)
            {
                return;
            }

            await progressAsync(new InspectionPipelineProgress
            {
                Context = context,
                Kind = InspectionPipelineProgressKind.InspectionUpdate,
                IsOk = isOk,
                Message = message ?? string.Empty,
                UsedModelName = usedModelName,
                WasFallback = wasFallback,
                BarcodeEnabled = barcodeEnabled,
                ProductBarcode = productBarcode,
                BarcodeReadSucceeded = barcodeReadSucceeded,
                BarcodeError = barcodeError
            }).ConfigureAwait(false);
        }

        private static TraceStatus ResolveTraceStatus(bool imageQueued, bool recordQueued)
        {
            return (imageQueued, recordQueued) switch
            {
                (true, true) => TraceStatus.Queued,
                (true, false) => TraceStatus.Partial,
                (false, true) => TraceStatus.Partial,
                _ => TraceStatus.Failed
            };
        }

        private static string BuildQueueStatus(InspectionContext context, bool imageQueued, bool recordQueued)
        {
            return JsonSerializer.Serialize(new
            {
                TraceStatus = ResolveTraceStatus(imageQueued, recordQueued).ToString(),
                ImageQueued = imageQueued,
                RecordQueued = recordQueued,
                ImageQueuePending = context.ImageQueuePending,
                RecordQueuePending = context.RecordQueuePending
            });
        }

        private static string SerializeHealthSnapshot(HealthSnapshot snapshot)
        {
            return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        private static string GetDetailedDetectionLog(
            List<YoloResult> results,
            string[]? labels,
            InspectionJudgeResult? judgeResult = null)
        {
            return DeepLearningResultSummarizer.CreateTaskAwareLogSummary(
                results,
                labels,
                judgeResult?.IsQualified,
                GetRulePrimaryReason(judgeResult));
        }

        private static string BuildRuleStatus(InspectionJudgeResult? judgeResult)
        {
            if (judgeResult == null)
            {
                return string.Empty;
            }

            string summary = judgeResult.IsQualified
                ? (string.IsNullOrWhiteSpace(judgeResult.Summary) ? "-" : judgeResult.Summary)
                : GetRulePrimaryReason(judgeResult);
            return $" | 规则: {(judgeResult.IsQualified ? "OK" : "NG")} [{summary}]";
        }

        private static string GetRulePrimaryReason(InspectionJudgeResult? judgeResult)
        {
            if (judgeResult == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(judgeResult.PrimaryReason))
            {
                return judgeResult.PrimaryReason;
            }

            string? failedReason = judgeResult.RuleResults
                .FirstOrDefault(result => !result.IsMatch)?.Message;
            if (!string.IsNullOrWhiteSpace(failedReason))
            {
                return failedReason;
            }

            return string.IsNullOrWhiteSpace(judgeResult.Summary) ? "-" : judgeResult.Summary;
        }

        private static string BuildFallbackStatus(DetectionResultData result)
        {
            if (result == null)
            {
                return string.Empty;
            }

            string attemptText = result.FallbackAttemptCount > 1
                ? $", 尝试{result.FallbackAttemptCount}个模型"
                : string.Empty;

            if (result.WasFallback)
            {
                return $" [切换至: {result.UsedModelName}{attemptText}]";
            }

            if (!string.IsNullOrWhiteSpace(result.FallbackSkippedReason))
            {
                return $" [回退未命中: {result.FallbackSkippedReason}{attemptText}]";
            }

            return attemptText.Length > 0
                ? $" [模型{attemptText.TrimStart(',', ' ')}]"
                : string.Empty;
        }

        private static void ApplyRuleTraceSnapshot(
            DetectionResultData result,
            string ruleSetJson,
            InspectionFallbackGoal? fallbackGoal)
        {
            result.RuleSetJson = ruleSetJson ?? string.Empty;
            result.TargetLabel = fallbackGoal?.TargetLabel ?? string.Empty;
            result.ExpectedCount = fallbackGoal?.TargetCount ?? 0;
        }

        private static int ClampLongToInt(long value)
        {
            if (value > int.MaxValue)
            {
                return int.MaxValue;
            }

            if (value < int.MinValue)
            {
                return int.MinValue;
            }

            return (int)value;
        }

        private static string SerializeDetectionResults(IEnumerable<YoloResult> results, IReadOnlyList<string>? labels)
        {
            List<YoloResult> resultList = results?.ToList() ?? new List<YoloResult>();
            if (resultList.Count == 0)
            {
                return string.Empty;
            }

            return JsonSerializer.Serialize(new
            {
                Results = resultList.Select(r => new
                {
                    DataKind = r.DataKind.ToString(),
                    r.ClassId,
                    Label = DeepLearningResultSummarizer.ResolveLabel(r.ClassId, labels),
                    r.Confidence,
                    r.Angle,
                    HasMask = r.MaskData != null && !r.MaskData.Empty(),
                    KeyPointCount = r.KeyPoints?.Length ?? 0,
                    BoundingBox = new
                    {
                        X = r.BoundingBox.X,
                        Y = r.BoundingBox.Y,
                        Width = r.BoundingBox.Width,
                        Height = r.BoundingBox.Height
                    }
                }),
                DeepLearningSummary = DeepLearningResultSummarizer.CreateTraceSummary(resultList, labels)
            });
        }

        private static string SerializeRuleResults(InspectionJudgeResult? judgeResult)
        {
            if (judgeResult == null || judgeResult.RuleResults.Count == 0)
            {
                return string.Empty;
            }

            return JsonSerializer.Serialize(judgeResult.RuleResults.Select(r => new
            {
                r.RuleId,
                r.RuleName,
                r.RuleType,
                r.IsMatch,
                r.Expected,
                r.Actual,
                r.Message
            }));
        }

        private static string AddFileNameSuffix(string fileName, string suffix)
        {
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            return $"{nameWithoutExtension}{suffix}{extension}";
        }

        private static string BuildTraceImageFileName(bool isQualified, string inspectionId, string? productBarcode)
        {
            string resultPrefix = isQualified ? "PASS" : "FAIL";
            string safeInspectionId = SanitizeTraceFileNamePart(inspectionId, maxLength: 96);
            string safeBarcode = SanitizeTraceFileNamePart(productBarcode, maxLength: 80);

            if (string.IsNullOrWhiteSpace(safeBarcode))
            {
                return $"{resultPrefix}_{safeInspectionId}.jpg";
            }

            return $"{resultPrefix}_SN-{safeBarcode}_{safeInspectionId}.jpg";
        }

        private static string SanitizeTraceFileNamePart(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
            var builder = new StringBuilder(value.Length);
            foreach (char ch in value.Trim())
            {
                if (invalidChars.Contains(ch) || char.IsControl(ch))
                {
                    builder.Append('_');
                    continue;
                }

                builder.Append(ch);
            }

            string safe = builder.ToString().Trim(' ', '.', '_');
            if (safe.Length > maxLength)
            {
                safe = safe.Substring(0, maxLength).Trim(' ', '.', '_');
            }

            return safe;
        }

        private static bool IsManualTriggerSource(string triggerSource)
        {
            return string.IsNullOrWhiteSpace(triggerSource)
                || triggerSource.Contains("手动", StringComparison.OrdinalIgnoreCase)
                || triggerSource.Contains("MANUAL", StringComparison.OrdinalIgnoreCase);
        }

        private static short MapHandshakeErrorCode(InspectionContext context)
        {
            if (string.IsNullOrWhiteSpace(context.ErrorCode))
            {
                return 0;
            }

            string stage = context.ErrorStage ?? string.Empty;
            if (stage.Contains(nameof(InspectionStage.Capture), StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (stage.Contains(nameof(InspectionStage.Barcode), StringComparison.OrdinalIgnoreCase))
            {
                return 150;
            }

            if (stage.Contains(nameof(InspectionStage.Inference), StringComparison.OrdinalIgnoreCase))
            {
                return 200;
            }

            if (stage.Contains(nameof(InspectionStage.RoiFilter), StringComparison.OrdinalIgnoreCase))
            {
                return 250;
            }

            if (stage.Contains(nameof(InspectionStage.PlcWrite), StringComparison.OrdinalIgnoreCase))
            {
                return 300;
            }

            if (stage.Contains(nameof(InspectionStage.SaveImage), StringComparison.OrdinalIgnoreCase))
            {
                return 400;
            }

            if (stage.Contains(nameof(InspectionStage.SaveRecord), StringComparison.OrdinalIgnoreCase))
            {
                return 500;
            }

            return 900;
        }

        private static short ClampIntToShort(int value)
        {
            if (value > short.MaxValue)
            {
                return short.MaxValue;
            }

            if (value < short.MinValue)
            {
                return short.MinValue;
            }

            return (short)value;
        }

        private string BaseStoragePath => _storageService.BaseStoragePath;

        private string Path_Images => Path.Combine(BaseStoragePath, "Images");

        private void DiagLog(string message)
        {
            _diagLog?.Invoke(message);
        }

        private readonly record struct BarcodeReadResult(
            string? ProductBarcode,
            bool? ReadSucceeded,
            string? ErrorCode,
            string? Message);
    }
}
