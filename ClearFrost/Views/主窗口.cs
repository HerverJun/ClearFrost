using MVSDK_Net;
using ClearFrost.Config;
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
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Yolo;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    public partial class 主窗口 : Form
    {
        #region 2. 初始化与生命周期 (Initialization)

        public 主窗口()
        {
            InitializeComponent();

            _appRuntime = new AppRuntime(_appConfig);
            _cameraManager = _appRuntime.CameraManager;
            _cameraService = _appRuntime.CameraService;

            // 向后兼容：从 CameraManager 获取活动相机
            var activeCam = _cameraManager.ActiveCamera;
            if (activeCam != null)
            {
                cam = activeCam.Camera;
            }
            else
            {
                // 未注册活动相机时仅创建真实相机占位，避免配置异常时静默进入模拟相机。
                cam = new RealCamera();
            }

            // 使用系统原生标题栏，启动时保持最大化
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.WindowState = FormWindowState.Maximized;
            this.Text = "清霜V5.5 正式版";

            // 初始化 WebUI 控制器
            _uiController = _appRuntime.WebUIController;
            _uiController.UseFileBackedImageTransport = _appConfig.UseFileBackedWebImageTransport;
            YoloDetector.IndustrialRenderMode = _appConfig.IndustrialRenderMode;

            // ====================== 初始化服务层 ======================
            _plcService = _appRuntime.PlcService;
            _detectionService = _appRuntime.DetectionService;
            _storageService = _appRuntime.StorageService;
            _statisticsService = _appRuntime.StatisticsService;
            _databaseService = _appRuntime.DatabaseService;
            _uiController.DatabaseService = _databaseService;
            SafeFireAndForget(_databaseService.InitializeAsync(), "数据库初始化");
            _imageSaveQueue = _appRuntime.ImageSaveQueue;
            _detectionRecordQueue = _appRuntime.DetectionRecordQueue;
            _recipeManager = _appRuntime.RecipeManager;
            _modelRegistry = _appRuntime.ModelRegistry;
            _healthMonitor = _appRuntime.HealthMonitor;
            _startupDiagnostics = _appRuntime.StartupDiagnostics;
            _serialTriggerService = new SerialPhotoelectricTriggerService();
            LogStartupDiagnostics();

            // 注册所有事件监听 (实现位于 主窗口.Init.cs)
            RegisterEvents();
        }

        private void LogStartupDiagnostics()
        {
            try
            {
                StartupDiagnosticReport report = _startupDiagnostics.CurrentReport;
                int failCount = report.Items.Count(i => i.Status == StartupDiagnosticStatus.Fail);
                int warningCount = report.Items.Count(i => i.Status == StartupDiagnosticStatus.Warning);
                _storageService.WriteStartupLog(
                    $"StartupDiagnostics Ready={report.IsReady}, Fail={failCount}, Warning={warningCount}");

                foreach (StartupDiagnosticItem item in report.Items)
                {
                    _storageService.WriteStartupLog(
                        $"StartupDiagnostics[{item.Status}] {item.Name}: {item.Message} {item.Details}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartupDiagnostics] 写入启动诊断失败: {ex.Message}");
            }
        }

        private async Task<bool> EnsureStartupReadyForProductionAsync(string operation, string? inspectionId = null)
        {
            if (_startupDiagnostics.CurrentReport.IsReady)
            {
                return true;
            }

            string summary = _appRuntime.StartupBlockingSummary;
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = "启动诊断存在阻塞项";
            }

            string message = $"启动诊断未通过，已阻止{operation}: {summary}";
            RecordHealthError("StartupDiagnostics", message, inspectionId);
            await _uiController.LogToFrontend(message, "error");
            return false;
        }

        #endregion
    }
}
