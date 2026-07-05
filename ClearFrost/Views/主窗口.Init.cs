using MVSDK_Net;
using ClearFrost.Config;
using ClearFrost.Models;
using ClearFrost.Hardware;
using ClearFrost.Hardware.Triggers;
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
using ClearFrost.Core.Models;
using ClearFrost.Core.Inspection;
using System.Threading.Tasks;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Core.Security;
using ClearFrost.Yolo;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Services.Replay;

namespace ClearFrost
{
    public partial class 主窗口
    {
        #region 2. 初始化与生命周期 (Initialization)

        private void RegisterEvents()
        {
            // PLC 服务事件
            _plcService.ConnectionChanged += (connected) =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.UpdateConnection("plc", connected), "更新PLC状态");
                    SafeFireAndForget(_uiController.LogToFrontend(
                        connected ? $"PLC: 已连接 ({_plcService.ProtocolName})" : "PLC: 已断开",
                        connected ? "success" : "error"), "PLC状态日志");
                    SafeFireAndForget(SendHealthSnapshotToFrontendAsync(), "刷新健康快照");
                });
            };
            _plcService.TriggerReceived += () =>
            {
                Debug.WriteLine($"[主窗口] 📥 收到PLC触发事件 - {DateTime.Now:HH:mm:ss.fff}");
                var context = new PlcTriggerContext
                {
                    TriggerSource = "PLC",
                    TriggerAddress = _appConfig.PlcTriggerAddress,
                    TriggerValue = 1,
                    TriggerTime = DateTimeOffset.Now
                };

                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.FlashPlcTrigger(), "PLC触发指示灯");
                    SafeFireAndForget(HandlePlcTriggerAsync(context), "PLC触发检测");
                });
            };
            _plcService.TriggerContextReceived += (context) =>
            {
                Debug.WriteLine($"[主窗口] 📥 收到PLC上下文触发事件 - Seq={context.TriggerSeq?.ToString() ?? "-"} - {DateTime.Now:HH:mm:ss.fff}");
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.FlashPlcTrigger(), "PLC触发指示灯");
                    SafeFireAndForget(HandlePlcTriggerAsync(context), "PLC上下文触发检测");
                });
            };
            _plcService.ErrorOccurred += (error) =>
            {
                RecordHealthError("PLC", error);
                SafeFireAndForget(_uiController.LogToFrontend($"PLC错误: {error}", "error"), "PLC错误日志");
                SafeFireAndForget(SendHealthSnapshotToFrontendAsync(), "刷新健康快照");
            };

            // 串口光电触发服务事件
            _serialTriggerService.ConnectionChanged += (connected) =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.UpdateConnection("serialTrigger", connected), "更新串口光电状态");
                });
            };
            _serialTriggerService.TriggerReceived += () =>
            {
                Debug.WriteLine($"[主窗口] 收到串口光电触发事件 - {DateTime.Now:HH:mm:ss.fff}");
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.FlashPlcTrigger(), "串口光电触发指示灯");
                });

                InvokeOnUIThread(() => SafeFireAndForget(btnCapture_LogicAsync("串口光电"), "串口光电触发检测"));
            };
            _serialTriggerService.ErrorOccurred += (error) =>
            {
                RecordHealthError("SerialTrigger", error);
                SafeFireAndForget(_uiController.LogToFrontend($"串口光电错误: {error}", "error"), "串口光电错误日志");
                SafeFireAndForget(SendHealthSnapshotToFrontendAsync(), "刷新健康快照");
            };

            // Camera 服务事件
            _cameraService.ConnectionChanged += (connected) =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.UpdateConnection("cam", connected), "更新相机状态");
                });
            };
            _cameraService.ErrorOccurred += (error) =>
            {
                RecordHealthError("Camera", error);
                SafeFireAndForget(_uiController.LogToFrontend($"相机错误: {error}", "error"), "相机错误日志");
                SafeFireAndForget(SendHealthSnapshotToFrontendAsync(), "刷新健康快照");
            };

            // Detection 服务事件
            _detectionService.DetectionCompleted += (result) =>
            {
                // 高频生产节拍下不向前端日志追加每次 OK/NG，主界面状态由 SendDetectionFrame 更新。
                Debug.WriteLine($"[DetectionService] 推理完成: Error={result.HasError}, {result.ElapsedMs}ms");
            };
            _detectionService.ModelLoaded += (modelName) =>
            {
                SafeFireAndForget(_uiController.LogToFrontend($"模型已加载: {modelName}", "success"), "模型加载日志");
                SafeFireAndForget(_uiController.SendModelLabels(_detectionService.GetLabels()), "推送模型标签");
            };
            _detectionService.ErrorOccurred += (error) =>
            {
                RecordHealthError("Detection", error);
                SafeFireAndForget(_uiController.LogToFrontend($"检测错误: {error}", "error"), "检测错误日志");
            };

            // Statistics 服务事件
            _statisticsService.StatisticsUpdated += (snapshot) =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.UpdateUI(snapshot.TotalCount, snapshot.QualifiedCount, snapshot.UnqualifiedCount), "统计更新");
                });
            };
            _statisticsService.DayReset += () =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.LogToFrontend("检测到跨日，统计已自动重置", "info"), "跨日重置日志");
                });
            };

            // 订阅退出事件
            _uiController.OnExitApp += (s, e) =>
            {
                BeginAppShutdown("WebUI.exit_app");
            };

            // 订阅最小化事件
            _uiController.OnMinimizeApp += (s, e) =>
            {
                if (IsShutdownInProgress) return;

                this.BeginInvoke((MethodInvoker)delegate
                {
                    this.WindowState = FormWindowState.Minimized;
                });
            };

            // 订阅最大化/还原事
            _uiController.OnToggleMaximize += (s, e) =>
            {
                if (IsShutdownInProgress) return;

                this.BeginInvoke((MethodInvoker)delegate
                {
                    if (this.WindowState == FormWindowState.Maximized)
                        this.WindowState = FormWindowState.Normal;
                    else
                        this.WindowState = FormWindowState.Maximized;
                });
            };

            // 订阅拖动窗口事件
            _uiController.OnStartDrag += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    WindowHelpers.StartWindowDrag(this);
                });
            };

            // 绑定 WebUI 事件
            _uiController.OnStartSystem += (s, e) => SafeFireAndForget(StartSystemAsync(), "启动系统");
            _uiController.OnStopSystem += (s, e) => SafeFireAndForget(StopSystemAsync(), "停止检测");
            _uiController.OnOpenCamera += (s, e) => SafeFireAndForget(btnOpenCamera_LogicAsync(), "启动系统");
            _uiController.OnManualDetect += (s, e) => InvokeOnUIThread(() => SafeFireAndForget(ManualDetectAsync(), "手动检测"));
            _uiController.OnCaptureCameraPreview += (s, json) => InvokeOnUIThread(() => SafeFireAndForget(CaptureCameraPreviewFrameAsync(json), "获取相机预览单帧"));
            _uiController.OnManualRelease += (s, payloadJson) => SafeFireAndForget(fx_btn_LogicAsync(payloadJson), "手动放行");
            _uiController.OnOpenSettings += (s, e) => InvokeOnUIThread(() => btnSettings_Logic());
            _uiController.OnCollectDataset += (s, e) => SafeFireAndForget(CollectDatasetAsync(), "数据集收集");
            _uiController.OnRunHistoryRulePreview += (s, json) => SafeFireAndForget(RunHistoryRulePreviewAsync(json), "历史图规则复判");
            _uiController.OnQueryManualReviewRecords += (s, args) => SafeFireAndForget(QueryManualReviewRecordsAsync(args), "人工复核记录查询");
            _uiController.OnSaveManualReview += (s, args) => SafeFireAndForget(SaveManualReviewAsync(args), "人工复核保存");
            _uiController.OnCreateReplayDataset += (s, args) => SafeFireAndForget(CreateReplayDatasetAsync(args), "Replay Dataset冻结");
            _uiController.OnRunReplayComparison += (s, args) => SafeFireAndForget(RunReplayComparisonAsync(args), "模型回放验收");
            _uiController.OnApproveReplayCandidate += (s, args) => SafeFireAndForget(ApproveReplayCandidateAsync(args), "Replay Evidence批准");
            _uiController.OnPreviewReplayDataset += (s, args) => SafeFireAndForget(PreviewReplayDatasetAsync(args), "Replay Dataset预览");
            _uiController.OnQueryReplayDatasets += (s, args) => SafeFireAndForget(QueryReplayDatasetsAsync(args), "Replay Dataset查询");
            _uiController.OnArchiveReplayDataset += (s, args) => SafeFireAndForget(ArchiveReplayDatasetAsync(args), "Replay Dataset归档");
            _uiController.OnCancelReplayRun += (s, args) => SafeFireAndForget(CancelReplayRunAsync(args), "Replay取消");
            _uiController.OnQueryReplayRuns += (s, args) => SafeFireAndForget(QueryReplayRunsAsync(args), "Replay Run查询");
            _uiController.OnQueryReplayReport += (s, args) => SafeFireAndForget(QueryReplayReportAsync(args), "Replay报告查询");
            _uiController.OnQueryModelApprovalEvidence += (s, args) => SafeFireAndForget(QueryModelApprovalEvidenceAsync(args), "Replay Evidence查询");
            _uiController.OnRunReplayIntegrityScan += (s, args) => SafeFireAndForget(RunReplayIntegrityScanAsync(args), "Replay完整性扫描");
            _uiController.OnGetModelList += (s, e) => SafeFireAndForget(InitModelList(), "刷新模型列表");
            _uiController.OnChangeModel += (s, modelName) => InvokeOnUIThread(() => ChangeModel_Logic(modelName));
            _uiController.OnConnectPlc += (s, e) => SafeFireAndForget(ConnectPlcViaServiceAsync(), "PLC手动连接");
            _uiController.OnRequestHealthSnapshot += (s, e) => SafeFireAndForget(SendHealthSnapshotToFrontendAsync(showToast: true), "前端刷新健康快照");
            _uiController.OnExportDiagnosticPackage += (s, args) => SafeFireAndForget(ExportDiagnosticPackageFromWebAsync(args), "导出诊断包");
            _uiController.OnQueryDiagnosticPackages += (s, args) => SafeFireAndForget(QueryDiagnosticPackagesFromWebAsync(args), "查询诊断包历史");
            _uiController.OnVerifyDiagnosticPackage += (s, args) => SafeFireAndForget(VerifyDiagnosticPackageFromWebAsync(args), "复核诊断包");
            _uiController.OnMaintenanceAdviceAction += (s, args) => SafeFireAndForget(HandleMaintenanceAdviceActionFromWebAsync(args), "维护建议处理/复检");
            _uiController.OnShiftTaskAction += (s, args) => SafeFireAndForget(HandleShiftTaskActionFromWebAsync(args), "班次待办处理/复检");
            _uiController.OnExportFieldHandoffReport += (s, args) => SafeFireAndForget(ExportFieldHandoffReportFromWebAsync(args), "导出现场交接报告");
            _uiController.OnQueryFieldHandoffReports += (s, args) => SafeFireAndForget(QueryFieldHandoffReportsFromWebAsync(args), "查询现场交接报告历史");
            _uiController.OnFieldDebugCommand += (s, args) => SafeFireAndForget(HandleFieldDebugCommandAsync(args), "现场单步调试");
            _uiController.OnVisionDebugCommand += (s, args) => SafeFireAndForget(HandleVisionDebugCommandAsync(args), "视觉算法调试");
            _uiController.OnThresholdChanged += (s, val) =>
            {
                if (IsRuntimeMutationBlocked("ROI阈值更新")) return;
                _appConfig.IouThreshold = Math.Clamp(val / 100f, 0f, 1f);
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("IOU阈值更新");
                }
            };
            _uiController.OnGetStatisticsHistory += async (s, e) =>
            {
                var (history, stats) = _statisticsService.GetStatisticsData();
                await _uiController.SendStatisticsHistory(history, stats);
            };
            _uiController.OnClearStatisticsHistory += async (s, e) =>
            {
                _statisticsService.ClearHistory();
                var (history, stats) = _statisticsService.GetStatisticsData();
                await _uiController.SendStatisticsHistory(history, stats);
                await _uiController.LogToFrontend("✅ 历史统计数据已清空", "success");
            };
            _uiController.OnResetStatistics += async (s, e) =>
            {
                _statisticsService.ResetToday();
                await _uiController.UpdateUI(0, 0, 0);
                await _uiController.LogToFrontend("✅ 今日统计已清除", "success");
            };

            // ================== 串口光电事件 ==================
            _uiController.OnSerialAutoDetectPorts += async (s, e) =>
            {
                try
                {
                    var ports = await _serialTriggerService.GetAvailablePortsAsync();
                    await _uiController.SendUiCommand("serialPortsDetected", new { ports });
                }
                catch (Exception ex)
                {
                    await _uiController.SendUiCommand("serialPortsDetected", new { ports = Array.Empty<object>() });
                    await _uiController.SendUiCommand("toast", new
                    {
                        message = $"串口识别失败: {ex.Message}",
                        type = "error",
                        durationMs = 2200
                    });
                    await _uiController.LogToFrontend($"串口识别失败: {ex.Message}", "error");
                }
            };
            _uiController.OnSerialTestTrigger += async (s, e) =>
            {
                try
                {
                    bool ok = await _serialTriggerService.SendTestTriggerAsync();
                    if (ok)
                    {
                        await _uiController.SendUiCommand("toast", new
                        {
                            message = "测试帧已写出",
                            type = "success",
                            durationMs = 1200
                        });
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"串口测试失败: {_serialTriggerService.LastError}", "error");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"串口测试异常: {ex.Message}", "error");
                }
            };
            _uiController.OnSerialSimulateTrigger += (s, e) =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.SendUiCommand("toast", new
                    {
                        message = "已模拟一次串口触发",
                        type = "info",
                        durationMs = 1200
                    }), "串口模拟触发提示");
                    SafeFireAndForget(_uiController.FlashPlcTrigger(), "串口模拟触发指示灯");
                    SafeFireAndForget(btnCapture_LogicAsync("串口光电模拟"), "串口光电模拟触发检测");
                });
            };

            // ================== 多相机事件 ==================
            _uiController.OnGetCameraList += async (s, e) =>
            {
                var cameras = _appConfig.Cameras.Select(c => new
                {
                    id = c.Id,
                    displayName = c.DisplayName,
                    serialNumber = c.SerialNumber,
                    manufacturer = c.Manufacturer,
                    pixelFormat = c.PixelFormat,
                    exposureTime = c.ExposureTime,
                    gain = c.Gain
                }).ToList();

                await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
            };

            _uiController.OnSwitchCamera += async (s, cameraId) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("相机切换")) return;
                    var newCam = _cameraService is CameraService cameraService
                        ? cameraService.SwitchActiveCamera(cameraId)
                        : null;

                    if (newCam == null && _cameraService is not CameraService)
                    {
                        _cameraService.StopCapture();
                        _cameraManager.ActiveCameraId = cameraId;
                        newCam = _cameraManager.ActiveCamera;
                        if (newCam?.IsOpen == true)
                        {
                            _cameraService.StartCapture();
                        }
                    }

                    if (newCam != null)
                    {
                        cam = newCam.Camera;
                        if (newCam.IsOpen)
                        {
                            _cameraService.StartCapture();
                            await _uiController.LogToFrontend($"✅ 已切换到相机: {newCam.Config.DisplayName}");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"ℹ️ 已切换到相机 (未连接): {newCam.Config.DisplayName}", "warning");
                        }

                        _cameraManager.SaveToConfig(_appConfig);
                        if (_appConfig.Save())
                        {
                            TrySaveCurrentRecipeSnapshot("相机切换");
                        }
                    }
                    else
                    {
                        // 尝试在配置中查找（支持离线切换）
                        var cfgCam = _appConfig.Cameras.FirstOrDefault(c => c.Id == cameraId);
                        if (cfgCam != null)
                        {
                            _appConfig.ActiveCameraId = cameraId;
                            if (_appConfig.Save())
                            {
                                TrySaveCurrentRecipeSnapshot("相机切换");
                            }
                            // 虽然离线，但更新了配置，后续点击"连接相机"时会尝试连接此相机
                            await _uiController.LogToFrontend($"ℹ️ 已切换到相机 (未连接): {cfgCam.DisplayName}", "warning");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"切换相机失败: 未找到 {cameraId}", "error");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"切换相机错误: {ex.Message}", "error");
                }
            };

            _uiController.OnAddCamera += async (s, json) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("相机配置更新")) return;
                    using var doc = JsonDocument.Parse(json);
                    var r = doc.RootElement;

                    string displayName = r.TryGetProperty("displayName", out var dn) ? dn.GetString()?.Trim() ?? "" : "";
                    string serialNumber = r.TryGetProperty("serialNumber", out var sn) ? sn.GetString()?.Trim() ?? "" : "";
                    string manufacturer = r.TryGetProperty("manufacturer", out var mf) ? mf.GetString() ?? "Huaray" : "Huaray";
                    string pixelFormat = r.TryGetProperty("pixelFormat", out var pf) ? NormalizeCameraPixelFormatForSave(pf.GetString()) : "Auto";
                    double exposure = r.TryGetProperty("exposureTime", out var exp) ? exp.GetDouble() : 50000;
                    double gain = r.TryGetProperty("gain", out var g) ? g.GetDouble() : 1.0;

                    if (string.IsNullOrEmpty(serialNumber))
                    {
                        await _uiController.LogToFrontend("序列号不能为空", "error");
                        return;
                    }

                    // 检查是否已存在（更新）或新增
                    var existing = _appConfig.Cameras.FirstOrDefault(c => c.SerialNumber == serialNumber);
                    if (existing != null)
                    {
                        existing.DisplayName = displayName;
                        existing.Manufacturer = manufacturer;
                        existing.PixelFormat = pixelFormat;
                        existing.ExposureTime = exposure;
                        existing.Gain = gain;
                        await _uiController.LogToFrontend($"✅ 已更新相机配置: {displayName} ({manufacturer})");
                    }
                    else
                    {
                        var newConfig = new CameraConfig
                        {
                            Id = $"cam_{DateTime.Now:yyyyMMddHHmmss}",
                            SerialNumber = serialNumber,
                            DisplayName = displayName,
                            Manufacturer = manufacturer,
                            PixelFormat = pixelFormat,
                            ExposureTime = exposure,
                            Gain = gain,
                            IsEnabled = true
                        };
                        _appConfig.Cameras.Add(newConfig);

                        // 添加到相机管理器（仅注册，不在此阶段占用硬件）
                        bool added = _cameraManager.AddCamera(newConfig);
                        if (added)
                        {
                            await _uiController.LogToFrontend($"✅ 已添加新相机: {displayName} ({manufacturer})");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"ℹ️ 相机配置已保存，但注册阶段未完成: {displayName}", "warning");
                        }
                    }

                    if (_appConfig.Save())
                    {
                        TrySaveCurrentRecipeSnapshot("相机配置更新");
                    }

                    // 刷新前端列表
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        pixelFormat = c.PixelFormat,
                        exposureTime = c.ExposureTime,
                        gain = c.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"添加相机失败: {ex.Message}", "error");
                }
            };

            _uiController.OnDeleteCamera += async (s, cameraId) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("相机配置删除")) return;
                    var camToRemove = _appConfig.Cameras.FirstOrDefault(c => c.Id == cameraId);
                    if (camToRemove == null)
                    {
                        await _uiController.LogToFrontend($"未找到相机: {cameraId}", "error");
                        return;
                    }

                    _cameraManager.RemoveCamera(cameraId);
                    _appConfig.Cameras.Remove(camToRemove);
                    if (_appConfig.Save())
                    {
                        TrySaveCurrentRecipeSnapshot("相机配置删除");
                    }

                    await _uiController.LogToFrontend($"? 已删除相机: {camToRemove.DisplayName}");

                    // 刷新前端列表
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        pixelFormat = c.PixelFormat,
                        exposureTime = c.ExposureTime,
                        gain = c.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"删除相机失败: {ex.Message}", "error");
                }
            };

            // 华睿搜索/超级搜索共用同一实现：通过华睿 SDK 枚举在线设备。
            _uiController.OnSuperSearchCameras += async (s, e) =>
            {
                var cameraList = new List<Dictionary<string, string>>();

                try
                {
                    Debug.WriteLine("[华睿搜索] 事件触发开始");
                    await _uiController.LogToFrontend("正在通过华睿SDK搜索局域网相机...");

                    // 直接调用华睿 SDK（与 CameraManager.AddCamera 完全一致的调用方式）
                    var deviceList = new IMVDefine.IMV_DeviceList();
                    int res = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                    Debug.WriteLine($"[华睿搜索] IMV_EnumDevices 返回码: {res}, 设备数: {deviceList.nDevNum}");

                    if (res == IMVDefine.IMV_OK && deviceList.nDevNum > 0)
                    {
                        int structSize = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
                        for (int i = 0; i < (int)deviceList.nDevNum; i++)
                        {
                            try
                            {
                                var info = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                                    deviceList.pDevInfo + structSize * i,
                                    typeof(IMVDefine.IMV_DeviceInfo))!;

                                string sn = info.serialNumber?.Trim() ?? "";
                                Debug.WriteLine($"[华睿搜索] 发现设备[{i}]: SN='{sn}'");

                                if (!string.IsNullOrEmpty(sn))
                                {
                                    cameraList.Add(new Dictionary<string, string>
                                    {
                                        ["serialNumber"] = sn,
                                        ["manufacturer"] = "Huaray",
                                        ["model"] = "Huaray Camera",
                                        ["userDefinedName"] = sn,
                                        ["interfaceType"] = "GigE/USB"
                                    });
                                }
                            }
                            catch (Exception innerEx)
                            {
                                Debug.WriteLine($"[华睿搜索] 解析设备[{i}]失败: {innerEx.Message}");
                            }
                        }
                    }
                    else if (res != IMVDefine.IMV_OK)
                    {
                        Debug.WriteLine($"[华睿搜索] SDK 枚举失败，错误码: {res}");
                    }
                    else
                    {
                        Debug.WriteLine("[华睿搜索] 未发现任何设备");
                    }

                    await _uiController.LogToFrontend($"华睿SDK发现 {cameraList.Count} 台相机", cameraList.Count > 0 ? "success" : "warning");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[华睿搜索] 异常: {ex}");
                    await _uiController.LogToFrontend($"华睿搜索失败: {ex.Message}", "error");
                }

                // 无论成功失败，必须通知前端结束加载状态
                Debug.WriteLine($"[华睿搜索] 准备发送 {cameraList.Count} 个结果到前端");
                await _uiController.SendDiscoveredCameras(cameraList);
                Debug.WriteLine("[华睿搜索] 完成");
            };

            // 相机超级搜索 (海康) - 使用海康SDK发现所有相机
            _uiController.OnSuperSearchCamerasHik += async (s, e) =>
            {
                try
                {
                    await _uiController.LogToFrontend("正在通过海康SDK搜索局域网相机...");
                    var allCameras = _cameraManager.DiscoverHikvisionCameras();
                    var cameraList = allCameras.Select(c => new
                    {
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        model = c.Model,
                        userDefinedName = c.UserDefinedName,
                        interfaceType = c.InterfaceType
                    }).ToList();
                    await _uiController.SendDiscoveredCameras(cameraList);
                    await _uiController.LogToFrontend($"海康SDK发现 {cameraList.Count} 台相机", cameraList.Count > 0 ? "success" : "warning");
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"海康搜索失败: {ex.Message}", "error");
                }
            };

            // 直接连接相机（无序列号过滤）
            _uiController.OnDirectConnectCamera += async (s, json) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("相机直连配置")) return;
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string sn = root.TryGetProperty("serialNumber", out var snEl) ? snEl.GetString()?.Trim() ?? "" : "";
                    string manufacturer = root.TryGetProperty("manufacturer", out var mfEl) ? mfEl.GetString() ?? "" : "";
                    string model = root.TryGetProperty("model", out var mdEl) ? mdEl.GetString() ?? "" : "";
                    string displayName = root.TryGetProperty("userDefinedName", out var dnEl) ? dnEl.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(sn))
                    {
                        const string message = "相机序列号为空，无法连接";
                        await _uiController.LogToFrontend(message, "error");
                        await _uiController.SendUiCommand("cameraDirectConnectResult", new
                        {
                            success = false,
                            message = message,
                            serialNumber = sn
                        });
                        return;
                    }

                    // 创建新相机配置
                    var newConfig = new CameraConfig
                    {
                        Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                        SerialNumber = sn,
                        Manufacturer = manufacturer,
                        DisplayName = string.IsNullOrEmpty(displayName) ? model : displayName,
                        ExposureTime = 10000,
                        Gain = 1.0
                    };

                    bool added = _cameraManager.AddCamera(newConfig);
                    if (added)
                    {
                        _appConfig.Cameras.Add(newConfig);
                        _appConfig.ActiveCameraId = newConfig.Id;
                        _cameraManager.ActiveCameraId = newConfig.Id;
                        if (_appConfig.Save())
                        {
                            TrySaveCurrentRecipeSnapshot("相机直连配置");
                        }

                        // 刷新前端相机列表
                        var cameras = _appConfig.Cameras.Select(c => new
                        {
                            id = c.Id,
                            displayName = c.DisplayName,
                            serialNumber = c.SerialNumber,
                            manufacturer = c.Manufacturer,
                            pixelFormat = c.PixelFormat,
                            exposureTime = c.ExposureTime,
                            gain = c.Gain
                        }).ToList();
                        await _uiController.SendCameraList(cameras, _appConfig.ActiveCameraId ?? "");
                        string message = $"相机 [{newConfig.DisplayName}] 已添加并设为当前相机，可直接获取单帧预览或点击“启动系统”开始检测";
                        await _uiController.LogToFrontend(message, "success");
                        await _uiController.SendUiCommand("cameraDirectConnectResult", new
                        {
                            success = true,
                            message = message,
                            serialNumber = sn,
                            cameraId = newConfig.Id
                        });
                    }
                    else
                    {
                        string message = $"相机连接失败: {sn}";
                        await _uiController.LogToFrontend(message, "error");
                        await _uiController.SendUiCommand("cameraDirectConnectResult", new
                        {
                            success = false,
                            message = message,
                            serialNumber = sn
                        });
                    }
                }
                catch (Exception ex)
                {
                    string message = $"直接连接相机失败: {ex.Message}";
                    await _uiController.LogToFrontend(message, "error");
                    await _uiController.SendUiCommand("cameraDirectConnectResult", new
                    {
                        success = false,
                        message = message
                    });
                }
            };

            // 注册窗体关闭事件
            this.FormClosing += OnFormClosingHandler;
        }

        private async void 主窗口_Load(object? sender, EventArgs e)
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[主窗口] 初始化失败: {ex}");
                MessageBox.Show(
                    $"系统初始化失败，程序将退出。\n\n{ex.Message}",
                    "初始化失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                BeginInvoke((MethodInvoker)delegate { Close(); });
            }
        }

        private async Task InitializeAsync()
        {
            // 阻止系统休眠
            WindowHelpers.PreventSleep();

            // 窗口样式与初始最大化已在构造函数中设置

            // 订阅 WebUI 就绪事件
            _uiController.OnAppReady += async (s, ev) =>
            {
                try
                {
                    await _uiController.LogToFrontend("? WebUI已就绪");
                    await _uiController.LogToFrontend("系统初始化完成");
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        pixelFormat = c.PixelFormat,
                        exposureTime = c.ExposureTime,
                        gain = c.Gain
                    }).ToList();

                    var currentStats = _statisticsService.Current;
                    object[] modelNames = GetModelListPayload();
                    await _uiController.SendBootstrapSnapshot(
                        _appConfig,
                        cameras,
                        _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId,
                        modelNames,
                        currentStats,
                        BuildFieldDiagnosticsSnapshot(),
                        _appConfig.StoragePath);
                    await _uiController.SendUiCommand("setRoi", new { rect = SnapshotCurrentROI() });
                    await _uiController.SendModelLabels(_detectionService.GetLabels());
                    await _uiController.SendProjectPresets(ProjectPresetStore.Load());

                    if (currentStats.TotalCount > 0)
                    {
                        await _uiController.LogToFrontend($"已加载今日统计: 总计{currentStats.TotalCount}, 合格{currentStats.QualifiedCount}, 不合格{currentStats.UnqualifiedCount}");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"WebUI初始化流程异常: {ex.Message}", "error");
                }
            };

            // 订阅测试YOLO事件
            _uiController.OnTestYolo += (s, e) => SafeFireAndForget(TestYolo_HandlerAsync(), "YOLO测试");

            // 订阅ROI更新事件
            _uiController.OnUpdateROI += (sender, normalizedRect) =>
            {
                if (IsRuntimeMutationBlocked("ROI更新")) return;
                _currentROI = Recipe.NormalizeRoi(normalizedRect);
                TrySaveCurrentRecipeSnapshot("ROI更新");
            };

            // 订阅YOLO参数修改事件
            _uiController.OnSetConfidence += (sender, conf) =>
            {
                if (IsRuntimeMutationBlocked("置信度阈值更新")) return;
                _appConfig.Confidence = conf;
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("置信度更新");
                }
            };

            _uiController.OnSetIou += (sender, iou) =>
            {
                if (IsRuntimeMutationBlocked("IOU阈值更新")) return;
                _appConfig.IouThreshold = Math.Clamp(iou, 0f, 1f);
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("IOU阈值更新");
                }
            };

            // 订阅任务类型修改事件
            _uiController.OnSetTaskType += (sender, taskType) =>
            {
                if (IsRuntimeMutationBlocked("检测任务类型更新")) return;
                _appConfig.TaskType = taskType;
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("任务类型更新");
                }
                // 使用检测服务更新任务类型
                _detectionService.SetTaskMode(taskType);
            };

            _uiController.OnSetAuxiliary1Model += async (sender, modelName) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("辅助模型1更新")) return;
                    if (!string.IsNullOrEmpty(modelName))
                    {
                        if (IsSameModelFile(modelName, _appConfig.CurrentModelReference?.ToSelectionValue() ?? _appConfig.CurrentModelFileName))
                        {
                            await _uiController.LogToFrontend("辅助模型1不能与主模型相同", "warning");
                            return;
                        }
                        if (IsSameModelFile(modelName, _appConfig.Auxiliary2ModelReference?.ToSelectionValue() ?? _appConfig.Auxiliary2ModelPath))
                        {
                            await _uiController.LogToFrontend("辅助模型1不能与辅助模型2相同", "warning");
                            return;
                        }
                    }

                    ProductionModelActivationResult result = await _modelActivationService.ActivateAuxiliaryAsync(
                        1,
                        modelName,
                        "辅助模型1更新",
                        _appConfig.EnableGpu,
                        _appConfig.GpuIndex).ConfigureAwait(false);
                    await _uiController.LogToFrontend(
                        result.Succeeded
                            ? (string.IsNullOrWhiteSpace(modelName) ? "辅助模型1已卸载" : $"辅助模型1已加载: {_appConfig.Auxiliary1ModelPath}")
                            : $"辅助模型1更新失败: [{result.ErrorCode}] {result.Message}{FormatCompensationFailures(result)}",
                        result.Succeeded ? "success" : "error");
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载辅助模型1失败: {ex.Message}", "error");
                }
            };

            _uiController.OnSetAuxiliary2Model += async (sender, modelName) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("辅助模型2更新")) return;
                    if (!string.IsNullOrEmpty(modelName))
                    {
                        if (IsSameModelFile(modelName, _appConfig.CurrentModelReference?.ToSelectionValue() ?? _appConfig.CurrentModelFileName))
                        {
                            await _uiController.LogToFrontend("辅助模型2不能与主模型相同", "warning");
                            return;
                        }
                        if (IsSameModelFile(modelName, _appConfig.Auxiliary1ModelReference?.ToSelectionValue() ?? _appConfig.Auxiliary1ModelPath))
                        {
                            await _uiController.LogToFrontend("辅助模型2不能与辅助模型1相同", "warning");
                            return;
                        }
                    }

                    ProductionModelActivationResult result = await _modelActivationService.ActivateAuxiliaryAsync(
                        2,
                        modelName,
                        "辅助模型2更新",
                        _appConfig.EnableGpu,
                        _appConfig.GpuIndex).ConfigureAwait(false);
                    await _uiController.LogToFrontend(
                        result.Succeeded
                            ? (string.IsNullOrWhiteSpace(modelName) ? "辅助模型2已卸载" : $"辅助模型2已加载: {_appConfig.Auxiliary2ModelPath}")
                            : $"辅助模型2更新失败: [{result.ErrorCode}] {result.Message}{FormatCompensationFailures(result)}",
                        result.Succeeded ? "success" : "error");
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载辅助模型2失败: {ex.Message}", "error");
                }
            };

            _uiController.OnToggleMultiModelFallback += async (sender, enabled) =>
            {
                if (!await EnsureRuntimeMutationAllowedAsync("多模型自动切换策略更新")) return;
                _appConfig.EnableMultiModelFallback = enabled;
                _detectionService.SetEnableFallback(enabled);
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("多模型策略更新");
                }
                await _uiController.LogToFrontend(enabled ? "? 多模型自动切换已启用" : "多模型自动切换已禁用");
            };

            // 订阅项目预设维护事件
            _uiController.OnGetProjectPresets += async (sender, e) =>
            {
                try
                {
                    await _uiController.SendProjectPresets(ProjectPresetStore.Load());
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载项目预设失败: {ex.Message}", "error");
                }
            };

            _uiController.OnSaveProjectPreset += async (sender, payloadJson) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("项目预设保存")) return;
                    var snapshot = ProjectPresetStore.SavePreset(payloadJson);
                    await _uiController.SendProjectPresets(snapshot);
                    await _uiController.LogToFrontend($"项目预设已保存: {snapshot.Path}", "success");
                }
                catch (Exception ex)
                {
                    await _uiController.SendUiCommand("alert", new { message = $"保存项目预设失败: {ex.Message}" });
                }
            };

            _uiController.OnDeleteProjectPreset += async (sender, presetId) =>
            {
                try
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("项目预设删除")) return;
                    var snapshot = ProjectPresetStore.DeletePreset(presetId);
                    await _uiController.SendProjectPresets(snapshot);
                    await _uiController.LogToFrontend("项目预设已删除", "success");
                }
                catch (Exception ex)
                {
                    await _uiController.SendUiCommand("alert", new { message = $"删除项目预设失败: {ex.Message}" });
                }
            };

            _uiController.OnExportConfigMigration += (sender, e) =>
                InvokeOnUIThread(() => SafeFireAndForget(ExportConfigMigrationAsync(), "导出配置迁移"));

            _uiController.OnImportConfigMigration += (sender, e) =>
                InvokeOnUIThread(() =>
                {
                    if (IsRuntimeMutationBlocked("配置迁移导入")) return;
                    SafeFireAndForget(ImportConfigMigrationAsync(), "导入配置迁移");
                });

            // 订阅配置保存事件
            _uiController.OnSaveSettings += async (sender, configJson) =>
            {
                AppConfig previousConfig = AppConfig.FromJson(_appConfig.ToPortableJson());
                bool configSaved = false;

                try
                {
                    bool hasSystemConfigChanges = CheckSystemConfigChanges(configJson);
                    if (hasSystemConfigChanges)
                    {
                        if (!await EnsureRuntimeMutationAllowedAsync("系统设置保存和部署"))
                        {
                            await _uiController.InitSettings(_appConfig);
                            return;
                        }
                    }
                    // 使用 JsonDocument 解析，允许部分更新
                    using (JsonDocument doc = JsonDocument.Parse(configJson))
                    {
                        var root = doc.RootElement;

                        // 逐个读取并更新配置属性
                        if (root.TryGetProperty("StoragePath", out var sp)) _appConfig.StoragePath = sp.GetString() ?? _appConfig.StoragePath;

                        string plcProtocol = _appConfig.PlcProtocol;
                        string plcDriverProvider = _appConfig.PlcDriverProvider;
                        PlcProtocolMode plcProtocolMode = _appConfig.PlcProtocolMode;
                        string plcIp = _appConfig.PlcIp;
                        int plcPort = _appConfig.PlcPort;
                        string plcTriggerAddress = _appConfig.PlcTriggerAddress;
                        string plcResultAddress = _appConfig.PlcResultAddress;
                        string plcTriggerSeqAddress = _appConfig.PlcTriggerSeqAddress;
                        string plcResultSeqAddress = _appConfig.PlcResultSeqAddress;
                        string plcVisionOnlineAddress = _appConfig.PlcVisionOnlineAddress;
                        string plcVisionReadyAddress = _appConfig.PlcVisionReadyAddress;
                        string plcVisionBusyAddress = _appConfig.PlcVisionBusyAddress;
                        string plcInspectionDoneAddress = _appConfig.PlcInspectionDoneAddress;
                        string plcErrorCodeAddress = _appConfig.PlcErrorCodeAddress;
                        string plcTraceSavedAddress = _appConfig.PlcTraceSavedAddress;
                        string plcHeartbeatAddress = _appConfig.PlcHeartbeatAddress;
                        string plcResetFaultAddress = _appConfig.PlcResetFaultAddress;
                        string plcTriggerAckAddress = _appConfig.PlcTriggerAckAddress;
                        string plcResultValidAddress = _appConfig.PlcResultValidAddress;
                        string plcResultAckAddress = _appConfig.PlcResultAckAddress;
                        int plcResultAckTimeoutMs = _appConfig.PlcResultAckTimeoutMs;
                        int plcTriggerDelayMs = _appConfig.PlcTriggerDelayMs;
                        int plcPollingIntervalMs = _appConfig.PlcPollingIntervalMs;
                        short plcOkValue = _appConfig.PlcOkValue;
                        short plcNgValue = _appConfig.PlcNgValue;
                        string plcSiemensCpuModel = _appConfig.PlcSiemensCpuModel;
                        int plcSiemensRack = _appConfig.PlcSiemensRack;
                        int plcSiemensSlot = _appConfig.PlcSiemensSlot;
                        bool barcodeEnabled = _appConfig.BarcodeEnabled;
                        string barcodeAddress = _appConfig.BarcodeAddress;
                        int barcodeWordLength = _appConfig.BarcodeWordLength;
                        string barcodeEncoding = _appConfig.BarcodeEncoding;
                        bool barcodeRequired = _appConfig.BarcodeRequired;
                        string currentOperatorId = _appConfig.CurrentOperatorId;
                        ProductionRole currentOperatorRole = _appConfig.CurrentOperatorRole;

                        TriggerSource triggerSource = _appConfig.TriggerSource;
                        string serialPortName = _appConfig.SerialPhotoelectricPortName;
                        int serialBaudRate = _appConfig.SerialPhotoelectricBaudRate;
                        int serialDebounceMs = _appConfig.SerialPhotoelectricDebounceMs;
                        int serialTimeoutMs = _appConfig.SerialPhotoelectricTimeoutMs;

                        if (root.TryGetProperty("TriggerSource", out var ts)) triggerSource = GetJsonEnumValue(ts, triggerSource);
                        if (root.TryGetProperty("SerialPhotoelectricPortName", out var spn)) serialPortName = GetJsonStringValue(spn, serialPortName);
                        if (root.TryGetProperty("SerialPhotoelectricBaudRate", out var sbr)) serialBaudRate = sbr.TryGetInt32(out int sbrVal) ? Math.Max(1200, sbrVal) : serialBaudRate;
                        if (root.TryGetProperty("SerialPhotoelectricDebounceMs", out var sdm)) serialDebounceMs = sdm.TryGetInt32(out int sdmVal) ? Math.Max(0, sdmVal) : serialDebounceMs;
                        if (root.TryGetProperty("SerialPhotoelectricTimeoutMs", out var stm)) serialTimeoutMs = stm.TryGetInt32(out int stmVal) ? Math.Max(100, stmVal) : serialTimeoutMs;
                        serialPortName = NormalizeSerialPortNameForSave(serialPortName);
                        if (triggerSource == TriggerSource.SerialPhotoelectric && string.IsNullOrWhiteSpace(serialPortName))
                        {
                            await _uiController.LogToFrontend("串口光电触发已保存，但 COM 口未配置，自动触发会暂不启动", "warning");
                        }

                        _appConfig.TriggerSource = triggerSource;
                        _appConfig.SerialPhotoelectricPortName = serialPortName;
                        _appConfig.SerialPhotoelectricBaudRate = serialBaudRate;
                        _appConfig.SerialPhotoelectricDebounceMs = serialDebounceMs;
                        _appConfig.SerialPhotoelectricTimeoutMs = serialTimeoutMs;

                        if (root.TryGetProperty("InspectionRuleSetJson", out var ruleSetJsonElement))
                        {
                            string ruleSetJson = ruleSetJsonElement.ValueKind == JsonValueKind.String
                                ? ruleSetJsonElement.GetString() ?? string.Empty
                                : ruleSetJsonElement.GetRawText();
                            if (!InspectionRuleSetSerializer.TryDeserialize(ruleSetJson, out InspectionRuleSet ruleSet, out string ruleSetError))
                            {
                                throw new InvalidOperationException($"判定规则配置无效: {ruleSetError}");
                            }

                            _appConfig.InspectionRuleSetJson = InspectionRuleSetSerializer.Serialize(ruleSet);
                        }

                        if (root.TryGetProperty("WireSequenceJudgeEnabled", out var wsEnabled)) _appConfig.WireSequenceJudgeEnabled = wsEnabled.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("WireSequenceExpectedLabels", out var wsLabels)) _appConfig.WireSequenceExpectedLabels = NormalizeWireSequenceLabelsForSave(GetJsonStringValue(wsLabels, _appConfig.WireSequenceExpectedLabels));
                        if (root.TryGetProperty("WireSequenceSortBy", out var wsSortBy)) _appConfig.WireSequenceSortBy = GetJsonStringValue(wsSortBy, _appConfig.WireSequenceSortBy);
                        if (root.TryGetProperty("WireSequenceDirection", out var wsDirection)) _appConfig.WireSequenceDirection = GetJsonStringValue(wsDirection, _appConfig.WireSequenceDirection);
                        if (root.TryGetProperty("WireSequenceExpectedCount", out var wsCount)) _appConfig.WireSequenceExpectedCount = wsCount.TryGetInt32(out int wsCountVal) ? Math.Clamp(wsCountVal, 0, 256) : _appConfig.WireSequenceExpectedCount;
                        if (root.TryGetProperty("WireSequenceMinConfidence", out var wsMinConfidence) && wsMinConfidence.TryGetDouble(out double wsMinConfidenceVal)) _appConfig.WireSequenceMinConfidence = Math.Clamp(wsMinConfidenceVal, 0d, 1d);
                        if (root.TryGetProperty("WireSequenceAllowMissing", out var wsAllowMissing)) _appConfig.WireSequenceAllowMissing = wsAllowMissing.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("WireSequenceAllowDuplicate", out var wsAllowDuplicate)) _appConfig.WireSequenceAllowDuplicate = wsAllowDuplicate.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("PlcProtocol", out var ppr)) plcProtocol = ppr.GetString() ?? plcProtocol;
                        if (root.TryGetProperty("PlcDriverProvider", out var pdp)) plcDriverProvider = pdp.GetString() ?? plcDriverProvider;
                        if (root.TryGetProperty("PlcProtocolMode", out var ppm)) plcProtocolMode = GetJsonEnumValue(ppm, plcProtocolMode);
                        if (root.TryGetProperty("PlcIp", out var pi)) plcIp = pi.GetString() ?? plcIp;
                        if (root.TryGetProperty("PlcPort", out var pp)) plcPort = pp.TryGetInt32(out int ppVal) ? ppVal : plcPort;
                        if (root.TryGetProperty("PlcTriggerAddress", out var pt)) plcTriggerAddress = GetJsonStringValue(pt, plcTriggerAddress);
                        if (root.TryGetProperty("PlcResultAddress", out var pr)) plcResultAddress = GetJsonStringValue(pr, plcResultAddress);
                        if (root.TryGetProperty("PlcTriggerSeqAddress", out var pts)) plcTriggerSeqAddress = GetJsonStringValue(pts, plcTriggerSeqAddress);
                        if (root.TryGetProperty("PlcResultSeqAddress", out var prs)) plcResultSeqAddress = GetJsonStringValue(prs, plcResultSeqAddress);
                        if (root.TryGetProperty("PlcVisionOnlineAddress", out var pvo)) plcVisionOnlineAddress = GetJsonStringValue(pvo, plcVisionOnlineAddress);
                        if (root.TryGetProperty("PlcVisionReadyAddress", out var pvr)) plcVisionReadyAddress = GetJsonStringValue(pvr, plcVisionReadyAddress);
                        if (root.TryGetProperty("PlcVisionBusyAddress", out var pvb)) plcVisionBusyAddress = GetJsonStringValue(pvb, plcVisionBusyAddress);
                        if (root.TryGetProperty("PlcInspectionDoneAddress", out var pid)) plcInspectionDoneAddress = GetJsonStringValue(pid, plcInspectionDoneAddress);
                        if (root.TryGetProperty("PlcErrorCodeAddress", out var pec)) plcErrorCodeAddress = GetJsonStringValue(pec, plcErrorCodeAddress);
                        if (root.TryGetProperty("PlcTraceSavedAddress", out var ptsa)) plcTraceSavedAddress = GetJsonStringValue(ptsa, plcTraceSavedAddress);
                        if (root.TryGetProperty("PlcHeartbeatAddress", out var phb)) plcHeartbeatAddress = GetJsonStringValue(phb, plcHeartbeatAddress);
                        if (root.TryGetProperty("PlcResetFaultAddress", out var prf)) plcResetFaultAddress = GetJsonStringValue(prf, plcResetFaultAddress);
                        if (root.TryGetProperty("PlcTriggerAckAddress", out var pta)) plcTriggerAckAddress = GetJsonStringValue(pta, plcTriggerAckAddress);
                        if (root.TryGetProperty("PlcResultValidAddress", out var prv)) plcResultValidAddress = GetJsonStringValue(prv, plcResultValidAddress);
                        if (root.TryGetProperty("PlcResultAckAddress", out var pra)) plcResultAckAddress = GetJsonStringValue(pra, plcResultAckAddress);
                        if (root.TryGetProperty("PlcResultAckTimeoutMs", out var prat)) plcResultAckTimeoutMs = prat.TryGetInt32(out int pratVal) ? Math.Clamp(pratVal, 0, 30000) : plcResultAckTimeoutMs;
                        if (root.TryGetProperty("PlcTriggerDelayMs", out var ptd)) plcTriggerDelayMs = ptd.TryGetInt32(out int ptdVal) ? Math.Max(0, ptdVal) : plcTriggerDelayMs;
                        if (root.TryGetProperty("PlcPollingIntervalMs", out var ppi)) plcPollingIntervalMs = ppi.TryGetInt32(out int ppiVal) ? Math.Max(50, ppiVal) : plcPollingIntervalMs;
                        if (root.TryGetProperty("PlcOkValue", out var pok)) plcOkValue = pok.TryGetInt16(out short pokVal) ? pokVal : plcOkValue;
                        if (root.TryGetProperty("PlcNgValue", out var png)) plcNgValue = png.TryGetInt16(out short pngVal) ? pngVal : plcNgValue;
                        if (root.TryGetProperty("PlcSiemensCpuModel", out var pscm)) plcSiemensCpuModel = pscm.GetString() ?? plcSiemensCpuModel;
                        if (root.TryGetProperty("PlcSiemensRack", out var psr)) plcSiemensRack = psr.TryGetInt32(out int psrVal) ? Math.Max(0, psrVal) : plcSiemensRack;
                        if (root.TryGetProperty("PlcSiemensSlot", out var pss)) plcSiemensSlot = pss.TryGetInt32(out int pssVal) ? Math.Max(0, pssVal) : plcSiemensSlot;
                        if (root.TryGetProperty("BarcodeEnabled", out var be)) barcodeEnabled = be.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("BarcodeAddress", out var ba)) barcodeAddress = GetJsonStringValue(ba, barcodeAddress);
                        if (root.TryGetProperty("BarcodeWordLength", out var bwl)) barcodeWordLength = bwl.TryGetInt32(out int bwlVal) ? Math.Clamp(bwlVal, 1, 64) : barcodeWordLength;
                        if (root.TryGetProperty("BarcodeEncoding", out var benc)) barcodeEncoding = benc.GetString() ?? barcodeEncoding;
                        if (root.TryGetProperty("BarcodeRequired", out var br)) barcodeRequired = br.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("CurrentOperatorId", out var coi)) currentOperatorId = GetJsonStringValue(coi, currentOperatorId);
                        if (root.TryGetProperty("CurrentOperatorRole", out var cor)) currentOperatorRole = GetJsonEnumValue(cor, currentOperatorRole);

                        bool shouldValidatePlcAddresses = triggerSource == TriggerSource.PLC;
                        PlcProtocolType plcProtocolType;
                        if (shouldValidatePlcAddresses)
                        {
                            if (!PlcFactory.TryParseProtocol(plcProtocol, out plcProtocolType))
                            {
                                throw new InvalidOperationException(
                                    $"PLC 协议无效: {plcProtocol}。支持: {string.Join(", ", Enum.GetNames<PlcProtocolType>())}");
                            }
                        }
                        else
                        {
                            plcProtocolType = PlcFactory.ParseProtocol(plcProtocol);
                        }

                        plcProtocol = plcProtocolType.ToString();
                        if (!PlcFactory.TryNormalizeDriverProvider(plcDriverProvider, out plcDriverProvider))
                        {
                            if (shouldValidatePlcAddresses)
                            {
                                throw new InvalidOperationException("PLC 驱动库仅支持 Hsl、HaoCommunication、McpX");
                            }

                            plcDriverProvider = "HaoCommunication";
                        }

                        bool isMitsubishiProtocol =
                            plcProtocolType == PlcProtocolType.Mitsubishi_MC_ASCII ||
                            plcProtocolType == PlcProtocolType.Mitsubishi_MC_Binary;

                        if (string.Equals(plcDriverProvider, "McpX", StringComparison.OrdinalIgnoreCase) && !isMitsubishiProtocol)
                        {
                            if (shouldValidatePlcAddresses)
                            {
                                throw new InvalidOperationException("仅三菱协议支持 McpX 驱动库");
                            }

                            plcDriverProvider = "HaoCommunication";
                        }

                        bool requiresHandshakeAddresses = shouldValidatePlcAddresses && plcProtocolMode == PlcProtocolMode.HandshakeV1;
                        plcTriggerAddress = shouldValidatePlcAddresses
                            ? NormalizeRequiredPlcAddressForSave(plcTriggerAddress, plcProtocolType, plcDriverProvider)
                            : NormalizeOptionalPlcAddressForSave(plcTriggerAddress, plcProtocolType, plcDriverProvider, 555, required: false);
                        plcResultAddress = shouldValidatePlcAddresses
                            ? NormalizeRequiredPlcAddressForSave(plcResultAddress, plcProtocolType, plcDriverProvider)
                            : NormalizeOptionalPlcAddressForSave(plcResultAddress, plcProtocolType, plcDriverProvider, 556, required: false);
                        plcTriggerSeqAddress = NormalizeOptionalPlcAddressForSave(plcTriggerSeqAddress, plcProtocolType, plcDriverProvider, 557, requiresHandshakeAddresses);
                        plcResultSeqAddress = NormalizeOptionalPlcAddressForSave(plcResultSeqAddress, plcProtocolType, plcDriverProvider, 558, requiresHandshakeAddresses);
                        plcVisionOnlineAddress = NormalizeOptionalPlcAddressForSave(plcVisionOnlineAddress, plcProtocolType, plcDriverProvider, 559, requiresHandshakeAddresses);
                        plcVisionReadyAddress = NormalizeOptionalPlcAddressForSave(plcVisionReadyAddress, plcProtocolType, plcDriverProvider, 560, requiresHandshakeAddresses);
                        plcVisionBusyAddress = NormalizeOptionalPlcAddressForSave(plcVisionBusyAddress, plcProtocolType, plcDriverProvider, 561, requiresHandshakeAddresses);
                        plcInspectionDoneAddress = NormalizeOptionalPlcAddressForSave(plcInspectionDoneAddress, plcProtocolType, plcDriverProvider, 562, requiresHandshakeAddresses);
                        plcErrorCodeAddress = NormalizeOptionalPlcAddressForSave(plcErrorCodeAddress, plcProtocolType, plcDriverProvider, 563, requiresHandshakeAddresses);
                        plcTraceSavedAddress = NormalizeOptionalPlcAddressForSave(plcTraceSavedAddress, plcProtocolType, plcDriverProvider, 564, requiresHandshakeAddresses);
                        plcHeartbeatAddress = NormalizeOptionalPlcAddressForSave(plcHeartbeatAddress, plcProtocolType, plcDriverProvider, 565, requiresHandshakeAddresses);
                        plcResetFaultAddress = NormalizeOptionalPlcAddressForSave(plcResetFaultAddress, plcProtocolType, plcDriverProvider, 566, requiresHandshakeAddresses);
                        plcTriggerAckAddress = NormalizeOptionalPlcAddressForSave(plcTriggerAckAddress, plcProtocolType, plcDriverProvider, 567, requiresHandshakeAddresses);
                        plcResultValidAddress = NormalizeOptionalPlcAddressForSave(plcResultValidAddress, plcProtocolType, plcDriverProvider, 568, requiresHandshakeAddresses);
                        plcResultAckAddress = NormalizeOptionalPlcAddressForSave(plcResultAckAddress, plcProtocolType, plcDriverProvider, 569, requiresHandshakeAddresses);
                        barcodeAddress = NormalizeOptionalPlcAddressForSave(barcodeAddress, plcProtocolType, plcDriverProvider, 570, shouldValidatePlcAddresses && barcodeEnabled);

                        _appConfig.PlcProtocol = plcProtocol;
                        _appConfig.PlcDriverProvider = plcDriverProvider;
                        _appConfig.PlcProtocolMode = plcProtocolMode;
                        _appConfig.PlcIp = plcIp;
                        _appConfig.PlcPort = plcPort;
                        _appConfig.PlcTriggerAddress = plcTriggerAddress;
                        _appConfig.PlcResultAddress = plcResultAddress;
                        _appConfig.PlcTriggerSeqAddress = plcTriggerSeqAddress;
                        _appConfig.PlcResultSeqAddress = plcResultSeqAddress;
                        _appConfig.PlcVisionOnlineAddress = plcVisionOnlineAddress;
                        _appConfig.PlcVisionReadyAddress = plcVisionReadyAddress;
                        _appConfig.PlcVisionBusyAddress = plcVisionBusyAddress;
                        _appConfig.PlcInspectionDoneAddress = plcInspectionDoneAddress;
                        _appConfig.PlcErrorCodeAddress = plcErrorCodeAddress;
                        _appConfig.PlcTraceSavedAddress = plcTraceSavedAddress;
                        _appConfig.PlcHeartbeatAddress = plcHeartbeatAddress;
                        _appConfig.PlcResetFaultAddress = plcResetFaultAddress;
                        _appConfig.PlcTriggerAckAddress = plcTriggerAckAddress;
                        _appConfig.PlcResultValidAddress = plcResultValidAddress;
                        _appConfig.PlcResultAckAddress = plcResultAckAddress;
                        _appConfig.PlcResultAckTimeoutMs = Math.Clamp(plcResultAckTimeoutMs, 0, 30000);
                        _appConfig.PlcTriggerDelayMs = plcTriggerDelayMs;
                        _appConfig.PlcPollingIntervalMs = plcPollingIntervalMs;
                        _appConfig.PlcOkValue = plcOkValue;
                        _appConfig.PlcNgValue = plcNgValue;
                        _appConfig.PlcSiemensCpuModel = string.IsNullOrWhiteSpace(plcSiemensCpuModel) ? "S1200" : plcSiemensCpuModel.Trim().ToUpperInvariant();
                        _appConfig.PlcSiemensRack = plcSiemensRack;
                        _appConfig.PlcSiemensSlot = plcSiemensSlot;
                        _appConfig.BarcodeEnabled = barcodeEnabled;
                        _appConfig.BarcodeAddress = barcodeAddress;
                        _appConfig.BarcodeWordLength = Math.Clamp(barcodeWordLength, 1, 64);
                        _appConfig.BarcodeEncoding = string.IsNullOrWhiteSpace(barcodeEncoding) ? "ASCII" : barcodeEncoding.Trim().ToUpperInvariant();
                        _appConfig.BarcodeRequired = barcodeRequired;
                        _appConfig.CurrentOperatorId = string.IsNullOrWhiteSpace(currentOperatorId) ? string.Empty : currentOperatorId.Trim();
                        _appConfig.CurrentOperatorRole = currentOperatorRole;
