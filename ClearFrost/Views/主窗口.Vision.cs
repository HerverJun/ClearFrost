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
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Yolo;
using ClearFrost.Helpers;
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
            int gpuIndex = Math.Max(0, _appConfig.GpuIndex);

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
                    if (files.Length > 0)
                    {
                        // 按文件名升序确定首选模型，避免依赖文件系统返回顺序。
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        模型名 = Path.GetFileName(files[0]);
                    }
                }
            }

            if (!string.IsNullOrEmpty(模型名))
            {
                try
                {
                    string modelPath = Path.Combine(模型路径, 模型名);
                    bool success = await _detectionService.LoadModelAsync(modelPath, useGpu, gpuIndex);
                    if (success)
                    {
                        模型名 = Path.GetFileName(modelPath);
                        if (!string.Equals(_appConfig.CurrentModelFileName, 模型名, StringComparison.OrdinalIgnoreCase))
                        {
                            _appConfig.CurrentModelFileName = 模型名;
                            _appConfig.Save();
                        }

                        await _uiController.LogToFrontend(BuildModelLoadStatusMessage($"模型加载成功: {模型名}"), "success");
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

                    InspectionRuleSet ruleSet = _appConfig.GetInspectionRuleSet();
                    InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(ruleSet);

                    // 执行模型推理，最终 OK/NG 在 ROI 过滤后由规则引擎判定。
                    var result = await _detectionService.DetectAsync(originalBitmap, _appConfig.Confidence, _appConfig.IouThreshold, fallbackGoal);

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
                        InspectionJudgeResult judgeResult = InspectionRuleEngine.Evaluate(ruleSet, results, labels);
                        result.JudgeResult = judgeResult;
                        result.IsRuleEvaluated = true;
                        result.IsQualified = judgeResult.IsQualified;
                        isQualified = judgeResult.IsQualified;
                        await _uiController.LogToFrontend(
                            $"规则判定: {(judgeResult.IsQualified ? "OK" : "NG")} | {judgeResult.Summary}",
                            judgeResult.IsQualified ? "info" : "warning");
                    }
                    using (var sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(originalBitmap))
                    using (var renderedMat = TryRenderDetectionMat(sourceMat, results, labels))
                    {
                        // 追溯保存原图与带框复查图；数据集收集只使用原图路径。
                        await SaveDetectionImage(sourceMat, isQualified, renderedMat);

                        _statisticsService.RecordDetection(isQualified);

                        string objDesc = GetDetailedDetectionLog(results, labels);
                        string modelInfo = result.WasFallback ? $" [切换至: {result.UsedModelName}]" : "";
                        string ruleInfo = BuildRuleStatus(result.JudgeResult);
                        string statusMessage = detectionFailed
                            ? $"检测失败，已判定为不合格: {result.ErrorMessage} | {sw.ElapsedMilliseconds}ms"
                            : $"检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc}{ruleInfo} | {sw.ElapsedMilliseconds}ms{modelInfo}";
                        await _uiController.SendDetectionFrame(
                            renderedMat ?? sourceMat,
                            isQualified,
                            _statisticsService.Current,
                            statusMessage,
                            isQualified && !detectionFailed ? "success" : "error",
                            (_detectionService as DetectionService)?.GetLastMetrics(),
                            actualCount: results.Count,
                            usedModelName: result.UsedModelName ?? _detectionService.CurrentModelName,
                            wasFallback: result.WasFallback,
                            totalMs: sw.ElapsedMilliseconds,
                            sourceLabel: "本地推理");
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

                bool success = await _detectionService.LoadModelAsync(modelPath, _appConfig.EnableGpu, _appConfig.GpuIndex);
                if (success)
                {
                    模型名 = modelFileName;
                    _appConfig.CurrentModelFileName = modelFileName;
                    _appConfig.Save();
                    await _uiController.LogToFrontend(BuildModelLoadStatusMessage($"模型切换成功: {modelFileName}"), "success");
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

        private static string BuildRuleStatus(InspectionJudgeResult? judgeResult)
        {
            if (judgeResult == null)
            {
                return string.Empty;
            }

            string summary = string.IsNullOrWhiteSpace(judgeResult.Summary)
                ? "-"
                : judgeResult.Summary;
            return $" | 规则: {(judgeResult.IsQualified ? "OK" : "NG")} [{summary}]";
        }

        private readonly record struct DetectionCycleRequest(
            string TriggerSource,
            string InspectionId,
            int? TriggerSeq,
            InspectionContext Context);

        private readonly record struct BarcodeReadResult(
            string? ProductBarcode,
            bool? ReadSucceeded,
            string? ErrorCode,
            string? Message);

        private async Task btnCapture_LogicAsync(string triggerSource = "手动", int? triggerSeq = null)
        {
            if (!await EnsureStartupReadyForProductionAsync("检测"))
            {
                return;
            }

            if (!await EnsureCameraReadyForManualInspectionAsync(triggerSource))
            {
                return;
            }

            DetectionTriggerDecision decision = await TryStartDetectionCycleAsync(triggerSource, null);
            if (!decision.Accepted)
            {
                return;
            }

            DateTimeOffset triggerTime = DateTimeOffset.Now;
            string inspectionId = InspectionIdGenerator.Next(triggerSource, triggerTime);
            var context = new InspectionContext
            {
                InspectionId = inspectionId,
                TriggerTime = triggerTime,
                TriggerSource = triggerSource,
                TriggerSeq = triggerSeq,
                CurrentStage = InspectionStage.Triggered,
                TraceStatus = TraceStatus.Unknown
            };

            DiagLog($"▶ [{triggerSource}] [{inspectionId}] btnCapture_LogicAsync 进入, 线程ID={Thread.CurrentThread.ManagedThreadId}");
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
                await _uiController.SendInspectionUpdate(
                    context,
                    message: "检测已触发",
                    usedModelName: _detectionService.CurrentModelName,
                    barcodeEnabled: _appConfig.BarcodeEnabled);

                (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount, finalAttemptCount) =
                    await ExecuteDetectionCycleAsync(request, _appShutdownCts.Token);
            }
            catch (Exception ex)
            {
                context.MarkFailed(InspectionStage.Unknown, "UnhandledDetectionException", ex.Message);
                DiagLog($"❌ [{request.TriggerSource}] [{request.InspectionId}] 检测异常: {ex.Message}");
                await _uiController.LogToFrontend($"检测异常({request.InspectionId}): {ex.Message}", "error");
                await _uiController.SendInspectionUpdate(
                    context,
                    false,
                    ex.Message,
                    0,
                    _detectionService.CurrentModelName,
                    false,
                    _appConfig.BarcodeEnabled);
            }
            finally
            {
                totalSw.Stop();
                context.TotalMs = totalSw.ElapsedMilliseconds;
                await WriteHandshakeDetectionCompletedAsync(context, finalQualified);
                _healthMonitor.RecordInspection(context);
                await SendHealthSnapshotToFrontendAsync();
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

        private async Task<DetectionTriggerDecision> TryStartDetectionCycleAsync(string triggerSource, string? inspectionId)
        {
            DetectionTriggerDecision decision = await _detectionGate.TryEnterAsync(IsShutdownInProgress);
            if (decision.Accepted)
            {
                return decision;
            }

            DetectionDropReason reason = decision.DropReason ?? DetectionDropReason.Busy;
            DetectionDropSnapshot snapshot = _detectionGate.GetSnapshot();
            string summary = $"busy={snapshot.BusyCount}, debounce={snapshot.DebounceCount}, shutdown={snapshot.ShutdownCount}";
            string idSuffix = string.IsNullOrWhiteSpace(inspectionId) ? string.Empty : $"({inspectionId})";

            switch (reason)
            {
                case DetectionDropReason.Shutdown:
                    DiagLog($"⚠ [{triggerSource}] [{inspectionId ?? "-"}] 软件正在退出，已忽略检测请求 | {summary}");
                    if (IsManualTriggerSource(triggerSource))
                    {
                        await _uiController.LogToFrontend($"软件正在退出，已忽略检测请求{idSuffix}", "warning");
                    }
                    break;
                case DetectionDropReason.Debounce:
                    DiagLog($"⚠ [{triggerSource}] [{inspectionId ?? "-"}] 触发命中防抖窗口，已忽略 | {summary}");
                    if (IsManualTriggerSource(triggerSource))
                    {
                        await _uiController.LogToFrontend($"检测触发过于频繁，已忽略本次请求{idSuffix}", "warning");
                    }
                    break;
                default:
                    DiagLog($"⚠ [{triggerSource}] [{inspectionId ?? "-"}] 信号量已被占用，跳过 | {summary}");
                    if (IsManualTriggerSource(triggerSource))
                    {
                        await _uiController.LogToFrontend($"检测正在进行中，请稍候...{idSuffix}", "warning");
                    }
                    break;
            }

            return decision;
        }

        private async Task<bool> EnsureCameraReadyForManualInspectionAsync(string triggerSource)
        {
            if (!IsManualTriggerSource(triggerSource))
            {
                return true;
            }

            if (IsCameraReadyForInspection(out string message))
            {
                return true;
            }

            RecordHealthError("Camera", $"手动检测已阻止: {message}");
            await _uiController.UpdateConnection("cam", false);
            await _uiController.SendUiCommand("toast", new
            {
                message,
                type = "warning",
                durationMs = 2600
            });
            await _uiController.LogToFrontend($"手动拍照已阻止: {message}", "warning");
            await SendHealthSnapshotToFrontendAsync();
            return false;
        }

        private bool IsCameraReadyForInspection(out string message)
        {
            if (_isCameraOpening)
            {
                message = "相机正在连接中，请稍候";
                return false;
            }

            if (_cameraManager.ActiveCamera == null)
            {
                message = "未配置活动相机，请先在设置中配置相机";
                return false;
            }

            if (!_cameraService.IsOpen)
            {
                message = "未连接相机，请先启动系统并确认相机连接成功";
                return false;
            }

            bool isGrabbing;
            try
            {
                isGrabbing = _cameraService.IsGrabbing;
            }
            catch (Exception ex)
            {
                message = $"相机状态读取失败: {ex.Message}";
                return false;
            }

            if (!isGrabbing)
            {
                message = "相机未处于采集状态，请重新启动系统";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private static bool IsManualTriggerSource(string triggerSource)
        {
            return string.IsNullOrWhiteSpace(triggerSource)
                || triggerSource.Contains("手动", StringComparison.OrdinalIgnoreCase)
                || triggerSource.Contains("MANUAL", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<(long CaptureMs, long InferenceMs, long RoiFilterMs, long PlcWriteMs, long RenderToUiMs, long SaveQueueMs, long DbWriteMs, bool FinalQualified, int FinalResultCount, int AttemptCount)> ExecuteDetectionCycleAsync(
            DetectionCycleRequest request,
            CancellationToken cancellationToken)
        {
            InspectionContext context = request.Context;
            bool isManualTrigger = IsManualTriggerSource(request.TriggerSource);
            if (isManualTrigger)
            {
                await _uiController.LogToFrontend($"开始检测... ({request.TriggerSource}触发, ID: {request.InspectionId})", "info");
            }
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
            List<ImageSavePayload>? imagePayloads = null;
            DetectionPersistencePayload? persistencePayload = null;
            string? productBarcode = null;
            bool? barcodeReadSucceeded = null;
            string? barcodeError = null;
            string? usedModelNameForUi = _detectionService.CurrentModelName;
            bool wasFallbackForUi = false;

            Mat? frameToProcess = null;

            var captureSw = new Stopwatch();
            try
            {
                await _uiController.SendInspectionUpdate(
                    context,
                    message: "检测流程启动",
                    usedModelName: usedModelNameForUi,
                    barcodeEnabled: _appConfig.BarcodeEnabled);

                if (_appConfig.BarcodeEnabled)
                {
                    context.CurrentStage = InspectionStage.Barcode;
                    await _uiController.SendInspectionUpdate(
                        context,
                        message: "读取 PLC 条码",
                        usedModelName: usedModelNameForUi,
                        barcodeEnabled: true);

                    BarcodeReadResult barcode = await ReadBarcodeForInspectionAsync(context);
                    productBarcode = barcode.ProductBarcode;
                    barcodeReadSucceeded = barcode.ReadSucceeded;
                    barcodeError = barcode.ErrorCode;
                    if (barcodeReadSucceeded == true && string.IsNullOrWhiteSpace(productBarcode))
                    {
                        barcodeReadSucceeded = false;
                        barcodeError = "NoBarcode";
                    }

                    context.ProductBarcode = productBarcode;
                    context.BarcodeReadSucceeded = barcodeReadSucceeded;
                    context.BarcodeError = barcodeError;

                    bool barcodeFailed = barcodeReadSucceeded == false || string.IsNullOrWhiteSpace(productBarcode);
                    string barcodeMessage = barcodeFailed
                        ? (barcodeError == "NoBarcode" ? "PLC 条码为空" : barcode.Message ?? "PLC 条码读取失败")
                        : "PLC 条码读取成功";
                    await _uiController.SendInspectionUpdate(
                        context,
                        isOk: barcodeFailed && _appConfig.BarcodeRequired ? false : null,
                        message: barcodeMessage,
                        usedModelName: usedModelNameForUi,
                        barcodeEnabled: true,
                        productBarcode: productBarcode,
                        barcodeReadSucceeded: barcodeReadSucceeded,
                        barcodeError: barcodeError);

                    if (_appConfig.BarcodeRequired && barcodeFailed)
                    {
                        string errorCode = barcodeError ?? "NoBarcode";
                        string detail = errorCode == "NoBarcode"
                            ? "PLC 条码为空，已按 NG 处理"
                            : "PLC 条码读取失败，已按 NG 处理";
                        context.MarkFailed(InspectionStage.Barcode, errorCode, detail);

                        var plcSw = Stopwatch.StartNew();
                        await WriteDetectionResultToPlc(false, context);
                        plcSw.Stop();
                        plcWriteMs = plcSw.ElapsedMilliseconds;
                        context.PlcWriteMs = plcWriteMs;

                        _statisticsService.RecordDetection(false);
                        _storageService.WriteDetectionLog(
                            $"InspectionId: {request.InspectionId}{Environment.NewLine}{detail}",
                            false);

                        context.TotalMs = plcWriteMs;
                        var barcodeFailurePayload = BuildDetectionPersistencePayload(
                            context,
                            null,
                            new List<YoloResult>(),
                            0,
                            false,
                            JsonSerializer.Serialize(new { Error = detail, Stage = "Barcode", context.InspectionId, ProductBarcode = productBarcode ?? string.Empty }));
                        dbWriteMs = await EnqueueDetectionRecordAsync(context, barcodeFailurePayload, imageQueued: false);
                        context.CurrentStage = InspectionStage.Failed;
                        await _uiController.SendInspectionUpdate(
                            context,
                            false,
                            detail,
                            0,
                            usedModelNameForUi,
                            wasFallbackForUi,
                            true,
                            productBarcode,
                            barcodeReadSucceeded,
                            barcodeError);

                        return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, false, 0, Math.Max(1, attemptCount));
                    }
                }

                context.CurrentStage = InspectionStage.Capture;
                captureSw.Start();
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
                context.CurrentStage = InspectionStage.Failed;
                await _uiController.SendInspectionUpdate(
                    context,
                    false,
                    detail,
                    0,
                    usedModelNameForUi,
                    wasFallbackForUi,
                    _appConfig.BarcodeEnabled,
                    productBarcode,
                    barcodeReadSucceeded,
                    barcodeError);

                return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount, Math.Max(1, attemptCount));
            }

            using (frameToProcess)
            {
                try
                {
                    context.CurrentStage = InspectionStage.Inference;
                    var inferSw = Stopwatch.StartNew();
                    InspectionRuleSet ruleSet = _appConfig.GetInspectionRuleSet();
                    InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
                    DetectionResultData result = await _detectionService.DetectAsync(
                        frameToProcess,
                        _appConfig.Confidence,
                        _appConfig.IouThreshold,
                        fallbackGoal);
                    inferSw.Stop();
                    inferenceMs = inferSw.ElapsedMilliseconds;
                    context.InferenceMs = inferenceMs;

                    bool isQualified = result.IsQualified;
                    List<YoloResult> results = result.Results ?? new List<YoloResult>();
                    bool detectionFailed = result.HasError;
                    usedModelNameForUi = string.IsNullOrWhiteSpace(result.UsedModelName)
                        ? _detectionService.CurrentModelName
                        : result.UsedModelName;
                    wasFallbackForUi = result.WasFallback;

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
                        InspectionJudgeResult judgeResult = InspectionRuleEngine.Evaluate(ruleSet, results, labels);
                        result.JudgeResult = judgeResult;
                        result.IsRuleEvaluated = true;
                        result.IsQualified = judgeResult.IsQualified;
                        isQualified = judgeResult.IsQualified;
                        string judgeMessage = $"规则判定({request.InspectionId}): {(judgeResult.IsQualified ? "OK" : "NG")} | {judgeResult.Summary}";
                        DiagLog(judgeMessage);
                        if (isManualTrigger)
                        {
                            await _uiController.LogToFrontend(
                                judgeMessage,
                                judgeResult.IsQualified ? "info" : "warning");
                        }
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
                        string ruleInfo = BuildRuleStatus(result.JudgeResult);
                        string statusMessage = detectionFailed
                            ? $"[{request.TriggerSource}] ID {request.InspectionId} 检测失败，已判定为不合格: {result.ErrorMessage} | {inferenceMs}ms"
                            : $"[{request.TriggerSource}] ID {request.InspectionId} 检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc}{ruleInfo} | {inferenceMs}ms{modelInfo}";
                        await _uiController.SendDetectionFrame(
                            renderedMat ?? frameToProcess,
                            isQualified,
                            _statisticsService.Current,
                            statusMessage,
                            isQualified && !detectionFailed ? "success" : "error",
                            (_detectionService as DetectionService)?.GetLastMetrics(),
                            context,
                            finalResultCount,
                            usedModelNameForUi,
                            wasFallbackForUi,
                            context.TotalMs,
                            null,
                            _appConfig.BarcodeEnabled,
                            productBarcode,
                            barcodeReadSucceeded,
                            barcodeError);
                        renderSw.Stop();
                        renderToUiMs = renderSw.ElapsedMilliseconds;
                        context.RenderToUiMs = renderToUiMs;

                        imagePayloads = CreateImageSavePayloads(
                            context,
                            frameToProcess,
                            isQualified,
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

                    imagePayloads = CreateImageSavePayloads(
                        context,
                        frameToProcess,
                        false);
                    (bool errorImageQueued, saveQueueMs) = await EnqueueImagePayloadsAsync(context, imagePayloads);
                    context.TotalMs = captureMs + inferenceMs + roiFilterMs + plcWriteMs + renderToUiMs + saveQueueMs;
                    var errorPayload = BuildDetectionPersistencePayload(
                        context,
                        null,
                        new List<YoloResult>(),
                        0,
                        false,
                        JsonSerializer.Serialize(new { Error = ex.Message, Stage = failedStage.ToString(), context.InspectionId }));
                    dbWriteMs = await EnqueueDetectionRecordAsync(context, errorPayload, errorImageQueued);
                    context.CurrentStage = InspectionStage.Failed;
                    await _uiController.SendInspectionUpdate(
                        context,
                        false,
                        ex.Message,
                        0,
                        usedModelNameForUi,
                        wasFallbackForUi,
                        _appConfig.BarcodeEnabled,
                        productBarcode,
                        barcodeReadSucceeded,
                        barcodeError);

                    return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, false, 0, Math.Max(1, attemptCount));
                }
            }

            bool imageQueuedForRecord;
            (imageQueuedForRecord, saveQueueMs) = await EnqueueImagePayloadsAsync(context, imagePayloads);

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
            await _uiController.SendInspectionUpdate(
                context,
                finalQualified,
                null,
                finalResultCount,
                usedModelNameForUi,
                wasFallbackForUi,
                _appConfig.BarcodeEnabled,
                productBarcode,
                barcodeReadSucceeded,
                barcodeError);

            return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount, Math.Max(1, attemptCount));
        }

        private async Task<BarcodeReadResult> ReadBarcodeForInspectionAsync(InspectionContext context)
        {
            try
            {
                var (success, value) = await _plcService.ReadStringAsync(
                    _appConfig.BarcodeAddress,
                    _appConfig.BarcodeWordLength,
                    _appConfig.BarcodeEncoding);
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

        private List<ImageSavePayload>? CreateImageSavePayloads(Mat image, bool isQualified, Mat? renderedImage = null)
        {
            var context = new InspectionContext
            {
                InspectionId = InspectionIdGenerator.Next("TEST"),
                TriggerTime = DateTimeOffset.Now,
                TriggerSource = "TEST"
            };

            return CreateImageSavePayloads(context, image, isQualified, renderedImage);
        }

        private List<ImageSavePayload>? CreateImageSavePayloads(InspectionContext context, Mat image, bool isQualified, Mat? renderedImage = null)
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
                payloads.Add(ImageSavePayload.Create(
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
                    payloads.Add(ImageSavePayload.Create(
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

        private async Task<string> SaveDetectionImage(Mat image, bool isQualified, Mat? renderedImage = null)
        {
            List<ImageSavePayload>? payloads = CreateImageSavePayloads(image, isQualified, renderedImage);
            if (payloads == null || payloads.Count == 0)
            {
                return string.Empty;
            }

            string originalPath = payloads[0].Path;
            foreach (ImageSavePayload payload in payloads)
            {
                bool enqueued = _imageSaveQueue.Enqueue(payload);
                if (!enqueued)
                {
                    payload.Dispose();
                }
            }

            return originalPath;
        }

        private async Task<(bool Queued, long ElapsedMs)> EnqueueImagePayloadsAsync(InspectionContext context, List<ImageSavePayload>? payloads)
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
                await _uiController.LogToFrontend($"图像保存入队失败({context.InspectionId}): 载荷为空", "error");
                DiagLog($"❌ [{context.TriggerSource}] [{context.InspectionId}] 图像保存入队失败: 载荷为空");
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

            if (!imageQueued)
            {
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

        private string BuildModelLoadStatusMessage(string prefix)
        {
            DetectionRuntimeStatus status = _detectionService.RuntimeStatus;
            if (status.GpuRequested && !status.GpuActive)
            {
                string reason = string.IsNullOrWhiteSpace(status.GpuFailureReason)
                    ? "DirectML 探针未通过"
                    : status.GpuFailureReason;
                return $"{prefix}，DirectML GPU 未生效，已使用 CPU（{reason}）";
            }

            return status.GpuActive
                ? $"{prefix}，DirectML GPU 已生效 (GPU {status.GpuDeviceId})"
                : $"{prefix}，当前使用 CPU";
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
            InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(_appConfig.GetInspectionRuleSet());

            return new DetectionPersistencePayload
            {
                Timestamp = context.TriggerTime.LocalDateTime,
                IsQualified = isQualified,
                InspectionId = context.InspectionId,
                TriggerSource = context.TriggerSource,
                TriggerSeq = context.TriggerSeq,
                ResultSeq = context.ResultSeq,
                ProductBarcode = context.ProductBarcode ?? string.Empty,
                BarcodeReadSucceeded = context.BarcodeReadSucceeded,
                BarcodeError = context.BarcodeError ?? string.Empty,
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
                TargetLabel = fallbackGoal?.TargetLabel ?? string.Empty,
                ExpectedCount = fallbackGoal?.TargetCount ?? 0,
                ActualCount = actualCount,
                CameraId = _cameraManager.ActiveCameraId ?? string.Empty,
                RuleSummary = result?.JudgeResult?.Summary ?? string.Empty,
                RuleResultJson = SerializeRuleResults(result?.JudgeResult),
                RuleSetJson = _appConfig.InspectionRuleSetJson ?? string.Empty,
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

        private static string SerializeRuleResults(InspectionJudgeResult? judgeResult)
        {
            if (judgeResult == null || judgeResult.RuleResults.Count == 0)
            {
                return string.Empty;
            }

            return JsonSerializer.Serialize(new
            {
                judgeResult.IsQualified,
                judgeResult.Summary,
                Rules = judgeResult.RuleResults
            });
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
            if (_appConfig.TriggerSource != TriggerSource.PLC)
            {
                return;
            }

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
            if (_appConfig.TriggerSource != TriggerSource.PLC)
            {
                return;
            }

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
            // 打开设置对话框
            SafeFireAndForget(_uiController.SendProjectPresets(ProjectPresetStore.Load()), "加载项目预设");
            SafeFireAndForget(_uiController.SendCurrentConfig(_appConfig), "打开设置");
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
