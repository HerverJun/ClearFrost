using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace YOLO
{
    public class AppConfig
    {
        // ================== PLC Settings ==================
        public string PlcIp { get; set; } = "192.168.22.44";
        public int PlcPort { get; set; } = 4999;
        public short PlcTriggerAddress { get; set; } = 555;
        public short PlcResultAddress { get; set; } = 556;
        /// <summary>
        /// PLC协议类型: Mitsubishi_MC_ASCII, Mitsubishi_MC_Binary, Modbus_TCP
        /// </summary>
        public string PlcProtocol { get; set; } = "Mitsubishi_MC_ASCII";

        // ================== Multi-Camera Settings ==================
        /// <summary>
        /// 相机配置列表 (多相机支持)
        /// </summary>
        public List<CameraConfig> Cameras { get; set; } = new();

        /// <summary>
        /// 当前活动相机的 ID
        /// </summary>
        public string ActiveCameraId { get; set; } = "";

        // ================== Legacy Camera Settings (向后兼容) ==================
        [Obsolete("Use Cameras list instead")]
        public string CameraName { get; set; } = "CAM: HIK-MV-01";
        [Obsolete("Use Cameras list instead")]
        public string CameraSerialNumber { get; set; } = "EF59632AAK00074";
        [Obsolete("Use Cameras list instead")]
        public double ExposureTime { get; set; } = 50000.0;
        [Obsolete("Use Cameras list instead")]
        public double GainRaw { get; set; } = 1.1;

        // ================== System Settings ==================
        public string AdminPassword { get; set; } = "xxgcb";
        public string StoragePath { get; set; } = @"C:\GreeVisionData";
        public bool IsDebugMode { get; set; } = true;

        // ================== YOLO Settings ==================
        public float Confidence { get; set; } = 0.5f;
        public float IouThreshold { get; set; } = 0.3f;
        public bool EnableGlobalIou { get; set; } = false;
        public int ModelVersion { get; set; } = 0;
        public int TaskType { get; set; } = 1;
        public bool EnablePreprocessing { get; set; } = true;
        public bool EnableGpu { get; set; } = false;
        public int GpuIndex { get; set; } = 0;

        // ================== Multi-Model Fallback Settings ==================
        /// <summary>
        /// 辅助模型1路径
        /// </summary>
        public string Auxiliary1ModelPath { get; set; } = "";

        /// <summary>
        /// 辅助模型2路径
        /// </summary>
        public string Auxiliary2ModelPath { get; set; } = "";

        /// <summary>
        /// 是否启用多模型自动切换
        /// </summary>
        public bool EnableMultiModelFallback { get; set; } = false;

        // ================== Logic Settings ==================
        public string TargetLabel { get; set; } = "screw";
        public int TargetCount { get; set; } = 4;
        public int MaxRetryCount { get; set; } = 1;
        public int RetryIntervalMs { get; set; } = 2000;

        // ================== Vision Mode Settings ==================
        public int VisionMode { get; set; } = 0;
        public string TemplateImagePath { get; set; } = "";
        public double TemplateThreshold { get; set; } = 0.8;
        public string VisionPipelineJson { get; set; } = "[]";

        [System.Text.Json.Serialization.JsonIgnore]
        public string? LastError { get; private set; }

        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static string ErrorLogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "config_errors.log");

        private static void LogError(string operation, Exception ex)
        {
            try
            {
                string logDir = Path.GetDirectoryName(ErrorLogPath) ?? "";
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {operation}: {ex.Message}\n";
                File.AppendAllText(ErrorLogPath, message);
                Debug.WriteLine($"[AppConfig] {operation}: {ex.Message}");
            }
            catch { }
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var options = new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                        PropertyNameCaseInsensitive = true
                    };
                    var config = JsonSerializer.Deserialize<AppConfig>(json, options) ?? new AppConfig();
                    config.MigrateLegacyCamera();
                    return config;
                }
            }
            catch (Exception ex)
            {
                LogError("Load", ex);
            }
            return new AppConfig();
        }

        /// <summary>
        /// 将旧版单相机配置迁移到多相机列表
        /// </summary>
        private void MigrateLegacyCamera()
        {
#pragma warning disable CS0618 // 忽略 Obsolete 警告
            if (Cameras.Count == 0 && !string.IsNullOrEmpty(CameraSerialNumber))
            {
                var legacyCam = new CameraConfig
                {
                    Id = "legacy_cam",
                    SerialNumber = CameraSerialNumber,
                    DisplayName = CameraName,
                    ExposureTime = ExposureTime,
                    Gain = GainRaw,
                    IsEnabled = true
                };
                Cameras.Add(legacyCam);
                ActiveCameraId = legacyCam.Id;
                Debug.WriteLine("[AppConfig] Migrated legacy camera to Cameras list");
            }
#pragma warning restore CS0618
        }

        /// <summary>
        /// 获取当前活动相机配置
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public CameraConfig? ActiveCamera =>
            Cameras.FirstOrDefault(c => c.Id == ActiveCameraId) ??
            Cameras.FirstOrDefault(c => c.IsEnabled);

        public bool Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogError("Save", ex);
                return false;
            }
        }
    }
}

