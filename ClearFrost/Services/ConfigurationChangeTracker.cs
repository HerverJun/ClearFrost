// ============================================================================
// 文件名: ConfigurationChangeTracker.cs
// 描述:   关键配置变更快照与差异摘要
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClearFrost.Config;

namespace ClearFrost.Services
{
    public sealed class ConfigurationChange
    {
        public string Key { get; init; } = string.Empty;
        public string Before { get; init; } = string.Empty;
        public string After { get; init; } = string.Empty;
    }

    public sealed class ConfigurationSnapshot
    {
        public ConfigurationSnapshot(IReadOnlyDictionary<string, string> values)
        {
            Values = values ?? throw new ArgumentNullException(nameof(values));
        }

        public IReadOnlyDictionary<string, string> Values { get; }

        public IReadOnlyList<ConfigurationChange> CompareTo(ConfigurationSnapshot other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            var keys = Values.Keys
                .Concat(other.Values.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            var changes = new List<ConfigurationChange>();

            foreach (string key in keys)
            {
                string before = Values.TryGetValue(key, out string? beforeValue) ? beforeValue : string.Empty;
                string after = other.Values.TryGetValue(key, out string? afterValue) ? afterValue : string.Empty;
                if (string.Equals(before, after, StringComparison.Ordinal))
                {
                    continue;
                }

                changes.Add(new ConfigurationChange
                {
                    Key = key,
                    Before = before,
                    After = after
                });
            }

            return changes;
        }
    }

    public static class ConfigurationChangeTracker
    {
        private const int MaxValueLength = 120;

        public static ConfigurationSnapshot Capture(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["Storage.Path"] = config.StoragePath,
                ["Storage.RetentionEnabled"] = config.DataRetentionEnabled.ToString(),
                ["Storage.ImageRetentionDays"] = config.ImageRetentionDays.ToString(CultureInfo.InvariantCulture),
                ["Storage.LogRetentionDays"] = config.LogRetentionDays.ToString(CultureInfo.InvariantCulture),
                ["Storage.AuditLogRetentionDays"] = config.AuditLogRetentionDays.ToString(CultureInfo.InvariantCulture),
                ["Storage.ReportRetentionDays"] = config.ReportRetentionDays.ToString(CultureInfo.InvariantCulture),
                ["Storage.TraceRecordRetentionDays"] = config.TraceRecordRetentionDays.ToString(CultureInfo.InvariantCulture),
                ["Production.RequireOperatorForStart"] = config.RequireOperatorForProductionStart.ToString(),
                ["Production.OperatorSessionMaxHours"] = config.OperatorSessionMaxHours.ToString(CultureInfo.InvariantCulture),
                ["Trigger.Source"] = config.TriggerSource.ToString(),
                ["Trigger.SerialPort"] = config.SerialPhotoelectricPortName,
                ["Trigger.SerialBaudRate"] = config.SerialPhotoelectricBaudRate.ToString(CultureInfo.InvariantCulture),
                ["Trigger.SerialDebounceMs"] = config.SerialPhotoelectricDebounceMs.ToString(CultureInfo.InvariantCulture),
                ["PLC.DriverProvider"] = config.PlcDriverProvider,
                ["PLC.Protocol"] = config.PlcProtocol,
                ["PLC.ProtocolMode"] = config.PlcProtocolMode.ToString(),
                ["PLC.Endpoint"] = $"{config.PlcIp}:{config.PlcPort}",
                ["PLC.TriggerAddress"] = config.PlcTriggerAddress,
                ["PLC.ResultAddress"] = config.PlcResultAddress,
                ["PLC.TriggerSeqAddress"] = config.PlcTriggerSeqAddress,
                ["PLC.ResultSeqAddress"] = config.PlcResultSeqAddress,
                ["PLC.OkValue"] = config.PlcOkValue.ToString(CultureInfo.InvariantCulture),
                ["PLC.NgValue"] = config.PlcNgValue.ToString(CultureInfo.InvariantCulture),
                ["Barcode.Enabled"] = config.BarcodeEnabled.ToString(),
                ["Barcode.Required"] = config.BarcodeRequired.ToString(),
                ["Barcode.Address"] = config.BarcodeAddress,
                ["Barcode.WordLength"] = config.BarcodeWordLength.ToString(CultureInfo.InvariantCulture),
                ["Barcode.Encoding"] = config.BarcodeEncoding,
                ["Camera.ActiveId"] = config.ActiveCameraId,
                ["Model.Current"] = config.CurrentModelFileName,
                ["Model.Auxiliary1"] = config.Auxiliary1ModelPath,
                ["Model.Auxiliary2"] = config.Auxiliary2ModelPath,
                ["Model.FallbackEnabled"] = config.EnableMultiModelFallback.ToString(),
                ["Model.TaskType"] = config.TaskType.ToString(CultureInfo.InvariantCulture),
                ["Model.Confidence"] = config.Confidence.ToString("0.###", CultureInfo.InvariantCulture),
                ["Model.Iou"] = config.IouThreshold.ToString("0.###", CultureInfo.InvariantCulture),
                ["Model.EnableGpu"] = config.EnableGpu.ToString(),
                ["Model.GpuIndex"] = config.GpuIndex.ToString(CultureInfo.InvariantCulture),
                ["Model.StrictPackageMode"] = config.StrictModelPackageMode.ToString(),
                ["Vision.TargetLabel"] = config.TargetLabel,
                ["Vision.TargetCount"] = config.TargetCount.ToString(CultureInfo.InvariantCulture),
                ["Vision.MaxRetryCount"] = config.MaxRetryCount.ToString(CultureInfo.InvariantCulture),
                ["Vision.RetryIntervalMs"] = config.RetryIntervalMs.ToString(CultureInfo.InvariantCulture),
                ["PLC.WriteRetryCount"] = config.PlcWriteRetryCount.ToString(CultureInfo.InvariantCulture),
                ["PLC.WriteRetryIntervalMs"] = config.PlcWriteRetryIntervalMs.ToString(CultureInfo.InvariantCulture),
                ["Vision.CycleSlaEnabled"] = config.InspectionCycleSlaEnabled.ToString(),
                ["Vision.CycleWarningMs"] = config.InspectionCycleWarningMs.ToString(CultureInfo.InvariantCulture),
                ["Vision.CycleCriticalMs"] = config.InspectionCycleCriticalMs.ToString(CultureInfo.InvariantCulture),
                ["Vision.CycleMinSamples"] = config.InspectionCycleMinSamples.ToString(CultureInfo.InvariantCulture),
                ["Vision.QualityYieldSlaEnabled"] = config.QualityYieldSlaEnabled.ToString(),
                ["Vision.QualityYieldWarningPercent"] = config.QualityYieldWarningPercent.ToString("0.###", CultureInfo.InvariantCulture),
                ["Vision.QualityYieldCriticalPercent"] = config.QualityYieldCriticalPercent.ToString("0.###", CultureInfo.InvariantCulture),
                ["Vision.QualityYieldMinSamples"] = config.QualityYieldMinSamples.ToString(CultureInfo.InvariantCulture),
                ["Vision.ConsecutiveNgAlarmEnabled"] = config.ConsecutiveNgAlarmEnabled.ToString(),
                ["Vision.ConsecutiveNgWarningCount"] = config.ConsecutiveNgWarningCount.ToString(CultureInfo.InvariantCulture),
                ["Vision.ConsecutiveNgCriticalCount"] = config.ConsecutiveNgCriticalCount.ToString(CultureInfo.InvariantCulture),
                ["Vision.RuleSetHash"] = HashValue(config.InspectionRuleSetJson),
                ["WireSequence.Enabled"] = config.WireSequenceJudgeEnabled.ToString(),
                ["WireSequence.ExpectedLabels"] = config.WireSequenceExpectedLabels,
                ["WireSequence.SortBy"] = config.WireSequenceSortBy,
                ["WireSequence.Direction"] = config.WireSequenceDirection,
                ["WireSequence.ExpectedCount"] = config.WireSequenceExpectedCount.ToString(CultureInfo.InvariantCulture),
                ["Render.Industrial"] = config.IndustrialRenderMode.ToString(),
                ["Render.FileBackedTransport"] = config.UseFileBackedWebImageTransport.ToString()
            };

