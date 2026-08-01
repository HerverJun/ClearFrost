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
using System.Globalization;
using System.Threading;
using OpenCvSharp;
using ClearFrost.Core.Inspection;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    public sealed class WebUiCommandEventArgs : EventArgs
    {
        public WebUiCommandEventArgs(string requestId, string payloadJson, string command = "")
        {
            RequestId = requestId ?? string.Empty;
            Command = command ?? string.Empty;
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) || string.Equals(payloadJson, "null", StringComparison.OrdinalIgnoreCase)
                ? "{}"
                : payloadJson;
        }

        public string RequestId { get; }

        public string Command { get; }

        public string PayloadJson { get; }
    }

    public sealed class StatisticsHistoryRequestEventArgs : EventArgs
    {
        public StatisticsHistoryRequestEventArgs(int days)
        {
            Days = days;
        }

        public int Days { get; }
    }

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
        private const int DefaultStatisticsHistoryDays = 30;
        private const int MaxStatisticsHistoryDays = 366;
        private long _lastImagePushTick;
        private int _imagePushInProgress;
        private int _previewFrameToggle;
        private long _previewFrameId;
        private string _webPreviewCachePath = string.Empty;
        private const int ImagePushMinIntervalMs = 50;
        private static readonly HashSet<string> CommandsRequiringValue = new(StringComparer.Ordinal)
        {
            "save_project_preset",
            "delete_project_preset",
            "change_model",
            "update_roi",
            "set_confidence",
            "set_iou",
            "set_task_type",
            "save_settings",
            "set_roi_threshold",
            "set_roi_threshold_final",
            "get_ng_hours",
            "get_ng_images",
            "run_history_rule_preview",
            "manual_release",
            "capture_camera_preview",
            "verify_diagnostic_package",
            "maintenance_advice_action",
            "shift_task_action",
            "vision_debug_query_recent",
            "vision_debug_run_current",
            "vision_debug_run_history",
            "vision_debug_run_batch",
            "vision_debug_save_params",
            "vision_debug_apply_template",
            "switch_camera",
            "add_camera",
            "delete_camera",
            "direct_connect_camera",
            "set_auxiliary1_model",
            "set_auxiliary2_model",
            "toggle_multi_model",
            "query_manual_review_records",
            "save_manual_review",
            "create_replay_dataset",
            "run_replay_comparison",
            "approve_replay_candidate",
            "preview_replay_dataset",
            "query_replay_datasets",
            "archive_replay_dataset",
            "cancel_replay_run",
            "query_replay_runs",
            "query_replay_report",
            "query_model_approval_evidence",
            "run_replay_integrity_scan",
        };
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
        public event EventHandler<string>? OnManualRelease;
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
        public event EventHandler<WebUiCommandEventArgs>? OnExportDiagnosticPackage;
        public event EventHandler<WebUiCommandEventArgs>? OnQueryDiagnosticPackages;
        public event EventHandler<WebUiCommandEventArgs>? OnVerifyDiagnosticPackage;
        public event EventHandler<WebUiCommandEventArgs>? OnMaintenanceAdviceAction;
        public event EventHandler<WebUiCommandEventArgs>? OnShiftTaskAction;
        public event EventHandler<WebUiCommandEventArgs>? OnExportFieldHandoffReport;
        public event EventHandler<WebUiCommandEventArgs>? OnQueryFieldHandoffReports;
        public event EventHandler<WebUiCommandEventArgs>? OnFieldDebugCommand;
        public event EventHandler<WebUiCommandEventArgs>? OnVisionDebugCommand;
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
        public event EventHandler<StatisticsHistoryRequestEventArgs>? OnGetStatisticsHistory;
        public event EventHandler? OnClearStatisticsHistory;
        public event EventHandler? OnResetStatistics;
        public event EventHandler? OnCollectDataset;
        public event EventHandler<string>? OnRunHistoryRulePreview;
        public event EventHandler<WebUiCommandEventArgs>? OnQueryManualReviewRecords;
        public event EventHandler<WebUiCommandEventArgs>? OnSaveManualReview;
        public event EventHandler<WebUiCommandEventArgs>? OnCreateReplayDataset;
        public event EventHandler<WebUiCommandEventArgs>? OnRunReplayComparison;
        public event EventHandler<WebUiCommandEventArgs>? OnApproveReplayCandidate;
        public event EventHandler<WebUiCommandEventArgs>? OnPreviewReplayDataset;
        public event EventHandler<WebUiCommandEventArgs>? OnQueryReplayDatasets;
        public event EventHandler<WebUiCommandEventArgs>? OnArchiveReplayDataset;
        public event EventHandler<WebUiCommandEventArgs>? OnCancelReplayRun;
        public event EventHandler<WebUiCommandEventArgs>? OnQueryReplayRuns;
        public event EventHandler<WebUiCommandEventArgs>? OnQueryReplayReport;
        public event EventHandler<WebUiCommandEventArgs>? OnQueryModelApprovalEvidence;
        public event EventHandler<WebUiCommandEventArgs>? OnRunReplayIntegrityScan;

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
        internal OperationAuditService? AuditService { get; set; }
        internal Func<CancellationToken, Task<OperationAuditChainVerificationResult>>? AuditChainVerifier { get; set; }

        /// <summary>
        /// Maps the image folder to a virtual host for direct access.
        /// </summary>
        public void SetImageMapping(string localPath)
        {
            WebView2? webView = _webView;
            if (!IsWebViewControlUsable(webView) || !IsSafeImageMappingDirectory(localPath))
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
        public Task UpdateImage(
            string base64Image,
            int sourceWidth = 0,
            int sourceHeight = 0,
            int previewWidth = 0,
            int previewHeight = 0)
        {
            if (!IsWebViewControlUsable(_webView)) return Task.CompletedTask;
            PostMessage("previewFrame", new
            {
                base64 = base64Image,
                frameId = Interlocked.Increment(ref _previewFrameId),
                sourceWidth = Math.Max(0, sourceWidth),
                sourceHeight = Math.Max(0, sourceHeight),
                previewWidth = Math.Max(0, previewWidth),
                previewHeight = Math.Max(0, previewHeight)
            });
            return Task.CompletedTask;
        }

        public Task UpdateImageUrl(
            string url,
            int sourceWidth = 0,
            int sourceHeight = 0,
            int previewWidth = 0,
            int previewHeight = 0)
        {
            if (!IsWebViewControlUsable(_webView)) return Task.CompletedTask;

            PostMessage("previewFrame", new
            {
                url = url,
                frameId = Interlocked.Increment(ref _previewFrameId),
                sourceWidth = Math.Max(0, sourceWidth),
                sourceHeight = Math.Max(0, sourceHeight),
                previewWidth = Math.Max(0, previewWidth),
                previewHeight = Math.Max(0, previewHeight)
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
                    await UpdateImageFileAsync(encoded, image.Width, image.Height, resized.Width, resized.Height);
                }
                else
                {
                    string base64 = Convert.ToBase64String(encoded);
                    await UpdateImage(base64, image.Width, image.Height, resized.Width, resized.Height);
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

        private async Task UpdateImageFileAsync(
            byte[] encoded,
            int sourceWidth = 0,
            int sourceHeight = 0,
            int previewWidth = 0,
            int previewHeight = 0)
        {
            if (string.IsNullOrWhiteSpace(_webPreviewCachePath))
            {
                string base64 = Convert.ToBase64String(encoded);
                await UpdateImage(base64, sourceWidth, sourceHeight, previewWidth, previewHeight);
                return;
            }

            int frameIndex = Interlocked.Increment(ref _previewFrameToggle);
            string fileName = (frameIndex & 1) == 0 ? "frame_a.jpg" : "frame_b.jpg";
            string filePath = Path.Combine(_webPreviewCachePath, fileName);

            await File.WriteAllBytesAsync(filePath, encoded);

            string imageUrl = $"https://{PreviewHostName}/{fileName}?t={Environment.TickCount64}";
            await UpdateImageUrl(imageUrl, sourceWidth, sourceHeight, previewWidth, previewHeight);
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
        public Task SendModelList(object models)
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
            string? requestId = null;
            string cmd = string.Empty;

            try
            {
                // Use WebMessageAsJson as TryGetWebMessageAsString might be missing/obsolete
                string json = e.WebMessageAsJson;
                if (string.IsNullOrEmpty(json)) return;

                // Parse the JSON
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    requestId = root.TryGetProperty("requestId", out JsonElement requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    if (root.TryGetProperty("cmd", out JsonElement cmdElement))
                    {
                        cmd = cmdElement.GetString() ?? string.Empty;
                        if (CommandRequiresValue(cmd) && IsMissingCommandValue(root))
                        {
                            await SendCommandErrorAsync(
                                cmd,
                                requestId,
                                "MissingValue",
                                $"前端命令缺少 value 字段: {cmd}");
                            return;
                        }

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
                                if (TryReadObjectCommandValue(root, out string previewPayload))
                                {
                                    OnCaptureCameraPreview?.Invoke(this, previewPayload);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是对象: capture_camera_preview");
                                }
                                break;
                            case "manual_release":
                                if (TryReadObjectCommandValue(root, out string releasePayload))
                                {
                                    OnManualRelease?.Invoke(this, releasePayload);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是对象: manual_release");
                                }
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
                                if (TryReadObjectCommandValue(root, out string presetSaveJson))
                                {
                                    OnSaveProjectPreset?.Invoke(this, presetSaveJson);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是对象: save_project_preset");
                                }
                                break;
                            case "delete_project_preset":
                                if (root.TryGetProperty("value", out JsonElement presetDeleteElement))
                                {
                                    OnDeleteProjectPreset?.Invoke(this, presetDeleteElement.GetString() ?? string.Empty);
                                }
                                break;
                            case "change_model":
                                if (TryReadNonEmptyStringCommandValue(root, out string modelName))
                                {
                                    OnChangeModel?.Invoke(this, modelName);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 不能为空: change_model");
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
                                if (TryReadRoiRect(root, out float[] rectArray, out string roiError))
                                {
                                    OnUpdateROI?.Invoke(this, rectArray);
#if DEBUG
                                    await LogToFrontend($"ROI已更新: [{string.Join(", ", rectArray.Select(v => v.ToString("F3")))}]");
#endif
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, roiError);
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
                            case "export_diagnostic_package":
                                OnExportDiagnosticPackage?.Invoke(this, CreateCommandEventArgs(root, requestId));
                                break;
                            case "query_diagnostic_packages":
                                OnQueryDiagnosticPackages?.Invoke(this, CreateCommandEventArgs(root, requestId));
                                break;
                            case "verify_diagnostic_package":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnVerifyDiagnosticPackage?.Invoke(this, args));
                                break;
                            case "maintenance_advice_action":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnMaintenanceAdviceAction?.Invoke(this, args));
                                break;
                            case "shift_task_action":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnShiftTaskAction?.Invoke(this, args));
                                break;
                            case "export_field_handoff_report":
                                OnExportFieldHandoffReport?.Invoke(this, CreateCommandEventArgs(root, requestId));
                                break;
                            case "query_field_handoff_reports":
                                OnQueryFieldHandoffReports?.Invoke(this, CreateCommandEventArgs(root, requestId));
                                break;
                            case "field_debug_step_capture":
                            case "field_debug_step_infer":
                            case "field_debug_plc_write_test":
                            case "field_debug_barcode_read_test":
                            case "field_debug_simulate_trigger":
                                OnFieldDebugCommand?.Invoke(this, CreateCommandEventArgs(root, requestId));
                                break;
                            case "vision_debug_query_recent":
                            case "vision_debug_run_current":
                            case "vision_debug_run_history":
                            case "vision_debug_run_batch":
                            case "vision_debug_save_params":
                            case "vision_debug_apply_template":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnVisionDebugCommand?.Invoke(this, args));
                                break;
                            case "set_confidence":
                                if (TryReadUnitFloatCommandValue(root, out float conf))
                                {
                                    OnSetConfidence?.Invoke(this, conf);
#if DEBUG
                                    await LogToFrontend($"置信度已设置: {conf:F2}");
#endif
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是 0 到 1 的数值: set_confidence");
                                }
                                break;
                            case "set_iou":
                                if (TryReadUnitFloatCommandValue(root, out float iou))
                                {
                                    OnSetIou?.Invoke(this, iou);
#if DEBUG
                                    await LogToFrontend($"IOU阈值已设置: {iou:F2}");
#endif
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是 0 到 1 的数值: set_iou");
                                }
                                break;
                            case "set_task_type":
                                if (TryReadInt32CommandValue(root, out int taskType) && IsSupportedTaskType(taskType))
                                {
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
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 不是受支持的任务类型: set_task_type");
                                }
                                break;
                            case "save_settings":
                                if (TryReadObjectCommandValue(root, out string settingsJson))
                                {
                                    OnSaveSettings?.Invoke(this, settingsJson);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是对象: save_settings");
                                }
                                break;
                            case "set_roi_threshold":
                            case "set_roi_threshold_final":
                                if (TryReadInt32CommandValue(root, out int threshold))
                                {
                                    OnThresholdChanged?.Invoke(this, threshold);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, $"前端命令 value 必须是整数: {cmd}");
                                }
                                break;
                            case "get_ng_dates":
                                await SendNGDates();
                                break;
                            case "get_ng_hours":
                                if (TryReadNonEmptyStringCommandValue(root, out string traceDateKey))
                                {
                                    if (TryParseTraceDate(traceDateKey, out _))
                                    {
                                        await SendNGHours(traceDateKey);
                                    }
                                    else
                                    {
                                        await SendInvalidValueAsync(cmd, requestId, "追溯日期格式无效: get_ng_hours");
                                    }
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 不能为空: get_ng_hours");
                                }
                                break;
                            case "get_ng_images":
                                if (TryReadTraceImagesRequest(
                                        root,
                                        out string date,
                                        out string hour,
                                        out int pageSize,
                                        out string? afterTimestamp,
                                        out long? afterId,
                                        out string traceError))
                                {
                                    await SendNGImages(date, hour, pageSize, afterTimestamp, afterId, requestId);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, traceError);
                                }
                                break;
                            case "run_history_rule_preview":
                                if (TryReadObjectCommandValue(root, out string historyRuleJson))
                                {
                                    OnRunHistoryRulePreview?.Invoke(this, historyRuleJson);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是对象: run_history_rule_preview");
                                }
                                break;
                            case "select_storage_folder":
                                OnSelectStorageFolder?.Invoke(this, EventArgs.Empty);
                                break;
                            case "get_detection_logs":
                                await SendDetectionLogs();
                                break;
                            case "query_audit_records":
                                {
                                    JsonElement auditQueryElement = root.TryGetProperty("value", out JsonElement auditQueryValueElement)
                                        ? auditQueryValueElement
                                        : default;
                                    await SendAuditRecordsAsync(auditQueryElement, requestId);
                                }
                                break;
                            case "export_audit_records":
                                {
                                    JsonElement auditExportElement = root.TryGetProperty("value", out JsonElement auditExportValueElement)
                                        ? auditExportValueElement
                                        : default;
                                    await ExportAuditRecordsAsync(auditExportElement, requestId);
                                }
                                break;
                            case "verify_audit_chain":
                                await SendAuditChainVerificationAsync(requestId);
                                break;
                            case "get_statistics_history":
                                int statisticsHistoryDays = TryReadInt32CommandValue(root, out int requestedStatisticsHistoryDays)
                                    ? NormalizeStatisticsHistoryDays(requestedStatisticsHistoryDays)
                                    : DefaultStatisticsHistoryDays;
                                OnGetStatisticsHistory?.Invoke(this, new StatisticsHistoryRequestEventArgs(statisticsHistoryDays));
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
                            case "query_manual_review_records":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryManualReviewRecords?.Invoke(this, args));
                                break;
                            case "save_manual_review":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnSaveManualReview?.Invoke(this, args));
                                break;
                            case "create_replay_dataset":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnCreateReplayDataset?.Invoke(this, args));
                                break;
                            case "run_replay_comparison":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnRunReplayComparison?.Invoke(this, args));
                                break;
                            case "approve_replay_candidate":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnApproveReplayCandidate?.Invoke(this, args));
                                break;
                            case "preview_replay_dataset":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnPreviewReplayDataset?.Invoke(this, args));
                                break;
                            case "query_replay_datasets":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryReplayDatasets?.Invoke(this, args));
                                break;
                            case "archive_replay_dataset":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnArchiveReplayDataset?.Invoke(this, args));
                                break;
                            case "cancel_replay_run":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnCancelReplayRun?.Invoke(this, args));
                                break;
                            case "query_replay_runs":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryReplayRuns?.Invoke(this, args));
                                break;
                            case "query_replay_report":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryReplayReport?.Invoke(this, args));
                                break;
                            case "query_model_approval_evidence":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryModelApprovalEvidence?.Invoke(this, args));
                                break;
                            case "run_replay_integrity_scan":
                                await DispatchObjectCommandAsync(cmd, requestId, root, args => OnRunReplayIntegrityScan?.Invoke(this, args));
                                break;

                            // ================== 多相机命令 ==================
                            case "get_camera_list":
                                OnGetCameraList?.Invoke(this, EventArgs.Empty);
                                break;
                            case "switch_camera":
                                if (TryReadNonEmptyStringCommandValue(root, out string camId))
                                {
                                    OnSwitchCamera?.Invoke(this, camId);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 不能为空: switch_camera");
                                }
                                break;
                            case "add_camera":
                                if (TryReadObjectCommandValue(root, out string addCameraJson))
                                {
                                    OnAddCamera?.Invoke(this, addCameraJson);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是对象: add_camera");
                                }
                                break;
                            case "delete_camera":
                                if (TryReadNonEmptyStringCommandValue(root, out string camIdToDelete))
                                {
                                    OnDeleteCamera?.Invoke(this, camIdToDelete);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 不能为空: delete_camera");
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
                                if (TryReadObjectCommandValue(root, out string directConnectJson))
                                {
                                    OnDirectConnectCamera?.Invoke(this, directConnectJson);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是对象: direct_connect_camera");
                                }
                                break;

                            // ================== 多模型切换命令 ==================
                            case "set_auxiliary1_model":
                                if (TryReadStringCommandValue(root, out string aux1ModelName))
                                {
                                    OnSetAuxiliary1Model?.Invoke(this, aux1ModelName);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是字符串: set_auxiliary1_model");
                                }
                                break;
                            case "set_auxiliary2_model":
                                if (TryReadStringCommandValue(root, out string aux2ModelName))
                                {
                                    OnSetAuxiliary2Model?.Invoke(this, aux2ModelName);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是字符串: set_auxiliary2_model");
                                }
                                break;
                            case "toggle_multi_model":
                                if (TryReadBoolCommandValue(root, out bool enableMultiModel))
                                {
                                    OnToggleMultiModelFallback?.Invoke(this, enableMultiModel);
                                }
                                else
                                {
                                    await SendInvalidValueAsync(cmd, requestId, "前端命令 value 必须是布尔值: toggle_multi_model");
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
                                await SendCommandErrorAsync(
                                    cmd,
                                    requestId,
                                    "UnknownCommand",
                                    $"未知前端命令: {cmd}");
                                break;
                        }
                    }
                    else
                    {
                        await SendCommandErrorAsync(
                            string.Empty,
                            requestId,
                            "MissingCommand",
                            "前端消息缺少 cmd 字段");
                    }
                }
            }
            catch (Exception ex)
            {
                // Optionally log error to debugger or frontend
                System.Diagnostics.Debug.WriteLine($"Error processing web message: {ex.Message}");
                try
                {
                    await SendCommandErrorAsync(
                        cmd,
                        requestId,
                        "CommandException",
                        $"前端命令处理异常: {(string.IsNullOrWhiteSpace(cmd) ? "<empty>" : cmd)} - {ex.Message}");
                }
                catch (Exception notifyEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reporting web message failure: {notifyEx.Message}");
                }
            }
        }

        private static bool CommandRequiresValue(string cmd)
        {
            return CommandsRequiringValue.Contains(cmd);
        }

        private static bool IsMissingCommandValue(JsonElement root)
        {
            return !root.TryGetProperty("value", out JsonElement value) ||
                   value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
        }

        private static bool TryReadNonEmptyStringCommandValue(JsonElement root, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            value = valueElement.ValueKind == JsonValueKind.String
                ? valueElement.GetString() ?? string.Empty
                : valueElement.GetRawText();
            value = value.Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryReadStringCommandValue(JsonElement root, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = valueElement.GetString() ?? string.Empty;
            return true;
        }

        private static bool TryReadObjectCommandValue(JsonElement root, out string valueJson)
        {
            valueJson = "{}";
            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            valueJson = valueElement.GetRawText();
            return true;
        }

        private static bool TryReadInt32CommandValue(JsonElement root, out int value)
        {
            value = 0;
            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            if (valueElement.TryGetInt32(out value))
            {
                return true;
            }

            return valueElement.ValueKind == JsonValueKind.String &&
                   int.TryParse(valueElement.GetString(), out value);
        }

        private static int NormalizeStatisticsHistoryDays(int days)
        {
            return Math.Clamp(days, 1, MaxStatisticsHistoryDays);
        }

        private static bool TryReadUnitFloatCommandValue(JsonElement root, out float value)
        {
            value = 0f;
            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            bool parsed = valueElement.TryGetSingle(out value) ||
                          (valueElement.ValueKind == JsonValueKind.String &&
                           float.TryParse(valueElement.GetString(), out value));
            return parsed && value is >= 0f and <= 1f;
        }

        private static bool TryReadBoolCommandValue(JsonElement root, out bool value)
        {
            value = false;
            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            if (valueElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = valueElement.GetBoolean();
                return true;
            }

            return valueElement.ValueKind == JsonValueKind.String &&
                   bool.TryParse(valueElement.GetString(), out value);
        }

        private static bool IsSupportedTaskType(int taskType)
        {
            return taskType is 0 or 1 or 2 or 3 or 5 or 6;
        }

        private static bool TryReadRoiRect(JsonElement root, out float[] rect, out string error)
        {
            rect = Array.Empty<float>();
            error = string.Empty;

            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind != JsonValueKind.Object ||
                !valueElement.TryGetProperty("rect", out JsonElement rectElement))
            {
                error = "前端命令缺少 ROI rect 字段";
                return false;
            }

            try
            {
                float[]? parsed = JsonSerializer.Deserialize<float[]>(rectElement.GetRawText());
                if (parsed == null || parsed.Length != 4)
                {
                    error = "ROI rect 必须包含 4 个数值";
                    return false;
                }

                if (parsed.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                {
                    error = "ROI rect 必须是有限数值";
                    return false;
                }

                if (parsed.Any(value => value < 0f || value > 1f))
                {
                    error = "ROI rect 数值必须在 0 到 1 之间";
                    return false;
                }

                float x = parsed[0];
                float y = parsed[1];
                float width = parsed[2];
                float height = parsed[3];
                bool isClearRoi = x == 0f && y == 0f && width == 0f && height == 0f;
                if (!isClearRoi && (width <= 0.001f || height <= 0.001f))
                {
                    error = "ROI rect 宽高必须大于 0，清除 ROI 请使用 [0,0,0,0]";
                    return false;
                }

                const float BoundaryTolerance = 0.0005f;
                if (!isClearRoi &&
                    (x + width > 1f + BoundaryTolerance || y + height > 1f + BoundaryTolerance))
                {
                    error = "ROI rect 不能超出图像边界";
                    return false;
                }

                rect = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = $"ROI rect 解析失败: {ex.Message}";
                return false;
            }
        }

        private static bool TryReadTraceImagesRequest(
            JsonElement root,
            out string date,
            out string hour,
            out int pageSize,
            out string? afterTimestamp,
            out long? afterId,
            out string error)
        {
            date = string.Empty;
            hour = string.Empty;
            pageSize = 100;
            afterTimestamp = null;
            afterId = null;
            error = string.Empty;

            if (!root.TryGetProperty("value", out JsonElement valueElement) ||
                valueElement.ValueKind != JsonValueKind.Object)
            {
                error = "追溯图片请求 value 必须是对象";
                return false;
            }

            date = (TryGetStringProperty(valueElement, "date") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(date))
            {
                error = "追溯图片请求缺少 date";
                return false;
            }

            hour = (TryGetStringProperty(valueElement, "hour") ?? string.Empty).Trim();
            if (!TryParseTraceDate(date, out _))
            {
                error = "追溯图片请求 date 格式无效";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(hour) && !TryParseTraceHour(hour, out _))
            {
                error = "追溯图片请求 hour 必须是 0 到 23";
                return false;
            }

            pageSize = Math.Clamp(TryGetInt32Property(valueElement, "pageSize") ?? 100, 1, 200);
            string? cursorTimestamp = TryGetStringProperty(valueElement, "afterTimestamp");
            long? cursorId = TryGetInt64Property(valueElement, "afterId");
            bool hasCursorTimestamp = !string.IsNullOrWhiteSpace(cursorTimestamp);
            bool hasCursorId = cursorId.HasValue;
            if (hasCursorTimestamp != hasCursorId)
            {
                error = "追溯图片分页游标必须同时包含 afterTimestamp 和 afterId";
                return false;
            }

            if (hasCursorTimestamp)
            {
                if (!TryNormalizeTraceCursorTimestamp(cursorTimestamp!, out string normalizedTimestamp))
                {
                    error = "追溯图片分页游标 afterTimestamp 格式无效";
                    return false;
                }

                if (cursorId.GetValueOrDefault() <= 0)
                {
                    error = "追溯图片分页游标 afterId 必须大于 0";
                    return false;
                }

                afterTimestamp = normalizedTimestamp;
                afterId = cursorId;
            }

            return true;
        }

        private Task SendInvalidValueAsync(string cmd, string? requestId, string message)
        {
            return SendCommandErrorAsync(cmd, requestId, "InvalidValue", message);
        }

        private async Task DispatchObjectCommandAsync(
            string cmd,
            string? requestId,
            JsonElement root,
            Action<WebUiCommandEventArgs> dispatch)
        {
            if (TryReadObjectCommandValue(root, out string payloadJson))
            {
                dispatch(new WebUiCommandEventArgs(requestId ?? string.Empty, payloadJson, cmd));
                return;
            }

            await SendInvalidValueAsync(cmd, requestId, $"前端命令 value 必须是对象: {cmd}");
        }

        private Task SendCommandErrorAsync(string cmd, string? requestId, string errorCode, string message)
        {
            string normalizedCmd = string.IsNullOrWhiteSpace(cmd) ? "<empty>" : cmd;
            string normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "CommandError" : errorCode;
            string normalizedMessage = string.IsNullOrWhiteSpace(message)
                ? $"前端命令处理失败: {normalizedCmd}"
                : message;

            Debug.WriteLine($"[WebUIController] {normalizedErrorCode}: {normalizedMessage}");
            PostMessage("commandError", new
            {
                cmd = normalizedCmd,
                errorCode = normalizedErrorCode,
                message = normalizedMessage
            }, requestId);
            return LogToFrontend(normalizedMessage, "error");
        }

        private static WebUiCommandEventArgs CreateCommandEventArgs(JsonElement root, string? requestId)
        {
            string payload = root.TryGetProperty("value", out JsonElement valueElement)
                ? valueElement.GetRawText()
                : "{}";
            string command = root.TryGetProperty("cmd", out JsonElement cmdElement)
                ? cmdElement.GetString() ?? string.Empty
                : string.Empty;
            return new WebUiCommandEventArgs(requestId ?? string.Empty, payload, command);
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

            object? inspectionPayload = inspection == null
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
                    ruleDetails);

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
                inspection = inspectionPayload
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

        public Task SendFieldDebugResult(object result, string? requestId = null)
        {
            PostMessage("fieldDebugResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendVisionDebugResult(object result, string? requestId = null)
        {
            PostMessage("visionDebugResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendDiagnosticPackageExportResult(object result, string? requestId = null)
        {
            PostMessage("diagnosticPackageExportResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendDiagnosticPackageHistoryResult(object result, string? requestId = null)
        {
            PostMessage("diagnosticPackageHistoryResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendDiagnosticPackageVerificationResult(object result, string? requestId = null)
        {
            PostMessage("diagnosticPackageVerificationResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendMaintenanceAdviceActionResult(object result, string? requestId = null)
        {
            PostMessage("maintenanceAdviceActionResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendShiftTaskActionResult(object result, string? requestId = null)
        {
            PostMessage("shiftTaskActionResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendFieldHandoffReportResult(object result, string? requestId = null)
        {
            PostMessage("fieldHandoffReportResult", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendFieldHandoffReportHistoryResult(object result, string? requestId = null)
        {
            PostMessage("fieldHandoffReportHistoryResult", result, requestId);
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
                terminalHandshakeAttempted = context.TerminalHandshakeAttempted,
                terminalHandshakeSucceeded = context.TerminalHandshakeSucceeded,
                terminalHandshakeErrorCode = context.TerminalHandshakeErrorCode,
                terminalHandshakeSignalName = context.TerminalHandshakeSignalName,
                terminalHandshakeAddress = context.TerminalHandshakeAddress,
                terminalHandshakeMessage = context.TerminalHandshakeMessage,
                cycleSucceeded = context.CycleSucceeded,
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
                string message = $"获取日期列表失败: {ex.Message}";
                await LogToFrontend(message, "error");
                PostMessage("historyDates", new
                {
                    dates = Array.Empty<string>(),
                    error = message
                });
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
            catch (Exception ex)
            {
                string message = $"获取时段列表失败: {ex.Message}";
                await LogToFrontend(message, "error");
                PostMessage("historyHours", new
                {
                    hours = Array.Empty<string>(),
                    error = message
                });
            }
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
            catch (Exception ex)
            {
                string message = $"获取追溯图片失败: {ex.Message}";
                await LogToFrontend(message, "error");
                PostMessage("historyImages", new
                {
                    records = Array.Empty<object>(),
                    images = Array.Empty<object>(),
                    hasMore = false,
                    pageSize = 0,
                    nextCursorTimestamp = (string?)null,
                    nextCursorId = (long?)null,
                    error = message
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
                    SafeImageFileExists,
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
                ruleSummary = record.RuleSummary,
                resultJson = record.ResultJson,
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
                if (!SafeDirectoryExists(directory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(directory)
                    .Where(path =>
                    {
                        string extension = Path.GetExtension(path);
                        return (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) &&
                            SafeImageFileExists(path);
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
                if (!SafeImageFileExists(fullPath))
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
            if (SafeImageFileExists(relativeToImageBase))
            {
                return relativeToImageBase;
            }

            string? parent = Directory.GetParent(basePath)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                string relativeToStorageRoot = Path.GetFullPath(Path.Combine(parent, path));
                if (SafeImageFileExists(relativeToStorageRoot))
                {
                    return relativeToStorageRoot;
                }
            }

            return relativeToImageBase;
        }

        internal static bool IsSafeImageMappingDirectory(string localPath)
        {
            return SafeDirectoryExists(localPath);
        }

        private static bool SafeImageFileExists(string path)
        {
            return SafeLocalFileExists(path);
        }

        private static bool SafeLocalFileExists(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }

                string fullPath = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) || DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                return file.Exists && !HasReparsePoint(file);
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeDirectoryExists(string directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory) &&
                    Directory.Exists(directory) &&
                    !DirectoryPathHasReparsePoint(directory);
            }
            catch
            {
                return false;
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
        {
            try
            {
                var current = new DirectoryInfo(Path.GetFullPath(directory));
                while (current != null)
                {
                    current.Refresh();
                    if (current.Exists && HasReparsePoint(current))
                    {
                        return true;
                    }

                    current = current.Parent;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string? TryGetStringProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out JsonElement propertyElement) ||
                propertyElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            return propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString()
                : propertyElement.GetRawText();
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

        private static DateTimeOffset? TryGetDateTimeOffsetProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement propertyElement))
            {
                return null;
            }

            string? raw = propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString()
                : propertyElement.GetRawText();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(raw, out DateTimeOffset value))
            {
                return value;
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

        private static bool TryNormalizeTraceCursorTimestamp(string value, out string timestamp)
        {
            timestamp = string.Empty;
            if (!DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out DateTime parsed))
            {
                return false;
            }

            timestamp = parsed.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            return true;
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
                IReadOnlyList<object> logEntries = ReadDetectionLogTableEntries(LogBasePath, maxCount);
                PostMessage("detectionLogTable", logEntries);
            }
            catch (Exception ex)
            {
                await LogToFrontend($"读取检测日志失败: {ex.Message}", "error");
                PostMessage("detectionLogTable", Array.Empty<object>());
            }
        }

        internal static IReadOnlyList<object> ReadDetectionLogTableEntries(string logBasePath, int maxCount = 100)
        {
            if (string.IsNullOrWhiteSpace(logBasePath) || maxCount <= 0)
            {
                return Array.Empty<object>();
            }

            string logsDir = Path.Combine(logBasePath, "DetectionLogs");
            if (!SafeDirectoryExists(logsDir))
            {
                return Array.Empty<object>();
            }

            var logEntries = new List<object>();
            int collected = 0;

            foreach (var dateFolder in EnumerateSafeDirectories(logsDir).OrderByDescending(d => d))
            {
                if (collected >= maxCount) break;

                foreach (var logFile in EnumerateSafeFiles(dateFolder, "*.txt").OrderByDescending(f => f))
                {
                    if (collected >= maxCount) break;

                    try
                    {
                        string content = File.ReadAllText(logFile, Encoding.UTF8);
                        var entries = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

                        for (int i = entries.Length - 1; i >= 0 && collected < maxCount; i--)
                        {
                            var entry = entries[i].Trim();
                            if (string.IsNullOrEmpty(entry)) continue;

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

            return logEntries;
        }

        private static IEnumerable<string> EnumerateSafeDirectories(string directory)
        {
            try
            {
                return Directory.EnumerateDirectories(directory)
                    .Where(SafeDirectoryExists)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> EnumerateSafeFiles(string directory, string searchPattern)
        {
            try
            {
                if (!SafeDirectoryExists(directory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(directory, searchPattern)
                    .Where(SafeLocalFileExists)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private async Task SendAuditRecordsAsync(JsonElement queryElement, string? requestId)
        {
            if (!IsWebViewControlUsable(_webView))
            {
                return;
            }

            OperationAuditQuery query = ParseAuditQuery(queryElement);
            if (AuditService == null)
            {
                PostMessage("auditRecords", new
                {
                    query = BuildAuditQueryEcho(queryElement, query),
                    records = Array.Empty<object>(),
                    error = "审计服务未初始化"
                }, requestId);
                return;
            }

            try
            {
                OperationAuditQueryResult result = await AuditService.QueryAsync(query).ConfigureAwait(false);
                PostMessage("auditRecords", new
                {
                    query = BuildAuditQueryEcho(queryElement, query),
                    records = result.Records.Select(record => new
                    {
                        timestamp = record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        correlationId = record.CorrelationId,
                        operation = record.Operation,
                        status = record.Status.ToString(),
                        operatorId = record.OperatorId,
                        role = record.Role.ToString(),
                        reason = record.Reason,
                        inspectionId = record.InspectionId,
                        details = record.Details,
                        failureBlocker = record.FailureBlocker,
                        previousRecordSha256 = record.PreviousRecordSha256,
                        recordSha256 = record.RecordSha256
                    }).ToArray(),
                    error = result.ErrorMessage
                }, requestId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebUIController] Audit records query failed: {ex.Message}");
                PostMessage("auditRecords", new
                {
                    query = BuildAuditQueryEcho(queryElement, query),
                    records = Array.Empty<object>(),
                    error = ex.Message
                }, requestId);
            }
        }

        private async Task SendAuditChainVerificationAsync(string? requestId)
        {
            if (!IsWebViewControlUsable(_webView))
            {
                return;
            }

            if (AuditChainVerifier == null && AuditService == null)
            {
                PostMessage("auditChainVerification", new
                {
                    status = "Unavailable",
                    checkedAt = DateTimeOffset.Now,
                    totalRecords = 0,
                    verifiedRecords = 0,
                    findingCount = 0,
                    lastRecordSha256 = "",
                    findings = Array.Empty<object>(),
                    error = "审计服务未初始化"
                }, requestId);
                return;
            }

            try
            {
                OperationAuditChainVerificationResult result = AuditChainVerifier != null
                    ? await AuditChainVerifier(CancellationToken.None).ConfigureAwait(false)
                    : await AuditService!.VerifyChainAsync().ConfigureAwait(false);
                PostMessage("auditChainVerification", new
                {
                    status = result.Status,
                    checkedAt = DateTimeOffset.Now,
                    totalRecords = result.TotalRecords,
                    verifiedRecords = result.VerifiedRecords,
                    findingCount = result.Findings.Count,
                    lastRecordSha256 = result.LastRecordSha256,
                    findings = result.Findings.Take(5).Select(finding => new
                    {
                        filePath = finding.FilePath,
                        auditFileName = string.IsNullOrWhiteSpace(finding.FilePath)
                            ? string.Empty
                            : Path.GetFileName(finding.FilePath),
                        lineNumber = finding.LineNumber,
                        severity = finding.Severity,
                        errorCode = finding.ErrorCode,
                        message = finding.Message,
                        expectedPreviousSha256 = finding.ExpectedPreviousSha256,
                        actualPreviousSha256 = finding.ActualPreviousSha256,
                        expectedRecordSha256 = finding.ExpectedRecordSha256,
                        actualRecordSha256 = finding.ActualRecordSha256
                    }).ToArray(),
                    error = string.Empty
                }, requestId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebUIController] Audit chain verification failed: {ex.Message}");
                PostMessage("auditChainVerification", new
                {
                    status = "Unavailable",
                    checkedAt = DateTimeOffset.Now,
                    totalRecords = 0,
                    verifiedRecords = 0,
                    findingCount = 1,
                    lastRecordSha256 = "",
                    findings = new[]
                    {
                        new
                        {
                            filePath = string.Empty,
                            auditFileName = string.Empty,
                            lineNumber = 0,
                            severity = "Blocking",
                            errorCode = "AuditChainVerificationFailed",
                            message = ex.Message,
                            expectedPreviousSha256 = string.Empty,
                            actualPreviousSha256 = string.Empty,
                            expectedRecordSha256 = string.Empty,
                            actualRecordSha256 = string.Empty
                        }
                    },
                    error = ex.Message
                }, requestId);
            }
        }

        private async Task ExportAuditRecordsAsync(JsonElement queryElement, string? requestId)
        {
            if (!IsWebViewControlUsable(_webView))
            {
                return;
            }

            if (AuditService == null)
            {
                PostMessage("auditExport", new { path = "", error = "审计服务未初始化" }, requestId);
                return;
            }

            try
            {
                string outputDirectory = string.IsNullOrWhiteSpace(LogBasePath)
                    ? Path.Combine(RuntimePaths.DataDirectory, "outbox")
                    : Path.Combine(LogBasePath, "Outbox");
                string outputPath = Path.Combine(outputDirectory, $"operation-audit-export-{DateTime.Now:yyyyMMddHHmmss}.csv");
                string path = await AuditService.ExportCsvAsync(ParseAuditQuery(queryElement), outputPath).ConfigureAwait(false);
                PostMessage("auditExport", new { path = path, error = "" }, requestId);
            }
            catch (Exception ex)
            {
                PostMessage("auditExport", new { path = "", error = ex.Message }, requestId);
            }
        }

        private static OperationAuditQuery ParseAuditQuery(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return new OperationAuditQuery();
            }

            OperationAuditStatus? status = null;
            string statusText = TryGetStringProperty(element, "status") ?? TryGetStringProperty(element, "Status") ?? string.Empty;
            if (Enum.TryParse(statusText, ignoreCase: true, out OperationAuditStatus parsedStatus))
            {
                status = parsedStatus;
            }

            return new OperationAuditQuery
            {
                StartTime = TryGetDateTimeOffsetProperty(element, "startTime") ?? TryGetDateTimeOffsetProperty(element, "StartTime"),
                EndTime = TryGetDateTimeOffsetProperty(element, "endTime") ?? TryGetDateTimeOffsetProperty(element, "EndTime"),
                Operation = TryGetStringProperty(element, "operation") ?? TryGetStringProperty(element, "Operation") ?? string.Empty,
                OperatorId = TryGetStringProperty(element, "operatorId") ?? TryGetStringProperty(element, "OperatorId") ?? string.Empty,
                Role = TryGetStringProperty(element, "role") ?? TryGetStringProperty(element, "Role") ?? string.Empty,
                Status = status,
                FailureReason = TryGetStringProperty(element, "failureReason") ?? TryGetStringProperty(element, "FailureReason") ?? string.Empty,
                Limit = TryGetInt32Property(element, "limit") ?? TryGetInt32Property(element, "Limit") ?? 200
            };
        }

        private static object BuildAuditQueryEcho(JsonElement element, OperationAuditQuery query)
        {
            return new
            {
                startTime = TryGetStringProperty(element, "startTime") ??
                            TryGetStringProperty(element, "StartTime") ??
                            query.StartTime?.ToString("yyyy-MM-ddTHH:mm") ??
                            string.Empty,
                endTime = TryGetStringProperty(element, "endTime") ??
                          TryGetStringProperty(element, "EndTime") ??
                          query.EndTime?.ToString("yyyy-MM-ddTHH:mm") ??
                          string.Empty,
                operation = query.Operation,
                operatorId = query.OperatorId,
                role = query.Role,
                status = query.Status?.ToString() ??
                         TryGetStringProperty(element, "status") ??
                         TryGetStringProperty(element, "Status") ??
                         string.Empty,
                failureReason = query.FailureReason,
                limit = query.Limit
            };
        }

        /// <summary>
        /// Sends statistics history to frontend
        /// </summary>
        public async Task SendStatisticsHistory(StatisticsHistory history, DetectionStatistics current, int days = DefaultStatisticsHistoryDays)
        {
            if (!IsWebViewControlUsable(_webView)) return;

            try
            {
                PostMessage("statisticsHistory", BuildStatisticsHistoryRows(history, current, days));
            }
            catch (Exception ex)
            {
                await LogToFrontend($"获取历史统计失败: {ex.Message}", "error");
            }
        }

        internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> BuildStatisticsHistoryRows(
            StatisticsHistory history,
            DetectionStatistics current,
            int days = DefaultStatisticsHistoryDays)
        {
            int requestedDays = NormalizeStatisticsHistoryDays(days);
            string currentDate = current.CurrentDate?.Trim() ?? string.Empty;
            var allRecords = new List<IReadOnlyDictionary<string, object?>>();

            allRecords.Add(CreateStatisticsHistoryRow(
                currentDate,
                current.TotalCount,
                current.QualifiedCount,
                current.UnqualifiedCount,
                current.QualifiedPercentage));

            foreach (var record in history.GetOrderedRecords())
            {
                string date = record.Date?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currentDate) &&
                    string.Equals(date, currentDate, StringComparison.Ordinal))
                {
                    continue;
                }

                allRecords.Add(CreateStatisticsHistoryRow(
                    date,
                    record.TotalCount,
                    record.QualifiedCount,
                    record.UnqualifiedCount,
                    record.QualifiedPercentage));
            }

            return allRecords.Take(requestedDays).ToList();
        }

        private static IReadOnlyDictionary<string, object?> CreateStatisticsHistoryRow(
            string date,
            int total,
            int ok,
            int ng,
            double rate)
        {
            return new Dictionary<string, object?>
            {
                ["date"] = date,
                ["total"] = total,
                ["ok"] = ok,
                ["ng"] = ng,
                ["rate"] = rate
            };
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
                OnExportDiagnosticPackage = null;
                OnQueryDiagnosticPackages = null;
                OnVerifyDiagnosticPackage = null;
                OnMaintenanceAdviceAction = null;
                OnShiftTaskAction = null;
                OnExportFieldHandoffReport = null;
                OnQueryFieldHandoffReports = null;
                OnFieldDebugCommand = null;
                OnVisionDebugCommand = null;
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
                OnQueryManualReviewRecords = null;
                OnSaveManualReview = null;
                OnCreateReplayDataset = null;
                OnRunReplayComparison = null;
                OnApproveReplayCandidate = null;
                OnPreviewReplayDataset = null;
                OnQueryReplayDatasets = null;
                OnArchiveReplayDataset = null;
                OnCancelReplayRun = null;
                OnQueryReplayRuns = null;
                OnQueryReplayReport = null;
                OnQueryModelApprovalEvidence = null;
                OnRunReplayIntegrityScan = null;
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
                AuditChainVerifier = null;
                _webView = null;
            }
        }
    }
}
