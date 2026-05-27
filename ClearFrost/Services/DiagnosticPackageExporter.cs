using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        public AlarmSnapshot? AlarmSnapshot { get; init; }
        public IReadOnlyList<DetectionRecord> RecentRecords { get; init; } = Array.Empty<DetectionRecord>();
        public string LogsDirectory { get; init; } = RuntimePaths.LogsDirectory;
    }

    public sealed class DiagnosticPackageManifest
    {
        public string Schema { get; init; } = "ClearFrost.DiagnosticPackage.v1";
        public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
        public int EntryCount { get; init; }
        public IReadOnlyList<DiagnosticPackageEntryManifest> Entries { get; init; } = Array.Empty<DiagnosticPackageEntryManifest>();
    }

    public sealed class DiagnosticPackageEntryManifest
    {
        public string Path { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }

    public sealed class DiagnosticAuditIntegritySummary
    {
        public int TotalRecords { get; init; }
        public int ValidRecords { get; init; }
        public int TamperedRecords { get; init; }
        public int LegacyRecords { get; init; }
        public IReadOnlyList<DiagnosticAuditIntegrityFinding> Findings { get; init; } = Array.Empty<DiagnosticAuditIntegrityFinding>();
    }

    public sealed class DiagnosticAuditIntegrityFinding
    {
        public DateTime Timestamp { get; init; }
        public string IntegrityStatus { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string SourceFile { get; init; } = string.Empty;
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
            var manifestEntries = new List<DiagnosticPackageEntryManifest>();

            manifestEntries.Add(AddText(archive, "config.sanitized.json", BuildSanitizedConfigJson(request.AppConfig)));
            manifestEntries.Add(AddJson(archive, "recipe.json", request.Recipe));
            manifestEntries.Add(AddJson(archive, "model_registry.json", request.ModelEntries));
            manifestEntries.Add(AddJson(archive, "startup_diagnostics.json", request.StartupDiagnostics));
            manifestEntries.Add(AddJson(archive, "health.json", request.HealthSnapshot));
            manifestEntries.Add(AddJson(archive, "alarms.json", request.AlarmSnapshot));
            manifestEntries.Add(AddJson(archive, "recent_records.json", request.RecentRecords));
            manifestEntries.Add(AddJson(archive, "audit_integrity_summary.json", BuildAuditIntegritySummary(request.LogsDirectory)));
            manifestEntries.Add(AddText(archive, "system_info.txt", BuildSystemInfo()));
            manifestEntries.AddRange(await AddLogsAsync(archive, request.LogsDirectory, cancellationToken).ConfigureAwait(false));
            manifestEntries.Add(AddJson(archive, "package_manifest.json", new DiagnosticPackageManifest
            {
                GeneratedAt = DateTimeOffset.Now,
                EntryCount = manifestEntries.Count,
                Entries = manifestEntries
            }));

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

        private static DiagnosticAuditIntegritySummary BuildAuditIntegritySummary(string logsDirectory)
        {
            IReadOnlyList<AuditLogRecord> records = AuditLogReader.Read(logsDirectory, new AuditLogQuery { Limit = 2000 });
            return new DiagnosticAuditIntegritySummary
            {
                TotalRecords = records.Count,
                ValidRecords = records.Count(record => string.Equals(record.IntegrityStatus, AuditLogIntegrity.ValidStatus, StringComparison.Ordinal)),
                TamperedRecords = records.Count(record => string.Equals(record.IntegrityStatus, AuditLogIntegrity.TamperedStatus, StringComparison.Ordinal)),
                LegacyRecords = records.Count(record => string.Equals(record.IntegrityStatus, AuditLogIntegrity.LegacyStatus, StringComparison.Ordinal)),
                Findings = records
                    .Where(record => !string.Equals(record.IntegrityStatus, AuditLogIntegrity.ValidStatus, StringComparison.Ordinal))
                    .Take(50)
                    .Select(record => new DiagnosticAuditIntegrityFinding
                    {
                        Timestamp = record.Timestamp,
                        IntegrityStatus = record.IntegrityStatus,
                        Category = record.Category,
                        Action = record.Action,
                        Detail = record.Detail,
                        SourceFile = record.SourceFile
                    })
                    .ToArray()
            };
        }

        private static async Task<IReadOnlyList<DiagnosticPackageEntryManifest>> AddLogsAsync(
            ZipArchive archive,
            string logsDirectory,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(logsDirectory) || !Directory.Exists(logsDirectory))
            {
                return Array.Empty<DiagnosticPackageEntryManifest>();
            }

            var files = Directory.EnumerateFiles(logsDirectory, "*.*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(info => IsAllowedLogFile(info))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(MaxLogFiles)
                .ToList();

            var entries = new List<DiagnosticPackageEntryManifest>();
            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(logsDirectory, file.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                string entryName = $"logs/{relative}";
                byte[] bytes = await ReadSharedFileBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                entries.Add(AddBytes(archive, entryName, bytes));
            }

            return entries;
        }

        private static async Task<byte[]> ReadSharedFileBytesAsync(string filePath, CancellationToken cancellationToken)
        {
            await using var source = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
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

        private static DiagnosticPackageEntryManifest AddJson<T>(ZipArchive archive, string entryName, T value)
        {
            string json = JsonSerializer.Serialize(value, JsonOptions);
            return AddText(archive, entryName, json);
        }

        private static DiagnosticPackageEntryManifest AddText(ZipArchive archive, string entryName, string content)
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return AddBytes(archive, entryName, encoding.GetPreamble().Concat(encoding.GetBytes(content ?? string.Empty)).ToArray());
        }

        private static DiagnosticPackageEntryManifest AddBytes(ZipArchive archive, string entryName, byte[] content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            stream.Write(content, 0, content.Length);
            return new DiagnosticPackageEntryManifest
            {
                Path = entryName,
                SizeBytes = content.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
            };
        }
    }
}
