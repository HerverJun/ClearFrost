using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClearFrost.Config;

namespace ClearFrost.Core.Recipes
{
    /// <summary>
    /// Production recipe snapshot derived from AppConfig plus runtime ROI. Detection records keep only RecipeId/Version.
    /// </summary>
    public sealed class Recipe
    {
        public string RecipeId { get; set; } = "default";
        public string Version { get; set; } = "1";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public string TargetLabel { get; set; } = string.Empty;
        public int TargetCount { get; set; }
        public float Confidence { get; set; }
        public float IouThreshold { get; set; }
        public bool EnableGlobalIou { get; set; }
        public string CurrentModelFileName { get; set; } = string.Empty;
        public string Auxiliary1ModelPath { get; set; } = string.Empty;
        public string Auxiliary2ModelPath { get; set; } = string.Empty;
        public bool EnableMultiModelFallback { get; set; }
        public bool EnableGpu { get; set; }
        public int GpuIndex { get; set; }
        public int TaskType { get; set; }
        public bool EnablePreprocessing { get; set; }
        public bool IndustrialRenderMode { get; set; }
        public string InspectionRuleSetJson { get; set; } = string.Empty;
        public int VisionMode { get; set; }
        public string TemplateImagePath { get; set; } = string.Empty;
        public double TemplateThreshold { get; set; }
        public string VisionPipelineJson { get; set; } = "[]";
        public float[]? Roi { get; set; }
        public string ActiveCameraId { get; set; } = string.Empty;
        public List<RecipeCameraSnapshot> Cameras { get; set; } = new();
        public RecipePlcSnapshot Plc { get; set; } = new();
        public RecipeBarcodeSnapshot Barcode { get; set; } = new();
        public RecipeTriggerSnapshot Trigger { get; set; } = new();

        public static Recipe FromAppConfig(AppConfig config, float[]? roi = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return new Recipe
            {
                RecipeId = "default",
                Version = DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                CreatedAt = DateTimeOffset.Now,
                TargetLabel = config.TargetLabel ?? string.Empty,
                TargetCount = config.TargetCount,
                Confidence = config.Confidence,
                IouThreshold = config.IouThreshold,
                EnableGlobalIou = config.EnableGlobalIou,
                CurrentModelFileName = config.CurrentModelFileName ?? string.Empty,
                Auxiliary1ModelPath = config.Auxiliary1ModelPath ?? string.Empty,
                Auxiliary2ModelPath = config.Auxiliary2ModelPath ?? string.Empty,
                EnableMultiModelFallback = config.EnableMultiModelFallback,
                EnableGpu = config.EnableGpu,
                GpuIndex = config.GpuIndex,
                TaskType = config.TaskType,
                EnablePreprocessing = config.EnablePreprocessing,
                IndustrialRenderMode = config.IndustrialRenderMode,
                InspectionRuleSetJson = config.InspectionRuleSetJson ?? string.Empty,
                VisionMode = config.VisionMode,
                TemplateImagePath = config.TemplateImagePath ?? string.Empty,
                TemplateThreshold = config.TemplateThreshold,
                VisionPipelineJson = config.VisionPipelineJson ?? "[]",
                Roi = NormalizeRoi(roi),
                ActiveCameraId = config.ActiveCameraId ?? string.Empty,
                Cameras = (config.Cameras ?? new List<CameraConfig>())
                    .Select(RecipeCameraSnapshot.FromCameraConfig)
                    .ToList(),
                Plc = RecipePlcSnapshot.FromAppConfig(config),
                Barcode = RecipeBarcodeSnapshot.FromAppConfig(config),
                Trigger = RecipeTriggerSnapshot.FromAppConfig(config)
            };
        }

        public float[]? GetRoiSnapshot()
        {
            return NormalizeRoi(Roi);
        }

