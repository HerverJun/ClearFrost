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

            var items = new List<StartupDiagnosticItem>
            {
                CheckWebView2Runtime(),
                CheckNativeDll("OpenCV native dll", "OpenCvSharpExtern.dll", isBlocking: false),
                CheckNativeDll("Camera SDK dll", "MVSDK_Net.dll", isBlocking: false),
                CheckWritableDirectory("Database directory", Path.GetDirectoryName(RuntimePaths.DatabasePath) ?? RuntimePaths.DataDirectory, isBlocking: true),
                CheckWritableDirectory("Storage directory", config.StoragePath, isBlocking: true),
                CheckWritableDirectory("Log directory", storageService.LogBasePath, isBlocking: true),
                CheckPlcAddresses(config),
                CheckCameraConfig(config),
                CheckDiskFreeSpace(config.StoragePath)
            };

            if (modelRegistry != null)
            {
                items.Add(CheckModelRegistry(modelRegistry));
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

        private static StartupDiagnosticItem CheckPlcAddresses(AppConfig config)
        {
            try
            {
                PlcProtocolType protocolType = PlcFactory.ParseProtocol(config.PlcProtocol);
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
                        config.PlcDriverProvider);
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
