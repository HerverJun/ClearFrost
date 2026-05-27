// ============================================================================
// 文件名: ModelPackageImporter.cs
// 描述:   模型包导入与验收
//
// 功能:
//   - 将 ONNX 文件导入为包含 manifest/hash/labels 的模型包
//   - 可选同步发布到 legacy ONNX 目录，兼容现有模型加载入口
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Helpers;

namespace ClearFrost.Core.Models
{
    public sealed class ModelPackageImportOptions
    {
        public string SourceModelPath { get; init; } = string.Empty;
        public string PackageDirectory { get; init; } = string.Empty;
        public string OnnxDirectory { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ModelFileName { get; init; } = string.Empty;
        public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
        public string Description { get; init; } = string.Empty;
        public bool OverwriteExisting { get; init; }
        public bool PublishToOnnxDirectory { get; init; } = true;
        public bool StrictValidation { get; init; }
        public Func<string, ModelPackageManifest, bool>? Warmup { get; init; }
    }

    public sealed class ModelPackageImportResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string PackageDirectory { get; init; } = string.Empty;
        public string ManifestPath { get; init; } = string.Empty;
        public string ModelPath { get; init; } = string.Empty;
        public string PublishedOnnxPath { get; init; } = string.Empty;
        public ModelPackageManifest? Manifest { get; init; }
        public ModelRegistryEntry? RegistryEntry { get; init; }

        public static ModelPackageImportResult Failed(string message)
        {
            return new ModelPackageImportResult
            {
                Success = false,
                Message = message ?? string.Empty
            };
        }
    }

    public static class ModelPackageImporter
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static ModelPackageImportResult Import(ModelPackageImportOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            try
            {
                string sourcePath = ResolveSourcePath(options.SourceModelPath);
                string packageRoot = ResolveWritableDirectory(options.PackageDirectory, nameof(options.PackageDirectory));
                string modelId = SanitizePackageId(options.ModelId);
                string version = string.IsNullOrWhiteSpace(options.Version)
                    ? DateTime.Now.ToString("yyyyMMdd.HHmmss")
                    : options.Version.Trim();
                string modelFileName = SanitizeModelFileName(options.ModelFileName, Path.GetFileName(sourcePath));
                List<string> labels = NormalizeLabels(options.Labels);
                if (labels.Count == 0)
                {
                    return ModelPackageImportResult.Failed("模型包导入失败: 标签列表不能为空。");
                }

                string targetPackageDir = Path.Combine(packageRoot, modelId);
                EnsureInsideRoot(packageRoot, targetPackageDir);
                if (Directory.Exists(targetPackageDir) && !options.OverwriteExisting)
                {
                    return ModelPackageImportResult.Failed($"模型包已存在: {targetPackageDir}");
                }

                string publishedOnnxPath = string.Empty;
                if (options.PublishToOnnxDirectory && !string.IsNullOrWhiteSpace(options.OnnxDirectory))
                {
                    string onnxRoot = ResolveWritableDirectory(options.OnnxDirectory, nameof(options.OnnxDirectory));
                    publishedOnnxPath = Path.Combine(onnxRoot, modelFileName);
                    EnsureInsideRoot(onnxRoot, publishedOnnxPath);
                    if (File.Exists(publishedOnnxPath) &&
                        !options.OverwriteExisting &&
                        !HashesEqual(sourcePath, publishedOnnxPath))
                    {
                        return ModelPackageImportResult.Failed($"ONNX 目录中已存在同名不同内容模型: {publishedOnnxPath}");
                    }
                }

                string stageDir = Path.Combine(packageRoot, $".{modelId}.import-{Guid.NewGuid():N}.tmp");
                Directory.CreateDirectory(stageDir);
                try
                {
                    string stagedModelPath = Path.Combine(stageDir, modelFileName);
                    File.Copy(sourcePath, stagedModelPath, overwrite: false);
                    string hash = ComputeSha256(stagedModelPath);
                    var manifest = new ModelPackageManifest
                    {
                        ModelId = modelId,
                        Version = version,
                        ModelFileName = modelFileName,
                        ModelHash = hash,
                        Sha256 = hash,
                        Labels = labels,
                        CreatedAt = DateTimeOffset.Now,
                        Description = options.Description?.Trim() ?? string.Empty
                    };
                    string manifestPath = Path.Combine(stageDir, "manifest.json");
                    AtomicFileWriter.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

                    if (Directory.Exists(targetPackageDir))
                    {
                        Directory.Delete(targetPackageDir, recursive: true);
                    }

                    Directory.Move(stageDir, targetPackageDir);
                    string targetModelPath = Path.Combine(targetPackageDir, modelFileName);
                    string targetManifestPath = Path.Combine(targetPackageDir, "manifest.json");

                    if (!string.IsNullOrWhiteSpace(publishedOnnxPath))
                    {
                        PublishOnnx(targetModelPath, publishedOnnxPath);
                    }

                    var registry = new ModelRegistry();
                    registry.Scan(new ModelRegistryScanOptions
                    {
                        PackageDirectory = packageRoot,
                        OnnxDirectory = options.OnnxDirectory,
                        StrictPackageMode = options.StrictValidation,
                        Warmup = options.Warmup
                    });
                    ModelRegistryEntry? entry = registry.Resolve(modelId);
                    if (entry == null)
                    {
                        return new ModelPackageImportResult
                        {
                            Success = false,
                            Message = "模型包已写入，但注册表无法解析该模型包。",
                            PackageDirectory = targetPackageDir,
                            ManifestPath = targetManifestPath,
                            ModelPath = targetModelPath,
                            PublishedOnnxPath = publishedOnnxPath,
                            Manifest = manifest
                        };
                    }

                    return new ModelPackageImportResult
                    {
                        Success = entry.Status != ModelRegistryStatus.Blocked,
                        Message = entry.Status == ModelRegistryStatus.Blocked
                            ? $"模型包导入后验收失败: {entry.Message}"
                            : $"模型包导入成功: {entry.ModelId}",
                        PackageDirectory = targetPackageDir,
                        ManifestPath = targetManifestPath,
                        ModelPath = targetModelPath,
                        PublishedOnnxPath = publishedOnnxPath,
                        Manifest = manifest,
                        RegistryEntry = entry
                    };
                }
                finally
                {
                    if (Directory.Exists(stageDir))
                    {
                        Directory.Delete(stageDir, recursive: true);
                    }
                }
            }
            catch (Exception ex)
            {
                return ModelPackageImportResult.Failed($"模型包导入失败: {ex.Message}");
            }
        }

