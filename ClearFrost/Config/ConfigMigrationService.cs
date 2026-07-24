using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClearFrost.Helpers;

namespace ClearFrost.Config
{
    public enum ConfigMigrationImportKind
    {
        MigrationPackage,
        AppConfig,
        ProjectPresets
    }

    public sealed class ConfigMigrationExportResult
    {
        public string Path { get; init; } = string.Empty;
        public string Schema { get; init; } = ConfigMigrationService.Schema;
        public int PresetCount { get; init; }
    }

    public class ConfigMigrationImportPreview
    {
        public ConfigMigrationImportKind Kind { get; init; }
        public bool HasConfig { get; init; }
        public bool HasPresets { get; init; }
        public int PresetCount { get; init; }
        public string? SourceAppVersion { get; init; }
    }

    public sealed class ConfigMigrationImportResult : ConfigMigrationImportPreview
    {
        public string RuntimeConfigPath { get; init; } = RuntimePaths.ConfigPath;
        public string ProjectPresetsPath { get; init; } = RuntimePaths.ProjectPresetsPath;
    }

    public static class ConfigMigrationService
    {
        public const string Schema = "ClearFrost.ConfigMigration.v1";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        private static readonly string[] AppConfigPropertyHints =
        {
            nameof(AppConfig.PlcIp),
            nameof(AppConfig.PlcPort),
            nameof(AppConfig.PlcTriggerAddress),
            nameof(AppConfig.PlcResultAddress),
            nameof(AppConfig.PlcNgValue),
            nameof(AppConfig.PlcOkValue),
            nameof(AppConfig.Cameras),
            nameof(AppConfig.ActiveCameraId),
            "CameraSerialNumber",
            "ExposureTime",
            "GainRaw",
            nameof(AppConfig.TriggerSource),
            nameof(AppConfig.SerialPhotoelectricPortName),
            nameof(AppConfig.InspectionRuleSetJson),
            nameof(AppConfig.StoragePath),
            nameof(AppConfig.CurrentModelFileName)
        };

        public static ConfigMigrationExportResult Export(AppConfig config, string targetPath, string? appVersion = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("导出路径不能为空", nameof(targetPath));
            }

            JsonObject presets = ProjectPresetStore.ExportPresets();
            JsonObject package = new()
            {
                ["schema"] = Schema,
                ["exportedAt"] = DateTimeOffset.Now.ToString("O"),
                ["appVersion"] = appVersion ?? string.Empty,
                ["sourceRuntimeConfigPath"] = RuntimePaths.ConfigPath,
                ["config"] = JsonNode.Parse(config.ToPortableJson()) ?? new JsonObject(),
                ["projectPresets"] = presets
            };

            AtomicFileWriter.WriteAllText(targetPath, package.ToJsonString(JsonOptions));
            return new ConfigMigrationExportResult
            {
                Path = targetPath,
                PresetCount = presets.Count
            };
        }

        public static ConfigMigrationImportPreview PreviewImport(string sourcePath)
        {
            ParsedImport parsed = ParseImportFile(sourcePath);
            return new ConfigMigrationImportPreview
            {
                Kind = parsed.Kind,
                HasConfig = parsed.Config != null,
                HasPresets = parsed.Presets != null,
                PresetCount = parsed.Presets?.Count ?? 0,
                SourceAppVersion = parsed.SourceAppVersion
            };
        }

        public static ConfigMigrationImportResult ImportFromFile(string sourcePath, AppConfig currentConfig)
        {
            ArgumentNullException.ThrowIfNull(currentConfig);

            ParsedImport parsed = ParseImportFile(sourcePath);
            AppConfig? previousConfig = parsed.Config != null
                ? AppConfig.FromJson(currentConfig.ToPortableJson())
                : null;
            FileSnapshot? configSnapshot = parsed.Config != null
                ? FileSnapshot.Capture(RuntimePaths.ConfigPath)
                : null;
            bool configCopied = false;
            bool configSaved = false;

            try
            {
                if (parsed.Config != null)
                {
                    currentConfig.CopyFrom(parsed.Config);
                    configCopied = true;
                    if (!currentConfig.Save())
                    {
                        throw new InvalidOperationException(currentConfig.LastError ?? "配置保存失败");
                    }

                    configSaved = true;
                }

                ProjectPresetStore.Snapshot? mergedSnapshot = null;
                if (parsed.Presets != null)
                {
                    mergedSnapshot = ProjectPresetStore.MergePresets(parsed.Presets);
                }

                return new ConfigMigrationImportResult
                {
                    Kind = parsed.Kind,
                    HasConfig = parsed.Config != null,
                    HasPresets = parsed.Presets != null,
                    PresetCount = parsed.Presets?.Count ?? 0,
                    SourceAppVersion = parsed.SourceAppVersion,
                    RuntimeConfigPath = RuntimePaths.ConfigPath,
                    ProjectPresetsPath = mergedSnapshot?.Path ?? RuntimePaths.ProjectPresetsPath
                };
            }
            catch (Exception ex)
            {
                try
                {
                    if (configSaved)
                    {
                        configSnapshot?.Restore();
                    }

                    if (configCopied && previousConfig != null)
                    {
                        currentConfig.CopyFrom(previousConfig);
                    }
                }
                catch (Exception rollbackEx)
                {
                    throw new InvalidOperationException(
                        $"导入失败，且运行配置回滚失败: {rollbackEx.Message}",
                        new AggregateException(ex, rollbackEx));
                }

                throw;
            }
        }