            CameraConfig? activeCamera = config.ActiveCamera;
            if (activeCamera != null)
            {
                values["Camera.DisplayName"] = activeCamera.DisplayName;
                values["Camera.SerialNumber"] = activeCamera.SerialNumber;
                values["Camera.Manufacturer"] = activeCamera.Manufacturer;
                values["Camera.PixelFormat"] = activeCamera.PixelFormat;
                values["Camera.ExposureTime"] = activeCamera.ExposureTime.ToString("0.###", CultureInfo.InvariantCulture);
                values["Camera.Gain"] = activeCamera.Gain.ToString("0.###", CultureInfo.InvariantCulture);
            }

            return new ConfigurationSnapshot(values);
        }

        public static string FormatChanges(IEnumerable<ConfigurationChange> changes, int maxItems = 24)
        {
            ConfigurationChange[] items = (changes ?? Array.Empty<ConfigurationChange>()).ToArray();
            if (items.Length == 0)
            {
                return "Changes=0";
            }

            int take = Math.Clamp(maxItems <= 0 ? 24 : maxItems, 1, 100);
            var parts = items.Take(take)
                .Select(change => $"{change.Key}: '{Compact(change.Before)}' -> '{Compact(change.After)}'");
            string suffix = items.Length > take ? $"; ... +{items.Length - take} more" : string.Empty;
            return $"Changes={items.Length}; {string.Join("; ", parts)}{suffix}";
        }

        private static string Compact(string? value)
        {
            string normalized = string.IsNullOrWhiteSpace(value)
                ? "-"
                : value
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Replace('\t', ' ')
                    .Trim();

            return normalized.Length <= MaxValueLength
                ? normalized
                : normalized[..MaxValueLength] + "...";
        }

        private static string HashValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "-";
            }

            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }
    }
}
