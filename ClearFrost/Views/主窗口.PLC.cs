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
using ClearFrost.Vision;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    public partial class 主窗口
    {
        #region 3. PLC控制逻辑 (PLC Control) - 委托给 PlcService

        /// <summary>
        /// 通过服务层连接 PLC
        /// </summary>
        private async Task ConnectPlcViaServiceAsync()
        {
            string protocol = _appConfig.PlcProtocol;
            string ip = _appConfig.PlcIp;
            int port = _appConfig.PlcPort;

            await _uiController.LogToFrontend($"正在连接 PLC: {protocol} @ {ip}:{port}", "info");

            bool success = await _plcService.ConnectAsync(protocol, ip, port);

            if (success)
            {
                // 启动触发监控
                _plcService.StartMonitoring(
                    _appConfig.PlcTriggerAddress,
                    _appConfig.PlcPollingIntervalMs,
                    _appConfig.PlcTriggerDelayMs);
            }
        }

        /// <summary>
        /// PLC 触发信号处理
        /// </summary>
        private async Task HandlePlcTriggerAsync()
        {
            var sw = Stopwatch.StartNew();
            Debug.WriteLine($"[主窗口-PLC] ▶ HandlePlcTriggerAsync 开始 - {DateTime.Now:HH:mm:ss.fff}");

            // 使用信号量防止并发检测
            if (!await _detectionSemaphore.WaitAsync(0))
            {
                Debug.WriteLine("[主窗口-PLC] ⚠ 信号量获取失败，检测正在进行中");
                await _uiController.LogToFrontend("检测进行中，跳过本次触发", "warning");
                return;
            }

            Debug.WriteLine("[主窗口-PLC] ✅ 信号量获取成功");

            Mat? lastFrameForSave = null; // 保留最后一帧用于保存
            Mat? lastRenderedForSave = null; // 保留最后一次渲染结果，供保存复用
            long lastInferenceMs = 0;     // 最后一次检测的推理时间
            long captureMs = 0;
            long inferenceMs = 0;
            long roiFilterMs = 0;
            long renderToUiMs = 0;
            long saveQueueMs = 0;
            long plcWriteMs = 0;
            long dbWriteMs = 0;
            int attemptsUsed = 0;
            bool finalQualified = false;
            int finalResultCount = 0;

            try
            {
                int maxRetries = _appConfig.MaxRetryCount;
                int retryInterval = _appConfig.RetryIntervalMs;
                DetectionResultData? lastResult = null;

                Debug.WriteLine($"[主窗口-PLC] 📋 配置: 最大重试={maxRetries}, 重试间隔={retryInterval}ms");

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    attemptsUsed = attempt + 1;

                    if (attempt > 0)
                    {
                        Debug.WriteLine($"[主窗口-PLC] 🔄 重试 {attempt}/{maxRetries}");
                        await _uiController.LogToFrontend($"触发重拍 ({attempt}/{maxRetries})", "warning");
                        await Task.Delay(retryInterval);
                    }

                    // 获取当前帧进行检测
                    Mat? frameToProcess = null;
                    var captureSw = Stopwatch.StartNew();

                    // 首先尝试触发相机拍照并获取实时图像
                    try
                    {
                        int res = cam.IMV_ExecuteCommandFeature("TriggerSoftware");
                        if (res == IMVDefine.IMV_OK)
                        {
                            IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
                            bool shouldReleaseFrame = false;
                            try
                            {
                                res = cam.IMV_GetFrame(ref frame, 2000); // 2秒超时
                                shouldReleaseFrame = res == IMVDefine.IMV_OK;
                                if (shouldReleaseFrame && frame.frameInfo.size > 0)
                                {
                                    frameToProcess = ConvertFrameToMat(frame);
                                    Debug.WriteLine($"[主窗口-PLC] 📷 主动拍照获取到图像帧: {frameToProcess.Width}x{frameToProcess.Height}");
                                }
                            }
                            finally
                            {
                                if (shouldReleaseFrame)
                                {
                                    cam.IMV_ReleaseFrame(ref frame);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[主窗口-PLC] ⚠ 触发拍照失败: {ex.Message}");
                    }

                    // 如果相机拍照失败，回退到缓存的最后一帧
                    if (frameToProcess == null)
                    {
                        lock (_frameLock)
                        {
                            if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                            {
                                frameToProcess = _lastCapturedFrame.Clone();
                                Debug.WriteLine($"[主窗口-PLC] 📷 使用缓存帧: {frameToProcess.Width}x{frameToProcess.Height}");
                            }
                        }
                    }
                    captureSw.Stop();
                    captureMs += captureSw.ElapsedMilliseconds;

                    if (frameToProcess == null)
                    {
                        Debug.WriteLine("[主窗口-PLC] ❌ 无可用图像帧!");
                        await _uiController.LogToFrontend("无可用图像进行检测", "error");
                        return;
                    }

                    Debug.WriteLine("[主窗口-PLC] 🔍 开始执行检测...");

                    // 释放之前保存的帧（如果存在）
                    lastFrameForSave?.Dispose();
                    lastFrameForSave = frameToProcess.Clone(); // 保留副本用于最后保存

                    var inferSw = Stopwatch.StartNew();

                    using (var mat = frameToProcess)
                    {
                        lastResult = await _detectionService.DetectAsync(mat, _appConfig.Confidence, overlapThreshold, _appConfig.TargetLabel, _appConfig.TargetCount);
                        inferSw.Stop();
                        lastInferenceMs = inferSw.ElapsedMilliseconds;
                        inferenceMs += lastInferenceMs;
                        Debug.WriteLine($"[主窗口-PLC] 🔍 检测完成 - 结果: {(lastResult?.IsQualified == true ? "合格" : "不合格")}");

                        // 无论检测结果如何，都将图像发送到前端显示
                        if (lastResult != null)
                        {
                            try
                            {
                                var results = lastResult.Results ?? new List<YoloResult>();
                                // 应用 ROI 过滤
                                var roiSw = Stopwatch.StartNew();
                                results = FilterResultsByROI(results, mat.Width, mat.Height);
                                roiSw.Stop();
                                roiFilterMs += roiSw.ElapsedMilliseconds;

                                // 生成带标注的结果图像并发送到前端
                                string[] labels = lastResult.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                                lastRenderedForSave?.Dispose();
                                lastRenderedForSave = TryRenderDetectionMat(mat, results, labels);

                                var renderSw = Stopwatch.StartNew();
                                if (lastRenderedForSave != null)
                                {
                                    await _uiController.UpdateImage(lastRenderedForSave);
                                }
                                else
                                {
                                    await _uiController.UpdateImage(mat);
                                }
                                renderSw.Stop();
                                renderToUiMs += renderSw.ElapsedMilliseconds;
                                Debug.WriteLine("[主窗口-PLC] 📷 结果图像已发送到前端");

                                // 更新 lastResult 中的 Results 为过滤后的结果
                                lastResult = new DetectionResultData
                                {
                                    IsQualified = lastResult.IsQualified,
                                    Results = results,
                                    UsedModelLabels = lastResult.UsedModelLabels,
                                    UsedModelName = lastResult.UsedModelName,
                                    WasFallback = lastResult.WasFallback
                                };
                                finalResultCount = results.Count;
                                finalQualified = lastResult.IsQualified;
                            }
                            catch (Exception imgEx)
                            {
                                Debug.WriteLine($"[主窗口-PLC] ⚠ 发送图像失败: {imgEx.Message}");
                            }
                        }
                    }

                    if (lastResult != null && lastResult.IsQualified)
                    {
                        Debug.WriteLine("[主窗口-PLC] ✅ 检测合格，退出重试循环");
                        break;
                    }
                }

                // 处理最终结果
                if (lastResult != null)
                {
                    bool isQualified = lastResult.IsQualified;
                    finalQualified = isQualified;
                    finalResultCount = lastResult.Results?.Count ?? 0;
                    Debug.WriteLine($"[主窗口-PLC] 📊 最终结果: {(isQualified ? "合格" : "不合格")}");

                    // 写入 PLC
                    Debug.WriteLine("[主窗口-PLC] 📝 写入PLC结果...");
                    var plcSw = Stopwatch.StartNew();
                    await WriteDetectionResult(isQualified);
                    plcSw.Stop();
                    plcWriteMs += plcSw.ElapsedMilliseconds;

                    // 保存检测图像（只保存最终结果）
                    if (lastFrameForSave != null && !lastFrameForSave.Empty())
                    {
                        try
                        {
                            var results = lastResult.Results ?? new List<YoloResult>();
                            var saveSw = Stopwatch.StartNew();
                            await SaveDetectionImage(lastFrameForSave, results, isQualified, lastResult.UsedModelLabels, lastRenderedForSave);
                            saveSw.Stop();
                            saveQueueMs += saveSw.ElapsedMilliseconds;
                            Debug.WriteLine("[主窗口-PLC] 💾 检测图像已保存");
                        }
                        catch (Exception saveEx)
                        {
                            Debug.WriteLine($"[主窗口-PLC] ⚠ 保存图像失败: {saveEx.Message}");
                        }
                    }

                    // 写入数据库记录
                    try
                    {
                        var dbSw = Stopwatch.StartNew();
                        await _databaseService.SaveDetectionRecordAsync(new DetectionRecord
                        {
                            Timestamp = DateTime.Now,
                            IsQualified = isQualified,
                            ModelName = _detectionService.CurrentModelName,
                            InferenceMs = (int)lastInferenceMs
                        });
                        dbSw.Stop();
                        dbWriteMs += dbSw.ElapsedMilliseconds;
                        Debug.WriteLine("[主窗口-PLC] 📀 数据库记录已保存");
                    }
                    catch (Exception dbEx)
                    {
                        Debug.WriteLine($"[主窗口-PLC] ⚠ 数据库写入失败: {dbEx.Message}");
                    }

                    // 更新统计
                    _statisticsService.RecordDetection(isQualified);
                    Debug.WriteLine("[主窗口-PLC] 📈 统计已更新");

                    // 日志（包含模型切换信息）
                    int detectedCount = lastResult.Results?.Count ?? 0;
                    // 使用 GetDetailedDetectionLog 生成详细日志
                    string[] logLabels = lastResult.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    string objDesc = GetDetailedDetectionLog(lastResult.Results ?? new List<YoloResult>(), logLabels);
                    string modelInfo = lastResult.WasFallback ? $" [切换至: {lastResult.UsedModelName}]" : "";
                    await _uiController.LogDetectionToFrontend($"PLC检测: {(isQualified ? "合格" : "不合格")} | {objDesc} | {lastInferenceMs}ms{modelInfo}", isQualified ? "success" : "error");

                    // 更新前端结果显示
                    await _uiController.UpdateResult(isQualified);
                }
                else
                {
                    Debug.WriteLine("[主窗口-PLC] ⚠ 检测结果为空!");
                }

                sw.Stop();
                Debug.WriteLine($"[主窗口-PLC] ⏱ HandlePlcTriggerAsync 完成 - 耗时: {sw.ElapsedMilliseconds}ms");
            }
            finally
            {
                sw.Stop();
                if (captureMs > 0 || inferenceMs > 0 || roiFilterMs > 0 || renderToUiMs > 0 || saveQueueMs > 0 || plcWriteMs > 0 || dbWriteMs > 0)
                {
                    WritePerformanceProfileLog(
                        "PLC",
                        finalQualified,
                        sw.ElapsedMilliseconds,
                        captureMs,
                        inferenceMs,
                        roiFilterMs,
                        renderToUiMs,
                        saveQueueMs,
                        plcWriteMs,
                        dbWriteMs,
                        attemptsUsed,
                        finalResultCount);
                }

                // 释放保存的帧
                lastFrameForSave?.Dispose();
                lastRenderedForSave?.Dispose();

                _detectionSemaphore.Release();
                Debug.WriteLine("[主窗口-PLC] 🔓 信号量已释放");
            }
        }

        /// <summary>
        /// 写入检测结果到 PLC
        /// </summary>
        public async Task WriteDetectionResult(bool isQualified)
        {
            if (!plcConnected) return;
            await _plcService.WriteResultAsync(_appConfig.PlcResultAddress, isQualified);
            await _uiController.LogToFrontend($"PLC写入结果: {(isQualified ? "合格" : "不合格")}", "info");
        }

        /// <summary>
        /// 手动放行
        /// </summary>
        private async Task fx_btn_LogicAsync()
        {
            try
            {
                bool success = await _plcService.WriteReleaseSignalAsync(_appConfig.PlcResultAddress);
                if (success)
                {
                    await _uiController.LogToFrontend("手动放行信号已发送", "success");
                }
                else
                {
                    await _uiController.LogToFrontend("放行失败: PLC未连接或写入错误", "error");
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"放行异常: {ex.Message}", "error");
            }
        }

        #endregion
    }
}
