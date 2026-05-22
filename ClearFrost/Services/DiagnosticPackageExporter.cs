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

            AddText(archive, "config.sanitized.json", BuildSanitizedConfigJson(request.AppConfig));
            AddJson(archive, "recipe.json", request.Recipe);
            AddJson(archive, "model_registry.json", request.ModelEntries);
            AddJson(archive, "startup_diagnostics.json", request.StartupDiagnostics);
            AddJson(archive, "health.json", request.HealthSnapshot);
            AddJson(archive, "recent_records.json", request.RecentRecords);
            AddText(archive, "system_info.txt", BuildSystemInfo());
            await AddLogsAsync(archive, request.LogsDirectory, cancellationToken).ConfigureAwait(false);

            return zipPath;
        }

        private static string BuildSanitizedConfigJson(AppConfig config)
        {
            JsonNode? node = JsonSerializer.SerializeToNode(config, JsonOptions);
            return node?.ToJsonString(JsonOptions) ?? "{}";
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
                fullPath.Contains(".onnx"))
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
