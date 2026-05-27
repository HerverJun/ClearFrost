using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Hardware;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using Microsoft.Web.WebView2.Core;

namespace ClearFrost.Services
{
    public enum StartupDiagnosticStatus
    {
        Pass,
        Warning,
        Fail
    }

    public sealed class StartupDiagnosticItem
    {
        public string Name { get; init; } = string.Empty;
        public StartupDiagnosticStatus Status { get; init; }
        public string Message { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
        public bool IsBlocking { get; init; }
    }

    public sealed class StartupDiagnosticReport
    {
        public IReadOnlyList<StartupDiagnosticItem> Items { get; init; } = Array.Empty<StartupDiagnosticItem>();
        public bool IsReady => Items.All(i => i.Status != StartupDiagnosticStatus.Fail || !i.IsBlocking);
        public int BlockingFailureCount => Items.Count(i => i.Status == StartupDiagnosticStatus.Fail && i.IsBlocking);
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    }

    public sealed class StartupDiagnostics
    {
        public StartupDiagnosticReport CurrentReport { get; private set; } = new StartupDiagnosticReport();

        public StartupDiagnosticReport Run(
            AppConfig config,
            IStorageService storageService,
            ModelRegistry? modelRegistry = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (storageService == null) throw new ArgumentNullException(nameof(storageService));

            string resolvedStoragePath = StorageService.ResolveStoragePath(config.StoragePath);
            var items = new List<StartupDiagnosticItem>
            {
                CheckWebView2Runtime(),
                CheckNativeDll("OpenCV native dll", "OpenCvSharpExtern.dll", isBlocking: false),
                CheckNativeDll("Camera SDK dll", "MVSDK_Net.dll", isBlocking: false),
                CheckWritableDirectory("Database directory", Path.GetDirectoryName(RuntimePaths.DatabasePath) ?? RuntimePaths.DataDirectory, isBlocking: true),
                CheckStoragePathResolution(config.StoragePath, resolvedStoragePath),
                CheckWritableDirectory("Storage directory", resolvedStoragePath, isBlocking: true),
                CheckWritableDirectory("Log directory", storageService.LogBasePath, isBlocking: true),
                CheckTriggerSourceConfig(config),
                CheckPlcAddresses(config),
                CheckVisionParameters(config),
                CheckCameraConfig(config),
                CheckDiskFreeSpace(resolvedStoragePath)
            };

            if (modelRegistry != null)
            {
                items.Add(CheckModelRegistry(modelRegistry));
                items.Add(CheckConfiguredModel(config, modelRegistry));
            }

            CurrentReport = new StartupDiagnosticReport
            {
                Items = items,
                UpdatedAt = DateTimeOffset.Now
            };
            return CurrentReport;
        }

        private static StartupDiagnosticItem CheckWebView2Runtime()
        {
            try
            {
                string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return Pass("WebView2 Runtime", $"WebView2 runtime detected: {version}", isBlocking: true);
            }
            catch (Exception ex)
            {
                return Fail("WebView2 Runtime", "WebView2 runtime is not available.", ex.Message, isBlocking: true);
            }
        }

        private static StartupDiagnosticItem CheckNativeDll(string name, string fileName, bool isBlocking)
        {
            string? path = FindNativeDll(fileName);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return Pass(name, $"{fileName} detected.", path, isBlocking);
            }

            return Warn(name, $"{fileName} was not found in the current runtime folders.", string.Empty, isBlocking);
        }

        private static StartupDiagnosticItem CheckWritableDirectory(string name, string path, bool isBlocking)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("Directory path is empty.");
                }

