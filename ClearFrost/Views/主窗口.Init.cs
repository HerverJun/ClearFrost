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
using System.Threading.Tasks;
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
            _uiController.OnOpenCamera += (s, e) => SafeFireAndForget(btnOpenCamera_LogicAsync(), "打开相机");
            _uiController.OnManualDetect += (s, e) => InvokeOnUIThread(() => SafeFireAndForget(btnCapture_LogicAsync(), "手动检测"));
            _uiController.OnCaptureCameraPreview += (s, json) => InvokeOnUIThread(() => SafeFireAndForget(CaptureCameraPreviewFrameAsync(json), "获取相机预览单帧"));
            _uiController.OnManualRelease += (s, e) => SafeFireAndForget(fx_btn_LogicAsync(), "手动放行"); // Async void handler
            _uiController.OnOpenSettings += (s, e) => InvokeOnUIThread(() => btnSettings_Logic());
            _uiController.OnCollectDataset += (s, e) => SafeFireAndForget(CollectDatasetAsync(), "数据集收集");
            _uiController.OnRunHistoryRulePreview += (s, json) => SafeFireAndForget(RunHistoryRulePreviewAsync(json), "历史图规则复判");
            _uiController.OnGetModelList += (s, e) => SafeFireAndForget(InitModelList(), "刷新模型列表");
            _uiController.OnChangeModel += (s, modelName) => InvokeOnUIThread(() => ChangeModel_Logic(modelName));
            _uiController.OnConnectPlc += (s, e) => SafeFireAndForget(ConnectPlcViaServiceAsync(), "PLC手动连接");
            _uiController.OnRequestHealthSnapshot += (s, e) => SafeFireAndForget(SendHealthSnapshotToFrontendAsync(showToast: true), "前端刷新健康快照");
            _uiController.OnThresholdChanged += (s, val) =>
            {
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
                    await _uiController.SendBootstrapSnapshot(
                        _appConfig,
                        cameras,
                        _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId,
                        modelNames,
                        currentStats,
                        _healthMonitor.GetSnapshot(),
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
                _currentROI = Recipe.NormalizeRoi(normalizedRect);
                TrySaveCurrentRecipeSnapshot("ROI更新");
            };

            // 订阅YOLO参数修改事件
            _uiController.OnSetConfidence += (sender, conf) =>
            {
                _appConfig.Confidence = conf;
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("置信度更新");
                }
            };

            _uiController.OnSetIou += (sender, iou) =>
            {
                _appConfig.IouThreshold = Math.Clamp(iou, 0f, 1f);
                if (_appConfig.Save())
                {
                    TrySaveCurrentRecipeSnapshot("IOU阈值更新");
                }
            };

            // 订阅任务类型修改事件
            _uiController.OnSetTaskType += (sender, taskType) =>
            {
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
                    if (string.IsNullOrEmpty(modelName))
                    {
                        _detectionService.UnloadAuxiliary1Model();
                        _appConfig.Auxiliary1ModelPath = "";
                        if (_appConfig.Save())
                        {
                            TrySaveCurrentRecipeSnapshot("辅助模型1更新");
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
                try
                {
                    if (string.IsNullOrEmpty(modelName))
                    {
                        _detectionService.UnloadAuxiliary2Model();
                        _appConfig.Auxiliary2ModelPath = "";
                        if (_appConfig.Save())
                        {
                            TrySaveCurrentRecipeSnapshot("辅助模型2更新");
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
                InvokeOnUIThread(() => SafeFireAndForget(ImportConfigMigrationAsync(), "导入配置迁移"));

            // 订阅配置保存事件
            _uiController.OnSaveSettings += async (sender, configJson) =>
            {
                try
                {
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
                        await _uiController.LogToFrontend("? 系统设置已更新", "success");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.SendUiCommand("alert", new { message = $"保存失败: {ex.Message}" });
                }
            };

            // 订阅选择文件夹事件
            _uiController.OnSelectStorageFolder += (sender, e) =>
            {
                InvokeOnUIThread(async () =>
                {
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

                SaveCurrentRecipeSnapshot();
                YoloDetector.IndustrialRenderMode = _appConfig.IndustrialRenderMode;
                _uiController.UseFileBackedImageTransport = _appConfig.UseFileBackedWebImageTransport;
                _detectionService.SetTaskMode(_appConfig.TaskType);

                _uiController.ImageBasePath = Path_Images;
                _uiController.LogBasePath = Path_Logs;
                InitDirectories();
                _uiController.SetImageMapping(Path_Images);

                模型名 = _appConfig.CurrentModelFileName?.Trim() ?? string.Empty;
                await WarnMissingImportedModelFilesAsync();
                InitYolo();
                RefreshStartupDiagnostics();
                _ = StartTriggerSourceAsync();

                await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");
                await _uiController.InitSettings(_appConfig);
                await _uiController.SendModelList(GetModelNames());
            }

            await _uiController.SendProjectPresets(ProjectPresetStore.Load());
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
                await _uiController.SendHealthSnapshot(_healthMonitor.GetSnapshot());
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