        private static string ResolveSourcePath(string sourceModelPath)
        {
            if (string.IsNullOrWhiteSpace(sourceModelPath))
            {
                throw new InvalidOperationException("源 ONNX 路径为空。");
            }

            string fullPath = Path.GetFullPath(sourceModelPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("源 ONNX 文件不存在。", fullPath);
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".onnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("源模型必须是 .onnx 文件。");
            }

            return fullPath;
        }

        private static string ResolveWritableDirectory(string path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException($"{parameterName} 不能为空。");
            }

            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        private static string SanitizePackageId(string modelId)
        {
            string value = string.IsNullOrWhiteSpace(modelId)
                ? $"model-{DateTime.Now:yyyyMMddHHmmss}"
                : modelId.Trim();
            value = string.Concat(value.Select(ch =>
                char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.'
                    ? ch
                    : '-')).Trim('-', '.', '_');
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("模型包 ID 无效。");
            }

            return value.Length > 96 ? value.Substring(0, 96).Trim('-', '.', '_') : value;
        }

        private static string SanitizeModelFileName(string requestedFileName, string sourceFileName)
        {
            string fileName = string.IsNullOrWhiteSpace(requestedFileName)
                ? sourceFileName
                : Path.GetFileName(requestedFileName.Trim());
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "model.onnx";
            }

            if (!fileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".onnx";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            fileName = string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
            return string.IsNullOrWhiteSpace(fileName) ? "model.onnx" : fileName;
        }

        private static List<string> NormalizeLabels(IEnumerable<string>? labels)
        {
            return (labels ?? Array.Empty<string>())
                .Select(label => label?.Trim() ?? string.Empty)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void PublishOnnx(string sourcePath, string targetPath)
        {
            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = Path.Combine(
                string.IsNullOrWhiteSpace(directory) ? "." : directory,
                $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(sourcePath, tempPath, overwrite: false);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void EnsureInsideRoot(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"目标路径超出允许目录: {path}");
            }
        }

        private static bool HashesEqual(string leftPath, string rightPath)
        {
            return string.Equals(
                ComputeSha256(leftPath),
                ComputeSha256(rightPath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }
    }
}
