using MVSDK_Net;
using ClearFrost.Config;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.IO;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClearFrost.Core.Inspection;
using ClearFrost.Core.Models;
using ClearFrost.Hardware;
using ClearFrost.Yolo;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    public partial class 主窗口
    {
        #region 5. YOLO检测逻辑 (检测与视觉逻辑)

        private void InitYolo()
        {
            // 同步调用异步方法
            SafeFireAndForget(InitYoloAsync(), "YOLO初始化");
        }

        private async Task InitYoloAsync()
        {
            await _uiController.LogToFrontend("正在加载 YOLO 模型...", "info");

            bool useGpu = _appConfig.EnableGpu;

            if (!Directory.Exists(模型路径))
            {
                await _uiController.LogToFrontend($"模型目录不存在: {模型路径}", "warning");
                return;
            }

            // 优先恢复上次使用的主模型；为空或文件不存在时再尝试目录中的第一个模型
            if (string.IsNullOrWhiteSpace(模型名))
            {
                模型名 = _appConfig.CurrentModelFileName?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(模型名) && !File.Exists(Path.Combine(模型路径, 模型名)))
                {
                    模型名 = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(模型名))
                {
                    var files = Directory.GetFiles(模型路径, "*.onnx");
                    if (files.Length > 0) 模型名 = Path.GetFileName(files[0]);
                }
            }

            if (!string.IsNullOrEmpty(模型名))
            {
                try
                {
                    string modelPath = Path.Combine(模型路径, 模型名);
                    bool success = await _detectionService.LoadModelAsync(modelPath, useGpu);
                    if (success)
                    {
                        模型名 = Path.GetFileName(modelPath);
                        if (!string.Equals(_appConfig.CurrentModelFileName, 模型名, StringComparison.OrdinalIgnoreCase))
                        {
                            _appConfig.CurrentModelFileName = 模型名;
                            _appConfig.Save();
                        }

                        await _uiController.LogToFrontend($"模型加载成功: {模型名}", "success");
                        await RestoreMultiModelConfigAsync();
                    }
                    else
                    {
                        await _uiController.LogToFrontend("模型加载失败", "error");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"模型加载失败: {ex.Message}", "error");
                }
            }
            else
            {
                await _uiController.LogToFrontend("未找到模型文件，请在设置中下载或上传模型", "warning");
            }
        }

        private async Task RestoreMultiModelConfigAsync()
        {
            _detectionService.SetEnableFallback(_appConfig.EnableMultiModelFallback);

            if (!string.IsNullOrWhiteSpace(_appConfig.Auxiliary1ModelPath))
            {
                string aux1Path = Path.Combine(模型路径, _appConfig.Auxiliary1ModelPath);
                if (File.Exists(aux1Path))
                {
                    bool ok = await _detectionService.LoadAuxiliary1ModelAsync(aux1Path);
                    if (ok)
                    {
                        await _uiController.LogToFrontend($"已恢复辅助模型1: {_appConfig.Auxiliary1ModelPath}");
                    }
                }
                else
                {
                    await _uiController.LogToFrontend($"辅助模型1文件不存在，跳过恢复: {_appConfig.Auxiliary1ModelPath}", "warning");
                }
            }

            if (!string.IsNullOrWhiteSpace(_appConfig.Auxiliary2ModelPath))
            {
                string aux2Path = Path.Combine(模型路径, _appConfig.Auxiliary2ModelPath);
                if (File.Exists(aux2Path))
                {
                    bool ok = await _detectionService.LoadAuxiliary2ModelAsync(aux2Path);
                    if (ok)
                    {
                        await _uiController.LogToFrontend($"已恢复辅助模型2: {_appConfig.Auxiliary2ModelPath}");
                    }
                }
                else
                {
                    await _uiController.LogToFrontend($"辅助模型2文件不存在，跳过恢复: {_appConfig.Auxiliary2ModelPath}", "warning");
                }
            }
        }

        private async Task TestYolo_HandlerAsync()
        {
            try
            {
                await _uiController.LogToFrontend("开始YOLO测试...", "info");

                string? selectedFile = await ShowOpenFileDialogOnStaThread("选择测试图片", "图像文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*");

                if (string.IsNullOrEmpty(selectedFile))
                {
                    await _uiController.LogToFrontend("已取消测试", "warning");
                    return;
                }

                await _uiController.LogToFrontend($"测试图片: {Path.GetFileName(selectedFile)}", "info");

                // 读取图片
                using (Bitmap originalBitmap = new Bitmap(selectedFile))
                {
                    // 检查模型是否初始化
                    if (!_detectionService.IsModelLoaded)
                    {
                        await _uiController.LogToFrontend("YOLO模型未初始化", "error");
                        return;
                    }

                    var sw = Stopwatch.StartNew();

                    // 执行检测
                    var result = await _detectionService.DetectAsync(originalBitmap, _appConfig.Confidence, _appConfig.IouThreshold, _appConfig.TargetLabel, _appConfig.TargetCount);

                    sw.Stop();

                    // 获取检测结果
                    var results = result.Results ?? new List<YoloResult>();
                    bool isQualified = result.IsQualified;
                    bool detectionFailed = result.HasError;

                    // 应用 ROI 过滤
                    results = FilterResultsByROI(results, originalBitmap.Width, originalBitmap.Height);

                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    if (detectionFailed)
                    {
                        isQualified = false;
                        await _uiController.LogToFrontend($"检测失败，已强制判定为不合格: {result.ErrorMessage}", "error");
                    }
                    else
                    {
                        isQualified = EvaluateQualificationByTarget(results, labels, _appConfig.TargetLabel, _appConfig.TargetCount);
                    }
                    using (var sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(originalBitmap))
                    using (var renderedMat = TryRenderDetectionMat(sourceMat, results, labels))
                    {
                        // 保存检测图像到追溯库（不合格时复用渲染结果）
                        await SaveDetectionImage(sourceMat, results, isQualified, result.UsedModelLabels, renderedMat);

                        _statisticsService.RecordDetection(isQualified);

                        string objDesc = GetDetailedDetectionLog(results, labels);
                        string modelInfo = result.WasFallback ? $" [切换至: {result.UsedModelName}]" : "";
                        string statusMessage = detectionFailed
                            ? $"检测失败，已判定为不合格: {result.ErrorMessage} | {sw.ElapsedMilliseconds}ms"
                            : $"检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | {sw.ElapsedMilliseconds}ms{modelInfo}";
                        await _uiController.SendDetectionFrame(
                            renderedMat ?? sourceMat,
                            isQualified,
                            _statisticsService.Current,
                            statusMessage,
                            isQualified && !detectionFailed ? "success" : "error",
                            (_detectionService as DetectionService)?.GetLastMetrics());
                    }
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"测试失败: {ex.Message}", "error");
            }
        }

        private async Task<string?> ShowOpenFileDialogOnStaThread(string title, string filter)
        {
            string? result = null;
            await Task.Run(() =>
            {
                Thread thread = new Thread(() =>
                {
                    using var ofd = new OpenFileDialog();
                    ofd.Title = title;
                    ofd.Filter = filter;
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        result = ofd.FileName;
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            });
            return result;
        }

        private void ChangeModel_Logic(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return;

            模型名 = modelName;
            SafeFireAndForget(ChangeModelAsync(modelName), "切换模型");
        }

        private async Task ChangeModelAsync(string modelName)
        {
            try
            {
                await _uiController.LogToFrontend($"正在切换模型: {modelName}", "info");

                string modelFileName = modelName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                    ? modelName
                    : $"{modelName}.onnx";
                string modelPath = Path.Combine(模型路径, modelFileName);
                if (!File.Exists(modelPath))
                {
                    await _uiController.LogToFrontend($"模型文件不存在: {modelFileName}", "error");
                    return;
                }

                bool success = await _detectionService.LoadModelAsync(modelPath, _appConfig.EnableGpu);
                if (success)
                {
                    模型名 = modelFileName;
                    _appConfig.CurrentModelFileName = modelFileName;
                    _appConfig.Save();
                    await _uiController.LogToFrontend($"模型切换成功: {modelFileName}", "success");
                }
                else
                {
                    await _uiController.LogToFrontend("模型切换失败", "error");
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"模型切换异常: {ex.Message}", "error");
            }
        }

        /// <summary>
        /// 手动检测逻辑 (PLC触发或手动按钮)
        /// </summary>
        private string GetDetailedDetectionLog(List<YoloResult> results, string[]? labels)
        {
            if (results == null || results.Count == 0) return "未检测到目标";

            // 格式: screw 0.98, body 0.99
            var details = results.Select(r =>
            {
                string label = (labels != null && r.ClassId >= 0 && r.ClassId < labels.Length)
                    ? labels[r.ClassId]
                    : $"Class_{r.ClassId}";
                return $"{label} {r.Confidence:F2}";
            });

            return $"Found {results.Count}: {string.Join(", ", details)}";
        }

        private readonly record struct DetectionCycleRequest(
            string TriggerSource,
            string InspectionId,
            int? TriggerSeq,
            InspectionContext Context);

        private async Task btnCapture_LogicAsync(
            string triggerSource = "手动",
            int? triggerSeq = null,
            string? productBarcode = null,
            bool barcodeReadSucceeded = true,
            string? barcodeError = null)
        {
            DateTimeOffset triggerTime = DateTimeOffset.Now;
            string inspectionId = InspectionIdGenerator.Next(triggerSource, triggerTime);
            var context = new InspectionContext
            {
                InspectionId = inspectionId,
                TriggerTime = triggerTime,
                TriggerSource = triggerSource,
                TriggerSeq = triggerSeq,
                ProductBarcode = productBarcode?.Trim() ?? string.Empty,
                BarcodeReadSucceeded = barcodeReadSucceeded,
                BarcodeError = barcodeError ?? string.Empty,
                CurrentStage = InspectionStage.Triggered,
                TraceStatus = TraceStatus.Unknown
            };

            DiagLog($"▶ [{triggerSource}] [{inspectionId}] btnCapture_LogicAsync 进入, 线程ID={Thread.CurrentThread.ManagedThreadId}");

            if (!await EnsureStartupReadyForProductionAsync("检测", inspectionId))
            {
                return;
            }

            DetectionTriggerDecision decision = await TryStartDetectionCycleAsync(triggerSource, inspectionId);
            if (!decision.Accepted)
            {
                return;
            }

            DetectionCycleRequest request = new DetectionCycleRequest(triggerSource, inspectionId, triggerSeq, context);
            var totalSw = Stopwatch.StartNew();
            long captureMs = 0;
            long inferenceMs = 0;
            long roiFilterMs = 0;
            long plcWriteMs = 0;
            long renderToUiMs = 0;
            long saveQueueMs = 0;
            long dbWriteMs = 0;
            bool finalQualified = false;
            int finalResultCount = 0;
            int finalAttemptCount = 1;

            try
            {
                (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount, finalAttemptCount) =
                    await ExecuteDetectionCycleAsync(request, _appShutdownCts.Token);
            }
            catch (Exception ex)
            {
                context.MarkFailed(InspectionStage.Unknown, "UnhandledDetectionException", ex.Message);
                DiagLog($"❌ [{request.TriggerSource}] [{request.InspectionId}] 检测异常: {ex.Message}");
                await _uiController.LogToFrontend($"检测异常({request.InspectionId}): {ex.Message}", "error");
            }
            finally
            {
                totalSw.Stop();
                context.TotalMs = totalSw.ElapsedMilliseconds;
                await WriteHandshakeDetectionCompletedAsync(context, finalQualified);
                _healthMonitor.RecordInspection(context);
                if (captureMs > 0 || inferenceMs > 0 || roiFilterMs > 0 || plcWriteMs > 0 || renderToUiMs > 0 || saveQueueMs > 0 || dbWriteMs > 0)
                {
                    WritePerformanceProfileLog(
                        context,
                        finalQualified,
                        totalSw.ElapsedMilliseconds,
                        captureMs,
                        inferenceMs,
                        roiFilterMs,
                        renderToUiMs,
                        saveQueueMs,
                        plcWriteMs,
                        dbWriteMs,
                        finalAttemptCount,
                        finalResultCount,
                        _detectionGate.GetSnapshot());
                }

                _detectionGate.Release();
                DiagLog($"✅ [{triggerSource}] [{inspectionId}] btnCapture_LogicAsync 完成, 信号量已释放");
            }
        }

        private async Task<DetectionTriggerDecision> TryStartDetectionCycleAsync(string triggerSource, string inspectionId)
        {
            DetectionTriggerDecision decision = await _detectionGate.TryEnterAsync(IsShutdownInProgress);
            if (decision.Accepted)
            {
                return decision;
            }

            DetectionDropReason reason = decision.DropReason ?? DetectionDropReason.Busy;
            DetectionDropSnapshot snapshot = _detectionGate.GetSnapshot();
            string summary = $"busy={snapshot.BusyCount}, debounce={snapshot.DebounceCount}, shutdown={snapshot.ShutdownCount}";

            switch (reason)
            {
                case DetectionDropReason.Shutdown:
                    DiagLog($"⚠ [{triggerSource}] [{inspectionId}] 软件正在退出，已忽略检测请求 | {summary}");
                    await _uiController.LogToFrontend($"软件正在退出，已忽略检测请求({inspectionId})", "warning");
                    break;
                case DetectionDropReason.Debounce:
                    DiagLog($"⚠ [{triggerSource}] [{inspectionId}] 触发命中防抖窗口，已忽略 | {summary}");
                    await _uiController.LogToFrontend($"检测触发过于频繁，已忽略本次请求({inspectionId})", "warning");
                    break;
                default:
                    DiagLog($"⚠ [{triggerSource}] [{inspectionId}] 信号量已被占用，跳过 | {summary}");
                    await _uiController.LogToFrontend($"检测正在进行中，请稍候...({inspectionId})", "warning");
                    break;
            }

            return decision;
        }

        private bool ShouldBlockDetectionForBarcode(
            InspectionContext context,
            out string errorCode,
            out string errorMessage)
        {
            return PlcBarcodeDetectionGate.ShouldBlockDetection(
                _appConfig,
                context,
                out errorCode,
                out errorMessage);
        }

        private static string FormatBarcodeSuffix(InspectionContext context)
        {
            return string.IsNullOrWhiteSpace(context.ProductBarcode)
                ? string.Empty
                : $"，条码: {context.ProductBarcode}";
        }

        private async Task<(long CaptureMs, long InferenceMs, long RoiFilterMs, long PlcWriteMs, long RenderToUiMs, long SaveQueueMs, long DbWriteMs, bool FinalQualified, int FinalResultCount, int AttemptCount)> ExecuteDetectionCycleAsync(
            DetectionCycleRequest request,
            CancellationToken cancellationToken)
        {
            InspectionContext context = request.Context;
            await _uiController.LogToFrontend(
                $"开始检测... ({request.TriggerSource}触发, ID: {request.InspectionId}){FormatBarcodeSuffix(context)}",
                "info");
            await WriteHandshakeDetectionStartedAsync(context);

            long captureMs = 0;
            long inferenceMs = 0;
            long roiFilterMs = 0;
            long plcWriteMs = 0;
            long renderToUiMs = 0;
            long saveQueueMs = 0;
            long dbWriteMs = 0;
            bool finalQualified = false;
            int finalResultCount = 0;
            int attemptCount = 0;
            ImageSavePayload? imagePayload = null;
            DetectionPersistencePayload? persistencePayload = null;

            if (ShouldBlockDetectionForBarcode(context, out string barcodeErrorCode, out string barcodeErrorMessage))
            {
                context.CurrentStage = InspectionStage.BarcodeRead;
                context.SetError(InspectionStage.BarcodeRead, barcodeErrorCode, barcodeErrorMessage);
                await _uiController.LogToFrontend($"{barcodeErrorMessage}，已判 NG (ID: {request.InspectionId})", "error");
                DiagLog($"❌ [{request.TriggerSource}] [{request.InspectionId}] {barcodeErrorMessage}");

                var plcSw = Stopwatch.StartNew();
                await WriteDetectionResultToPlc(false, context);
                plcSw.Stop();
                plcWriteMs = plcSw.ElapsedMilliseconds;
                context.PlcWriteMs = plcWriteMs;

                _statisticsService.RecordDetection(false);
                _storageService.WriteDetectionLog(
                    $"InspectionId: {request.InspectionId}{Environment.NewLine}{barcodeErrorMessage}",
                    false);

                context.TotalMs = plcWriteMs;
                var barcodeFailurePayload = BuildDetectionPersistencePayload(
                    context,
                    null,
                    new List<YoloResult>(),
                    0,
                    false,
                    JsonSerializer.Serialize(new
                    {
                        Error = barcodeErrorMessage,
                        Stage = nameof(InspectionStage.BarcodeRead),
                        context.InspectionId,
                        context.ProductBarcode,
                        context.BarcodeError
                    }));
                dbWriteMs = await EnqueueDetectionRecordAsync(context, barcodeFailurePayload, imageQueued: false);

                return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, false, finalResultCount, Math.Max(1, attemptCount));
            }

            Mat? frameToProcess = null;

            var captureSw = Stopwatch.StartNew();
            try
            {
                context.CurrentStage = InspectionStage.Capture;
                int maxRetryCount = Math.Clamp(_appConfig.MaxRetryCount, 0, 5);
                int totalAttempts = maxRetryCount + 1;
                int retryDelayMs = Math.Max(0, _appConfig.RetryIntervalMs);

                for (int attempt = 1; attempt <= totalAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attemptCount = attempt;
                    frameToProcess = _cameraService.CaptureFrame(3000);
                    DiagLog($"📷 [{request.TriggerSource}] [{request.InspectionId}] CaptureFrame 尝试 {attempt}/{totalAttempts}: {(frameToProcess != null ? "OK" : "FAIL")}");

                    if (frameToProcess != null)
                    {
                        break;
                    }

                    if (attempt < totalAttempts)
                    {
                        string retryDetail = string.IsNullOrWhiteSpace(_cameraService.LastError)
                            ? "取图失败"
                            : _cameraService.LastError;
                        await _uiController.LogToFrontend(
                            $"拍照失败，准备重试 {attempt}/{maxRetryCount}: {retryDetail}",
                            "warning");

                        if (retryDelayMs > 0)
                        {
                            await Task.Delay(retryDelayMs, cancellationToken);
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
                DiagLog($"❌ [{request.TriggerSource}] [{request.InspectionId}] CaptureFrame 异常: {ex.Message}");
                Debug.WriteLine($"[手动检测] 触发拍照失败: {ex.Message}");
            }

            captureSw.Stop();
            captureMs = captureSw.ElapsedMilliseconds;
            context.CaptureMs = captureMs;

            if (frameToProcess == null)
            {
                string detail = string.IsNullOrWhiteSpace(_cameraService.LastError)
                    ? "无可用图像进行检测，请先打开相机"
                    : $"相机拍照失败: {_cameraService.LastError}";
                context.MarkFailed(InspectionStage.Capture, "CaptureFrameFailed", detail);
                await _uiController.LogToFrontend($"{detail} (ID: {request.InspectionId})", "error");

                var plcSw = Stopwatch.StartNew();
                await WriteDetectionResultToPlc(false, context);
                plcSw.Stop();
                plcWriteMs = plcSw.ElapsedMilliseconds;
                context.PlcWriteMs = plcWriteMs;

                _statisticsService.RecordDetection(false);
                _storageService.WriteDetectionLog($"InspectionId: {request.InspectionId}{Environment.NewLine}{detail}", false);

                context.TotalMs = captureMs + plcWriteMs;
                var captureFailurePayload = BuildDetectionPersistencePayload(
                    context,
                    null,
                    new List<YoloResult>(),
                    0,
                    false,
                    JsonSerializer.Serialize(new { Error = detail, Stage = "Capture", context.InspectionId }));
                dbWriteMs = await EnqueueDetectionRecordAsync(context, captureFailurePayload, imageQueued: false);

                return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount, Math.Max(1, attemptCount));
            }

            using (frameToProcess)
            {
                try
                {
                    context.CurrentStage = InspectionStage.Inference;
                    var inferSw = Stopwatch.StartNew();
                    DetectionResultData result = await _detectionService.DetectAsync(
                        frameToProcess,
                        _appConfig.Confidence,
                        _appConfig.IouThreshold,
                        _appConfig.TargetLabel,
                        _appConfig.TargetCount);
                    inferSw.Stop();
                    inferenceMs = inferSw.ElapsedMilliseconds;
                    context.InferenceMs = inferenceMs;

                    bool isQualified = result.IsQualified;
                    List<YoloResult> results = result.Results ?? new List<YoloResult>();
                    bool detectionFailed = result.HasError;

                    context.CurrentStage = InspectionStage.RoiFilter;
                    var roiSw = Stopwatch.StartNew();
                    results = FilterResultsByROI(results, frameToProcess.Width, frameToProcess.Height);
                    roiSw.Stop();
                    roiFilterMs = roiSw.ElapsedMilliseconds;
                    context.RoiMs = roiFilterMs;
                    finalResultCount = results.Count;

                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    if (detectionFailed)
                    {
                        isQualified = false;
                        if (string.IsNullOrWhiteSpace(context.ErrorCode))
                        {
                            context.SetError(InspectionStage.Inference, "DetectionServiceError", result.ErrorMessage);
                        }

                        await _uiController.LogToFrontend($"检测失败({request.InspectionId})，已强制判定为不合格: {result.ErrorMessage}", "error");
                    }
                    else
                    {
                        isQualified = EvaluateQualificationByTarget(results, labels, _appConfig.TargetLabel, _appConfig.TargetCount);
                    }
                    finalQualified = isQualified;
                    context.ResultSeq = context.TriggerSeq;

                    context.CurrentStage = InspectionStage.PlcWrite;
                    var plcSw = Stopwatch.StartNew();
                    await WriteDetectionResultToPlc(isQualified, context);
                    plcSw.Stop();
                    plcWriteMs = plcSw.ElapsedMilliseconds;
                    context.PlcWriteMs = plcWriteMs;

                    using (Mat? renderedMat = TryRenderDetectionMat(frameToProcess, results, labels))
                    {
                        _statisticsService.RecordDetection(isQualified);

                        context.CurrentStage = InspectionStage.RenderToUi;
                        var renderSw = Stopwatch.StartNew();
                        string objDesc = GetDetailedDetectionLog(results, labels);
                        string modelInfo = result.WasFallback ? $" [切换至: {result.UsedModelName}]" : "";
                        string barcodeInfo = string.IsNullOrWhiteSpace(context.ProductBarcode)
                            ? string.Empty
                            : $" | 条码: {context.ProductBarcode}";
                        string statusMessage = detectionFailed
                            ? $"[{request.TriggerSource}] ID {request.InspectionId} 检测失败，已判定为不合格: {result.ErrorMessage} | {inferenceMs}ms{barcodeInfo}"
                            : $"[{request.TriggerSource}] ID {request.InspectionId} 检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | {inferenceMs}ms{modelInfo}{barcodeInfo}";
                        await _uiController.SendDetectionFrame(
                            renderedMat ?? frameToProcess,
                            isQualified,
                            _statisticsService.Current,
                            statusMessage,
                            isQualified && !detectionFailed ? "success" : "error",
                            (_detectionService as DetectionService)?.GetLastMetrics());
                        renderSw.Stop();
                        renderToUiMs = renderSw.ElapsedMilliseconds;
                        context.RenderToUiMs = renderToUiMs;

                        imagePayload = CreateImageSavePayload(
                            context,
                            frameToProcess,
                            results,
                            isQualified,
                            result.UsedModelLabels,
                            renderedMat);
                        context.TotalMs = captureMs + inferenceMs + roiFilterMs + plcWriteMs + renderToUiMs;
                        persistencePayload = BuildDetectionPersistencePayload(context, result, results, finalResultCount, isQualified);
                    }
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
                    DiagLog($"❌ [{request.TriggerSource}] [{request.InspectionId}] 检测流程异常: {ex.Message}");
                    await _uiController.LogToFrontend($"检测异常({request.InspectionId}): {ex.Message}", "error");

                    if (failedStage is InspectionStage.Inference or InspectionStage.RoiFilter)
                    {
                        var plcSw = Stopwatch.StartNew();
                        await WriteDetectionResultToPlc(false, context);
                        plcSw.Stop();
                        plcWriteMs = plcSw.ElapsedMilliseconds;
                        context.PlcWriteMs = plcWriteMs;
                    }

                    _statisticsService.RecordDetection(false);
                    _storageService.WriteDetectionLog(
                        $"InspectionId: {request.InspectionId}{Environment.NewLine}检测流程异常: {ex.Message}",
                        false);

                    imagePayload = CreateImageSavePayload(
                        context,
                        frameToProcess,
                        new List<YoloResult>(),
                        false);
                    (bool errorImageQueued, saveQueueMs) = await EnqueueImagePayloadAsync(context, imagePayload);
                    context.TotalMs = captureMs + inferenceMs + roiFilterMs + plcWriteMs + renderToUiMs + saveQueueMs;
                    var errorPayload = BuildDetectionPersistencePayload(
                        context,
                        null,
                        new List<YoloResult>(),
                        0,
                        false,
                        JsonSerializer.Serialize(new { Error = ex.Message, Stage = failedStage.ToString(), context.InspectionId }));
                    dbWriteMs = await EnqueueDetectionRecordAsync(context, errorPayload, errorImageQueued);

                    return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, false, 0, Math.Max(1, attemptCount));
                }
            }

            bool imageQueuedForRecord;
            (imageQueuedForRecord, saveQueueMs) = await EnqueueImagePayloadAsync(context, imagePayload);

            context.TotalMs = captureMs + inferenceMs + roiFilterMs + plcWriteMs + renderToUiMs + saveQueueMs;
            if (persistencePayload != null)
            {
                persistencePayload.TotalMs = context.TotalMs;
                persistencePayload.SaveImageMs = context.SaveImageMs;
                dbWriteMs = await EnqueueDetectionRecordAsync(context, persistencePayload, imageQueuedForRecord);
            }
            else
            {
                context.TraceStatus = ResolveTraceStatus(imageQueuedForRecord, recordQueued: false);
                await _uiController.LogToFrontend($"检测记录构造失败({request.InspectionId})", "error");
                DiagLog($"❌ [{request.TriggerSource}] [{request.InspectionId}] 检测记录构造失败");
            }

            context.CurrentStage = InspectionStage.Completed;

            return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount, Math.Max(1, attemptCount));
        }

        private Mat? TryRenderDetectionMat(Mat sourceImage, List<YoloResult> results, string[] labels)
        {
            if (results == null || results.Count == 0)
            {
                return null;
            }

            // 工业模式：纯 Mat 快速路径（避免 MatBitmap 双重转换）
            if (YoloDetector.IndustrialRenderMode)
            {
                Mat? matResult = (_detectionService as DetectionService)?.GenerateResultMat(sourceImage, results, labels);
                if (matResult != null)
                {
                    return matResult;
                }
            }

            // 回退：Bitmap 路径（美观模式或不支持的任务类型）
            using var bitmap = sourceImage.ToBitmap();
            using var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels);
            return OpenCvSharp.Extensions.BitmapConverter.ToMat(resultImage);
        }

        private ImageSavePayload? CreateImageSavePayload(Mat image, List<YoloResult> results, bool isQualified, string[]? usedLabels = null, Mat? renderedImage = null)
        {
            var context = new InspectionContext
            {
                InspectionId = InspectionIdGenerator.Next("TEST"),
                TriggerTime = DateTimeOffset.Now,
                TriggerSource = "TEST"
            };

            return CreateImageSavePayload(context, image, results, isQualified, usedLabels, renderedImage);
        }

        private ImageSavePayload? CreateImageSavePayload(InspectionContext context, Mat image, List<YoloResult> results, bool isQualified, string[]? usedLabels = null, Mat? renderedImage = null)
        {
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
                string fileName = $"{(isQualified ? "PASS" : "FAIL")}_{safeInspectionId}.jpg";
                string filePath = Path.Combine(directory, fileName);
                context.ImagePath = filePath;

                // 不合格图像优先复用调用方已渲染结果，避免二次 ToBitmap + 渲染。
                if (!isQualified && results.Count > 0)
                {
                    if (renderedImage != null && !renderedImage.Empty())
                    {
                        context.RenderedImagePath = filePath;
                        return ImageSavePayload.Create(renderedImage, filePath);
                    }

                    string[] labels = usedLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    using var bitmap = image.ToBitmap();
                    using var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels);
                    using var renderedMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(resultImage);
                    context.RenderedImagePath = filePath;
                    return ImageSavePayload.Create(renderedMat, filePath);
                }

                return ImageSavePayload.Create(image, filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存检测图像失败: {ex.Message}");
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.SaveImage, "ImagePayloadCreateFailed", ex.Message);
                }

                return null;
            }
        }

        private async Task<string> SaveDetectionImage(Mat image, List<YoloResult> results, bool isQualified, string[]? usedLabels = null, Mat? renderedImage = null)
        {
            ImageSavePayload? payload = CreateImageSavePayload(image, results, isQualified, usedLabels, renderedImage);
            if (payload == null)
            {
                return string.Empty;
            }

            bool enqueued = _imageSaveQueue.Enqueue(payload);
            if (!enqueued)
            {
                payload.Dispose();
                return string.Empty;
            }

            return payload.Path;
        }

        private async Task<(bool Queued, long ElapsedMs)> EnqueueImagePayloadAsync(InspectionContext context, ImageSavePayload? payload)
        {
            context.CurrentStage = InspectionStage.SaveImage;
            var saveSw = Stopwatch.StartNew();

            if (payload == null)
            {
                saveSw.Stop();
                context.SaveImageMs = saveSw.ElapsedMilliseconds;
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.SaveImage, "ImagePayloadMissing", "图像保存载荷为空");
                }

                RecordHealthError("ImageSaveQueue", "图像保存入队失败: 载荷为空", context.InspectionId);
                await _uiController.LogToFrontend($"图像保存入队失败({context.InspectionId}): 载荷为空", "error");
                DiagLog($"❌ [{context.TriggerSource}] [{context.InspectionId}] 图像保存入队失败: 载荷为空");
                return (false, context.SaveImageMs);
            }

            bool imageQueued = _imageSaveQueue.Enqueue(payload);
            saveSw.Stop();
            context.SaveImageMs = saveSw.ElapsedMilliseconds;

            if (!imageQueued)
            {
                payload.Dispose();
                if (string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.SaveImage, "ImageQueueFull", "图像保存队列入队失败");
                }

                Debug.WriteLine("[主窗口] 图像保存入队失败");
                DiagLog($"❌ [{context.TriggerSource}] [{context.InspectionId}] 图像保存入队失败");
                RecordHealthError("ImageSaveQueue", "图像保存队列入队失败", context.InspectionId);
                await _uiController.LogToFrontend($"图像保存入队失败({context.InspectionId})", "error");
            }

            return (imageQueued, context.SaveImageMs);
        }

        private async Task<long> EnqueueDetectionRecordAsync(InspectionContext context, DetectionPersistencePayload payload, bool imageQueued)
        {
            context.CurrentStage = InspectionStage.SaveRecord;
            payload.ImagePath = context.ImagePath ?? string.Empty;
            payload.RenderedImagePath = context.RenderedImagePath ?? string.Empty;
            payload.ErrorStage = context.ErrorStage ?? string.Empty;
            payload.ErrorCode = context.ErrorCode ?? string.Empty;
            payload.ErrorMessage = context.ErrorMessage ?? string.Empty;
            payload.TotalMs = context.TotalMs;
            payload.SaveImageMs = context.SaveImageMs;
            payload.TraceStatus = ResolveTraceStatus(imageQueued, recordQueued: true);

            var dbSw = Stopwatch.StartNew();
            bool dbQueued = _detectionRecordQueue.Enqueue(payload);
            dbSw.Stop();
            context.SaveRecordMs = dbSw.ElapsedMilliseconds;
            payload.SaveRecordMs = context.SaveRecordMs;
            context.TraceStatus = ResolveTraceStatus(imageQueued, dbQueued);

            if (!dbQueued)
            {
                Debug.WriteLine("[主窗口] 检测记录入队失败");
                DiagLog($"❌ [{context.TriggerSource}] [{context.InspectionId}] 检测记录入队失败");
                RecordHealthError("DetectionRecordQueue", "检测记录队列入队失败", context.InspectionId);
                await _uiController.LogToFrontend($"检测记录入队失败({context.InspectionId})", "error");
            }

            return context.SaveRecordMs;
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
                Debug.WriteLine($"[HealthMonitor] 记录错误失败: {ex.Message}");
            }
        }

        private void WriteHealthSnapshotLog(string reason)
        {
            try
            {
                HealthSnapshot snapshot = _healthMonitor.GetSnapshot();
                _storageService.WriteStartupLog($"HealthSnapshot({reason}): {SerializeHealthSnapshot(snapshot)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HealthMonitor] 写入快照失败: {ex.Message}");
            }
        }

        private static string SerializeHealthSnapshot(HealthSnapshot snapshot)
        {
            return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            });
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
            ModelRegistryEntry? modelEntry = _modelRegistry.Resolve(usedModelName);
            string fallbackModelId = string.IsNullOrWhiteSpace(usedModelName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(usedModelName);
            string recipeId = _recipeManager.CurrentRecipe?.RecipeId ?? "default";
            string recipeVersion = _recipeManager.CurrentRecipe?.Version ?? string.Empty;

            return new DetectionPersistencePayload
            {
                Timestamp = context.TriggerTime.LocalDateTime,
                IsQualified = isQualified,
                InspectionId = context.InspectionId,
                TriggerSource = context.TriggerSource,
                TriggerSeq = context.TriggerSeq,
                ProductBarcode = context.ProductBarcode ?? string.Empty,
                ResultSeq = context.ResultSeq,
                TraceStatus = context.TraceStatus,
                ImagePath = context.ImagePath ?? string.Empty,
                RenderedImagePath = context.RenderedImagePath ?? string.Empty,
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
                TargetLabel = _appConfig.TargetLabel ?? string.Empty,
                ExpectedCount = _appConfig.TargetCount,
                ActualCount = actualCount,
                CameraId = _cameraManager.ActiveCameraId ?? string.Empty,
                ResultJson = resultJsonOverride ?? SerializeDetectionResults(results)
            };
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

        private static string SerializeDetectionResults(IEnumerable<YoloResult> results)
        {
            List<YoloResult> resultList = results?.ToList() ?? new List<YoloResult>();
            if (resultList.Count == 0)
            {
                return string.Empty;
            }

            return JsonSerializer.Serialize(resultList.Select(r => new
            {
                r.ClassId,
                r.Confidence,
                BoundingBox = new
                {
                    X = r.BoundingBox.X,
                    Y = r.BoundingBox.Y,
                    Width = r.BoundingBox.Width,
                    Height = r.BoundingBox.Height
                }
            }));
        }

        private void WritePerformanceProfileLog(
            InspectionContext context,
            bool isQualified,
            long totalMs,
            long captureMs,
            long inferenceMs,
            long roiFilterMs,
            long renderToUiMs,
            long saveQueueMs,
            long plcWriteMs,
            long dbWriteMs,
            int attempts,
            int resultCount,
            DetectionDropSnapshot dropSnapshot)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                sb.AppendLine($"InspectionId: {context.InspectionId}");
                sb.AppendLine($"触发来源: {context.TriggerSource}");
                sb.AppendLine($"TriggerSeq: {(context.TriggerSeq.HasValue ? context.TriggerSeq.Value.ToString(CultureInfo.InvariantCulture) : "-")}");
                if (!string.IsNullOrWhiteSpace(context.ProductBarcode))
                {
                    sb.AppendLine($"ProductBarcode: {context.ProductBarcode}");
                }
                sb.AppendLine($"TraceStatus: {context.TraceStatus}");
                if (!string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    sb.AppendLine($"错误: {context.ErrorStage}/{context.ErrorCode} {context.ErrorMessage}");
                }
                sb.AppendLine($"总耗时: {totalMs}ms");
                sb.AppendLine($"尝试次数: {Math.Max(1, attempts)} (重试{Math.Max(0, attempts - 1)}次)");
                sb.AppendLine($"目标数量: {resultCount}");
                sb.AppendLine($"丢弃累计: busy={dropSnapshot.BusyCount}, debounce={dropSnapshot.DebounceCount}, shutdown={dropSnapshot.ShutdownCount}");
                sb.AppendLine("阶段耗时:");
                sb.AppendLine($"- 取图: {captureMs}ms");
                sb.AppendLine($"- 推理: {inferenceMs}ms");
                sb.AppendLine($"- ROI过滤: {roiFilterMs}ms");
                sb.AppendLine($"- 前端渲染: {renderToUiMs}ms");
                sb.AppendLine($"- 图像入队: {saveQueueMs}ms");
                sb.AppendLine($"- PLC写入: {plcWriteMs}ms");
                sb.AppendLine($"- 数据库写入: {dbWriteMs}ms");

                _storageService.WriteDetectionLog(sb.ToString(), isQualified);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[主窗口-性能日志] 写入失败: {ex.Message}");
            }
        }

        private async Task WriteHandshakeDetectionStartedAsync(InspectionContext context)
        {
            if (_appConfig.PlcProtocolMode != PlcProtocolMode.HandshakeV1)
            {
                return;
            }

            await WriteHandshakeWordAsync(_appConfig.PlcVisionOnlineAddress, 1, "VisionOnline", context);
            await WriteHandshakeWordAsync(_appConfig.PlcVisionReadyAddress, 1, "VisionReady", context);
            await WriteHandshakeWordAsync(_appConfig.PlcVisionBusyAddress, 1, "VisionBusy", context);
            await WriteHandshakeWordAsync(_appConfig.PlcInspectionDoneAddress, 0, "InspectionDone", context);
            await WriteHandshakeWordAsync(_appConfig.PlcTraceSavedAddress, 0, "TraceSaved", context);
            await WriteHandshakeWordAsync(_appConfig.PlcErrorCodeAddress, 0, "ErrorCode", context);
            await WriteHandshakeWordAsync(_appConfig.PlcHeartbeatAddress, 1, "Heartbeat", context);
        }

        private async Task WriteHandshakeDetectionCompletedAsync(InspectionContext context, bool isQualified)
        {
            if (_appConfig.PlcProtocolMode != PlcProtocolMode.HandshakeV1)
            {
                return;
            }

            if (!context.ResultSeq.HasValue && context.TriggerSeq.HasValue)
            {
                context.ResultSeq = context.TriggerSeq;
            }

            short errorCode = MapHandshakeErrorCode(context);
            short traceSaved = context.TraceStatus is TraceStatus.Queued or TraceStatus.Full ? (short)1 : (short)0;

            await WriteHandshakeWordAsync(_appConfig.PlcVisionBusyAddress, 0, "VisionBusy", context);
            if (context.ResultSeq.HasValue)
            {
                await WriteHandshakeWordAsync(
                    _appConfig.PlcResultSeqAddress,
                    ClampIntToShort(context.ResultSeq.Value),
                    "ResultSeq",
                    context);
            }

            await WriteHandshakeWordAsync(_appConfig.PlcErrorCodeAddress, errorCode, "ErrorCode", context);
            await WriteHandshakeWordAsync(_appConfig.PlcTraceSavedAddress, traceSaved, "TraceSaved", context);
            await WriteHandshakeWordAsync(_appConfig.PlcInspectionDoneAddress, 1, "InspectionDone", context);
            await WriteHandshakeWordAsync(_appConfig.PlcHeartbeatAddress, 1, "Heartbeat", context);

            DiagLog($"HandshakeV1完成[{context.InspectionId}]: Result={(isQualified ? "OK" : "NG")}, ResultSeq={context.ResultSeq?.ToString() ?? "-"}, TraceSaved={traceSaved}, ErrorCode={errorCode}");
        }

        private async Task<bool> WriteHandshakeWordAsync(
            string address,
            short value,
            string signalName,
            InspectionContext context)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            bool success = await _plcService.WriteResultAsync(address, value);
            if (!success)
            {
                string message = $"HandshakeV1写入失败: {signalName}@{address}={value}";
                DiagLog($"❌ [{context.TriggerSource}] [{context.InspectionId}] {message}");
                RecordHealthError("PLC.HandshakeV1", message, context.InspectionId);
            }

            return success;
        }

        private static short MapHandshakeErrorCode(InspectionContext context)
        {
            if (string.IsNullOrWhiteSpace(context.ErrorCode))
            {
                return 0;
            }

            string stage = context.ErrorStage ?? string.Empty;
            if (stage.Contains(nameof(InspectionStage.BarcodeRead), StringComparison.OrdinalIgnoreCase))
            {
                return 150;
            }

            if (stage.Contains(nameof(InspectionStage.Capture), StringComparison.OrdinalIgnoreCase))
            {
                return 100;
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

        private async Task<bool> WriteDetectionResultToPlc(bool isQualified, InspectionContext? context = null)
        {
            if (!_plcService.IsConnected)
            {
                if (context != null && string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.PlcWrite, "PlcNotConnected", "PLC未连接，检测结果未写入");
                }

                string suffix = context == null ? "" : $"({context.InspectionId})";
                RecordHealthError("PLC", "PLC未连接，检测结果未写入", context?.InspectionId);
                await _uiController.LogToFrontend($"PLC未连接，检测结果未写入{suffix}", "error");
                return false;
            }

            try
            {
                short writeValue = isQualified ? _appConfig.PlcOkValue : _appConfig.PlcNgValue;
                bool success = await _plcService.WriteResultAsync(_appConfig.PlcResultAddress, writeValue);
                string inspectionId = context?.InspectionId ?? "-";
                DiagLog($"PLC结果写入[{inspectionId}]: 地址={_appConfig.PlcResultAddress}, 值={writeValue}, 判定={(isQualified ? "OK" : "NG")}, 结果={(success ? "成功" : "失败")}");
                if (!success)
                {
                    if (context != null && string.IsNullOrWhiteSpace(context.ErrorCode))
                    {
                        context.SetError(InspectionStage.PlcWrite, "PlcWriteFailed", "PLC写入失败: 结果未成功落地");
                    }

                    string message = "PLC写入失败: 结果未成功落地";
                    RecordHealthError("PLC", message, context?.InspectionId);
                    await _uiController.LogToFrontend($"PLC写入失败({inspectionId}): 结果未成功落地", "error");
                }

                return success;
            }
            catch (Exception ex)
            {
                if (context != null && string.IsNullOrWhiteSpace(context.ErrorCode))
                {
                    context.SetError(InspectionStage.PlcWrite, "PlcWriteException", ex.Message);
                }

                string suffix = context == null ? "" : $"({context.InspectionId})";
                RecordHealthError("PLC", $"PLC写入异常: {ex.Message}", context?.InspectionId);
                await _uiController.LogToFrontend($"PLC写入失败{suffix}: {ex.Message}", "error");
                return false;
            }
        }


        private void btnSettings_Logic()
        {
            // 打开设置对话框 (通过前端密码验证)
            SafeFireAndForget(_uiController.ExecuteScriptAsync("showPasswordModal()"), "显示密码框");
        }

        #endregion

        #region ROI 过滤辅助方法

        /// <summary>
        /// 根据 ROI 区域过滤检测结果（仅保留中心点在 ROI 内的检测框）
        /// </summary>
        private List<YoloResult> FilterResultsByROI(List<YoloResult> results, int imageWidth, int imageHeight)
        {
            if (_currentROI == null || _currentROI.Length != 4 || _currentROI[2] <= 0.001f || _currentROI[3] <= 0.001f)
                return results; // 无 ROI 设置或 ROI 为空（宽度或高度约为0），返回全部结果

            // 将归一化 ROI 转换为像素坐标
            float roiX = _currentROI[0] * imageWidth;
            float roiY = _currentROI[1] * imageHeight;
            float roiW = _currentROI[2] * imageWidth;
            float roiH = _currentROI[3] * imageHeight;

            Debug.WriteLine($"[ROI过滤] ROI区域: X={roiX:F0}, Y={roiY:F0}, W={roiW:F0}, H={roiH:F0}");

            // 过滤：仅保留检测框中心点在 ROI 内的结果
            // 注意：YoloResult 直接有 CenterX, CenterY 属性
            var filtered = results.Where(r =>
            {
                float centerX = r.CenterX;
                float centerY = r.CenterY;
                bool inROI = centerX >= roiX && centerX <= roiX + roiW &&
                             centerY >= roiY && centerY <= roiY + roiH;
                if (!inROI)
                    Debug.WriteLine($"[ROI过滤] 过滤掉: 中心点({centerX:F0},{centerY:F0}) 不在ROI内");
                return inROI;
            }).ToList();

            Debug.WriteLine($"[ROI过滤] 过滤前: {results.Count} 个, 过滤后: {filtered.Count} 个");
            return filtered;
        }

        #endregion
    }
}
