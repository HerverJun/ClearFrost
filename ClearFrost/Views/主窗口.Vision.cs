
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
using ClearFrost.Core.DeepLearning;
using ClearFrost.Core.Inspection;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Yolo;
using ClearFrost.Helpers;
using ClearFrost.Services;

namespace ClearFrost
{
    // ============================================================================
    // 文件名: 主窗口.Vision.cs
    // 作者: 蘅芜君
    // 描述:   主窗口中的视觉检测、规则判定和检测结果展示逻辑
    //
    // 功能:
    //   - 初始化/切换 YOLO 模型与辅助模型
    //   - 处理手动检测、历史图复判和检测流水线回调
    //   - 统一完成 ROI 过滤、规则判定、图像渲染和追溯图入队
    // ============================================================================

    public partial class 主窗口
    {
        #region 5. YOLO检测逻辑 (检测与视觉逻辑)

        private void InitYolo()
        {
            // 初始化由 WinForms 生命周期触发，使用统一的 fire-and-forget 包装记录异常。
            SafeFireAndForget(InitYoloAsync(), "YOLO初始化");
        }

        private async Task InitYoloAsync()
        {
            await _uiController.LogToFrontend("正在加载 YOLO 模型...", "info");

            bool useGpu = _appConfig.EnableGpu;
            int gpuIndex = Math.Max(0, _appConfig.GpuIndex);

            try
            {
                ProductionModelActivationResult result = await _modelActivationService.LoadConfiguredModelsAsync(
                    "主模型初始化",
                    useGpu,
                    gpuIndex).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    模型名 = _appConfig.CurrentModelFileName?.Trim() ?? string.Empty;
                    await _uiController.LogToFrontend(BuildModelLoadStatusMessage($"模型加载成功: {模型名}"), "success");
                    await _uiController.SendModelLabels(_detectionService.GetLabels());
                    return;
                }

                await _uiController.LogToFrontend(
                    $"{OperatorFaultMessages.ForActivationFailure(result.ErrorCode, result.Message)}{FormatCompensationFailures(result)}",
                    result.IsFaulted ? "error" : "warning");
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"模型加载失败: {ex.Message}", "error");
            }
        }

        private async Task RestoreMultiModelConfigAsync()
        {
            _detectionService.SetEnableFallback(_appConfig.EnableMultiModelFallback);
            ProductionModelActivationResult result = await _modelActivationService.LoadConfiguredModelsAsync(
                "辅助模型恢复",
                _appConfig.EnableGpu,
                Math.Max(0, _appConfig.GpuIndex)).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await _uiController.LogToFrontend(
                    $"{OperatorFaultMessages.ForActivationFailure(result.ErrorCode, result.Message)}{FormatCompensationFailures(result)}",
                    result.IsFaulted ? "error" : "warning");
            }
        }

        private async Task TestYolo_HandlerAsync()
        {
            try
            {
                await _uiController.LogToFrontend("开始测试推理...", "info");

                string? selectedFile = await ShowOpenFileDialogOnStaThread("选择测试图片", "图像文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*");

                if (string.IsNullOrEmpty(selectedFile))
                {
                    await _uiController.LogToFrontend("已取消测试", "warning");
                    return;
                }

                await _uiController.LogToFrontend($"测试推理图片: {Path.GetFileName(selectedFile)}", "info");

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
                    string ruleSetJson = InspectionRuleSetSerializer.Serialize(ruleSet);
                    InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
                    float[]? roiSnapshot = SnapshotCurrentROI();
                    // 测试推理使用与生产检测一致的候选评估，避免设置页测试和现场判定逻辑分叉。
                    MultiModelCandidateEvaluator candidateEvaluator = _appRuntime.DecisionEvaluator.CreateCandidateEvaluator(
                        ruleSet,
                        originalBitmap.Width,
                        originalBitmap.Height,
                        roiSnapshot);

                    // 执行模型推理，最终 OK/NG 在 ROI 过滤后由规则引擎判定。
                    var result = await _detectionService.DetectAsync(
                        originalBitmap,
                        _appConfig.Confidence,
                        _appConfig.IouThreshold,
                        fallbackGoal,
                        candidateEvaluator);
                    ApplyRuleTraceSnapshot(result, ruleSetJson, fallbackGoal);

                    sw.Stop();

                    // 检测服务返回的是模型原始结果，最终 OK/NG 以 ROI 过滤后的规则评估为准。
                    var results = result.Results ?? new List<YoloResult>();
                    bool isQualified = result.IsQualified;
                    bool detectionFailed = result.HasError;
                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    if (detectionFailed)
                    {
                        isQualified = false;
                        await _uiController.LogToFrontend($"测试推理失败，已强制判定为不合格: {result.ErrorMessage}", "error");
                    }
                    else
                    {
                        InspectionDecisionResult decision = _appRuntime.DecisionEvaluator.Evaluate(new InspectionDecisionRequest
                        {
                            RuleSet = ruleSet,
                            Detections = results,
                            Labels = labels,
                            ImageWidth = originalBitmap.Width,
                            ImageHeight = originalBitmap.Height,
                            Roi = roiSnapshot
                        });
                        results = decision.FilteredDetections.ToList();

                        InspectionJudgeResult judgeResult = decision.JudgeResult;
                        result.JudgeResult = judgeResult;
                        result.IsRuleEvaluated = true;
                        result.IsQualified = decision.Succeeded && judgeResult.IsQualified;
                        isQualified = result.IsQualified;
                        string judgeMessage = decision.Succeeded
                            ? $"测试推理规则判定: {(judgeResult.IsQualified ? "OK" : "NG")} | {judgeResult.Summary}"
                            : $"测试推理 ROI/规则判定失败，已判定为 NG: {decision.Message}";
                        await _uiController.LogToFrontend(
                            judgeMessage,
                            isQualified ? "info" : "warning");
                    }
                    using (var sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(originalBitmap))
                    using (var renderedMat = TryRenderDetectionMat(sourceMat, results, labels))
                    {
                        string objDesc = GetDetailedDetectionLog(results, labels, result.JudgeResult);
                        string modelInfo = BuildFallbackStatus(result);
                        string ruleInfo = BuildRuleStatus(result.JudgeResult);
                        string statusMessage = detectionFailed
                            ? $"[测试推理] 检测失败，已判定为不合格: {result.ErrorMessage} | {sw.ElapsedMilliseconds}ms"
                            : $"[测试推理] 检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc}{ruleInfo} | {sw.ElapsedMilliseconds}ms{modelInfo}";
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
                            sourceLabel: "测试推理",
                            fallbackAttemptCount: result.FallbackAttemptCount,
                            fallbackSkippedReason: result.FallbackSkippedReason,
                            inferenceMs: result.ElapsedMs,
                            ruleSummary: result.JudgeResult?.Summary,
                            rulePrimaryReason: GetRulePrimaryReason(result.JudgeResult),
                            ruleDetails: result.JudgeResult?.Details);
                    }
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"测试失败: {ex.Message}", "error");
            }
        }

        private sealed class HistoryRulePreviewRequest
        {
            public string InspectionId { get; set; } = string.Empty;
            public string Timestamp { get; set; } = string.Empty;
            public string ImagePath { get; set; } = string.Empty;
            public string RenderedImagePath { get; set; } = string.Empty;
            public string RuleSetJson { get; set; } = string.Empty;
        }

        private async Task RunHistoryRulePreviewAsync(string requestJson)
        {
            HistoryRulePreviewRequest request;
            try
            {
                request = JsonSerializer.Deserialize<HistoryRulePreviewRequest>(
                    requestJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new HistoryRulePreviewRequest();
            }
            catch (Exception ex)
            {
                var invalidRequest = new HistoryRulePreviewRequest();
                await SendHistoryRulePreviewStatusAsync(
                    invalidRequest,
                    "failed",
                    null,
                    $"历史图复判参数无效: {ex.Message}");
                return;
            }

            DetectionTriggerDecision decision = await _detectionGate.TryEnterAsync(IsShutdownInProgress);
            if (!decision.Accepted)
            {
                string message = decision.DropReason == DetectionDropReason.Shutdown
                    ? "软件正在退出，已忽略历史图复判"
                    : "检测正在进行中，请稍后再复判历史图";
                await SendHistoryRulePreviewStatusAsync(request, "failed", null, message);
                await _uiController.SendUiCommand("toast", new
                {
                    message,
                    type = "warning",
                    durationMs = 2200
                });
                return;
            }

            // 历史图复判复用检测信号量，避免和实时相机检测同时占用模型/GPU 资源。
            await SendHistoryRulePreviewStatusAsync(
                request,
                "running",
                null,
                "正在用当前规则复判历史图...");

            try
            {
                if (!_detectionService.IsModelLoaded)
                {
                    await SendHistoryRulePreviewStatusAsync(request, "failed", null, "YOLO模型未初始化，无法复判历史图");
                    await _uiController.LogToFrontend("历史图复判失败: YOLO模型未初始化", "error");
                    return;
                }

                string imagePath = ResolveHistoryPreviewImagePath(request);
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    await SendHistoryRulePreviewStatusAsync(request, "failed", null, "历史图文件不存在或路径无效");
                    await _uiController.LogToFrontend("历史图复判失败: 历史图文件不存在或路径无效", "error");
                    return;
                }

                if (!TryResolveHistoryRuleSet(request.RuleSetJson, out InspectionRuleSet ruleSet, out string ruleSetJson, out string ruleError))
                {
                    string message = $"当前规则无效: {ruleError}";
                    await SendHistoryRulePreviewStatusAsync(request, "failed", null, message);
                    await _uiController.LogToFrontend($"历史图复判失败: {message}", "error");
                    return;
                }

                using Bitmap originalBitmap = new Bitmap(imagePath);
                using Mat sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(originalBitmap);
                if (sourceMat.Empty())
                {
                    await SendHistoryRulePreviewStatusAsync(request, "failed", null, "历史图读取失败，图像为空");
                    return;
                }

                Stopwatch totalSw = Stopwatch.StartNew();
                InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
                float[]? roiSnapshot = SnapshotCurrentROI();
                // 对历史图使用当前规则和当前 ROI 重新评估，便于调试规则变更后的影响。
                MultiModelCandidateEvaluator candidateEvaluator = _appRuntime.DecisionEvaluator.CreateCandidateEvaluator(
                    ruleSet,
                    sourceMat.Width,
                    sourceMat.Height,
                    roiSnapshot);

                Stopwatch inferSw = Stopwatch.StartNew();
                DetectionResultData result = await _detectionService.DetectAsync(
                    sourceMat,
                    _appConfig.Confidence,
                    _appConfig.IouThreshold,
                    fallbackGoal,
                    candidateEvaluator);
                inferSw.Stop();
                ApplyRuleTraceSnapshot(result, ruleSetJson, fallbackGoal);

                List<YoloResult> results = result.Results ?? new List<YoloResult>();
                string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();

                bool isQualified = false;
                InspectionJudgeResult? judgeResult = null;
                if (result.HasError)
                {
                    result.IsQualified = false;
                    result.IsRuleEvaluated = true;
                }
                else
                {
                    InspectionDecisionResult ruleDecision = _appRuntime.DecisionEvaluator.Evaluate(new InspectionDecisionRequest
                    {
                        RuleSet = ruleSet,
                        Detections = results,
                        Labels = labels,
                        ImageWidth = sourceMat.Width,
                        ImageHeight = sourceMat.Height,
                        Roi = roiSnapshot
                    });
                    results = ruleDecision.FilteredDetections.ToList();
                    judgeResult = ruleDecision.JudgeResult;
                    result.JudgeResult = judgeResult;
                    result.IsRuleEvaluated = true;
                    result.IsQualified = ruleDecision.Succeeded && judgeResult.IsQualified;
                    isQualified = result.IsQualified;
                    if (!ruleDecision.Succeeded)
                    {
                        isQualified = false;
                        await _uiController.LogToFrontend($"历史图复判 ROI/规则判定失败，已判定为 NG: {ruleDecision.Message}", "warning");
                    }
                }

                totalSw.Stop();
                string summary = judgeResult?.Summary ?? result.ErrorMessage;
                string statusMessage = result.HasError
                    ? $"历史图规则复判失败，已判定为 NG: {result.ErrorMessage}"
                    : $"历史图规则复判: {(isQualified ? "OK" : "NG")} | {summary}{BuildFallbackStatus(result)}";
                string usedModelName = string.IsNullOrWhiteSpace(result.UsedModelName)
                    ? _detectionService.CurrentModelName
                    : result.UsedModelName;

                using Mat? renderedMat = TryRenderDetectionMat(sourceMat, results, labels);
                await _uiController.SendDetectionFrame(
                    renderedMat ?? sourceMat,
                    isQualified,
                    stats: null,
                    logMessage: statusMessage,
                    logType: isQualified && !result.HasError ? "success" : "warning",
                    metrics: (_detectionService as DetectionService)?.GetLastMetrics(),
                    actualCount: results.Count,
                    usedModelName: usedModelName,
                    wasFallback: result.WasFallback,
                    totalMs: totalSw.ElapsedMilliseconds,
                    sourceLabel: "历史规则复判",
                    fallbackAttemptCount: result.FallbackAttemptCount,
                    fallbackSkippedReason: result.FallbackSkippedReason,
                    inferenceMs: result.ElapsedMs,
                    ruleSummary: judgeResult?.Summary,
                    rulePrimaryReason: GetRulePrimaryReason(judgeResult),
                    ruleDetails: judgeResult?.Details);

                await _uiController.SendHistoryRulePreview(new
                {
                    status = "completed",
                    inspectionId = request.InspectionId,
                    timestamp = request.Timestamp,
                    isQualified = isQualified,
                    result = isQualified ? "OK" : "NG",
                    summary = summary,
                    rulePrimaryReason = GetRulePrimaryReason(judgeResult),
                    ruleDetails = judgeResult?.Details,
                    message = statusMessage,
                    actualCount = results.Count,
                    inferenceMs = inferSw.ElapsedMilliseconds,
                    totalMs = totalSw.ElapsedMilliseconds,
                    usedModelName = usedModelName,
                    wasFallback = result.WasFallback
                });
            }
            catch (Exception ex)
            {
                await SendHistoryRulePreviewStatusAsync(
                    request,
                    "failed",
                    null,
                    $"历史图复判失败: {ex.Message}");
                await _uiController.LogToFrontend($"历史图复判失败: {ex.Message}", "error");
            }
            finally
            {
                _detectionGate.Release();
            }
        }

        private async Task SendHistoryRulePreviewStatusAsync(
            HistoryRulePreviewRequest request,
            string status,
            bool? isQualified,
            string message)
        {
            await _uiController.SendHistoryRulePreview(new
            {
                status = status,
                inspectionId = request.InspectionId,
                timestamp = request.Timestamp,
                isQualified = isQualified,
                result = isQualified.HasValue ? (isQualified.Value ? "OK" : "NG") : string.Empty,
                message = message,
                summary = message
            });
        }

        private bool TryResolveHistoryRuleSet(
            string? ruleSetJson,
            out InspectionRuleSet ruleSet,
            out string normalizedJson,
            out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(ruleSetJson))
            {
                // 前端未携带历史规则快照时，用当前配置复判。
                ruleSet = _appConfig.GetInspectionRuleSet();
                normalizedJson = InspectionRuleSetSerializer.Serialize(ruleSet);
                errorMessage = string.Empty;
                return true;
            }

            if (!InspectionRuleSetSerializer.TryDeserialize(ruleSetJson, out ruleSet, out errorMessage))
            {
                normalizedJson = string.Empty;
                return false;
            }

            normalizedJson = InspectionRuleSetSerializer.Serialize(ruleSet);
            return true;
        }

        private string ResolveHistoryPreviewImagePath(HistoryRulePreviewRequest request)
        {
            foreach (string candidate in new[] { request.ImagePath, request.RenderedImagePath })
            {
                string? resolved = TryResolveHistoryImagePath(candidate);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            return string.Empty;
        }

        private string? TryResolveHistoryImagePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string trimmed = path.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
            {
                if (uri.IsFile)
                {
                    return TryResolveHistoryImagePath(uri.LocalPath);
                }

                if (string.Equals(uri.Host, "ng-images.local", StringComparison.OrdinalIgnoreCase))
                {
                    // 前端历史图可能使用虚拟域名 URL，这里还原为本地图片相对路径。
                    string relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
                        .Replace('/', Path.DirectorySeparatorChar);
                    return TryResolveHistoryImagePath(relative);
                }
            }

            if (Path.IsPathRooted(trimmed))
            {
                string fullPath = Path.GetFullPath(trimmed);
                return VisionDebugHistoryImageResolver.ResolveExistingImagePathIfSafe(fullPath);
            }

            foreach (string basePath in GetHistoryImageBasePaths())
            {
                string fullPath = Path.GetFullPath(Path.Combine(basePath, trimmed));
                string? resolved = VisionDebugHistoryImageResolver.ResolveExistingImagePathIfSafe(fullPath);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            return null;
        }

        private async Task HandleVisionDebugCommandAsync(WebUiCommandEventArgs args)
        {
            VisionDebugRunParameters parameters;
            try
            {
                parameters = DeserializeVisionDebugParameters(args.PayloadJson);
            }
            catch (Exception ex)
            {
                await SendVisionDebugErrorAsync(args.RequestId, "InvalidRequest", $"算法调试参数无效: {ex.Message}");
                return;
            }

            try
            {
                switch (args.Command)
                {
                    case "vision_debug_query_recent":
                        await SendVisionDebugRecentRecordsAsync(args.RequestId);
                        break;
                    case "vision_debug_apply_template":
                        await ApplyVisionDebugTemplateAsync(parameters, args.RequestId);
                        break;
                    case "vision_debug_save_params":
                        await SaveVisionDebugParametersAsync(parameters, args.RequestId);
                        break;
                    case "vision_debug_run_history":
                        await RunVisionDebugHistoryAsync(parameters, args.RequestId);
                        break;
                    case "vision_debug_run_batch":
                        await RunVisionDebugBatchHistoryAsync(parameters, args.RequestId);
                        break;
                    case "vision_debug_run_current":
                        await RunVisionDebugCurrentFrameAsync(parameters, args.RequestId);
                        break;
                    default:
                        await SendVisionDebugErrorAsync(args.RequestId, "UnknownVisionDebugCommand", $"未知算法调试命令: {args.Command}");
                        break;
                }
            }
            catch (NotSupportedException ex)
            {
                await SendVisionDebugErrorAsync(args.RequestId, "UnsupportedPreprocessingMode", ex.Message);
            }
            catch (Exception ex)
            {
                await SendVisionDebugErrorAsync(args.RequestId, "VisionDebugException", $"算法调试失败: {ex.Message}");
            }
        }

        private static VisionDebugRunParameters DeserializeVisionDebugParameters(string payloadJson)
        {
            return JsonSerializer.Deserialize<VisionDebugRunParameters>(
                string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new VisionDebugRunParameters();
        }

        private async Task SendVisionDebugRecentRecordsAsync(string? requestId)
        {
            List<DetectionRecord> records = await _databaseService.GetRecordsAsync(limit: 30).ConfigureAwait(false);
            await _uiController.SendVisionDebugResult(new
            {
                status = "recentRecords",
                records = records.Select(record => new
                {
                    record.Id,
                    timestamp = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    record.InspectionId,
                    isQualified = record.IsQualified,
                    result = record.IsQualified ? "OK" : "NG",
                    record.ImagePath,
                    record.RenderedImagePath,
                    record.TraceImagePath,
                    record.ModelName,
                    record.UsedModelName,
                    record.RuleSummary,
                    hasOriginalImage = !string.IsNullOrWhiteSpace(record.ImagePath) && File.Exists(record.ImagePath)
                }).ToArray()
            }, requestId);
        }

        private async Task ApplyVisionDebugTemplateAsync(VisionDebugRunParameters parameters, string? requestId)
        {
            InspectionRuleSet ruleSet = VisionDebugParameterService.ResolveRuleSet(
                _appConfig,
                parameters,
                out string ruleSetJson);
            await _uiController.SendVisionDebugResult(new
            {
                status = "templateApplied",
                templateId = parameters.TemplateId,
                ruleSet = ruleSet,
                ruleSetJson,
                message = "场景模板已生成，仅用于当前调试会话，点击保存参数后才会写入配置"
            }, requestId);
        }

        private async Task SaveVisionDebugParametersAsync(VisionDebugRunParameters parameters, string? requestId)
        {
            if (!await EnsureRuntimeMutationAllowedAsync("算法调试参数保存").ConfigureAwait(false))
            {
                await SendVisionDebugErrorAsync(requestId, "RuntimeMutationBlocked", "系统运行中，暂不允许保存算法调试参数");
                return;
            }

            VisionDebugParameterService.ApplySavedParameters(_appConfig, parameters);
            if (!_appConfig.Save())
            {
                await SendVisionDebugErrorAsync(requestId, "ConfigSaveFailed", _appConfig.LastError ?? "配置保存失败");
                return;
            }

            TrySaveCurrentRecipeSnapshot("算法调试参数保存");
            InspectionRuleSet savedRuleSet = _appConfig.GetInspectionRuleSet();
            await _uiController.SendVisionDebugResult(new
            {
                status = "paramsSaved",
                succeeded = true,
                message = "算法调试参数已保存到生产配置和配方快照",
                confidence = _appConfig.Confidence,
                iouThreshold = _appConfig.IouThreshold,
                targetLabel = _appConfig.TargetLabel,
                targetCount = _appConfig.TargetCount,
                ruleSetJson = InspectionRuleSetSerializer.Serialize(savedRuleSet)
            }, requestId);
            await _uiController.InitSettings(_appConfig);
        }

        private async Task RunVisionDebugCurrentFrameAsync(VisionDebugRunParameters parameters, string? requestId)
        {
            DetectionTriggerDecision decision = await _detectionGate.TryEnterAsync(IsShutdownInProgress).ConfigureAwait(false);
            if (!decision.Accepted)
            {
                string message = decision.DropReason == DetectionDropReason.Shutdown
                    ? "软件正在退出，已忽略算法调试"
                    : "检测正在进行中，请稍后再运行算法调试";
                await SendVisionDebugErrorAsync(requestId, "DetectionBusy", message);
                return;
            }

            try
            {
                using Mat? frame = TryCloneCurrentVisionDebugFrame();
                if (frame == null || frame.Empty())
                {
                    await SendVisionDebugErrorAsync(requestId, "NoCurrentFrame", "当前帧不可用，请先启动相机并完成一次取图");
                    return;
                }

                VisionDebugSnapshot snapshot = await RunVisionDebugOnMatAsync(
                    frame,
                    parameters,
                    comparison: null,
                    requestId,
                    "当前帧算法调试").ConfigureAwait(false);
                await PublishVisionDebugSnapshotAsync(frame, snapshot, requestId).ConfigureAwait(false);
            }
            finally
            {
                _detectionGate.Release();
            }
        }

        private async Task RunVisionDebugHistoryAsync(VisionDebugRunParameters parameters, string? requestId)
        {
            long recordId = parameters.RecordId ?? 0;
            if (recordId <= 0)
            {
                await SendVisionDebugErrorAsync(requestId, "MissingRecordId", "请选择一条历史样本记录");
                return;
            }

            DetectionTriggerDecision decision = await _detectionGate.TryEnterAsync(IsShutdownInProgress).ConfigureAwait(false);
            if (!decision.Accepted)
            {
                string message = decision.DropReason == DetectionDropReason.Shutdown
                    ? "软件正在退出，已忽略历史样本回放"
                    : "检测正在进行中，请稍后再回放历史样本";
                await SendVisionDebugErrorAsync(requestId, "DetectionBusy", message);
                return;
            }

            try
            {
                DetectionRecord? record = await _databaseService.GetDetectionRecordByIdAsync(recordId).ConfigureAwait(false);
                if (record == null)
                {
                    await SendVisionDebugErrorAsync(requestId, "RecordNotFound", $"历史样本不存在: {recordId}");
                    return;
                }

                VisionDebugHistoryImageResolution imageResolution = ResolveDebugHistoryImagePath(record);
                if (!imageResolution.Succeeded)
                {
                    await SendVisionDebugErrorAsync(requestId, "HistoryImageMissing", imageResolution.FailureReason);
                    return;
                }

                using Mat image = Cv2.ImRead(imageResolution.ImagePath, ImreadModes.Color);
                if (image.Empty())
                {
                    await SendVisionDebugErrorAsync(requestId, "ImageReadFailed", $"历史样本图片读取失败: {imageResolution.ImagePath}");
                    return;
                }

                var comparison = new VisionDebugComparison
                {
                    RecordId = record.Id,
                    InspectionId = record.InspectionId,
                    OldIsQualified = record.IsQualified,
                    OldPrimaryReason = ResolveRecordPrimaryReason(record),
                    ImagePath = imageResolution.ImagePath,
                    UsedRenderedImage = imageResolution.UsedRenderedImage,
                    ImageSourceKind = imageResolution.SourceKind,
                    ImageWarning = imageResolution.Warning
                };
                VisionDebugSnapshot snapshot = await RunVisionDebugOnMatAsync(
                    image,
                    parameters,
                    comparison,
                    requestId,
                    "历史样本算法调试").ConfigureAwait(false);
                snapshot.ImageSourceKind = imageResolution.SourceKind;
                snapshot.ImageSourceWarning = imageResolution.Warning;
                await PublishVisionDebugSnapshotAsync(image, snapshot, requestId).ConfigureAwait(false);
            }
            finally
            {
                _detectionGate.Release();
            }
        }

        private async Task RunVisionDebugBatchHistoryAsync(VisionDebugRunParameters parameters, string? requestId)
        {
            int requestedLimit = parameters.BatchLimit ?? VisionDebugBatchReplayService.DefaultLimit;
            int effectiveLimit = VisionDebugBatchReplayService.ClampLimit(parameters.BatchLimit);
            bool? resultFilter = VisionDebugBatchReplayService.ParseResultFilter(parameters.BatchResult);

            DetectionTriggerDecision decision = await _detectionGate.TryEnterAsync(IsShutdownInProgress).ConfigureAwait(false);
            if (!decision.Accepted)
            {
                string message = decision.DropReason == DetectionDropReason.Shutdown
                    ? "软件正在退出，已忽略批量历史样本回放"
                    : "检测正在进行中，请稍后再批量回放历史样本";
                await SendVisionDebugErrorAsync(requestId, "DetectionBusy", message);
                return;
            }

            try
            {
                if (!_detectionService.IsModelLoaded)
                {
                    await SendVisionDebugErrorAsync(requestId, "ModelNotLoaded", "YOLO模型未初始化，无法批量回放历史样本");
                    return;
                }

                List<DetectionRecord> records = await _databaseService.GetReplayRecordsAsync(new DetectionReplayQuery
                {
                    IsQualified = resultFilter,
                    Limit = effectiveLimit
                }).ConfigureAwait(false);

                var items = new List<VisionDebugBatchReplayItem>();
                foreach (DetectionRecord record in records)
                {
                    var item = new VisionDebugBatchReplayItem
                    {
                        RecordId = record.Id,
                        InspectionId = record.InspectionId,
                        Timestamp = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        OldIsQualified = record.IsQualified,
                        OldPrimaryReason = ResolveRecordPrimaryReason(record)
                    };

                    VisionDebugHistoryImageResolution imageResolution = ResolveDebugHistoryImagePath(record);
                    if (!imageResolution.Succeeded)
                    {
                        item.Status = "missingImage";
                        item.ImageMissing = true;
                        item.FailureReason = imageResolution.FailureReason;
                        items.Add(item);
                        continue;
                    }

                    item.ImagePath = imageResolution.ImagePath;
                    item.UsedRenderedImage = imageResolution.UsedRenderedImage;
                    item.ImageSourceKind = imageResolution.SourceKind;
                    item.ImageWarning = imageResolution.Warning;

                    try
                    {
                        using Mat image = Cv2.ImRead(imageResolution.ImagePath, ImreadModes.Color);
                        if (image.Empty())
                        {
                            item.Status = "failed";
                            item.FailureReason = "图片读取失败";
                            items.Add(item);
                            continue;
                        }

                        VisionDebugSnapshot snapshot = await RunVisionDebugOnMatAsync(
                            image,
                            parameters,
                            comparison: null,
                            requestId,
                            $"批量历史样本 {record.Id}",
                            writeFrontendLog: false).ConfigureAwait(false);
                        item.Status = "completed";
                        item.NewIsQualified = snapshot.FinalOk;
                        item.NewPrimaryReason = snapshot.PrimaryFailureReason;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "failed";
                        item.FailureReason = ex.Message;
                    }

                    items.Add(item);
                }

                VisionDebugBatchReplaySummary summary = VisionDebugBatchReplayService.BuildSummary(
                    items,
                    requestedLimit,
                    effectiveLimit);

                await _uiController.SendVisionDebugResult(new
                {
                    status = "batchCompleted",
                    succeeded = true,
                    batchReplay = summary,
                    message = $"批量回放完成：{summary.CompletedCount}/{summary.TotalRecords} 条完成，变化 {summary.ChangedCount} 条"
                }, requestId).ConfigureAwait(false);
            }
            finally
            {
                _detectionGate.Release();
            }
        }

        private async Task<VisionDebugSnapshot> RunVisionDebugOnMatAsync(
            Mat image,
            VisionDebugRunParameters parameters,
            VisionDebugComparison? comparison,
            string? requestId,
            string sourceLabel,
            bool writeFrontendLog = true)
        {
            if (!_detectionService.IsModelLoaded)
            {
                throw new InvalidOperationException("YOLO模型未初始化，无法运行算法调试");
            }

            VisionDebugParameterService.ValidatePreprocessingMode(parameters.PreprocessingMode);
            InspectionRuleSet ruleSet = VisionDebugParameterService.ResolveRuleSet(_appConfig, parameters, out string ruleSetJson);
            InspectionFallbackGoal? fallbackGoal = InspectionRuleEngine.GetFallbackGoal(ruleSet);
            float confidence = VisionDebugParameterService.ResolveConfidence(_appConfig, parameters);
            float iouThreshold = VisionDebugParameterService.ResolveIou(_appConfig, parameters);
            float[]? productionRoiSnapshot = SnapshotCurrentROI();
            float[]? roiSnapshot = parameters.RoiEnabled ? productionRoiSnapshot : null;
            MultiModelCandidateEvaluator candidateEvaluator = _appRuntime.DecisionEvaluator.CreateCandidateEvaluator(
                ruleSet,
                image.Width,
                image.Height,
                roiSnapshot);

            Stopwatch sw = Stopwatch.StartNew();
            DetectionResultData result;
            using (await DetectionRuntimeConcurrencyGate.EnterAsync().ConfigureAwait(false))
            {
                if (_detectionService is DetectionService concrete)
                {
                    result = await concrete.DetectAsync(
                        image,
                        confidence,
                        iouThreshold,
                        fallbackGoal,
                        candidateEvaluator,
                        parameters.PreprocessingMode).ConfigureAwait(false);
                }
                else
                {
                    result = await _detectionService.DetectAsync(
                        image,
                        confidence,
                        iouThreshold,
                        fallbackGoal,
                        candidateEvaluator).ConfigureAwait(false);
                }
            }

            sw.Stop();
            ApplyRuleTraceSnapshot(result, ruleSetJson, fallbackGoal);
            string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
            string usedModelName = string.IsNullOrWhiteSpace(result.UsedModelName)
                ? _detectionService.CurrentModelName
                : result.UsedModelName;
            VisionDebugSnapshot snapshot = _appRuntime.DecisionEvaluator.EvaluateWithDebug(new InspectionDecisionRequest
            {
                RuleSet = ruleSet,
                Detections = result.Results ?? new List<YoloResult>(),
                Labels = labels,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                Roi = roiSnapshot,
                ModelName = usedModelName,
                PreprocessingMode = parameters.PreprocessingMode,
                Confidence = confidence,
                IouThreshold = iouThreshold,
                ElapsedMs = sw.ElapsedMilliseconds
            });
            snapshot.ParameterComparison = VisionDebugParameterService.BuildParameterComparison(
                _appConfig,
                parameters,
                ruleSetJson,
                productionRoiSnapshot != null);

            if (result.HasError)
            {
                snapshot.Succeeded = false;
                snapshot.ErrorCode = "DetectionServiceError";
                snapshot.Message = result.ErrorMessage;
                snapshot.FinalOk = false;
                snapshot.PrimaryFailureReason = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "检测服务失败，按 NG 处理"
                    : result.ErrorMessage;
            }

            snapshot.Comparison = comparison;
            if (snapshot.Comparison != null)
            {
                snapshot.Comparison.NewIsQualified = snapshot.FinalOk;
                snapshot.Comparison.NewPrimaryReason = snapshot.PrimaryFailureReason;
            }

            if (writeFrontendLog)
            {
                await _uiController.LogToFrontend(
                    $"{sourceLabel}: {(snapshot.FinalOk ? "OK" : "NG")} | {snapshot.JudgeResult.Summary}",
                    snapshot.FinalOk ? "info" : "warning").ConfigureAwait(false);
            }

            return snapshot;
        }

        private async Task PublishVisionDebugSnapshotAsync(Mat image, VisionDebugSnapshot snapshot, string? requestId)
        {
            await _uiController.UpdateImage(image, targetWidth: 960, targetHeight: 540, jpegQuality: 70).ConfigureAwait(false);
            await _uiController.SendVisionDebugResult(new
            {
                status = "completed",
                succeeded = snapshot.Succeeded,
                snapshot,
                message = snapshot.FinalOk
                    ? "算法调试完成: OK"
                    : $"算法调试完成: NG - {snapshot.PrimaryFailureReason}"
            }, requestId).ConfigureAwait(false);
        }

        private Mat? TryCloneCurrentVisionDebugFrame()
        {
            try
            {
                Mat? lastFrame = _cameraService.LastFrame;
                if (lastFrame != null && !lastFrame.Empty())
                {
                    return lastFrame.Clone();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VisionDebug] 获取当前帧失败: {ex.Message}");
            }

            return null;
        }

        private VisionDebugHistoryImageResolution ResolveDebugHistoryImagePath(DetectionRecord record) =>
            VisionDebugHistoryImageResolver.Resolve(record, TryResolveHistoryImagePath);

        private static string ResolveRecordPrimaryReason(DetectionRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.RuleSummary))
            {
                return record.RuleSummary;
            }

            if (!string.IsNullOrWhiteSpace(record.ErrorMessage))
            {
                return record.ErrorMessage;
            }

            return record.IsQualified ? "历史判定 OK" : "历史判定 NG";
        }

        private Task SendVisionDebugErrorAsync(string? requestId, string errorCode, string message)
        {
            return _uiController.SendVisionDebugResult(new
            {
                status = "failed",
                succeeded = false,
                errorCode,
                message
            }, requestId);
        }

        private IEnumerable<string> GetHistoryImageBasePaths()
        {
            var paths = new[]
            {
                _storageService.ImageBasePath,
                Path_Images,
                BaseStoragePath,
                Directory.GetParent(_storageService.ImageBasePath)?.FullName
            };

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase);
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
            if (IsRuntimeMutationBlocked("模型切换"))
            {
                SafeFireAndForget(SyncModelSelectionStateAsync(), "同步模型选择状态");
                return;
            }

            模型名 = modelName;
            SafeFireAndForget(ChangeModelAsync(modelName), "切换模型");
        }

        private async Task ChangeModelAsync(string modelName)
        {
            try
            {
                await _uiController.LogToFrontend($"正在切换模型: {modelName}", "info");

                ProductionModelActivationResult activation = await _modelActivationService.ActivatePrimaryAsync(
                    modelName,
                    "主模型切换",
                    _appConfig.EnableGpu,
                    _appConfig.GpuIndex).ConfigureAwait(false);
                if (activation.Succeeded)
                {
                    模型名 = _appConfig.CurrentModelFileName ?? string.Empty;
                    await _uiController.LogToFrontend(BuildModelLoadStatusMessage($"模型切换成功: {模型名}"), "success");
                    await _uiController.SendModelLabels(_detectionService.GetLabels());
                }
                else
                {
                    模型名 = _appConfig.CurrentModelFileName ?? string.Empty;
                    await _uiController.LogToFrontend(
                        $"{OperatorFaultMessages.ForActivationFailure(activation.ErrorCode, activation.Message)}{FormatCompensationFailures(activation)}",
                        "error");
                }
            }
            catch (Exception ex)
            {
                模型名 = _appConfig.CurrentModelFileName ?? string.Empty;
                await _uiController.LogToFrontend($"模型切换异常: {ex.Message}", "error");
            }
            finally
            {
                await SyncModelSelectionStateAsync();
            }
        }

        private static string FormatCompensationFailures(ProductionModelActivationResult result)
        {
            if (result.CompensationFailures.Count == 0)
            {
                return string.Empty;
            }

            return $"；补偿失败: {string.Join("; ", result.CompensationFailures)}";
        }

        /// <summary>
        /// 手动检测逻辑 (PLC触发或手动按钮)
        /// </summary>
        private string GetDetailedDetectionLog(
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
            await RunAcceptedDetectionCycleAsync(triggerSource, triggerSeq, triggerTime);
        }

        private async Task RunAcceptedDetectionCycleAsync(
            string triggerSource,
            int? triggerSeq,
            DateTimeOffset triggerTime,
            bool plcTriggerAccepted = false)
        {
            string inspectionId = InspectionIdGenerator.Next(triggerSource, triggerTime);
            var context = new InspectionContext
            {
                InspectionId = inspectionId,
                TriggerTime = triggerTime,
                TriggerSource = triggerSource,
                TriggerSeq = triggerSeq,
                PlcTriggerAccepted = plcTriggerAccepted,
                CurrentStage = InspectionStage.Triggered,
                TraceStatus = TraceStatus.Unknown
            };

            DiagLog($"▶ [{triggerSource}] [{inspectionId}] btnCapture_LogicAsync 进入, 线程ID={Thread.CurrentThread.ManagedThreadId}");
            InspectionPipelineRequest request = new InspectionPipelineRequest(triggerSource, inspectionId, triggerSeq, context);
            var totalSw = Stopwatch.StartNew();
            bool finalQualified = false;
            int finalResultCount = 0;
            int finalAttemptCount = 1;
            InspectionPipelineResult? pipelineResult = null;
            bool barcodeEnabled = _appConfig.TriggerSource == TriggerSource.PLC && _appConfig.BarcodeEnabled;

            try
            {
                // 真实检测流程交给 InspectionPipelineService，窗口层只负责 UI 展示、健康状态和追溯日志。
                await _uiController.SendInspectionUpdate(
                    context,
                    message: "检测已触发",
                    usedModelName: _detectionService.CurrentModelName,
                    barcodeEnabled: barcodeEnabled);

                pipelineResult = await _inspectionPipelineService.ExecuteAsync(
                    request,
                    _appShutdownCts.Token,
                    OnInspectionPipelineProgressAsync);
                await PresentInspectionPipelineResultAsync(pipelineResult);

                finalQualified = pipelineResult.FinalQualified;
                finalResultCount = pipelineResult.FinalResultCount;
                finalAttemptCount = pipelineResult.AttemptCount;
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
                    barcodeEnabled);
            }
            finally
            {
                totalSw.Stop();
                context.TotalMs = totalSw.ElapsedMilliseconds;
                if (pipelineResult != null)
                {
                    _healthMonitor.RecordInspection(pipelineResult);
                }
                else
                {
                    _healthMonitor.RecordInspection(context);
                }

                await SendHealthSnapshotToFrontendAsync();

                long captureMs = pipelineResult?.Timings.CaptureMs ?? 0;
                long inferenceMs = pipelineResult?.Timings.InferenceMs ?? 0;
                long roiFilterMs = pipelineResult?.Timings.RoiFilterMs ?? 0;
                long plcWriteMs = pipelineResult?.Timings.PlcWriteMs ?? 0;
                long renderToUiMs = pipelineResult?.Timings.RenderToUiMs ?? 0;
                long saveQueueMs = pipelineResult?.Timings.SaveQueueMs ?? 0;
                long dbWriteMs = pipelineResult?.Timings.DbWriteMs ?? 0;
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
                        _detectionGate.GetSnapshot(),
                        pipelineResult?.JudgeResult);
                }

                pipelineResult?.Dispose();
                _detectionGate.Release();
                DiagLog($"✅ [{triggerSource}] [{inspectionId}] btnCapture_LogicAsync 完成, 信号量已释放");
            }
        }

        private async Task ManualDetectAsync()
        {
            await _uiController.LogToFrontend("手动检测已触发", "info");

            await btnCapture_LogicAsync("手动");
        }

        private async Task OnInspectionPipelineProgressAsync(InspectionPipelineProgress progress)
        {
            if (progress.Kind == InspectionPipelineProgressKind.Log)
            {
                await _uiController.LogToFrontend(progress.Message, progress.Level);
                return;
            }

            await _uiController.SendInspectionUpdate(
                progress.Context,
                progress.IsOk,
                progress.Message,
                progress.ActualCount,
                progress.UsedModelName,
                progress.WasFallback,
                progress.BarcodeEnabled,
                progress.ProductBarcode,
                progress.BarcodeReadSucceeded,
                progress.BarcodeError);
        }

        private async Task PresentInspectionPipelineResultAsync(InspectionPipelineResult result)
        {
            InspectionContext context = result.Context;
            if (result.HasFrame)
            {
                context.CurrentStage = InspectionStage.RenderToUi;
                var renderSw = Stopwatch.StartNew();
                await _uiController.SendDetectionFrame(
                    result.RenderedFrame ?? result.Frame!,
                    result.FinalQualified,
                    _statisticsService.Current,
                    result.StatusMessage,
                    result.StatusLevel,
                    result.DetectionMetrics ?? _detectionService.GetLastMetrics(),
                    context,
                    result.FinalResultCount,
                    result.UsedModelName,
                    result.WasFallback,
                    context.TotalMs,
                    null,
                    result.BarcodeEnabled,
                    result.ProductBarcode,
                    result.BarcodeReadSucceeded,
                    result.BarcodeError,
                    ruleSummary: result.JudgeResult?.Summary,
                    rulePrimaryReason: GetRulePrimaryReason(result.JudgeResult),
                    ruleDetails: result.JudgeResult?.Details);
                renderSw.Stop();
                result.Timings.RenderToUiMs = renderSw.ElapsedMilliseconds;
                context.RenderToUiMs = result.Timings.RenderToUiMs;
                context.TotalMs += result.Timings.RenderToUiMs;
                context.CurrentStage = InspectionStage.Completed;
                string? terminalFailureMessage =
                    context.TerminalHandshakeAttempted && !context.TerminalHandshakeSucceeded
                        ? result.StatusMessage
                        : null;

                await _uiController.SendInspectionUpdate(
                    context,
                    result.FinalQualified,
                    terminalFailureMessage,
                    result.FinalResultCount,
                    result.UsedModelName,
                    result.WasFallback,
                    result.BarcodeEnabled,
                    result.ProductBarcode,
                    result.BarcodeReadSucceeded,
                    result.BarcodeError,
                    ruleSummary: result.JudgeResult?.Summary,
                    rulePrimaryReason: GetRulePrimaryReason(result.JudgeResult),
                    ruleDetails: result.JudgeResult?.Details);
                return;
            }

            await _uiController.SendInspectionUpdate(
                context,
                result.FinalQualified,
                string.IsNullOrWhiteSpace(result.StatusMessage) ? context.ErrorMessage : result.StatusMessage,
                result.FinalResultCount,
                result.UsedModelName,
                result.WasFallback,
                result.BarcodeEnabled,
                result.ProductBarcode,
                result.BarcodeReadSucceeded,
                result.BarcodeError,
                ruleSummary: result.JudgeResult?.Summary,
                rulePrimaryReason: GetRulePrimaryReason(result.JudgeResult),
                ruleDetails: result.JudgeResult?.Details);
        }

        private async Task<DetectionTriggerDecision> TryStartDetectionCycleAsync(string triggerSource, string? inspectionId)
        {
            // 检测信号量同时承担忙碌保护、防抖和退出保护，所有触发源必须先经过这里。
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
                // PLC 等自动触发在启动链路中已做硬件状态控制，这里只拦截手动误触发。
                return true;
            }

            if (IsCameraReadyForInspection(out string message))
            {
                return true;
            }

            await _uiController.LogToFrontend($"手动拍照: 相机未就绪，正在尝试自动恢复: {message}", "warning");
            var readyAfterResume = await WaitForCameraReadyForInspectionAsync(timeoutMs: 1200);
            if (readyAfterResume.Ready)
            {
                await _uiController.UpdateConnection("cam", true);
                await _uiController.LogToFrontend("手动拍照: 相机采集已恢复", "info");
                return true;
            }

            bool cameraReopened = await btnOpenCamera_LogicAsync(startTriggerSource: false);
            if (cameraReopened)
            {
                var readyAfterReconnect = await WaitForCameraReadyForInspectionAsync(timeoutMs: 1200);
                if (readyAfterReconnect.Ready)
                {
                    await _uiController.UpdateConnection("cam", true);
                    await _uiController.LogToFrontend("手动拍照: 相机已自动重连", "info");
                    return true;
                }

                message = readyAfterReconnect.Message;
            }
            else if (!string.IsNullOrWhiteSpace(_cameraService.LastError))
            {
                message = _cameraService.LastError!;
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

        private const int CameraReadyWaitTimeoutMs = 3000;
        private const int CameraReadyPollIntervalMs = 100;

        private async Task<(bool Ready, string Message)> WaitForCameraReadyForInspectionAsync(
            int timeoutMs = CameraReadyWaitTimeoutMs,
            int pollIntervalMs = CameraReadyPollIntervalMs)
        {
            timeoutMs = Math.Max(0, timeoutMs);
            pollIntervalMs = Math.Clamp(pollIntervalMs, 50, 500);

            var sw = Stopwatch.StartNew();
            string lastMessage = string.Empty;

            while (true)
            {
                if (IsCameraReadyForInspection(out lastMessage))
                {
                    return (true, string.Empty);
                }

                if (!_isCameraOpening)
                {
                    TryResumeCameraCaptureIfOpen();
                }

                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    break;
                }

                int remainingMs = Math.Max(0, timeoutMs - (int)sw.ElapsedMilliseconds);
                await Task.Delay(Math.Min(pollIntervalMs, Math.Max(1, remainingMs)));
            }

            return (false, string.IsNullOrWhiteSpace(lastMessage) ? "相机未就绪" : lastMessage);
        }

        private void TryResumeCameraCaptureIfOpen()
        {
            try
            {
                if (_cameraService.IsOpen && !_cameraService.IsGrabbing)
                {
                    _cameraService.StartCapture();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraReady] 恢复采集失败: {ex.Message}");
            }
        }

        private bool IsCameraReadyForInspection(out string message)
        {
            if (_isCameraOpening)
            {
                bool isAlreadyReady = false;
                try
                {
                    isAlreadyReady = _cameraService.IsOpen && _cameraService.IsGrabbing;
                }
                catch
                {
                    isAlreadyReady = false;
                }

                if (!isAlreadyReady)
                {
                    message = "相机正在连接中，请稍候";
                    return false;
                }
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
                // ImageSavePayload 持有 Mat 的只读视图，真正编码写盘由后台队列完成。
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

        private static string AddFileNameSuffix(string fileName, string suffix)
        {
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            return $"{nameWithoutExtension}{suffix}{extension}";
        }

        private static string BuildTraceImageFileName(bool isQualified, string inspectionId, string? productBarcode)
        {
            // 文件名包含检测结论、追溯 ID 和可选条码，方便脱离数据库时人工定位图片。
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

        private Task<string> SaveDetectionImage(Mat image, bool isQualified, Mat? renderedImage = null)
        {
            List<ImageSavePayload>? payloads = CreateImageSavePayloads(image, isQualified, renderedImage);
            if (payloads == null || payloads.Count == 0)
            {
                return Task.FromResult(string.Empty);
            }

            string originalPath = payloads[0].Path;
            foreach (ImageSavePayload payload in payloads)
            {
                bool enqueued = _imageSaveQueue.Enqueue(payload);
                if (!enqueued)
                {
                    // 入队失败时当前线程仍拥有 payload，必须立即释放 Mat 视图。
                    payload.Dispose();
                }
            }

            return Task.FromResult(originalPath);
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
            DetectionDropSnapshot dropSnapshot,
            InspectionJudgeResult? judgeResult)
        {
            try
            {
                // 性能日志按阶段写入，便于区分相机、推理、UI、PLC 和数据库瓶颈。
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
                sb.AppendLine($"模型尝试: {Math.Max(0, context.FallbackAttemptCount)}");
                if (!string.IsNullOrWhiteSpace(context.FallbackSkippedReason))
                {
                    sb.AppendLine($"回退状态: {context.FallbackSkippedReason}");
                }
                sb.AppendLine($"目标数量: {resultCount}");
                if (judgeResult != null)
                {
                    sb.AppendLine($"判定规则: {(judgeResult.IsQualified ? "OK" : "NG")} {judgeResult.Summary}");
                    if (!judgeResult.IsQualified)
                    {
                        sb.AppendLine($"NG原因: {GetRulePrimaryReason(judgeResult)}");
                    }

                    if (judgeResult.Details.Count > 0)
                    {
                        sb.AppendLine($"规则明细: {string.Join("；", judgeResult.Details)}");
                    }
                }
                sb.AppendLine($"队列: image={context.ImageQueuePending}, record={context.RecordQueuePending}");
                sb.AppendLine($"丢弃累计: busy={dropSnapshot.BusyCount}, debounce={dropSnapshot.DebounceCount}, shutdown={dropSnapshot.ShutdownCount}");
                sb.AppendLine("阶段耗时:");
                if (context.HandshakeStartMs > 0 || context.HandshakeCompleteMs > 0)
                {
                    sb.AppendLine($"- 握手启动: {context.HandshakeStartMs}ms");
                }
                sb.AppendLine($"- 取图: {captureMs}ms");
                sb.AppendLine($"- 推理: {inferenceMs}ms");
                sb.AppendLine($"- ROI过滤: {roiFilterMs}ms");
                sb.AppendLine($"- 前端渲染: {renderToUiMs}ms");
                sb.AppendLine($"- 图像入队: {saveQueueMs}ms");
                sb.AppendLine($"- PLC结果写入: {(context.PlcResultWriteMs > 0 ? context.PlcResultWriteMs : plcWriteMs)}ms");
                if (context.HandshakeStartMs > 0 || context.HandshakeCompleteMs > 0)
                {
                    sb.AppendLine($"- 握手完成: {context.HandshakeCompleteMs}ms");
                }
                sb.AppendLine($"- 数据库写入: {dbWriteMs}ms");

                _storageService.WriteDetectionLog(sb.ToString(), isQualified);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[主窗口-性能日志] 写入失败: {ex.Message}");
            }
        }

        private void btnSettings_Logic()
        {
            // 打开设置对话框
            SafeFireAndForget(_uiController.SendProjectPresets(ProjectPresetStore.Load()), "加载项目预设");
            SafeFireAndForget(_uiController.SendCurrentConfig(_appConfig), "打开设置");
        }

        #endregion

        #region ROI 与配方辅助方法

        private float[]? SnapshotCurrentROI()
        {
            return _currentROI == null || _currentROI.Length != 4
                ? null
                : Recipe.NormalizeRoi(_currentROI);
        }

        private Recipe SaveCurrentRecipeSnapshot(string changeSummary = "生产配置保存")
        {
            return _recipeManager.SaveNewVersion(
                _appConfig,
                SnapshotCurrentROI(),
                ResolveCurrentOperatorId(),
                _appConfig.CurrentOperatorRole.ToString(),
                changeSummary);
        }

        private void TrySaveCurrentRecipeSnapshot(string operation)
        {
            try
            {
                SaveCurrentRecipeSnapshot(operation);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Recipe] {operation} 保存配方快照失败: {ex.Message}");
                SafeFireAndForget(
                    _uiController.LogToFrontend($"{operation} 已保存，但配方快照更新失败: {ex.Message}", "error"),
                    "配方快照保存失败");
            }
        }

        #endregion
    }
}
