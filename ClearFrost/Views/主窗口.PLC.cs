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
                _plcService.StartMonitoring(_appConfig.PlcTriggerAddress, 500);
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

            try
            {
                int maxRetries = _appConfig.MaxRetryCount;
                int retryInterval = _appConfig.RetryIntervalMs;
                DetectionResultData? lastResult = null;

                Debug.WriteLine($"[主窗口-PLC] 📋 配置: 最大重试={maxRetries}, 重试间隔={retryInterval}ms");

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    if (attempt > 0)
                    {
                        Debug.WriteLine($"[主窗口-PLC] 🔄 重试 {attempt}/{maxRetries}");
                        await _uiController.LogToFrontend($"触发重拍 ({attempt}/{maxRetries})", "warning");
                        await Task.Delay(retryInterval);
                    }

                    // 获取当前帧进行检测
                    Mat? frameToProcess = null;
                    lock (_frameLock)
                    {
                        if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                        {
                            frameToProcess = _lastCapturedFrame.Clone();
                            Debug.WriteLine($"[主窗口-PLC] 📷 获取到图像帧: {frameToProcess.Width}x{frameToProcess.Height}");
                        }
                    }

                    if (frameToProcess == null)
                    {
                        Debug.WriteLine("[主窗口-PLC] ❌ 无可用图像帧!");
                        await _uiController.LogToFrontend("无可用图像进行检测", "error");
                        return;
                    }

                    Debug.WriteLine("[主窗口-PLC] 🔍 开始执行检测...");
                    using (var mat = frameToProcess)
                    {
                        lastResult = await _detectionService.DetectAsync(mat, _appConfig.Confidence, overlapThreshold);
                    }
                    Debug.WriteLine($"[主窗口-PLC] 🔍 检测完成 - 结果: {(lastResult?.IsQualified == true ? "合格" : "不合格")}");

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
                    Debug.WriteLine($"[主窗口-PLC] 📊 最终结果: {(isQualified ? "合格" : "不合格")}");

                    // 写入 PLC
                    Debug.WriteLine("[主窗口-PLC] 📝 写入PLC结果...");
                    await WriteDetectionResult(isQualified);

                    // 更新统计
                    _statisticsService.RecordDetection(isQualified);
                    Debug.WriteLine("[主窗口-PLC] 📈 统计已更新");

                    // 日志
                    int detectedCount = lastResult.Results?.Count ?? 0;
                    string objDesc = detectedCount > 0 ? $"检测到 {detectedCount} 个目标" : "未检测到目标";
                    await _uiController.LogToFrontend($"PLC触发检测: {(isQualified ? "合格" : "不合格")} | {objDesc}", isQualified ? "success" : "error");

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
                await _plcService.WriteReleaseSignalAsync(_appConfig.PlcResultAddress);
                await _uiController.LogToFrontend("手动放行信号已发送", "success");
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"放行失败: {ex.Message}", "error");
            }
        }

        #endregion
    }
}
