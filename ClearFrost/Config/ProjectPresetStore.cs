using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClearFrost.Helpers;

namespace ClearFrost.Config
{
    /// <summary>
    /// 现场项目预设存储。首次运行从安装目录复制默认模板，之后只读写用户可写目录。
    /// </summary>
    public static class ProjectPresetStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public sealed class Snapshot
        {
            public JsonObject Presets { get; init; } = new();
            public string Path { get; init; } = RuntimePaths.ProjectPresetsPath;
        }

        public static Snapshot Load()
        {
            EnsureRuntimePresetFile();
            return new Snapshot
            {
                Presets = ReadPresetObject(RuntimePaths.ProjectPresetsPath) ?? new JsonObject(),
                Path = RuntimePaths.ProjectPresetsPath
            };
        }

        public static Snapshot SavePreset(string payloadJson)
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            JsonElement root = doc.RootElement;

            string id = GetRequiredString(root, "id");
            string name = GetRequiredString(root, "name");
            if (!root.TryGetProperty("preset", out JsonElement presetElement) ||
                presetElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("预设内容不能为空");
            }

            id = NormalizePresetId(id);
            JsonObject preset = JsonNode.Parse(presetElement.GetRawText()) as JsonObject
                ?? throw new InvalidOperationException("预设内容格式无效");
            preset["name"] = name.Trim();

            Snapshot snapshot = Load();
            snapshot.Presets[id] = preset;
            WritePresetObject(RuntimePaths.ProjectPresetsPath, snapshot.Presets);
            return Load();
        }

        public static Snapshot DeletePreset(string id)
        {
            id = NormalizePresetId(id);
            Snapshot snapshot = Load();
            if (!snapshot.Presets.Remove(id))
            {
                throw new InvalidOperationException("未找到要删除的项目预设");
            }

            WritePresetObject(RuntimePaths.ProjectPresetsPath, snapshot.Presets);
            return Load();
        }

        public static JsonObject ExportPresets()
        {
            return CloneObject(Load().Presets);
        }

        public static Snapshot MergePresets(JsonObject importedPresets)
        {
            if (importedPresets == null)
            {
                throw new ArgumentNullException(nameof(importedPresets));
            }

            Snapshot snapshot = Load();
            foreach (KeyValuePair<string, JsonNode?> item in importedPresets)
            {
                string id = NormalizePresetId(item.Key);
                if (item.Value is not JsonObject preset)
                {
                    throw new InvalidOperationException($"预设 {id} 内容必须是 JSON 对象");
                }

                snapshot.Presets[id] = CloneObject(preset);
            }

            WritePresetObject(RuntimePaths.ProjectPresetsPath, snapshot.Presets);
            return Load();
        }

        private static void EnsureRuntimePresetFile()
        {
            string runtimePath = RuntimePaths.ProjectPresetsPath;
            if (File.Exists(runtimePath))
            {
                EnsurePresetFileSafeForRead(runtimePath);
                return;
            }

            string? seedPath = GetSeedPresetPath();
            if (!string.IsNullOrWhiteSpace(seedPath))
            {
                WritePresetObject(runtimePath, ReadPresetObject(seedPath) ?? new JsonObject());
                return;
            }

            WritePresetObject(runtimePath, new JsonObject());
        }

        private static string? GetSeedPresetPath()
        {
            foreach (string path in GetSeedPresetPaths())
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static IEnumerable<string> GetSeedPresetPaths()
        {
            yield return RuntimePaths.LegacySharedProjectPresetsPath;
            yield return RuntimePaths.BundledProjectPresetsPath;
        }

        private static JsonObject? ReadPresetObject(string path)
        {
            EnsurePresetFileSafeForRead(path);
            string json = File.ReadAllText(path, Encoding.UTF8);
            JsonNode? node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (node is not JsonObject root)
            {
                return null;
            }

            if (root["presets"] is JsonObject wrappedPresets)
            {
                return CloneObject(wrappedPresets);
            }

            return root;
        }

        private static JsonObject CloneObject(JsonObject source)
        {
            return JsonNode.Parse(source.ToJsonString()) as JsonObject ?? new JsonObject();
        }

        private static void WritePresetObject(string targetPath, JsonObject presets)
        {
            AtomicFileWriter.WriteAllText(targetPath, presets.ToJsonString(JsonOptions));
        }

        private static void EnsurePresetFileSafeForRead(string path)
        {
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists)
            {
                return;
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"项目预设文件是链接文件，拒绝读取: {path}");
            }
        }

        private static string GetRequiredString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement element))
            {
                throw new InvalidOperationException($"{propertyName} 不能为空");
            }

            string value = element.GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{propertyName} 不能为空");
            }

            return value;
        }

        private static string NormalizePresetId(string id)
        {
            string normalized = id.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("预设编号不能为空");
            }

            foreach (char ch in normalized)
            {
                if (char.IsControl(ch))
                {
                    throw new InvalidOperationException("预设编号不能包含控制字符");
                }
            }

            return normalized.Length <= 128 ? normalized : normalized[..128];
        }
    }
}
