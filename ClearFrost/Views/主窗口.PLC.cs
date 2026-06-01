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
using ClearFrost.Hardware;
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
        private static readonly string _diagLogPath = RuntimePaths.PlcDiagLogPath;
        private static readonly AsyncDiagnosticLogger _diagLogger = new AsyncDiagnosticLogger(_diagLogPath);

        private static void DiagLog(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                Debug.WriteLine(line);
                if (!_diagLogger.Enqueue(line))
                {
                    Debug.WriteLine("[PLC-DIAG] 异步诊断日志队列已满，丢弃一条日志");
                }
            }
            catch { /* 诊断日志写入失败不影响业务 */ }
        }

        private static void FlushDiagnosticLog()
        {
            try
            {
                _diagLogger.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PLC-DIAG] 刷新诊断日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过服务层连接 PLC
        /// </summary>
        private async Task ConnectPlcViaServiceAsync(bool startTriggerMonitoring = true)
        {
            string driverProvider = _appConfig.PlcDriverProvider;
            string protocol = _appConfig.PlcProtocol;
            string ip = _appConfig.PlcIp;
            int port = _appConfig.PlcPort;
            string triggerAddress = _appConfig.PlcTriggerAddress;

            await _uiController.LogToFrontend($"正在连接 PLC: {driverProvider}/{protocol} @ {ip}:{port}", "info");

            bool success = await _plcService.ConnectAsync(new PlcConnectionOptions
            {
                Protocol = protocol,
                DriverProvider = driverProvider,
                Ip = ip,
                Port = port,
                SiemensCpuModel = _appConfig.PlcSiemensCpuModel,
                SiemensRack = _appConfig.PlcSiemensRack,
                SiemensSlot = _appConfig.PlcSiemensSlot,
                TriggerAddress = triggerAddress
            });

            if (success)
            {
                bool shouldStartPlcTrigger =
                    _appConfig.TriggerSource == TriggerSource.PLC &&
                    startTriggerMonitoring;

                if (shouldStartPlcTrigger)
                {
                    var cameraReady = await WaitForCameraReadyForInspectionAsync();
                    if (!cameraReady.Ready)
                    {
                        await _uiController.LogToFrontend(
                            $"PLC已连接，相机暂未就绪，仍启动触发监听；触发时将自动恢复相机: {cameraReady.Message}",
                            "warning");
                    }
                }

                if (shouldStartPlcTrigger)
                {
                    // 启动 PLC 触发监控
                    _plcService.StartMonitoring(
                        triggerAddress,
                        _appConfig.PlcPollingIntervalMs,
                        _appConfig.PlcTriggerDelayMs,
                        new PlcMonitoringOptions
                        {
                            ProtocolMode = _appConfig.PlcProtocolMode,
                            TriggerSeqAddress = _appConfig.PlcTriggerSeqAddress
                        });
                    await _uiController.LogToFrontend(
                        $"✅ PLC连接成功，开始监听 {triggerAddress} ({_appConfig.PlcProtocolMode})", "success");
                }
                else
                {
                    _plcService.StopMonitoring();
                    string modeText = _appConfig.TriggerSource == TriggerSource.SerialPhotoelectric
                        ? "串口光电触发模式，PLC仅用于结果写回/条码读取"
                        : "PLC触发监听暂未启动";
                    await _uiController.LogToFrontend(
                        $"✅ PLC连接成功（{modeText}）", "success");
                }

                WriteHealthSnapshotLog("PLC连接成功");
                await SendHealthSnapshotToFrontendAsync();
            }
            else
            {
                string err = _plcService.LastError ?? "未知错误";
                RecordHealthError("PLC", $"PLC连接失败: {err}");
                await _uiController.LogToFrontend(
                    $"❌ PLC连接失败: {err}（协议: {protocol}, 地址: {ip}:{port}）", "error");
                await SendHealthSnapshotToFrontendAsync();
            }
        }

        /// <summary>
        /// 启动 PLC 触发监听（要求 PLC 已连接且当前触发源为 PLC）。
        /// </summary>
        private async Task StartPlcTriggerMonitoringIfReadyAsync()
        {
            if (_appConfig.TriggerSource != TriggerSource.PLC)
            {
                _plcService.StopMonitoring();
                return;
            }

            if (!_plcService.IsConnected)
            {
                await ConnectPlcViaServiceAsync(startTriggerMonitoring: true);
                return;
            }

            var cameraReady = await WaitForCameraReadyForInspectionAsync();
            if (!cameraReady.Ready)
            {
                await _uiController.LogToFrontend(
                    $"PLC已连接，相机暂未就绪，保持触发监听；触发时将自动恢复相机: {cameraReady.Message}",
                    "warning");
            }

            _plcService.StartMonitoring(
                _appConfig.PlcTriggerAddress,
                _appConfig.PlcPollingIntervalMs,
                _appConfig.PlcTriggerDelayMs,
                new PlcMonitoringOptions
                {
                    ProtocolMode = _appConfig.PlcProtocolMode,
                    TriggerSeqAddress = _appConfig.PlcTriggerSeqAddress
                });

            await _uiController.LogToFrontend(
                $"✅ PLC开始监听 {_appConfig.PlcTriggerAddress} ({_appConfig.PlcProtocolMode})", "success");
            await SendHealthSnapshotToFrontendAsync();
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
