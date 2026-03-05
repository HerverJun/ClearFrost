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
        /// [PLC-DIAG] 诊断日志 → 追加写入 plc_diag.log（现场无需开发工具即可查看）
        /// </summary>
        private static readonly string _diagLogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "plc_diag.log");
        private static void DiagLog(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                Debug.WriteLine(line);
                File.AppendAllText(_diagLogPath, line + Environment.NewLine);
            }
            catch { /* 诊断日志写入失败不影响业务 */ }
        }

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
                await _uiController.LogToFrontend(
                    $"✅ PLC连接成功，开始监听 D{_appConfig.PlcTriggerAddress}", "success");
            }
            else
            {
                string err = _plcService.LastError ?? "未知错误";
                await _uiController.LogToFrontend(
                    $"❌ PLC连接失败: {err}（协议: {protocol}, 地址: {ip}:{port}）", "error");
            }
        }

        /// <summary>
        /// 基于目标标签与目标数量重新计算合格判定（用于 ROI 过滤后）。
        /// </summary>
        private static bool EvaluateQualificationByTarget(
            IReadOnlyList<YoloResult> results,
            string[]? labels,
            string? targetLabel,
            int targetCount)
        {
            if (!string.IsNullOrWhiteSpace(targetLabel) && targetCount > 0 && labels != null)
            {
                int actualCount = results.Count(r =>
                {
                    if (r.ClassId < 0 || r.ClassId >= labels.Length)
                    {
                        return false;
                    }

                    return string.Equals(labels[r.ClassId], targetLabel, StringComparison.OrdinalIgnoreCase);
                });
                return actualCount == targetCount;
            }

            return results.Count == 0;
        }

        /// <summary>
        /// 写入检测结果到 PLC
        /// </summary>
        public async Task<bool> WriteDetectionResult(bool isQualified)
        {
            if (!plcConnected)
            {
                await _uiController.LogToFrontend("PLC未连接，无法写入检测结果", "error");
                return false;
            }

            short writeValue = isQualified ? _appConfig.PlcOkValue : _appConfig.PlcNgValue;
            bool success = await _plcService.WriteResultAsync(_appConfig.PlcResultAddress, writeValue);
            await _uiController.LogToFrontend(
                success
                    ? $"PLC写入结果成功: {(isQualified ? "合格" : "不合格")}"
                    : $"PLC写入结果失败: {(isQualified ? "合格" : "不合格")}",
                success ? "info" : "error");
            return success;
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
