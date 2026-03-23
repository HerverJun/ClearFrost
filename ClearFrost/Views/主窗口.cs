using MVSDK_Net;
using ClearFrost.Config;
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
                // 如果没有配置相机，创建默认相机
                cam = _appConfig.IsDebugMode ? new MockCamera() : new RealCamera();
            }

            // 使用系统原生标题栏，启动时保持最大化
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.WindowState = FormWindowState.Maximized;
            this.Text = "清霜 ClearFrost V4 预览版";

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
            SafeFireAndForget(_databaseService.InitializeAsync(), "数据库初始化");
            _imageSaveQueue = _appRuntime.ImageSaveQueue;
            _detectionRecordQueue = _appRuntime.DetectionRecordQueue;

            // 注册所有事件监听 (实现位于 主窗口.Init.cs)
            RegisterEvents();
        }

        #endregion
    }
}
