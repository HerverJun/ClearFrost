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
using YOLO.Helpers;
using YOLO.Interfaces;
using YOLO.Services;

namespace YOLO
{
    public partial class 主窗口 : Form
    {
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

            // ====================== 初始化服务层 ======================
            // PLC 服务
            _plcService = new PlcService();

            // Detection 服务
            _detectionService = new DetectionService(_appConfig.EnableGpu);

            // Storage 服务
            _storageService = new StorageService(_appConfig?.StoragePath);

            // Statistics 服务
            _statisticsService = new StatisticsService(_storageService.SystemPath.Replace("\\System", ""));

            // Database 服务 (SQLite)
            _databaseService = new SqliteDatabaseService();
            SafeFireAndForget(_databaseService.InitializeAsync(), "数据库初始化");

            // 注册所有事件监听 (实现位于 主窗口.Init.cs)
            RegisterEvents();
        }

        #endregion
    }
}