        public static float[]? NormalizeRoi(float[]? roi)
        {
            if (roi == null || roi.Length != 4 ||
                roi.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            {
                return null;
            }

            float x = Math.Clamp(roi[0], 0f, 1f);
            float y = Math.Clamp(roi[1], 0f, 1f);
            float width = Math.Clamp(roi[2], 0f, 1f - x);
            float height = Math.Clamp(roi[3], 0f, 1f - y);

            if (width <= 0.001f || height <= 0.001f)
            {
                return null;
            }

            return new[] { x, y, width, height };
        }

        public static bool AreRoisEquivalent(float[]? left, float[]? right)
        {
            float[]? normalizedLeft = NormalizeRoi(left);
            float[]? normalizedRight = NormalizeRoi(right);
            if (normalizedLeft == null || normalizedRight == null)
            {
                return normalizedLeft == null && normalizedRight == null;
            }

            const float tolerance = 0.0005f;
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(normalizedLeft[i] - normalizedRight[i]) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class RecipeCameraSnapshot
    {
        public string Id { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double ExposureTime { get; set; }
        public double Gain { get; set; }
        public bool IsEnabled { get; set; }
        public string Manufacturer { get; set; } = string.Empty;
        public string PixelFormat { get; set; } = "Mono8";

        public static RecipeCameraSnapshot FromCameraConfig(CameraConfig camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            return new RecipeCameraSnapshot
            {
                Id = camera.Id ?? string.Empty,
                SerialNumber = camera.SerialNumber ?? string.Empty,
                DisplayName = camera.DisplayName ?? string.Empty,
                ExposureTime = camera.ExposureTime,
                Gain = camera.Gain,
                IsEnabled = camera.IsEnabled,
                Manufacturer = camera.Manufacturer ?? string.Empty,
                PixelFormat = string.IsNullOrWhiteSpace(camera.PixelFormat)
                    ? "Mono8"
                    : camera.PixelFormat
            };
        }
    }

    public sealed class RecipePlcSnapshot
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Protocol { get; set; } = string.Empty;
        public string DriverProvider { get; set; } = string.Empty;
        public string ProtocolMode { get; set; } = string.Empty;
        public string TriggerAddress { get; set; } = string.Empty;
        public string ResultAddress { get; set; } = string.Empty;
        public int TriggerDelayMs { get; set; }
        public int PollingIntervalMs { get; set; }
        public short OkValue { get; set; }
        public short NgValue { get; set; }
        public string TriggerSeqAddress { get; set; } = string.Empty;
        public string ResultSeqAddress { get; set; } = string.Empty;
        public string VisionOnlineAddress { get; set; } = string.Empty;
        public string VisionReadyAddress { get; set; } = string.Empty;
        public string VisionBusyAddress { get; set; } = string.Empty;
        public string InspectionDoneAddress { get; set; } = string.Empty;
        public string ErrorCodeAddress { get; set; } = string.Empty;
        public string TraceSavedAddress { get; set; } = string.Empty;
        public string HeartbeatAddress { get; set; } = string.Empty;
        public string ResetFaultAddress { get; set; } = string.Empty;
        public string SiemensCpuModel { get; set; } = string.Empty;
        public int SiemensRack { get; set; }
        public int SiemensSlot { get; set; }

        public static RecipePlcSnapshot FromAppConfig(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return new RecipePlcSnapshot
            {
                Ip = config.PlcIp ?? string.Empty,
                Port = config.PlcPort,
                Protocol = config.PlcProtocol ?? string.Empty,
                DriverProvider = config.PlcDriverProvider ?? string.Empty,
                ProtocolMode = config.PlcProtocolMode.ToString(),
                TriggerAddress = config.PlcTriggerAddress ?? string.Empty,
                ResultAddress = config.PlcResultAddress ?? string.Empty,
                TriggerDelayMs = config.PlcTriggerDelayMs,
                PollingIntervalMs = config.PlcPollingIntervalMs,
                OkValue = config.PlcOkValue,
                NgValue = config.PlcNgValue,
                TriggerSeqAddress = config.PlcTriggerSeqAddress ?? string.Empty,
                ResultSeqAddress = config.PlcResultSeqAddress ?? string.Empty,
                VisionOnlineAddress = config.PlcVisionOnlineAddress ?? string.Empty,
                VisionReadyAddress = config.PlcVisionReadyAddress ?? string.Empty,
                VisionBusyAddress = config.PlcVisionBusyAddress ?? string.Empty,
                InspectionDoneAddress = config.PlcInspectionDoneAddress ?? string.Empty,
                ErrorCodeAddress = config.PlcErrorCodeAddress ?? string.Empty,
                TraceSavedAddress = config.PlcTraceSavedAddress ?? string.Empty,
                HeartbeatAddress = config.PlcHeartbeatAddress ?? string.Empty,
                ResetFaultAddress = config.PlcResetFaultAddress ?? string.Empty,
                SiemensCpuModel = config.PlcSiemensCpuModel ?? string.Empty,
                SiemensRack = config.PlcSiemensRack,
                SiemensSlot = config.PlcSiemensSlot
            };
        }
    }

    public sealed class RecipeBarcodeSnapshot
    {
        public bool Enabled { get; set; }
        public string Address { get; set; } = string.Empty;
        public int WordLength { get; set; }
        public string Encoding { get; set; } = "ASCII";
        public bool Required { get; set; }

        public static RecipeBarcodeSnapshot FromAppConfig(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return new RecipeBarcodeSnapshot
            {
                Enabled = config.BarcodeEnabled,
                Address = config.BarcodeAddress ?? string.Empty,
                WordLength = config.BarcodeWordLength,
                Encoding = string.IsNullOrWhiteSpace(config.BarcodeEncoding)
                    ? "ASCII"
                    : config.BarcodeEncoding,
                Required = config.BarcodeRequired
            };
        }
    }

    public sealed class RecipeTriggerSnapshot
    {
        public string Source { get; set; } = string.Empty;
        public string SerialPhotoelectricPortName { get; set; } = string.Empty;
        public int SerialPhotoelectricBaudRate { get; set; }
        public int SerialPhotoelectricDebounceMs { get; set; }
        public int SerialPhotoelectricTimeoutMs { get; set; }

        public static RecipeTriggerSnapshot FromAppConfig(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            return new RecipeTriggerSnapshot
            {
                Source = config.TriggerSource.ToString(),
                SerialPhotoelectricPortName = config.SerialPhotoelectricPortName ?? string.Empty,
                SerialPhotoelectricBaudRate = config.SerialPhotoelectricBaudRate,
                SerialPhotoelectricDebounceMs = config.SerialPhotoelectricDebounceMs,
                SerialPhotoelectricTimeoutMs = config.SerialPhotoelectricTimeoutMs
            };
        }
    }
}
