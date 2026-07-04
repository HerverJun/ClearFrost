using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    public sealed class DiagnosticPackageRequest
    {
        public string OutputDirectory { get; init; } = Path.Combine(RuntimePaths.DataDirectory, "Diagnostics");
        public AppConfig AppConfig { get; init; } = new AppConfig();
        public Recipe? Recipe { get; init; }
        public IReadOnlyList<ModelRegistryEntry> ModelEntries { get; init; } = Array.Empty<ModelRegistryEntry>();
        public StartupDiagnosticReport? StartupDiagnostics { get; init; }
        public HealthSnapshot? HealthSnapshot { get; init; }
        public FieldDiagnosticsSnapshot? FieldDiagnostics { get; init; }
        public IReadOnlyList<RecentInspectionTimingSnapshot> RecentInspectionTimings { get; init; } = Array.Empty<RecentInspectionTimingSnapshot>();
        public FieldModelProbeSummary? ModelProbeSummary { get; init; }
        public FieldQueueStatus? QueueStatus { get; init; }
        public IReadOnlyList<DetectionRecord> RecentRecords { get; init; } = Array.Empty<DetectionRecord>();
        public string LogsDirectory { get; init; } = RuntimePaths.LogsDirectory;
    }

    public sealed class DiagnosticPackageExporter
    {
        private const long MaxLogFileBytes = 2 * 1024 * 1024;
        private const int MaxLogFiles = 20;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public async Task<string> ExportAsync(
            DiagnosticPackageRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            Directory.CreateDirectory(request.OutputDirectory);
            string zipPath = Path.Combine(
                request.OutputDirectory,
                $"ClearFrost_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            FieldDiagnosticsSnapshot fieldDiagnostics = request.FieldDiagnostics ??
                BuildFallbackFieldDiagnostics(request);
            IReadOnlyList<RecentInspectionTimingSnapshot> recentTimings =
                request.RecentInspectionTimings.Count > 0
                    ? request.RecentInspectionTimings
                    : fieldDiagnostics.RecentInspectionTimings;

            AddText(archive, "config.sanitized.json", BuildSanitizedConfigJson(request.AppConfig));
            AddJson(archive, "recipe.json", request.Recipe);
            AddJson(archive, "model_registry.json", SanitizeModelEntries(request.ModelEntries));
            AddJson(archive, "startup_diagnostics.json", request.StartupDiagnostics);
            AddJson(archive, "health.json", request.HealthSnapshot);
            AddJson(archive, "field_diagnostics.json", fieldDiagnostics);
            AddJson(archive, "recent_inspection_timings.json", recentTimings);
            AddJson(archive, "recent_errors.json", fieldDiagnostics.RecentErrors);
            AddJson(archive, "model_probe_summary.json", request.ModelProbeSummary ?? fieldDiagnostics.ModelProbe);
            AddJson(archive, "queue_status.json", request.QueueStatus ?? fieldDiagnostics.Queues);
            AddJson(archive, "recent_records.json", SanitizeRecentRecords(request.RecentRecords));
            AddText(archive, "system_info.txt", BuildSystemInfo());
            AddText(archive, "native_dependencies.txt", BuildNativeDependencyManifest());
            await AddLogsAsync(archive, request.LogsDirectory, cancellationToken).ConfigureAwait(false);

            return zipPath;
        }

        private static FieldDiagnosticsSnapshot BuildFallbackFieldDiagnostics(DiagnosticPackageRequest request)
        {
            HealthSnapshot health = request.HealthSnapshot ?? new HealthSnapshot();
            FieldModelProbeSummary modelProbe = request.ModelProbeSummary ??
                FieldDiagnosticsSnapshotFactory.BuildModelProbeSummary(
                    health,
                    request.ModelEntries,
                    new DetectionRuntimeModelSnapshot(),
                    null,
                    null);
            FieldQueueStatus queueStatus = request.QueueStatus ??
                FieldDiagnosticsSnapshotFactory.BuildQueueStatus(health);
            IReadOnlyList<RecentInspectionTimingSnapshot> timings = request.RecentInspectionTimings.Count > 0
                ? request.RecentInspectionTimings
                : health.RecentInspectionTimings;

            FieldDiagnosticsSnapshot snapshot = FieldDiagnosticsSnapshotFactory.Create(
                health,
                request.StartupDiagnostics,
                request.ModelEntries,
                new DetectionRuntimeModelSnapshot(),
                modelProbe.CurrentModelName,
                null);

            return new FieldDiagnosticsSnapshot
            {
                UpdatedAt = snapshot.UpdatedAt,
                OverallLevel = snapshot.OverallLevel,
                CameraStatus = snapshot.CameraStatus,
                PlcStatus = snapshot.PlcStatus,
                CurrentModelName = snapshot.CurrentModelName,
                ModelStatus = snapshot.ModelStatus,
                StorageStatus = snapshot.StorageStatus,
                DatabaseStatus = snapshot.DatabaseStatus,
                LastInspectionId = snapshot.LastInspectionId,
                LastInspectionTotalMs = snapshot.LastInspectionTotalMs,
                RecentInspectionP95Ms = snapshot.RecentInspectionP95Ms,
                RecentInspectionP99Ms = snapshot.RecentInspectionP99Ms,
                ImageQueueLength = snapshot.ImageQueueLength,
                ImageQueueCapacity = snapshot.ImageQueueCapacity,
                RecordQueueLength = snapshot.RecordQueueLength,
                RecordQueueCapacity = snapshot.RecordQueueCapacity,
                FreeDiskGb = snapshot.FreeDiskGb,
                MemoryMb = snapshot.MemoryMb,
                HealthSnapshot = health,
                StartupDiagnostics = request.StartupDiagnostics,
                Queues = queueStatus,
                ModelProbe = modelProbe,
                Components = snapshot.Components,
                RecentInspectionTimings = timings,
                RecentErrors = health.RecentErrors
            };
        }

        private static string BuildSanitizedConfigJson(AppConfig config)
        {
            JsonNode? node = JsonSerializer.SerializeToNode(config, JsonOptions);
            if (node is JsonObject obj)
            {
                Redact(obj, nameof(AppConfig.CurrentOperatorId));
                Redact(obj, nameof(AppConfig.StoragePath));
            }

            return node?.ToJsonString(JsonOptions) ?? "{}";
        }

        private static IReadOnlyList<object> SanitizeModelEntries(IReadOnlyList<ModelRegistryEntry> entries)
        {
            return (entries ?? Array.Empty<ModelRegistryEntry>())
                .Select(entry => new
                {
                    entry.ModelId,
                    entry.Version,
                    entry.ModelHash,
                    ModelFileName = Path.GetFileName(entry.ModelPath ?? string.Empty),
                    entry.IsPackage,
                    entry.Status,
                    entry.Message,
                    entry.TaskType,
                    entry.InputWidth,
                    entry.InputHeight,
                    entry.ApprovalStatus,
                    entry.ApprovedForProduction
                })
                .Cast<object>()
                .ToList();
        }

        private static IReadOnlyList<object> SanitizeRecentRecords(IReadOnlyList<DetectionRecord> records)
        {
            return (records ?? Array.Empty<DetectionRecord>())
                .Select(record => new
                {
                    record.Id,
                    record.Timestamp,
                    record.IsQualified,
                    record.InspectionId,
                    record.TriggerSource,
                    ProductBarcode = Redacted(record.ProductBarcode),
                    Barcode = Redacted(record.Barcode),
                    record.TraceStatus,
                    record.QueueStatus,
                    ImagePath = Redacted(record.ImagePath),
                    RenderedImagePath = Redacted(record.RenderedImagePath),
                    TraceImagePath = Redacted(record.TraceImagePath),
                    record.ErrorStage,
                    record.ErrorCode,
                    record.ErrorMessage,
                    record.TotalMs,
                    record.RecipeId,
                    record.RecipeVersion,
                    record.ModelId,
                    record.ModelVersion,
                    record.ModelHash,
                    record.WasFallback,
                    record.UsedModelName,
                    record.TargetLabel,
                    record.ExpectedCount,
                    record.ActualCount,
                    record.InferenceMs,
                    record.ModelName,
                    record.CameraId,
                    record.RuleSummary
                })
                .Cast<object>()
                .ToList();
        }

        private static void Redact(JsonObject obj, string name)
        {
            if (obj.ContainsKey(name))
            {
                obj[name] = "<redacted>";
            }
        }

        private static string Redacted(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : "<redacted>";
        }

        private static string BuildSystemInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"GeneratedAt: {DateTimeOffset.Now:O}");
            sb.AppendLine($"MachineName: {Environment.MachineName}");
            sb.AppendLine($"OSVersion: {Environment.OSVersion}");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"BaseDirectory: {AppDomain.CurrentDomain.BaseDirectory}");
            sb.AppendLine($"WorkingSetMb: {Environment.WorkingSet / 1024 / 1024}");
            return sb.ToString();
        }

        private static string BuildNativeDependencyManifest()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] names =
            {
                "MVSDK_Net.dll",
                "HaoCommunication.dll",
                "MvCameraControl.dll",
                "MVSDKmd.dll"
            };

            var sb = new StringBuilder();
            sb.AppendLine($"BaseDirectory: {baseDirectory}");
            foreach (string name in names)
            {
                string path = Path.Combine(baseDirectory, name);
                if (!File.Exists(path))
                {
                    sb.AppendLine($"{name}: missing");
                    continue;
                }

                var info = new FileInfo(path);
                sb.AppendLine($"{name}: present, {info.Length} bytes, {info.LastWriteTimeUtc:O}");
            }

            return sb.ToString();
        }

        private static async Task AddLogsAsync(
            ZipArchive archive,
            string logsDirectory,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(logsDirectory) || !Directory.Exists(logsDirectory))
            {
                return;
            }

            var files = Directory.EnumerateFiles(logsDirectory, "*.*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(info => IsAllowedLogFile(info))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(MaxLogFiles)
                .ToList();

            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(logsDirectory, file.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                string entryName = $"logs/{relative}";
                ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                await using Stream entryStream = entry.Open();
                await using FileStream source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool IsAllowedLogFile(FileInfo info)
        {
            if (!info.Exists || info.Length > MaxLogFileBytes)
            {
                return false;
            }

            string extension = info.Extension.ToLowerInvariant();
            if (extension != ".log" && extension != ".txt" && extension != ".json")
            {
                return false;
            }

            string fullPath = info.FullName.ToLowerInvariant();
            if (fullPath.Contains($"{Path.DirectorySeparatorChar}images{Path.DirectorySeparatorChar}") ||
                fullPath.Contains(".onnx") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}outbox{Path.DirectorySeparatorChar}") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}replayevidence{Path.DirectorySeparatorChar}") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}replaydatasets{Path.DirectorySeparatorChar}") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}replayreports{Path.DirectorySeparatorChar}"))
            {
                return false;
            }

            return true;
        }

        private static void AddJson<T>(ZipArchive archive, string entryName, T value)
        {
            string json = JsonSerializer.Serialize(value, JsonOptions);
            AddText(archive, entryName, json);
        }

        private static void AddText(ZipArchive archive, string entryName, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write(content ?? string.Empty);
        }
    }
}
