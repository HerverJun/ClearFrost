using System;
using ClearFrost.Hardware;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClearFrost.Helpers;

namespace ClearFrost.Config
{
    public class AppConfig : IJsonOnDeserialized
    {
        // ================== PLC Settings ==================
        public string PlcIp { get; set; } = "192.168.250.1";
        public int PlcPort { get; set; } = 5999;
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcTriggerAddress { get; set; } = "D555";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcResultAddress { get; set; } = "D556";
        public int PlcTriggerDelayMs { get; set; } = 800;
        public int PlcPollingIntervalMs { get; set; } = 500;
        /// <summary>
        /// 合格时写入PLC的值
        /// </summary>
        public short PlcOkValue { get; set; } = 1;
        /// <summary>
        /// 不合格时写入PLC的值
        /// </summary>
        public short PlcNgValue { get; set; } = 2;
        /// <summary>
        /// PLC协议类型: Mitsubishi_MC_ASCII, Mitsubishi_MC_Binary, Modbus_TCP, Siemens_S7, Omron_Fins
        /// </summary>
        public string PlcProtocol { get; set; } = "Mitsubishi_MC_ASCII";
        /// <summary>
        /// PLC驱动库: Hsl, McpX (McpX 仅支持三菱)
        /// </summary>
        public string PlcDriverProvider { get; set; } = "Hsl";
        /// <summary>
        /// 西门子 CPU 型号: S1200, S1500, S300, S400
        /// </summary>
        public string PlcSiemensCpuModel { get; set; } = "S1200";
        /// <summary>
        /// 西门子 Rack，仅对 S300/S400 生效
        /// </summary>
        public int PlcSiemensRack { get; set; }
        /// <summary>
        /// 西门子 Slot，仅对 S300/S400 生效
        /// </summary>
        public int PlcSiemensSlot { get; set; } = 2;

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
        public string CameraName { get; set; } = "W6电加热螺钉视觉检测";
        [Obsolete("Use Cameras list instead")]
        public string CameraSerialNumber { get; set; } = "EF59632AAK00291";
        [Obsolete("Use Cameras list instead")]
        public string CameraManufacturer { get; set; } = "Huaray";
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
        /// <summary>
        /// 当前使用的主模型文件名（含扩展名，如 "model_v1.onnx"）
        /// </summary>
        public string CurrentModelFileName { get; set; } = "";
        public int TaskType { get; set; } = 1;
        public bool EnablePreprocessing { get; set; } = true;
        public bool EnableGpu { get; set; } = false;
        public int GpuIndex { get; set; } = 0;
        public bool IndustrialRenderMode { get; set; } = true;
        public bool UseFileBackedWebImageTransport { get; set; } = false;

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

        // ================== Legacy Traditional Vision Compatibility Settings ==================
        public int VisionMode { get; set; } = 0;
        public string TemplateImagePath { get; set; } = "";
        public double TemplateThreshold { get; set; } = 0.8;
        public string VisionPipelineJson { get; set; } = "[]";

        [JsonIgnore]
        public string? LastError { get; private set; }

        private static string ConfigPath => RuntimePaths.ConfigPath;
        private static string LegacySharedConfigPath => RuntimePaths.LegacySharedConfigPath;
        private static string BundledConfigPath => RuntimePaths.BundledConfigPath;
        private static string ErrorLogPath => RuntimePaths.ConfigErrorLogPath;

        public AppConfig()
        {
            MigrateLegacyCamera();
            NormalizePlcAddresses();
        }

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
            catch (Exception logEx) { System.Diagnostics.Debug.WriteLine($"[AppConfig] 日志写入失败: {logEx.Message}"); }
        }

        public static AppConfig Load()
        {
            try
            {
                string loadPath = GetReadableConfigPath();
                if (File.Exists(loadPath))
                {
                    string json = File.ReadAllText(loadPath);
                    var options = new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                        PropertyNameCaseInsensitive = true
                    };
                    var config = JsonSerializer.Deserialize<AppConfig>(json, options) ?? new AppConfig();
                    config.MigrateLegacyCamera();
                    config.NormalizePlcAddresses();
                    if (!PathsEqual(loadPath, ConfigPath))
                    {
                        config.TrySeedRuntimeConfig();
                    }

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
                    SerialNumber = CameraSerialNumber?.Trim() ?? "",
                    DisplayName = CameraName?.Trim() ?? "",
                    Manufacturer = string.IsNullOrWhiteSpace(CameraManufacturer) ? "Huaray" : CameraManufacturer.Trim(),
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
        [JsonIgnore]
        public CameraConfig? ActiveCamera =>
            Cameras.FirstOrDefault(c => c.Id == ActiveCameraId) ??
            Cameras.FirstOrDefault(c => c.IsEnabled);

        public bool Save()
        {
            try
            {
                NormalizePlcAddresses();
                string configDir = Path.GetDirectoryName(ConfigPath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

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

        public void OnDeserialized()
        {
            MigrateLegacyCamera();
            NormalizePlcAddresses();
        }

        private void NormalizePlcAddresses()
        {
            PlcProtocolType protocolType = PlcFactory.ParseProtocol(PlcProtocol);
            PlcTriggerAddress = PlcAddressNormalizer.MigrateLegacyAddress(
                PlcTriggerAddress,
                protocolType,
                GetProtocolDefaultAddress(protocolType, 555));
            PlcResultAddress = PlcAddressNormalizer.MigrateLegacyAddress(
                PlcResultAddress,
                protocolType,
                GetProtocolDefaultAddress(protocolType, 556));

            if (!IsMitsubishiProtocol(protocolType) &&
                string.Equals(PlcDriverProvider, "McpX", StringComparison.OrdinalIgnoreCase))
            {
                PlcDriverProvider = "Hsl";
            }

            if (string.IsNullOrWhiteSpace(PlcSiemensCpuModel))
            {
                PlcSiemensCpuModel = "S1200";
            }
        }

        private static bool IsMitsubishiProtocol(PlcProtocolType protocolType)
        {
            return protocolType == PlcProtocolType.Mitsubishi_MC_ASCII ||
                   protocolType == PlcProtocolType.Mitsubishi_MC_Binary;
        }

        private static string GetProtocolDefaultAddress(PlcProtocolType protocolType, int number)
        {
            return protocolType switch
            {
                PlcProtocolType.Siemens_S7 => $"DB1.{number}",
                PlcProtocolType.Modbus_TCP => number.ToString(),
                PlcProtocolType.Omron_Fins => $"D{number}",
                _ => $"D{number}"
            };
        }

        private static string GetReadableConfigPath()
        {
            if (File.Exists(ConfigPath))
            {
                return ConfigPath;
            }

            if (File.Exists(LegacySharedConfigPath))
            {
                return LegacySharedConfigPath;
            }

            if (File.Exists(BundledConfigPath))
            {
                return BundledConfigPath;
            }

            return ConfigPath;
        }

        private void TrySeedRuntimeConfig()
        {
            try
            {
                Save();
            }
            catch (Exception ex)
            {
                LogError("SeedRuntimeConfig", ex);
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