                Directory.CreateDirectory(path);
                string probe = Path.Combine(path, $".startup-diagnostics-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return Pass(name, "Writable.", Path.GetFullPath(path), isBlocking);
            }
            catch (Exception ex)
            {
                return Fail(name, "Directory is not writable.", ex.Message, isBlocking);
            }
        }

        private static StartupDiagnosticItem CheckStoragePathResolution(string configuredPath, string resolvedPath)
        {
            if (PathsEqual(configuredPath, resolvedPath))
            {
                return Pass("Storage path config", "Storage path resolved.", resolvedPath, isBlocking: false);
            }

            return Warn(
                "Storage path config",
                "Configured storage path is unavailable; runtime fallback is active.",
                $"{configuredPath} -> {resolvedPath}",
                isBlocking: false);
        }

        private static StartupDiagnosticItem CheckTriggerSourceConfig(AppConfig config)
        {
            try
            {
                switch (config.TriggerSource)
                {
                    case TriggerSource.PLC:
                        ValidatePlcEndpoint(config);
                        return Pass("Trigger source config", "PLC trigger source config is valid.", $"{config.PlcIp}:{config.PlcPort}", isBlocking: true);

                    case TriggerSource.SerialPhotoelectric:
                        if (string.IsNullOrWhiteSpace(config.SerialPhotoelectricPortName))
                        {
                            throw new InvalidOperationException("串口光电触发已启用，但 COM 口为空。");
                        }

                        if (config.SerialPhotoelectricBaudRate < 1200)
                        {
                            throw new InvalidOperationException("串口光电波特率不能低于 1200。");
                        }

                        if (config.SerialPhotoelectricDebounceMs < 0)
                        {
                            throw new InvalidOperationException("串口光电去抖时间不能为负数。");
                        }

                        if (config.SerialPhotoelectricTimeoutMs < 100)
                        {
                            throw new InvalidOperationException("串口光电读取超时不能低于 100 ms。");
                        }

                        return Pass("Trigger source config", "Serial photoelectric trigger config is valid.", config.SerialPhotoelectricPortName, isBlocking: true);

                    default:
                        throw new InvalidOperationException($"未知触发源: {config.TriggerSource}");
                }
            }
            catch (Exception ex)
            {
                return Fail("Trigger source config", "Trigger source validation failed.", ex.Message, isBlocking: true);
            }
        }

        private static void ValidatePlcEndpoint(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.PlcIp))
            {
                throw new InvalidOperationException("PLC IP/主机名为空。");
            }

            UriHostNameType hostNameType = Uri.CheckHostName(config.PlcIp.Trim());
            if (hostNameType == UriHostNameType.Unknown)
            {
                throw new InvalidOperationException($"PLC IP/主机名无效: {config.PlcIp}");
            }

            if (config.PlcPort < 1 || config.PlcPort > 65535)
            {
                throw new InvalidOperationException($"PLC 端口超出范围: {config.PlcPort}");
            }
        }

        private static StartupDiagnosticItem CheckVisionParameters(AppConfig config)
        {
            var failures = new List<string>();
            var warnings = new List<string>();

            if (!IsUnitInterval(config.Confidence))
            {
                failures.Add($"Confidence 必须在 0~1 之间，当前 {config.Confidence}。");
            }

            if (!IsUnitInterval(config.IouThreshold))
            {
                failures.Add($"IouThreshold 必须在 0~1 之间，当前 {config.IouThreshold}。");
            }

            if (config.TargetCount < 0)
            {
                failures.Add($"TargetCount 不能为负数，当前 {config.TargetCount}。");
            }

            if (config.MaxRetryCount < 0 || config.MaxRetryCount > 5)
            {
                warnings.Add($"MaxRetryCount 建议在 0~5 之间，运行时会夹紧，当前 {config.MaxRetryCount}。");
            }

            if (config.RetryIntervalMs < 0)
            {
                warnings.Add($"RetryIntervalMs 不能为负数，运行时会按 0 处理，当前 {config.RetryIntervalMs}。");
            }

            if (config.PlcWriteRetryCount < 0 || config.PlcWriteRetryCount > 5)
            {
                warnings.Add($"PlcWriteRetryCount 建议在 0~5 之间，运行时会夹紧，当前 {config.PlcWriteRetryCount}。");
            }

            if (config.PlcWriteRetryIntervalMs < 0)
            {
                warnings.Add($"PlcWriteRetryIntervalMs 不能为负数，运行时会按 0 处理，当前 {config.PlcWriteRetryIntervalMs}。");
            }

            if (failures.Count > 0)
            {
                return Fail("Vision parameter config", "Vision parameter validation failed.", string.Join(" ", failures), isBlocking: true);
            }

            if (warnings.Count > 0)
            {
                return Warn("Vision parameter config", "Vision parameters have compatibility warnings.", string.Join(" ", warnings), isBlocking: false);
            }

            return Pass("Vision parameter config", "Vision parameters are valid.", $"Confidence={config.Confidence:F3}, IoU={config.IouThreshold:F3}", isBlocking: false);
        }

        private static StartupDiagnosticItem CheckPlcAddresses(AppConfig config)
        {
            try
            {
                if (!PlcFactory.TryParseProtocol(config.PlcProtocol, out PlcProtocolType protocolType))
                {
                    throw new ArgumentException(
                        $"PLC 协议无效: {config.PlcProtocol}。支持: {string.Join(", ", Enum.GetNames<PlcProtocolType>())}");
                }

                string driverProvider = PlcFactory.NormalizeDriverProviderOrThrow(config.PlcDriverProvider);
                var addresses = new List<string>
                {
                    config.PlcTriggerAddress,
                    config.PlcResultAddress
                };

                if (config.PlcProtocolMode == PlcProtocolMode.HandshakeV1)
                {
                    addresses.AddRange(new[]
                    {
                        config.PlcTriggerSeqAddress,
                        config.PlcResultSeqAddress,
                        config.PlcVisionOnlineAddress,
                        config.PlcVisionReadyAddress,
                        config.PlcVisionBusyAddress,
                        config.PlcInspectionDoneAddress,
                        config.PlcErrorCodeAddress,
                        config.PlcTraceSavedAddress,
                        config.PlcHeartbeatAddress,
                        config.PlcResetFaultAddress
                    });
                }

                if (config.BarcodeEnabled)
                {
                    addresses.Add(config.BarcodeAddress);
                }

                foreach (string address in addresses)
                {
                    string normalized = PlcAddressNormalizer.NormalizeOrThrow(address, protocolType);
                    PlcAddressNormalizer.EnsureDriverSupportsAddress(
                        normalized,
                        protocolType,
                        driverProvider);
                }

                return Pass("PLC address config", "PLC addresses are valid.", config.PlcProtocolMode.ToString(), isBlocking: true);
            }
            catch (Exception ex)
            {
                return Fail("PLC address config", "PLC address validation failed.", ex.Message, isBlocking: true);
            }
        }

        private static StartupDiagnosticItem CheckCameraConfig(AppConfig config)
        {
            CameraConfig? active = config.ActiveCamera;
            if (active == null)
            {
                return Warn("Camera config", "No active camera is configured.", string.Empty, isBlocking: false);
            }

            if (string.IsNullOrWhiteSpace(active.SerialNumber))
            {
                return Warn("Camera config", "Active camera serial number is empty.", active.Id, isBlocking: false);
            }

            return Pass("Camera config", "Active camera config is present.", $"{active.DisplayName}/{active.SerialNumber}", isBlocking: false);
        }

        private static StartupDiagnosticItem CheckConfiguredModel(AppConfig config, ModelRegistry modelRegistry)
        {
            if (modelRegistry.Entries.Count == 0)
            {
                return Fail("Configured model", "No model entries are available for primary model resolution.", string.Empty, isBlocking: true);
            }

            string configuredModel = config.CurrentModelFileName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredModel))
            {
                return Warn(
                    "Configured model",
                    "No primary model is configured; startup will auto-select the first available model.",
                    string.Empty,
                    isBlocking: false);
            }

            ModelRegistryEntry? entry = modelRegistry.Resolve(configuredModel);
            if (entry == null)
            {
                return Warn(
                    "Configured model",
                    "Configured primary model was not found in the model registry.",
                    configuredModel,
                    isBlocking: false);
            }

            if (entry.Status == ModelRegistryStatus.Blocked)
            {
                return Fail(
                    "Configured model",
                    "Configured primary model is blocked.",
                    $"{entry.ModelId}: {entry.Message}",
                    isBlocking: true);
            }

            if (entry.Status == ModelRegistryStatus.Warning)
            {
                return Warn(
                    "Configured model",
                    "Configured primary model has compatibility warnings.",
                    $"{entry.ModelId}: {entry.Message}",
                    isBlocking: false);
            }

            return Pass("Configured model", "Configured primary model is ready.", $"{entry.ModelId}/{entry.Version}", isBlocking: true);
        }

        private static bool IsUnitInterval(float value)
        {
            return !float.IsNaN(value) &&
                   !float.IsInfinity(value) &&
                   value >= 0f &&
                   value <= 1f;
        }

        private static StartupDiagnosticItem CheckDiskFreeSpace(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(root))
                {
                    throw new InvalidOperationException("Cannot resolve storage drive.");
                }

                var drive = new DriveInfo(root);
                double freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                if (freeGb < 0.5)
                {
                    return Fail("Disk free space", "Available disk space is critically low.", $"{freeGb:F2} GB", isBlocking: true);
                }

                if (freeGb < 2)
                {
                    return Warn("Disk free space", "Available disk space is low.", $"{freeGb:F2} GB", isBlocking: false);
                }

                return Pass("Disk free space", "Disk free space is sufficient.", $"{freeGb:F2} GB", isBlocking: false);
            }
            catch (Exception ex)
            {
                return Fail("Disk free space", "Unable to check disk free space.", ex.Message, isBlocking: true);
            }
        }

        private static StartupDiagnosticItem CheckModelRegistry(ModelRegistry modelRegistry)
        {
            if (modelRegistry.Entries.Count == 0)
            {
                return Fail("Model registry", "No model entries were discovered.", string.Empty, isBlocking: true);
            }

            if (modelRegistry.HasBlockingErrors)
            {
                string details = string.Join(
                    Environment.NewLine,
                    modelRegistry.Entries
                        .Where(e => e.Status == ModelRegistryStatus.Blocked)
                        .Select(e => $"{e.ModelId}: {e.Message}"));
                return Fail("Model registry", "Blocking model package errors exist.", details, isBlocking: true);
            }

            int warningCount = modelRegistry.Entries.Count(e => e.Status == ModelRegistryStatus.Warning);
            if (warningCount > 0)
            {
                return Warn("Model registry", $"Model registry has {warningCount} compatibility warnings.", string.Empty, isBlocking: false);
            }

            return Pass("Model registry", "Model registry is ready.", $"{modelRegistry.Entries.Count} entries", isBlocking: true);
        }

        private static string? FindNativeDll(string fileName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, fileName),
                Path.Combine(baseDir, "x64", fileName),
                Path.Combine(baseDir, "runtimes", "win-x64", "native", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left ?? string.Empty),
                    Path.GetFullPath(right ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static StartupDiagnosticItem Pass(string name, string message, string details = "", bool isBlocking = false)
        {
            return new StartupDiagnosticItem
            {
                Name = name,
                Status = StartupDiagnosticStatus.Pass,
                Message = message,
                Details = details,
                IsBlocking = isBlocking
            };
        }

        private static StartupDiagnosticItem Warn(string name, string message, string details = "", bool isBlocking = false)
        {
            return new StartupDiagnosticItem
            {
                Name = name,
                Status = StartupDiagnosticStatus.Warning,
                Message = message,
                Details = details,
                IsBlocking = isBlocking
            };
        }

        private static StartupDiagnosticItem Fail(string name, string message, string details, bool isBlocking)
        {
            Debug.WriteLine($"[StartupDiagnostics] {name}: {message} {details}");
            return new StartupDiagnosticItem
            {
                Name = name,
                Status = StartupDiagnosticStatus.Fail,
                Message = message,
                Details = details,
                IsBlocking = isBlocking
            };
        }
    }
}
