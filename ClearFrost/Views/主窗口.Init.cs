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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Yolo;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

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
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.FlashPlcTrigger(), "PLC触发指示灯");
                });

                InvokeOnUIThread(() => SafeFireAndForget(btnCapture_LogicAsync("PLC半自动"), "PLC触发检测"));
            };
            _plcService.TriggerContextReceived += (context) =>
            {
                Debug.WriteLine($"[主窗口] 📥 收到PLC上下文触发事件 - Seq={context.TriggerSeq?.ToString() ?? "-"} - {DateTime.Now:HH:mm:ss.fff}");
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.FlashPlcTrigger(), "PLC触发指示灯");
                });

                InvokeOnUIThread(() => SafeFireAndForget(
                    btnCapture_LogicAsync(context.TriggerSource, context.TriggerSeq),
                    "PLC上下文触发检测"));
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
            _uiController.OnOpenCamera += (s, e) => InvokeOnUIThread(() => SafeFireAndForget(OpenCameraWithPermissionAsync(), "打开相机"));
            _uiController.OnManualDetect += (s, e) => InvokeOnUIThread(() => SafeFireAndForget(ManualDetectWithPermissionAsync(), "手动检测"));
            _uiController.OnCaptureCameraPreview += (s, json) => InvokeOnUIThread(() => SafeFireAndForget(CaptureCameraPreviewFrameAsync(json), "获取相机预览单帧"));
            _uiController.OnManualRelease += (s, json) => InvokeOnUIThread(() => SafeFireAndForget(ManualReleaseWithPermissionAsync(json), "手动放行"));
            _uiController.OnOpenSettings += (s, e) => InvokeOnUIThread(() => btnSettings_Logic());
            _uiController.OnCollectDataset += (s, e) => SafeFireAndForget(CollectDatasetAsync(), "数据集收集");
            _uiController.OnRunHistoryRulePreview += (s, json) => SafeFireAndForget(RunHistoryRulePreviewAsync(json), "历史图规则复判");
            _uiController.OnGetModelList += (s, e) => SafeFireAndForget(InitModelList(), "刷新模型列表");
            _uiController.OnImportModelPackage += (s, e) => InvokeOnUIThread(() => SafeFireAndForget(ImportModelPackageWithPermissionAsync(), "导入模型包"));
            _uiController.OnChangeModel += (s, modelName) => InvokeOnUIThread(() => SafeFireAndForget(ChangeModelWithPermissionAsync(modelName), "切换模型"));
            _uiController.OnConnectPlc += (s, e) => SafeFireAndForget(ConnectPlcWithPermissionAsync(), "PLC手动连接");
            _uiController.OnRequestHealthSnapshot += (s, e) => SafeFireAndForget(SendHealthSnapshotToFrontendAsync(showToast: true), "前端刷新健康快照");
            _uiController.OnGetAlarms += (s, e) => SafeFireAndForget(SendAlarmSnapshotToFrontendAsync(), "刷新告警中心");
            _uiController.OnAcknowledgeAlarm += (s, alarmId) => SafeFireAndForget(AcknowledgeAlarmAsync(alarmId), "确认告警");
            _uiController.OnAcknowledgeAllAlarms += (s, e) => SafeFireAndForget(AcknowledgeAllAlarmsAsync(), "确认全部告警");
            _uiController.OnOperatorSignIn += (s, json) => SafeFireAndForget(HandleOperatorSignInAsync(json), "操作员登录");
            _uiController.OnOperatorSignOut += (s, e) => SafeFireAndForget(HandleOperatorSignOutAsync(), "操作员登出");
            _uiController.OnThresholdChanged += async (s, val) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeInspectionParameters, "更新IOU阈值"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                _appConfig.IouThreshold = Math.Clamp(val / 100f, 0f, 1f);
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("IOU阈值更新");
                    WriteConfigChangeAudit("SetIouThreshold", beforeConfig);
                }
            };
            _uiController.OnGetStatisticsHistory += async (s, e) =>
            {
                var (history, stats) = _statisticsService.GetStatisticsData();
                await _uiController.SendStatisticsHistory(history, stats);
            };
            _uiController.OnClearStatisticsHistory += async (s, e) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageStatistics, "清空历史统计"))
                {
                    return;
                }

                _statisticsService.ClearHistory();
                var (history, stats) = _statisticsService.GetStatisticsData();
                await _uiController.SendStatisticsHistory(history, stats);
                await _uiController.LogToFrontend("✅ 历史统计数据已清空", "success");
            };
            _uiController.OnResetStatistics += async (s, e) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageStatistics, "清除今日统计"))
                {
                    return;
                }

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
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageCamera, "切换生产相机"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                try
                {
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
                            WriteConfigChangeAudit("SwitchCamera", beforeConfig, $"CameraId={cameraId}");
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
                                WriteConfigChangeAudit("SwitchCamera", beforeConfig, $"CameraId={cameraId}");
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
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageCamera, "添加相机配置"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                try
                {
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
                        WriteConfigChangeAudit("SaveCamera", beforeConfig, $"SerialNumber={serialNumber}");
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
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageCamera, "删除相机配置"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                try
                {
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
                        WriteConfigChangeAudit("DeleteCamera", beforeConfig, $"CameraId={cameraId}");
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
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageCamera, "直连相机配置"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string sn = root.TryGetProperty("serialNumber", out var snEl) ? snEl.GetString()?.Trim() ?? "" : "";
                    string manufacturer = root.TryGetProperty("manufacturer", out var mfEl) ? mfEl.GetString() ?? "" : "";
                    string model = root.TryGetProperty("model", out var mdEl) ? mdEl.GetString() ?? "" : "";
                    string displayName = root.TryGetProperty("userDefinedName", out var dnEl) ? dnEl.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(sn))
                    {
                        await _uiController.LogToFrontend("相机序列号为空，无法连接", "error");
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
                            WriteConfigChangeAudit("DirectConnectCamera", beforeConfig, $"SerialNumber={sn}; Manufacturer={manufacturer}");
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
                        await _uiController.LogToFrontend($"相机 [{newConfig.DisplayName}] 已添加并设为当前相机，请点击“打开相机”完成连接", "success");
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"相机连接失败: {sn}", "error");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"直接连接相机失败: {ex.Message}", "error");
                }
            };

            // 注册窗体关闭事件
            this.FormClosing += OnFormClosingHandler;
        }

        private async Task HandleOperatorSignInAsync(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string operatorName = root.TryGetProperty("operatorName", out JsonElement nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                string role = root.TryGetProperty("role", out JsonElement roleElement)
                    ? roleElement.GetString() ?? OperatorSession.DefaultRole
                    : OperatorSession.DefaultRole;
                string? shiftName = root.TryGetProperty("shiftName", out JsonElement shiftElement)
                    ? shiftElement.GetString()
                    : null;

                OperatorPermissionDecision roleGrant = OperatorPermissionService.AuthorizeRoleGrant(
                    _operatorSessionService.Current,
                    role,
                    IsTrustedLocalAdministrator(),
                    "登录操作员角色");
                if (!roleGrant.Allowed)
                {
                    WriteAuditLogSafe(
                        "Permission",
                        "RoleGrantDenied",
                        $"RequestedOperator={NormalizeAuditText(operatorName, 64)}; RequestedRole={roleGrant.RequiredRole}; Operator={roleGrant.OperatorName}; Role={roleGrant.OperatorRole}; Reason={roleGrant.Message}",
                        success: false);
                    await _uiController.LogToFrontend($"角色授权失败: {roleGrant.Message}", "warning");
                    await _uiController.SendOperatorSession(_operatorSessionService.Current);
                    await _uiController.SendUiCommand("toast", new
                    {
                        message = roleGrant.Message,
                        type = "warning",
                        durationMs = 3600
                    });
                    return;
                }

                OperatorSession session = _operatorSessionService.SignIn(operatorName, role, shiftName);
                WriteAuditLogSafe("Operator", "SignIn", $"Operator={session.OperatorName}, Role={session.Role}, Shift={session.ShiftName}");
                await _uiController.SendOperatorSession(session);
                await _uiController.LogToFrontend($"操作员已登录: {session.OperatorName} / {session.ShiftName}", "success");
                if (_appConfig.RequireOperatorForProductionStart &&
                    IsCameraReadyForInspection(out _))
                {
                    SafeFireAndForget(StartTriggerSourceAsync(), "操作员登录后启动生产触发源");
                }
            }
            catch (Exception ex)
            {
                WriteAuditLogSafe("Operator", "SignIn", ex.Message, success: false);
                await _uiController.LogToFrontend($"操作员登录失败: {ex.Message}", "error");
                await _uiController.SendUiCommand("toast", new
                {
                    message = $"操作员登录失败: {ex.Message}",
                    type = "warning",
                    durationMs = 2600
                });
            }
        }

        private async Task HandleOperatorSignOutAsync()
        {
            OperatorSession previous = _operatorSessionService.Current;
            OperatorSession session = _operatorSessionService.SignOut();
            WriteAuditLogSafe("Operator", "SignOut", $"Operator={previous.OperatorName}, Role={previous.Role}, Shift={previous.ShiftName}");
            await _uiController.SendOperatorSession(session);
            await _uiController.LogToFrontend("操作员已退出", "info");
            if (_appConfig.RequireOperatorForProductionStart)
            {
                _serialTriggerService.Stop();
                _plcService.StopMonitoring();
                await _uiController.LogToFrontend("已退出操作员，自动生产触发监听已停止", "warning");
            }
        }

        private Task SendOperatorSessionToFrontendAsync()
        {
            return _uiController.SendOperatorSession(_operatorSessionService.Current);
        }

        private string BuildOperatorAuditContext()
        {
            OperatorSession session = _operatorSessionService.Current;
            return $"Operator={session.OperatorName}; Role={session.Role}; Shift={session.ShiftName}";
        }

        private static bool IsTrustedLocalAdministrator()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeAuditText(string? value, int maxLength)
        {
            string normalized = (value ?? string.Empty)
                .Trim()
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Replace(';', '，');

            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
            }

            return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
        }

        private static string ExtractManualReleaseReason(string? payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return string.Empty;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(payloadJson);
                JsonElement root = doc.RootElement;
                string? reason = root.ValueKind switch
                {
                    JsonValueKind.String => root.GetString(),
                    JsonValueKind.Object when root.TryGetProperty("reason", out JsonElement reasonElement) => reasonElement.GetString(),
                    JsonValueKind.Object when root.TryGetProperty("Reason", out JsonElement reasonElement) => reasonElement.GetString(),
                    _ => null
                };

                return NormalizeAuditText(reason, 160);
            }
            catch (JsonException)
            {
                return NormalizeAuditText(payloadJson, 160);
            }
        }

        private async Task<bool> EnsureProductionOperatorSessionAsync(string operation, string? inspectionId = null)
        {
            if (!_appConfig.RequireOperatorForProductionStart)
            {
                return true;
            }

            OperatorSession session = _operatorSessionService.Current;
            if (session.IsSignedIn)
            {
                return true;
            }

            string message = $"{operation}已阻止: 当前未登录操作员，无法建立生产追溯";
            RecordHealthError("Operator", message, inspectionId);
            WriteAuditLogSafe(
                "Permission",
                "ProductionStartBlocked",
                $"Operation={operation}; Operator={session.OperatorName}; Role={session.Role}; Required=Operator",
                success: false);
            await _uiController.LogToFrontend(message, "warning");
            await _uiController.SendOperatorSession(session);
            await _uiController.SendUiCommand("toast", new
            {
                message = "请先登录操作员，再启动自动生产触发",
                type = "warning",
                durationMs = 3200
            });
            return false;
        }

        private string SaveConfigVersionForAudit(string action, string changeSummary)
        {
            try
            {
                OperatorSession session = _operatorSessionService.Current;
                ConfigVersionEntry version = _configVersionStore.SaveVersion(_appConfig, new ConfigVersionCreateOptions
                {
                    Reason = action,
                    OperatorName = session.OperatorName,
                    OperatorRole = session.Role,
                    ShiftName = session.ShiftName,
                    ChangeSummary = changeSummary
                });
                return $"ConfigVersion={version.VersionId}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigVersion] 保存配置版本失败: {ex.Message}");
                string message = ex.Message
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Replace('\t', ' ');
                return $"ConfigVersionError={message}";
            }
        }

        private void WriteConfigChangeAudit(string action, ConfigurationSnapshot beforeSnapshot, string? detail = null)
        {
            ConfigurationSnapshot afterSnapshot = ConfigurationChangeTracker.Capture(_appConfig);
            string changeSummary = ConfigurationChangeTracker.FormatChanges(beforeSnapshot.CompareTo(afterSnapshot));
            string prefix = BuildOperatorAuditContext();
            string versionDetail = SaveConfigVersionForAudit(action, changeSummary);
            string fullDetail = string.IsNullOrWhiteSpace(detail)
                ? $"{prefix}; {changeSummary}; {versionDetail}"
                : $"{prefix}; {detail}; {changeSummary}; {versionDetail}";
            WriteAuditLogSafe("ConfigChange", action, fullDetail, success: true);
        }

        private async Task<bool> EnsureOperatorPermissionAsync(OperatorPermission permission, string operation)
        {
            OperatorPermissionDecision decision = OperatorPermissionService.Authorize(
                _operatorSessionService.Current,
                permission,
                operation);
            if (decision.Allowed)
            {
                return true;
            }

            WriteAuditLogSafe(
                "Permission",
                "Denied",
                $"Operation={decision.Operation}; Operator={decision.OperatorName}; Role={decision.OperatorRole}; Required={decision.RequiredRole}; Reason={decision.Message}",
                success: false);
            await _uiController.SendOperatorSession(_operatorSessionService.Current);
            await _uiController.LogToFrontend($"权限不足: {decision.Message}", "warning");
            await _uiController.SendUiCommand("toast", new
            {
                message = decision.Message,
                type = "warning",
                durationMs = 3200
            });
            return false;
        }

        private async Task OpenCameraWithPermissionAsync()
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.OperateProductionHardware, "打开生产相机"))
            {
                return;
            }

            await btnOpenCamera_LogicAsync();
        }

        private async Task ConnectPlcWithPermissionAsync()
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.OperateProductionHardware, "PLC手动连接"))
            {
                return;
            }

            await ConnectPlcViaServiceAsync();
        }

        private async Task ManualDetectWithPermissionAsync()
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.RunManualInspection, "手动检测"))
            {
                return;
            }

            WriteAuditLogSafe("Inspection", "ManualTrigger", BuildOperatorAuditContext(), success: true);
            await btnCapture_LogicAsync();
        }

        private async Task ManualReleaseWithPermissionAsync(string payloadJson)
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManualRelease, "手动放行"))
            {
                return;
            }

            string reason = ExtractManualReleaseReason(payloadJson);
            if (string.IsNullOrWhiteSpace(reason))
            {
                WriteAuditLogSafe(
                    "PLC",
                    "ManualReleaseBlocked",
                    $"{BuildOperatorAuditContext()}; Address={_appConfig.PlcResultAddress}; Reason=MissingReleaseReason",
                    success: false);
                await _uiController.LogToFrontend("强制放行已阻止: 必须填写放行原因", "warning");
                await _uiController.SendUiCommand("toast", new
                {
                    message = "强制放行必须填写原因",
                    type = "warning",
                    durationMs = 3200
                });
                return;
            }

            await fx_btn_LogicAsync(reason);
        }

        private async Task ChangeModelWithPermissionAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return;
            }

            if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeModel, "切换主模型"))
            {
                return;
            }

            模型名 = modelName;
            await ChangeModelAsync(modelName);
        }

        private async Task ImportConfigMigrationWithPermissionAsync()
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.ImportConfiguration, "导入配置迁移包"))
            {
                return;
            }

            await ImportConfigMigrationAsync();
        }

        private async Task RestoreConfigVersionWithPermissionAsync(string versionId)
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.ImportConfiguration, "恢复配置版本"))
            {
                await _uiController.SendConfigVersionRestoreResult(false, "权限不足，已取消恢复配置版本");
                return;
            }

            if (string.IsNullOrWhiteSpace(versionId))
            {
                await _uiController.SendConfigVersionRestoreResult(false, "配置版本号不能为空");
                return;
            }

            try
            {
                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                ConfigVersionRestoreResult result = _configVersionStore.RestoreVersion(versionId, _appConfig);
                await RefreshRuntimeConfigStateAsync();
                WriteConfigChangeAudit(
                    "RestoreConfigVersion",
                    beforeConfig,
                    $"RestoredVersion={result.Version.VersionId}; VersionCreatedAt={result.Version.CreatedAt:O}");
                await _uiController.SendConfigVersionRestoreResult(true, "配置版本已恢复", result.Version);
                await _uiController.SendConfigVersions(_configVersionStore.ListVersions(100));
                await _uiController.LogToFrontend($"配置版本已恢复: {result.Version.VersionId}", "success");
                await _uiController.SendUiCommand("toast", new
                {
                    message = "配置版本已恢复，运行参数已刷新",
                    type = "success",
                    durationMs = 2600
                });
            }
            catch (Exception ex)
            {
                WriteAuditLogSafe(
                    "ConfigChange",
                    "RestoreConfigVersion",
                    $"{BuildOperatorAuditContext()}; VersionId={versionId}; Error={ex.Message}",
                    success: false);
                await _uiController.SendConfigVersionRestoreResult(false, $"恢复配置版本失败: {ex.Message}");
                await _uiController.LogToFrontend($"恢复配置版本失败: {ex.Message}", "error");
            }
        }

        private async Task AcknowledgeAlarmAsync(string alarmId)
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.BasicOperation, "确认告警"))
            {
                await _uiController.SendAlarmActionResult(false, "权限不足，已取消确认告警");
                return;
            }

            try
            {
                AlarmRecord alarm = _alarmCenterService.Acknowledge(alarmId, _operatorSessionService.Current);
                WriteAuditLogSafe(
                    "Alarm",
                    "Acknowledge",
                    $"{BuildOperatorAuditContext()}; AlarmId={alarm.AlarmId}; Severity={alarm.Severity}; Source={alarm.Source}; Message={alarm.Message}",
                    success: true);
                await _uiController.SendAlarmActionResult(true, "告警已确认", alarm);
                await SendAlarmSnapshotToFrontendAsync();
            }
            catch (Exception ex)
            {
                WriteAuditLogSafe(
                    "Alarm",
                    "Acknowledge",
                    $"{BuildOperatorAuditContext()}; AlarmId={alarmId}; Error={ex.Message}",
                    success: false);
                await _uiController.SendAlarmActionResult(false, $"确认告警失败: {ex.Message}");
            }
        }

        private async Task AcknowledgeAllAlarmsAsync()
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.BasicOperation, "确认全部告警"))
            {
                await _uiController.SendAlarmActionResult(false, "权限不足，已取消确认全部告警");
                return;
            }

            try
            {
                int count = _alarmCenterService.AcknowledgeAll(_operatorSessionService.Current);
                WriteAuditLogSafe(
                    "Alarm",
                    "AcknowledgeAll",
                    $"{BuildOperatorAuditContext()}; Count={count}",
                    success: true);
                await _uiController.SendAlarmActionResult(true, $"已确认 {count} 条告警");
                await SendAlarmSnapshotToFrontendAsync();
            }
            catch (Exception ex)
            {
                WriteAuditLogSafe(
                    "Alarm",
                    "AcknowledgeAll",
                    $"{BuildOperatorAuditContext()}; Error={ex.Message}",
                    success: false);
                await _uiController.SendAlarmActionResult(false, $"确认全部告警失败: {ex.Message}");
            }
        }

        private async Task ExportDiagnosticPackageWithPermissionAsync()
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.ExportDiagnostics, "导出诊断包"))
            {
                return;
            }

            string selectedOutputDirectory = string.Empty;
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "选择诊断包导出目录",
                    UseDescriptionForTitle = true,
                    SelectedPath = Directory.Exists(Path_System)
                        ? Path_System
                        : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                selectedOutputDirectory = dialog.SelectedPath;
                string packagePath = await _appRuntime.ExportDiagnosticPackageAsync(
                    selectedOutputDirectory,
                    _appShutdownCts.Token);
                WriteAuditLogSafe(
                    "Diagnostics",
                    "ExportPackage",
                    $"{BuildOperatorAuditContext()}; Path={packagePath}",
                    success: true);
                await _uiController.LogToFrontend($"诊断包已导出: {packagePath}", "success");
                await _uiController.SendUiCommand("toast", new
                {
                    message = "诊断包已导出",
                    type = "success",
                    durationMs = 2200
                });
            }
            catch (Exception ex)
            {
                WriteAuditLogSafe(
                    "Diagnostics",
                    "ExportPackage",
                    $"{BuildOperatorAuditContext()}; OutputDirectory={selectedOutputDirectory}; Error={ex.Message}",
                    success: false);
                await _uiController.SendUiCommand("alert", new { message = $"导出诊断包失败: {ex.Message}" });
            }
        }

        private async Task ImportModelPackageWithPermissionAsync()
        {
            if (!await EnsureOperatorPermissionAsync(OperatorPermission.ImportModelPackage, "导入模型包"))
            {
                await _uiController.SendModelPackageImportResult(new
                {
                    success = false,
                    message = "权限不足，已取消导入模型包"
                });
                return;
            }

            await ImportModelPackageAsync();
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
                    string[] modelNames = GetModelNames();
                    HealthSnapshot healthSnapshot = _healthMonitor.GetSnapshot();
                    AlarmSnapshot alarmSnapshot = _alarmCenterService.Evaluate(healthSnapshot);
                    await _uiController.SendBootstrapSnapshot(
                        _appConfig,
                        cameras,
                        _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId,
                        modelNames,
                        currentStats,
                        healthSnapshot,
                        _appConfig.StoragePath);
                    await _uiController.SendAlarmSnapshot(alarmSnapshot);
                    await _uiController.SendUiCommand("setRoi", new { rect = SnapshotCurrentROI() });
                    await _uiController.SendModelLabels(_detectionService.GetLabels());
                    await _uiController.SendProjectPresets(ProjectPresetStore.Load());
                    await SendOperatorSessionToFrontendAsync();

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
                _currentROI = Recipe.NormalizeRoi(normalizedRect);
                TrySaveCurrentRecipeSnapshot("ROI更新");
            };

            // 订阅YOLO参数修改事件
            _uiController.OnSetConfidence += async (sender, conf) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeInspectionParameters, "更新置信度阈值"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                _appConfig.Confidence = conf;
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("置信度更新");
                    WriteConfigChangeAudit("SetConfidence", beforeConfig);
                }
            };

            _uiController.OnSetIou += async (sender, iou) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeInspectionParameters, "更新IOU阈值"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                _appConfig.IouThreshold = Math.Clamp(iou, 0f, 1f);
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("IOU阈值更新");
                    WriteConfigChangeAudit("SetIou", beforeConfig);
                }
            };

            // 订阅任务类型修改事件
            _uiController.OnSetTaskType += async (sender, taskType) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeInspectionParameters, "更新YOLO任务类型"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                _appConfig.TaskType = taskType;
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("任务类型更新");
                    WriteConfigChangeAudit("SetTaskType", beforeConfig);
                }
                // 使用检测服务更新任务类型
                _detectionService.SetTaskMode(taskType);
            };

            _uiController.OnSetAuxiliary1Model += async (sender, modelName) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeModel, "更新辅助模型1"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                try
                {
                    if (string.IsNullOrEmpty(modelName))
                    {
                        _detectionService.UnloadAuxiliary1Model();
                        _appConfig.Auxiliary1ModelPath = "";
                        if (_appConfig.Save())
                        {
                            TrySaveCurrentRecipeSnapshot("辅助模型1更新");
                            WriteConfigChangeAudit("SetAuxiliary1Model", beforeConfig, "Model=-");
                        }
                        await _uiController.LogToFrontend("辅助模型1已卸载");
                    }
                    else
                    {
                        if (IsSameModelFile(modelName, _appConfig.CurrentModelFileName))
                        {
                            await _uiController.LogToFrontend("辅助模型1不能与主模型相同", "warning");
                            return;
                        }
                        if (IsSameModelFile(modelName, _appConfig.Auxiliary2ModelPath))
                        {
                            await _uiController.LogToFrontend("辅助模型1不能与辅助模型2相同", "warning");
                            return;
                        }

                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            bool ok = await _detectionService.LoadAuxiliary1ModelAsync(modelPath);
                            if (ok)
                            {
                                _appConfig.Auxiliary1ModelPath = modelName;
                                if (_appConfig.Save())
                                {
                                    TrySaveCurrentRecipeSnapshot("辅助模型1更新");
                                    WriteConfigChangeAudit("SetAuxiliary1Model", beforeConfig, $"Model={modelName}");
                                }
                                await _uiController.LogToFrontend($"? 辅助模型1已加载: {modelName}");
                            }
                            else
                            {
                                await _uiController.LogToFrontend($"辅助模型1加载失败，未保存配置: {modelName}", "error");
                            }
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"辅助模型1文件不存在: {modelName}", "error");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载辅助模型1失败: {ex.Message}", "error");
                }
            };

            _uiController.OnSetAuxiliary2Model += async (sender, modelName) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeModel, "更新辅助模型2"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                try
                {
                    if (string.IsNullOrEmpty(modelName))
                    {
                        _detectionService.UnloadAuxiliary2Model();
                        _appConfig.Auxiliary2ModelPath = "";
                        if (_appConfig.Save())
                        {
                            TrySaveCurrentRecipeSnapshot("辅助模型2更新");
                            WriteConfigChangeAudit("SetAuxiliary2Model", beforeConfig, "Model=-");
                        }
                        await _uiController.LogToFrontend("辅助模型2已卸载");
                    }
                    else
                    {
                        if (IsSameModelFile(modelName, _appConfig.CurrentModelFileName))
                        {
                            await _uiController.LogToFrontend("辅助模型2不能与主模型相同", "warning");
                            return;
                        }
                        if (IsSameModelFile(modelName, _appConfig.Auxiliary1ModelPath))
                        {
                            await _uiController.LogToFrontend("辅助模型2不能与辅助模型1相同", "warning");
                            return;
                        }

                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            bool ok = await _detectionService.LoadAuxiliary2ModelAsync(modelPath);
                            if (ok)
                            {
                                _appConfig.Auxiliary2ModelPath = modelName;
                                if (_appConfig.Save())
                                {
                                    TrySaveCurrentRecipeSnapshot("辅助模型2更新");
                                    WriteConfigChangeAudit("SetAuxiliary2Model", beforeConfig, $"Model={modelName}");
                                }
                                await _uiController.LogToFrontend($"? 辅助模型2已加载: {modelName}");
                            }
                            else
                            {
                                await _uiController.LogToFrontend($"辅助模型2加载失败，未保存配置: {modelName}", "error");
                            }
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"辅助模型2文件不存在: {modelName}", "error");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载辅助模型2失败: {ex.Message}", "error");
                }
            };

            _uiController.OnToggleMultiModelFallback += async (sender, enabled) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ChangeModel, "更新多模型策略"))
                {
                    return;
                }

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                _appConfig.EnableMultiModelFallback = enabled;
                _detectionService.SetEnableFallback(enabled);
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("多模型策略更新");
                    WriteConfigChangeAudit("ToggleMultiModelFallback", beforeConfig, $"Enabled={enabled}");
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
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageProjectPreset, "保存项目预设"))
                {
                    return;
                }

                try
                {
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
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageProjectPreset, "删除项目预设"))
                {
                    return;
                }

                try
                {
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
                InvokeOnUIThread(() => SafeFireAndForget(ImportConfigMigrationWithPermissionAsync(), "导入配置迁移"));

            _uiController.OnGetConfigVersions += async (sender, e) =>
            {
                try
                {
                    await _uiController.SendConfigVersions(_configVersionStore.ListVersions(100));
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载配置版本失败: {ex.Message}", "error");
                }
            };

            _uiController.OnRestoreConfigVersion += (sender, versionId) =>
                InvokeOnUIThread(() => SafeFireAndForget(RestoreConfigVersionWithPermissionAsync(versionId), "恢复配置版本"));

            _uiController.OnExportDiagnosticPackage += (sender, e) =>
                InvokeOnUIThread(() => SafeFireAndForget(ExportDiagnosticPackageWithPermissionAsync(), "导出诊断包"));

            // 订阅配置保存事件
            _uiController.OnSaveSettings += async (sender, configJson) =>
            {
                if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageSettings, "保存系统设置"))
                {
                    return;
                }

                try
                {
                    ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
                    // 使用 JsonDocument 解析，允许部分更新
                    using (JsonDocument doc = JsonDocument.Parse(configJson))
                    {
                        var root = doc.RootElement;

                        // 逐个读取并更新配置属性
                        if (root.TryGetProperty("StoragePath", out var sp)) _appConfig.StoragePath = sp.GetString() ?? _appConfig.StoragePath;
                        if (root.TryGetProperty("DataRetentionEnabled", out var dre)) _appConfig.DataRetentionEnabled = dre.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("RequireOperatorForProductionStart", out var rops))
                        {
                            _appConfig.RequireOperatorForProductionStart = rops.ValueKind == JsonValueKind.True;
                        }
                        if (root.TryGetProperty("OperatorSessionMaxHours", out var osmh))
                        {
                            _appConfig.OperatorSessionMaxHours = osmh.TryGetInt32(out int osmhVal)
                                ? Math.Clamp(osmhVal, 1, 72)
                                : _appConfig.OperatorSessionMaxHours;
                        }
                        if (root.TryGetProperty("ImageRetentionDays", out var ird))
                        {
                            _appConfig.ImageRetentionDays = ird.TryGetInt32(out int irdVal)
                                ? Math.Clamp(irdVal, 1, 3650)
                                : _appConfig.ImageRetentionDays;
                        }
                        if (root.TryGetProperty("LogRetentionDays", out var lrd))
                        {
                            _appConfig.LogRetentionDays = lrd.TryGetInt32(out int lrdVal)
                                ? Math.Clamp(lrdVal, 1, 3650)
                                : _appConfig.LogRetentionDays;
                        }
                        if (root.TryGetProperty("AuditLogRetentionDays", out var ard))
                        {
                            _appConfig.AuditLogRetentionDays = ard.TryGetInt32(out int ardVal)
                                ? Math.Clamp(ardVal, 1, 3650)
                                : _appConfig.AuditLogRetentionDays;
                        }
                        if (root.TryGetProperty("ReportRetentionDays", out var rrd))
                        {
                            _appConfig.ReportRetentionDays = rrd.TryGetInt32(out int rrdVal)
                                ? Math.Clamp(rrdVal, 1, 3650)
                                : _appConfig.ReportRetentionDays;
                        }
                        if (root.TryGetProperty("TraceRecordRetentionDays", out var trrd))
                        {
                            _appConfig.TraceRecordRetentionDays = trrd.TryGetInt32(out int trrdVal)
                                ? Math.Clamp(trrdVal, 1, 3650)
                                : _appConfig.TraceRecordRetentionDays;
                        }

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
                            throw new InvalidOperationException("选择串口光电触发时，必须先选择 COM 口");
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

                        if (!PlcFactory.TryParseProtocol(plcProtocol, out PlcProtocolType plcProtocolType))
                        {
                            throw new InvalidOperationException(
                                $"PLC 协议无效: {plcProtocol}。支持: {string.Join(", ", Enum.GetNames<PlcProtocolType>())}");
                        }

                        plcProtocol = plcProtocolType.ToString();
                        if (!PlcFactory.TryNormalizeDriverProvider(plcDriverProvider, out plcDriverProvider))
                        {
                            throw new InvalidOperationException("PLC 驱动库仅支持 Hsl、HaoCommunication、McpX");
                        }

                        bool isMitsubishiProtocol =
                            plcProtocolType == PlcProtocolType.Mitsubishi_MC_ASCII ||
                            plcProtocolType == PlcProtocolType.Mitsubishi_MC_Binary;

                        if (string.Equals(plcDriverProvider, "McpX", StringComparison.OrdinalIgnoreCase) && !isMitsubishiProtocol)
                        {
                            throw new InvalidOperationException("仅三菱协议支持 McpX 驱动库");
                        }

                        bool requiresHandshakeAddresses = plcProtocolMode == PlcProtocolMode.HandshakeV1;
                        plcTriggerAddress = NormalizeRequiredPlcAddressForSave(plcTriggerAddress, plcProtocolType, plcDriverProvider);
                        plcResultAddress = NormalizeRequiredPlcAddressForSave(plcResultAddress, plcProtocolType, plcDriverProvider);
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
                        barcodeAddress = NormalizeOptionalPlcAddressForSave(barcodeAddress, plcProtocolType, plcDriverProvider, 570, barcodeEnabled);

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
                        if (root.TryGetProperty("PlcWriteRetryCount", out var plcWriteRetryCount))
                        {
                            _appConfig.PlcWriteRetryCount = plcWriteRetryCount.TryGetInt32(out int retryCountVal)
                                ? Math.Clamp(retryCountVal, 0, 5)
                                : _appConfig.PlcWriteRetryCount;
                        }
                        if (root.TryGetProperty("PlcWriteRetryIntervalMs", out var plcWriteRetryInterval))
                        {
                            _appConfig.PlcWriteRetryIntervalMs = plcWriteRetryInterval.TryGetInt32(out int retryIntervalVal)
                                ? Math.Clamp(retryIntervalVal, 0, 60000)
                                : _appConfig.PlcWriteRetryIntervalMs;
                        }
                        if (root.TryGetProperty("InspectionCycleSlaEnabled", out var cycleEnabled))
                        {
                            _appConfig.InspectionCycleSlaEnabled = cycleEnabled.ValueKind == JsonValueKind.True;
                        }
                        if (root.TryGetProperty("InspectionCycleWarningMs", out var cycleWarn))
                        {
                            _appConfig.InspectionCycleWarningMs = cycleWarn.TryGetInt32(out int cycleWarnVal)
                                ? Math.Clamp(cycleWarnVal, 100, 600000)
                                : _appConfig.InspectionCycleWarningMs;
                        }
                        if (root.TryGetProperty("InspectionCycleCriticalMs", out var cycleCritical))
                        {
                            _appConfig.InspectionCycleCriticalMs = cycleCritical.TryGetInt32(out int cycleCriticalVal)
                                ? Math.Clamp(cycleCriticalVal, _appConfig.InspectionCycleWarningMs, 600000)
                                : _appConfig.InspectionCycleCriticalMs;
                        }
                        if (root.TryGetProperty("InspectionCycleMinSamples", out var cycleSamples))
                        {
                            _appConfig.InspectionCycleMinSamples = cycleSamples.TryGetInt32(out int cycleSamplesVal)
                                ? Math.Clamp(cycleSamplesVal, 1, 200)
                                : _appConfig.InspectionCycleMinSamples;
                        }
                        if (root.TryGetProperty("QualityYieldSlaEnabled", out var yieldEnabled))
                        {
                            _appConfig.QualityYieldSlaEnabled = yieldEnabled.ValueKind == JsonValueKind.True;
                        }
                        if (root.TryGetProperty("QualityYieldWarningPercent", out var yieldWarn) &&
                            yieldWarn.TryGetDouble(out double yieldWarnVal))
                        {
                            _appConfig.QualityYieldWarningPercent = Math.Clamp(yieldWarnVal, 0d, 100d);
                        }
                        if (root.TryGetProperty("QualityYieldCriticalPercent", out var yieldCritical) &&
                            yieldCritical.TryGetDouble(out double yieldCriticalVal))
                        {
                            _appConfig.QualityYieldCriticalPercent = Math.Clamp(
                                yieldCriticalVal,
                                0d,
                                _appConfig.QualityYieldWarningPercent);
                        }
                        if (root.TryGetProperty("QualityYieldMinSamples", out var yieldSamples))
                        {
                            _appConfig.QualityYieldMinSamples = yieldSamples.TryGetInt32(out int yieldSamplesVal)
                                ? Math.Clamp(yieldSamplesVal, 1, 200)
                                : _appConfig.QualityYieldMinSamples;
                        }
                        if (root.TryGetProperty("ConsecutiveNgAlarmEnabled", out var ngEnabled))
                        {
                            _appConfig.ConsecutiveNgAlarmEnabled = ngEnabled.ValueKind == JsonValueKind.True;
                        }
                        if (root.TryGetProperty("ConsecutiveNgWarningCount", out var ngWarn))
                        {
                            _appConfig.ConsecutiveNgWarningCount = ngWarn.TryGetInt32(out int ngWarnVal)
                                ? Math.Clamp(ngWarnVal, 1, 200)
                                : _appConfig.ConsecutiveNgWarningCount;
                        }
                        if (root.TryGetProperty("ConsecutiveNgCriticalCount", out var ngCritical))
                        {
                            _appConfig.ConsecutiveNgCriticalCount = ngCritical.TryGetInt32(out int ngCriticalVal)
                                ? Math.Clamp(ngCriticalVal, _appConfig.ConsecutiveNgWarningCount, 200)
                                : _appConfig.ConsecutiveNgCriticalCount;
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
                        SaveCurrentRecipeSnapshot();

                        // 更新相关路径
                        _appRuntime.ApplyRuntimeStoragePath();
                        _uiController.ImageBasePath = Path_Images;
                        _uiController.LogBasePath = Path_Logs;
                        InitDirectories();
                        _uiController.SetImageMapping(Path_Images);

                        // 重新初始化YOLO(如果GPU设置改变)
                        InitYolo();
                        RefreshStartupDiagnostics();

                        // 根据 TriggerSource 切换触发源；PLC 通讯连接保留用于写回/条码，监听只按触发源启动。
                        _ = StartTriggerSourceAsync();

                        await _uiController.SendUiCommand("closeSettingsModal");
                        await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");
                        await _uiController.InitSettings(_appConfig);
                        await SendHealthSnapshotToFrontendAsync();
                        string changeSummary = ConfigurationChangeTracker.FormatChanges(
                            beforeConfig.CompareTo(ConfigurationChangeTracker.Capture(_appConfig)));
                        string versionDetail = SaveConfigVersionForAudit("SaveSettings", changeSummary);
                        WriteAuditLogSafe(
                            "Settings",
                            "Save",
                            $"{BuildOperatorAuditContext()}; {changeSummary}; {versionDetail}; StoragePath={_appConfig.StoragePath}; Camera={_appConfig.ActiveCamera?.DisplayName ?? "-"}; " +
                            $"TriggerSource={_appConfig.TriggerSource}; PLC={_appConfig.PlcDriverProvider}/{_appConfig.PlcProtocol}@{_appConfig.PlcIp}:{_appConfig.PlcPort}; " +
                            $"Model={_appConfig.CurrentModelFileName}; Confidence={_appConfig.Confidence}; Iou={_appConfig.IouThreshold}",
                            success: true);
                        await _uiController.LogToFrontend("? 系统设置已更新", "success");
                    }
                }
                catch (Exception ex)
                {
                    WriteAuditLogSafe("Settings", "Save", $"Error={ex.Message}", success: false);
                    await _uiController.SendUiCommand("alert", new { message = $"保存失败: {ex.Message}" });
                }
            };

            // 订阅选择文件夹事件
            _uiController.OnSelectStorageFolder += (sender, e) =>
            {
                InvokeOnUIThread(async () =>
                {
                    if (!await EnsureOperatorPermissionAsync(OperatorPermission.ManageStorage, "选择存储目录"))
                    {
                        return;
                    }

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

        private async Task ImportModelPackageAsync()
        {
            string selectedModelPath = string.Empty;
            ModelPackageDialogData? dialogData = null;
            ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
            try
            {
                using var openDialog = new OpenFileDialog
                {
                    Title = "导入 ONNX 模型并生成模型包",
                    Filter = "ONNX 模型 (*.onnx)|*.onnx|所有文件 (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };

                if (openDialog.ShowDialog(this) != DialogResult.OK)
                {
                    await _uiController.SendModelPackageImportResult(new
                    {
                        success = false,
                        message = "已取消导入"
                    });
                    return;
                }

                selectedModelPath = openDialog.FileName;
                dialogData = ShowModelPackageImportDialog(openDialog.FileName);
                if (dialogData == null)
                {
                    await _uiController.SendModelPackageImportResult(new
                    {
                        success = false,
                        message = "已取消导入"
                    });
                    return;
                }

                ModelPackageImportResult result = _appRuntime.ImportModelPackage(new ModelPackageImportOptions
                {
                    SourceModelPath = openDialog.FileName,
                    OnnxDirectory = 模型路径,
                    ModelId = dialogData.ModelId,
                    Version = dialogData.Version,
                    Labels = dialogData.Labels,
                    Description = dialogData.Description,
                    OverwriteExisting = dialogData.OverwriteExisting,
                    StrictValidation = dialogData.StrictValidation,
                    Warmup = dialogData.StrictValidation ? null : (_, _) => true
                });

                if (!result.Success)
                {
                    await _uiController.SendModelPackageImportResult(new
                    {
                        success = false,
                        message = result.Message
                    });
                    WriteAuditLogSafe(
                        "ModelPackage",
                        "Import",
                        $"Source={Path.GetFileName(selectedModelPath)}; ModelId={dialogData.ModelId}; Version={dialogData.Version}; Error={result.Message}",
                        success: false);
                    await _uiController.LogToFrontend($"模型包导入失败: {result.Message}", "error");
                    return;
                }

                string modelFileName = !string.IsNullOrWhiteSpace(result.PublishedOnnxPath)
                    ? Path.GetFileName(result.PublishedOnnxPath)
                    : result.RegistryEntry?.UsedModelName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(modelFileName))
                {
                    模型名 = modelFileName;
                    _appConfig.CurrentModelFileName = modelFileName;
                    if (_appConfig.Save())
                    {
                        TrySaveCurrentRecipeSnapshot("模型包导入");
                        WriteConfigChangeAudit("ImportModelPackage", beforeConfig, $"ModelFile={modelFileName}");
                    }
                }

                await InitModelList();
                await InitYoloAsync();
                RefreshStartupDiagnostics();
                await _uiController.InitSettings(_appConfig);
                await SendHealthSnapshotToFrontendAsync();

                await _uiController.SendModelPackageImportResult(new
                {
                    success = true,
                    modelId = result.Manifest?.ModelId ?? result.RegistryEntry?.ModelId ?? dialogData.ModelId,
                    version = result.Manifest?.Version ?? dialogData.Version,
                    modelFileName,
                    packageDirectory = result.PackageDirectory,
                    manifestPath = result.ManifestPath,
                    message = result.Message
                });
                WriteAuditLogSafe(
                    "ModelPackage",
                    "Import",
                    $"Source={Path.GetFileName(selectedModelPath)}; ModelId={result.Manifest?.ModelId ?? dialogData.ModelId}; " +
                    $"Version={result.Manifest?.Version ?? dialogData.Version}; ModelFile={modelFileName}; Package={result.PackageDirectory}",
                    success: true);
                await _uiController.LogToFrontend($"模型包导入完成: {result.PackageDirectory}", "success");
            }
            catch (Exception ex)
            {
                await _uiController.SendModelPackageImportResult(new
                {
                    success = false,
                    message = ex.Message
                });
                WriteAuditLogSafe(
                    "ModelPackage",
                    "Import",
                    $"Source={Path.GetFileName(selectedModelPath)}; ModelId={dialogData?.ModelId ?? "-"}; Error={ex.Message}",
                    success: false);
                await _uiController.LogToFrontend($"模型包导入异常: {ex.Message}", "error");
            }
        }

        private ModelPackageDialogData? ShowModelPackageImportDialog(string sourceModelPath)
        {
            string defaultModelId = Path.GetFileNameWithoutExtension(sourceModelPath) ?? "model";
            string defaultVersion = DateTime.Now.ToString("yyyyMMdd.HHmmss", CultureInfo.InvariantCulture);
            string[] existingLabels = _detectionService.GetLabels() ?? Array.Empty<string>();
            string defaultLabels = existingLabels.Length > 0
                ? string.Join(",", existingLabels.Where(label => !string.IsNullOrWhiteSpace(label)))
                : _appConfig.TargetLabel;

            using var dialog = new Form
            {
                Text = "模型包信息",
                StartPosition = FormStartPosition.CenterParent,
                Width = 520,
                Height = 360,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 7
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++)
            {
                layout.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.Absolute, i == 6 ? 100 : 36));
            }

            var modelIdBox = new TextBox { Text = defaultModelId, Dock = DockStyle.Fill };
            var versionBox = new TextBox { Text = defaultVersion, Dock = DockStyle.Fill };
            var labelsBox = new TextBox { Text = defaultLabels, Dock = DockStyle.Fill };
            var descriptionBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 70 };
            var overwriteBox = new CheckBox { Text = "覆盖同名模型包和同名 ONNX 文件", AutoSize = true };
            var strictBox = new CheckBox { Text = "严格 warmup 验收 ONNX 结构", AutoSize = true };

            AddDialogRow(layout, 0, "模型包 ID", modelIdBox);
            AddDialogRow(layout, 1, "版本", versionBox);
            AddDialogRow(layout, 2, "标签", labelsBox);
            AddDialogRow(layout, 3, "", overwriteBox);
            AddDialogRow(layout, 4, "", strictBox);
            AddDialogRow(layout, 5, "说明", descriptionBox);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill
            };
            var okButton = new Button { Text = "导入", DialogResult = DialogResult.OK, Width = 88 };
            var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88 };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(buttons, 0, 6);
            layout.SetColumnSpan(buttons, 2);

            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return null;
            }

            return new ModelPackageDialogData(
                modelIdBox.Text.Trim(),
                versionBox.Text.Trim(),
                SplitLabels(labelsBox.Text),
                descriptionBox.Text.Trim(),
                overwriteBox.Checked,
                strictBox.Checked);
        }

        private static void AddDialogRow(TableLayoutPanel layout, int row, string label, Control editor)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
            layout.Controls.Add(editor, 1, row);
        }

        private static string[] SplitLabels(string labels)
        {
            return (labels ?? string.Empty)
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private sealed record ModelPackageDialogData(
            string ModelId,
            string Version,
            string[] Labels,
            string Description,
            bool OverwriteExisting,
            bool StrictValidation);

        private async Task ExportConfigMigrationAsync()
        {
            string selectedExportPath = string.Empty;
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

                selectedExportPath = dialog.FileName;
                string appVersion = AppVersion.InformationalVersion;
                ConfigMigrationExportResult result = ConfigMigrationService.Export(_appConfig, dialog.FileName, appVersion);
                WriteAuditLogSafe(
                    "ConfigMigration",
                    "Export",
                    $"Path={result.Path}; Presets={result.PresetCount}; AppVersion={appVersion}",
                    success: true);
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
                WriteAuditLogSafe("ConfigMigration", "Export", $"Path={selectedExportPath}; Error={ex.Message}", success: false);
                await _uiController.SendUiCommand("alert", new { message = $"导出配置迁移失败: {ex.Message}" });
            }
        }

        private async Task ImportConfigMigrationAsync()
        {
            string selectedImportPath = string.Empty;
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

                selectedImportPath = dialog.FileName;
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

                ConfigurationSnapshot beforeConfig = ConfigurationChangeTracker.Capture(_appConfig);
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
                if (result.HasConfig)
                {
                    WriteConfigChangeAudit(
                        "ImportConfigMigration",
                        beforeConfig,
                        $"Path={selectedImportPath}; Kind={result.Kind}; RefreshSucceeded={refreshSucceeded}");
                }

                WriteAuditLogSafe(
                    "ConfigMigration",
                    "Import",
                    $"Path={selectedImportPath}; HasConfig={result.HasConfig}; Presets={result.PresetCount}; RefreshSucceeded={refreshSucceeded}",
                    success: refreshSucceeded);
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
                WriteAuditLogSafe("ConfigMigration", "Import", $"Path={selectedImportPath}; Error={ex.Message}", success: false);
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
                await RefreshRuntimeConfigStateAsync();
            }

            await _uiController.SendProjectPresets(ProjectPresetStore.Load());
            if (!result.HasConfig)
            {
                await SendConfiguredCameraListToFrontendAsync();
                await SendHealthSnapshotToFrontendAsync();
            }
        }

        private async Task RefreshRuntimeConfigStateAsync()
        {
            try
            {
                _cameraService.StopCapture();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigRefresh] StopCapture before camera reload failed: {ex.Message}");
            }

            _cameraManager.ReloadFromConfig(_appConfig);

            SaveCurrentRecipeSnapshot();
            YoloDetector.IndustrialRenderMode = _appConfig.IndustrialRenderMode;
            _uiController.UseFileBackedImageTransport = _appConfig.UseFileBackedWebImageTransport;
            _detectionService.SetTaskMode(_appConfig.TaskType);

            _appRuntime.ApplyRuntimeStoragePath();
            _uiController.ImageBasePath = Path_Images;
            _uiController.LogBasePath = Path_Logs;
            InitDirectories();
            _uiController.SetImageMapping(Path_Images);

            模型名 = _appConfig.CurrentModelFileName?.Trim() ?? string.Empty;
            await WarnMissingImportedModelFilesAsync();
            _appRuntime.RefreshModelRegistry();
            InitYolo();
            RefreshStartupDiagnostics();
            _ = StartTriggerSourceAsync();

            await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");
            await _uiController.InitSettings(_appConfig);
            await _uiController.SendModelList(GetModelNames());
            await SendConfiguredCameraListToFrontendAsync();
            await SendHealthSnapshotToFrontendAsync();
        }

        private async Task WarnMissingImportedModelFilesAsync()
        {
            if (!Directory.Exists(模型路径))
            {
                await _uiController.LogToFrontend($"模型目录不存在: {模型路径}", "warning");
                return;
            }

            var modelNames = new[]
            {
                _appConfig.CurrentModelFileName,
                _appConfig.Auxiliary1ModelPath,
                _appConfig.Auxiliary2ModelPath
            }
                .Select(name => name?.Trim() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string modelName in modelNames)
            {
                if (!File.Exists(Path.Combine(模型路径, modelName)))
                {
                    await _uiController.LogToFrontend($"导入配置引用的模型文件不存在: {modelName}", "warning");
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
        /// 根据 TriggerSource 启动对应触发源
        /// </summary>
        private async Task StartTriggerSourceAsync()
        {
            if (!await EnsureProductionOperatorSessionAsync("自动生产触发监听"))
            {
                _serialTriggerService.Stop();
                _plcService.StopMonitoring();
                return;
            }

            if (_appConfig.TriggerSource == TriggerSource.SerialPhotoelectric)
            {
                _plcService.StopMonitoring();

                if (!IsCameraReadyForInspection(out string cameraBlockReason))
                {
                    _serialTriggerService.Stop();
                    await _uiController.LogToFrontend(
                        $"串口光电触发暂未启动: {cameraBlockReason}",
                        "warning");
                }
                else if (!string.IsNullOrWhiteSpace(_appConfig.SerialPhotoelectricPortName))
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
                    }
                }
                else
                {
                    await _uiController.LogToFrontend("串口光电 COM 口未配置，跳过自动启动", "warning");
                }

                await ConnectPlcViaServiceAsync(startTriggerMonitoring: false);
            }
            else
            {
                _serialTriggerService.Stop();
                await StartPlcTriggerMonitoringIfReadyAsync();
            }
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

            string nameA = Path.GetFileNameWithoutExtension(a.Trim());
            string nameB = Path.GetFileNameWithoutExtension(b.Trim());
            return string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        }

        private async Task InitModelList()
        {
            await _uiController.LogToFrontend("开始加载模型列表...");
            _appRuntime.RefreshModelRegistry();
            RefreshStartupDiagnostics();
            await SendHealthSnapshotToFrontendAsync();

            if (!Directory.Exists(模型路径))
            {
                await _uiController.LogToFrontend($"模型目录不存在: {模型路径}", "warning");
                await _uiController.SendModelList(Array.Empty<string>());
                return;
            }

            var names = GetModelNames();
            await _uiController.LogToFrontend($"找到 {names.Length} 个ONNX模型文件");

            // Push to Frontend (Requirement from Step 177/147)
            await _uiController.SendModelList(names!);
            await _uiController.LogToFrontend($"? 已通过 SendModelList 推送 {names.Length} 个模型");
        }

        private string[] GetModelNames()
        {
            if (!Directory.Exists(模型路径))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(模型路径, "*.onnx")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToArray();
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
                    await RunDataRetentionCleanupAsync();
                    await Task.Delay(TimeSpan.FromHours(24));
                }
            });
        }

        private async Task RunDataRetentionCleanupAsync()
        {
            if (!_appConfig.DataRetentionEnabled)
            {
                return;
            }

            try
            {
                var service = new DataRetentionService(BaseStoragePath);
                DataRetentionCleanupSummary summary = await service.CleanupAsync(new DataRetentionPolicy
                {
                    Enabled = _appConfig.DataRetentionEnabled,
                    ImageRetentionDays = _appConfig.ImageRetentionDays,
                    LogRetentionDays = _appConfig.LogRetentionDays,
                    AuditLogRetentionDays = _appConfig.AuditLogRetentionDays,
                    ReportRetentionDays = _appConfig.ReportRetentionDays,
                    TraceRecordRetentionDays = _appConfig.TraceRecordRetentionDays
                }, _databaseService);

                string detail = FormatDataRetentionSummary(summary);
                WriteAuditLogSafe("DataRetention", "Cleanup", detail, summary.ErrorCount == 0);

                if (summary.TotalDeletedItems > 0 || summary.ErrorCount > 0)
                {
                    string type = summary.ErrorCount == 0 ? "info" : "warning";
                    SafeFireAndForget(_uiController.LogToFrontend($"数据保留清理完成: {detail}", type), "数据保留清理日志");
                }
            }
            catch (Exception ex)
            {
                WriteAuditLogSafe("DataRetention", "Cleanup", $"Error={ex.Message}", success: false);
                Debug.WriteLine($"[DataRetention] 清理异常: {ex.Message}");
            }
        }

        private static string FormatDataRetentionSummary(DataRetentionCleanupSummary summary)
        {
            return $"Images={summary.ImageDirectoriesDeleted}; LogDirs={summary.LogDirectoriesDeleted}; " +
                   $"LogFiles={summary.LogFilesDeleted}; Reports={summary.ReportFilesDeleted}; " +
                   $"TraceRecords={summary.TraceRecordsDeleted}; Errors={summary.ErrorCount}";
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
                HealthSnapshot healthSnapshot = _healthMonitor.GetSnapshot();
                AlarmSnapshot alarmSnapshot = _alarmCenterService.Evaluate(healthSnapshot);
                await _uiController.SendHealthSnapshot(healthSnapshot);
                await _uiController.SendAlarmSnapshot(alarmSnapshot);
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

        private async Task SendAlarmSnapshotToFrontendAsync()
        {
            try
            {
                await _uiController.SendAlarmSnapshot(_alarmCenterService.GetSnapshot());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AlarmCenter] 推送告警快照失败: {ex.Message}");
            }
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
                return "Mono8";
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

            return allowed.FirstOrDefault(format => string.Equals(format, raw, StringComparison.OrdinalIgnoreCase)) ?? "Mono8";
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

        #region 数据集自动收集

        private async Task CollectDatasetAsync()
        {
            try
            {
                var service = new ClearFrost.Services.DatasetCollectionService(
                    ClearFrost.Helpers.RuntimePaths.DatabasePath,
                    _appConfig.StoragePath);

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

        #endregion
    }
}
