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
//   - OnOpenCamera, OnManualDetect, OnConnectPlc, ...  (操作事件)
//   - OnVisionModeChanged, OnPipelineUpdate, ...      (传统视觉事件)
//   - OnSaveSettings, OnVerifyPassword, ...           (配置事件)
//
// 前端通信:
//   - 发送: UpdateUI(), UpdateImage(), LogToFrontend(), SendVisionConfig(), ...
//   - 接收: 通过 { "cmd": "xxx", "value": ... } JSON 格式解析
//
// 作者: 蘅芜君
// 创建日期: 2024
// ============================================================================
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using ClearFrost.Vision;

namespace ClearFrost
{
    /// <summary>
    /// Manages the WebView2 control and communication between C# and the Web frontend.
    /// </summary>
    public class WebUIController
    {
        private WebView2? _webView;

        // Events to notify the main window about frontend actions
        public event EventHandler? OnFindCamera;
        public event EventHandler? OnOpenCamera;
        public event EventHandler? OnManualDetect;
        public event EventHandler? OnManualRelease;
        public event EventHandler? OnOpenSettings;
        public event EventHandler<string>? OnChangeModel;
        public event EventHandler<int>? OnThresholdChanged;
        public event EventHandler? OnAppReady;
        public event EventHandler? OnTestYolo;
        public event EventHandler? OnExitApp;
        public event EventHandler? OnMinimizeApp;
        public event EventHandler? OnToggleMaximize;
        public event EventHandler? OnStartDrag;
        public event EventHandler? OnConnectPlc;
        public event EventHandler<float[]>? OnUpdateROI;
        public event EventHandler<float>? OnSetConfidence;
        public event EventHandler<float>? OnSetIou;
        public event EventHandler<int>? OnSetTaskType;  // YOLO任务类型设置事件
        public event EventHandler<string>? OnVerifyPassword;
        public event EventHandler<string>? OnSaveSettings;
        public event EventHandler? OnSelectStorageFolder;
        public event EventHandler? OnGetStatisticsHistory;
        public event EventHandler? OnResetStatistics;

        // ================== 传统视觉模式事件 ==================
        public event EventHandler<int>? OnVisionModeChanged;
        public event EventHandler<PipelineUpdateRequest>? OnPipelineUpdate;
        public event EventHandler? OnGetVisionConfig;
        public event EventHandler? OnGetPreview;
        public event EventHandler<string>? OnUploadTemplate;
        public event EventHandler<string>? OnSaveCroppedTemplate;
        public event EventHandler? OnTestTemplateMatch;
        public event EventHandler? OnGetFrameForTemplate;
        public event EventHandler<TrainOperatorRequest>? OnTrainOperator;

        // ================== 多相机事件 ==================
        public event EventHandler? OnGetCameraList;
        public event EventHandler<string>? OnSwitchCamera;
        public event EventHandler<string>? OnAddCamera;  // JSON格式的相机数据
        public event EventHandler<string>? OnDeleteCamera;  // 相机ID
        public event EventHandler? OnSuperSearchCameras;  // 相机超级搜索
        public event EventHandler<string>? OnDirectConnectCamera;  // 直接连接相机（JSON格式）

        // ================== 多模型切换事件 ==================
        public event EventHandler<string>? OnSetAuxiliary1Model;
        public event EventHandler<string>? OnSetAuxiliary2Model;
        public event EventHandler<bool>? OnToggleMultiModelFallback;

        public WebUIController()
        {
        }

        public string ImageBasePath { get; set; } = "";

        /// <summary>
        /// Maps the image folder to a virtual host for direct access.
        /// </summary>
        public void SetImageMapping(string localPath)
        {
            if (_webView?.CoreWebView2 != null && Directory.Exists(localPath))
            {
                // Map http://ng-images.local/ to the local folder
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("ng-images.local", localPath, CoreWebView2HostResourceAccessKind.Allow);
            }
        }

