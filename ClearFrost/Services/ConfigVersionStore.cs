// ============================================================================
// 文件名: ConfigVersionStore.cs
// 描述:   运行配置版本归档与恢复服务
//
// 功能:
//   - 为关键配置变更生成可恢复版本
//   - 校验版本文件完整性
//   - 支持按版本恢复运行配置
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClearFrost.Config;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    public sealed class ConfigVersionCreateOptions
    {
        public string Reason { get; init; } = string.Empty;
        public string OperatorName { get; init; } = string.Empty;
        public string OperatorRole { get; init; } = string.Empty;
        public string ShiftName { get; init; } = string.Empty;
        public string ChangeSummary { get; init; } = string.Empty;
        public DateTimeOffset? CreatedAt { get; init; }
    }

    public sealed class ConfigVersionEntry
    {
        public string VersionId { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public string Reason { get; init; } = string.Empty;
        public string OperatorName { get; init; } = string.Empty;
        public string OperatorRole { get; init; } = string.Empty;
        public string ShiftName { get; init; } = string.Empty;
        public string ChangeSummary { get; init; } = string.Empty;
        public string ConfigHash { get; init; } = string.Empty;
        public string ConfigPath { get; init; } = string.Empty;
        public string MetadataPath { get; init; } = string.Empty;
    }

    public sealed class ConfigVersionRestoreResult
    {
        public ConfigVersionEntry Version { get; init; } = new();
        public string RestoredConfigPath { get; init; } = string.Empty;
    }

    public sealed class ConfigVersionStore
    {
        private const string Schema = "ClearFrost.ConfigVersion.v1";
        private const int DefaultMaxVersions = 200;
        private readonly object _sync = new();
        private string _versionRoot;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public ConfigVersionStore(string systemPath)
        {
            if (string.IsNullOrWhiteSpace(systemPath))
            {
                throw new ArgumentException("系统目录不能为空", nameof(systemPath));
            }

            _versionRoot = Path.Combine(systemPath, "ConfigVersions");
            Directory.CreateDirectory(_versionRoot);
        }

        public string VersionRoot => _versionRoot;

        public void Reconfigure(string systemPath)
        {
            if (string.IsNullOrWhiteSpace(systemPath))
            {
                throw new ArgumentException("系统目录不能为空", nameof(systemPath));
            }

            lock (_sync)
            {
                _versionRoot = Path.Combine(systemPath, "ConfigVersions");
                Directory.CreateDirectory(_versionRoot);
            }
        }

        public ConfigVersionEntry EnsureBaseline(AppConfig config, OperatorSession? session = null)
        {
            ArgumentNullException.ThrowIfNull(config);

            lock (_sync)
            {
                ConfigVersionEntry? existing = ListVersionsCore(1).FirstOrDefault();
                if (existing != null)
                {
                    return existing;
                }

                return SaveVersionCore(config, new ConfigVersionCreateOptions
                {
                    Reason = "Baseline",
                    OperatorName = session?.OperatorName ?? "System",
                    OperatorRole = session?.Role ?? "System",
                    ShiftName = session?.ShiftName ?? string.Empty,
                    ChangeSummary = "Initial runtime configuration snapshot"
                });
            }
        }

        public ConfigVersionEntry SaveVersion(AppConfig config, ConfigVersionCreateOptions options)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(options);

            lock (_sync)
            {
                ConfigVersionEntry entry = SaveVersionCore(config, options);
                PruneCore(DefaultMaxVersions);
                return entry;
            }
        }

        public IReadOnlyList<ConfigVersionEntry> ListVersions(int limit = 100)
        {
            lock (_sync)
            {
                return ListVersionsCore(limit).ToArray();
            }
        }

        public AppConfig LoadConfig(string versionId)
        {
            lock (_sync)
            {
                ConfigVersionEntry entry = FindVersion(versionId);
                string json = ReadAndValidateConfig(entry);
                return AppConfig.FromJson(json);
            }
        }

        public ConfigVersionRestoreResult RestoreVersion(string versionId, AppConfig targetConfig)
        {
            ArgumentNullException.ThrowIfNull(targetConfig);

            lock (_sync)
            {
                ConfigVersionEntry entry = FindVersion(versionId);
                string json = ReadAndValidateConfig(entry);
                AppConfig restored = AppConfig.FromJson(json);
                AppConfig previousConfig = AppConfig.FromJson(targetConfig.ToPortableJson());
                FileSnapshot configSnapshot = FileSnapshot.Capture(RuntimePaths.ConfigPath);
                bool copied = false;

                try
                {
                    targetConfig.CopyFrom(restored);
                    copied = true;
                    if (!targetConfig.Save())
                    {
                        throw new InvalidOperationException(targetConfig.LastError ?? "配置保存失败");
                    }

                    return new ConfigVersionRestoreResult
                    {
                        Version = entry,
                        RestoredConfigPath = RuntimePaths.ConfigPath
                    };
                }
                catch (Exception ex)
                {
                    try
                    {
                        configSnapshot.Restore();
                        if (copied)
                        {
                            targetConfig.CopyFrom(previousConfig);
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        throw new InvalidOperationException(
                            $"配置版本恢复失败，且运行配置回滚失败: {rollbackEx.Message}",
                            new AggregateException(ex, rollbackEx));
                    }

                    throw;
                }
            }
        }

        private ConfigVersionEntry SaveVersionCore(AppConfig config, ConfigVersionCreateOptions options)
        {
            DateTimeOffset createdAt = options.CreatedAt ?? DateTimeOffset.Now;
            string reason = NormalizeText(options.Reason, "ConfigChange", 64);
            string versionId = BuildVersionId(createdAt, reason);
            string dayDirectory = Path.Combine(_versionRoot, createdAt.LocalDateTime.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture));
            string configFileName = $"{versionId}.config.json";
            string metadataFileName = $"{versionId}.meta.json";
            string configPath = Path.Combine(dayDirectory, configFileName);
            string metadataPath = Path.Combine(dayDirectory, metadataFileName);
            string configJson = config.ToPortableJson();
            string hash = ComputeSha256(configJson);

            var metadata = new ConfigVersionMetadata
            {
                Schema = Schema,
                VersionId = versionId,
                CreatedAt = createdAt,
                Reason = reason,
                OperatorName = NormalizeText(options.OperatorName, "System", 64),
                OperatorRole = NormalizeText(options.OperatorRole, "System", 32),
                ShiftName = NormalizeText(options.ShiftName, string.Empty, 32),
                ChangeSummary = NormalizeAuditLine(options.ChangeSummary),
                ConfigFileName = configFileName,
                ConfigHash = hash
            };

            AtomicFileWriter.WriteAllText(configPath, configJson);
            AtomicFileWriter.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
            return ToEntry(metadata, metadataPath);
        }

        private IReadOnlyList<ConfigVersionEntry> ListVersionsCore(int limit)
        {
            if (!Directory.Exists(_versionRoot))
            {
                return Array.Empty<ConfigVersionEntry>();
            }

            int safeLimit = limit <= 0 ? 100 : Math.Min(limit, 1000);
            return Directory.EnumerateFiles(_versionRoot, "*.meta.json", SearchOption.AllDirectories)
                .Select(TryReadEntry)
                .Where(entry => entry != null)
                .Select(entry => entry!)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(safeLimit)
                .ToArray();
        }

        private ConfigVersionEntry FindVersion(string versionId)
        {
            string normalized = (versionId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new ArgumentException("配置版本号不能为空", nameof(versionId));
            }

            ConfigVersionEntry? entry = ListVersionsCore(1000)
                .FirstOrDefault(item => string.Equals(item.VersionId, normalized, StringComparison.Ordinal));
            return entry ?? throw new FileNotFoundException($"未找到配置版本: {normalized}");
        }

        private string ReadAndValidateConfig(ConfigVersionEntry entry)
        {
            if (!File.Exists(entry.ConfigPath))
            {
                throw new FileNotFoundException("配置版本文件不存在", entry.ConfigPath);
            }

            string json = File.ReadAllText(entry.ConfigPath);
            string actualHash = ComputeSha256(json);
            if (!string.IsNullOrWhiteSpace(entry.ConfigHash) &&
                !string.Equals(entry.ConfigHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"配置版本校验失败: {entry.VersionId}");
            }

            return json;
        }

        private ConfigVersionEntry? TryReadEntry(string metadataPath)
        {
            try
            {
                string json = File.ReadAllText(metadataPath);
                ConfigVersionMetadata? metadata = JsonSerializer.Deserialize<ConfigVersionMetadata>(json);
                if (metadata == null ||
                    !string.Equals(metadata.Schema, Schema, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(metadata.VersionId))
                {
                    return null;
                }

                return ToEntry(metadata, metadataPath);
            }
            catch
            {
                return null;
            }
        }

        private ConfigVersionEntry ToEntry(ConfigVersionMetadata metadata, string metadataPath)
        {
            string directory = Path.GetDirectoryName(metadataPath) ?? _versionRoot;
            string configFileName = string.IsNullOrWhiteSpace(metadata.ConfigFileName)
                ? $"{metadata.VersionId}.config.json"
                : metadata.ConfigFileName;
            string configPath = Path.Combine(directory, configFileName);
            return new ConfigVersionEntry
            {
                VersionId = metadata.VersionId,
                CreatedAt = metadata.CreatedAt,
                Reason = metadata.Reason,
                OperatorName = metadata.OperatorName,
                OperatorRole = metadata.OperatorRole,
                ShiftName = metadata.ShiftName,
                ChangeSummary = metadata.ChangeSummary,
                ConfigHash = metadata.ConfigHash,
                ConfigPath = configPath,
                MetadataPath = metadataPath
            };
        }

        private void PruneCore(int maxVersions)
        {
            if (maxVersions <= 0)
            {
                return;
            }

            ConfigVersionEntry[] oldEntries = ListVersionsCore(1000)
                .OrderByDescending(entry => entry.CreatedAt)
                .Skip(maxVersions)
                .ToArray();

            foreach (ConfigVersionEntry entry in oldEntries)
            {
                TryDeleteFile(entry.ConfigPath);
                TryDeleteFile(entry.MetadataPath);
            }
        }

        private static string BuildVersionId(DateTimeOffset createdAt, string reason)
        {
            string safeReason = SanitizePathSegment(reason, 40);
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"{createdAt.LocalDateTime:yyyyMMddHHmmssfff}_{safeReason}_{suffix}";
        }

        private static string NormalizeText(string? value, string fallback, int maxLength)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = fallback;
            }

            return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
        }

        private static string NormalizeAuditLine(string? value)
        {
            string normalized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
            }

            normalized = normalized.Trim();
            return normalized.Length <= 2000 ? normalized : normalized[..2000];
        }

        private static string SanitizePathSegment(string value, int maxLength)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (invalidChars.Contains(ch) || char.IsWhiteSpace(ch))
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(ch);
                }
            }

            string sanitized = builder.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "ConfigChange";
            }

            return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
        }

        private static string ComputeSha256(string text)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private sealed class ConfigVersionMetadata
        {
            public string Schema { get; init; } = ConfigVersionStore.Schema;
            public string VersionId { get; init; } = string.Empty;
            public DateTimeOffset CreatedAt { get; init; }
            public string Reason { get; init; } = string.Empty;
            public string OperatorName { get; init; } = string.Empty;
            public string OperatorRole { get; init; } = string.Empty;
            public string ShiftName { get; init; } = string.Empty;
            public string ChangeSummary { get; init; } = string.Empty;
            public string ConfigFileName { get; init; } = string.Empty;
            public string ConfigHash { get; init; } = string.Empty;
        }

        private sealed class FileSnapshot
        {
            private FileSnapshot(string path, bool exists, byte[]? content)
            {
                Path = path;
                Exists = exists;
                Content = content;
            }

            private string Path { get; }
            private bool Exists { get; }
            private byte[]? Content { get; }

            public static FileSnapshot Capture(string path)
            {
                return File.Exists(path)
                    ? new FileSnapshot(path, true, File.ReadAllBytes(path))
                    : new FileSnapshot(path, false, null);
            }

            public void Restore()
            {
                string directory = System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!Exists)
                {
                    if (File.Exists(Path))
                    {
                        File.Delete(Path);
                    }

                    return;
                }

                string tempPath = System.IO.Path.Combine(
                    string.IsNullOrWhiteSpace(directory) ? "." : directory,
                    $"{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.rollback");

                try
                {
                    File.WriteAllBytes(tempPath, Content ?? Array.Empty<byte>());
                    if (File.Exists(Path))
                    {
                        File.Replace(tempPath, Path, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tempPath, Path);
                    }
                }
                finally
                {
                    TryDeleteFile(tempPath);
                }
            }
        }
    }
}
