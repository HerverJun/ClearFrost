using MVSDK_Net;
using ClearFrost.Config;
using ClearFrost.Models;
using ClearFrost.Hardware;
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
            _plcService.ErrorOccurred += (error) =>
            {
                SafeFireAndForget(_uiController.LogToFrontend($"PLC错误: {error}", "error"), "PLC错误日志");
            };

            // Detection 服务事件
            _detectionService.DetectionCompleted += (result) =>
            {
                // 检测完成后的 UI 更新
                SafeFireAndForget(_uiController.LogToFrontend(
                    $"检测完成: {(result.IsQualified ? "合格" : "不合格")} ({result.ElapsedMs}ms)",
                    result.IsQualified ? "success" : "error"), "检测结果日志");
            };
            _detectionService.ModelLoaded += (modelName) =>
            {
                SafeFireAndForget(_uiController.LogToFrontend($"模型已加载: {modelName}", "success"), "模型加载日志");
            };
            _detectionService.ErrorOccurred += (error) =>
            {
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
            _uiController.OnManualRelease += (s, e) => SafeFireAndForget(fx_btn_LogicAsync(), "手动放行"); // Async void handler
            _uiController.OnOpenSettings += (s, e) => InvokeOnUIThread(() => btnSettings_Logic());
            _uiController.OnChangeModel += (s, modelName) => InvokeOnUIThread(() => ChangeModel_Logic(modelName));
            _uiController.OnConnectPlc += (s, e) => SafeFireAndForget(ConnectPlcViaServiceAsync(), "PLC手动连接");
            _uiController.OnThresholdChanged += (s, val) =>
            {
                _appConfig.IouThreshold = Math.Clamp(val / 100f, 0f, 1f);
                _appConfig.Save();
            };
            _uiController.OnGetStatisticsHistory += async (s, e) =>
            {
                // 使用 StatisticsService 获取底层数据
                var stats = ((StatisticsService)_statisticsService).GetDetectionStats();
                var history = ((StatisticsService)_statisticsService).GetStatisticsHistory();
                await _uiController.SendStatisticsHistory(history, stats);
            };
            _uiController.OnClearStatisticsHistory += async (s, e) =>
            {
                _statisticsService.ClearHistory();
                // 刷新历史记录及图表
                var stats = ((StatisticsService)_statisticsService).GetDetectionStats();
                var history = ((StatisticsService)_statisticsService).GetStatisticsHistory();
                await _uiController.SendStatisticsHistory(history, stats);
                await _uiController.LogToFrontend("✅ 历史统计数据已清空", "success");
            };
            _uiController.OnResetStatistics += async (s, e) =>
            {
                _statisticsService.ResetToday();
                await _uiController.UpdateUI(0, 0, 0);
                await _uiController.LogToFrontend("✅ 今日统计已清除", "success");
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
                    exposureTime = c.ExposureTime,
                    gain = c.Gain
                }).ToList();

                await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
            };

            _uiController.OnSwitchCamera += async (s, cameraId) =>
            {
                try
                {
                    _cameraService.StopCapture();

                    var prevCam = _cameraManager.ActiveCamera;
                    if (prevCam != null && prevCam.IsOpen)
                    {
                        prevCam.Close();
                    }

                    _cameraManager.ActiveCameraId = cameraId;
                    var newCam = _cameraManager.ActiveCamera;

                    if (newCam != null)
                    {
                        cam = newCam.Camera;
                        if (newCam.IsOpen)
                        {
                            _cameraService.StartCapture();
                        }
                        _cameraManager.SaveToConfig(_appConfig);
                        _appConfig.Save();

                        await _uiController.LogToFrontend($"✅ 已切换到相机: {newCam.Config.DisplayName}");
                    }
                    else
                    {
                        // 尝试在配置中查找（支持离线切换）
                        var cfgCam = _appConfig.Cameras.FirstOrDefault(c => c.Id == cameraId);
                        if (cfgCam != null)
                        {
                            _appConfig.ActiveCameraId = cameraId;
                            _appConfig.Save();
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
                            ExposureTime = exposure,
                            Gain = gain,
                            IsEnabled = true
                        };
                        _appConfig.Cameras.Add(newConfig);

                        // 尝试添加到相机管理器（可能失败如果相机未连接）
                        bool added = _cameraManager.AddCamera(newConfig);
                        if (added)
                        {
                            await _uiController.LogToFrontend($"✅ 已添加新相机: {displayName} ({manufacturer})");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"ℹ️ 相机配置已保存，但设备未连接或SDK加载失败: {displayName}", "warning");
                        }
                    }

                    _appConfig.Save();

                    // 刷新前端列表
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
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
                    _appConfig.Save();

                    await _uiController.LogToFrontend($"? 已删除相机: {camToRemove.DisplayName}");

                    // 刷新前端列表
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
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

            // 相机超级搜索 - 发现局域网中所有相机（复用 CameraManager.AddCamera 的枚举逻辑）
            _uiController.OnSuperSearchCameras += async (s, e) =>
            {
                var cameraList = new List<Dictionary<string, string>>();

                try
                {
                    Debug.WriteLine("[超级搜索] 事件触发开始");
                    await _uiController.LogToFrontend("正在搜索局域网中的所有相机...");

                    // 直接调用 SDK（与 CameraManager.AddCamera 完全一致的调用方式）
                    var deviceList = new IMVDefine.IMV_DeviceList();
                    int res = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                    Debug.WriteLine($"[超级搜索] IMV_EnumDevices 返回码: {res}, 设备数: {deviceList.nDevNum}");

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
                                Debug.WriteLine($"[超级搜索] 发现设备[{i}]: SN='{sn}'");

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
                                Debug.WriteLine($"[超级搜索] 解析设备[{i}]失败: {innerEx.Message}");
                            }
                        }
                    }
                    else if (res != IMVDefine.IMV_OK)
                    {
                        Debug.WriteLine($"[超级搜索] SDK 枚举失败，错误码: {res}");
                    }
                    else
                    {
                        Debug.WriteLine("[超级搜索] 未发现任何设备");
                    }

                    await _uiController.LogToFrontend($"发现 {cameraList.Count} 台相机", cameraList.Count > 0 ? "success" : "warning");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[超级搜索] 异常: {ex}");
                    await _uiController.LogToFrontend($"相机搜索失败: {ex.Message}", "error");
                }

                // 无论成功失败，必须通知前端结束加载状态
                Debug.WriteLine($"[超级搜索] 准备发送 {cameraList.Count} 个结果到前端");
                await _uiController.SendDiscoveredCameras(cameraList);
                Debug.WriteLine("[超级搜索] 完成");
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
                        _appConfig.Save();

                        // 刷新前端相机列表
                        var cameras = _appConfig.Cameras.Select(c => new
                        {
                            id = c.Id,
                            displayName = c.DisplayName,
                            serialNumber = c.SerialNumber,
                            manufacturer = c.Manufacturer,
                            exposureTime = c.ExposureTime,
                            gain = c.Gain
                        }).ToList();
                        await _uiController.SendCameraList(cameras, _appConfig.ActiveCameraId ?? "");
                        await _uiController.LogToFrontend($"相机 [{newConfig.DisplayName}] 已连接", "success");
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
                // UI Controller might not be ready if error happens too early, but we try
                if (_uiController != null)
                {
                    await _uiController.LogToFrontend($"系统初始化异常: {ex.Message}", "error");
                }
                else
                {
                    MessageBox.Show($"初始化严重错误: {ex.Message}");
                }
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
                    await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");

                    // 发送相机列表以消除前端“正在加载相机列表”的提示
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        exposureTime = c.ExposureTime,
                        gain = c.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);

                    // 初始化前端设置 (Sidebar Controls)
                    await _uiController.InitSettings(_appConfig);

                    // 发送已加载的统计数据到前端（修复重启后饼状图不更新的问题）
                    var currentStats = _statisticsService.Current;
                    await _uiController.UpdateUI(currentStats.TotalCount, currentStats.QualifiedCount, currentStats.UnqualifiedCount);
                    if (currentStats.TotalCount > 0)
                    {
                        await _uiController.LogToFrontend($"已加载今日统计: 总计{currentStats.TotalCount}, 合格{currentStats.QualifiedCount}, 不合格{currentStats.UnqualifiedCount}");
                    }

                    await InitModelList();
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
                _currentROI = normalizedRect;
            };

            // 订阅YOLO参数修改事件
            _uiController.OnSetConfidence += (sender, conf) =>
            {
                _appConfig.Confidence = conf;
                _appConfig.Save();
            };

            _uiController.OnSetIou += (sender, iou) =>
            {
                _appConfig.IouThreshold = Math.Clamp(iou, 0f, 1f);
                _appConfig.Save();
            };

            // 订阅任务类型修改事件
            _uiController.OnSetTaskType += (sender, taskType) =>
            {
                _appConfig.TaskType = taskType;
                _appConfig.Save();
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
                        await _uiController.LogToFrontend("辅助模型1已卸载");
                    }
                    else
                    {
                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            await _detectionService.LoadAuxiliary1ModelAsync(modelPath);
                            _appConfig.Auxiliary1ModelPath = modelName;
                            await _uiController.LogToFrontend($"? 辅助模型1已加载: {modelName}");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"辅助模型1文件不存在: {modelName}", "error");
                        }
                    }
                    _appConfig.Save();
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
                        await _uiController.LogToFrontend("辅助模型2已卸载");
                    }
                    else
                    {
                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            await _detectionService.LoadAuxiliary2ModelAsync(modelPath);
                            _appConfig.Auxiliary2ModelPath = modelName;
                            await _uiController.LogToFrontend($"? 辅助模型2已加载: {modelName}");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"辅助模型2文件不存在: {modelName}", "error");
                        }
                    }
                    _appConfig.Save();
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
                _appConfig.Save();
                await _uiController.LogToFrontend(enabled ? "? 多模型自动切换已启用" : "多模型自动切换已禁用");
            };

            // 订阅密码验证事件
            _uiController.OnVerifyPassword += async (sender, password) =>
            {
                if (password == _appConfig.AdminPassword)
                {
                    // 密码正确,关闭密码框并发送配置到前端打开设置界面
                    await _uiController.ExecuteScriptAsync("closePasswordModal();");
                    await _uiController.SendCurrentConfig(_appConfig);
                }
                else
                {
                    // 密码错误
                    await _uiController.ExecuteScriptAsync("alert('密码错误'); closePasswordModal();");
                }
            };

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
                        if (root.TryGetProperty("PlcProtocol", out var ppr)) _appConfig.PlcProtocol = ppr.GetString() ?? _appConfig.PlcProtocol;
                        if (root.TryGetProperty("PlcDriverProvider", out var pdp)) _appConfig.PlcDriverProvider = pdp.GetString() ?? _appConfig.PlcDriverProvider;
                        if (root.TryGetProperty("PlcIp", out var pi)) _appConfig.PlcIp = pi.GetString() ?? _appConfig.PlcIp;
                        if (root.TryGetProperty("PlcPort", out var pp)) _appConfig.PlcPort = pp.TryGetInt32(out int ppVal) ? ppVal : _appConfig.PlcPort;
                        if (root.TryGetProperty("PlcTriggerAddress", out var pt)) _appConfig.PlcTriggerAddress = ParsePlcAddress(pt, _appConfig.PlcTriggerAddress);
                        if (root.TryGetProperty("PlcResultAddress", out var pr)) _appConfig.PlcResultAddress = ParsePlcAddress(pr, _appConfig.PlcResultAddress);
                        if (root.TryGetProperty("PlcTriggerDelayMs", out var ptd)) _appConfig.PlcTriggerDelayMs = ptd.TryGetInt32(out int ptdVal) ? Math.Max(0, ptdVal) : _appConfig.PlcTriggerDelayMs;
                        if (root.TryGetProperty("PlcPollingIntervalMs", out var ppi)) _appConfig.PlcPollingIntervalMs = ppi.TryGetInt32(out int ppiVal) ? Math.Max(50, ppiVal) : _appConfig.PlcPollingIntervalMs;
                        if (root.TryGetProperty("PlcOkValue", out var pok)) _appConfig.PlcOkValue = pok.TryGetInt16(out short pokVal) ? pokVal : _appConfig.PlcOkValue;
                        if (root.TryGetProperty("PlcNgValue", out var png)) _appConfig.PlcNgValue = png.TryGetInt16(out short pngVal) ? pngVal : _appConfig.PlcNgValue;
