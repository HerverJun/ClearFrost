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
using ClearFrost.Yolo;
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
            ModelRegistry? modelRegistry = null,
            Func<ModelRole, ModelRegistryEntry, ProductionModelReference, ProductionModelReadinessResult>? approvalEvidenceValidator = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (storageService == null) throw new ArgumentNullException(nameof(storageService));

            string operationalStoragePath = ResolveOperationalStoragePath(config, storageService);
            var items = new List<StartupDiagnosticItem>
            {
                CheckWebView2Runtime(),
                CheckNativeDll("OpenCV native dll", "OpenCvSharpExtern.dll", isBlocking: false),
                CheckNativeDll("Camera SDK dll", "MVSDK_Net.dll", isBlocking: false),
                CheckWritableDirectory("Database directory", Path.GetDirectoryName(RuntimePaths.DatabasePath) ?? RuntimePaths.DataDirectory, isBlocking: true),
                CheckWritableDirectory("Storage directory", operationalStoragePath, isBlocking: true),
                CheckWritableDirectory("Log directory", storageService.LogBasePath, isBlocking: true),
                CheckWritableDirectory("System evidence directory", storageService.SystemPath, isBlocking: true),
                CheckWritableDirectory("Audit outbox directory", Path.Combine(storageService.LogBasePath, "Outbox"), isBlocking: true),
                CheckWritableDirectory("Diagnostic package directory", Path.Combine(storageService.LogBasePath, "Diagnostics"), isBlocking: false),
                CheckWritableDirectory("Handoff report directory", Path.Combine(storageService.LogBasePath, "HandoffReports"), isBlocking: false),
                CheckPlcAddresses(config),
                CheckCameraConfig(config),
                CheckDiskFreeSpace(operationalStoragePath)
            };

            if (modelRegistry != null)
            {
                items.Add(CheckModelRegistry(modelRegistry));
                if (config.RequireApprovedModelsForProduction)
                {
                    items.Add(CheckReplayEvidenceGate(config, modelRegistry, approvalEvidenceValidator));
                }
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

        private static string ResolveOperationalStoragePath(AppConfig config, IStorageService storageService)
        {
            return storageService.BaseStoragePath;
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

                string fullPath = Path.GetFullPath(path);
                EnsureProbeDirectorySafe(fullPath);
                Directory.CreateDirectory(fullPath);
                EnsureProbeDirectorySafe(fullPath);
                string probe = Path.Combine(fullPath, $".startup-diagnostics-{Guid.NewGuid():N}.tmp");
                WriteAndDeleteProbeFile(probe);
                return Pass(name, "Writable.", fullPath, isBlocking);
            }
            catch (Exception ex)
            {
                return Fail(name, "Directory is not writable.", ex.Message, isBlocking);
            }
        }

        private static void EnsureProbeDirectorySafe(string directory)
        {
            if (DirectoryPathHasReparsePoint(directory))
            {
                throw new IOException($"Directory contains a linked path segment: {directory}");
            }
        }

        private static void WriteAndDeleteProbeFile(string probePath)
        {
            using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write("ok");
            }

            var probe = new FileInfo(probePath);
            probe.Refresh();
            if (probe.Exists && HasReparsePoint(probe))
            {
                throw new IOException($"Probe file is a linked file: {probePath}");
            }

            File.Delete(probePath);
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null)
            {
                current.Refresh();
                if (current.Exists && HasReparsePoint(current))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static StartupDiagnosticItem CheckPlcAddresses(AppConfig config)
        {
            if (config.TriggerSource != TriggerSource.PLC)
            {
                return Pass(
                    "PLC address config",
                    "PLC address validation skipped because trigger source is not PLC.",
                    config.TriggerSource.ToString(),
                    isBlocking: false);
            }

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
                        config.PlcResetFaultAddress,
                        config.PlcTriggerAckAddress,
                        config.PlcResultValidAddress,
                        config.PlcResultAckAddress
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
                return Warn("Model registry", "No model entries were discovered; traceability metadata will be incomplete.", string.Empty, isBlocking: false);
            }

            if (modelRegistry.HasBlockingErrors)
            {
                string details = string.Join(
                    Environment.NewLine,
                    modelRegistry.Entries
                        .Where(e => e.Status == ModelRegistryStatus.Blocked)
                        .Select(e => $"{e.ModelId}: {e.Message}"));
                return Warn("Model registry", "Model package errors exist; traceability metadata will be incomplete.", details, isBlocking: false);
            }

            int warningCount = modelRegistry.Entries.Count(e => e.Status == ModelRegistryStatus.Warning);
            if (warningCount > 0)
            {
                return Warn("Model registry", $"Model registry has {warningCount} compatibility warnings.", string.Empty, isBlocking: false);
            }

            return Pass("Model registry", "Model registry is ready.", $"{modelRegistry.Entries.Count} entries", isBlocking: true);
        }

        private static StartupDiagnosticItem CheckReplayEvidenceGate(
            AppConfig config,
            ModelRegistry modelRegistry,
            Func<ModelRole, ModelRegistryEntry, ProductionModelReference, ProductionModelReadinessResult>? approvalEvidenceValidator)
        {
            if (approvalEvidenceValidator == null)
            {
                return Fail(
                    "Replay evidence gate",
                    "Replay evidence gate is not configured.",
                    "RequireApprovedModelsForProduction=true",
                    isBlocking: true);
            }

            foreach ((ModelRole Role, ProductionModelReference? Reference) slot in new[]
            {
                (ModelRole.Primary, config.CurrentModelReference),
                (ModelRole.Auxiliary1, config.Auxiliary1ModelReference),
                (ModelRole.Auxiliary2, config.Auxiliary2ModelReference)
            })
            {
                ProductionModelReference reference = slot.Reference?.Clone() ?? ProductionModelReference.Empty();
                if (reference.IsEmpty)
                {
                    if (slot.Role == ModelRole.Primary)
                    {
                        return Fail(
                            "Replay evidence gate",
                            "Primary model reference is empty.",
                            "RequireApprovedModelsForProduction=true",
                            isBlocking: true);
                    }

                    continue;
                }

                ProductionModelResolutionResult resolved = modelRegistry.ResolveReference(
                    reference,
                    requireProductionApproval: true);
                if (!resolved.Succeeded || resolved.Entry == null)
                {
                    return Fail(
                        "Replay evidence gate",
                        "Configured model reference cannot be resolved.",
                        $"{slot.Role}: {resolved.ErrorCode} {resolved.Message}",
                        isBlocking: true);
                }

                ProductionModelReadinessResult result = approvalEvidenceValidator(slot.Role, resolved.Entry, reference);
                if (!result.Succeeded)
                {
                    return Fail(
                        "Replay evidence gate",
                        "Approved model evidence validation failed.",
                        $"{slot.Role} {resolved.Entry.ModelId}/{resolved.Entry.Version}: {result.ErrorCode} {result.Message}",
                        isBlocking: true);
                }
            }

            return Pass("Replay evidence gate", "Approved model evidence validation passed.", string.Empty, isBlocking: true);
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