#pragma warning disable CS0618
                        var activeCamBefore = _appConfig.ActiveCamera;
                        string previousCameraId = activeCamBefore?.Id ?? string.Empty;
                        string previousSerialNumber = activeCamBefore?.SerialNumber?.Trim() ?? string.Empty;
                        string previousManufacturer = activeCamBefore?.Manufacturer?.Trim() ?? string.Empty;
                        string cameraPixelFormat = activeCamBefore?.PixelFormat ?? "Auto";
                        var activeCam = activeCamBefore;
                        if (root.TryGetProperty("CameraName", out var cn))
                        {
                            _appConfig.CameraName = cn.GetString()?.Trim() ?? _appConfig.CameraName;
                            if (activeCam != null) activeCam.DisplayName = _appConfig.CameraName;
                        }
                        if (root.TryGetProperty("CameraSerialNumber", out var cs))
                        {
                            _appConfig.CameraSerialNumber = cs.GetString()?.Trim() ?? _appConfig.CameraSerialNumber;
                            if (activeCam != null) activeCam.SerialNumber = _appConfig.CameraSerialNumber;
                        }
                        if (root.TryGetProperty("CameraManufacturer", out var cm))
                        {
                            _appConfig.CameraManufacturer = cm.GetString()?.Trim() ?? _appConfig.CameraManufacturer;
                            if (activeCam != null) activeCam.Manufacturer = _appConfig.CameraManufacturer;
                        }
                        if (root.TryGetProperty("CameraPixelFormat", out var cpf))
                        {
                            cameraPixelFormat = NormalizeCameraPixelFormatForSave(GetJsonStringValue(cpf, cameraPixelFormat));
                        }
                        if (root.TryGetProperty("ExposureTime", out var et))
                        {
                            _appConfig.ExposureTime = et.TryGetDouble(out double etVal) ? etVal : _appConfig.ExposureTime;
                            if (activeCam != null) activeCam.ExposureTime = _appConfig.ExposureTime;
                        }
                        if (root.TryGetProperty("GainRaw", out var gr))
                        {
                            _appConfig.GainRaw = gr.TryGetDouble(out double grVal) ? grVal : _appConfig.GainRaw;
                            if (activeCam != null) activeCam.Gain = _appConfig.GainRaw;
                        }
                        activeCam = _appConfig.EnsureActiveCameraConfigFromLegacy();
                        if (activeCam != null)
                        {
                            activeCam.PixelFormat = NormalizeCameraPixelFormatForSave(cameraPixelFormat);
                        }
                        bool cameraIdentityChanged = activeCam != null &&
                            (string.IsNullOrWhiteSpace(previousCameraId) ||
                             !string.Equals(previousCameraId, activeCam.Id, StringComparison.OrdinalIgnoreCase) ||
                             !string.Equals(previousSerialNumber, activeCam.SerialNumber?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                             !string.Equals(previousManufacturer, activeCam.Manufacturer?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                        SynchronizeActiveCameraRegistration(activeCam, cameraIdentityChanged);
#pragma warning restore CS0618
                        if (root.TryGetProperty("TargetLabel", out var tl)) _appConfig.TargetLabel = tl.GetString() ?? _appConfig.TargetLabel;
                        if (root.TryGetProperty("TargetCount", out var tc))
                        {
                            if (tc.TryGetInt32(out int tcVal))
                            {
                                if (tcVal < 0) throw new InvalidOperationException("目标数量不能为负数");
                                _appConfig.TargetCount = tcVal;
                            }
                        }
                        if (root.TryGetProperty("MaxRetryCount", out var mrc))
                        {
                            _appConfig.MaxRetryCount = mrc.TryGetInt32(out int mrcVal)
                                ? Math.Clamp(mrcVal, 0, 5)
                                : _appConfig.MaxRetryCount;
                        }
                        if (root.TryGetProperty("RetryIntervalMs", out var rim))
                        {
                            _appConfig.RetryIntervalMs = rim.TryGetInt32(out int rimVal)
                                ? Math.Clamp(rimVal, 0, 60000)
                                : _appConfig.RetryIntervalMs;
                        }
                        if (root.TryGetProperty("TaskType", out var taskType)) _appConfig.TaskType = taskType.TryGetInt32(out int taskTypeVal) ? taskTypeVal : _appConfig.TaskType;
                        if (root.TryGetProperty("Confidence", out var conf) && conf.TryGetDouble(out double confVal)) _appConfig.Confidence = (float)Math.Clamp(confVal, 0d, 1d);
                        if (root.TryGetProperty("IouThreshold", out var iou) && iou.TryGetDouble(out double iouVal)) _appConfig.IouThreshold = (float)Math.Clamp(iouVal, 0d, 1d);
                        if (root.TryGetProperty("EnableGpu", out var eg)) _appConfig.EnableGpu = eg.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("GpuIndex", out var gpuIndex))
                        {
                            _appConfig.GpuIndex = gpuIndex.TryGetInt32(out int gpuIndexVal)
                                ? Math.Max(0, gpuIndexVal)
                                : _appConfig.GpuIndex;
                        }
                        if (root.TryGetProperty("IndustrialRenderMode", out var irm)) _appConfig.IndustrialRenderMode = irm.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("UseFileBackedWebImageTransport", out var fileTransport)) _appConfig.UseFileBackedWebImageTransport = fileTransport.ValueKind == JsonValueKind.True;
                        YoloDetector.IndustrialRenderMode = _appConfig.IndustrialRenderMode;
                        _uiController.UseFileBackedImageTransport = _appConfig.UseFileBackedWebImageTransport;
                        _detectionService.SetTaskMode(_appConfig.TaskType);

                        // 保存并重新加载
                        if (!_appConfig.Save())
                        {
                            throw new InvalidOperationException(_appConfig.LastError ?? "配置保存失败");
                        }
                        configSaved = true;
                        _appRuntime.RefreshStoragePath();
                        SaveCurrentRecipeSnapshot("系统设置保存");

                        // 更新相关路径
                        _uiController.ImageBasePath = Path_Images;
                        _uiController.LogBasePath = Path_Logs;
                        InitDirectories();
                        _uiController.SetImageMapping(Path_Images);

                        // 重新初始化YOLO(如果GPU设置改变)
                        InitYolo();
                        RefreshStartupDiagnostics();

                        // 根据 TriggerSource 切换触发源；使用协调器避免在未运行状态下自动连接 PLC
                        await RestartTriggerSourceAfterConfigurationChangeAsync("系统设置保存");

                        await _uiController.SendUiCommand("closeSettingsModal");
                        await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");
                        await _uiController.InitSettings(_appConfig);
                        await SendHealthSnapshotToFrontendAsync();
                        await _uiController.LogToFrontend("? 系统设置已更新", "success");
                    }
                }
                catch (Exception ex)
                {
                    if (!configSaved)
                    {
                        _appConfig.CopyFrom(previousConfig);
                    }

                    await _uiController.InitSettings(_appConfig);
                    await _uiController.SendUiCommand("alert", new { message = $"保存失败: {ex.Message}" });
                }
            };

            // 订阅选择文件夹事件
            _uiController.OnSelectStorageFolder += (sender, e) =>
            {
                InvokeOnUIThread(async () =>
                {
                    if (!await EnsureRuntimeMutationAllowedAsync("数据保存路径修改")) return;
                    using (var fbd = new FolderBrowserDialog())
                    {
                        fbd.Description = "选择数据存储根目录";
                        fbd.UseDescriptionForTitle = true;
                        // fbd.ShowNewFolderButton = true; // Default is true
                        if (Directory.Exists(_appConfig.StoragePath))
                            fbd.SelectedPath = _appConfig.StoragePath;

                        if (fbd.ShowDialog() == DialogResult.OK)
                        {
                            string path = fbd.SelectedPath;
                            await _uiController.UpdateStoragePathInUI(path);
                        }
                    }
                });
            };

            // 模型加载与 WebView2 初始化并行，减少冷启动等待时间
            Task initYoloTask = InitYoloAsync();

            // 初始化 WebUI
            if (webView21 != null)
            {
                await _uiController.InitializeAsync(webView21);
                // 配置 NG 图片查看路径
                _uiController.ImageBasePath = Path_Images;
                _uiController.UseFileBackedImageTransport = _appConfig.UseFileBackedWebImageTransport;
                _uiController.SetImageMapping(Path_Images);
                // 配置检测日志路径
                _uiController.LogBasePath = Path_Logs;
            }

            await initYoloTask;

            // 统计数据已由 _statisticsService 在构造时加载
            // 检测跨日，如果需要则保存历史并重置今日数据
            bool isNewDay = _statisticsService.CheckAndResetForNewDay();
            if (isNewDay)
            {
                SafeFireAndForget(_uiController.LogToFrontend("检测到新的一天，统计数据已重置", "info"), "日志记录");
            }

            InitDirectories();

            // 启动后台清理
            StartCleanupTask();
            StartEmergencyCleanupMonitor();

            // 触发监听在相机打开成功后启动，避免软件刚启动时现场信号误入检测链路。
        }

        private async Task ExportConfigMigrationAsync()
        {
            try
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "导出配置迁移文件",
                    FileName = $"ClearFrost_Config_{DateTime.Now:yyyyMMdd_HHmmss}.clearfrost-config.json",
                    Filter = "ClearFrost 配置迁移 (*.clearfrost-config.json)|*.clearfrost-config.json|JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                    DefaultExt = "clearfrost-config.json",
                    AddExtension = true,
                    OverwritePrompt = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                string appVersion = AppVersion.InformationalVersion;
                ConfigMigrationExportResult result = ConfigMigrationService.Export(_appConfig, dialog.FileName, appVersion);
                await _uiController.LogToFrontend($"配置迁移文件已导出: {result.Path}", "success");
                await _uiController.SendUiCommand("toast", new
                {
                    message = $"已导出配置迁移文件，包含 {result.PresetCount} 个项目预设",
                    type = "success",
                    durationMs = 2200
                });
            }
            catch (Exception ex)
            {
                await _uiController.SendUiCommand("alert", new { message = $"导出配置迁移失败: {ex.Message}" });
            }
        }

        private async Task ImportConfigMigrationAsync()
        {
            try
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "导入配置迁移文件",
                    Filter = "ClearFrost 配置迁移 (*.clearfrost-config.json)|*.clearfrost-config.json|JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                ConfigMigrationImportPreview preview = ConfigMigrationService.PreviewImport(dialog.FileName);
                DialogResult confirmResult = MessageBox.Show(
                    this,
                    BuildConfigMigrationImportConfirmText(dialog.FileName, preview),
                    "导入配置迁移",
                    MessageBoxButtons.OKCancel,
                    preview.HasConfig ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                if (confirmResult != DialogResult.OK)
                {
                    return;
                }

                ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(dialog.FileName, _appConfig);
                bool refreshSucceeded = true;
                try
                {
                    await RefreshAfterConfigMigrationImportAsync(result);
                }
                catch (Exception refreshEx)
                {
                    refreshSucceeded = false;
                    await _uiController.LogToFrontend($"配置已导入，但刷新运行状态失败: {refreshEx.Message}", "warning");
                    await _uiController.SendUiCommand("alert", new
                    {
                        message = $"配置迁移已写入，但刷新运行状态失败。建议重启软件后确认相机、模型和触发源状态。\n\n{refreshEx.Message}"
                    });
                }

                await _uiController.LogToFrontend(
                    BuildConfigMigrationImportLogText(result),
                    refreshSucceeded ? "success" : "warning");
                await _uiController.SendUiCommand("toast", new
                {
                    message = refreshSucceeded
                        ? (result.HasConfig ? "配置迁移导入完成，运行参数已覆盖" : "项目预设导入完成")
                        : "配置已导入，刷新状态需要人工确认",
                    type = refreshSucceeded ? "success" : "warning",
                    durationMs = 2600
                });
            }
            catch (Exception ex)
            {
                await _uiController.SendUiCommand("alert", new { message = $"导入配置迁移失败: {ex.Message}" });
            }
        }

        private string BuildConfigMigrationImportConfirmText(string filePath, ConfigMigrationImportPreview preview)
        {
            string kindText = preview.Kind switch
            {
                ConfigMigrationImportKind.MigrationPackage => "ClearFrost 配置迁移包",
                ConfigMigrationImportKind.AppConfig => "普通 config.json",
                ConfigMigrationImportKind.ProjectPresets => "项目预设文件",
                _ => "配置文件"
            };
            string configText = preview.HasConfig ? "覆盖当前运行配置" : "不修改当前运行配置";
            string presetText = preview.HasPresets
                ? $"合并 {preview.PresetCount} 个项目预设，同 id 以导入文件为准"
                : "不导入项目预设";
            string versionText = string.IsNullOrWhiteSpace(preview.SourceAppVersion)
                ? ""
                : $"\n来源版本: {preview.SourceAppVersion}";

            return
                $"文件: {Path.GetFileName(filePath)}\n" +
                $"类型: {kindText}{versionText}\n\n" +
                $"{configText}\n" +
                $"{presetText}\n\n" +
                "导入后会刷新设置页、相机列表、触发源和模型状态。是否继续？";
        }

        private static string BuildConfigMigrationImportLogText(ConfigMigrationImportResult result)
        {
            string configText = result.HasConfig ? $"运行配置已写入 {result.RuntimeConfigPath}" : "运行配置未修改";
            string presetText = result.HasPresets ? $"项目预设已合并 {result.PresetCount} 个" : "未导入项目预设";
            return $"配置迁移导入完成: {configText}; {presetText}";
        }

        private async Task RefreshAfterConfigMigrationImportAsync(ConfigMigrationImportResult result)
        {
            if (result.HasConfig)
            {
                try
                {
                    _cameraService.StopCapture();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ConfigMigration] StopCapture before camera reload failed: {ex.Message}");
                }

                _cameraManager.ReloadFromConfig(_appConfig);
                CameraInstance? activeCamera = _cameraManager.ActiveCamera;
                cam = activeCamera?.Camera ?? new RealCamera();

                SaveCurrentRecipeSnapshot("配置迁移导入");
                YoloDetector.IndustrialRenderMode = _appConfig.IndustrialRenderMode;
                _uiController.UseFileBackedImageTransport = _appConfig.UseFileBackedWebImageTransport;
                _detectionService.SetTaskMode(_appConfig.TaskType);
                _appRuntime.RefreshStoragePath();

                _uiController.ImageBasePath = Path_Images;
                _uiController.LogBasePath = Path_Logs;
                InitDirectories();
                _uiController.SetImageMapping(Path_Images);

                模型名 = _appConfig.CurrentModelFileName?.Trim() ?? string.Empty;
                await WarnMissingImportedModelFilesAsync();
                InitYolo();
                RefreshStartupDiagnostics();
                await RestartTriggerSourceAfterConfigurationChangeAsync("配置迁移导入");

                await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");
                await _uiController.InitSettings(_appConfig);
                await _uiController.SendModelList(GetModelListPayload());
            }

            await _uiController.SendProjectPresets(ProjectPresetStore.Load());
            await SendConfiguredCameraListToFrontendAsync();
            await SendHealthSnapshotToFrontendAsync();
        }

        private async Task WarnMissingImportedModelFilesAsync()
        {
            try
            {
                _appRuntime.RefreshModelRegistry();
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"模型注册表刷新失败: {ex.Message}", "warning");
                return;
            }

            var slots = new[]
            {
                ("主模型", _appConfig.CurrentModelReference, _appConfig.CurrentModelFileName, false),
                ("辅助模型1", _appConfig.Auxiliary1ModelReference, _appConfig.Auxiliary1ModelPath, true),
                ("辅助模型2", _appConfig.Auxiliary2ModelReference, _appConfig.Auxiliary2ModelPath, true)
            };

            foreach (var slot in slots)
            {
                ProductionModelResolutionResult resolution = slot.Item2 != null && !slot.Item2.IsEmpty
                    ? _modelRegistry.ResolveReference(slot.Item2, _appConfig.RequireApprovedModelsForProduction)
                    : _modelRegistry.MigrateLegacyReference(slot.Item3, _appConfig.RequireApprovedModelsForProduction);
                if (!resolution.Succeeded && !(slot.Item4 && string.IsNullOrWhiteSpace(slot.Item3)))
                {
                    await _uiController.LogToFrontend(
                        $"导入配置引用的{slot.Item1}不可用: [{resolution.ErrorCode}] {resolution.Message}",
                        "warning");
                }
            }
        }

        private Task SendConfiguredCameraListToFrontendAsync()
        {
            object[] cameras = _appConfig.Cameras.Select(c => new
            {
                id = c.Id,
                displayName = c.DisplayName,
                serialNumber = c.SerialNumber,
                manufacturer = c.Manufacturer,
                pixelFormat = c.PixelFormat,
                exposureTime = c.ExposureTime,
                gain = c.Gain
            }).Cast<object>().ToArray();

            return _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
        }

        /// <summary>
        /// 启动系统：先连接相机，再连接 PLC / 启动当前触发源。
        /// </summary>
        private async Task StartSystemAsync()
        {
            if (IsShutdownInProgress)
            {
                await _uiController.LogToFrontend("软件正在退出，已忽略启动系统请求", "warning");
                return;
            }

            if (!await _appRuntime.ReplayCoordinator.TryBeginProductionAsync(_appShutdownCts.Token).ConfigureAwait(false))
            {
                await _uiController.SendUiCommand("toast", new
                {
                    message = _appRuntime.ReplayCoordinator.IsReplayRunning ? "Replay 运行中，已拒绝启动检测" : "系统已在检测中",
                    type = "warning",
                    durationMs = 1200
                });
                await SendSystemRunningStateAsync(IsProductionRunning);
                return;
            }

            await SendSystemRunningStateAsync(false, isBusy: true);

            try
            {
                await RefreshRuntimeModelStateAsync(loadDefaultModelIfMissing: true, pushModelList: true);
                ProductionModelReadinessResult modelReadiness = _modelActivationService.EnsureReadyForProduction();
                if (!modelReadiness.Succeeded)
                {
                    string message = $"启动系统已停止: 生产模型未就绪 [{modelReadiness.ErrorCode}] {modelReadiness.Message}";
                    RecordHealthError("ProductionModel", message);
                    await _uiController.LogToFrontend(message, "error");
                    await SendHealthSnapshotToFrontendAsync();
                    await MarkSystemStoppedAsync();
                    return;
                }

                if (!await EnsureStartupReadyForProductionAsync("启动系统"))
                {
                    await SendHealthSnapshotToFrontendAsync();
                    await MarkSystemStoppedAsync();
                    return;
                }

                if (!_detectionService.IsModelLoaded)
                {
                    string message = "启动系统已停止: 没有可用的检测模型，请检查 ONNX 模型文件是否能正常加载";
                    RecordHealthError("Detection", message);
                    await _uiController.LogToFrontend(message, "error");
                    await SendHealthSnapshotToFrontendAsync();
                    await MarkSystemStoppedAsync();
                    return;
                }

                await _uiController.LogToFrontend("启动系统: 正在连接相机...", "info");
                bool cameraStarted = await btnOpenCamera_LogicAsync(startTriggerSource: false);

                if (!cameraStarted)
                {
                    await _uiController.LogToFrontend("启动系统已停止: 相机未连接成功", "warning");
                    await MarkSystemStoppedAsync();
                    return;
                }

                if (IsShutdownInProgress)
                {
                    await MarkSystemStoppedAsync();
                    return;
                }

                var cameraReady = await WaitForCameraReadyForInspectionAsync();
                if (!cameraReady.Ready)
                {
                    await _uiController.LogToFrontend(
                        $"启动系统已停止: 相机已连接但未进入采集状态，{cameraReady.Message}",
                        "warning");
                    await SendHealthSnapshotToFrontendAsync();
                    await MarkSystemStoppedAsync();
                    return;
                }

                await _uiController.LogToFrontend("启动系统: 正在启动触发源...", "info");
                if (!await StartTriggerSourceAsync())
                {
                    await _uiController.LogToFrontend("启动系统已停止: 触发源未启动成功", "error");
                    await _uiController.SendUiCommand("toast", new
                    {
                        message = "触发源启动失败，检测未启动",
                        type = "error",
                        durationMs = 2200
                    });
                    await MarkSystemStoppedAsync();
                    return;
                }

                await _uiController.LogToFrontend("启动系统完成，检测已启动", "success");
                await _uiController.SendUiCommand("toast", new
                {
                    message = "检测已启动",
                    type = "success",
                    durationMs = 1400
                });
                await SendSystemRunningStateAsync(true);
            }
            catch (Exception ex)
            {
                RecordHealthError("Startup", $"启动系统异常: {ex.Message}");
                await _uiController.LogToFrontend($"启动系统异常: {ex.Message}", "error");
                await _uiController.SendUiCommand("toast", new
                {
                    message = "启动系统异常，已停止",
                    type = "error",
                    durationMs = 2200
                });
                await SendHealthSnapshotToFrontendAsync();
                await MarkSystemStoppedAsync();
            }
        }

        private async Task StopSystemAsync()
        {
            if (IsShutdownInProgress)
            {
                await _uiController.LogToFrontend("软件正在退出，已忽略停止检测请求", "warning");
                return;
            }

            if (!IsProductionRunning)
            {
                await _uiController.SendUiCommand("toast", new
                {
                    message = "当前未在检测",
                    type = "warning",
                    durationMs = 1200
                });
                await SendSystemRunningStateAsync(false);
                return;
            }

            try
            {
                await SendSystemRunningStateAsync(true, isBusy: true);
                await _uiController.LogToFrontend("停止检测: 正在停止触发监听和相机采集...", "info");

                try
                {
                    _plcService.StopMonitoring();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StopSystem] Stop PLC monitoring failed: {ex.Message}");
                    await _uiController.LogToFrontend($"停止 PLC 监听失败: {ex.Message}", "warning");
                }

                try
                {
                    _serialTriggerService.Stop();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StopSystem] Stop serial trigger failed: {ex.Message}");
                    await _uiController.LogToFrontend($"停止串口光电监听失败: {ex.Message}", "warning");
                }

                try
                {
                    _cameraService.StopCapture();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StopSystem] Stop camera capture failed: {ex.Message}");
                    await _uiController.LogToFrontend($"停止相机采集失败: {ex.Message}", "warning");
                }

                await SendHealthSnapshotToFrontendAsync();
                await _uiController.LogToFrontend("检测已停止", "success");
                await _uiController.SendUiCommand("toast", new
                {
                    message = "检测已停止",
                    type = "success",
                    durationMs = 1400
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StopSystem] Stop sequence failed: {ex.Message}");
                await _uiController.LogToFrontend($"停止检测流程异常: {ex.Message}", "warning");
            }
            finally
            {
                _appRuntime.ReplayCoordinator.EndProduction();
                await SendSystemRunningStateAsync(false);
            }
        }

        private Task MarkSystemStoppedAsync()
        {
            _appRuntime.ReplayCoordinator.EndProduction();
            return SendSystemRunningStateAsync(false);
        }

        private Task SendSystemRunningStateAsync(bool isRunning, bool isBusy = false)
        {
            return _uiController.SendUiCommand("setSystemRunning", new
            {
                isRunning,
                isBusy
            });
        }

        private bool IsRuntimeMutationBlocked(
            string operation,
            ProductionOperation requiredOperation = ProductionOperation.EngineeringChange)
        {
            if (!ProductionAuthorizationService.Authorize(_appConfig.CurrentOperatorRole, requiredOperation, out string denialReason))
            {
                SafeFireAndForget(
                    ReportUnauthorizedOperationAsync(operation, requiredOperation, denialReason),
                    $"权限阻止{operation}");
                return true;
            }

            if (!IsProductionRunning)
            {
                return false;
            }

            SafeFireAndForget(ReportRuntimeMutationBlockedAsync(operation), $"运行中阻止{operation}");
            return true;
        }

        private async Task<bool> EnsureRuntimeMutationAllowedAsync(
            string operation,
            ProductionOperation requiredOperation = ProductionOperation.EngineeringChange)
        {
            if (!await EnsureProductionOperationAuthorizedAsync(operation, requiredOperation))
            {
                return false;
            }

            if (!IsProductionRunning)
            {
                return true;
            }

            await ReportRuntimeMutationBlockedAsync(operation);
            return false;
        }

        private async Task ReportRuntimeMutationBlockedAsync(string operation)
        {
            string message = $"检测运行中，已阻止{operation}。请先停止检测。";
            RecordHealthError("RuntimeConfigLock", message);
            await _uiController.LogToFrontend(message, "warning");
            await _uiController.SendUiCommand("toast", new
            {
                message,
                type = "warning",
                durationMs = 2200
            });
            await SendHealthSnapshotToFrontendAsync();
        }

        private async Task<bool> EnsureProductionOperationAuthorizedAsync(
            string operation,
            ProductionOperation requiredOperation)
        {
            if (ProductionAuthorizationService.Authorize(_appConfig.CurrentOperatorRole, requiredOperation, out string denialReason))
            {
                return true;
            }

            await ReportUnauthorizedOperationAsync(operation, requiredOperation, denialReason);
            return false;
        }

        private async Task<bool> EnsureReplayWriteAuthorizedAsync(
            WebUiCommandEventArgs args,
            string operation,
            Func<Task> sendUnauthorizedResponseAsync)
        {
            if (ProductionAuthorizationService.Authorize(
                    _appConfig.CurrentOperatorRole,
                    ProductionOperation.EngineeringChange,
                    out string denialReason))
            {
                return true;
            }

            await ReportUnauthorizedOperationAsync(
                operation,
                ProductionOperation.EngineeringChange,
                denialReason).ConfigureAwait(false);
            await sendUnauthorizedResponseAsync().ConfigureAwait(false);
            return false;
        }

        private async Task ReportUnauthorizedOperationAsync(
            string operation,
            ProductionOperation requiredOperation,
            string denialReason)
        {
            ProductionRole requiredRole = ProductionAuthorizationService.GetRequiredRole(requiredOperation);
            string message = $"{operation}已拒绝: {denialReason}";
            RecordHealthError("Authorization", message);
            await _operationAuditService.AppendAsync(new OperationAuditRecord
            {
                Operation = operation,
                Status = OperationAuditStatus.Denied,
                OperatorId = ResolveCurrentOperatorId(),
                Role = _appConfig.CurrentOperatorRole,
                Details = message,
                FailureBlocker = $"RequiredRole={requiredRole}"
            }).ConfigureAwait(false);

            await _uiController.LogToFrontend(message, "error");
            await _uiController.SendUiCommand("toast", new
            {
                message,
                type = "error",
                durationMs = 2200
            });
            await SendHealthSnapshotToFrontendAsync();
        }

        /// <summary>
        /// 根据 TriggerSource 启动对应触发源
        /// </summary>
        private async Task<bool> StartTriggerSourceAsync()
        {
            if (_appConfig.TriggerSource == TriggerSource.Manual)
            {
                _plcService.StopMonitoring();
                _serialTriggerService.Stop();
                await _uiController.LogToFrontend("手动检测模式已启用：自动生产触发未启动，可使用手动拍照检测", "info");
                return true;
            }

            if (_appConfig.TriggerSource == TriggerSource.SerialPhotoelectric)
            {
                _plcService.StopMonitoring();

                var cameraReady = await WaitForCameraReadyForInspectionAsync();
                if (!cameraReady.Ready)
                {
                    await _uiController.LogToFrontend(
                        $"串口光电触发继续启动，相机暂未就绪；触发时将自动恢复相机: {cameraReady.Message}",
                        "warning");
                }

                if (!string.IsNullOrWhiteSpace(_appConfig.SerialPhotoelectricPortName))
                {
                    bool ok = await _serialTriggerService.StartAsync(
                        _appConfig.SerialPhotoelectricPortName,
                        _appConfig.SerialPhotoelectricBaudRate,
                        _appConfig.SerialPhotoelectricDebounceMs,
                        _appConfig.SerialPhotoelectricTimeoutMs);
                    if (ok)
                    {
                        await _uiController.LogToFrontend($"串口光电已启动: {_appConfig.SerialPhotoelectricPortName}", "success");
                    }
                    else
                    {
                        string err = _serialTriggerService.LastError ?? "未知错误";
                        RecordHealthError("SerialTrigger", $"串口光电启动失败: {err}");
                        await _uiController.LogToFrontend($"串口光电启动失败: {err}", "error");
                        return false;
                    }
                }
                else
                {
                    await _uiController.LogToFrontend("串口光电 COM 口未配置，跳过自动启动", "warning");
                    RecordHealthError("SerialTrigger", "串口光电 COM 口未配置");
                    return false;
                }

                await _uiController.LogToFrontend("串口光电触发模式已跳过 PLC 连接、监听和写回", "info");
                return true;
            }

            _serialTriggerService.Stop();
            return await StartPlcTriggerMonitoringIfReadyAsync();
        }

        /// <summary>
        /// 比较两个模型文件名是否指向同一文件（忽略 .onnx 后缀与大小写）。
        /// 用于防止辅助模型与主模型/另一辅助模型设置重复。
        /// </summary>
        private static bool IsSameModelFile(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            if (ProductionModelReference.TryParseSelectionValue(a, out ProductionModelReference left) &&
                ProductionModelReference.TryParseSelectionValue(b, out ProductionModelReference right) &&
                !left.IsEmpty &&
                !right.IsEmpty)
            {
                return left.IdentityEquals(right);
            }

            string nameA = Path.GetFileNameWithoutExtension(a.Trim());
            string nameB = Path.GetFileNameWithoutExtension(b.Trim());
            return string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        }

        private async Task InitModelList()
        {
            await _uiController.LogToFrontend("开始加载模型列表...");

            await RefreshRuntimeModelStateAsync(loadDefaultModelIfMissing: true, pushModelList: true);
            object[] names = GetModelListPayload();
            await _uiController.LogToFrontend($"找到 {names.Length} 个可选模型");

            await _uiController.LogToFrontend($"? 已通过 SendModelList 推送 {names.Length} 个模型");
        }

        private async Task RefreshRuntimeModelStateAsync(bool loadDefaultModelIfMissing, bool pushModelList)
        {
            object[] names = GetModelListPayload();
            if (pushModelList)
            {
                await _uiController.SendModelList(names);
            }

            if (loadDefaultModelIfMissing &&
                !_detectionService.IsModelLoaded &&
                names.Length > 0)
            {
                await _uiController.LogToFrontend("检测到可用模型配置，正在按 AppConfig 引用加载", "info");
                await InitYoloAsync();
            }

            RefreshStartupDiagnostics();
        }

        private object[] GetModelListPayload()
        {
            try
            {
                return _modelActivationService.GetSelectionOptions()
                    .Select(option => new
                    {
                        value = option.Value,
                        text = option.Text,
                        modelId = option.ModelId,
                        version = option.Version,
                        sha256 = option.Sha256,
                        fileName = option.FileName,
                        isApprovedPackage = option.IsApprovedPackage
                    })
                    .Cast<object>()
                    .ToArray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelList] 获取模型列表失败: {ex.Message}");
                return Array.Empty<object>();
            }
        }

        private void InitDirectories()
        {
            if (!Directory.Exists(Path_Logs)) Directory.CreateDirectory(Path_Logs);
            if (!Directory.Exists(Path_Images)) Directory.CreateDirectory(Path_Images);
            if (!Directory.Exists(Path_System)) Directory.CreateDirectory(Path_System);
        }

        private void StartCleanupTask()
        {
            Task.Run(async () =>
            {
                while (!停止)
                {
                    _storageService?.CleanOldData(30);
                    await Task.Delay(TimeSpan.FromHours(24));
                }
            });
        }

        private void StartEmergencyCleanupMonitor()
        {
            Task.Run(async () =>
            {
                DateTime lastCleanupTime = DateTime.MinValue;
                TimeSpan cooldown = TimeSpan.FromHours(2);
                TimeSpan checkInterval = TimeSpan.FromHours(1);
                const double thresholdGb = 1.0;

                while (!停止)
                {
                    await Task.Delay(checkInterval);

                    try
                    {
                        double freeGb = _storageService?.GetDiskFreeSpaceGb() ?? 999;
                        if (freeGb < thresholdGb && DateTime.Now - lastCleanupTime > cooldown)
                        {
                            double afterGb = _storageService?.PerformEmergencyCleanup() ?? freeGb;
                            lastCleanupTime = DateTime.Now;

                            if (afterGb < thresholdGb)
                            {
                                SafeFireAndForget(_uiController.LogToFrontend(
                                    $"磁盘空间严重不足：紧急清理后仍仅剩 {afterGb:F2} GB，请立即人工处理", "error"), "紧急清理告警");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[EmergencyCleanupMonitor] 异常: {ex.Message}");
                    }
                }
            });
        }

        protected void OnFormClosingHandler(object? sender, FormClosingEventArgs e)
        {
            // 如果已经在 OnExitApp 中完成了清理，直接放行
            // （Application.Exit 触发 FormClosing 时 CloseReason=ApplicationExitCall）
            if (e.CloseReason == CloseReason.ApplicationExitCall) return;

            if (IsShutdownInProgress)
            {
                e.Cancel = true;
                return;
            }

            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                BeginAppShutdown($"FormClosing.{e.CloseReason}");
                return;
            }

            e.Cancel = true;
            BeginAppShutdown($"FormClosing.{e.CloseReason}");
        }

        private void BeginAppShutdown(string source)
        {
            if (Interlocked.CompareExchange(ref _shutdownState, 1, 0) != 0)
            {
                Debug.WriteLine($"[Shutdown] 忽略重复退出请求: {source}");
                return;
            }

            Debug.WriteLine($"[Shutdown] 开始退出流程: {source}");

            try
            {
                _storageService?.WriteStartupLog("软件关闭", null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 记录关闭日志失败: {ex.Message}");
            }

            this.停止 = true;

            try
            {
                _appShutdownCts.Cancel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 取消后台任务失败: {ex.Message}");
            }

            try
            {
                _plcService?.StopMonitoring();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 停止 PLC 监控失败: {ex.Message}");
            }

            try
            {
                _serialTriggerService?.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 停止串口光电监听失败: {ex.Message}");
            }

            try
            {
                WindowHelpers.RestoreSleep();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 恢复休眠策略失败: {ex.Message}");
            }

            try
            {
                _statisticsService?.SaveAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 保存统计失败: {ex.Message}");
            }

            SafeFireAndForget(_uiController.LogToFrontend("正在安全退出，请稍候...", "info"), "退出提示");

            lock (_shutdownTaskSync)
            {
                _shutdownTask ??= Task.Run(() => ShutdownCleanupCore(source));
            }

            _ = MonitorShutdownAsync(source);
        }

        private void ShutdownCleanupCore(string source)
        {
            Debug.WriteLine($"[Shutdown] 后台清理开始: {source}");

            try
            {
                _appConfig?.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 保存配置失败: {ex.Message}");
            }

            try
            {
                Debug.WriteLine(
                    $"[Shutdown] 清理前队列状态: Images={_imageSaveQueue.PendingCount}/{_imageSaveQueue.Capacity}, " +
                    $"ImageBuffer={FormatBytes(_imageSaveQueue.PendingBytes)}/{FormatBytes(_imageSaveQueue.MaxBufferedBytes)}, " +
                    $"ImageDropped={_imageSaveQueue.DroppedCount}, ImageFailed={_imageSaveQueue.FailedCount}, " +
                    $"Records={_detectionRecordQueue.PendingCount}/{_detectionRecordQueue.Capacity}, " +
                    $"RecordDropped={_detectionRecordQueue.DroppedCount}, RecordFailed={_detectionRecordQueue.FailedCount}");

                _appRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (_serialTriggerService is IDisposable serialDisposable)
                {
                    serialDisposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 运行时清理失败: {ex.Message}");
            }
            finally
            {
                FlushDiagnosticLog();
            }

            Debug.WriteLine($"[Shutdown] 后台清理完成: {source}");
        }

        private async Task MonitorShutdownAsync(string source)
        {
            Task cleanupTask;

            lock (_shutdownTaskSync)
            {
                cleanupTask = _shutdownTask ?? Task.CompletedTask;
            }

            try
            {
                await cleanupTask.WaitAsync(_shutdownTimeout);
                Debug.WriteLine($"[Shutdown] 清理完成，准备退出: {source}");
                RequestGracefulExit(source);
            }
            catch (TimeoutException)
            {
                Debug.WriteLine($"[Shutdown] 清理超时，尝试退出并保留最终兜底: {source}");
                Debug.WriteLine(
                    $"[Shutdown] 超时时队列状态: Images={_imageSaveQueue.PendingCount}/{_imageSaveQueue.Capacity}, " +
                    $"ImageBuffer={FormatBytes(_imageSaveQueue.PendingBytes)}/{FormatBytes(_imageSaveQueue.MaxBufferedBytes)}, " +
                    $"ImageDropped={_imageSaveQueue.DroppedCount}, ImageFailed={_imageSaveQueue.FailedCount}, " +
                    $"Records={_detectionRecordQueue.PendingCount}/{_detectionRecordQueue.Capacity}, " +
                    $"RecordDropped={_detectionRecordQueue.DroppedCount}, RecordFailed={_detectionRecordQueue.FailedCount}");

                try
                {
                    _storageService?.WriteStartupLog($"软件关闭超时强退[{source}]", null);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Shutdown] 记录强退日志失败: {ex.Message}");
                }

                RequestGracefulExit(source, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 监控退出流程异常: {ex.Message}");
                RequestGracefulExit(source);
            }
        }

        private void RequestGracefulExit(string source, TimeSpan? forceAfter = null)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Debug.WriteLine($"[Shutdown] Application.Exit: {source}");
                        Application.Exit();
                    });

                    if (forceAfter.HasValue)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(forceAfter.Value);
                            Environment.Exit(0);
                        });
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 请求 UI 线程退出失败: {ex.Message}");
            }

            Environment.Exit(0);
        }

        private void SynchronizeActiveCameraRegistration(CameraConfig? activeConfig, bool recreateExisting)
        {
            if (activeConfig == null)
            {
                return;
            }

            CameraInstance? registeredCamera = _cameraManager.GetCamera(activeConfig.Id);
            if (registeredCamera != null && recreateExisting)
            {
                try
                {
                    _cameraService.StopCapture();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CameraSync] StopCapture before re-register failed: {ex.Message}");
                }

                _cameraManager.RemoveCamera(activeConfig.Id);
                registeredCamera = null;
            }

            if (registeredCamera == null)
            {
                _cameraManager.AddCamera(activeConfig);
            }

            _cameraManager.ActiveCameraId = activeConfig.Id;
            _appConfig.ActiveCameraId = activeConfig.Id;
        }

        private async Task SendHealthSnapshotToFrontendAsync(bool showToast = false)
        {
            try
            {
                if (showToast)
                {
                    await RefreshRuntimeModelStateAsync(loadDefaultModelIfMissing: true, pushModelList: true);
                }

                await _uiController.SendHealthSnapshot(BuildFieldDiagnosticsSnapshot());
                if (showToast)
                {
                    await _uiController.SendUiCommand("toast", new
                    {
                        message = "健康状态已刷新",
                        type = "success",
                        durationMs = 1200
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HealthMonitor] 推送健康快照失败: {ex.Message}");
            }
        }

        private FieldDiagnosticsSnapshot BuildFieldDiagnosticsSnapshot()
        {
            return _appRuntime.BuildFieldDiagnosticsSnapshot(_healthMonitor.GetSnapshot());
        }

        private static string FormatBytes(long bytes)
        {
            return $"{bytes / 1024d / 1024d:F1}MB";
        }

        private static string NormalizeRequiredPlcAddressForSave(
            string address,
            PlcProtocolType protocolType,
            string driverProvider)
        {
            string normalized = PlcAddressNormalizer.NormalizeOrThrow(address, protocolType);
            PlcAddressNormalizer.EnsureDriverSupportsAddress(normalized, protocolType, driverProvider);
            return normalized;
        }

        private static string NormalizeOptionalPlcAddressForSave(
            string address,
            PlcProtocolType protocolType,
            string driverProvider,
            int defaultNumber,
            bool required)
        {
            if (required)
            {
                return NormalizeRequiredPlcAddressForSave(address, protocolType, driverProvider);
            }

            string normalized = PlcAddressNormalizer.MigrateLegacyAddress(
                address,
                protocolType,
                GetProtocolDefaultPlcAddress(protocolType, defaultNumber));
            return PlcAddressNormalizer.IsSupportedByDriver(normalized, protocolType, driverProvider, out _)
                ? normalized
                : GetProtocolDefaultPlcAddress(protocolType, defaultNumber);
        }

        private static string GetProtocolDefaultPlcAddress(PlcProtocolType protocolType, int number)
        {
            return protocolType switch
            {
                PlcProtocolType.Siemens_S7 => $"DB1.{number}",
                PlcProtocolType.Modbus_TCP => number.ToString(),
                PlcProtocolType.Omron_Fins => $"D{number}",
                _ => $"D{number}"
            };
        }

        private static string GetJsonStringValue(JsonElement value, string fallback)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                string raw = value.GetString()?.Trim() ?? string.Empty;
                return string.IsNullOrWhiteSpace(raw) ? fallback : raw;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out long longValue))
                {
                    return longValue.ToString();
                }
            }

            return fallback;
        }

        private static string NormalizeSerialPortNameForSave(string value)
        {
            string raw = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            int comIndex = raw.IndexOf("COM", StringComparison.OrdinalIgnoreCase);
            if (comIndex < 0)
            {
                return raw;
            }

            int digitStart = comIndex + 3;
            int digitEnd = digitStart;
            while (digitEnd < raw.Length && char.IsDigit(raw[digitEnd]))
            {
                digitEnd++;
            }

            return digitEnd > digitStart
                ? raw.Substring(comIndex, digitEnd - comIndex).ToUpperInvariant()
                : raw;
        }

        private static string NormalizeWireSequenceLabelsForSave(string value)
        {
            return string.Join(
                ",",
                (value ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(label => !string.IsNullOrWhiteSpace(label)));
        }

        private static bool HasConfiguredWireSequenceLabels(string value)
        {
            return !string.IsNullOrWhiteSpace(NormalizeWireSequenceLabelsForSave(value));
        }

        private static string NormalizeCameraPixelFormatForSave(string? value)
        {
            string raw = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Auto";
            }

            string normalized = raw.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            if (string.Equals(normalized, "BGR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Color", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Colour", StringComparison.OrdinalIgnoreCase))
            {
                return "BGR8";
            }

            if (string.Equals(normalized, "RGB", StringComparison.OrdinalIgnoreCase))
            {
                return "RGB8";
            }

            if (string.Equals(normalized, "BayerRG", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerRG8";
            }

            if (string.Equals(normalized, "BayerGB", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerGB8";
            }

            if (string.Equals(normalized, "BayerGR", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerGR8";
            }

            if (string.Equals(normalized, "BayerBG", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerBG8";
            }

            string[] allowed =
            {
                "Auto",
                "Mono8",
                "BGR8",
                "RGB8",
                "BayerRG8",
                "BayerGB8",
                "BayerGR8",
                "BayerBG8"
            };

            return allowed.FirstOrDefault(format => string.Equals(format, raw, StringComparison.OrdinalIgnoreCase)) ?? "Auto";
        }

        private static TEnum GetJsonEnumValue<TEnum>(JsonElement value, TEnum fallback)
            where TEnum : struct, Enum
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                string? raw = value.GetString();
                return Enum.TryParse(raw, ignoreCase: true, out TEnum parsed)
                    ? parsed
                    : fallback;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int intValue))
            {
                return Enum.IsDefined(typeof(TEnum), intValue)
                    ? (TEnum)Enum.ToObject(typeof(TEnum), intValue)
                    : fallback;
            }

            return fallback;
        }

        private void InvokeOnUIThread(Action action)
        {
            if (action == null || IsShutdownInProgress || IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }

            void SafeInvoke()
            {
                if (IsShutdownInProgress || IsDisposed || Disposing || !IsHandleCreated)
                {
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UI] Invoke action failed: {ex.Message}");
                }
            }

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((MethodInvoker)SafeInvoke);
                }
                else
                {
                    SafeInvoke();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                Debug.WriteLine($"[UI] Invoke skipped: {ex.Message}");
            }
        }

        #endregion

        #region Replay闭环WebUI

        private async Task PreviewReplayDatasetAsync(WebUiCommandEventArgs args)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                string datasetId = GetString(document.RootElement, "datasetId", "DatasetId") ?? _lastReplayDatasetId;
                ReplayDatasetSnapshot snapshot = await _appRuntime.ReplayDatasetStore
                    .LoadSnapshotAsync(datasetId, _appShutdownCts.Token)
                    .ConfigureAwait(false);
                await _uiController.SendReplayDatasetPreview(snapshot, args.RequestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await SendReplayDatasetFailureAsync(args.RequestId, "ReplayDatasetPreviewFailed", ex.Message).ConfigureAwait(false);
            }
        }

        private async Task QueryReplayDatasetsAsync(WebUiCommandEventArgs args)
        {
            try
            {
                IReadOnlyList<ReplayDatasetSummary> datasets = await _appRuntime.ReplayDatasetStore
                    .ListSnapshotsAsync(_appShutdownCts.Token)
                    .ConfigureAwait(false);
                await _uiController.SendReplayDatasets(datasets, args.RequestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _uiController.SendReplayDatasets(Array.Empty<ReplayDatasetSummary>(), args.RequestId).ConfigureAwait(false);
                await _uiController.LogToFrontend($"Replay Dataset查询失败: {ex.Message}", "error").ConfigureAwait(false);
            }
        }

        private async Task ArchiveReplayDatasetAsync(WebUiCommandEventArgs args)
        {
            if (!await EnsureReplayWriteAuthorizedAsync(
                    args,
                    "archive_replay_dataset",
                    () => SendReplayDatasetFailureAsync(args.RequestId, "ReplayWriteUnauthorized", "Replay dataset archive requires engineering change permission."))
                .ConfigureAwait(false))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                string datasetId = GetString(document.RootElement, "datasetId", "DatasetId") ?? _lastReplayDatasetId;
                if (string.IsNullOrWhiteSpace(datasetId))
                {
                    await SendReplayDatasetFailureAsync(args.RequestId, "ReplayDatasetMissing", "Replay dataset id is required.").ConfigureAwait(false);
                    return;
                }

                ReplayDatasetArchiveResult result = await _appRuntime.ReplayDatasetLifecycleService
                    .ArchiveSnapshotAsync(datasetId, _appShutdownCts.Token)
                    .ConfigureAwait(false);
                await _uiController.SendDatasetCreateStatus(result, args.RequestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await SendReplayDatasetFailureAsync(args.RequestId, "ReplayDatasetArchiveFailed", ex.Message).ConfigureAwait(false);
            }
        }

        private async Task CancelReplayRunAsync(WebUiCommandEventArgs args)
        {
            if (!await EnsureReplayWriteAuthorizedAsync(
                    args,
                    "cancel_replay_run",
                    () => _uiController.SendReplayRunStatus(new ReplayRunProgress
                    {
                        RunId = _appRuntime.ReplayCoordinator.CurrentRun?.RunId ?? _lastReplayRunId,
                        Status = ReplayRunStatuses.Failed,
                        Phase = "authorization",
                        Message = "ReplayWriteUnauthorized: Replay cancel requires engineering change permission."
                    }, args.RequestId))
                .ConfigureAwait(false))
            {
                return;
            }

            ReplayCancelRequestResult result = await _appRuntime.ReplayCoordinator
                .RequestCancelAsync(_appShutdownCts.Token)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await _uiController.SendReplayRunStatus(new ReplayRunProgress
                {
                    RunId = _appRuntime.ReplayCoordinator.CurrentRun?.RunId ?? _lastReplayRunId,
                    Status = ReplayRunStatuses.Failed,
                    Phase = "cancel",
                    Message = $"{result.ErrorCode}: {result.Message}"
                }, args.RequestId).ConfigureAwait(false);
                return;
            }

            await _uiController.SendReplayRunStatus(result.Progress!, args.RequestId).ConfigureAwait(false);
        }

        private async Task QueryReplayRunsAsync(WebUiCommandEventArgs args)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                int limit = GetInt32(document.RootElement, "limit", "Limit") ?? 100;
                IReadOnlyList<ReplayRunRecord> runs = await _appRuntime.ReplayRunStore
                    .ListRunRecordsAsync(limit, _appShutdownCts.Token)
                    .ConfigureAwait(false);
                await _uiController.SendReplayRuns(runs, args.RequestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _uiController.SendReplayRuns(Array.Empty<ReplayRunRecord>(), args.RequestId).ConfigureAwait(false);
                await _uiController.LogToFrontend($"Replay Run查询失败: {ex.Message}", "error").ConfigureAwait(false);
            }
        }

        private async Task QueryReplayReportAsync(WebUiCommandEventArgs args)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                string runId = GetString(document.RootElement, "runId", "RunId") ?? _lastReplayRunId;
                ReplayRunReport report = await _appRuntime.ReplayRunStore
                    .LoadReportAsync(runId, _appShutdownCts.Token)
                    .ConfigureAwait(false);
                await _uiController.SendReplayReport(report, args.RequestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await SendReplayRunFailureAsync(args.RequestId, "ReplayReportQueryFailed", ex.Message).ConfigureAwait(false);
            }
        }

        private Task QueryModelApprovalEvidenceAsync(WebUiCommandEventArgs args)
        {
            IReadOnlyList<ModelApprovalEvidence> evidence = _appRuntime.ReplayApprovalApplicationService.ListEvidence();
            return _uiController.SendModelApprovalEvidence(evidence, args.RequestId);
        }

        private async Task RunReplayIntegrityScanAsync(WebUiCommandEventArgs args)
        {
            ReplayIntegrityScanResult result = await _appRuntime.ReplayIntegrityScanner
                .ScanApprovedModelsAsync(_appShutdownCts.Token)
                .ConfigureAwait(false);
            await _uiController.SendReplayIntegrityScan(result, args.RequestId).ConfigureAwait(false);
        }

        private async Task QueryManualReviewRecordsAsync(WebUiCommandEventArgs args)
        {
            try
            {
                ManualReviewQuery query = ParseManualReviewQuery(args.PayloadJson);
                IReadOnlyList<ManualReviewTraceItem> records = await _appRuntime.ManualReviewStore
                    .QueryAsync(query, _appShutdownCts.Token)
                    .ConfigureAwait(false);
                await _uiController.SendManualReviewRecords(records, args.RequestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _uiController.SendManualReviewRecords(Array.Empty<ManualReviewTraceItem>(), args.RequestId)
                    .ConfigureAwait(false);
                await _uiController.LogToFrontend($"人工复核查询失败: {ex.Message}", "error").ConfigureAwait(false);
            }
        }

        private async Task SaveManualReviewAsync(WebUiCommandEventArgs args)
        {
            if (!await EnsureReplayWriteAuthorizedAsync(
                    args,
                    "save_manual_review",
                    () => _uiController.SendManualReviewResponse(
                        ManualReviewSaveResult.Fail("ReplayWriteUnauthorized", "Manual review save requires engineering change permission."),
                        args.RequestId))
                .ConfigureAwait(false))
            {
                return;
            }

            try
            {
                ManualReviewSaveRequest request = ParseManualReviewSaveRequest(args.PayloadJson);
                ManualReviewSaveResult result = await _appRuntime.ManualReviewStore
                    .SaveReviewAsync(request, _appShutdownCts.Token)
                    .ConfigureAwait(false);
                await _uiController.SendManualReviewResponse(result, args.RequestId).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    await QueryManualReviewRecordsAsync(new WebUiCommandEventArgs(args.RequestId, "{}")).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await _uiController.SendManualReviewResponse(
                    ManualReviewSaveResult.Fail("ManualReviewUiHandlerFailed", ex.Message),
                    args.RequestId).ConfigureAwait(false);
            }
        }

        private async Task CreateReplayDatasetAsync(WebUiCommandEventArgs args)
        {
            if (!await EnsureReplayWriteAuthorizedAsync(
                    args,
                    "create_replay_dataset",
                    () => SendReplayDatasetFailureAsync(args.RequestId, "ReplayWriteUnauthorized", "Replay dataset creation requires engineering change permission."))
                .ConfigureAwait(false))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                JsonElement root = document.RootElement;
                DetectionReplayQuery replayQuery = ParseReplayQuery(root);
                string recipeId = GetString(root, "recipeId", "RecipeId") ?? _recipeManager.CurrentRecipe.RecipeId;
                string recipeVersion = GetString(root, "recipeVersion", "RecipeVersion") ?? _recipeManager.CurrentRecipe.Version;

                if (!_recipeManager.TryLoadVersion(recipeId, recipeVersion, out Recipe recipe, out string recipeError))
                {
                    await SendReplayDatasetFailureAsync(args.RequestId, "ReplayRecipeVersionMissing", recipeError)
                        .ConfigureAwait(false);
                    return;
                }

                replayQuery.RecipeVersion = recipe.Version;
                if (!TryResolveReplayModel(
                        GetString(root, "baselineModel", "BaselineModel", "baselineSelection", "BaselineSelection"),
                        requireApproved: true,
                        candidateDefault: false,
                        out ModelRegistryEntry baselineEntry,
                        out string baselineError))
                {
                    await SendReplayDatasetFailureAsync(args.RequestId, "ReplayBaselineModelInvalid", baselineError)
                        .ConfigureAwait(false);
                    return;
                }

                if (!TryResolveReplayModel(
                        GetString(root, "candidateModel", "CandidateModel", "candidateSelection", "CandidateSelection"),
                        requireApproved: false,
                        candidateDefault: true,
                        out ModelRegistryEntry candidateEntry,
                        out string candidateError))
                {
                    await SendReplayDatasetFailureAsync(args.RequestId, "ReplayCandidateModelInvalid", candidateError)
                        .ConfigureAwait(false);
                    return;
                }

                ManualReviewQuery reviewQuery = new ManualReviewQuery { ReplayQuery = replayQuery };
                IReadOnlyList<ManualReviewTraceItem> reviewItems = await _appRuntime.ManualReviewStore
                    .QueryAsync(reviewQuery, _appShutdownCts.Token)
                    .ConfigureAwait(false);
                Dictionary<long, ReplayManualReviewRecord> reviewsByRecordId = reviewItems
                    .Where(item => item.Review != null && item.DetectionRecordId > 0)
                    .ToDictionary(item => item.DetectionRecordId, item => item.Review!);

                ReplayDatasetSnapshot snapshot = await _appRuntime.ReplayDatasetStore.CreateSnapshotAsync(
                    new ReplayDatasetCreateRequest
                    {
                        DatasetId = GetString(root, "datasetId", "DatasetId") ?? $"dataset-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
                        Query = replayQuery,
                        Recipe = CreateReplayRecipeSnapshot(recipe),
                        BaselineModel = ReplayModelIdentity.FromRegistryEntry(baselineEntry),
                        CandidateModel = ReplayModelIdentity.FromRegistryEntry(candidateEntry),
                        ManualReviewsByDetectionRecordId = reviewsByRecordId
                    },
                    _appShutdownCts.Token).ConfigureAwait(false);

                _lastReplayDatasetId = snapshot.DatasetId;
                _lastReplayBaselineModel = snapshot.BaselineModel;
                _lastReplayCandidateModel = snapshot.CandidateModel;

                await _uiController.SendDatasetCreateStatus(new
                {
                    succeeded = true,
                    status = "Frozen",
                    datasetId = snapshot.DatasetId,
                    datasetHash = snapshot.DatasetHash,
                    sampleCount = snapshot.Samples.Count,
                    recipeId = snapshot.Recipe.RecipeId,
                    recipeVersion = snapshot.Recipe.RecipeVersion,
                    baselineModel = snapshot.BaselineModel,
                    candidateModel = snapshot.CandidateModel,
                    message = "Replay dataset frozen."
                }, args.RequestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await SendReplayDatasetFailureAsync(args.RequestId, "ReplayDatasetCreateFailed", ex.Message)
                    .ConfigureAwait(false);
            }
        }

        private async Task RunReplayComparisonAsync(WebUiCommandEventArgs args)
        {
            if (!await EnsureReplayWriteAuthorizedAsync(
                    args,
                    "run_replay_comparison",
                    () => SendReplayRunFailureAsync(args.RequestId, "ReplayWriteUnauthorized", "Replay comparison requires engineering change permission."))
                .ConfigureAwait(false))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                JsonElement root = document.RootElement;
                string datasetId = GetString(root, "datasetId", "DatasetId") ?? _lastReplayDatasetId;
                if (string.IsNullOrWhiteSpace(datasetId))
                {
                    await SendReplayRunFailureAsync(args.RequestId, "ReplayDatasetMissing", "Replay dataset id is required.")
                        .ConfigureAwait(false);
                    return;
                }

                ReplayModelIdentity baseline = await ResolveReplayRunModelAsync(
                    root,
                    datasetId,
                    baseline: true,
                    args.RequestId).ConfigureAwait(false);
                ReplayModelIdentity candidate = await ResolveReplayRunModelAsync(
                    root,
                    datasetId,
                    baseline: false,
                    args.RequestId).ConfigureAwait(false);

                string runId = GetString(root, "runId", "RunId") ?? $"replay-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                var progress = new Progress<ReplayRunProgress>(item =>
                    SafeFireAndForget(_uiController.SendReplayRunStatus(item, args.RequestId), "Replay进度推送"));

                ReplayRunReport report = await _appRuntime.ReplayCoordinator.StartAsync(
                    new ReplayComparisonRequest
                    {
                        RunId = runId,
                        DatasetId = datasetId,
                        BaselineModel = baseline,
                        CandidateModel = candidate
                    },
                    progress,
                    _appShutdownCts.Token).ConfigureAwait(false);

                _lastReplayRunId = report.RunId;
                _lastReplayDatasetId = report.DatasetId;
                _lastReplayBaselineModel = report.BaselineModel;
                _lastReplayCandidateModel = report.CandidateModel;

                ReplayApprovalDecision decision = _appRuntime.ReplayPolicy.Evaluate(report);
                await _uiController.SendReplayRunCompleted(report, args.RequestId).ConfigureAwait(false);
                await _uiController.SendModelApprovalAvailability(decision.Approved, decision.Reasons, args.RequestId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string errorCode = string.Equals(ex.Message, "ReplayProductionBusy", StringComparison.Ordinal)
                    ? "ReplayProductionBusy"
                    : string.Equals(ex.Message, "ReplayAlreadyRunning", StringComparison.Ordinal)
                        ? "ReplayAlreadyRunning"
                        : "ReplayRunFailed";
                await SendReplayRunFailureAsync(args.RequestId, errorCode, ex.Message).ConfigureAwait(false);
            }
        }

        private async Task ApproveReplayCandidateAsync(WebUiCommandEventArgs args)
        {
            if (!await EnsureReplayWriteAuthorizedAsync(
                    args,
                    "approve_replay_candidate",
                    () => _uiController.SendReplayApprovalResponse(
                        ReplayApprovalResult.Fail("ReplayWriteUnauthorized", "Replay approval requires engineering change permission."),
                        args.RequestId))
                .ConfigureAwait(false))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(args.PayloadJson);
                JsonElement root = document.RootElement;
                string runId = GetString(root, "runId", "RunId") ?? _lastReplayRunId;
                if (string.IsNullOrWhiteSpace(runId))
                {
                    await _uiController.SendReplayApprovalResponse(
                        ReplayApprovalResult.Fail("ReplayApprovalRunIdMissing", "Replay run id is required."),
                        args.RequestId).ConfigureAwait(false);
                    return;
                }

                ReplayApprovalResult result = await _appRuntime.ReplayApprovalApplicationService.ApproveCandidateAsync(
                    new ReplayApprovalRequest
                    {
                        RunId = runId
                    },
                    _appShutdownCts.Token).ConfigureAwait(false);

                await _uiController.SendReplayApprovalResponse(result, args.RequestId).ConfigureAwait(false);
                await _uiController.SendModelApprovalAvailability(
                    result.Succeeded,
                    result.Succeeded ? Array.Empty<string>() : new[] { result.Message },
                    args.RequestId).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    await RefreshRuntimeModelStateAsync(loadDefaultModelIfMissing: false, pushModelList: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await _uiController.SendReplayApprovalResponse(
                    ReplayApprovalResult.Fail("ReplayApprovalUiHandlerFailed", ex.Message),
                    args.RequestId).ConfigureAwait(false);
            }
        }

        private async Task<ReplayModelIdentity> ResolveReplayRunModelAsync(
            JsonElement root,
            string datasetId,
            bool baseline,
            string requestId)
        {
            string? selection = baseline
                ? GetString(root, "baselineModel", "BaselineModel", "baselineSelection", "BaselineSelection")
                : GetString(root, "candidateModel", "CandidateModel", "candidateSelection", "CandidateSelection");
            ReplayModelIdentity? remembered = baseline ? _lastReplayBaselineModel : _lastReplayCandidateModel;
            if (!string.IsNullOrWhiteSpace(selection) &&
                TryResolveReplayModel(selection, baseline, !baseline, out ModelRegistryEntry entry, out _))
            {
                return ReplayModelIdentity.FromRegistryEntry(entry);
            }

            if (remembered != null)
            {
                ModelRegistryEntry? rememberedEntry = ResolveEntryForIdentity(remembered);
                if (rememberedEntry != null)
                {
                    return ReplayModelIdentity.FromRegistryEntry(rememberedEntry);
                }

                if (!string.IsNullOrWhiteSpace(remembered.ModelPath))
                {
                    return remembered;
                }
            }

            ReplayDatasetSnapshot snapshot = await _appRuntime.ReplayDatasetStore
                .LoadSnapshotAsync(datasetId, _appShutdownCts.Token)
                .ConfigureAwait(false);
            ReplayModelIdentity datasetIdentity = baseline ? snapshot.BaselineModel : snapshot.CandidateModel;
            ModelRegistryEntry? datasetEntry = ResolveEntryForIdentity(datasetIdentity);
            if (datasetEntry != null)
            {
                return ReplayModelIdentity.FromRegistryEntry(datasetEntry);
            }

            if (!string.IsNullOrWhiteSpace(datasetIdentity.ModelPath))
            {
                return datasetIdentity;
            }

            throw new InvalidOperationException(
                baseline
                    ? "Replay baseline package could not be resolved from dataset identity."
                    : "Replay candidate package could not be resolved from dataset identity.");
        }

        private ManualReviewQuery ParseManualReviewQuery(string payloadJson)
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            return new ManualReviewQuery
            {
                ReplayQuery = ParseReplayQuery(root),
                ReviewStatus = GetString(root, "reviewStatus", "ReviewStatus") ?? string.Empty
            };
        }

        private ManualReviewSaveRequest ParseManualReviewSaveRequest(string payloadJson)
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            return new ManualReviewSaveRequest
            {
                DetectionRecordId = GetInt64(root, "detectionRecordId", "DetectionRecordId") ?? 0,
                InspectionId = GetString(root, "inspectionId", "InspectionId") ?? string.Empty,
                SampleId = GetString(root, "sampleId", "SampleId") ?? string.Empty,
                GroundTruth = GetString(root, "groundTruth", "GroundTruth") ?? ReplayDecisions.OK,
                Disposition = GetString(root, "disposition", "Disposition") ?? ReplayReviewDispositions.Pending,
                ReviewerId = ResolveCurrentOperatorId(),
                ReviewerRole = _appConfig.CurrentOperatorRole.ToString(),
                ExpectedRevision = GetInt64(root, "expectedRevision", "ExpectedRevision"),
                Notes = GetString(root, "notes", "Notes") ?? string.Empty
            };
        }

        private DetectionReplayQuery ParseReplayQuery(JsonElement root)
        {
            return new DetectionReplayQuery
            {
                ProductOrBarcode = GetString(root, "productOrBarcode", "ProductOrBarcode", "barcode", "Barcode"),
                IsQualified = GetBoolean(root, "isQualified", "IsQualified"),
                ModelName = GetString(root, "modelName", "ModelName"),
                ModelVersion = GetString(root, "modelVersion", "ModelVersion"),
                RecipeVersion = GetString(root, "recipeVersion", "RecipeVersion"),
                Limit = Math.Clamp(GetInt32(root, "limit", "Limit") ?? 100, 1, 10000),
                StartTime = GetDateTime(root, "startTime", "StartTime"),
                EndTime = GetDateTime(root, "endTime", "EndTime")
            };
        }

        private ReplayRecipeSnapshot CreateReplayRecipeSnapshot(Recipe recipe)
        {
            string ruleSetJson = recipe.InspectionRuleSetJson ?? string.Empty;
            return new ReplayRecipeSnapshot
            {
                RecipeId = recipe.RecipeId,
                RecipeVersion = recipe.Version,
                Confidence = recipe.Confidence,
                IouThreshold = recipe.IouThreshold,
                Roi = recipe.GetRoiSnapshot(),
                RuleSetJson = ruleSetJson,
                RuleSet = InspectionRuleSetSerializer.DeserializeOrDefault(ruleSetJson)
            };
        }

        private bool TryResolveReplayModel(
            string? selection,
            bool requireApproved,
            bool candidateDefault,
            out ModelRegistryEntry entry,
            out string error)
        {
            entry = new ModelRegistryEntry();
            error = string.Empty;

            ModelRegistryEntry? resolved = null;
            if (!string.IsNullOrWhiteSpace(selection))
            {
                if (ProductionModelReference.TryParseSelectionValue(selection, out ProductionModelReference reference) &&
                    !reference.IsEmpty)
                {
                    ProductionModelResolutionResult result = _modelRegistry.ResolveReference(reference, requireApproved);
                    if (!result.Succeeded || result.Entry == null)
                    {
                        error = string.IsNullOrWhiteSpace(result.Message) ? result.ErrorCode : result.Message;
                        return false;
                    }

                    resolved = result.Entry;
                }
                else
                {
                    resolved = _modelRegistry.Resolve(selection);
                }
            }

            resolved ??= candidateDefault
                ? _modelRegistry.Entries.FirstOrDefault(item =>
                    item.IsPackage &&
                    item.Status == ModelRegistryStatus.Ready &&
                    !item.ApprovedForProduction)
                : ResolveEntryForReference(_appConfig.CurrentModelReference) ??
                  _modelRegistry.Entries.FirstOrDefault(item =>
                      item.IsPackage &&
                      item.Status == ModelRegistryStatus.Ready &&
                      item.ApprovedForProduction);

            if (resolved == null)
            {
                error = candidateDefault
                    ? "No pending candidate package is available for replay."
                    : "No approved baseline package is available for replay.";
                return false;
            }

            if (!resolved.IsPackage || resolved.Status != ModelRegistryStatus.Ready)
            {
                error = $"Replay model must be a ready package: {resolved.ModelId}/{resolved.Version}.";
                return false;
            }

            if (requireApproved && !resolved.ApprovedForProduction)
            {
                error = $"Baseline model is not approved for production: {resolved.ModelId}/{resolved.Version}.";
                return false;
            }

            entry = resolved;
            return true;
        }

        private ModelRegistryEntry? ResolveEntryForReference(ProductionModelReference? reference)
        {
            if (reference == null || reference.IsEmpty)
            {
                return null;
            }

            ProductionModelResolutionResult result = _modelRegistry.ResolveReference(reference, true);
            return result.Succeeded ? result.Entry : null;
        }

        private ModelRegistryEntry? ResolveEntryForIdentity(ReplayModelIdentity identity)
        {
            return _modelRegistry.Entries.FirstOrDefault(entry =>
                entry.IsPackage &&
                string.Equals(entry.ModelId, identity.ModelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Version, identity.Version, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.ModelHash, identity.Sha256, StringComparison.OrdinalIgnoreCase));
        }

        private Task SendReplayDatasetFailureAsync(string requestId, string errorCode, string message)
        {
            return _uiController.SendDatasetCreateStatus(new
            {
                succeeded = false,
                status = "Failed",
                errorCode,
                message
            }, requestId);
        }

        private Task SendReplayRunFailureAsync(string requestId, string errorCode, string message)
        {
            return _uiController.SendReplayRunStatus(new ReplayRunProgress
            {
                RunId = _lastReplayRunId,
                Status = ReplayRunStatuses.Failed,
                Phase = errorCode,
                Message = message,
                CompletedSamples = 0,
                TotalSamples = 0
            }, requestId);
        }

        private static string? GetString(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty(name, out JsonElement element) &&
                    element.ValueKind != JsonValueKind.Null &&
                    element.ValueKind != JsonValueKind.Undefined)
                {
                    return element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : element.ToString();
                }
            }

            return null;
        }

        private static int? GetInt32(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement element))
                {
                    if (element.TryGetInt32(out int value)) return value;
                    if (element.ValueKind == JsonValueKind.String &&
                        int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static long? GetInt64(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement element))
                {
                    if (element.TryGetInt64(out long value)) return value;
                    if (element.ValueKind == JsonValueKind.String &&
                        long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static bool? GetBoolean(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement element))
                {
                    if (element.ValueKind == JsonValueKind.True) return true;
                    if (element.ValueKind == JsonValueKind.False) return false;
                    if (element.ValueKind == JsonValueKind.String &&
                        bool.TryParse(element.GetString(), out bool value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static DateTime? GetDateTime(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                string? value = GetString(root, name);
                if (!string.IsNullOrWhiteSpace(value) &&
                    DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        #endregion

        #region 数据集自动收集

        private async Task CollectDatasetAsync()
        {
            try
            {
                var service = new ClearFrost.Services.DatasetCollectionService(
                    ClearFrost.Helpers.RuntimePaths.DatabasePath,
                    BaseStoragePath);

                var progress = new Progress<string>(msg =>
                {
                    _ = _uiController.LogToFrontend($"[数据集] {msg}", "info");
                });

                var result = await service.CollectAsync(progress: progress);

                await _uiController.SendDatasetCollectResult(new
                {
                    success = result.Success,
                    totalCopied = result.FailCopied + result.PassCopied,
                    failCopied = result.FailCopied,
                    passCopied = result.PassCopied,
                    outputDirectory = result.OutputDirectory,
                    message = result.Message
                });

                if (result.Success)
                {
                    await _uiController.LogToFrontend($"数据集收集完成: {result.OutputDirectory}", "success");
                }
                else
                {
                    await _uiController.LogToFrontend($"数据集收集失败: {result.Message}", "error");
                }
            }
            catch (Exception ex)
            {
                await _uiController.SendDatasetCollectResult(new
                {
                    success = false,
                    message = ex.Message
                });
                await _uiController.LogToFrontend($"数据集收集异常: {ex.Message}", "error");
            }
        }

        private bool CheckSystemConfigChanges(string configJson)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(configJson))
                {
                    var root = doc.RootElement;
                    foreach (var property in root.EnumerateObject())
                    {
                        string name = property.Name;
                        if (RuntimeConfigurationChangeClassifier.ShouldIgnoreForSystemConfigChange(name))
                        {
                            continue;
                        }

                        var prop = _appConfig.GetType().GetProperty(name);
                        if (prop == null || !prop.CanWrite)
                        {
                            continue;
                        }

                        object? curVal = prop.GetValue(_appConfig);
                        bool changed = false;
                        var val = property.Value;

                        try
                        {
                            if (prop.PropertyType == typeof(string))
                            {
                                string s1 = (curVal as string)?.Trim() ?? string.Empty;
                                string s2 = (val.GetString())?.Trim() ?? string.Empty;

                                if (name == "StoragePath")
                                {
                                    try
                                    {
                                        s1 = Path.GetFullPath(s1).TrimEnd('\\', '/');
                                        s2 = Path.GetFullPath(s2).TrimEnd('\\', '/');
                                    }
                                    catch { }
                                }
                                else if (name == "InspectionRuleSetJson")
                                {
                                    changed = !IsRuleSetJsonEqual(s1, s2);
                                    goto CheckEnd;
                                }

                                changed = !string.Equals(s1, s2, StringComparison.Ordinal);
                            }
                            else if (prop.PropertyType == typeof(bool))
                            {
                                bool b1 = curVal is bool bv && bv;
                                bool b2 = val.ValueKind == JsonValueKind.True;
                                changed = (b1 != b2);
                            }
                            else if (prop.PropertyType == typeof(int))
                            {
                                int i1 = curVal is int ivVal ? ivVal : 0;
                                int i2 = val.TryGetInt32(out int iv) ? iv : i1;
                                changed = (i1 != i2);
                            }
                            else if (prop.PropertyType == typeof(float))
                            {
                                float f1 = curVal is float fvVal ? fvVal : 0f;
                                float f2 = val.TryGetDouble(out double dv) ? (float)dv : f1;
                                changed = (Math.Abs(f1 - f2) > 1e-4f);
                            }
                            else if (prop.PropertyType == typeof(double))
                            {
                                double d1 = curVal is double dvVal ? dvVal : 0.0;
                                double d2 = val.TryGetDouble(out double dv) ? dv : d1;
                                changed = (Math.Abs(d1 - d2) > 1e-4);
                            }
                            else if (prop.PropertyType == typeof(short))
                            {
                                short sh1 = curVal is short svVal ? svVal : (short)0;
                                short sh2 = val.TryGetInt16(out short sv) ? sv : sh1;
                                changed = (sh1 != sh2);
                            }
                            else if (prop.PropertyType.IsEnum)
                            {
                                string e1 = curVal?.ToString() ?? string.Empty;
                                string e2 = val.GetString() ?? string.Empty;
                                changed = !string.Equals(e1, e2, StringComparison.OrdinalIgnoreCase);
                            }
                            else
                            {
                                changed = true;
                            }
                        }
                        catch
                        {
                            changed = true;
                        }

                    CheckEnd:
                        if (changed)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private bool IsRuleSetJsonEqual(string json1, string json2)
        {
            if (string.Equals(json1, json2, StringComparison.Ordinal)) return true;
            try
            {
                if (InspectionRuleSetSerializer.TryDeserialize(json1, out var rs1, out _) &&
                    InspectionRuleSetSerializer.TryDeserialize(json2, out var rs2, out _))
                {
                    string s1 = InspectionRuleSetSerializer.Serialize(rs1);
                    string s2 = InspectionRuleSetSerializer.Serialize(rs2);
                    return string.Equals(s1, s2, StringComparison.Ordinal);
                }
            }
            catch { }
            return false;
        }

        private Task<bool> RestartTriggerSourceAfterConfigurationChangeAsync(string reason)
        {
            return TriggerSourceRuntimeCoordinator.RestartAfterConfigurationChangeAsync(
                IsProductionRunning,
                () => StopTriggerSourcesAsync(logWarnings: true),
                StartTriggerSourceAsync,
                message => _uiController.LogToFrontend(message, "info"),
                reason);
        }

        private Task StopTriggerSourcesAsync(bool logWarnings)
        {
            try
            {
                _plcService.StopMonitoring();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TriggerSource] Stop PLC monitoring failed: {ex.Message}");
            }
            try
            {
                _serialTriggerService.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TriggerSource] Stop serial trigger failed: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        #endregion
    }
}
