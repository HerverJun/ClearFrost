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
using ClearFrost.Vision;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    public partial class 主窗口 : Form
    {
        #region 1. 全局变量与配置定义 (Global Definitions)

        // ====================== 服务层 ======================
        private readonly IPlcService _plcService;
        private readonly IDetectionService _detectionService;
        private readonly IStorageService _storageService;
        private readonly IStatisticsService _statisticsService;
        private readonly IDatabaseService _databaseService;

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

        // ====================== 统计 (由 _statisticsService 管理) ======================

        // ====================== 硬件设备对象 (兼容旧代码) ======================
        // PLC - 通过服务层管理
        private bool plcConnected => _plcService?.IsConnected ?? false;

        // ====================== 相机管理 ======================
        // 架构说明:
        // - _cameraManager: 多相机配置管理器,负责相机列表和切换
        // - cam: 当前活动相机的 SDK 句柄,用于直接硬件操作
        // TODO: 后续版本考虑将 cam 的 SDK 调用封装到 ICameraService
        private CameraManager _cameraManager;
        private ICamera cam; // 活动相机 SDK 句柄 (由 _cameraManager.ActiveCamera 提供)
        private int _targetCameraIndex = -1;
        private Thread? renderThread = null;
        private BlockingCollection<IMVDefine.IMV_Frame> m_frameQueue = new BlockingCollection<IMVDefine.IMV_Frame>(10);
        private CancellationTokenSource m_cts = new CancellationTokenSource();

        // YOLO (由 _detectionService 管理)
        // 多模型管理器 (由 _detectionService 管理)
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

        /// <summary>
        /// 检测操作信号量，防止并发检测（如 PLC 快速触发）
        /// </summary>
        private readonly SemaphoreSlim _detectionSemaphore = new SemaphoreSlim(1, 1);

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
    }
}


