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

            if (!Directory.Exists(模型路径))
            {
                await _uiController.LogToFrontend($"模型目录不存在: {模型路径}", "warning");
                return;
            }

            // 优先使用当前选择/配置的模型；文件不存在时自动回退到目录中的第一个模型。
            模型名 = ResolvePreferredModelFileName() ?? string.Empty;

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
                            if (_appConfig.Save())
                            {
                                TrySaveCurrentRecipeSnapshot("主模型初始化");
                            }
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

        private string? ResolvePreferredModelFileName()
        {
            if (!Directory.Exists(模型路径))
            {
                return null;
            }

            foreach (string? candidate in new[] { 模型名, _appConfig.CurrentModelFileName })
            {
                string modelFileName = NormalizeModelFileName(candidate);
                if (!string.IsNullOrWhiteSpace(modelFileName) &&
                    File.Exists(Path.Combine(模型路径, modelFileName)))
                {
                    return modelFileName;
                }
            }

            string[] files = Directory.GetFiles(模型路径, "*.onnx");
            if (files.Length == 0)
            {
                return null;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return Path.GetFileName(files[0]);
        }

        private static string NormalizeModelFileName(string? modelName)
        {
            string name = Path.GetFileName(modelName?.Trim() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            return name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}.onnx";
        }

        private async Task RestoreMultiModelConfigAsync()
        {
            // 主模型加载成功后再恢复辅助模型，确保多模型管理器已经具备基础推理上下文。
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
                    MultiModelCandidateEvaluator candidateEvaluator = CreateRuleCandidateEvaluator(
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

                    // 应用 ROI 过滤
                    results = FilterResultsByROI(results, originalBitmap.Width, originalBitmap.Height, roiSnapshot);

                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    if (detectionFailed)
                    {
                        isQualified = false;
                        await _uiController.LogToFrontend($"测试推理失败，已强制判定为不合格: {result.ErrorMessage}", "error");
                    }
                    else
                    {
                        InspectionJudgeResult judgeResult = InspectionRuleEngine.Evaluate(ruleSet, results, labels);
                        result.JudgeResult = judgeResult;
                        result.IsRuleEvaluated = true;
                        result.IsQualified = judgeResult.IsQualified;
                        isQualified = judgeResult.IsQualified;
                        await _uiController.LogToFrontend(
                            $"测试推理规则判定: {(judgeResult.IsQualified ? "OK" : "NG")} | {judgeResult.Summary}",
                            judgeResult.IsQualified ? "info" : "warning");
                    }
                    using (var sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(originalBitmap))
                    using (var renderedMat = TryRenderDetectionMat(sourceMat, results, labels))
                    {
                        string objDesc = GetDetailedDetectionLog(results, labels);
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
                MultiModelCandidateEvaluator candidateEvaluator = CreateRuleCandidateEvaluator(
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
                results = FilterResultsByROI(results, sourceMat.Width, sourceMat.Height, roiSnapshot);
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
                    judgeResult = InspectionRuleEngine.Evaluate(ruleSet, results, labels);
                    result.JudgeResult = judgeResult;
                    result.IsRuleEvaluated = true;
                    result.IsQualified = judgeResult.IsQualified;
                    isQualified = judgeResult.IsQualified;
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
                return File.Exists(fullPath) ? fullPath : null;
            }

            foreach (string basePath in GetHistoryImageBasePaths())
            {
                string fullPath = Path.GetFullPath(Path.Combine(basePath, trimmed));
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            return null;
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
                    if (_appConfig.Save())
                    {
                        TrySaveCurrentRecipeSnapshot("主模型切换");
                    }
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

        private MultiModelCandidateEvaluator CreateRuleCandidateEvaluator(
            InspectionRuleSet ruleSet,
            int imageWidth,
            int imageHeight,
            float[]? roiSnapshot)
        {
            return candidate =>
            {
                var rawResults = candidate.Results?.ToList() ?? new List<YoloResult>();
                // 多模型候选必须先过同一份 ROI，再交给规则引擎；否则辅助模型选择会和最终展示不一致。
                List<YoloResult> filteredResults = FilterResultsByROI(rawResults, imageWidth, imageHeight, roiSnapshot);
                InspectionJudgeResult judgeResult = InspectionRuleEngine.Evaluate(ruleSet, filteredResults, candidate.Labels);

                return new MultiModelCandidateEvaluation
                {
                    IsMatch = judgeResult.IsQualified,
                    Score = ScoreRuleCandidate(judgeResult, filteredResults.Count),
                    Summary = judgeResult.Summary
                };
            };
        }

        private static int ScoreRuleCandidate(InspectionJudgeResult judgeResult, int filteredCount)
        {
            // 未完全命中时也给候选打分，回退链路可返回最接近规则的结果供追溯。
            int matchedRules = judgeResult.RuleResults.Count(result => result.IsMatch);
            int failedRules = judgeResult.RuleResults.Count - matchedRules;
            int score = matchedRules * 1000 - failedRules * 100 + Math.Min(filteredCount, 100);
            return judgeResult.IsQualified ? score + 1_000_000 : score;
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
                _healthMonitor.RecordInspection(context);

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
                context.CurrentStage = string.IsNullOrWhiteSpace(context.ErrorCode)
                    ? InspectionStage.Completed
                    : InspectionStage.Failed;

                await _uiController.SendInspectionUpdate(
                    context,
                    result.FinalQualified,
                    null,
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

            if (!string.IsNullOrWhiteSpace(context.ErrorCode))
            {
                context.CurrentStage = InspectionStage.Failed;
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
                    // 入队失败时当前线程仍拥有 payload，必须立即释放 Mat 视图。
                    payload.Dispose();
                }
            }

            return originalPath;
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

        #region ROI 过滤辅助方法

        /// <summary>
        /// 根据 ROI 区域过滤检测结果（仅保留中心点在 ROI 内的检测框）
        /// </summary>
        private List<YoloResult> FilterResultsByROI(List<YoloResult> results, int imageWidth, int imageHeight)
        {
            return FilterResultsByROI(results, imageWidth, imageHeight, _currentROI);
        }

        private float[]? SnapshotCurrentROI()
        {
            return _currentROI == null || _currentROI.Length != 4
                ? null
                : Recipe.NormalizeRoi(_currentROI);
        }

        private Recipe SaveCurrentRecipeSnapshot()
        {
            Recipe recipe = _recipeManager.GenerateDefault(_appConfig, SnapshotCurrentROI());
            _recipeManager.Save(recipe);
            return recipe;
        }

        private void TrySaveCurrentRecipeSnapshot(string operation)
        {
            try
            {
                SaveCurrentRecipeSnapshot();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Recipe] {operation} 保存配方快照失败: {ex.Message}");
                SafeFireAndForget(
                    _uiController.LogToFrontend($"{operation} 已保存，但配方快照更新失败: {ex.Message}", "error"),
                    "配方快照保存失败");
            }
        }

        private static List<YoloResult> FilterResultsByROI(
            List<YoloResult> results,
            int imageWidth,
            int imageHeight,
            float[]? roi)
        {
            if (roi == null || roi.Length != 4 || roi[2] <= 0.001f || roi[3] <= 0.001f)
                return results; // 无 ROI 设置或 ROI 为空（宽度或高度约为0），返回全部结果

            // 将归一化 ROI 转换为像素坐标
            float roiX = roi[0] * imageWidth;
            float roiY = roi[1] * imageHeight;
            float roiW = roi[2] * imageWidth;
            float roiH = roi[3] * imageHeight;

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