        private static ParsedImport ParseImportFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("导入路径不能为空", nameof(sourcePath));
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("配置迁移文件不存在", sourcePath);
            }

            string json = File.ReadAllText(sourcePath);
            JsonNode? node = JsonNode.Parse(json, documentOptions: DocumentOptions);
            if (node is not JsonObject root)
            {
                throw new InvalidOperationException("配置迁移文件必须是 JSON 对象");
            }

            if (root.TryGetPropertyValue("schema", out JsonNode? schemaNode) &&
                schemaNode?.GetValue<string>() is string schema)
            {
                if (!string.Equals(schema, Schema, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"不支持的配置迁移格式: {schema}");
                }

                return ParseMigrationPackage(root);
            }

            if (TryExtractWrappedPresets(root, out JsonObject? wrappedPresets))
            {
                return new ParsedImport(ConfigMigrationImportKind.ProjectPresets, null, wrappedPresets, null);
            }

            if (LooksLikeRawPresetStore(root))
            {
                JsonObject presets = CloneObject(root);
                ValidatePresets(presets);
                return new ParsedImport(ConfigMigrationImportKind.ProjectPresets, null, presets, null);
            }

            if (LooksLikeAppConfig(root))
            {
                return new ParsedImport(ConfigMigrationImportKind.AppConfig, AppConfig.FromJson(root.ToJsonString()), null, null);
            }

            throw new InvalidOperationException("未识别的配置迁移文件");
        }

        private static ParsedImport ParseMigrationPackage(JsonObject root)
        {
            if (root["config"] is not JsonObject configObject)
            {
                throw new InvalidOperationException("配置迁移包缺少 config 对象");
            }

            AppConfig config = AppConfig.FromJson(configObject.ToJsonString());
            JsonObject? presets = null;
            if (root["projectPresets"] is JsonObject projectPresets)
            {
                presets = ExtractPresetObject(projectPresets);
            }

            string? sourceAppVersion = root["appVersion"]?.GetValue<string>();
            return new ParsedImport(ConfigMigrationImportKind.MigrationPackage, config, presets, sourceAppVersion);
        }

        private static bool TryExtractWrappedPresets(JsonObject root, out JsonObject? presets)
        {
            presets = null;
            if (root["presets"] is not JsonObject wrappedPresets)
            {
                return false;
            }

            presets = ExtractPresetObject(wrappedPresets);
            return true;
        }

        private static JsonObject ExtractPresetObject(JsonObject source)
        {
            JsonObject presets = source["presets"] is JsonObject nested
                ? CloneObject(nested)
                : CloneObject(source);
            ValidatePresets(presets);
            return presets;
        }

        private static bool LooksLikeRawPresetStore(JsonObject root)
        {
            return root.All(item => item.Value is JsonObject);
        }

        private static bool LooksLikeAppConfig(JsonObject root)
        {
            string[] keys = root.Select(item => item.Key).ToArray();
            return AppConfigPropertyHints.Any(propertyName =>
                keys.Any(key => string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase)));
        }

        private static JsonObject CloneObject(JsonObject source)
        {
            return JsonNode.Parse(source.ToJsonString()) as JsonObject ?? new JsonObject();
        }

        private static void ValidatePresets(JsonObject presets)
        {
            foreach (KeyValuePair<string, JsonNode?> item in presets)
            {
                ValidatePresetId(item.Key);
                if (item.Value is not JsonObject)
                {
                    throw new InvalidOperationException($"预设 {item.Key} 内容必须是 JSON 对象");
                }
            }
        }

        private static void ValidatePresetId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("预设编号不能为空");
            }

            foreach (char ch in id)
            {
                if (char.IsControl(ch))
                {
                    throw new InvalidOperationException("预设编号不能包含控制字符");
                }
            }
        }

        private sealed record ParsedImport(
            ConfigMigrationImportKind Kind,
            AppConfig? Config,
            JsonObject? Presets,
            string? SourceAppVersion);

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
                EnsureFileIsNotReparsePoint(path);
                return File.Exists(path)
                    ? new FileSnapshot(path, true, File.ReadAllBytes(path))
                    : new FileSnapshot(path, false, null);
            }

            public void Restore()
            {
                if (!Exists)
                {
                    if (File.Exists(Path))
                    {
                        EnsureFileIsNotReparsePoint(Path);
                        EnsureDirectoryPathHasNoReparsePoint(System.IO.Path.GetDirectoryName(Path) ?? string.Empty);
                        File.Delete(Path);
                    }

                    return;
                }

                AtomicFileWriter.RestoreAllBytes(Path, Content ?? Array.Empty<byte>());
            }

            private static void EnsureFileIsNotReparsePoint(string path)
            {
                var file = new FileInfo(path);
                file.Refresh();
                if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"运行配置文件是链接文件，拒绝回滚: {path}");
                }
            }

            private static void EnsureDirectoryPathHasNoReparsePoint(string directory)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                var current = new DirectoryInfo(System.IO.Path.GetFullPath(directory));
                while (current != null)
                {
                    current.Refresh();
                    if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"运行配置目录包含链接目录，拒绝回滚: {current.FullName}");
                    }

                    current = current.Parent;
                }
            }
        }
    }
}