        /// <summary>
        /// Initializes the WebView2 environment and mapping.
        /// </summary>
        public async Task InitializeAsync(WebView2 webView)
        {
            _webView = webView;
            try
            {
                // Specify the user data folder to avoid permission issues
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GreeVision_WebView2");
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
                    Directory.CreateDirectory(htmlPath);
                }

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", htmlPath, CoreWebView2HostResourceAccessKind.Allow);

                // Disable some browser features for industrial app look and feel
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // Clear cache to ensure latest HTML is loaded
                await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync();

                // Register message received handler BEFORE navigation to ensure no messages (like app_ready) are missed
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // Navigate to the index page with cache-busting timestamp
                string timestamp = DateTime.Now.Ticks.ToString();
                _webView.CoreWebView2.Navigate($"https://app.local/index.html?v={timestamp}");

                // Warn/Notify user if in Dev Mode
                if (isDevMode)
                {
                    // Delay slightly to allow page load, then log
                    _ = Task.Delay(1000).ContinueWith(async _ => await LogToFrontend($"[DEV] Source Mapping Active: {htmlPath}", "warning"));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 Initialization failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        public async Task UpdateUI(int total, int ok, int ng)
        {
            if (_webView?.CoreWebView2 == null) return;

            var data = new { total = total, ok = ok, ng = ng };
            string json = JsonSerializer.Serialize(data);

            // Calls JavaScript function: updateStatus(json)
            await _webView.ExecuteScriptAsync($"updateStatus({json})");
        }

        /// <summary>
        /// Shows the OK/NG large result overlay.
        /// </summary>
        /// <param name="isOk">Result</param>
        public async Task UpdateResult(bool isOk)
        {
            if (_webView?.CoreWebView2 == null) return;

            // Calls JavaScript function: updateResult(isOk)
            await _webView.ExecuteScriptAsync($"updateResult({(isOk ? "true" : "false")})");
        }

        /// <summary>
        /// Sends inference performance metrics to frontend
        /// </summary>
        public async Task SendInferenceMetrics(object metrics)
        {
            if (_webView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(metrics);
            await _webView.ExecuteScriptAsync($"updateInferenceMetrics({json})");
        }

        /// <summary>
        /// Sends the real-time camera image as base64 to the frontend.
        /// </summary>
        public async Task UpdateImage(string base64Image)
        {
            if (_webView?.CoreWebView2 == null) return;

            // Note: Sending very large strings via ExecuteScriptAsync can be performant enough for simple use cases,
            // but for high FPS, PostWebMessageAsJson or shared buffer is better. 
            // Stick to requested specific function updateImage(base64).
            await _webView.ExecuteScriptAsync($"updateImage('{base64Image}')");
        }

        /// <summary>
        /// Sends the model list to the frontend (Requirement from Step 177/147).
        /// </summary>
        public async Task SendModelList(string[] models)
        {
            if (_webView?.CoreWebView2 == null) return;

            string json = JsonSerializer.Serialize(models);

            // Call the JS function as requested: initModelList(jsonList)
            await _webView.ExecuteScriptAsync($"initModelList({json})");
        }

        /// <summary>
        /// Updates the camera name displayed on the frontend.
        /// </summary>
        public async Task UpdateCameraName(string name)
        {
            if (_webView?.CoreWebView2 == null) return;

            // Escape the string to prevent JS injection
            string safeName = name.Replace("'", "\\'");
            await _webView.ExecuteScriptAsync($"updateCameraName('{safeName}')");
        }

        /// <summary>
        /// Updates connection status indicators on the frontend.
        /// </summary>
        /// <param name="type">"cam" or "plc"</param>
        /// <param name="isConnected">Connection state</param>
        public async Task UpdateConnection(string type, bool isConnected)
        {
            if (_webView?.CoreWebView2 == null) return;

            string jsCode = $"updateConnection('{type}', {isConnected.ToString().ToLower()})";
            await _webView.ExecuteScriptAsync(jsCode);
        }

        /// <summary>
        /// Flashes the PLC trigger indicator on the frontend.
        /// Called when a trigger signal is received from PLC.
        /// </summary>
        public async Task FlashPlcTrigger()
        {
            if (_webView?.CoreWebView2 == null) return;
            await _webView.ExecuteScriptAsync("flashPlcTrigger()");
        }

        /// <summary>
        /// Sends current config to frontend to open settings modal
        /// </summary>
        public async Task SendCurrentConfig(AppConfig config)
        {
            if (_webView?.CoreWebView2 == null) return;

            string json = JsonSerializer.Serialize(config);
            await _webView.ExecuteScriptAsync($"openSettingsModal({json})");
        }

        public async Task InitSettings(AppConfig config)
        {
            if (_webView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(config);
            await _webView.ExecuteScriptAsync($"initSettings({json})");
        }

        /// <summary>
        /// Execute arbitrary JavaScript code
        /// </summary>
        public async Task ExecuteScriptAsync(string script)
        {
            if (_webView?.CoreWebView2 == null) return;
            await _webView.ExecuteScriptAsync(script);
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
                    if (root.TryGetProperty("cmd", out JsonElement cmdElement))
                    {
                        string cmd = cmdElement.GetString() ?? string.Empty;

                        switch (cmd)
                        {
                            case "find_camera":
                                OnFindCamera?.Invoke(this, EventArgs.Empty);
                                break;
                            case "open_camera":
                                OnOpenCamera?.Invoke(this, EventArgs.Empty);
                                break;
                            case "manual_detect":
                                OnManualDetect?.Invoke(this, EventArgs.Empty);
                                break;
                            case "manual_release":
                                OnManualRelease?.Invoke(this, EventArgs.Empty);
                                break;
                            case "open_settings":
                                OnOpenSettings?.Invoke(this, EventArgs.Empty);
                                break;
                            case "change_model":
                                if (root.TryGetProperty("value", out JsonElement modelElement))
                                {
                                    OnChangeModel?.Invoke(this, modelElement.GetString() ?? "");
                                }
                                break;
                            case "app_ready":
                                OnAppReady?.Invoke(this, EventArgs.Empty);
                                // Debug log
                                await LogToFrontend("收到 app_ready 指令");
                                break;
                            case "test_yolo":
                                OnTestYolo?.Invoke(this, EventArgs.Empty);
                                await LogToFrontend("收到 test_yolo 指令");
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
                                            await LogToFrontend($"ROI已更新: [{string.Join(", ", rectArray.Select(v => v.ToString("F3")))}]");
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
                            case "set_confidence":
                                if (root.TryGetProperty("value", out JsonElement confElement))
                                {
                                    float conf = confElement.GetSingle();
                                    OnSetConfidence?.Invoke(this, conf);
                                    await LogToFrontend($"置信度已设置: {conf:F2}");
                                }
                                break;
                            case "set_iou":
                                if (root.TryGetProperty("value", out JsonElement iouElement))
                                {
                                    float iou = iouElement.GetSingle();
                                    OnSetIou?.Invoke(this, iou);
                                    await LogToFrontend($"IOU阈值已设置: {iou:F2}");
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
                                        3 => "实例分割 (Segment)",
                                        5 => "姿态估计 (Pose)",
                                        6 => "旋转框检测 (OBB)",
                                        _ => $"未知 ({taskType})"
                                    };
                                    await LogToFrontend($"任务类型已设置: {taskName}");
                                }
                                break;
                            case "verify_password":
                                if (root.TryGetProperty("value", out JsonElement pwdElement))
                                {
                                    OnVerifyPassword?.Invoke(this, pwdElement.GetString() ?? "");
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
                                    await SendNGImages(date, hour);
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
                            case "reset_statistics":
                                OnResetStatistics?.Invoke(this, EventArgs.Empty);
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
                                OnSuperSearchCameras?.Invoke(this, EventArgs.Empty);
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

                            // ================== 传统视觉模式命令 ==================
                            case "set_vision_mode":
                                if (root.TryGetProperty("value", out JsonElement modeElement))
                                {
                                    int mode = modeElement.GetInt32();
                                    OnVisionModeChanged?.Invoke(this, mode);
                                    await LogToFrontend($"视觉模式已切换为: {(mode == 0 ? "YOLO" : "传统视觉")}");
                                }
                                break;
                            case "get_vision_config":
                                OnGetVisionConfig?.Invoke(this, EventArgs.Empty);
                                break;
                            case "pipeline_update":
                                if (root.TryGetProperty("value", out JsonElement pipelineElement))
                                {
                                    try
                                    {
                                        var request = JsonSerializer.Deserialize<PipelineUpdateRequest>(pipelineElement.GetRawText());
                                        if (request != null)
                                        {
                                            OnPipelineUpdate?.Invoke(this, request);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        await LogToFrontend($"流程更新解析错误: {ex.Message}", "error");
                                    }
                                }
                                break;
                            case "get_preview":
                                OnGetPreview?.Invoke(this, EventArgs.Empty);
                                break;
                            case "upload_template":
                                if (root.TryGetProperty("value", out JsonElement templateElement))
                                {
                                    OnUploadTemplate?.Invoke(this, templateElement.GetString() ?? "");
                                }
                                break;
                            case "test_template_match":
                                OnTestTemplateMatch?.Invoke(this, EventArgs.Empty);
                                break;
                            case "save_cropped_template_data":
                                if (root.TryGetProperty("value", out JsonElement cropElement))
                                {
                                    OnSaveCroppedTemplate?.Invoke(this, cropElement.GetString() ?? "");
                                }
                                break;
                            case "get_frame_for_template":
                                OnGetFrameForTemplate?.Invoke(this, EventArgs.Empty);
                                break;
                            case "train_operator":
                                if (root.TryGetProperty("value", out JsonElement trainElement))
                                {
                                    try
                                    {
                                        var req = JsonSerializer.Deserialize<TrainOperatorRequest>(trainElement.GetRawText());
                                        if (req != null)
                                        {
                                            OnTrainOperator?.Invoke(this, req);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        await LogToFrontend($"解析训练请求失败: {ex.Message}", "error");
                                    }
                                }
                                break;
                            case "get_operator_template":
                                // Request template data for a specific operator - handled via OnGetVisionConfig for now
                                OnGetVisionConfig?.Invoke(this, EventArgs.Empty);
                                break;
                            case "set_template_threshold":
                                if (root.TryGetProperty("value", out JsonElement threshElement))
                                {
                                    float thresh = threshElement.GetSingle();
                                    // This is handled through pipeline_update with threshold param
                                    await LogToFrontend($"模板阈值已设置: {thresh:F2}");
                                }
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

        /// <summary>
        /// Sends a log message to the upper "Detection Log" window.
        /// </summary>
        public async Task LogDetectionToFrontend(string message, string type = "normal")
        {
            if (_webView?.CoreWebView2 == null) return;
            string safeMsg = message.Replace("'", "\\'").Replace("\n", "\\n");
            await _webView.ExecuteScriptAsync($"addDetectionLog('{safeMsg}', '{type}')");
        }

        public async Task LogToFrontend(string message, string type = "normal")
        {
            if (_webView?.CoreWebView2 == null) return;
            string safeMsg = message.Replace("'", "\\'").Replace("\n", "\\n");
            await _webView.ExecuteScriptAsync($"addLog('{safeMsg}', '{type}')");
        }

        public async Task UpdateStoragePathInUI(string path)
        {
            if (_webView?.CoreWebView2 == null) return;
            string safePath = path.Replace("\\", "\\\\").Replace("'", "\\'");
            await _webView.ExecuteScriptAsync($"updateStoragePath('{safePath}')");
        }

        private async Task SendNGDates()
        {
            if (string.IsNullOrEmpty(ImageBasePath) || _webView == null) return;
            try
            {
                string path = Path.Combine(ImageBasePath, "Unqualified");
                if (Directory.Exists(path))
                {
                    var dates = Directory.GetDirectories(path)
                        .Select(Path.GetFileName)
                        .OrderByDescending(d => d) // Newest first
                        .ToArray();
                    string json = JsonSerializer.Serialize(dates);
                    await _webView.ExecuteScriptAsync($"updateNGDates({json})");
                }
                else
                {
                    await _webView.ExecuteScriptAsync("updateNGDates([])");
                }
            }
            catch (Exception ex)
            {
                await LogToFrontend($"获取日期列表失败: {ex.Message}", "error");
            }
        }

        private async Task SendNGHours(string? date)
        {
            if (string.IsNullOrEmpty(ImageBasePath) || string.IsNullOrEmpty(date) || _webView == null) return;
            try
            {
                string path = Path.Combine(ImageBasePath, "Unqualified", date);
                if (Directory.Exists(path))
                {
                    var hours = Directory.GetDirectories(path)
                        .Select(Path.GetFileName)
                        .OrderByDescending(h => h)
                        .ToArray();
                    string json = JsonSerializer.Serialize(hours);
                    await _webView.ExecuteScriptAsync($"updateNGHours({json})");
                }
                else
                {
                    await _webView.ExecuteScriptAsync("updateNGHours([])");
                }
            }
            catch { await _webView.ExecuteScriptAsync("updateNGHours([])"); }
        }

        private async Task SendNGImages(string date, string hour)
        {
            if (string.IsNullOrEmpty(ImageBasePath) || string.IsNullOrEmpty(date) || string.IsNullOrEmpty(hour) || _webView == null) return;
            try
            {
                string path = Path.Combine(ImageBasePath, "Unqualified", date, hour);
                if (Directory.Exists(path))
                {
                    var images = Directory.GetFiles(path, "*.*")
                        .Where(s => s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        .Select(Path.GetFileName)
                        .OrderByDescending(f => f)
                        .ToArray();
                    string json = JsonSerializer.Serialize(images);
                    await _webView.ExecuteScriptAsync($"updateNGImages({json})");
                }
                else
                {
                    await _webView.ExecuteScriptAsync("updateNGImages([])");
                }
            }
            catch { await _webView.ExecuteScriptAsync("updateNGImages([])"); }
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
            if (string.IsNullOrEmpty(LogBasePath) || _webView?.CoreWebView2 == null) return;
            try
            {
                string logsDir = Path.Combine(LogBasePath, "DetectionLogs");
                if (!Directory.Exists(logsDir))
                {
                    await _webView.ExecuteScriptAsync("updateDetectionLogTable([])");
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

                string json = JsonSerializer.Serialize(logEntries);
                await _webView.ExecuteScriptAsync($"updateDetectionLogTable({json})");
            }
            catch (Exception ex)
            {
                await LogToFrontend($"读取检测日志失败: {ex.Message}", "error");
                await _webView.ExecuteScriptAsync("updateDetectionLogTable([])");
            }
        }

        /// <summary>
        /// Sends statistics history to frontend
        /// </summary>
        public async Task SendStatisticsHistory(StatisticsHistory history, DetectionStatistics current)
        {
            if (_webView?.CoreWebView2 == null) return;

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

                string json = JsonSerializer.Serialize(allRecords);
                await _webView.ExecuteScriptAsync($"receiveStatisticsHistory({json})");
            }
            catch (Exception ex)
            {
                await LogToFrontend($"获取历史统计失败: {ex.Message}", "error");
            }
        }

        // ================== 传统视觉模式方法 ==================

        /// <summary>
        /// 发送视觉配置到前端
        /// </summary>
        public async Task SendVisionConfig(VisionConfigResponse config)
        {
            if (_webView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(config);
            await _webView.ExecuteScriptAsync($"receiveVisionConfig({json})");
        }

        /// <summary>
        /// 发送预览图像到前端
        /// </summary>
        public async Task SendPreviewImage(PreviewResponse preview)
        {
            if (_webView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(preview);
            await _webView.ExecuteScriptAsync($"updatePreviewImage({json})");
        }

        /// <summary>
        /// 发送可用算子列表到前端
        /// </summary>
        public async Task SendAvailableOperators()
        {
            if (_webView?.CoreWebView2 == null) return;
            var operators = OperatorFactory.GetAvailableOperators();
            string json = JsonSerializer.Serialize(operators);
            await _webView.ExecuteScriptAsync($"receiveAvailableOperators({json})");
        }

        /// <summary>
        /// 发送流程更新确认
        /// </summary>
        public async Task SendPipelineUpdated(VisionConfig config)
        {
            if (_webView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(config);
            await _webView.ExecuteScriptAsync($"receivePipelineUpdate({json})");
        }

        /// <summary>
        /// 发送检测结果到前端
        /// </summary>
        public async Task SendDetectionResult(DetectionResponse result)
        {
            if (_webView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(result);
            await _webView.ExecuteScriptAsync($"receiveDetectionResult({json})");
        }

        /// <summary>
        /// Sends the captured frame for template editing to the frontend.
        /// </summary>
        public async Task ReceiveTemplateFrame(string base64)
        {
            if (_webView?.CoreWebView2 == null) return;
            await _webView.ExecuteScriptAsync($"receiveTemplateFrame('{base64}')");
        }

        // ================== 多相机方法 ==================

        /// <summary>
        /// 发送相机列表到前端
        /// </summary>
        public async Task SendCameraList(IEnumerable<object> cameras, string activeCameraId)
        {
            if (_webView?.CoreWebView2 == null) return;
            var data = new { cameras = cameras, activeId = activeCameraId };
            string json = JsonSerializer.Serialize(data);
            await _webView.ExecuteScriptAsync($"receiveCameraList({json})");
        }

        /// <summary>
        /// 发送超级搜索结果到前端（所有局域网相机）
        /// </summary>
        public async Task SendDiscoveredCameras(IEnumerable<object> cameras)
        {
            if (_webView?.CoreWebView2 == null) return;
            string json = JsonSerializer.Serialize(new { cameras = cameras });
            await _webView.ExecuteScriptAsync($"receiveSuperSearchResult({json})");
        }
    }
}



