using System;
using ClearFrost.Hardware;
using ClearFrost.Hardware.Triggers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClearFrost.Core.Rules;
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
        public short PlcNgValue { get; set; } = 0;
        /// <summary>
        /// PLC协议类型: Mitsubishi_MC_ASCII, Mitsubishi_MC_Binary, Modbus_TCP, Siemens_S7, Omron_Fins
        /// </summary>
        public string PlcProtocol { get; set; } = "Mitsubishi_MC_ASCII";
        /// <summary>
        /// PLC驱动库: Hsl, HaoCommunication, McpX (McpX 仅支持三菱)
        /// </summary>
        public string PlcDriverProvider { get; set; } = "HaoCommunication";
        /// <summary>
        /// PLC 业务协议模式。默认 Legacy，保持旧现场行为不变。
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public PlcProtocolMode PlcProtocolMode { get; set; } = PlcProtocolMode.Legacy;
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcTriggerSeqAddress { get; set; } = "D557";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcResultSeqAddress { get; set; } = "D558";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcVisionOnlineAddress { get; set; } = "D559";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcVisionReadyAddress { get; set; } = "D560";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcVisionBusyAddress { get; set; } = "D561";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcInspectionDoneAddress { get; set; } = "D562";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcErrorCodeAddress { get; set; } = "D563";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcTraceSavedAddress { get; set; } = "D564";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcHeartbeatAddress { get; set; } = "D565";
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string PlcResetFaultAddress { get; set; } = "D566";
        /// <summary>
        /// PLC 条码读取配置。默认关闭，保持 Legacy 现场行为不变。
        /// </summary>
        public bool BarcodeEnabled { get; set; } = false;
        [JsonConverter(typeof(LegacyPlcAddressJsonConverter))]
        public string BarcodeAddress { get; set; } = "D570";
        public int BarcodeWordLength { get; set; } = 16;
        public string BarcodeEncoding { get; set; } = "ASCII";
        public bool BarcodeRequired { get; set; } = false;
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

        // ================== Trigger Source Settings ==================
        /// <summary>
        /// 触发源: PLC 或 SerialPhotoelectric
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TriggerSource TriggerSource { get; set; } = TriggerSource.PLC;

        /// <summary>
        /// 串口光电 COM 口名称
        /// </summary>
        public string SerialPhotoelectricPortName { get; set; } = "";

        /// <summary>
        /// 串口光电波特率
        /// </summary>
        public int SerialPhotoelectricBaudRate { get; set; } = 9600;

        /// <summary>
        /// 串口光电去抖时间 (毫秒)
        /// </summary>
        public int SerialPhotoelectricDebounceMs { get; set; } = 50;

        /// <summary>
        /// 串口光电读取超时 (毫秒)
        /// </summary>
        public int SerialPhotoelectricTimeoutMs { get; set; } = 1000;

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
        public string StoragePath { get; set; } = @"C:\GreeVisionData";
        public bool IsDebugMode { get; set; } = false;

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
        public bool UseFileBackedWebImageTransport { get; set; } = true;
        public string ModelPackageDirectory { get; set; } = "models";
        public bool StrictModelPackageMode { get; set; } = false;

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

        // ================== Inspection Rule Settings ==================
        /// <summary>
        /// 检测判定规则集 JSON。新配置入口，旧 Target/WireSequence 字段仅作为迁移输入保留。
        /// </summary>
        public string InspectionRuleSetJson { get; set; } = "";

        // ================== Wire Sequence Judge Settings ==================
        /// <summary>
        /// 启用端子线序判定。启用后检测结果按配置排序并比对 ExpectedLabels。
        /// </summary>
        public bool WireSequenceJudgeEnabled { get; set; } = false;

        /// <summary>
        /// 期望线序标签，逗号分隔，例如 Wire_Brown,Wire_Black,Wire_Blue。
        /// </summary>
        public string WireSequenceExpectedLabels { get; set; } = "Wire_Brown,Wire_Black,Wire_Blue";

        /// <summary>
        /// 排序字段: CenterX, CenterY, TopY, Confidence, Area。
        /// </summary>
        public string WireSequenceSortBy { get; set; } = "CenterX";

        /// <summary>
        /// 排序方向: Ascending, Descending, LeftToRight, RightToLeft, TopToBottom, BottomToTop。
        /// </summary>
        public string WireSequenceDirection { get; set; } = "LeftToRight";

        /// <summary>
        /// 期望检测数量。0 表示按期望标签数量推导。
        /// </summary>
        public int WireSequenceExpectedCount { get; set; } = 0;

        /// <summary>
        /// 线序判定前的最低置信度过滤。
        /// </summary>
        public double WireSequenceMinConfidence { get; set; } = 0.0;

        public bool WireSequenceAllowMissing { get; set; } = false;
        public bool WireSequenceAllowDuplicate { get; set; } = false;

        // ================== Legacy Traditional Vision Compatibility Settings ==================
        public int VisionMode { get; set; } = 0;
        public string TemplateImagePath { get; set; } = "";
        public double TemplateThreshold { get; set; } = 0.8;
        public string VisionPipelineJson { get; set; } = "[]";

        [JsonIgnore]
        public string? LastError { get; private set; }

        private static string ConfigPath => RuntimePaths.ConfigPath;
        private static string ConfigBackupPath => ConfigPath + ".bak";
        private static string LegacySharedConfigPath => RuntimePaths.LegacySharedConfigPath;
        private static string BundledConfigPath => RuntimePaths.BundledConfigPath;
        private static string ErrorLogPath => RuntimePaths.ConfigErrorLogPath;

        public AppConfig()
        {
            MigrateLegacyCamera();
            NormalizePlcAddresses();
            NormalizeSequenceJudgeSettings();
            NormalizeCameraPixelFormats();
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
            foreach (string loadPath in GetReadableConfigPaths())
            {
                try
                {
                    if (!File.Exists(loadPath))
                    {
                        continue;
                    }

                    string json = File.ReadAllText(loadPath);
                    var config = FromJson(json);
                    config.MigrateLegacyCamera();
                    config.NormalizeRuntimeSettings();
                    if (!PathsEqual(loadPath, ConfigPath))
                    {
                        config.TrySeedRuntimeConfig();
                    }

                    return config;
                }
                catch (Exception ex)
                {
                    LogError($"Load({loadPath})", ex);
                }
            }

            return new AppConfig();
        }

        public static AppConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("配置文件内容为空");
            }

            var config = JsonSerializer.Deserialize<AppConfig>(json, CreateJsonOptions())
                ?? throw new InvalidOperationException("配置文件内容为空");
            config.MigrateLegacyCamera();
            config.NormalizeRuntimeSettings();
            return config;
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

        public CameraConfig? EnsureActiveCameraConfigFromLegacy()
        {
#pragma warning disable CS0618
            Cameras ??= new List<CameraConfig>();

            string legacySerial = CameraSerialNumber?.Trim() ?? string.Empty;
            var activeCamera =
                Cameras.FirstOrDefault(c => c.Id == ActiveCameraId) ??
                (!string.IsNullOrWhiteSpace(legacySerial)
                    ? Cameras.FirstOrDefault(c => string.Equals(c.SerialNumber?.Trim(), legacySerial, StringComparison.OrdinalIgnoreCase))
                    : null) ??
                Cameras.FirstOrDefault(c => c.IsEnabled) ??
                Cameras.FirstOrDefault();

            if (activeCamera == null)
            {
                if (string.IsNullOrWhiteSpace(legacySerial))
                {
                    return null;
                }

                activeCamera = new CameraConfig
                {
                    Id = "legacy_cam"
                };
                Cameras.Add(activeCamera);
            }

            activeCamera.SerialNumber = legacySerial;
            activeCamera.DisplayName = CameraName?.Trim() ?? string.Empty;
            activeCamera.Manufacturer = string.IsNullOrWhiteSpace(CameraManufacturer) ? "Huaray" : CameraManufacturer.Trim();
            activeCamera.ExposureTime = ExposureTime;
            activeCamera.Gain = GainRaw;
            activeCamera.IsEnabled = true;
            ActiveCameraId = activeCamera.Id;
            return activeCamera;
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
                NormalizeSequenceJudgeSettings();
                NormalizeInspectionRuleSetSettings();
                NormalizeCameraPixelFormats();
                string configDir = Path.GetDirectoryName(ConfigPath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                string json = SerializeForSave();
                WriteConfigAtomically(ConfigPath, json);
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

        public string ToPortableJson()
        {
            NormalizeRuntimeSettings();
            return SerializeForSave();
        }

        public void CopyFrom(AppConfig source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            AppConfig normalizedSource = FromJson(source.ToPortableJson());
            foreach (PropertyInfo property in typeof(AppConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead ||
                    property.SetMethod?.IsPublic != true ||
                    property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object? value = property.GetValue(normalizedSource);
                if (value is List<CameraConfig> cameras)
                {
                    value = cameras.Select(camera => camera.Clone()).ToList();
                }

                property.SetValue(this, value);
            }

            NormalizeRuntimeSettings();
            LastError = null;
        }

        public void OnDeserialized()
        {
            MigrateLegacyCamera();
            NormalizeRuntimeSettings();
        }

        private string SerializeForSave()
        {
            NormalizeInspectionRuleSetSettings();
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            JsonObject? root = JsonNode.Parse(json)?.AsObject();
            if (root == null)
            {
                return json;
            }

            string[] legacyRuleFields =
            {
                nameof(TargetLabel),
                nameof(TargetCount),
                nameof(WireSequenceJudgeEnabled),
                nameof(WireSequenceExpectedLabels),
                nameof(WireSequenceSortBy),
                nameof(WireSequenceDirection),
                nameof(WireSequenceExpectedCount),
                nameof(WireSequenceMinConfidence),
                nameof(WireSequenceAllowMissing),
                nameof(WireSequenceAllowDuplicate)
            };

            foreach (string field in legacyRuleFields)
            {
                root.Remove(field);
            }

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        private void NormalizeRuntimeSettings()
        {
            NormalizePlcAddresses();
            NormalizeSequenceJudgeSettings();
            NormalizeInspectionRuleSetSettings();
            NormalizeCameraPixelFormats();
        }

        public InspectionRuleSet GetInspectionRuleSet()
        {
            NormalizeInspectionRuleSetSettings();
            return InspectionRuleSetSerializer.DeserializeOrDefault(InspectionRuleSetJson);
        }

        private void NormalizeSequenceJudgeSettings()
        {
            WireSequenceExpectedLabels = string.Join(
                ",",
                (WireSequenceExpectedLabels ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(label => !string.IsNullOrWhiteSpace(label)));
            WireSequenceExpectedCount = Math.Clamp(WireSequenceExpectedCount, 0, 256);
            WireSequenceMinConfidence = Math.Clamp(WireSequenceMinConfidence, 0.0, 1.0);

            if (string.IsNullOrWhiteSpace(WireSequenceSortBy))
            {
                WireSequenceSortBy = "CenterX";
            }

            if (string.IsNullOrWhiteSpace(WireSequenceDirection))
            {
                WireSequenceDirection = "LeftToRight";
            }
        }

        private void NormalizeInspectionRuleSetSettings()
        {
            if (!InspectionRuleSetSerializer.TryDeserialize(InspectionRuleSetJson, out InspectionRuleSet ruleSet, out string errorMessage))
            {
                throw new InvalidOperationException($"判定规则配置 JSON 无效: {errorMessage}");
            }

            if (ruleSet.Rules.Count == 0)
            {
                ruleSet = WireSequenceJudgeEnabled
                    ? InspectionRuleSetSerializer.FromLegacyWireSequence(
                        WireSequenceExpectedLabels,
                        WireSequenceSortBy,
                        WireSequenceDirection,
                        WireSequenceExpectedCount,
                        WireSequenceMinConfidence,
                        WireSequenceAllowMissing,
                        WireSequenceAllowDuplicate,
                        TargetLabel,
                        TargetCount)
                    : InspectionRuleSetSerializer.FromLegacyTarget(TargetLabel, TargetCount);
            }

            InspectionRuleSetJson = InspectionRuleSetSerializer.Serialize(ruleSet);
        }

        private void NormalizeCameraPixelFormats()
        {
            if (Cameras == null)
            {
                return;
            }

            foreach (var camera in Cameras)
            {
                camera.PixelFormat = NormalizeCameraPixelFormat(camera.PixelFormat);
            }
        }

        private static string NormalizeCameraPixelFormat(string? value)
        {
            string raw = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Auto";
            }

            string normalized = raw
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);

            if (string.Equals(normalized, "BGR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Color", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Colour", StringComparison.OrdinalIgnoreCase))
            {
                return "BGR8";
            }

            if (string.Equals(normalized, "RGB", StringComparison.OrdinalIgnoreCase))
            {
                return "RGB8";
            }

            if (string.Equals(normalized, "BayerRG", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerRG8";
            }

            if (string.Equals(normalized, "BayerGB", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerGB8";
            }

            if (string.Equals(normalized, "BayerGR", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerGR8";
            }

            if (string.Equals(normalized, "BayerBG", StringComparison.OrdinalIgnoreCase))
            {
                return "BayerBG8";
            }

            string[] allowed =
            {
                "Auto",
                "Mono8",
                "BGR8",
                "RGB8",
                "BayerRG8",
                "BayerGB8",
                "BayerGR8",
                "BayerBG8"
            };

            foreach (string format in allowed)
            {
                if (string.Equals(format, raw, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }

            return "Auto";
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
            PlcTriggerSeqAddress = NormalizePlcAddressOrDefault(protocolType, PlcTriggerSeqAddress, 557);
            PlcResultSeqAddress = NormalizePlcAddressOrDefault(protocolType, PlcResultSeqAddress, 558);
            PlcVisionOnlineAddress = NormalizePlcAddressOrDefault(protocolType, PlcVisionOnlineAddress, 559);
            PlcVisionReadyAddress = NormalizePlcAddressOrDefault(protocolType, PlcVisionReadyAddress, 560);
            PlcVisionBusyAddress = NormalizePlcAddressOrDefault(protocolType, PlcVisionBusyAddress, 561);
            PlcInspectionDoneAddress = NormalizePlcAddressOrDefault(protocolType, PlcInspectionDoneAddress, 562);
            PlcErrorCodeAddress = NormalizePlcAddressOrDefault(protocolType, PlcErrorCodeAddress, 563);
            PlcTraceSavedAddress = NormalizePlcAddressOrDefault(protocolType, PlcTraceSavedAddress, 564);
            PlcHeartbeatAddress = NormalizePlcAddressOrDefault(protocolType, PlcHeartbeatAddress, 565);
            PlcResetFaultAddress = NormalizePlcAddressOrDefault(protocolType, PlcResetFaultAddress, 566);
            BarcodeAddress = NormalizePlcAddressOrDefault(protocolType, BarcodeAddress, 570);
            BarcodeWordLength = Math.Clamp(BarcodeWordLength, 1, 64);
            if (string.IsNullOrWhiteSpace(BarcodeEncoding))
            {
                BarcodeEncoding = "ASCII";
            }

            if (!IsMitsubishiProtocol(protocolType) &&
                string.Equals(PlcDriverProvider, "McpX", StringComparison.OrdinalIgnoreCase))
            {
                PlcDriverProvider = "HaoCommunication";
            }

            if (string.IsNullOrWhiteSpace(PlcSiemensCpuModel))
            {
                PlcSiemensCpuModel = "S1200";
            }
        }

        private static string NormalizePlcAddressOrDefault(PlcProtocolType protocolType, string address, int defaultNumber)
        {
            return PlcAddressNormalizer.MigrateLegacyAddress(
                address,
                protocolType,
                GetProtocolDefaultAddress(protocolType, defaultNumber));
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

        private static IEnumerable<string> GetReadableConfigPaths()
        {
            if (File.Exists(ConfigPath))
            {
                yield return ConfigPath;
            }

            if (File.Exists(ConfigBackupPath))
            {
                yield return ConfigBackupPath;
            }

            if (File.Exists(LegacySharedConfigPath))
            {
                yield return LegacySharedConfigPath;
            }

            if (File.Exists(BundledConfigPath))
            {
                yield return BundledConfigPath;
            }
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            return new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true
            };
        }

        private static void WriteConfigAtomically(string targetPath, string json)
        {
            string configDir = Path.GetDirectoryName(targetPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            string tempPath = Path.Combine(
                string.IsNullOrWhiteSpace(configDir) ? "." : configDir,
                $"config.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, ConfigBackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    LogError("CleanupTempConfig", ex);
                }
            }
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