#pragma warning disable CS0618
                        var activeCam = _appConfig.ActiveCamera;
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
#pragma warning restore CS0618
                        if (root.TryGetProperty("TargetLabel", out var tl)) _appConfig.TargetLabel = tl.GetString() ?? _appConfig.TargetLabel;
                        if (root.TryGetProperty("TargetCount", out var tc)) _appConfig.TargetCount = tc.TryGetInt32(out int tcVal) ? tcVal : _appConfig.TargetCount;
                        if (root.TryGetProperty("MaxRetryCount", out var mrc)) _appConfig.MaxRetryCount = mrc.TryGetInt32(out int mrcVal) ? mrcVal : _appConfig.MaxRetryCount;
                        if (root.TryGetProperty("RetryIntervalMs", out var rim)) _appConfig.RetryIntervalMs = rim.TryGetInt32(out int rimVal) ? rimVal : _appConfig.RetryIntervalMs;
                        if (root.TryGetProperty("TaskType", out var taskType)) _appConfig.TaskType = taskType.TryGetInt32(out int taskTypeVal) ? taskTypeVal : _appConfig.TaskType;
                        if (root.TryGetProperty("EnableGpu", out var eg)) _appConfig.EnableGpu = eg.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("IndustrialRenderMode", out var irm)) _appConfig.IndustrialRenderMode = irm.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("UseFileBackedWebImageTransport", out var fileTransport)) _appConfig.UseFileBackedWebImageTransport = fileTransport.ValueKind == JsonValueKind.True;
                        YoloDetector.IndustrialRenderMode = _appConfig.IndustrialRenderMode;
                        _uiController.UseFileBackedImageTransport = _appConfig.UseFileBackedWebImageTransport;
                        _detectionService.SetTaskMode(_appConfig.TaskType);

                        // 保存并重新加载
                        _appConfig.Save();

                        // 更新相关路径
                        _uiController.ImageBasePath = Path_Images;
                        _uiController.LogBasePath = Path_Logs;
                        InitDirectories();
                        _uiController.SetImageMapping(Path_Images);

                        // 重新初始化YOLO(如果GPU设置改变)
                        InitYolo();

                        // 尝试重新连接PLC (应用新IP/端口)
                        _ = ConnectPlcViaServiceAsync();

                        await _uiController.ExecuteScriptAsync("closeSettingsModal();");
                        await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");
                        await _uiController.LogToFrontend("? 系统设置已更新", "success");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.ExecuteScriptAsync($"alert('保存失败: {ex.Message.Replace("'", "\\'")}');");
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
        }

        private async Task InitModelList()
        {
            await _uiController.LogToFrontend("开始加载模型列表...");

            if (!Directory.Exists(模型路径))
            {
                Directory.CreateDirectory(模型路径);
                await _uiController.LogToFrontend($"创建模型目录: {模型路径}");
            }

            var files = Directory.GetFiles(模型路径, "*.onnx");
            await _uiController.LogToFrontend($"找到 {files.Length} 个ONNX模型文件");

            var names = files.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToArray();

            // Push to Frontend (Requirement from Step 177/147)
            await _uiController.SendModelList(names!);
            await _uiController.LogToFrontend($"? 已通过 SendModelList 推送 {names.Length} 个模型");
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
                ReleaseCameraResources();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 释放相机资源失败: {ex.Message}");
            }

            try
            {
                _cameraManager?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 销毁 CameraManager 失败: {ex.Message}");
            }

            try
            {
                if (plcConnected)
                {
                    _plcService?.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 断开 PLC 失败: {ex.Message}");
            }

            try
            {
                _imageSaveQueue?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 释放图像保存队列失败: {ex.Message}");
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
                Debug.WriteLine($"[Shutdown] 清理超时，强制退出: {source}");

                try
                {
                    _storageService?.WriteStartupLog($"软件关闭超时强退[{source}]", null);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Shutdown] 记录强退日志失败: {ex.Message}");
                }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 监控退出流程异常: {ex.Message}");
                RequestGracefulExit(source);
            }
        }

        private void RequestGracefulExit(string source)
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

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1500);
                        Environment.Exit(0);
                    });

                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] 请求 UI 线程退出失败: {ex.Message}");
            }

            Environment.Exit(0);
        }

        private static short ParsePlcAddress(JsonElement value, short fallback)
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt16(out short shortValue))
                {
                    return shortValue;
                }

                if (value.TryGetInt32(out int intValue) && intValue >= short.MinValue && intValue <= short.MaxValue)
                {
                    return (short)intValue;
                }

                return fallback;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string raw = value.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(raw))
                {
                    return fallback;
                }

                // 兼容现场输入: D100 / d100 / DB1.100 / 100
                if (raw.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                {
                    int dotIndex = raw.LastIndexOf('.');
                    raw = dotIndex >= 0 && dotIndex < raw.Length - 1
                        ? raw.Substring(dotIndex + 1)
                        : raw.Substring(2);
                }
                else if (char.IsLetter(raw[0]))
                {
                    raw = raw.Substring(1);
                }

                if (short.TryParse(raw, out short parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        private void InvokeOnUIThread(Action action)
        {
            if (InvokeRequired) Invoke(action);
            else action();
        }

        #endregion
    }
}


