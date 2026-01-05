// ============================================================================
// 文件名: 主窗口.cs
// 描述:   清霜视觉检测系统的主窗口逻辑
//
// 模块划分:
//   #region 阻止休眠 Helper      - 阻止系统休眠的 Win32 API 调用
//   #region 拖动窗口 Helper      - 无边框窗口拖动的 P/Invoke 实现
//   #region 1. 全局变量与配置定义 - 所有成员变量、状态标志、设备引用
//   #region 2. 初始化与生命周期   - 构造函数、事件绑定、窗体加载/关闭
//   #region 3. PLC 通信逻辑      - PLC 连接、触发监控、结果写入
//   #region 4. 相机控制逻辑      - 相机枚举、打开/关闭、帧采集
//   #region 5. YOLO 检测逻辑     - 推理调用、ROI 过滤、结果可视化
//
// 依赖项:
//   - MVSDK_Net:     工业相机 SDK
//   - OpenCvSharp:   图像处理库
//   - YoloDetection: ONNX 推理封装
//   - WebView2:      前端 UI 通信
//
// 作者: ClearFrost Team
// 创建日期: 2024
// ============================================================================
using MVSDK_Net;
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
using YoloDetection;
using YOLO.Vision;

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sleep] PreventSleep error: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sleep] RestoreSleep error: {ex.Message}");
            }
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
        private double _currentCropScale = 1.0;

        // ====================== 文件存储配置 ======================
        private string BaseStoragePath
        {
            get
            {
                string? path = _appConfig?.StoragePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return @"C:\GreeVisionData";
                }

                // Check if the drive exists
                try
                {
                    string? root = Path.GetPathRoot(path);
                    if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                    {
                        // Fallback if configured drive doesn't exist
                        return @"C:\GreeVisionData";
                    }
                }
                catch (Exception ex)
                {
                    // 忽略驱动器检查异常，直接回退默认路径
                    Debug.WriteLine($"Error checking drive: {ex.Message}");
                    return @"C:\GreeVisionData";
                }

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
        private bool _isConnecting = false;
        private CancellationTokenSource? plcCts;

        // 相机
        private CameraManager _cameraManager;
        private ICamera cam; // 向后兼容，指向活动相机
        private int _targetCameraIndex = -1;
        private Thread? renderThread = null;
        private BlockingCollection<IMVDefine.IMV_Frame> m_frameQueue = new BlockingCollection<IMVDefine.IMV_Frame>(10);
        private CancellationTokenSource m_cts = new CancellationTokenSource();

        // YOLO
        YoloDetector? yolo;
        // 多模型管理器
        MultiModelManager? _modelManager;
        string 模型路径 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX");
        string 模型名 = "";
        bool 停止 = false;
        private AppConfig _appConfig = AppConfig.Load();

        // ROI归一化坐标 [x, y, w, h] (0.0~1.0)
        private float[]? _currentROI = null;

        // 传统视觉处理器
        private PipelineProcessor? _pipelineProcessor;

        // ====================== 线程安全 ======================
        /// <summary>
        /// 用于保护 _lastCapturedFrame 的线程同步锁
        /// </summary>
        private readonly object _frameLock = new object();
        private Mat? _lastCapturedFrame; // 用于预览的最后一帧

        // Helper for safe fire-and-forget
        private void SafeFireAndForget(Task task, string name, Action<Exception>? onError = null)
        {
            _ = task.ContinueWith(async t =>
            {
                if (t.IsFaulted)
                {
                    Exception ex = t.Exception?.InnerException ?? new Exception("Unknown error");
                    if (onError != null) onError(ex);
                    else if (_uiController != null) await _uiController.LogToFrontend($"{name} 异常: {ex.Message}", "error");
                }
            }, TaskScheduler.Default);
        }

        #endregion

        #region 2. 初始化与生命周期 (Initialization)

        public 主窗口()
        {
            InitializeComponent();

            // 初始化相机管理器
            _cameraManager = new CameraManager(_appConfig.IsDebugMode);
            _cameraManager.LoadFromConfig(_appConfig);

            // 向后兼容：从 CameraManager 获取活动相机
            var activeCam = _cameraManager.ActiveCamera;
            if (activeCam != null)
            {
                cam = activeCam.Camera;
            }
            else
            {
                // 如果没有配置相机，创建默认相机
                cam = _appConfig.IsDebugMode ? new MockCamera() : new RealCamera();
            }

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
            _uiController.OnConnectPlc += (s, e) => SafeFireAndForget(ConnectToPlcAsync(), "PLC手动连接");
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

            // ================== 模板管理器事件 ==================
            _uiController.OnGetFrameForTemplate += async (s, e) =>
            {
                Mat? frameClone = null;
                lock (_frameLock)
                {
                    if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                    {
                        frameClone = _lastCapturedFrame.Clone();
                    }
                }

                if (frameClone != null)
                {
                    try
                    {
                        using var clone = frameClone;
                        // 缩放以加快传输
                        int maxDim = 1200;
                        if (clone.Width > maxDim || clone.Height > maxDim)
                        {
                            double scale = Math.Min((double)maxDim / clone.Width, (double)maxDim / clone.Height);
                            Cv2.Resize(clone, clone, new OpenCvSharp.Size(0, 0), scale, scale);
                        }

                        using var bitmap = clone.ToBitmap();
                        using var ms = new MemoryStream();
                        bitmap.Save(ms, ImageFormat.Jpeg);
                        string base64 = Convert.ToBase64String(ms.ToArray());

                        await _uiController.ReceiveTemplateFrame(base64);
                    }
                    catch (Exception ex)
                    {
                        await _uiController.LogToFrontend($"获取模板帧失败: {ex.Message}", "error");
                    }
                }
                else
                {
                    await _uiController.LogToFrontend("请先打开相机并确保有画面", "warning");
                }
            };

            _uiController.OnTrainOperator += async (s, request) =>
            {
                if (_pipelineProcessor == null) return;

                try
                {
                    var opNode = _pipelineProcessor.GetOperator(request.InstanceId);
                    if (opNode == null)
                    {
                        await _uiController.LogToFrontend($"找不到算子 InstanceId={request.InstanceId}", "error");
                        return;
                    }

                    byte[] imageBytes = Convert.FromBase64String(request.ImageBase64);
                    using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
                    if (mat.Empty()) throw new Exception("解码图像为空");

                    if (opNode.Operator is ITemplateTrainable trainable)
                    {
                        // 统一保存模板图像到本地作为备份
                        string templateDir = Path.Combine(BaseStoragePath, "Templates");
                        if (!Directory.Exists(templateDir)) Directory.CreateDirectory(templateDir);
                        string templatePath = Path.Combine(templateDir, $"template_{request.InstanceId}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        Cv2.ImWrite(templatePath, mat);

                        // 训练/设置模板
                        // 1. 先更新 templatePath (避免 FeatureMatchOp 等算子因为设置路径而清空内存中的模板)
                        if (opNode.Operator is IImageOperator op && op.Parameters.ContainsKey("templatePath"))
                        {
                            op.SetParameter("templatePath", templatePath);
                        }

                        // 2. 训练/设置模板 (确保这是最后一步，保证 _templateImage 会被正确赋值且 IsTrained 为 true)
                        trainable.SetTemplateFromMat(mat);

                        await _uiController.LogToFrontend($"✓ 算子 [{opNode.Operator.Name}] 模板已更新并训练");
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"⚠ 算子 [{opNode.Operator.Name}] 不支持模板训练", "warning");
                    }

                    // 刷新UI参数（通知前端更新 isTrained 状态）
                    // 更新配置并刷新UI
                    var config = _pipelineProcessor.ExportConfig();
                    _appConfig.VisionPipelineJson = JsonSerializer.Serialize(config);
                    _appConfig.Save();

                    await _uiController.SendPipelineUpdated(config);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"训练失败: {ex.Message}", "error");
                }
            };

            // ================== 传统视觉事件订阅 ==================
            _uiController.OnVisionModeChanged += async (s, mode) =>
            {
                _appConfig.VisionMode = mode;
                _appConfig.Save();
                await _uiController.LogToFrontend($"视觉模式切换为: {(mode == 0 ? "YOLO" : "传统视觉")}");

                // 初始化传统视觉流程处理器
                if (mode == 1 && _pipelineProcessor == null)
                {
                    _pipelineProcessor = new PipelineProcessor();
                    // 尝试从配置加载
                    if (!string.IsNullOrEmpty(_appConfig.VisionPipelineJson) && _appConfig.VisionPipelineJson != "[]")
                    {
                        try
                        {
                            var config = JsonSerializer.Deserialize<VisionConfig>(_appConfig.VisionPipelineJson);
                            if (config != null)
                            {
                                try
                                {
                                    _pipelineProcessor.ImportConfig(config);
                                }
                                catch (Exception ex)
                                {
                                    await _uiController.LogToFrontend($"流程加载失败: {ex.Message}", "error");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[主窗口] Pipeline init error: {ex.Message}");
                        }
                    }
                }
            };

            _uiController.OnGetVisionConfig += async (s, e) =>
            {
                if (_pipelineProcessor == null) _pipelineProcessor = new PipelineProcessor();

                var config = _pipelineProcessor.ExportConfig();
                config.TemplatePath = _appConfig.TemplateImagePath;
                config.TemplateThreshold = _appConfig.TemplateThreshold;

                var response = new VisionConfigResponse
                {
                    Config = config,
                    AvailableOperators = OperatorFactory.GetAvailableOperators()
                };
                await _uiController.SendVisionConfig(response);
            };

            _uiController.OnPipelineUpdate += async (s, request) =>
            {
                if (_pipelineProcessor == null) _pipelineProcessor = new PipelineProcessor();

                try
                {
                    switch (request.Action?.ToLower())
                    {
                        case "add":
                            await _uiController.LogToFrontend($"[DEBUG] 准备添加算子, TypeId={request.TypeId}");
                            await _uiController.LogToFrontend($"[DEBUG] 添加前算子数: {_pipelineProcessor.Operators.Count}");
                            var newOp = OperatorFactory.Create(request.TypeId ?? "");
                            if (newOp != null)
                            {
                                // 如果是匹配算子，自动设置当前的模板路径
                                if (!string.IsNullOrEmpty(_appConfig.TemplateImagePath))
                                {
                                    if (newOp is TemplateMatchOp tmOp)
                                    {
                                        tmOp.SetParameter("templatePath", _appConfig.TemplateImagePath);
                                    }
                                    else if (newOp is FeatureMatchOp fmOp)
                                    {
                                        fmOp.SetParameter("templatePath", _appConfig.TemplateImagePath);
                                    }
                                }
                                var instanceId = _pipelineProcessor.AddOperator(newOp);
                                await _uiController.LogToFrontend($"[DEBUG] 添加后算子数: {_pipelineProcessor.Operators.Count}, InstanceId={instanceId}");
                                await _uiController.LogToFrontend($"✓ 已添加算子: {newOp.Name}");
                            }
                            else
                            {
                                await _uiController.LogToFrontend($"[DEBUG] OperatorFactory.Create 返回 null, TypeId={request.TypeId}", "error");
                            }
                            break;
                        case "remove":
                            if (_pipelineProcessor.RemoveOperator(request.InstanceId ?? ""))
                            {
                                await _uiController.LogToFrontend($"✓ 已移除算子");
                            }
                            break;
                        case "update":
                            if (!string.IsNullOrEmpty(request.InstanceId) && !string.IsNullOrEmpty(request.ParamName))
                            {
                                // 处理 JsonElement 类型的参数值
                                object actualValue = request.ParamValue ?? 0;
                                if (actualValue is JsonElement jsonElement)
                                {
                                    actualValue = jsonElement.ValueKind switch
                                    {
                                        JsonValueKind.Number => jsonElement.TryGetDouble(out var d) ? d : 0,
                                        JsonValueKind.String => jsonElement.GetString() ?? "",
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        _ => 0
                                    };
                                }
                                _pipelineProcessor.UpdateOperatorParameter(request.InstanceId, request.ParamName, actualValue);
                            }
                            break;
                    }

                    // 保存配置
                    var config = _pipelineProcessor.ExportConfig();
                    _appConfig.VisionPipelineJson = JsonSerializer.Serialize(config);
                    _appConfig.Save();

                    // 调试日志
                    await _uiController.LogToFrontend($"[DEBUG] 处理器中有 {_pipelineProcessor.Operators.Count} 个算子");
                    await _uiController.LogToFrontend($"[DEBUG] 导出配置有 {config.Operators.Count} 个算子");

                    // 发送更新后的配置
                    await _uiController.SendPipelineUpdated(config);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"流程更新失败: {ex.Message}", "error");
                }
            };

            _uiController.OnGetPreview += async (s, e) =>
            {
                Mat? frameClone = null;
                lock (_frameLock)
                {
                    if (_pipelineProcessor != null && _lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                    {
                        frameClone = _lastCapturedFrame.Clone();
                    }
                }

                if (_pipelineProcessor == null || frameClone == null)
                {
                    await _uiController.LogToFrontend("无可用图像进行预览", "warning");
                    return;
                }

                try
                {
                    using var inputFrame = frameClone;
                    var sw = Stopwatch.StartNew();
                    using var preview = await _pipelineProcessor.GetPreviewAsync(inputFrame);
                    sw.Stop();

                    // 转换为 Base64
                    using var bitmap = preview.ToBitmap();
                    using var ms = new MemoryStream();
                    bitmap.Save(ms, ImageFormat.Jpeg);
                    string base64 = Convert.ToBase64String(ms.ToArray());

                    var response = new PreviewResponse
                    {
                        ImageBase64 = base64,
                        ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                    };
                    await _uiController.SendPreviewImage(response);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"预览失败: {ex.Message}", "error");
                }
            };

            _uiController.OnUploadTemplate += async (s, action) =>
            {
                if (action == "select")
                {
                    // 文件选择对话框
                    InvokeOnUIThread(async () =>
                    {
                        using var ofd = new OpenFileDialog();
                        ofd.Filter = "图像文件|*.jpg;*.jpeg;*.png;*.bmp";
                        ofd.Title = "选择模板图像";

                        if (ofd.ShowDialog() == DialogResult.OK)
                        {
                            _appConfig.TemplateImagePath = ofd.FileName;
                            _appConfig.Save();

                            // 更新所有 TemplateMatchOp 和 FeatureMatchOp 算子
                            if (_pipelineProcessor != null)
                            {
                                foreach (var op in _pipelineProcessor.Operators)
                                {
                                    if (op.Operator is TemplateMatchOp tmOp)
                                    {
                                        tmOp.SetParameter("templatePath", ofd.FileName);
                                    }
                                    else if (op.Operator is FeatureMatchOp fmOp)
                                    {
                                        fmOp.SetParameter("templatePath", ofd.FileName);
                                    }
                                    else if (op.Operator is OrbMatchOp omOp)
                                    {
                                        omOp.SetParameter("templatePath", ofd.FileName);
                                    }
                                }
                                var config = _pipelineProcessor.ExportConfig();
                                _appConfig.VisionPipelineJson = JsonSerializer.Serialize(config);
                                _appConfig.Save();
                            }

                            await _uiController.LogToFrontend($"✓ 模板已加载: {Path.GetFileName(ofd.FileName)}");

                            // 发送模板预览到前端
                            try
                            {
                                using var templateMat = Cv2.ImRead(ofd.FileName, ImreadModes.Color);
                                if (!templateMat.Empty())
                                {
                                    // 缩放到合适大小
                                    using var resized = new Mat();
                                    double scale = Math.Min(128.0 / templateMat.Width, 128.0 / templateMat.Height);
                                    Cv2.Resize(templateMat, resized, new OpenCvSharp.Size(0, 0), scale, scale);

                                    using var bitmap = resized.ToBitmap();
                                    using var ms = new MemoryStream();
                                    bitmap.Save(ms, ImageFormat.Jpeg);
                                    string base64 = Convert.ToBase64String(ms.ToArray());
                                    await _uiController.ExecuteScriptAsync($"updateTemplatePreview('{base64}')");
                                }
                            }
                            catch { }
                        }
                    });
                }
                else if (action == "capture")
                {
                    // 从当前帧截取 -> 打开前端裁剪弹窗
                    Mat? frameClone = null;
                    lock (_frameLock)
                    {
                        if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                        {
                            frameClone = _lastCapturedFrame.Clone();
                        }
                    }

                    if (frameClone != null)
                    {
                        try
                        {
                            using var clone = frameClone;
                            // 缩小及计算比例
                            int targetWidth = 1200;
                            double scale = 1.0;
                            Mat displayMat = clone;
                            Mat resizedMat = null;
                            if (clone.Width > targetWidth)
                            {
                                scale = (double)targetWidth / clone.Width;
                                resizedMat = new Mat();
                                Cv2.Resize(clone, resizedMat, new OpenCvSharp.Size(0, 0), scale, scale);
                                _currentCropScale = scale;
                                displayMat = resizedMat;
                            }
                            else
                            {
                                _currentCropScale = 1.0;
                            }

                            using var bitmap = displayMat.ToBitmap();
                            using var ms = new MemoryStream();
                            bitmap.Save(ms, ImageFormat.Jpeg);
                            string base64 = Convert.ToBase64String(ms.ToArray());

                            if (resizedMat != null) resizedMat.Dispose();

                            // 调用前端 openCropper
                            await _uiController.ExecuteScriptAsync($"openCropper('{base64}')");
                            await _uiController.LogToFrontend("请在弹窗中裁剪模板区域", "info");
                        }
                        catch (Exception ex)
                        {
                            await _uiController.LogToFrontend($"打开裁剪失败: {ex.Message}", "error");
                        }
                    }
                    else
                    {
                        await _uiController.LogToFrontend("请先打开相机并确保有画面", "warning");
                    }
                }
            };

            // 处理裁剪后的模板保存
            _uiController.OnSaveCroppedTemplate += async (s, json) =>
            {
                try
                {
                    // 解析 JSON: {x, y, width, height, rotate, scaleX, scaleY}
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var r = doc.RootElement;
                    double x = r.GetProperty("x").GetDouble();
                    double y = r.GetProperty("y").GetDouble();
                    double w = r.GetProperty("width").GetDouble();
                    double h = r.GetProperty("height").GetDouble();
                    double rotate = 0;
                    if (r.TryGetProperty("rotate", out var rotProp)) rotate = rotProp.GetDouble();

                    if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                    {
                        using var clone = _lastCapturedFrame.Clone();
                        Mat sourceToCrop = clone;

                        // 1. 处理旋转 (仅支持 90度 整数倍)
                        if (Math.Abs(rotate) > 0.1)
                        {
                            int rot = (int)rotate;
                            RotateFlags? flag = null;
                            if (rot == 90 || rot == -270) flag = RotateFlags.Rotate90Clockwise;
                            else if (rot == -90 || rot == 270) flag = RotateFlags.Rotate90Counterclockwise;
                            else if (rot == 180 || rot == -180) flag = RotateFlags.Rotate180;

                            if (flag.HasValue)
                            {
                                var rotated = new Mat();
                                Cv2.Rotate(sourceToCrop, rotated, flag.Value);
                                // Move ownership
                                if (sourceToCrop != clone) sourceToCrop.Dispose();
                                sourceToCrop = rotated;
                            }
                        }

                        // 2. 映射坐标
                        if (_currentCropScale <= 0) _currentCropScale = 1.0;
                        double realX = x / _currentCropScale;
                        double realY = y / _currentCropScale;
                        double realW = w / _currentCropScale;
                        double realH = h / _currentCropScale;

                        // 3. 安全裁剪
                        int ix = Math.Max(0, (int)realX);
                        int iy = Math.Max(0, (int)realY);
                        int iw = (int)realW;
                        int ih = (int)realH;

                        // Boundary checks
                        if (ix + iw > sourceToCrop.Width) iw = sourceToCrop.Width - ix;
                        if (iy + ih > sourceToCrop.Height) ih = sourceToCrop.Height - iy;

                        if (iw > 0 && ih > 0)
                        {
                            var roi = new Rect(ix, iy, iw, ih);
                            using var cropMat = new Mat(sourceToCrop, roi);

                            if (!cropMat.Empty())
                            {
                                string templateDir = Path.Combine(BaseStoragePath, "Templates");
                                if (!Directory.Exists(templateDir)) Directory.CreateDirectory(templateDir);
                                string templatePath = Path.Combine(templateDir, $"template_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                                Cv2.ImWrite(templatePath, cropMat);

                                _appConfig.TemplateImagePath = templatePath;
                                _appConfig.Save();

                                // Update Ops
                                if (_pipelineProcessor != null)
                                {
                                    foreach (var op in _pipelineProcessor.Operators)
                                    {
                                        if (op.Operator is TemplateMatchOp tmOp)
                                        {
                                            tmOp.SetTemplateFromMat(cropMat);
                                            tmOp.SetParameter("templatePath", templatePath);
                                        }
                                        else if (op.Operator is FeatureMatchOp fmOp)
                                        {
                                            fmOp.SetParameter("templatePath", templatePath);
                                        }
                                    }
                                }

                                await _uiController.LogToFrontend("✓ 高分辨率模板已应用");

                                using var preview = new Mat();
                                double pScale = 128.0 / Math.Max(iw, ih);
                                Cv2.Resize(cropMat, preview, new OpenCvSharp.Size(0, 0), pScale, pScale);
                                using var bmp = preview.ToBitmap();
                                using var msp = new MemoryStream();
                                bmp.Save(msp, ImageFormat.Jpeg);
                                string b64 = Convert.ToBase64String(msp.ToArray());
                                await _uiController.ExecuteScriptAsync($"updateTemplatePreview('{b64}')");
                            }
                        }

                        if (sourceToCrop != clone) sourceToCrop.Dispose();
                    }

                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"保存模板失败: {ex.Message}", "error");
                }
            };

            // ================== 多相机事件 ==================
            _uiController.OnGetCameraList += async (s, e) =>
            {
                var cameras = _cameraManager.Cameras.Select(c => new
                {
                    id = c.Id,
                    displayName = c.Config.DisplayName,
                    serialNumber = c.Config.SerialNumber,
                    exposureTime = c.Config.ExposureTime,
                    gain = c.Config.Gain
                }).ToList();

                await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId);
            };

            _uiController.OnSwitchCamera += async (s, cameraId) =>
            {
                try
                {
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
                        _cameraManager.SaveToConfig(_appConfig);
                        _appConfig.Save();

                        await _uiController.LogToFrontend($"✓ 已切换到相机: {newCam.Config.DisplayName}");
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"切换相机失败: 未找到 {cameraId}", "error");
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

                    string displayName = r.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                    string serialNumber = r.TryGetProperty("serialNumber", out var sn) ? sn.GetString() ?? "" : "";
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
                        existing.ExposureTime = exposure;
                        existing.Gain = gain;
                        await _uiController.LogToFrontend($"✓ 已更新相机配置: {displayName}");
                    }
                    else
                    {
                        var newConfig = new CameraConfig
                        {
                            Id = $"cam_{DateTime.Now:yyyyMMddHHmmss}",
                            SerialNumber = serialNumber,
                            DisplayName = displayName,
                            ExposureTime = exposure,
                            Gain = gain,
                            IsEnabled = true
                        };
                        _appConfig.Cameras.Add(newConfig);
                        _cameraManager.AddCamera(newConfig);
                        await _uiController.LogToFrontend($"✓ 已添加新相机: {displayName}");
                    }

                    _appConfig.Save();

                    // 刷新前端列表
                    var cameras = _cameraManager.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.Config.DisplayName,
                        serialNumber = c.Config.SerialNumber,
                        exposureTime = c.Config.ExposureTime,
                        gain = c.Config.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId);
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

                    await _uiController.LogToFrontend($"✓ 已删除相机: {camToRemove.DisplayName}");

                    // 刷新前端列表
                    var cameras = _cameraManager.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.Config.DisplayName,
                        serialNumber = c.Config.SerialNumber,
                        exposureTime = c.Config.ExposureTime,
                        gain = c.Config.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"删除相机失败: {ex.Message}", "error");
                }
            };

            // 手动测试模板匹配
            _uiController.OnTestTemplateMatch += async (s, e) =>
            {
                if (_pipelineProcessor == null)
                {
                    await _uiController.LogToFrontend("请先构建处理流程", "warning");
                    return;
                }

                string? fileName = await ShowOpenFileDialogOnStaThread("选择测试图片", "图像文件|*.jpg;*.jpeg;*.png;*.bmp");

                if (!string.IsNullOrEmpty(fileName))
                {

                    var ofd = new { FileName = fileName }; // Mocking ofd object for minimal code change or just use fileName directly

                    try
                    {
                        await _uiController.LogToFrontend($"正在测试: {Path.GetFileName(ofd.FileName)}");

                        using var testImage = Cv2.ImRead(ofd.FileName, ImreadModes.Color);
                        if (testImage.Empty())
                        {
                            await _uiController.LogToFrontend("无法读取图像文件", "error");
                            return;
                        }

                        var sw = Stopwatch.StartNew();
                        var result = await _pipelineProcessor.ProcessAsync(testImage);
                        sw.Stop();

                        // 获取最后一个算子的输出（带锚框）
                        Mat? lastOutput = _pipelineProcessor.GetLastOutput();
                        if (lastOutput == null || lastOutput.Empty())
                        {
                            await _uiController.LogToFrontend("处理后无输出图像", "warning");
                            return;
                        }

                        // 确保是彩色图像
                        Mat outputForDisplay;
                        if (lastOutput.Channels() == 1)
                        {
                            outputForDisplay = new Mat();
                            Cv2.CvtColor(lastOutput, outputForDisplay, ColorConversionCodes.GRAY2BGR);
                        }
                        else
                        {
                            outputForDisplay = lastOutput.Clone();
                        }

                        // 转换为 Base64 并发送
                        using var bitmap = outputForDisplay.ToBitmap();
                        using var ms = new MemoryStream();
                        bitmap.Save(ms, ImageFormat.Jpeg);
                        string base64 = Convert.ToBase64String(ms.ToArray());
                        outputForDisplay.Dispose();

                        var response = new PreviewResponse
                        {
                            ImageBase64 = base64,
                            ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                        };
                        await _uiController.SendPreviewImage(response);

                        // 显示结果
                        string resultMsg;
                        // 如果 Pipeline 返回了详细消息（不是默认的"检测通过/未通过"），则直接显示
                        if (!string.IsNullOrEmpty(result.Message) && result.Message != "检测通过" && result.Message != "检测未通过")
                        {
                            resultMsg = result.IsPass
                                ? $"✓ {result.Message}"
                                : $"✗ 匹配失败: {result.Message}";
                        }
                        else
                        {
                            // 默认显示 (兼容旧逻辑)
                            resultMsg = result.IsPass
                                ? $"✓ 匹配成功! 得分: {result.Score:F3}"
                                : $"✗ 匹配失败 (得分: {result.Score:F3} < 阈值)";
                        }

                        await _uiController.LogToFrontend(resultMsg, result.IsPass ? "success" : "error");
                        await _uiController.LogToFrontend($"处理耗时: {sw.Elapsed.TotalMilliseconds:F1}ms");

                        // 更新统计数据
                        detectionStats.AddRecord(result.IsPass);
                        await _uiController.UpdateUI(detectionStats.TotalCount, detectionStats.QualifiedCount, detectionStats.UnqualifiedCount);
                    }
                    catch (Exception ex)
                    {
                        await _uiController.LogToFrontend($"测试失败: {ex.Message}", "error");
                    }
                }

            };

            // 注册窗体关闭事件
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
            PreventSleep();

            // 确保无边框全屏
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;

            // 订阅 WebUI 就绪事件
            _uiController.OnAppReady += async (s, ev) =>
            {
                try
                {
                    await _uiController.LogToFrontend("✓ WebUI已就绪");
                    await _uiController.LogToFrontend("系统初始化完成");
                    await _uiController.UpdateCameraName(_appConfig.CameraName);

                    // 初始化前端设置 (Sidebar Controls)
                    await _uiController.InitSettings(_appConfig);

                    // 发送已加载的统计数据到前端（修复重启后饼状图不更新的问题）
                    await _uiController.UpdateUI(detectionStats.TotalCount, detectionStats.QualifiedCount, detectionStats.UnqualifiedCount);
                    if (detectionStats.TotalCount > 0)
                    {
                        await _uiController.LogToFrontend($"已加载今日统计: 总计{detectionStats.TotalCount}, 合格{detectionStats.QualifiedCount}, 不合格{detectionStats.UnqualifiedCount}");
                    }

                    await InitModelList();
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"WebUI初始化流程异常: {ex.Message}", "error");
                }
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

            // 订阅任务类型修改事件
            _uiController.OnSetTaskType += (sender, taskType) =>
            {
                _appConfig.TaskType = taskType;
                _appConfig.Save();
                // 如果当前有YOLO检测器实例，更新其TaskMode
                if (yolo != null)
                {
                    yolo.TaskMode = (YoloDetection.YoloTaskType)taskType;
                }
                // 同时更新多模型管理器
                _modelManager?.SetTaskMode((YoloDetection.YoloTaskType)taskType);
            };

            // ================== 多模型切换事件 ==================
            _uiController.OnSetAuxiliary1Model += async (sender, modelName) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(modelName))
                    {
                        _modelManager?.UnloadAuxiliary1Model();
                        _appConfig.Auxiliary1ModelPath = "";
                        await _uiController.LogToFrontend("辅助模型1已卸载");
                    }
                    else
                    {
                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            _modelManager?.LoadAuxiliary1Model(modelPath);
                            _appConfig.Auxiliary1ModelPath = modelName;
                            await _uiController.LogToFrontend($"✓ 辅助模型1已加载: {modelName}");
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
                        _modelManager?.UnloadAuxiliary2Model();
                        _appConfig.Auxiliary2ModelPath = "";
                        await _uiController.LogToFrontend("辅助模型2已卸载");
                    }
                    else
                    {
                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            _modelManager?.LoadAuxiliary2Model(modelPath);
                            _appConfig.Auxiliary2ModelPath = modelName;
                            await _uiController.LogToFrontend($"✓ 辅助模型2已加载: {modelName}");
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
                if (_modelManager != null)
                {
                    _modelManager.EnableFallback = enabled;
                }
                _appConfig.Save();
                await _uiController.LogToFrontend(enabled ? "✓ 多模型自动切换已启用" : "多模型自动切换已禁用");
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
                        if (root.TryGetProperty("MaxRetryCount", out var mrc)) _appConfig.MaxRetryCount = mrc.TryGetInt32(out int mrcVal) ? mrcVal : _appConfig.MaxRetryCount;
                        if (root.TryGetProperty("RetryIntervalMs", out var rim)) _appConfig.RetryIntervalMs = rim.TryGetInt32(out int rimVal) ? rimVal : _appConfig.RetryIntervalMs;
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

                        // 尝试重新连接PLC (应用新IP/端口)
                        if (_isConnecting)
                        {
                            // 如果正在连接中，可能需要等待或让用户手动重试
                            await _uiController.LogToFrontend("PLC正在连接中，新配置将在下次连接时生效", "warning");
                        }
                        else
                        {
                            _ = ConnectToPlcAsync();
                        }

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
            // 检测跨日，如果需要则保存历史并重置今日数据
            // Note: CheckAndResetForNewDay appears to be async based on lint, but if it returns Task<bool>, we need await.
            // Assuming we can await it here if we are in InitializeAsync.
            // But wait, if this code block is in 主窗口 constructor or Load, we need to be careful.
            // Let's wrap execution if it's really async, but for now let's just leave it if inconsistent.
            // Actually, best to SafeFire it if it's a Task<bool> but strictly used for side effect?
            // "if (task)" is invalid in C#. Lint says "Consider await", implies it returns Task.
            // If it returns Task<bool>, we must await it to get bool.
            bool isNewDay = detectionStats.CheckAndResetForNewDay(statisticsHistory);
            if (isNewDay)
            {
                SafeFireAndForget(_uiController.LogToFrontend("检测到新的一天，统计数据已重置", "info"), "日志记录");
            }

            // 初始化YOLO
            InitYolo();
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
            SafeFireAndForget(InitYoloAsync(), "YOLO初始化");
        }

        private async Task InitYoloAsync()
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
                try
                {
                    // 在后台线程加载模型,避免阻塞UI
                    await Task.Run(() =>
                    {
                        try
                        {
                            yolo = new YoloDetector(Path.Combine(模型路径, 模型名), ver, gpuIdx, useGpu);
                            yolo.TaskMode = (YoloDetection.YoloTaskType)_appConfig.TaskType;
                        }
                        catch (Exception ex) when (useGpu)
                        {
                            // GPU加载失败，尝试CPU回退
                            // 记录详细错误以便调试
                            Debug.WriteLine($"GPU init failed: {ex}");

                            // 抛出特定的异常类型以便外部捕获，或者直接在这里处理回退
                            throw new InvalidOperationException($"GPU加速初始化失败，将自动回退到CPU模式。错误: {ex.Message}");
                        }
                    });

                    await _uiController.LogToFrontend($"✓ YOLO模型已加载: {模型名} {(useGpu ? "[GPU]" : "[CPU]")}", "success");
                }
                catch (InvalidOperationException ex)
                {
                    // 捕获回退信号，尝试使用CPU重新加载
                    await _uiController.LogToFrontend(ex.Message, "warning");
                    try
                    {
                        await Task.Run(() =>
                        {
                            yolo = new YoloDetector(Path.Combine(模型路径, 模型名), ver, 0, false); // 强制CPU
                            yolo.TaskMode = (YoloDetection.YoloTaskType)_appConfig.TaskType;
                        });
                        await _uiController.LogToFrontend($"✓ 已回退到 CPU 模式加载模型: {模型名}", "success");

                        // 可选：更新配置以禁用GPU，避免下次启动重复报错
                        // _appConfig.EnableGpu = false;
                        // _appConfig.Save();
                    }
                    catch (Exception exCpu)
                    {
                        await _uiController.LogToFrontend($"CPU模式也加载失败: {exCpu.Message}", "error");
                        yolo = null;
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"模型加载失败: {ex.Message}", "error");
                    // 尝试输出更详细的内部异常
                    if (ex.InnerException != null)
                    {
                        await _uiController.LogToFrontend($"内部错误: {ex.InnerException.Message}", "error");
                    }
                    yolo = null;
                }
            }
            else
            {
                await _uiController.LogToFrontend("未找到模型文件，请在设置中下载或上传模型", "warning");
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
            // 防止重复调用
            if (e.CloseReason == CloseReason.ApplicationExitCall) return;

            try
            {
                WriteStartupLog("软件关闭", null);

                // 恢复系统休眠策略
                RestoreSleep();

                // 保存统计数据
                try { detectionStats?.Save(); }
                catch (Exception ex)
                {
                    // 忽略统计保存错误
                    Debug.WriteLine($"Stats Save Error: {ex.Message}");
                }
                try { statisticsHistory?.Save(); }
                catch (Exception ex)
                {
                    // 忽略历史保存错误
                    Debug.WriteLine($"History Save Error: {ex.Message}");
                }

                // 停止后台任务
                this.停止 = true;
                plcCts?.Cancel();

                // 使用线程等待模式进行资源释放，防止界面卡死
                // 给予500ms的尝试断开时间，超时强制退出
                var cleanupTask = Task.Run(() =>
                {
                    try
                    {
                        if (plcConnected)
                        {
                            _plcDevice?.Disconnect();
                            plcConnected = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"PLC Disconnect Error: {ex.Message}");
                    }

                    try
                    {
                        ReleaseCameraResources();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Camera Release Error: {ex.Message}");
                    }

                    try
                    {
                        if (yolo is IDisposable disposableYolo) disposableYolo.Dispose();
                        yolo = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"YOLO Dispose Error: {ex.Message}");
                    }

                    try
                    {
                        _pipelineProcessor?.Dispose();
                        _pipelineProcessor = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Pipeline Dispose Error: {ex.Message}");
                    }
                });

                // 等待清理完成或超时 (800ms)
                if (!cleanupTask.Wait(800))
                {
                    // 超时，强制不再等待
                }
            }
            catch (Exception)
            {
                // 确保任何错误都不阻止关闭
            }
        }

        #endregion

        #region 3. PLC控制逻辑 (PLC Control)

        private const int PlcTriggerDelay = 800;
        private const int PlcPollingInterval = 500;

        // 使用工厂模式创建PLC设备，支持多协议切换
        private async Task ConnectToPlcAsync()
        {
            if (_isConnecting) return;
            _isConnecting = true;

            const int maxRetries = 3;
            const int retryDelay = 2000;
            string ip = _appConfig.PlcIp;
            int port = _appConfig.PlcPort;
            var protocol = PlcFactory.ParseProtocol(_appConfig.PlcProtocol);

            try
            {
                // [修复] 先停止旧的监控循环，避免残留线程干扰新连接
                if (plcCts != null && !plcCts.IsCancellationRequested)
                {
                    plcCts.Cancel();
                    await Task.Delay(100); // 给监控循环一点时间退出
                }
                plcCts?.Dispose();
                plcCts = null;

                // [修复] 断开旧连接并重置状态
                try { _plcDevice?.Disconnect(); } catch { }
                _plcDevice = null;
                plcConnected = false;

                UpdatePlcStatus(false, "连接中...");
                await _uiController.LogToFrontend($"正在使用 {protocol} 协议连接 {ip}:{port}", "info");

                for (int i = 0; i < maxRetries; i++)
                {
                    // 使用工厂创建对应协议的适配器
                    _plcDevice = PlcFactory.Create(protocol, ip, port);

                    bool connected = await _plcDevice.ConnectAsync();

                    if (connected)
                    {
                        UpdatePlcStatus(true, $"已连接 ({_plcDevice.ProtocolName})");
                        plcConnected = true;

                        plcCts = new CancellationTokenSource();
                        SafeFireAndForget(Task.Run(() => PlcMonitoringLoop(plcCts.Token)), "PLC监控循环");
                        return;
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"连接失败: {_plcDevice?.LastError}", "error");
                    }

                    if (i < maxRetries - 1)
                    {
                        // UpdatePlcStatus(false, $"重试({i + 2}/{maxRetries})");
                        SafeFireAndForget(_uiController.UpdateConnection("plc", false), "更新PLC状态(重试)");
                        _uiController.LogToFrontend($"重试({i + 2}/{maxRetries})", "error");
                        await Task.Delay(retryDelay);
                    }
                }
                UpdatePlcStatus(false, "连接失败");
            }
            catch (Exception ex)
            {
                HandlePlcError($"连接异常: {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
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

                        // 动态重拍逻辑
                        int maxRetries = _appConfig.MaxRetryCount; // e.g. 1
                        int retryInterval = _appConfig.RetryIntervalMs; // e.g. 2000
                        DetectionResult? lastResult = null;

                        // 尝试 1 + maxRetries 次
                        for (int attempt = 0; attempt <= maxRetries; attempt++)
                        {
                            // 如果是重试
                            if (attempt > 0)
                            {
                                await _uiController.LogToFrontend($"触发重拍 ({attempt}/{maxRetries})", "warning");
                                await Task.Delay(retryInterval, token);
                            }

                            lastResult = await InvokeAsync(RunDetectionOnceAsync);

                            if (lastResult != null && lastResult.IsQualified)
                            {
                                break; // 只要一次合格，直接跳出，视为合格
                            }
                            else if (lastResult != null && !lastResult.IsQualified)
                            {
                                // 失败，如果是最后一次尝试，则最终判定为失败
                                // 否则仅显示中间结果(不保存)
                                if (attempt < maxRetries)
                                {
                                    InvokeOnUIThread(() =>
                                    {
                                        DisplayImageOnly(lastResult.OriginalBitmap, lastResult.Results, lastResult.UsedModelLabels);
                                        lastResult.OriginalBitmap?.Dispose();
                                    });
                                }
                            }
                        }

                        // 处理最终结果 (lastResult 可能为null如果每次都报错)
                        if (lastResult != null)
                        {
                            ProcessFinalResult(lastResult);
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

        private Task<T?> InvokeAsync<T>(Func<Task<T?>> task)
        {
            var tcs = new TaskCompletionSource<T?>();
            try
            {
                // 使用 BeginInvoke 避免阻塞调用线程，并正确处理异步委托的等待
                this.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        var result = await task();
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            return tcs.Task;
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
                SafeFireAndForget(UpdateUIAndPLC(result.IsQualified, result.Results, result.UsedModelLabels), "UI更新");
                ProcessAndSaveImages(result.OriginalBitmap, result.Results, result.IsQualified, result.UsedModelLabels);

                if (yolo?.LastMetrics != null)
                {
                    SafeFireAndForget(_uiController.SendInferenceMetrics(yolo.LastMetrics), "发送推理指标");
                }

                result.OriginalBitmap?.Dispose();
            });
        }

        private void DisplayImageOnly(Bitmap? original, List<YoloResult>? results, string[]? labels = null)
        {
            if (yolo == null || original == null || results == null) return;

            using (Bitmap roiMarked = DrawROIBorder(original))
            {
                // 使用传入的标签，或者回退到默认
                string[] actualLabels = labels ?? _modelManager?.PrimaryLabels ?? yolo.Labels;

                using (Image finalImg = yolo.GenerateImage(roiMarked, results, actualLabels))
                using (Bitmap webImg = new Bitmap(finalImg))
                {
                    // 推送到 WebUI
                    SendImageToWeb(webImg);
                }
            }
        }

        public async Task WriteDetectionResult(bool isQualified)
        {
            if (!plcConnected || _plcDevice == null) return;
            string address = GetPlcAddress(_appConfig.PlcResultAddress);
            try
            {
                bool success = await _plcDevice.WriteInt16Async(address, (short)(isQualified ? 1 : 0));
                await _uiController.LogToFrontend(success ? $"PLC写入结果{(isQualified ? 1 : 0)}成功" : $"PLC写入失败: {_plcDevice.LastError}", "info");
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
                await _uiController.LogToFrontend("手动放行信号已发送", "success");
            }
            catch (Exception ex) { HandlePlcError($"放行失败: {ex.Message}"); }
        }

        private void UpdatePlcStatus(bool connected, string text)
        {
            SafeFireAndForget(_uiController.UpdateConnection("plc", connected), "更新PLC状态");
            SafeFireAndForget(_uiController.LogToFrontend($"PLC: {text}", connected ? "success" : "error"), "PLC日志");
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
                int res = cam.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                if (res != IMVDefine.IMV_OK || deviceList.nDevNum == 0)
                {
                    _uiController.LogToFrontend("✗ 未找到任何相机设备", "error");
                    return -1;
                }

                for (int i = 0; i < deviceList.nDevNum; i++)
                {
                    var infoPtr = deviceList.pDevInfo + Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)) * i;
                    var infoObj = Marshal.PtrToStructure(infoPtr, typeof(IMVDefine.IMV_DeviceInfo));
                    if (infoObj == null) continue;
                    var info = (IMVDefine.IMV_DeviceInfo)infoObj;

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

                SafeFireAndForget(_uiController.UpdateConnection("cam", true), "更新相机状态");
                SafeFireAndForget(_uiController.LogToFrontend("✓ 相机开启成功", "success"), "相机开启日志");
                // 自动连接PLC
                SafeFireAndForget(ConnectToPlcAsync(), "PLC自动连接");
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

                string? selectedFile = await ShowOpenFileDialogOnStaThread("选择测试图片", "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*");

                if (string.IsNullOrEmpty(selectedFile))
                {
                    await _uiController.LogToFrontend("已取消测试", "warning");
                    return;
                }

                await _uiController.LogToFrontend($"加载图片: {Path.GetFileName(selectedFile)}", "info");

                // 读取图片
                using (Bitmap originalBitmap = new Bitmap(selectedFile))
                {
                    // 检查模型是否初始化
                    if (_modelManager == null || !_modelManager.IsPrimaryLoaded)
                    {
                        if (yolo == null)
                        {
                            await _uiController.LogToFrontend("YOLO模型未初始化", "error");
                            return;
                        }
                    }

                    // 执行YOLO检测
                    Stopwatch sw = Stopwatch.StartNew();

                    float conf = _appConfig.Confidence;
                    float iou = _appConfig.IouThreshold;
                    bool globalIou = _appConfig.EnableGlobalIou;
                    int highSpeed = _appConfig.EnablePreprocessing ? 1 : 0;

                    List<YoloResult> allResults;
                    string usedModelName = "";
                    YoloDetection.ModelRole usedRole = YoloDetection.ModelRole.Primary;
                    string[] usedModelLabels = Array.Empty<string>();  // 存储实际使用模型的标签

                    // 使用多模型管理器进行推理（支持自动切换）
                    if (_modelManager != null && _modelManager.IsPrimaryLoaded)
                    {
                        var inferenceResult = await _modelManager.InferenceWithFallbackAsync(originalBitmap, conf, iou, globalIou, highSpeed);
                        allResults = inferenceResult.Results;
                        usedModelName = inferenceResult.UsedModelName;
                        usedRole = inferenceResult.UsedModel;
                        usedModelLabels = inferenceResult.UsedModelLabels;  // 使用实际模型的标签

                        // 记录使用的模型
                        if (inferenceResult.WasFallback)
                        {
                            await _uiController.LogToFrontend($"⚡ 主模型未检测到, 已切换到: {usedModelName}", "warning");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"使用模型: {usedModelName}", "info");
                        }

                        // 发送性能指标
                        if (_modelManager.PrimaryDetector?.LastMetrics != null)
                        {
                            await _uiController.SendInferenceMetrics(_modelManager.PrimaryDetector.LastMetrics);
                        }
                    }
                    else
                    {
                        // 向后兼容：使用旧的单模型推理
                        allResults = await Task.Run(() => yolo!.Inference(originalBitmap, conf, iou, globalIou, highSpeed));
                        usedModelLabels = yolo?.Labels ?? Array.Empty<string>();
                        if (yolo!.LastMetrics != null)
                        {
                            await _uiController.SendInferenceMetrics(yolo.LastMetrics);
                        }
                    }

                    sw.Stop();

                    // 标准化需要imgW/H
                    List<YoloResult> pixelResults = StandardizeYoloResults(allResults, originalBitmap.Width, originalBitmap.Height);

                    // 应用UI绘制的ROI过滤
                    List<YoloResult> results = FilterByROI(pixelResults, originalBitmap.Width, originalBitmap.Height);

                    // 使用实际推理模型的标签列表（关键修复：确保辅助模型的标签正确显示）
                    string[] labels = usedModelLabels;

                    // 3. 判别该次是否合格
                    int targetCount = results.Count(r =>
                    {
                        int labelIndex = (int)r.BasicData[5];
                        if (labelIndex >= 0 && labelIndex < labels.Length)
                        {
                            return labels[labelIndex].Equals(_appConfig.TargetLabel, StringComparison.OrdinalIgnoreCase);
                        }
                        return false;
                    });
                    bool isQualified = (targetCount == _appConfig.TargetCount);


                    string roiInfo = _currentROI != null ? $" (ROI过滤: {allResults.Count} → {results.Count})" : "";
                    string objDesc = GetDetectedObjectsDescription(results, usedModelLabels);
                    string modelInfo = usedRole != YoloDetection.ModelRole.Primary ? $" [模型: {usedModelName}]" : "";
                    await _uiController.LogDetectionToFrontend($"检测完成! 耗时: {sw.ElapsedMilliseconds}ms, 检测到 {objDesc}{roiInfo}{modelInfo}, 判定: {(isQualified ? "合格" : "不合格")}", isQualified ? "success" : "error");

                    // 4. 更新UI、PLC、保存结果 (复用生产逻辑)
                    UpdateUIAndPLC(isQualified, results, usedModelLabels);
                    ProcessAndSaveImages(originalBitmap, results, isQualified, usedModelLabels);
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"测试异常: {ex.Message}", "error");
            }
        }

        /// <summary>
        /// 在独立的STA线程中运行OpenFileDialog，彻底解决WebView2线程冲突导致的闪退问题
        /// </summary>
        private Task<string?> ShowOpenFileDialogOnStaThread(string title, string filter)
        {
            var tcs = new TaskCompletionSource<string?>();

            Thread thread = new Thread(() =>
            {
                try
                {
                    using var ofd = new OpenFileDialog();
                    ofd.Title = title;
                    ofd.Filter = filter;
                    ofd.Multiselect = false;
                    ofd.AutoUpgradeEnabled = true; // 在独立线程中通常可以恢复新版界面

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        tcs.SetResult(ofd.FileName);
                    }
                    else
                    {
                        tcs.SetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
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

        private string GetDetectedObjectsDescription(List<YoloResult>? results, string[]? labels = null)
        {
            if (results == null || results.Count == 0) return "未检测到物体";

            // 使用传入的标签列表，否则回退到默认
            string[]? actualLabels = labels ?? _modelManager?.PrimaryLabels ?? yolo?.Labels;
            if (actualLabels == null) return $"{results.Count} 个物体";

            var descriptions = results
                .GroupBy(r => (int)r.BasicData[5])
                .Select(g =>
                {
                    int index = g.Key;
                    string name = (index >= 0 && index < actualLabels.Length) ? actualLabels[index] : $"未知({index})";

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
                    SafeFireAndForget(ProcessFrame(frame), "处理图像帧");
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
        // 对应原 btnCapture_Click_1
        private async void btnCapture_Logic()
        {
            try
            {
                var result = await RunDetectionOnceAsync();
                if (result != null)
                {
                    await UpdateUIAndPLC(result.IsQualified, result.Results, result.UsedModelLabels);
                    ProcessAndSaveImages(result.OriginalBitmap, result.Results, result.IsQualified, result.UsedModelLabels);
                    result.OriginalBitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"手动检测异常: {ex.Message}", "error");
            }
        }

        private class DetectionResult
        {
            public bool IsQualified { get; set; }
            public List<YoloResult>? Results { get; set; }
            public Bitmap? OriginalBitmap { get; set; }
            public long ElapsedMs { get; set; }
            /// <summary>使用的模型标签列表（多模型切换时关键）</summary>
            public string[]? UsedModelLabels { get; set; }
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
            Bitmap? originalBitmap = null;

            try
            {
                cam.IMV_ExecuteCommandFeature("TriggerSoftware");
                int res = cam.IMV_GetFrame(ref frame, 3000);
                if (res != IMVDefine.IMV_OK)
                {
                    _uiController.LogToFrontend("获取图像帧失败", "error");
                    return null;
                }

                originalBitmap = ConvertFrameToBitmap(frame);
                sw.Start();

                // 保存最后一帧用于传统视觉预览
                lock (_frameLock)
                {
                    _lastCapturedFrame?.Dispose();
                    _lastCapturedFrame = BitmapConverter.ToMat(originalBitmap);
                }

                // 检查模型是否初始化
                if (_modelManager == null || !_modelManager.IsPrimaryLoaded)
                {
                    if (yolo == null) throw new Exception("YOLO模型未初始化");
                }

                float conf = _appConfig.Confidence;
                float iou = _appConfig.IouThreshold;
                bool globalIou = _appConfig.EnableGlobalIou;
                int highSpeed = _appConfig.EnablePreprocessing ? 1 : 0;

                List<YoloResult> allResults;
                string usedModelName = "";
                YoloDetection.ModelRole usedRole = YoloDetection.ModelRole.Primary;
                string[] usedModelLabels = Array.Empty<string>();  // 存储实际使用模型的标签

                // 使用多模型管理器进行推理（支持自动切换）
                if (_modelManager != null && _modelManager.IsPrimaryLoaded)
                {
                    var inferenceResult = await _modelManager.InferenceWithFallbackAsync(originalBitmap, conf, iou, globalIou, highSpeed);
                    allResults = inferenceResult.Results;
                    usedModelName = inferenceResult.UsedModelName;
                    usedRole = inferenceResult.UsedModel;
                    usedModelLabels = inferenceResult.UsedModelLabels;  // 使用实际模型的标签

                    // 记录使用的模型
                    if (inferenceResult.WasFallback)
                    {
                        await _uiController.LogToFrontend($"主模型未检测到, 切换到: {usedModelName}", "warning");
                    }
                }
                else
                {
                    // 向后兼容：使用旧的单模型推理
                    allResults = await Task.Run(() => yolo!.Inference(originalBitmap, conf, iou, globalIou, highSpeed));
                    usedModelLabels = yolo?.Labels ?? Array.Empty<string>();
                }

                List<YoloResult> pixelResults = StandardizeYoloResults(allResults, originalBitmap.Width, originalBitmap.Height);
                // 使用内部参数进行ROI过滤
                List<YoloResult> finalResults = FilterResultsByROIWithThreshold(pixelResults, overlapThreshold);

                // 应用UI绘制的ROI过滤
                finalResults = FilterByROI(finalResults, originalBitmap.Width, originalBitmap.Height);

                // 使用实际推理模型的标签列表（关键修复：确保辅助模型的标签正确显示）
                string[] labels = usedModelLabels;
                int targetCount = finalResults.Count(r =>
                {
                    int labelIndex = (int)r.BasicData[5];
                    if (labelIndex >= 0 && labelIndex < labels.Length)
                    {
                        return labels[labelIndex].Equals(_appConfig.TargetLabel, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                });
                bool isQualified = (targetCount == _appConfig.TargetCount);

                sw.Stop();
                return new DetectionResult
                {
                    IsQualified = isQualified,
                    Results = finalResults,
                    OriginalBitmap = originalBitmap,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    UsedModelLabels = usedModelLabels
                };
            }
            catch (Exception ex)
            {
                originalBitmap?.Dispose(); // 发生异常时释放Bitmap
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
                newItem.MaskData = r.MaskData?.Clone() ?? new Mat();
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

        private async Task UpdateUIAndPLC(bool isQualified, List<YoloResult>? results, string[]? labels = null)
        {
            // 更新 WebUI
            detectionStats.AddRecord(isQualified);
            await _uiController.UpdateUI(detectionStats.TotalCount, detectionStats.QualifiedCount, detectionStats.UnqualifiedCount);

            // 可以在html添加 showResultOverlay(bool) 接口，这里先不传
            // 若 WebUIController 支持，可调用 _uiController.ShowResult(isQualified);

            string[] actualLabels = labels ?? _modelManager?.PrimaryLabels ?? yolo?.Labels;

            StringBuilder sb = new StringBuilder();
            if (actualLabels != null && results != null)
            {
                foreach (var r in results)
                {
                    int labelIdx = (int)r.BasicData[5];
                    string label = (labelIdx >= 0 && labelIdx < actualLabels.Length) ? actualLabels[labelIdx] : $"Unknown({labelIdx})";
                    float conf = r.BasicData[4];
                    sb.AppendLine($"发现物体: {label} ({conf:P0})");
                }
            }
            // 生产模式下的每次检测结果也输出到检测日志窗口
            string objDesc = GetDetectedObjectsDescription(results, actualLabels);
            await _uiController.LogDetectionToFrontend($"PLC触发: {(isQualified ? "OK" : "NG")} | {objDesc}", isQualified ? "success" : "error");

            WriteDetectionLog(sb, isQualified);

            // 下发 PLC
            await WriteDetectionResult(isQualified);
        }

        private void ProcessAndSaveImages(Bitmap? original, List<YoloResult>? results, bool isQualified, string[]? labels = null)
        {
            if (original == null) return;

            using (Bitmap roiMarked = DrawROIBorder(original))
            {
                if (yolo == null || results == null) return;

                // 使用传入的标签列表，否则回退到主模型标签
                string[] actualLabels = labels ?? _modelManager?.PrimaryLabels ?? yolo.Labels;

                using (Image finalImg = yolo.GenerateImage(roiMarked, results, actualLabels))
                {
                    // 更新 WebUI 图片
                    using (Bitmap webImg = new Bitmap(finalImg))
                    {
                        SendImageToWeb(webImg);
                    }
                    // 保存
                    SaveImage(roiMarked, isQualified);
                }
            }
        }

        /// <summary>
        /// 将图片发送到前端显示
        /// <para>注意：此方法不负责 Dispose 传入的 Bitmap，调用者需自行管理生命周期</para>
        /// </summary>
        /// <param name="bmp">要显示的图片对象</param>
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
        private List<YoloResult> FilterResultsByROIWithThreshold(List<YoloResult>? input, float threshold)
        {
            if (input == null) return new List<YoloResult>();
            if (!useROI || !isROISet) return input;
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
            InitYoloAndMultiModel();
        }

        /// <summary>
        /// 初始化YOLO检测器和多模型管理器
        /// </summary>
        private void InitYoloAndMultiModel()
        {
            if (string.IsNullOrEmpty(模型名)) return;

            string primaryModelPath = Path.Combine(模型路径, 模型名);
            if (!File.Exists(primaryModelPath))
            {
                SafeFireAndForget(_uiController.LogToFrontend($"模型文件不存在: {模型名}", "error"), "模型加载");
                return;
            }

            try
            {
                // 释放旧资源
                yolo?.Dispose();
                _modelManager?.Dispose();

                // 初始化多模型管理器
                _modelManager = new MultiModelManager(_appConfig.EnableGpu, _appConfig.GpuIndex);
                _modelManager.EnableFallback = _appConfig.EnableMultiModelFallback;

                // 加载主模型
                _modelManager.LoadPrimaryModel(primaryModelPath);

                // 向后兼容：同时更新yolo引用
                yolo = _modelManager.PrimaryDetector;

                // 设置任务类型
                _modelManager.SetTaskMode((YoloDetection.YoloTaskType)_appConfig.TaskType);

                SafeFireAndForget(_uiController.LogToFrontend($"✓ 主模型已加载: {模型名}"), "模型加载");

                // 加载辅助模型（如果配置了）
                if (!string.IsNullOrEmpty(_appConfig.Auxiliary1ModelPath))
                {
                    string aux1Path = Path.Combine(模型路径, _appConfig.Auxiliary1ModelPath);
                    if (File.Exists(aux1Path))
                    {
                        _modelManager.LoadAuxiliary1Model(aux1Path);
                        SafeFireAndForget(_uiController.LogToFrontend($"✓ 辅助模型1已加载: {_appConfig.Auxiliary1ModelPath}"), "模型加载");
                    }
                }

                if (!string.IsNullOrEmpty(_appConfig.Auxiliary2ModelPath))
                {
                    string aux2Path = Path.Combine(模型路径, _appConfig.Auxiliary2ModelPath);
                    if (File.Exists(aux2Path))
                    {
                        _modelManager.LoadAuxiliary2Model(aux2Path);
                        SafeFireAndForget(_uiController.LogToFrontend($"✓ 辅助模型2已加载: {_appConfig.Auxiliary2ModelPath}"), "模型加载");
                    }
                }
            }
            catch (Exception ex)
            {
                SafeFireAndForget(_uiController.LogToFrontend($"模型加载失败: {ex.Message}", "error"), "模型加载");
            }
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
                catch (Exception ex)
                {
                    // 忽略图片保存错误，以免阻塞主流程
                    Debug.WriteLine($"SaveImage Error: {ex.Message}");
                }
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

        private void WriteStartupLog(string action, string? serial)
        {
            try
            {
                string msg = $"[{DateTime.Now}] {action} {(serial != null ? "SN:" + serial : "")}\n";
                File.AppendAllText(StartupLogPath, msg);
            }
            catch (Exception ex)
            {
                // 忽略启动日志写入错误
                Debug.WriteLine($"WriteStartupLog Error: {ex.Message}");
            }
        }

        private void LogError(string msg)
        {
            try
            {
                string logFile = Path.Combine(Path_Logs, "error_log.txt");
                string content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n{Environment.StackTrace}\r\n--------------------------------\r\n";
                // 使用追加模式写入，如果文件不存在会自动创建
                File.AppendAllText(logFile, content);
            }
            catch (Exception ex)
            {
                // 日志本身写入失败，仅输出到Debug，不再抛出以免死循环
                Debug.WriteLine($"LogError Failed: {ex.Message}");
            }
        }

        #endregion
    }
}