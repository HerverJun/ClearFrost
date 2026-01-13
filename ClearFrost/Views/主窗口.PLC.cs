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
        private async void HandlePlcTrigger()
        {
            int maxRetries = _appConfig.MaxRetryCount;
            int retryInterval = _appConfig.RetryIntervalMs;
            DetectionResult? lastResult = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    await _uiController.LogToFrontend($"触发重拍 ({attempt}/{maxRetries})", "warning");
                    await Task.Delay(retryInterval);
                }

                lastResult = await RunDetectionOnceAsync();

                if (lastResult != null && lastResult.IsQualified)
                {
                    break;
                }
                else if (lastResult != null && !lastResult.IsQualified && attempt < maxRetries)
                {
                    // 显示中间结果
                    DisplayImageOnly(lastResult.OriginalBitmap, lastResult.Results, lastResult.UsedModelLabels);
                    lastResult.OriginalBitmap?.Dispose();
                }
            }

            if (lastResult != null)
            {
                ProcessFinalResult(lastResult);
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
        private async void fx_btn_Logic()
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


