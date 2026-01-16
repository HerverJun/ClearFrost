using MVSDK_Net;
using OpenCvSharp;
using OpenCvSharp.Extensions;
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
using YoloDetection;

namespace YOLO
{
    public partial class 主窗口 : Form
    {
        #region 阻止休眠 Helper
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern uint SetThreadExecutionState(uint esFlags);
        const uint ES_SYSTEM_REQUIRED = 0x00000001;
        const uint ES_DISPLAY_REQUIRED = 0x00000002;
        const uint ES_CONTINUOUS = 0x80000000;

        /// <summary>
        /// 阻止系统休眠和屏幕关闭
        /// </summary>
        public static void PreventSleep()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            }
            catch { }
        }

        /// <summary>
        /// 恢复系统正常休眠策略
        /// </summary>
        public static void RestoreSleep()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS);
            }
            catch { }
        }
        #endregion

        #region 拖动窗口 Helper
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        /// <summary>
        /// 启动窗口拖动（模拟标题栏拖动）
        /// </summary>
        public void StartWindowDrag()
        {
            ReleaseCapture();
            SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }
        #endregion

        #region 1. 全局变量与配置定义 (Global Definitions)

        // WebUI 控制器
        private WebUIController _uiController;

        // ====================== ROI配置 ======================
        // 注意：由于移除 PictureBox，鼠标绘制 ROI 逻辑暂时失效，仅保留参数供后端计算
        private int roiX = 100;
        private int roiY = 100;
        private int roiWidth = 400;
        private int roiHeight = 400;
        private bool useROI = false;
        private bool isROISet = false;
        private float overlapThreshold = 0.1f;

        // ====================== 文件存储配置 ======================
        private string BaseStoragePath
        {
            get
            {
                string path = _appConfig?.StoragePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return @"C:\GreeVisionData";
                }

                // Check if the drive exists
                try
                {
                    string root = Path.GetPathRoot(path);
                    if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                    {
                        // Fallback if configured drive doesn't exist
                        return @"C:\GreeVisionData";
                    }
                }
                catch { return @"C:\GreeVisionData"; }

                return path;
            }
        }
        private string Path_Images => Path.Combine(BaseStoragePath, "Images");
        private string Path_Logs => Path.Combine(BaseStoragePath, "Logs");
        private string Path_System => Path.Combine(BaseStoragePath, "System");
        private string StartupLogPath => Path.Combine(Path_Logs, "SoftwareStartLog.txt");

        // ====================== 统计 ======================
        private DetectionStatistics detectionStats = null!;
        private StatisticsHistory statisticsHistory = null!;

        // ====================== 硬件设备对象 ======================
        // PLC (支持多协议)
        private IPlcDevice? _plcDevice;
        private bool plcConnected = false;
        private CancellationTokenSource? plcCts;

        // 相机
        private MyCamera cam = new MyCamera();
        private int _targetCameraIndex = -1;
        private Thread? renderThread = null;
        private BlockingCollection<IMVDefine.IMV_Frame> m_frameQueue = new BlockingCollection<IMVDefine.IMV_Frame>(10);
        private CancellationTokenSource m_cts = new CancellationTokenSource();

        // YOLO
        YoloDetector? yolo;
        string 模型路径 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX");
        string 模型名 = "";
        bool 停止 = false;
        private AppConfig _appConfig = AppConfig.Load();

        // ROI归一化坐标 [x, y, w, h] (0.0~1.0)
        private float[]? _currentROI = null;

        #endregion

        #region 2. 初始化与生命周期 (Initialization)

        public 主窗口()
        {
            InitializeComponent();

            // 设置无边框全屏
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;

            // 初始化 WebUI 控制器
            _uiController = new WebUIController();

            // 订阅退出事件
            _uiController.OnExitApp += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    // 停止所有后台任务
                    this.停止 = true;
                    // 保存配置
                    _appConfig?.Save();
                    // 强制退出
                    Application.Exit();
                });
            };

            // 订阅最小化事件
            _uiController.OnMinimizeApp += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    this.WindowState = FormWindowState.Minimized;
                });
            };

            // 订阅最大化/还原事
            _uiController.OnToggleMaximize += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
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
                    StartWindowDrag();
                });
            };

            // 绑定 WebUI 事件
            _uiController.OnOpenCamera += (s, e) => InvokeOnUIThread(() => btnOpenCamera_Logic());
            _uiController.OnManualDetect += (s, e) => InvokeOnUIThread(() => btnCapture_Logic());
            _uiController.OnManualRelease += (s, e) => fx_btn_Logic(); // Async void handler
            _uiController.OnOpenSettings += (s, e) => InvokeOnUIThread(() => btnSettings_Logic());
            _uiController.OnChangeModel += (s, modelName) => InvokeOnUIThread(() => ChangeModel_Logic(modelName));
            _uiController.OnConnectPlc += (s, e) => _ = ConnectToPlcAsync();
            _uiController.OnThresholdChanged += (s, val) =>
            {
                overlapThreshold = val / 100f;
            };
            _uiController.OnGetStatisticsHistory += async (s, e) =>
            {
                await _uiController.SendStatisticsHistory(statisticsHistory, detectionStats);
            };
            _uiController.OnResetStatistics += async (s, e) =>
            {
                detectionStats.Reset();
                detectionStats.Save();
                await _uiController.UpdateUI(0, 0, 0);
                await _uiController.LogToFrontend("✓ 今日统计已清除", "success");
            };

            // 注册窗体关闭事件
            // 注册窗体关闭事件
            this.FormClosing += OnFormClosingHandler;
        }

        private async void 主窗口_Load(object? sender, EventArgs e)
        {
            // 阻止系统休眠
            PreventSleep();

            // 确保无边框全屏
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;

            // 订阅 WebUI 就绪事件
            _uiController.OnAppReady += async (s, ev) =>
            {
                await _uiController.LogToFrontend("✓ WebUI已就绪");
                await _uiController.LogToFrontend("系统初始化完成");
                await _uiController.UpdateCameraName(_appConfig.CameraName);

                // 发送已加载的统计数据到前端（修复重启后饼状图不更新的问题）
                await _uiController.UpdateUI(detectionStats.TotalCount, detectionStats.QualifiedCount, detectionStats.UnqualifiedCount);
                if (detectionStats.TotalCount > 0)
                {
                    await _uiController.LogToFrontend($"已加载今日统计: 总计{detectionStats.TotalCount}, 合格{detectionStats.QualifiedCount}, 不合格{detectionStats.UnqualifiedCount}");
                }

                await InitModelList();
            };

            // 订阅测试YOLO事件
            _uiController.OnTestYolo += TestYolo_Handler;

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
                _appConfig.IouThreshold = iou;
                _appConfig.Save();
            };

            // 订阅密码验证事件
            _uiController.OnVerifyPassword += async (sender, password) =>
            {
                if (password == _appConfig.AdminPassword)
                {
                    // 密码正确,发送配置到前端打开设置界面
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
                        if (root.TryGetProperty("PlcIp", out var pi)) _appConfig.PlcIp = pi.GetString() ?? _appConfig.PlcIp;
                        if (root.TryGetProperty("PlcPort", out var pp)) _appConfig.PlcPort = pp.TryGetInt32(out int ppVal) ? ppVal : _appConfig.PlcPort;
                        if (root.TryGetProperty("PlcTriggerAddress", out var pt)) _appConfig.PlcTriggerAddress = pt.TryGetInt16(out short ptVal) ? ptVal : _appConfig.PlcTriggerAddress;
                        if (root.TryGetProperty("PlcResultAddress", out var pr)) _appConfig.PlcResultAddress = pr.TryGetInt16(out short prVal) ? prVal : _appConfig.PlcResultAddress;
                        if (root.TryGetProperty("CameraName", out var cn)) _appConfig.CameraName = cn.GetString() ?? _appConfig.CameraName;
                        if (root.TryGetProperty("CameraSerialNumber", out var cs)) _appConfig.CameraSerialNumber = cs.GetString() ?? _appConfig.CameraSerialNumber;
                        if (root.TryGetProperty("ExposureTime", out var et)) _appConfig.ExposureTime = et.TryGetDouble(out double etVal) ? etVal : _appConfig.ExposureTime;
                        if (root.TryGetProperty("GainRaw", out var gr)) _appConfig.GainRaw = gr.TryGetDouble(out double grVal) ? grVal : _appConfig.GainRaw;
                        if (root.TryGetProperty("TargetLabel", out var tl)) _appConfig.TargetLabel = tl.GetString() ?? _appConfig.TargetLabel;
                        if (root.TryGetProperty("TargetCount", out var tc)) _appConfig.TargetCount = tc.TryGetInt32(out int tcVal) ? tcVal : _appConfig.TargetCount;
                        if (root.TryGetProperty("EnableGpu", out var eg)) _appConfig.EnableGpu = eg.ValueKind == JsonValueKind.True;

                        // 保存并重新加载
                        _appConfig.Save();

                        // 更新相关路径
                        _uiController.ImageBasePath = Path_Images;
                        _uiController.LogBasePath = Path_Logs;
                        InitDirectories();
                        _uiController.SetImageMapping(Path_Images);

                        // 重新初始化YOLO(如果GPU设置改变)
                        InitYolo();

                        await _uiController.ExecuteScriptAsync("closeSettingsModal();");
                        await _uiController.UpdateCameraName(_appConfig.CameraName);
                        await _uiController.LogToFrontend("✓ 系统设置已更新", "success");
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

            // 初始化 WebUI
            if (webView21 != null)
            {
                await _uiController.InitializeAsync(webView21);
                // 配置 NG 图片查看路径
                _uiController.ImageBasePath = Path_Images;
                _uiController.SetImageMapping(Path_Images);
                // 配置检测日志路径
                _uiController.LogBasePath = Path_Logs;
            }

            // 加载统计数据（从持久化文件）
            detectionStats = DetectionStatistics.Load(BaseStoragePath);
            statisticsHistory = StatisticsHistory.Load(BaseStoragePath);

            // 检测跨日，如果需要则保存历史并重置今日数据
            if (detectionStats.CheckAndResetForNewDay(statisticsHistory))
            {
                _ = _uiController.LogToFrontend("检测到新的一天，统计数据已重置", "info");
            }

            // 初始化YOLO
            InitYolo();
            InitDirectories();

            // 启动后台清理
            StartCleanupTask();

            // LogToFrontend moved to OnAppReady handler
        }

        private async Task InitModelList()
        {
            await _uiController.LogToFrontend("开始加载模型列表...");

            if (!Directory.Exists(模型路径))
            {
                Directory.CreateDirectory(模型路径);
                _uiController.LogToFrontend($"创建模型目录: {模型路径}");
            }

            var files = Directory.GetFiles(模型路径, "*.onnx");
            await _uiController.LogToFrontend($"找到 {files.Length} 个ONNX模型文件");

            var names = files.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToArray();

            // Push to Frontend (Requirement from Step 177/147)
            await _uiController.SendModelList(names!);
            await _uiController.LogToFrontend($"✓ 已通过 SendModelList 推送 {names.Length} 个模型");
        }

        private void InitYolo()
        {
            // 同步调用异步方法
            _ = InitYoloAsync();
        }

        private async Task InitYoloAsync()
        {
            try
            {
                await _uiController.LogToFrontend("正在加载 YOLO 模型...", "info");

                int ver = _appConfig.ModelVersion;
                int gpuIdx = _appConfig.GpuIndex;
                bool useGpu = _appConfig.EnableGpu;

                // 如果没指定模型名，尝试找一个默认的
                if (string.IsNullOrEmpty(模型名))
                {
                    var files = Directory.GetFiles(模型路径, "*.onnx");
                    if (files.Length > 0) 模型名 = Path.GetFileName(files[0]);
                }

                if (!string.IsNullOrEmpty(模型名))
                {
                    // 在后台线程加载模型,避免阻塞UI
                    await Task.Run(() =>
                    {
                        yolo = new YoloDetector(Path.Combine(模型路径, 模型名), ver, gpuIdx, useGpu);
                        yolo.TaskMode = _appConfig.TaskType;
                    });

                    await _uiController.LogToFrontend($"✓ YOLO模型已加载: {模型名}", "success");
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"模型加载失败: {ex.Message}", "error");
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
                    CleanOldData(30);
                    await Task.Delay(TimeSpan.FromHours(24));
                }
            });
        }

        protected void OnFormClosingHandler(object? sender, FormClosingEventArgs e)
        {
            WriteStartupLog("软件关闭", null);

            // 恢复系统休眠策略
            RestoreSleep();

            // 保存统计数据
            detectionStats?.Save();
            statisticsHistory?.Save();

            plcCts?.Cancel();
            if (plcConnected)
            {
                _plcDevice?.Disconnect();
                plcConnected = false;
            }
            ReleaseCameraResources();
        }

        #endregion

        #region 3. PLC控制逻辑 (PLC Control)

        private const int PlcTriggerDelay = 800;
        private const int RetryWaitDelay = 2000;
        private const int PlcPollingInterval = 500;

        // 使用工厂模式创建PLC设备，支持多协议切换
        private async Task ConnectToPlcAsync()
        {
            const int maxRetries = 3;
            const int retryDelay = 2000;
            string ip = _appConfig.PlcIp;
            int port = _appConfig.PlcPort;
            var protocol = PlcFactory.ParseProtocol(_appConfig.PlcProtocol);

            try
            {
                UpdatePlcStatus(false, "连接中...");
                await _uiController.LogToFrontend($"正在使用 {protocol} 协议连接 {ip}:{port}", "info");

                for (int i = 0; i < maxRetries; i++)
                {
                    // 释放旧连接
                    _plcDevice?.Disconnect();
                    // 使用工厂创建对应协议的适配器
                    _plcDevice = PlcFactory.Create(protocol, ip, port);

                    bool connected = await _plcDevice.ConnectAsync();

                    if (connected)
                    {
                        // 连接成功后，尝试读取触发地址验证通讯
                        string address = GetPlcAddress(_appConfig.PlcTriggerAddress);
                        var (success, _) = await _plcDevice.ReadInt16Async(address);

                        if (success)
                        {
                            UpdatePlcStatus(true, $"已连接 ({_plcDevice.ProtocolName})");
                            plcConnected = true;

                            plcCts = new CancellationTokenSource();
                            _ = Task.Run(() => PlcMonitoringLoop(plcCts.Token));
                            return;
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"PLC通讯验证失败: {_plcDevice.LastError}", "error");
                            _plcDevice?.Disconnect();
                        }
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"连接失败: {_plcDevice?.LastError}", "error");
                    }

                    if (i < maxRetries - 1)
                    {
                        UpdatePlcStatus(false, $"重试({i + 2}/{maxRetries})");
                        await Task.Delay(retryDelay);
                    }
                }
                UpdatePlcStatus(false, "连接失败");
            }
            catch (Exception ex)
            {
                HandlePlcError($"连接异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据协议类型生成正确的地址格式
        /// </summary>
        private string GetPlcAddress(short address)
        {
            var protocol = PlcFactory.ParseProtocol(_appConfig.PlcProtocol);
            return protocol switch
            {
                PlcProtocolType.Mitsubishi_MC_ASCII => $"D{address}",
                PlcProtocolType.Mitsubishi_MC_Binary => $"D{address}",
                PlcProtocolType.Modbus_TCP => address.ToString(), // Modbus直接使用数字地址
                PlcProtocolType.Siemens_S7 => $"DB1.{address}", // 西门子默认DB1块
                PlcProtocolType.Omron_Fins => $"D{address}", // 欧姆龙D区
                _ => $"D{address}"
            };
        }

        private async Task PlcMonitoringLoop(CancellationToken token)
        {
            short triggerAddress = _appConfig.PlcTriggerAddress;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_plcDevice == null) break;

                    string address = GetPlcAddress(triggerAddress);
                    var (success, value) = await _plcDevice.ReadInt16Async(address);

                    if (success && value == 1)
                    {
                        await _plcDevice.WriteInt16Async(address, 0);
                        await Task.Delay(PlcTriggerDelay);

                        // 第一次检测
                        DetectionResult? result1 = await InvokeAsync(RunDetectionOnceAsync);

                        if (result1 != null)
                        {
                            if (result1.IsQualified)
                            {
                                ProcessFinalResult(result1);
                            }
                            else
                            {
                                // 第一次NG，仅显示图像，不记录
                                InvokeOnUIThread(() =>
                                {
                                    DisplayImageOnly(result1.OriginalBitmap, result1.Results);
                                    result1.OriginalBitmap?.Dispose();
                                });

                                await Task.Delay(RetryWaitDelay, token);

                                // 第二次检测
                                var result2 = await InvokeAsync(RunDetectionOnceAsync);
                                if (result2 != null)
                                {
                                    ProcessFinalResult(result2);
                                }
                            }
                        }
                    }
                    await Task.Delay(PlcPollingInterval, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    HandlePlcError($"监控异常: {ex.Message}");
                    break;
                }
            }
        }

        private async Task<T> InvokeAsync<T>(Func<Task<T>> task)
        {
            T result = default;
            await this.Invoke(async () => { result = await task(); });
            return result;
        }

        private void InvokeOnUIThread(Action action)
        {
            if (InvokeRequired) Invoke(action);
            else action();
        }

        private void ProcessFinalResult(DetectionResult result)
        {
            if (result == null || result.Results == null || result.OriginalBitmap == null) return;

            InvokeOnUIThread(() =>
            {
                UpdateUIAndPLC(result.IsQualified, result.Results);
                ProcessAndSaveImages(result.OriginalBitmap, result.Results, result.IsQualified);
                result.OriginalBitmap?.Dispose();
            });
        }

        private void DisplayImageOnly(Bitmap? original, List<YoloResult>? results)
        {
            if (yolo == null || original == null || results == null) return;
            using (Bitmap roiMarked = DrawROIBorder(original))
            using (Image finalImg = yolo.GenerateImage(roiMarked, results, yolo.Labels))
            {
                // 推送到 WebUI
                SendImageToWeb(new Bitmap(finalImg));
            }
        }

        public async Task WriteDetectionResult(bool isQualified)
        {
            if (!plcConnected || _plcDevice == null) return;
            string address = GetPlcAddress(_appConfig.PlcResultAddress);
            try
            {
                bool success = await _plcDevice.WriteInt16Async(address, (short)(isQualified ? 1 : 0));
                _uiController.LogToFrontend(success ? $"PLC写入结果{(isQualified ? 1 : 0)}成功" : $"PLC写入失败: {_plcDevice.LastError}", "info");
            }
            catch (Exception ex)
            {
                HandlePlcError($"写入异常: {ex.Message}");
            }
        }

        // 逻辑对应原 fx_btn_Click
        private async void fx_btn_Logic()
        {
            try
            {
                if (_plcDevice == null) return;
                string address = GetPlcAddress(_appConfig.PlcResultAddress);
                await _plcDevice.WriteInt16Async(address, 1);
                _uiController.LogToFrontend("手动放行信号已发送", "success");
            }
            catch (Exception ex) { HandlePlcError($"放行失败: {ex.Message}"); }
        }

        private void UpdatePlcStatus(bool connected, string text)
        {
            _ = _uiController.UpdateConnection("plc", connected);
            _uiController.LogToFrontend($"PLC: {text}", connected ? "success" : "error");
        }

        private void HandlePlcError(string message)
        {
            plcConnected = false;
            UpdatePlcStatus(false, message);
        }

        #endregion

        #region 4. 相机控制逻辑

        /// <summary>
        /// 查找并返回目标相机的索引，找不到返回-1
        /// </summary>
        private int FindTargetCamera()
        {
            try
            {
                IMVDefine.IMV_DeviceList deviceList = new IMVDefine.IMV_DeviceList();
                int res = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                if (res != IMVDefine.IMV_OK || deviceList.nDevNum == 0)
                {
                    _uiController.LogToFrontend("✗ 未找到任何相机设备", "error");
                    return -1;
                }

                for (int i = 0; i < deviceList.nDevNum; i++)
                {
                    var info = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                        deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i,
                        typeof(IMVDefine.IMV_DeviceInfo));

                    if (info.serialNumber.Equals(_appConfig.CameraSerialNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }

                // 未找到匹配的序列号
                _uiController.LogToFrontend($"✗ 未找到序列号为 {_appConfig.CameraSerialNumber} 的相机", "error");
                _uiController.LogToFrontend($"请检查相机连接或在设置中修改序列号", "warning");
                return -1;
            }
            catch (DllNotFoundException dllEx)
            {
                _uiController.LogToFrontend($"相机驱动缺失: {dllEx.Message}", "error");
                return -1;
            }
            catch (Exception ex)
            {
                _uiController.LogToFrontend($"查找相机异常: {ex.Message}", "error");
                return -1;
            }
        }

        /// <summary>
        /// 一键打开相机：自动查找目标相机并打开
        /// </summary>
        private void btnOpenCamera_Logic()
        {
            // 先查找目标相机
            _targetCameraIndex = FindTargetCamera();

            if (_targetCameraIndex == -1)
            {
                return; // 查找失败，已在日志中报警
            }

            try
            {
                int res = cam.IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode.modeByIndex, _targetCameraIndex);
                if (res != IMVDefine.IMV_OK) throw new Exception($"创建句柄失败:{res}");

                res = cam.IMV_Open();
                if (res != IMVDefine.IMV_OK) throw new Exception($"打开相机失败:{res}");

                cam.IMV_SetEnumFeatureSymbol("TriggerSource", "Software");
                cam.IMV_SetEnumFeatureSymbol("TriggerMode", "On");
                cam.IMV_SetBufferCount(8);

                getParam();

                res = cam.IMV_StartGrabbing();
                if (res != IMVDefine.IMV_OK) throw new Exception($"启动采集失败:{res}");

                if (renderThread != null && renderThread.IsAlive) renderThread.Join(100);
                renderThread = new Thread(DisplayThread);
                renderThread.IsBackground = true;
                renderThread.Start();

                _ = _uiController.UpdateConnection("cam", true);
                _uiController.LogToFrontend("✓ 相机开启成功", "success");
                // 自动连接PLC
                _ = ConnectToPlcAsync();
            }
            catch (Exception ex)
            {
                ReleaseCameraResources();
                _uiController.LogToFrontend($"相机开启异常: {ex.Message}", "error");
            }
        }

        private void getParam()
        {
            cam.IMV_SetEnumFeatureSymbol("PixelFormat", "Mono8");
            cam.IMV_SetDoubleFeatureValue("ExposureTime", _appConfig.ExposureTime);
            cam.IMV_SetDoubleFeatureValue("GainRaw", _appConfig.GainRaw);
        }

        private async void TestYolo_Handler(object? sender, EventArgs e)
        {
            try
            {
                await _uiController.LogToFrontend("开始YOLO测试...", "info");

                // 使用OpenFileDialog选择图片
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Title = "选择测试图片";
                    openFileDialog.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*";
                    openFileDialog.Multiselect = false;

                    if (openFileDialog.ShowDialog() != DialogResult.OK)
                    {
                        await _uiController.LogToFrontend("已取消测试", "warning");
                        return;
                    }

                    string selectedFile = openFileDialog.FileName;
                    await _uiController.LogToFrontend($"加载图片: {Path.GetFileName(selectedFile)}", "info");

                    // 读取图片
                    using (Bitmap originalBitmap = new Bitmap(selectedFile))
                    {
                        if (yolo == null)
                        {
                            await _uiController.LogToFrontend("YOLO模型未初始化", "error");
                            return;
                        }

                        // 执行YOLO检测
                        Stopwatch sw = Stopwatch.StartNew();

                        float conf = _appConfig.Confidence;
                        float iou = _appConfig.IouThreshold;
                        bool globalIou = _appConfig.EnableGlobalIou;
                        int highSpeed = _appConfig.EnablePreprocessing ? 1 : 0;

                        List<YoloResult> allResults = await Task.Run(() => yolo.Inference(originalBitmap, conf, iou, globalIou, highSpeed));
                        sw.Stop();

                        // 标准化需要imgW/H
                        List<YoloResult> pixelResults = StandardizeYoloResults(allResults, originalBitmap.Width, originalBitmap.Height);

                        // 应用UI绘制的ROI过滤
                        List<YoloResult> results = FilterByROI(pixelResults, originalBitmap.Width, originalBitmap.Height);

                        // 3. 判别该次是否合格
                        int targetCount = results.Count(r => yolo.Labels[(int)r.BasicData[5]].Equals(_appConfig.TargetLabel, StringComparison.OrdinalIgnoreCase));
                        bool isQualified = (targetCount == _appConfig.TargetCount);


                        string roiInfo = _currentROI != null ? $" (ROI过滤: {allResults.Count} → {results.Count})" : "";
                        string objDesc = GetDetectedObjectsDescription(results);
                        await _uiController.LogDetectionToFrontend($"检测完成! 耗时: {sw.ElapsedMilliseconds}ms, 检测到 {objDesc}{roiInfo}, 判定: {(isQualified ? "合格" : "不合格")}", isQualified ? "success" : "error");

                        // 4. 更新UI、PLC、保存结果 (复用生产逻辑)
                        UpdateUIAndPLC(isQualified, results);
                        ProcessAndSaveImages(originalBitmap, results, isQualified);
                    }
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"测试异常: {ex.Message}", "error");
            }
        }

        /// <summary>
        /// 根据ROI区域过滤检测结果,只保留中心点在ROI内的检测
        /// </summary>
        private List<YoloResult> FilterByROI(List<YoloResult> results, int imageWidth, int imageHeight)
        {
            if (_currentROI == null || _currentROI.Length != 4)
                return results; // 没有ROI则不过滤

            // 如果ROI宽高几乎为0，也就是未设置有效ROI，则不过滤
            if (_currentROI[2] < 0.001f || _currentROI[3] < 0.001f)
                return results;

            // 将归一化坐标转换为像素坐标
            float roiX = _currentROI[0] * imageWidth;
            float roiY = _currentROI[1] * imageHeight;
            float roiW = _currentROI[2] * imageWidth;
            float roiH = _currentROI[3] * imageHeight;

            // 过滤:只保留中心点在ROI内的检测结果
            var filtered = results.Where(r =>
            {
                float centerX = r.BasicData[0];
                float centerY = r.BasicData[1];

                return centerX >= roiX && centerX <= (roiX + roiW) &&
                       centerY >= roiY && centerY <= (roiY + roiH);
            }).ToList();

            return filtered;
        }

        private string GetDetectedObjectsDescription(List<YoloResult>? results)
        {
            if (results == null || results.Count == 0) return "未检测到物体";
            if (yolo == null || yolo.Labels == null) return $"{results.Count} 个物体";

            var descriptions = results
                .GroupBy(r => (int)r.BasicData[5])
                .Select(g =>
                {
                    int index = g.Key;
                    string name = (index >= 0 && index < yolo.Labels.Length) ? yolo.Labels[index] : $"未知({index})";

                    if (name.Equals("remote", StringComparison.OrdinalIgnoreCase)) name = "遥控器";
                    else if (name.Equals("screw", StringComparison.OrdinalIgnoreCase)) name = "螺钉";

                    return $"{g.Count()}个{name}";
                });

            return string.Join(", ", descriptions);
        }


        private void DisplayThread()
        {
            try
            {
                foreach (var frame in m_frameQueue.GetConsumingEnumerable(m_cts.Token))
                {
                    _ = ProcessFrame(frame);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task ProcessFrame(IMVDefine.IMV_Frame frame)
        {
            await Task.Yield();
            IMVDefine.IMV_Frame temp = frame;
            cam.IMV_ReleaseFrame(ref temp);
        }

        private void ReleaseCameraResources()
        {
            try
            {
                m_cts.Cancel();
                renderThread?.Join(200);
                if (cam != null)
                {
                    cam.IMV_StopGrabbing();
                    cam.IMV_Close();
                    cam.IMV_DestroyHandle();
                }
            }
            catch { }
        }

        private Bitmap ConvertFrameToBitmap(IMVDefine.IMV_Frame frame)
        {
            if (frame.frameInfo.pixelFormat != IMVDefine.IMV_EPixelType.gvspPixelMono8) throw new Exception("非Mono8格式");
            var bitmap = new Bitmap((int)frame.frameInfo.width, (int)frame.frameInfo.height, PixelFormat.Format8bppIndexed);
            ColorPalette palette = bitmap.Palette;
            for (int i = 0; i < 256; i++) palette.Entries[i] = Color.FromArgb(i, i, i);
            bitmap.Palette = palette;
            BitmapData bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            CopyMemory(bmpData.Scan0, frame.pData, (uint)frame.frameInfo.size);
            bitmap.UnlockBits(bmpData);
            return bitmap;
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        #endregion

        #region 5. YOLO检测逻辑

        // 对应原 btnCapture_Click_1
        private async void btnCapture_Logic()
        {
            var result = await RunDetectionOnceAsync();
            if (result != null)
            {
                UpdateUIAndPLC(result.IsQualified, result.Results);
                ProcessAndSaveImages(result.OriginalBitmap, result.Results, result.IsQualified);
                result.OriginalBitmap?.Dispose();
            }
        }

        private class DetectionResult
        {
            public bool IsQualified { get; set; }
            public List<YoloResult>? Results { get; set; }
            public Bitmap? OriginalBitmap { get; set; }
            public long ElapsedMs { get; set; }
        }

        private async Task<DetectionResult?> RunDetectionOnceAsync()
        {
            if (!cam.IMV_IsGrabbing())
            {
                _uiController.LogToFrontend("请先启动相机", "warning");
                return null;
            }

            IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
            Stopwatch sw = new Stopwatch();

            try
            {
                cam.IMV_ExecuteCommandFeature("TriggerSoftware");
                int res = cam.IMV_GetFrame(ref frame, 3000);
                if (res != IMVDefine.IMV_OK)
                {
                    _uiController.LogToFrontend("获取图像帧失败", "error");
                    return null;
                }

                Bitmap originalBitmap = ConvertFrameToBitmap(frame);
                sw.Start();

                if (yolo == null) throw new Exception("YOLO模型未初始化");

                float conf = _appConfig.Confidence;
                float iou = _appConfig.IouThreshold;
                bool globalIou = _appConfig.EnableGlobalIou;
                int highSpeed = _appConfig.EnablePreprocessing ? 1 : 0;

                List<YoloResult> allResults = await Task.Run(() => yolo.Inference(originalBitmap, conf, iou, globalIou, highSpeed));
                List<YoloResult> pixelResults = StandardizeYoloResults(allResults, originalBitmap.Width, originalBitmap.Height);
                // 使用内部参数进行ROI过滤
                List<YoloResult> finalResults = FilterResultsByROIWithThreshold(pixelResults, overlapThreshold);

                // 应用UI绘制的ROI过滤
                finalResults = FilterByROI(finalResults, originalBitmap.Width, originalBitmap.Height);

                int targetCount = finalResults.Count(r => yolo.Labels[(int)r.BasicData[5]].Equals(_appConfig.TargetLabel, StringComparison.OrdinalIgnoreCase));
                bool isQualified = (targetCount == _appConfig.TargetCount);

                sw.Stop();
                return new DetectionResult
                {
                    IsQualified = isQualified,
                    Results = finalResults,
                    OriginalBitmap = originalBitmap,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                _uiController.LogToFrontend($"检测异常: {ex.Message}", "error");
                return null;
            }
            finally
            {
                cam.IMV_ReleaseFrame(ref frame);
            }
        }

        private List<YoloResult> StandardizeYoloResults(List<YoloResult> results, int imgW, int imgH)
        {
            var outList = new List<YoloResult>();
            foreach (var r in results)
            {
                YoloResult newItem = new YoloResult();
                newItem.BasicData = (float[])r.BasicData.Clone();
                newItem.MaskData = r.MaskData?.Clone();
                if (r.KeyPoints != null)
                {
                    newItem.KeyPoints = new PosePoint[r.KeyPoints.Length];
                    for (int i = 0; i < r.KeyPoints.Length; i++)
                        newItem.KeyPoints[i] = new PosePoint { X = r.KeyPoints[i].X, Y = r.KeyPoints[i].Y, Score = r.KeyPoints[i].Score };
                }

                outList.Add(newItem);
            }
            return outList;
        }

        private async void UpdateUIAndPLC(bool isQualified, List<YoloResult>? results)
        {
            // 更新 WebUI
            detectionStats.AddRecord(isQualified);
            await _uiController.UpdateUI(detectionStats.TotalCount, detectionStats.QualifiedCount, detectionStats.UnqualifiedCount);

            // 可以在html添加 showResultOverlay(bool) 接口，这里先不传
            // 若 WebUIController 支持，可调用 _uiController.ShowResult(isQualified);

            StringBuilder sb = new StringBuilder();
            if (yolo != null && results != null)
            {
                foreach (var r in results)
                {
                    string label = yolo.Labels[(int)r.BasicData[5]];
                    float conf = r.BasicData[4];
                    sb.AppendLine($"发现物体: {label} ({conf:P0})");
                }
            }
            // 生产模式下的每次检测结果也输出到检测日志窗口
            string objDesc = GetDetectedObjectsDescription(results);
            await _uiController.LogDetectionToFrontend($"PLC触发: {(isQualified ? "OK" : "NG")} | {objDesc}", isQualified ? "success" : "error");

            WriteDetectionLog(sb, isQualified);

            // 下发 PLC
            await WriteDetectionResult(isQualified);
        }

        private void ProcessAndSaveImages(Bitmap? original, List<YoloResult>? results, bool isQualified)
        {
            if (original == null) return;

            using (Bitmap roiMarked = DrawROIBorder(original))
            {
                if (yolo == null || results == null) return;
                using (Image finalImg = yolo.GenerateImage(roiMarked, results, yolo.Labels))
                {
                    // 更新 WebUI 图片
                    SendImageToWeb(new Bitmap(finalImg));
                    // 保存
                    SaveImage(roiMarked, isQualified);
                }
            }
        }

        private void SendImageToWeb(Bitmap bmp)
        {
            if (bmp == null) return;
            using (MemoryStream ms = new MemoryStream())
            {
                //保存为 Jpeg 减少数据量
                bmp.Save(ms, ImageFormat.Jpeg);
                byte[] byteImage = ms.ToArray();
                string base64 = Convert.ToBase64String(byteImage);
                _uiController.UpdateImage(base64);
            }
        }

        // 仅在原图上画 ROI 虚线框 (供保存/显示)
        // 仅在原图上画 ROI 虚线框 (供保存/显示)
        private Bitmap DrawROIBorder(Bitmap src)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));

            Bitmap ret = new Bitmap(src);

            // 优先使用 WebUI 设置的 ROI (_currentROI)
            if (_currentROI != null && _currentROI.Length == 4)
            {
                // _currentROI is [x, y, w, h] normalized (0.0 ~ 1.0)
                int x = (int)(_currentROI[0] * src.Width);
                int y = (int)(_currentROI[1] * src.Height);
                int w = (int)(_currentROI[2] * src.Width);
                int h = (int)(_currentROI[3] * src.Height);

                // 避免无效绘制
                if (w > 0 && h > 0)
                {
                    using (Graphics g = Graphics.FromImage(ret))
                    {
                        using (Pen p = new Pen(Color.Red, 3) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 8, 4 } })
                        {
                            g.DrawRectangle(p, x, y, w, h);
                        }
                    }
                }
                return ret;
            }

            // 兼容旧版逻辑 (如果 WebUI 未设置 ROI，但后端启用了 useROI)
            if (!useROI) return ret;

            using (Graphics g = Graphics.FromImage(ret))
            {
                using (Pen p = new Pen(Color.Red, 3) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 8, 4 } })
                    g.DrawRectangle(p, roiX, roiY, roiWidth, roiHeight);
            }
            return ret;
        }

        // 纯数值计算 ROI 过滤
        private List<YoloResult> FilterResultsByROIWithThreshold(List<YoloResult> input, float threshold)
        {
            if (!useROI || !isROISet || input == null) return input;
            var outList = new List<YoloResult>();
            RectangleF roiF = new RectangleF(roiX, roiY, roiWidth, roiHeight);

            foreach (var item in input)
            {
                float w = item.BasicData[2];
                float h = item.BasicData[3];
                float left = item.BasicData[0] - w / 2f;
                float top = item.BasicData[1] - h / 2f;
                RectangleF itemRect = new RectangleF(left, top, w, h);
                RectangleF inter = RectangleF.Intersect(roiF, itemRect);
                float interArea = Math.Max(0, inter.Width) * Math.Max(0, inter.Height);
                float boxArea = w * h;
                if (boxArea > 0 && (interArea / boxArea) >= threshold) outList.Add(item);
            }
            return outList;
        }

        #endregion

        #region 6. 辅助逻辑

        private void btnSettings_Logic()
        {
            // 触发HTML密码模态框而非WinForms对话框
            _ = _uiController.ExecuteScriptAsync("openPasswordModal();");
        }

        private void ChangeModel_Logic(string modelFilename)
        {
            模型名 = modelFilename;
            InitYolo();
        }

        private void SaveImage(Bitmap bitmap, bool isQualified)
        {
            if (bitmap == null) return;
            Bitmap toSave = (Bitmap)bitmap.Clone();
            Task.Run(() =>
            {
                try
                {
                    DateTime now = DateTime.Now;
                    string saveDir = Path.Combine(Path_Images, isQualified ? "Qualified" : "Unqualified", now.ToString("yyyy年MM月dd日"), now.ToString("HH"));
                    if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
                    string file = $"{(isQualified ? "PASS" : "FAIL")}_{now:HHmmssfff}.jpg";
                    toSave.Save(Path.Combine(saveDir, file), ImageFormat.Jpeg);
                }
                catch { }
                finally { toSave.Dispose(); }
            });
        }

        public void CleanOldData(int retainDays)
        {
            try
            {
                DateTime limit = DateTime.Now.Date.AddDays(-retainDays);
                string[] types = { "Qualified", "Unqualified" };
                foreach (var t in types)
                {
                    string p = Path.Combine(Path_Images, t);
                    if (!Directory.Exists(p)) continue;
                    foreach (var d in Directory.GetDirectories(p))
                    {
                        string dirName = Path.GetFileName(d);
                        bool isLegacy = DateTime.TryParseExact(dirName, "yyyyMMdd", null, DateTimeStyles.None, out DateTime fdLegacy);
                        bool isNew = DateTime.TryParseExact(dirName, "yyyy年MM月dd日", null, DateTimeStyles.None, out DateTime fdNew);

                        DateTime? fd = isNew ? fdNew : (isLegacy ? fdLegacy : (DateTime?)null);

                        if (fd.HasValue && fd.Value < limit)
                        {
                            Directory.Delete(d, true);
                        }
                    }
                }
            }
            catch (Exception ex) { LogError(ex.Message); }
        }

        private void WriteDetectionLog(StringBuilder info, bool isQualified)
        {
            try
            {
                DateTime now = DateTime.Now;
                string dir = Path.Combine(Path_Logs, "DetectionLogs", now.ToString("yyyy年MM月dd日"));
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = $"{now:yyyyMMddHH}.txt";
                string content = $"检测时间: {now}\r\n结果: {(isQualified ? "合格" : "不合格")}\r\n{info}\r\n";
                File.AppendAllText(Path.Combine(dir, file), content, Encoding.UTF8);
            }
            catch (Exception ex) { LogError(ex.Message); }
        }

        private void WriteStartupLog(string action, string serial)
        {
            try
            {
                string msg = $"[{DateTime.Now}] {action} {(serial != null ? "SN:" + serial : "")}\n";
                File.AppendAllText(StartupLogPath, msg);
            }
            catch { }
        }

        private void LogError(string msg) { /* File logging */ }

        #endregion
    }
}