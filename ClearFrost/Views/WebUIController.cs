using ClearFrost.Config;
using ClearFrost.Models;
// ============================================================================
// 文件名: WebUIController.cs
// 描述:   WebView2 前后端通信控制器 - C# 与 HTML/JS 前端的桥梁
//
// 功能概述:
//   - 初始化 WebView2 环境并加载 HTML 前端
//   - 提供 C# → JS 的方法调用 (ExecuteScriptAsync)
//   - 处理 JS → C# 的消息接收 (WebMessageReceived)
//   - 支持开发模式热更新 (自动查找源码目录)
//
// 事件定义:
//   - OnStartSystem, OnOpenCamera, OnManualDetect, OnConnectPlc, ...  (操作事件)
//   - OnSaveSettings, OnSaveProjectPreset, ...        (配置事件)
//
// 前端通信:
//   - 发送: UpdateUI(), UpdateImage(), LogToFrontend(), SendCameraList(), ...
//   - 接收: 通过 { "cmd": "xxx", "value": ... } JSON 格式解析
//
// 作者: 蘅芜君
// 创建日期: 2024
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using OpenCvSharp;
using ClearFrost.Core.Inspection;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    /// <summary>
    /// Manages the WebView2 control and communication between C# and the Web frontend.
    /// </summary>
    public partial class WebUIController : IDisposable
    {
        private const string PreviewHostName = "preview.local";
        private const string ImageHostName = "ng-images.local";

        private WebView2? _webView;
        private readonly object _logThrottleLock = new object();
        private long _lastFrontendLogTick;
        private const int FrontendLogThrottleMs = 100;
        private long _lastImagePushTick;
        private int _imagePushInProgress;
        private int _previewFrameToggle;
        private long _previewFrameId;
        private string _webPreviewCachePath = string.Empty;
        private const int ImagePushMinIntervalMs = 50;
        private EventHandler<CoreWebView2WebMessageReceivedEventArgs>? _webMessageReceivedHandler;
        private EventHandler<CoreWebView2NavigationCompletedEventArgs>? _navigationCompletedHandler;
        private bool _disposed;

        // Events to notify the main window about frontend actions
        public event EventHandler? OnFindCamera;
        public event EventHandler? OnStartSystem;
        public event EventHandler? OnStopSystem;
        public event EventHandler? OnOpenCamera;
        public event EventHandler? OnManualDetect;
        public event EventHandler<string>? OnCaptureCameraPreview;
        public event EventHandler? OnManualRelease;
        public event EventHandler? OnOpenSettings;
        public event EventHandler? OnGetModelList;
        public event EventHandler<string>? OnChangeModel;
        public event EventHandler<int>? OnThresholdChanged;
        public event EventHandler? OnAppReady;
        public event EventHandler? OnTestYolo;
        public event EventHandler? OnExitApp;
        public event EventHandler? OnMinimizeApp;
        public event EventHandler? OnToggleMaximize;
        public event EventHandler? OnStartDrag;
        public event EventHandler? OnConnectPlc;
        public event EventHandler? OnRequestHealthSnapshot;
        public event EventHandler<float[]>? OnUpdateROI;
        public event EventHandler<float>? OnSetConfidence;
        public event EventHandler<float>? OnSetIou;
        public event EventHandler<int>? OnSetTaskType;  // YOLO任务类型设置事件
        public event EventHandler<string>? OnSaveSettings;
        public event EventHandler<string>? OnSaveProjectPreset;
        public event EventHandler<string>? OnDeleteProjectPreset;
        public event EventHandler? OnGetProjectPresets;
        public event EventHandler? OnExportConfigMigration;
        public event EventHandler? OnImportConfigMigration;
        public event EventHandler? OnSelectStorageFolder;
        public event EventHandler? OnGetStatisticsHistory;
        public event EventHandler? OnClearStatisticsHistory;
        public event EventHandler? OnResetStatistics;
        public event EventHandler? OnCollectDataset;
        public event EventHandler<string>? OnRunHistoryRulePreview;

        // ================== 多相机事件 ==================
        public event EventHandler? OnGetCameraList;
        public event EventHandler<string>? OnSwitchCamera;
        public event EventHandler<string>? OnAddCamera;  // JSON格式的相机数据
        public event EventHandler<string>? OnDeleteCamera;  // 相机ID
        public event EventHandler? OnSuperSearchCameras;  // 华睿/超级搜索（同一实现）
        public event EventHandler? OnSuperSearchCamerasHik;  // 相机超级搜索 (海康)
        public event EventHandler<string>? OnDirectConnectCamera;  // 直接连接相机（JSON格式）

        // ================== 多模型切换事件 ==================
        public event EventHandler<string>? OnSetAuxiliary1Model;
        public event EventHandler<string>? OnSetAuxiliary2Model;
        public event EventHandler<bool>? OnToggleMultiModelFallback;

        // ================== 串口光电事件 ==================
        public event EventHandler? OnSerialAutoDetectPorts;
        public event EventHandler? OnSerialTestTrigger;
        public event EventHandler? OnSerialSimulateTrigger;

        public WebUIController()
        {
        }

        public string ImageBasePath { get; set; } = "";
        public bool UseFileBackedImageTransport { get; set; }
        public IDatabaseService? DatabaseService { get; set; }

        /// <summary>
        /// Maps the image folder to a virtual host for direct access.
        /// </summary>
        public void SetImageMapping(string localPath)
        {
            WebView2? webView = _webView;
            if (!IsWebViewControlUsable(webView) || !Directory.Exists(localPath))
            {
                return;
            }

            void SetMapping()
            {
                if (!IsWebViewReadyOnUiThread(webView))
                {
                    return;
                }

                // Map http://ng-images.local/ to the local folder
                webView!.CoreWebView2!.SetVirtualHostNameToFolderMapping(
                    ImageHostName,
                    localPath,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            if (webView!.InvokeRequired)
            {
                try
                {
                    webView.BeginInvoke(new Action(SetMapping));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
                {
                    Debug.WriteLine($"[WebUIController] SetImageMapping BeginInvoke skipped: {ex.Message}");
                }
                return;
            }

            SetMapping();
        }

        /// <summary>
        /// Initializes the WebView2 environment and mapping.
        /// </summary>
        public async Task InitializeAsync(WebView2 webView)
        {
            _webView = webView;
            try
            {
                // [安全工业模式] UDF 加入版本号隔离，升级后不继承旧版缓存
                string appVer = AppVersion.CacheKey;
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GreeVision_WebView2", appVer);
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                // Default path (production)
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "html");
                bool isDevMode = false;

#if DEBUG
                // Robust Dev Mode: Search upwards for the source 'html' directory
                // This allows editing files in VS Code and seeing changes immediately without build
                string? sourcePath = TryFindSourceHtmlPath(AppDomain.CurrentDomain.BaseDirectory);
                if (!string.IsNullOrEmpty(sourcePath))
                {
                    htmlPath = sourcePath;
                    isDevMode = true;
                }
#endif

                if (!Directory.Exists(htmlPath))
                {
                    throw new DirectoryNotFoundException($"Web UI 资源目录不存在: {htmlPath}");
                }

                string indexPath = Path.Combine(htmlPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    throw new FileNotFoundException("Web UI 入口文件不存在", indexPath);
                }

                _webPreviewCachePath = Path.Combine(userDataFolder, "preview-cache");
                Directory.CreateDirectory(_webPreviewCachePath);

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", htmlPath, CoreWebView2HostResourceAccessKind.Allow);
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(PreviewHostName, _webPreviewCachePath, CoreWebView2HostResourceAccessKind.Allow);

                // Disable some browser features for industrial app look and feel
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // Register message received handler BEFORE navigation to ensure no messages (like app_ready) are missed
                _webMessageReceivedHandler ??= CoreWebView2_WebMessageReceived;
                _webView.CoreWebView2.WebMessageReceived -= _webMessageReceivedHandler;
                _webView.CoreWebView2.WebMessageReceived += _webMessageReceivedHandler;

                if (isDevMode)
                {
                    await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__CF_DEV_MODE = true;");

                    _navigationCompletedHandler ??= CoreWebView2_NavigationCompleted;
                    _webView.CoreWebView2.NavigationCompleted -= _navigationCompletedHandler;
                    _webView.CoreWebView2.NavigationCompleted += _navigationCompletedHandler;

                    string timestamp = DateTime.Now.Ticks.ToString();
                    _webView.CoreWebView2.Navigate($"https://app.local/index.html?v={timestamp}");
                }
                else
                {
                    if (_navigationCompletedHandler != null)
                    {
                        _webView.CoreWebView2.NavigationCompleted -= _navigationCompletedHandler;
                    }

                    _webView.CoreWebView2.Navigate($"https://app.local/index.html?v={DateTime.Now.Ticks}");
                }

                // Warn/Notify user if in Dev Mode
                if (isDevMode)
                {
                    // Delay slightly to allow page load, then log
                    _ = Task.Delay(1000).ContinueWith(async _ => await LogToFrontend($"[DEV] Source Mapping Active: {htmlPath}", "warning"));
                }
            }
            catch (Exception ex)
            {
                _webView = null;
                throw new InvalidOperationException($"WebView2 初始化失败: {ex.Message}", ex);
            }
        }

        private string? TryFindSourceHtmlPath(string startPath)
        {
            DirectoryInfo? dir = new DirectoryInfo(startPath);
            int maxDepth = 6; // Look up to 6 levels
            while (dir != null && maxDepth > 0)
            {
                string target = Path.Combine(dir.FullName, "html");
                // Check if 'html' exists AND 'ClearFrost.csproj' exists (to confirm it's the source root)
                if (Directory.Exists(target) && File.Exists(Path.Combine(dir.FullName, "ClearFrost.csproj")))
                {
                    return target;
                }
                dir = dir.Parent;
                maxDepth--;
            }
            return null;
        }

        /// <summary>
        /// Updates the production statistics on the frontend.
        /// </summary>
        /// <param name="total">Total count</param>
        /// <param name="ok">OK count</param>
        /// <param name="ng">NG count</param>
        public Task UpdateUI(int total, int ok, int ng)
        {
            var data = new { total = total, ok = ok, ng = ng };
            PostMessage("updateStatus", data);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Shows the OK/NG large result overlay.
        /// </summary>
        /// <param name="isOk">Result</param>
        public Task UpdateResult(bool isOk)
        {
            PostMessage("updateResult", new { isOk = isOk });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends inference performance metrics to frontend
        /// </summary>
        public Task SendInferenceMetrics(object metrics)
        {
            PostMessage("inferenceMetrics", metrics);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends the real-time camera image as base64 to the frontend.
        /// </summary>
        public async Task UpdateImage(string base64Image)
        {
            if (!IsWebViewControlUsable(_webView)) return;
            PostMessage("previewFrame", new
            {
                base64 = base64Image,
                frameId = Interlocked.Increment(ref _previewFrameId)
            });
        }

        public Task UpdateImageUrl(string url)
        {
            if (!IsWebViewControlUsable(_webView)) return Task.CompletedTask;

            PostMessage("previewFrame", new
            {
                url = url,
                frameId = Interlocked.Increment(ref _previewFrameId)
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends image from Mat after resizing and JPEG encoding to reduce WebView2 payload.
        /// </summary>
        public async Task UpdateImage(Mat image, int targetWidth = 960, int targetHeight = 540, int jpegQuality = 60)
        {
            if (!IsWebViewControlUsable(_webView) || image == null || image.Empty())
            {
                return;
            }

            long nowTick = Environment.TickCount64;
            if (nowTick - Volatile.Read(ref _lastImagePushTick) < ImagePushMinIntervalMs)
            {
                return;
            }

            // Avoid async backlog and JS heap pressure when image updates arrive too frequently.
            if (Interlocked.Exchange(ref _imagePushInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                nowTick = Environment.TickCount64;
                if (nowTick - Volatile.Read(ref _lastImagePushTick) < ImagePushMinIntervalMs)
                {
                    return;
                }

                using Mat resized = ResizeForPreview(image, targetWidth, targetHeight);

                int quality = Math.Clamp(jpegQuality, 1, 100);
                Cv2.ImEncode(".jpg", resized, out byte[] encoded, new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, quality) });

                if (UseFileBackedImageTransport && !string.IsNullOrWhiteSpace(_webPreviewCachePath))
                {
                    await UpdateImageFileAsync(encoded);
                }
                else
                {
                    string base64 = Convert.ToBase64String(encoded);
                    await UpdateImage(base64);
                }

                Volatile.Write(ref _lastImagePushTick, Environment.TickCount64);
            }
            finally
            {
                Volatile.Write(ref _imagePushInProgress, 0);
            }
        }

        /// <summary>
        /// Sends a single settings-modal camera preview frame to the frontend.
        /// </summary>
        public Task SendCameraPreviewFrame(Mat image, int targetWidth = 640, int targetHeight = 360, int jpegQuality = 70)
        {
            if (!IsWebViewControlUsable(_webView) || image == null || image.Empty())
            {
                return Task.CompletedTask;
            }

            using Mat resized = ResizeForPreview(image, targetWidth, targetHeight);
            int quality = Math.Clamp(jpegQuality, 1, 100);
            Cv2.ImEncode(".jpg", resized, out byte[] encoded, new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, quality) });

            PostMessage("cameraPreviewFrame", new
            {
                base64 = Convert.ToBase64String(encoded),
                frameId = Interlocked.Increment(ref _previewFrameId)
            });
            return Task.CompletedTask;
        }

        private async Task UpdateImageFileAsync(byte[] encoded)
        {
            if (string.IsNullOrWhiteSpace(_webPreviewCachePath))
            {
                string base64 = Convert.ToBase64String(encoded);
                await UpdateImage(base64);
                return;
            }

            int frameIndex = Interlocked.Increment(ref _previewFrameToggle);
            string fileName = (frameIndex & 1) == 0 ? "frame_a.jpg" : "frame_b.jpg";
            string filePath = Path.Combine(_webPreviewCachePath, fileName);

            await File.WriteAllBytesAsync(filePath, encoded);

            string imageUrl = $"https://{PreviewHostName}/{fileName}?t={Environment.TickCount64}";
            await UpdateImageUrl(imageUrl);
        }

        private static Mat ResizeForPreview(Mat image, int targetWidth, int targetHeight)
        {
            targetWidth = Math.Max(1, targetWidth);
            targetHeight = Math.Max(1, targetHeight);

            double scale = Math.Min(
                targetWidth / (double)Math.Max(1, image.Width),
                targetHeight / (double)Math.Max(1, image.Height));
            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));

            Mat canvas = new Mat(new OpenCvSharp.Size(targetWidth, targetHeight), image.Type(), Scalar.All(0));
            using Mat resized = new Mat();
            Cv2.Resize(image, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Linear);

            int x = (targetWidth - width) / 2;
            int y = (targetHeight - height) / 2;
            using Mat roi = new Mat(canvas, new Rect(x, y, width, height));
            resized.CopyTo(roi);
            return canvas;
        }

        /// <summary>
        /// Sends the model list to the frontend (Requirement from Step 177/147).
        /// </summary>
        public Task SendModelList(string[] models)
        {
            PostMessage("modelList", new { models = models });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Updates the camera name displayed on the frontend.
        /// </summary>
        public Task UpdateCameraName(string name)
        {
            PostMessage("updateCameraName", new { name = name });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Updates connection status indicators on the frontend.
        /// </summary>
        /// <param name="type">"cam" or "plc"</param>
        /// <param name="isConnected">Connection state</param>
        public Task UpdateConnection(string type, bool isConnected)
        {
            PostMessage("updateConnection", new { type = type, isConnected = isConnected });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Flashes the trigger-photo indicator on the frontend.
        /// Called when a trigger signal is received from PLC or serial photoelectric input.
        /// </summary>
        public Task FlashPlcTrigger()
        {
            PostMessage("flashPlcTrigger");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends current config to frontend to open settings modal
        /// </summary>
        public Task SendCurrentConfig(AppConfig config)
        {
            PostMessage("configSnapshot", new { config = config, open = true });
            return Task.CompletedTask;
        }

        public Task InitSettings(AppConfig config)
        {
            PostMessage("configSnapshot", new { config = config, open = false });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Execute arbitrary JavaScript code
        /// </summary>
        public async Task ExecuteScriptAsync(string script)
        {
            await ExecuteScriptOnUiThreadAsync(script);
        }

        private Task ExecuteScriptOnUiThreadAsync(string script)
        {
            WebView2? webView = _webView;
            if (!IsWebViewControlUsable(webView))
            {
                return Task.CompletedTask;
            }

            if (webView!.InvokeRequired)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    webView.BeginInvoke(new Action(async () =>
                    {
                        await ExecuteScriptCoreAsync(webView, script, tcs);
                    }));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
                {
                    Debug.WriteLine($"[WebUIController] ExecuteScript BeginInvoke skipped: {ex.Message}");
                    tcs.TrySetResult(false);
                }

                return tcs.Task;
            }

            return ExecuteScriptCoreAsync(webView, script);
        }

        private void PostMessage(string type, object? data = null, string? requestId = null)
        {
            WebView2? webView = _webView;
            if (!IsWebViewControlUsable(webView)) return;

            string json = JsonSerializer.Serialize(new
            {
                type = type,
                data = data,
                requestId = requestId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            void PostCoreMessage()
            {
                try
                {
                    if (IsWebViewReadyOnUiThread(webView))
                    {
                        webView!.CoreWebView2!.PostWebMessageAsJson(json);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebUIController] PostMessage failed: {ex.Message}");
                }
            }

            if (webView!.InvokeRequired)
            {
                try
                {
                    webView.BeginInvoke(new Action(PostCoreMessage));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
                {
                    Debug.WriteLine($"[WebUIController] PostMessage BeginInvoke skipped: {ex.Message}");
                }
                return;
            }

            PostCoreMessage();
        }

        private static bool IsWebViewControlUsable(WebView2? webView)
        {
            return webView != null &&
                   !webView.IsDisposed &&
                   !webView.Disposing &&
                   webView.IsHandleCreated;
        }

        private static bool IsWebViewReadyOnUiThread(WebView2? webView)
        {
            return IsWebViewControlUsable(webView) &&
                   webView!.CoreWebView2 != null;
        }

        private static async Task ExecuteScriptCoreAsync(WebView2 webView, string script, TaskCompletionSource<bool>? completion = null)
        {
            try
            {
                if (!IsWebViewReadyOnUiThread(webView))
                {
                    completion?.TrySetResult(false);
                    return;
                }

                await webView.ExecuteScriptAsync(script);
                completion?.TrySetResult(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                Debug.WriteLine($"[WebUIController] ExecuteScript skipped: {ex.Message}");
                completion?.TrySetResult(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebUIController] ExecuteScript failed: {ex.Message}");
                completion?.TrySetException(ex);
                if (completion == null)
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Processes messages received from the frontend.
        /// Expected JSON format: { "cmd": "start_camera", "value": ... }
        /// </summary>
        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // Use WebMessageAsJson as TryGetWebMessageAsString might be missing/obsolete
                string json = e.WebMessageAsJson;
                if (string.IsNullOrEmpty(json)) return;

                // Parse the JSON
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    string? requestId = root.TryGetProperty("requestId", out JsonElement requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    if (root.TryGetProperty("cmd", out JsonElement cmdElement))
                    {
                        string cmd = cmdElement.GetString() ?? string.Empty;

                        switch (cmd)
                        {
                            case "find_camera":
                                OnFindCamera?.Invoke(this, EventArgs.Empty);
                                break;
                            case "start_system":
                                OnStartSystem?.Invoke(this, EventArgs.Empty);
                                break;
                            case "stop_system":
                                OnStopSystem?.Invoke(this, EventArgs.Empty);
                                break;
                            case "open_camera":
                                OnOpenCamera?.Invoke(this, EventArgs.Empty);
                                break;
                            case "manual_detect":
                                OnManualDetect?.Invoke(this, EventArgs.Empty);
                                break;
                            case "capture_camera_preview":
                                OnCaptureCameraPreview?.Invoke(
                                    this,
                                    root.TryGetProperty("value", out JsonElement previewElement)
                                        ? previewElement.GetRawText()
                                        : "{}");
                                break;
                            case "manual_release":
                                OnManualRelease?.Invoke(this, EventArgs.Empty);
                                break;
                            case "open_settings":
                                OnOpenSettings?.Invoke(this, EventArgs.Empty);
                                break;
                            case "get_project_presets":
                                OnGetProjectPresets?.Invoke(this, EventArgs.Empty);
                                break;
                            case "export_config_migration":
                                OnExportConfigMigration?.Invoke(this, EventArgs.Empty);
                                break;
                            case "import_config_migration":
                                OnImportConfigMigration?.Invoke(this, EventArgs.Empty);
                                break;
                            case "save_project_preset":
                                if (root.TryGetProperty("value", out JsonElement presetSaveElement))
                                {
                                    OnSaveProjectPreset?.Invoke(this, presetSaveElement.GetRawText());
                                }
                                break;
                            case "delete_project_preset":
                                if (root.TryGetProperty("value", out JsonElement presetDeleteElement))
                                {
                                    OnDeleteProjectPreset?.Invoke(this, presetDeleteElement.GetString() ?? string.Empty);
                                }
                                break;
                            case "change_model":
                                if (root.TryGetProperty("value", out JsonElement modelElement))
                                {
                                    OnChangeModel?.Invoke(this, modelElement.GetString() ?? "");
                                }
                                break;
                            case "get_model_list":
                                OnGetModelList?.Invoke(this, EventArgs.Empty);
                                break;
                            case "app_ready":
                                OnAppReady?.Invoke(this, EventArgs.Empty);
                                // Debug log
                                await LogToFrontend("收到 app_ready 指令");
                                break;
                            case "test_yolo":
                                OnTestYolo?.Invoke(this, EventArgs.Empty);
#if DEBUG
                                await LogToFrontend("收到 test_yolo 指令");
#endif
                                break;
                            case "update_roi":
                                if (root.TryGetProperty("value", out JsonElement valueElement) &&
                                    valueElement.TryGetProperty("rect", out JsonElement rectElement))
                                {
                                    try
                                    {
                                        var rectArray = JsonSerializer.Deserialize<float[]>(rectElement.GetRawText());
                                        if (rectArray != null && rectArray.Length == 4)
                                        {
                                            OnUpdateROI?.Invoke(this, rectArray);
#if DEBUG
                                            await LogToFrontend($"ROI已更新: [{string.Join(", ", rectArray.Select(v => v.ToString("F3")))}]");
#endif
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        await LogToFrontend($"ROI解析错误: {ex.Message}", "error");
                                    }
                                }
                                break;
                            case "exit_app":
                                await LogToFrontend("收到 exit_app 指令, 正在退出...");
                                OnExitApp?.Invoke(this, EventArgs.Empty);
                                break;
                            case "minimize_app":
                                OnMinimizeApp?.Invoke(this, EventArgs.Empty);
                                break;
                            case "toggle_maximize":
                                OnToggleMaximize?.Invoke(this, EventArgs.Empty);
                                break;
                            case "start_drag":
                                OnStartDrag?.Invoke(this, EventArgs.Empty);
                                break;
                            case "connect_plc":
                                OnConnectPlc?.Invoke(this, EventArgs.Empty);
                                break;
                            case "request_health_snapshot":
                                OnRequestHealthSnapshot?.Invoke(this, EventArgs.Empty);
                                break;
                            case "set_confidence":
                                if (root.TryGetProperty("value", out JsonElement confElement))
                                {
                                    float conf = confElement.GetSingle();
                                    OnSetConfidence?.Invoke(this, conf);
#if DEBUG
                                    await LogToFrontend($"置信度已设置: {conf:F2}");
#endif
                                }
                                break;
                            case "set_iou":
                                if (root.TryGetProperty("value", out JsonElement iouElement))
                                {
                                    float iou = iouElement.GetSingle();
                                    OnSetIou?.Invoke(this, iou);
#if DEBUG
                                    await LogToFrontend($"IOU阈值已设置: {iou:F2}");
#endif
                                }
                                break;
                            case "set_task_type":
                                if (root.TryGetProperty("value", out JsonElement taskTypeElement))
                                {
                                    int taskType = taskTypeElement.GetInt32();
                                    OnSetTaskType?.Invoke(this, taskType);
                                    string taskName = taskType switch
                                    {
                                        0 => "分类 (Classify)",
                                        1 => "目标检测 (Detect)",
                                        2 => "分割检测 (Segment Detect Only)",
                                        3 => "实例分割 (Segment)",
                                        5 => "姿态估计 (Pose)",
                                        6 => "旋转框检测 (OBB)",
                                        _ => $"未知 ({taskType})"
                                    };
#if DEBUG
                                    await LogToFrontend($"任务类型已设置: {taskName}");
#endif
                                }
                                break;
                            case "save_settings":
                                if (root.TryGetProperty("value", out JsonElement settingsElement))
                                {
                                    // value 现在是JSON对象，用 GetRawText() 获取其JSON字符串
                                    string jsonStr = settingsElement.GetRawText();
                                    OnSaveSettings?.Invoke(this, jsonStr);
                                }
                                break;
                            case "set_roi_threshold":
                            case "set_roi_threshold_final":
                                if (root.TryGetProperty("value", out JsonElement valElement))
                                {
                                    if (valElement.TryGetInt32(out int threshold))
                                    {
                                        OnThresholdChanged?.Invoke(this, threshold);
                                    }
                                }
                                break;
                            case "get_ng_dates":
                                await SendNGDates();
                                break;
                            case "get_ng_hours":
                                if (root.TryGetProperty("value", out JsonElement dateElement))
                                {
                                    await SendNGHours(dateElement.GetString());
                                }
                                break;
                            case "get_ng_images":
                                if (root.TryGetProperty("value", out JsonElement paramsElement))
                                {
                                    string date = paramsElement.GetProperty("date").GetString() ?? "";
                                    string hour = paramsElement.GetProperty("hour").GetString() ?? "";
                                    int pageSize = TryGetInt32Property(paramsElement, "pageSize") ?? 100;
                                    string? afterTimestamp = TryGetStringProperty(paramsElement, "afterTimestamp");
                                    long? afterId = TryGetInt64Property(paramsElement, "afterId");
                                    await SendNGImages(date, hour, pageSize, afterTimestamp, afterId, requestId);
                                }
                                break;
                            case "run_history_rule_preview":
                                if (root.TryGetProperty("value", out JsonElement historyRuleElement))
                                {
                                    OnRunHistoryRulePreview?.Invoke(this, historyRuleElement.GetRawText());
                                }
                                break;
                            case "select_storage_folder":
                                OnSelectStorageFolder?.Invoke(this, EventArgs.Empty);
                                break;
                            case "get_detection_logs":
                                await SendDetectionLogs();
                                break;
                            case "get_statistics_history":
                                OnGetStatisticsHistory?.Invoke(this, EventArgs.Empty);
                                break;
                            case "clear_stats_history":
                                OnClearStatisticsHistory?.Invoke(this, EventArgs.Empty);
                                break;
                            case "reset_statistics":
                                OnResetStatistics?.Invoke(this, EventArgs.Empty);
                                break;
                            case "collect_dataset":
                                OnCollectDataset?.Invoke(this, EventArgs.Empty);
                                break;

                            // ================== 多相机命令 ==================
                            case "get_camera_list":
                                OnGetCameraList?.Invoke(this, EventArgs.Empty);
                                break;
                            case "switch_camera":
                                if (root.TryGetProperty("value", out JsonElement camIdElement))
                                {
                                    string camId = camIdElement.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(camId))
                                    {
                                        OnSwitchCamera?.Invoke(this, camId);
                                    }
                                }
                                break;
                            case "add_camera":
                                if (root.TryGetProperty("value", out JsonElement addCamElement))
                                {
                                    string camJson = addCamElement.GetRawText();
                                    OnAddCamera?.Invoke(this, camJson);
                                }
                                break;
                            case "delete_camera":
                                if (root.TryGetProperty("value", out JsonElement delCamElement))
                                {
                                    string camIdToDelete = delCamElement.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(camIdToDelete))
                                    {
                                        OnDeleteCamera?.Invoke(this, camIdToDelete);
                                    }
                                }
                                break;
                            case "super_search_cameras":
                            case "search_huaray_cameras":
                                System.Diagnostics.Debug.WriteLine("[WebUIController] 收到华睿相机搜索命令");
                                OnSuperSearchCameras?.Invoke(this, EventArgs.Empty);
                                break;
                            case "super_search_cameras_hik":
                                OnSuperSearchCamerasHik?.Invoke(this, EventArgs.Empty);
                                break;
                            case "direct_connect_camera":
                                if (root.TryGetProperty("value", out JsonElement directConnectElement))
                                {
                                    string camJson = directConnectElement.GetRawText();
                                    OnDirectConnectCamera?.Invoke(this, camJson);
                                }
                                break;

                            // ================== 多模型切换命令 ==================
                            case "set_auxiliary1_model":
                                if (root.TryGetProperty("value", out JsonElement aux1Element))
                                {
                                    OnSetAuxiliary1Model?.Invoke(this, aux1Element.GetString() ?? "");
                                }
                                break;
                            case "set_auxiliary2_model":
                                if (root.TryGetProperty("value", out JsonElement aux2Element))
                                {
                                    OnSetAuxiliary2Model?.Invoke(this, aux2Element.GetString() ?? "");
                                }
                                break;
                            case "toggle_multi_model":
                                if (root.TryGetProperty("value", out JsonElement toggleElement))
                                {
                                    OnToggleMultiModelFallback?.Invoke(this, toggleElement.GetBoolean());
                                }
                                break;

                            // ================== 串口光电命令 ==================
                            case "serial_auto_detect_ports":
                                OnSerialAutoDetectPorts?.Invoke(this, EventArgs.Empty);
                                break;
                            case "serial_test_trigger":
                                OnSerialTestTrigger?.Invoke(this, EventArgs.Empty);
                                break;
                            case "serial_simulate_trigger":
                                OnSerialSimulateTrigger?.Invoke(this, EventArgs.Empty);
                                break;

                            default:
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Optionally log error to debugger or frontend
                System.Diagnostics.Debug.WriteLine($"Error processing web message: {ex.Message}");
            }
        }

        private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess || _webView?.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                    "Network.clearBrowserCache", "{}");
            }
            catch
            {
            }
        }

        /// <summary>
        /// Sends a log message to the upper "Detection Log" window.
        /// </summary>
        public Task LogDetectionToFrontend(string message, string type = "normal")
        {
            PostMessage("detectionLog", new { message = message, type = type });
            return Task.CompletedTask;
        }

        public Task LogToFrontend(string message, string type = "normal")
        {
            if (string.Equals(type, "normal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "info", StringComparison.OrdinalIgnoreCase))
            {
                long now = Environment.TickCount64;
                lock (_logThrottleLock)
                {
                    if (now - _lastFrontendLogTick < FrontendLogThrottleMs)
                    {
                        return Task.CompletedTask;
                    }

                    _lastFrontendLogTick = now;
                }
            }

            PostMessage("log", new { message = message, type = type });
            return Task.CompletedTask;
        }

        public async Task SendDetectionFrame(
            Mat image,
            bool isOk,
            StatisticsSnapshot? stats = null,
            string? logMessage = null,
            string logType = "normal",
            object? metrics = null,
            InspectionContext? inspection = null,
            int? actualCount = null,
            string? usedModelName = null,
            bool wasFallback = false,
            long? totalMs = null,
            string? sourceLabel = null,
            bool barcodeEnabled = false,
            string? productBarcode = null,
            bool? barcodeReadSucceeded = null,
            string? barcodeError = null,
            int? fallbackAttemptCount = null,
            string? fallbackSkippedReason = null,
            long? imageQueuePending = null,
            long? recordQueuePending = null,
            long? handshakeStartMs = null,
            long? plcResultWriteMs = null,
            long? handshakeCompleteMs = null,
            long? inferenceMs = null,
            string? ruleSummary = null,
            string? rulePrimaryReason = null,
            IReadOnlyList<string>? ruleDetails = null)
        {
            if (!IsWebViewControlUsable(_webView) || image == null || image.Empty())
            {
                return;
            }

            await UpdateImage(image);

            PostMessage("detectionFrame", new
            {
                isOk = isOk,
                stats = stats == null
                    ? null
                    : new
                    {
                        total = stats.TotalCount,
                        ok = stats.QualifiedCount,
                        ng = stats.UnqualifiedCount
                    },
                log = string.IsNullOrWhiteSpace(logMessage)
                    ? null
                    : new { message = logMessage, type = logType },
                metrics = metrics,
                totalMs = totalMs,
                inferenceMs = inferenceMs ?? inspection?.InferenceMs,
                actualCount = actualCount,
                usedModelName = usedModelName,
                wasFallback = wasFallback,
                fallbackAttemptCount = fallbackAttemptCount ?? inspection?.FallbackAttemptCount,
                fallbackSkippedReason = fallbackSkippedReason ?? inspection?.FallbackSkippedReason,
                imageQueuePending = imageQueuePending ?? inspection?.ImageQueuePending,
                recordQueuePending = recordQueuePending ?? inspection?.RecordQueuePending,
                handshakeStartMs = handshakeStartMs ?? inspection?.HandshakeStartMs,
                plcResultWriteMs = plcResultWriteMs ?? inspection?.PlcResultWriteMs,
                handshakeCompleteMs = handshakeCompleteMs ?? inspection?.HandshakeCompleteMs,
                ruleSummary = ruleSummary,
                rulePrimaryReason = rulePrimaryReason,
                ruleDetails = ruleDetails,
                sourceLabel = sourceLabel,
                inspection = inspection == null
                    ? null
                    : BuildInspectionPayload(
                        inspection,
                        isOk,
                        logMessage,
                        actualCount,
                        usedModelName,
                        wasFallback,
                        barcodeEnabled,
                        productBarcode,
                        barcodeReadSucceeded,
                        barcodeError,
                        ruleSummary,
                        rulePrimaryReason,
                        ruleDetails)
            });
        }

        public Task SendInspectionUpdate(
            InspectionContext context,
            bool? isOk = null,
            string? message = null,
            int? actualCount = null,
            string? usedModelName = null,
            bool wasFallback = false,
            bool barcodeEnabled = false,
            string? productBarcode = null,
            bool? barcodeReadSucceeded = null,
            string? barcodeError = null,
            string? ruleSummary = null,
            string? rulePrimaryReason = null,
            IReadOnlyList<string>? ruleDetails = null)
        {
            if (context == null)
            {
                return Task.CompletedTask;
            }

            PostMessage("inspectionUpdate", BuildInspectionPayload(
                context,
                isOk,
                message,
                actualCount,
                usedModelName,
                wasFallback,
                barcodeEnabled,
                productBarcode,
                barcodeReadSucceeded,
                barcodeError,
                ruleSummary,
                rulePrimaryReason,
                ruleDetails));
            return Task.CompletedTask;
        }

        public Task SendHealthSnapshot(object snapshot)
        {
            if (snapshot == null)
            {
                return Task.CompletedTask;
            }

            PostMessage("healthSnapshot", snapshot);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 发送数据集收集结果到前端
        /// </summary>
        public Task SendDatasetCollectResult(object result)
        {
            PostMessage("datasetCollectResult", result);
            return Task.CompletedTask;
        }

        private static object BuildInspectionPayload(
            InspectionContext context,
            bool? isOk,
            string? message,
            int? actualCount,
            string? usedModelName,
            bool wasFallback,
            bool barcodeEnabled,
            string? productBarcode,
            bool? barcodeReadSucceeded,
            string? barcodeError,
            string? ruleSummary,
            string? rulePrimaryReason,
            IReadOnlyList<string>? ruleDetails)
        {
            return new
            {
                inspectionId = context.InspectionId,
                triggerSource = context.TriggerSource,
                triggerSeq = context.TriggerSeq,
                resultSeq = context.ResultSeq,
                productBarcode = productBarcode,
                barcodeEnabled = barcodeEnabled,
                barcodeReadSucceeded = barcodeReadSucceeded,
                barcodeError = barcodeError,
                traceStatus = context.TraceStatus.ToString(),
                currentStage = context.CurrentStage.ToString(),
                errorStage = context.ErrorStage,
                errorCode = context.ErrorCode,
                errorMessage = context.ErrorMessage,
                totalMs = context.TotalMs,
                captureMs = context.CaptureMs,
                inferenceMs = context.InferenceMs,
                plcWriteMs = context.PlcWriteMs,
                handshakeStartMs = context.HandshakeStartMs,
                plcResultWriteMs = context.PlcResultWriteMs,
                handshakeCompleteMs = context.HandshakeCompleteMs,
                fallbackAttemptCount = context.FallbackAttemptCount,
                fallbackSkippedReason = context.FallbackSkippedReason,
                imageQueuePending = context.ImageQueuePending,
                recordQueuePending = context.RecordQueuePending,
                usedModelName = usedModelName,
                wasFallback = wasFallback,
                actualCount = actualCount,
                isOk = isOk,
                message = message,
                ruleSummary = ruleSummary,
                rulePrimaryReason = rulePrimaryReason,
                ruleDetails = ruleDetails
            };
        }

        public Task UpdateStoragePathInUI(string path)
        {
            PostMessage("configSnapshot", new { storagePath = path });
            return Task.CompletedTask;
        }

        private async Task SendNGDates()
        {
            if (_webView == null) return;
            try
            {
                if (DatabaseService == null)
                {
                    PostMessage("historyDates", Array.Empty<string>());
                    return;
                }

                List<string> dates = await DatabaseService.GetTraceDateKeysAsync(isQualified: false);
                PostMessage("historyDates", dates);
            }
            catch (Exception ex)
            {
                await LogToFrontend($"获取日期列表失败: {ex.Message}", "error");
                PostMessage("historyDates", Array.Empty<string>());
            }
        }

        private async Task SendNGHours(string? date)
        {
            if (string.IsNullOrEmpty(date) || _webView == null) return;
            try
            {
                if (DatabaseService == null || !TryParseTraceDate(date, out DateTime traceDate))
                {
                    PostMessage("historyHours", Array.Empty<string>());
                    return;
                }

                List<string> hours = await DatabaseService.GetTraceHourKeysAsync(traceDate, isQualified: false);
                PostMessage("historyHours", hours);
            }
            catch { PostMessage("historyHours", Array.Empty<string>()); }
        }

        private async Task SendNGImages(string date, string hour, int pageSize, string? afterTimestamp, long? afterId, string? requestId)
        {
            if (string.IsNullOrEmpty(date) || _webView == null) return;
            try
            {
                if (DatabaseService == null || !TryParseTraceDate(date, out DateTime traceDate))
                {
                    PostMessage("historyImages", new
                    {
                        records = Array.Empty<object>(),
                        images = Array.Empty<object>(),
                        hasMore = false,
                        pageSize = 0,
                        nextCursorTimestamp = (string?)null,
                        nextCursorId = (long?)null
                    }, requestId);
                    return;
                }

                var query = new DetectionTraceQuery
                {
                    IsQualified = false,
                    StartTime = traceDate.Date,
                    EndTime = traceDate.Date.AddDays(1).AddMilliseconds(-1),
                    Limit = pageSize,
                    AfterTimestamp = afterTimestamp,
                    AfterId = afterId
                };

                if (TryParseTraceHour(hour, out int traceHour))
                {
                    DateTime start = traceDate.Date.AddHours(traceHour);
                    query.StartTime = start;
                    query.EndTime = start.AddHours(1).AddMilliseconds(-1);
                }

                DetectionTracePage page = await DatabaseService.GetTraceRecordPageAsync(query);
                List<DetectionTraceRecord> records = page.Records.ToList();
                var imageFileCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                object[] payload = records
                    .Select(record => BuildTraceRecordPayload(record, imageFileCache))
                    .ToArray();
                PostMessage("historyImages", new
                {
                    records = payload,
                    images = payload,
                    hasMore = page.HasMore,
                    pageSize = page.PageSize,
                    nextCursorTimestamp = page.NextCursorTimestamp,
                    nextCursorId = page.NextCursorId
                }, requestId);
            }
            catch
            {
                PostMessage("historyImages", new
                {
                    records = Array.Empty<object>(),
                    images = Array.Empty<object>(),
                    hasMore = false,
                    pageSize = 0,
                    nextCursorTimestamp = (string?)null,
                    nextCursorId = (long?)null
                }, requestId);
            }
        }

        private object BuildTraceRecordPayload(
            DetectionTraceRecord record,
            IDictionary<string, IReadOnlyList<string>>? imageFileCache = null)
        {
            var resolution = string.IsNullOrWhiteSpace(ImageBasePath)
                ? DetectionTraceImageResolver.Resolve(record)
                : DetectionTraceImageResolver.Resolve(
                    record,
                    ImageBasePath,
                    File.Exists,
                    directory => GetCachedTraceImageFiles(imageFileCache, directory));
            string? imageUrl = TryCreateImageUrl(resolution.ImagePath);
            string? renderedImageUrl = TryCreateImageUrl(resolution.RenderedImagePath);
            bool hasRenderedImage = !string.IsNullOrWhiteSpace(renderedImageUrl);
            bool hasImage = !string.IsNullOrWhiteSpace(imageUrl);

            return new
            {
                id = record.Id,
                inspectionId = record.InspectionId,
                productBarcode = record.ProductBarcode,
                timestamp = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                isQualified = record.IsQualified,
                modelVersion = record.ModelVersion,
                modelName = record.ModelName,
                cameraId = record.CameraId,
                errorStage = record.ErrorStage,
                errorCode = record.ErrorCode,
                errorMessage = record.ErrorMessage,
                imagePath = resolution.ImagePath,
                renderedImagePath = resolution.RenderedImagePath,
                imageUrl = imageUrl,
                renderedImageUrl = renderedImageUrl,
                thumbnailUrl = renderedImageUrl ?? imageUrl,
                displayImageUrl = renderedImageUrl ?? imageUrl,
                hasImage = hasImage,
                hasRenderedImage = hasRenderedImage,
                missingRenderedImage = !hasRenderedImage,
                usedDerivedRenderedPath = resolution.UsedDerivedRenderedPath,
                usedFallbackImagePath = resolution.UsedFallbackImagePath
            };
        }

        private static IEnumerable<string> GetCachedTraceImageFiles(
            IDictionary<string, IReadOnlyList<string>>? imageFileCache,
            string directory)
        {
            if (imageFileCache == null)
            {
                return EnumerateTraceImageFiles(directory);
            }

            if (imageFileCache.TryGetValue(directory, out IReadOnlyList<string>? cachedFiles))
            {
                return cachedFiles;
            }

            IReadOnlyList<string> files = EnumerateTraceImageFiles(directory);
            imageFileCache[directory] = files;
            return files;
        }

        private static string[] EnumerateTraceImageFiles(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(directory)
                    .Where(path =>
                    {
                        string extension = Path.GetExtension(path);
                        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private string? TryCreateImageUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(ImageBasePath) || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                string basePath = Path.GetFullPath(ImageBasePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullPath = ResolveImageFullPath(path, basePath);
                if (!File.Exists(fullPath))
                {
                    return null;
                }

                string relativePath = Path.GetRelativePath(basePath, fullPath);

                if (relativePath.Equals("..", StringComparison.Ordinal) ||
                    relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
                    Path.IsPathRooted(relativePath))
                {
                    return null;
                }

                string[] segments = relativePath
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                string encodedPath = string.Join("/", segments.Select(Uri.EscapeDataString));
                return $"http://{ImageHostName}/{encodedPath}";
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveImageFullPath(string path, string basePath)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            string relativeToImageBase = Path.GetFullPath(Path.Combine(basePath, path));
            if (File.Exists(relativeToImageBase))
            {
                return relativeToImageBase;
            }

            string? parent = Directory.GetParent(basePath)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                string relativeToStorageRoot = Path.GetFullPath(Path.Combine(parent, path));
                if (File.Exists(relativeToStorageRoot))
                {
                    return relativeToStorageRoot;
                }
            }

            return relativeToImageBase;
        }

        private static string? TryGetStringProperty(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement propertyElement)
                ? propertyElement.GetString()
                : null;
        }

        private static int? TryGetInt32Property(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement propertyElement))
            {
                return null;
            }

            if (propertyElement.ValueKind == JsonValueKind.Number && propertyElement.TryGetInt32(out int value))
            {
                return value;
            }

            if (propertyElement.ValueKind == JsonValueKind.String &&
                int.TryParse(propertyElement.GetString(), out int parsed))
            {
                return parsed;
            }

            return null;
        }

        private static long? TryGetInt64Property(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement propertyElement))
            {
                return null;
            }

            if (propertyElement.ValueKind == JsonValueKind.Number && propertyElement.TryGetInt64(out long value))
            {
                return value;
            }

            if (propertyElement.ValueKind == JsonValueKind.String &&
                long.TryParse(propertyElement.GetString(), out long parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool TryParseTraceDate(string value, out DateTime date)
        {
            if (DateTime.TryParse(value, out date))
            {
                date = date.Date;
                return true;
            }

            return DateTime.TryParseExact(
                value,
                "yyyy年MM月dd日",
                null,
                System.Globalization.DateTimeStyles.None,
                out date);
        }

        private static bool TryParseTraceHour(string value, out int hour)
        {
            if (int.TryParse(value, out hour) && hour >= 0 && hour <= 23)
            {
                return true;
            }

            hour = 0;
            return false;
        }

        /// <summary>
        /// Base path for detection logs (e.g., StoragePath\Logs)
        /// </summary>
        public string LogBasePath { get; set; } = "";

        /// <summary>
        /// Reads and parses the last N detection log entries and sends to frontend.
        /// </summary>
        public async Task SendDetectionLogs(int maxCount = 100)
        {
            if (string.IsNullOrEmpty(LogBasePath) || !IsWebViewControlUsable(_webView)) return;
            try
            {
                string logsDir = Path.Combine(LogBasePath, "DetectionLogs");
                if (!Directory.Exists(logsDir))
                {
                    PostMessage("detectionLogTable", Array.Empty<object>());
                    return;
                }

                // Get all date folders, newest first
                var dateFolders = Directory.GetDirectories(logsDir)
                    .OrderByDescending(d => d)
                    .ToList();

                var logEntries = new List<object>();
                int collected = 0;

                foreach (var dateFolder in dateFolders)
                {
                    if (collected >= maxCount) break;

                    // Get all log files in this date folder, newest first
                    var logFiles = Directory.GetFiles(dateFolder, "*.txt")
                        .OrderByDescending(f => f)
                        .ToList();

                    foreach (var logFile in logFiles)
                    {
                        if (collected >= maxCount) break;

                        try
                        {
                            // Read all lines and split into entries
                            string content = File.ReadAllText(logFile, Encoding.UTF8);
                            // Each entry is separated by double newline
                            var entries = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

                            // Process in reverse order (newest first)
                            for (int i = entries.Length - 1; i >= 0 && collected < maxCount; i--)
                            {
                                var entry = entries[i].Trim();
                                if (string.IsNullOrEmpty(entry)) continue;

                                // Parse entry: "检测时间: {time}\r\n结果: {result}\r\n{details}"
                                var lines = entry.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                                string time = "";
                                string result = "";
                                string details = "";

                                foreach (var line in lines)
                                {
                                    if (line.StartsWith("检测时间:"))
                                        time = line.Substring("检测时间:".Length).Trim();
                                    else if (line.StartsWith("结果:"))
                                        result = line.Substring("结果:".Length).Trim();
                                    else if (!string.IsNullOrWhiteSpace(line))
                                        details += (details.Length > 0 ? "; " : "") + line.Trim();
                                }

                                if (!string.IsNullOrEmpty(time))
                                {
                                    logEntries.Add(new
                                    {
                                        time = time,
                                        result = result,
                                        details = details
                                    });
                                    collected++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WebUIController] Log entry parse error: {ex.Message}");
                        }
                    }
                }

                PostMessage("detectionLogTable", logEntries);
            }
            catch (Exception ex)
            {
                await LogToFrontend($"读取检测日志失败: {ex.Message}", "error");
                PostMessage("detectionLogTable", Array.Empty<object>());
            }
        }

        /// <summary>
        /// Sends statistics history to frontend
        /// </summary>
        public async Task SendStatisticsHistory(StatisticsHistory history, DetectionStatistics current)
        {
            if (!IsWebViewControlUsable(_webView)) return;

            try
            {
                var records = history.GetOrderedRecords();

                // Add current day as first item
                var allRecords = new List<object>();
                allRecords.Add(new
                {
                    date = current.CurrentDate,
                    total = current.TotalCount,
                    ok = current.QualifiedCount,
                    ng = current.UnqualifiedCount,
                    rate = current.QualifiedPercentage
                });

                foreach (var r in records)
                {
                    allRecords.Add(new
                    {
                        date = r.Date,
                        total = r.TotalCount,
                        ok = r.QualifiedCount,
                        ng = r.UnqualifiedCount,
                        rate = r.QualifiedPercentage
                    });
                }

                PostMessage("statisticsHistory", allRecords);
            }
            catch (Exception ex)
            {
                await LogToFrontend($"获取历史统计失败: {ex.Message}", "error");
            }
        }

        // ================== 多相机方法 ==================

        /// <summary>
        /// 发送相机列表到前端
        /// </summary>
        public Task SendCameraList(IEnumerable<object> cameras, string activeCameraId)
        {
            PostMessage("cameraList", new { cameras = cameras, activeId = activeCameraId });
            return Task.CompletedTask;
        }

        /// <summary>
        /// 发送超级搜索结果到前端（所有局域网相机）
        /// </summary>
        public Task SendDiscoveredCameras(IEnumerable<object> cameras)
        {
            PostMessage("discoveredCameras", new { cameras = cameras });
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                WebView2? webView = _webView;
                var webMessageReceivedHandler = _webMessageReceivedHandler;
                var navigationCompletedHandler = _navigationCompletedHandler;

                void Unsubscribe()
                {
                    if (!IsWebViewReadyOnUiThread(webView))
                    {
                        return;
                    }

                    if (webMessageReceivedHandler != null)
                    {
                        webView!.CoreWebView2!.WebMessageReceived -= webMessageReceivedHandler;
                    }

                    if (navigationCompletedHandler != null)
                    {
                        webView!.CoreWebView2!.NavigationCompleted -= navigationCompletedHandler;
                    }
                }

                if (IsWebViewControlUsable(webView))
                {
                    if (webView!.InvokeRequired)
                    {
                        webView.BeginInvoke(new Action(Unsubscribe));
                    }
                    else
                    {
                        Unsubscribe();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebUIController] Dispose unsubscribe failed: {ex.Message}");
            }
            finally
            {
                OnFindCamera = null;
                OnStartSystem = null;
                OnStopSystem = null;
                OnOpenCamera = null;
                OnManualDetect = null;
                OnCaptureCameraPreview = null;
                OnManualRelease = null;
                OnOpenSettings = null;
                OnGetModelList = null;
                OnChangeModel = null;
                OnThresholdChanged = null;
                OnAppReady = null;
                OnTestYolo = null;
                OnExitApp = null;
                OnMinimizeApp = null;
                OnToggleMaximize = null;
                OnStartDrag = null;
                OnConnectPlc = null;
                OnRequestHealthSnapshot = null;
                OnUpdateROI = null;
                OnSetConfidence = null;
                OnSetIou = null;
                OnSetTaskType = null;
                OnSaveSettings = null;
                OnSaveProjectPreset = null;
                OnDeleteProjectPreset = null;
                OnGetProjectPresets = null;
                OnExportConfigMigration = null;
                OnImportConfigMigration = null;
                OnSelectStorageFolder = null;
                OnGetStatisticsHistory = null;
                OnClearStatisticsHistory = null;
                OnResetStatistics = null;
                OnCollectDataset = null;
                OnRunHistoryRulePreview = null;
                OnGetCameraList = null;
                OnSwitchCamera = null;
                OnAddCamera = null;
                OnDeleteCamera = null;
                OnSuperSearchCameras = null;
                OnSuperSearchCamerasHik = null;
                OnDirectConnectCamera = null;
                OnSetAuxiliary1Model = null;
                OnSetAuxiliary2Model = null;
                OnToggleMultiModelFallback = null;
                OnSerialAutoDetectPorts = null;
                OnSerialTestTrigger = null;
                OnSerialSimulateTrigger = null;
                _webView = null;
            }
        }
    }
}
