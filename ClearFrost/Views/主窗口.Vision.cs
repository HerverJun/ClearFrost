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

                    // 应用 ROI 过滤
                    results = FilterResultsByROI(results, originalBitmap.Width, originalBitmap.Height);

                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    isQualified = EvaluateQualificationByTarget(results, labels, _appConfig.TargetLabel, _appConfig.TargetCount);
                    using (var sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(originalBitmap))
                    using (var renderedMat = TryRenderDetectionMat(sourceMat, results, labels))
                    {
                        // 保存检测图像到追溯库（不合格时复用渲染结果）
                        await SaveDetectionImage(sourceMat, results, isQualified, result.UsedModelLabels, renderedMat);

                        _statisticsService.RecordDetection(isQualified);

                        string objDesc = GetDetailedDetectionLog(results, labels);
                        string modelInfo = result.WasFallback ? $" [切换至: {result.UsedModelName}]" : "";
                        await _uiController.SendDetectionFrame(
                            renderedMat ?? sourceMat,
                            isQualified,
                            _statisticsService.Current,
                            $"检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | {sw.ElapsedMilliseconds}ms{modelInfo}",
                            isQualified ? "success" : "error",
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

        private readonly record struct DetectionCycleRequest(string TriggerSource);

        private async Task btnCapture_LogicAsync(string triggerSource = "手动")
        {
            DiagLog($"▶ [{triggerSource}] btnCapture_LogicAsync 进入, 线程ID={Thread.CurrentThread.ManagedThreadId}");

            DetectionTriggerDecision decision = await TryStartDetectionCycleAsync(triggerSource);
            if (!decision.Accepted)
            {
                return;
            }

            DetectionCycleRequest request = new DetectionCycleRequest(triggerSource);
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

            try
            {
                (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount) =
                    await ExecuteDetectionCycleAsync(request, _appShutdownCts.Token);
            }
            catch (Exception ex)
            {
                DiagLog($"❌ [{request.TriggerSource}] 检测异常: {ex.Message}");
                await _uiController.LogToFrontend($"检测异常: {ex.Message}", "error");
            }
            finally
            {
                totalSw.Stop();
                if (captureMs > 0 || inferenceMs > 0 || roiFilterMs > 0 || plcWriteMs > 0 || renderToUiMs > 0 || saveQueueMs > 0 || dbWriteMs > 0)
                {
                    WritePerformanceProfileLog(
                        request.TriggerSource,
                        finalQualified,
                        totalSw.ElapsedMilliseconds,
                        captureMs,
                        inferenceMs,
                        roiFilterMs,
                        renderToUiMs,
                        saveQueueMs,
                        plcWriteMs,
                        dbWriteMs,
                        1,
                        finalResultCount,
                        _detectionGate.GetSnapshot());
                }

                _detectionGate.Release();
                DiagLog($"✅ [{triggerSource}] btnCapture_LogicAsync 完成, 信号量已释放");
            }
        }

        private async Task<DetectionTriggerDecision> TryStartDetectionCycleAsync(string triggerSource)
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
                    DiagLog($"⚠ [{triggerSource}] 软件正在退出，已忽略检测请求 | {summary}");
                    await _uiController.LogToFrontend("软件正在退出，已忽略检测请求", "warning");
                    break;
                case DetectionDropReason.Debounce:
                    DiagLog($"⚠ [{triggerSource}] 触发命中防抖窗口，已忽略 | {summary}");
                    await _uiController.LogToFrontend("检测触发过于频繁，已忽略本次请求", "warning");
                    break;
                default:
                    DiagLog($"⚠ [{triggerSource}] 信号量已被占用，跳过 | {summary}");
                    await _uiController.LogToFrontend("检测正在进行中，请稍候...", "warning");
                    break;
            }

            return decision;
        }

        private async Task<(long CaptureMs, long InferenceMs, long RoiFilterMs, long PlcWriteMs, long RenderToUiMs, long SaveQueueMs, long DbWriteMs, bool FinalQualified, int FinalResultCount)> ExecuteDetectionCycleAsync(
            DetectionCycleRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            await _uiController.LogToFrontend($"开始检测... ({request.TriggerSource}触发)", "info");

            long captureMs = 0;
            long inferenceMs = 0;
            long roiFilterMs = 0;
            long plcWriteMs = 0;
            long renderToUiMs = 0;
            long saveQueueMs = 0;
            long dbWriteMs = 0;
            bool finalQualified = false;
            int finalResultCount = 0;
            ImageSavePayload? imagePayload = null;
            DetectionPersistencePayload? persistencePayload = null;

            Mat? frameToProcess = null;

            var captureSw = Stopwatch.StartNew();
            try
            {
                frameToProcess = _cameraService.CaptureFrame(3000);
                DiagLog($"📷 [{request.TriggerSource}] CaptureFrame 结果: {(frameToProcess != null ? "OK" : "FAIL")}");
            }
            catch (Exception ex)
            {
                DiagLog($"❌ [{request.TriggerSource}] CaptureFrame 异常: {ex.Message}");
                Debug.WriteLine($"[手动检测] 触发拍照失败: {ex.Message}");
            }

            if (frameToProcess == null)
            {
                Mat? cachedFrame = _cameraService.LastFrame;
                if (cachedFrame != null && !cachedFrame.Empty())
                {
                    frameToProcess = cachedFrame;
                }
                else
                {
                    cachedFrame?.Dispose();
                }
            }

            captureSw.Stop();
            captureMs = captureSw.ElapsedMilliseconds;

            if (frameToProcess == null)
            {
                await _uiController.LogToFrontend("无可用图像进行检测，请先打开相机", "error");
                return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount);
            }

            using (frameToProcess)
            {
                var inferSw = Stopwatch.StartNew();
                DetectionResultData result = await _detectionService.DetectAsync(
                    frameToProcess,
                    _appConfig.Confidence,
                    _appConfig.IouThreshold,
                    _appConfig.TargetLabel,
                    _appConfig.TargetCount);
                inferSw.Stop();
                inferenceMs = inferSw.ElapsedMilliseconds;

                bool isQualified = result.IsQualified;
                List<YoloResult> results = result.Results ?? new List<YoloResult>();

                var roiSw = Stopwatch.StartNew();
                results = FilterResultsByROI(results, frameToProcess.Width, frameToProcess.Height);
                roiSw.Stop();
                roiFilterMs = roiSw.ElapsedMilliseconds;
                finalResultCount = results.Count;

                string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                isQualified = EvaluateQualificationByTarget(results, labels, _appConfig.TargetLabel, _appConfig.TargetCount);
                finalQualified = isQualified;

                var plcSw = Stopwatch.StartNew();
                await WriteDetectionResultToPlc(isQualified);
                plcSw.Stop();
                plcWriteMs = plcSw.ElapsedMilliseconds;

                using (Mat? renderedMat = TryRenderDetectionMat(frameToProcess, results, labels))
                {
                    _statisticsService.RecordDetection(isQualified);

                    var renderSw = Stopwatch.StartNew();
                    string objDesc = GetDetailedDetectionLog(results, labels);
                    string modelInfo = result.WasFallback ? $" [切换至: {result.UsedModelName}]" : "";
                    await _uiController.SendDetectionFrame(
                        renderedMat ?? frameToProcess,
                        isQualified,
                        _statisticsService.Current,
                        $"[{request.TriggerSource}] 检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | {inferenceMs}ms{modelInfo}",
                        isQualified ? "success" : "error",
                        (_detectionService as DetectionService)?.GetLastMetrics());
                    renderSw.Stop();
                    renderToUiMs = renderSw.ElapsedMilliseconds;

                    imagePayload = CreateImageSavePayload(
                        frameToProcess,
                        results,
                        isQualified,
                        result.UsedModelLabels,
                        renderedMat);
                    persistencePayload = BuildDetectionPersistencePayload(result, results, inferenceMs, finalResultCount, isQualified);
                }
            }

            var saveSw = Stopwatch.StartNew();
            if (imagePayload != null)
            {
                bool imageQueued = _imageSaveQueue.Enqueue(imagePayload);
                if (!imageQueued)
                {
                    imagePayload.Dispose();
                    Debug.WriteLine("[主窗口] 图像保存入队失败");
                }
            }
            saveSw.Stop();
            saveQueueMs = saveSw.ElapsedMilliseconds;

            var dbSw = Stopwatch.StartNew();
            if (persistencePayload != null)
            {
                bool dbQueued = _detectionRecordQueue.Enqueue(persistencePayload);
                if (!dbQueued)
                {
                    Debug.WriteLine("[主窗口] 检测记录入队失败");
                }
            }
            dbSw.Stop();
            dbWriteMs = dbSw.ElapsedMilliseconds;

            return (captureMs, inferenceMs, roiFilterMs, plcWriteMs, renderToUiMs, saveQueueMs, dbWriteMs, finalQualified, finalResultCount);
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
                var detector = (_detectionService as DetectionService)?.PrimaryDetector;
                if (detector != null)
                {
                    var matResult = detector.GenerateImageMat(sourceImage, results, labels);
                    if (matResult != null) return matResult;
                }
            }

            // 回退：Bitmap 路径（美观模式或不支持的任务类型）
            using var bitmap = sourceImage.ToBitmap();
            using var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels);
            return OpenCvSharp.Extensions.BitmapConverter.ToMat(resultImage);
        }

        private ImageSavePayload? CreateImageSavePayload(Mat image, List<YoloResult> results, bool isQualified, string[]? usedLabels = null, Mat? renderedImage = null)
        {
            try
            {
                DateTime now = DateTime.Now;
                string subFolder = isQualified ? "Qualified" : "Unqualified";
                string dateFolder = now.ToString("yyyy年MM月dd日");
                string hourFolder = now.ToString("HH");
                string directory = Path.Combine(Path_Images, subFolder, dateFolder, hourFolder);

                Directory.CreateDirectory(directory);

                string fileName = $"{(isQualified ? "PASS" : "FAIL")}_{now:HHmmssfff}.jpg";
                string filePath = Path.Combine(directory, fileName);

                // 不合格图像优先复用调用方已渲染结果，避免二次 ToBitmap + 渲染。
                if (!isQualified && results.Count > 0)
                {
                    if (renderedImage != null && !renderedImage.Empty())
                    {
                        return ImageSavePayload.Create(renderedImage, filePath);
                    }

                    string[] labels = usedLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    using var bitmap = image.ToBitmap();
                    using var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels);
                    using var renderedMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(resultImage);
                    return ImageSavePayload.Create(renderedMat, filePath);
                }

                return ImageSavePayload.Create(image, filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存检测图像失败: {ex.Message}");
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

        private DetectionPersistencePayload BuildDetectionPersistencePayload(
            DetectionResultData result,
            List<YoloResult> results,
            long inferenceMs,
            int actualCount,
            bool isQualified)
        {
            return new DetectionPersistencePayload
            {
                Timestamp = DateTime.Now,
                IsQualified = isQualified,
                ModelName = result.UsedModelName ?? _detectionService.CurrentModelName,
                InferenceMs = (int)inferenceMs,
                TargetLabel = _appConfig.TargetLabel ?? string.Empty,
                ExpectedCount = _appConfig.TargetCount,
                ActualCount = actualCount,
                CameraId = _cameraManager.ActiveCameraId ?? string.Empty,
                ResultJson = SerializeDetectionResults(results)
            };
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
            string mode,
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
                sb.AppendLine($"模式: {mode}");
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

        private async Task WriteDetectionResultToPlc(bool isQualified)
        {
            if (_plcService.IsConnected)
            {
                try
                {
                    short resultAddress = (short)_appConfig.PlcResultAddress;
                    short writeValue = isQualified ? _appConfig.PlcOkValue : _appConfig.PlcNgValue;
                    bool success = await _plcService.WriteResultAsync(resultAddress, writeValue);
                    if (!success)
                    {
                        await _uiController.LogToFrontend("PLC写入失败: 结果未成功落地", "error");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"PLC写入失败: {ex.Message}", "error");
                }
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
