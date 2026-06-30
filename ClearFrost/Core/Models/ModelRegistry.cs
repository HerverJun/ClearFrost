using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace ClearFrost.Core.Models
{
    public sealed class ModelRegistryScanOptions
    {
        public string PackageDirectory { get; init; } = string.Empty;
        public string OnnxDirectory { get; init; } = string.Empty;
        public bool StrictPackageMode { get; init; }
        public bool RequireProductionApproval { get; init; }
        public Func<string, ModelPackageManifest, bool>? Warmup { get; init; }
    }

    public sealed class ModelRegistry
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        private IReadOnlyList<ModelRegistryEntry> _entries = Array.Empty<ModelRegistryEntry>();

        public IReadOnlyList<ModelRegistryEntry> Entries => Volatile.Read(ref _entries);

        public bool HasBlockingErrors => Entries.Any(e => e.Status == ModelRegistryStatus.Blocked);

        public IReadOnlyList<ModelRegistryEntry> Scan(ModelRegistryScanOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var entries = new List<ModelRegistryEntry>();
            ScanPackages(options, entries);
            ScanBareOnnx(options.OnnxDirectory, options.RequireProductionApproval, entries);

            IReadOnlyList<ModelRegistryEntry> snapshot = entries.AsReadOnly();
            Volatile.Write(ref _entries, snapshot);
            return snapshot;
        }

        public ModelRegistryEntry? Resolve(string? usedModelName)
        {
            if (string.IsNullOrWhiteSpace(usedModelName))
            {
                return null;
            }

            IReadOnlyList<ModelRegistryEntry> entries = Entries;
            string normalized = NormalizeName(usedModelName);
            if (IsPathLike(usedModelName))
            {
                string fullPath = GetFullPathSafe(usedModelName);
                return entries.FirstOrDefault(e =>
                    string.Equals(GetFullPathSafe(e.ModelPath), fullPath, StringComparison.OrdinalIgnoreCase));
            }

            var candidates = entries
                .Where(e =>
                    string.Equals(NormalizeName(e.UsedModelName), normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(e.ModelId), normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(Path.GetFileName(e.ModelPath)), normalized, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => GetFullPathSafe(e.ModelPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            // 多匹配时优先返回 package 条目（包含 manifest/Hash/Version），保持追溯字段完整。
            ModelRegistryEntry? packageMatch = candidates.FirstOrDefault(e => e.IsPackage);
            return packageMatch ?? candidates[0];
        }

        private void ScanPackages(ModelRegistryScanOptions options, List<ModelRegistryEntry> entries)
        {
            string packageDirectory = options.PackageDirectory;
            if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
            {
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(packageDirectory))
            {
                string manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    string[] onnxFiles = Directory.GetFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly);
                    if (onnxFiles.Length > 0)
                    {
                        entries.Add(new ModelRegistryEntry
                        {
                            ModelId = Path.GetFileName(directory),
                            UsedModelName = Path.GetFileName(onnxFiles[0]),
                            ModelPath = onnxFiles[0],
                            IsPackage = true,
                            Status = options.StrictPackageMode || options.RequireProductionApproval
                                ? ModelRegistryStatus.Blocked
                                : ModelRegistryStatus.Warning,
                            Message = options.StrictPackageMode || options.RequireProductionApproval
                                ? "Model package manifest is missing."
                                : "Manifest missing; package kept as warning for compatibility.",
                            ApprovalStatus = ModelApprovalStatuses.Pending,
                            ApprovedForProduction = false
                        });
                    }

                    continue;
                }

                entries.Add(ValidatePackage(directory, manifestPath, options));
            }
        }

        private ModelRegistryEntry ValidatePackage(
            string directory,
            string manifestPath,
            ModelRegistryScanOptions options)
        {
            var warnings = new List<string>();
            var failures = new List<string>();
            ModelPackageManifest? manifest = null;

            try
            {
                string json = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<ModelPackageManifest>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                failures.Add($"Manifest parse failed: {ex.Message}");
            }

            manifest ??= new ModelPackageManifest
            {
                ModelId = Path.GetFileName(directory)
            };

            if (string.IsNullOrWhiteSpace(manifest.ModelId))
            {
                manifest.ModelId = Path.GetFileName(directory);
            }

            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                warnings.Add("Version is empty.");
            }

            string modelFileName = string.IsNullOrWhiteSpace(manifest.ModelFileName)
                ? "model.onnx"
                : manifest.ModelFileName.Trim();
            string modelPath = Path.Combine(directory, modelFileName);
            string computedHash = string.Empty;

            if (!File.Exists(modelPath))
            {
                failures.Add($"Model file is missing: {modelFileName}");
            }
            else
            {
                computedHash = ComputeSha256(modelPath);
                string expectedHash = manifest.EffectiveHash;
                if (string.IsNullOrWhiteSpace(expectedHash))
                {
                    if (options.StrictPackageMode || options.RequireProductionApproval)
                    {
                        failures.Add("Model hash is missing.");
                    }
                    else
                    {
                        warnings.Add("Model hash is missing.");
                    }
                }
                else if (!string.Equals(expectedHash, computedHash, StringComparison.OrdinalIgnoreCase))
                {
                    string message = "Model hash does not match manifest.";
                    if (options.StrictPackageMode)
                    {
                        failures.Add(message);
                    }
                    else
                    {
                        warnings.Add(message);
                    }
                }
            }

            if (manifest.Labels == null || manifest.Labels.Count == 0 || manifest.Labels.All(string.IsNullOrWhiteSpace))
            {
                string message = "Labels are missing.";
                if (options.StrictPackageMode || options.RequireProductionApproval)
                {
                    failures.Add(message);
                }
                else
                {
                    warnings.Add(message);
                }
            }

            if (options.StrictPackageMode && failures.Count == 0)
            {
                bool warmupOk = options.Warmup?.Invoke(modelPath, manifest) ?? DefaultWarmup(modelPath);
                if (!warmupOk)
                {
                    failures.Add("Model warmup failed.");
                }
            }

            if (options.RequireProductionApproval)
            {
                if (manifest.InputWidth <= 0 || manifest.InputHeight <= 0)
                {
                    failures.Add("Model input size metadata is missing.");
                }

                if (string.IsNullOrWhiteSpace(manifest.TaskType))
                {
                    failures.Add("Model task type metadata is missing.");
                }

                if (!IsApprovedForProduction(manifest))
                {
                    failures.Add("Model is not approved for production.");
                }
            }

            ModelRegistryStatus status = failures.Count > 0
                ? ModelRegistryStatus.Blocked
                : warnings.Count > 0
                    ? ModelRegistryStatus.Warning
                    : ModelRegistryStatus.Ready;

            string messageText = string.Join(" ", failures.Concat(warnings));
            if (string.IsNullOrWhiteSpace(messageText))
            {
                messageText = options.StrictPackageMode ? "Package validated and warmup passed." : "Package validated.";
            }

            return new ModelRegistryEntry
            {
                ModelId = manifest.ModelId,
                Version = manifest.Version,
                ModelHash = computedHash,
                UsedModelName = Path.GetFileName(modelPath),
                ModelPath = modelPath,
                ManifestPath = manifestPath,
                IsPackage = true,
                Status = status,
                Message = messageText,
                Labels = manifest.Labels ?? new List<string>(),
                Manifest = manifest,
                TaskType = manifest.TaskType ?? string.Empty,
                InputWidth = manifest.InputWidth,
                InputHeight = manifest.InputHeight,
                ApprovalStatus = manifest.Approval?.Status ?? ModelApprovalStatuses.Pending,
                ApprovedForProduction = IsApprovedForProduction(manifest)
            };
        }

        private void ScanBareOnnx(string onnxDirectory, bool requireProductionApproval, List<ModelRegistryEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(onnxDirectory) || !Directory.Exists(onnxDirectory))
            {
                return;
            }

            foreach (string modelPath in Directory.EnumerateFiles(onnxDirectory, "*.onnx", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(modelPath);
                entries.Add(new ModelRegistryEntry
                {
                    ModelId = Path.GetFileNameWithoutExtension(fileName),
                    Version = "legacy",
                    ModelHash = ComputeSha256(modelPath),
                    UsedModelName = fileName,
                    ModelPath = modelPath,
                    IsPackage = false,
                    Status = requireProductionApproval ? ModelRegistryStatus.Blocked : ModelRegistryStatus.Warning,
                    Message = "Bare ONNX model discovered; kept for legacy compatibility.",
                    ApprovalStatus = ModelApprovalStatuses.Legacy,
                    ApprovedForProduction = false
                });
            }
        }

        public bool IsApprovedForProduction(string? usedModelName)
        {
            ModelRegistryEntry? entry = Resolve(usedModelName);
            return entry != null && entry.ApprovedForProduction;
        }

        public ModelProductionValidationResult ValidateForProductionActivation(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return ModelProductionValidationResult.Fail("ProductionModelPathEmpty", "模型路径为空。");
            }

            string fullPath = GetFullPathSafe(modelPath);
            if (!File.Exists(fullPath))
            {
                return ModelProductionValidationResult.Fail("ProductionModelFileMissing", $"模型文件不存在: {fullPath}");
            }

            IReadOnlyList<ModelRegistryEntry> entries = Entries;
            string fileName = Path.GetFileName(fullPath);
            var sameNameDifferentPath = entries
                .Where(entry => string.Equals(Path.GetFileName(entry.ModelPath), fileName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => GetFullPathSafe(entry.ModelPath))
                .Where(path => !string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sameNameDifferentPath.Count > 0)
            {
                return ModelProductionValidationResult.Fail(
                    "ProductionModelNameAmbiguous",
                    $"模型文件名存在不同路径条目，禁止按同名模型进入生产: {fileName}");
            }

            var exactMatches = entries
                .Where(entry => string.Equals(GetFullPathSafe(entry.ModelPath), fullPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exactMatches.Count == 0)
            {
                return ModelProductionValidationResult.Fail("ProductionModelNotRegistered", $"模型未注册: {fullPath}");
            }

            if (exactMatches.Count > 1)
            {
                return ModelProductionValidationResult.Fail("ProductionModelPathAmbiguous", $"模型路径存在重复注册条目: {fullPath}");
            }

            ModelRegistryEntry entry = exactMatches[0];
            if (!entry.IsPackage || string.IsNullOrWhiteSpace(entry.ManifestPath) || entry.Manifest == null)
            {
                return ModelProductionValidationResult.Fail("ProductionModelManifestMissing", $"模型缺少有效 manifest: {fullPath}");
            }

            if (entry.Status != ModelRegistryStatus.Ready)
            {
                return ModelProductionValidationResult.Fail("ProductionModelRegistryBlocked", entry.Message);
            }

            if (!entry.ApprovedForProduction)
            {
                return ModelProductionValidationResult.Fail("ProductionModelNotApproved", $"模型未批准: {entry.ApprovalStatus}");
            }

            if (entry.Labels.Count == 0 || entry.Labels.All(string.IsNullOrWhiteSpace))
            {
                return ModelProductionValidationResult.Fail("ProductionModelLabelsMissing", "模型类别元数据缺失。");
            }

            if (entry.InputWidth <= 0 || entry.InputHeight <= 0)
            {
                return ModelProductionValidationResult.Fail("ProductionModelInputSizeMissing", "模型输入尺寸元数据缺失。");
            }

            if (string.IsNullOrWhiteSpace(entry.TaskType))
            {
                return ModelProductionValidationResult.Fail("ProductionModelTaskTypeMissing", "模型任务类型元数据缺失。");
            }

            string actualHash = ComputeSha256(fullPath);
            if (string.IsNullOrWhiteSpace(entry.ModelHash) ||
                !string.Equals(entry.ModelHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelProductionValidationResult.Fail("ProductionModelHashMismatch", "模型文件 SHA-256 与注册表不一致。");
            }

            string expectedHash = entry.Manifest.EffectiveHash;
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelProductionValidationResult.Fail("ProductionModelManifestHashMismatch", "模型文件 SHA-256 与 manifest 不一致。");
            }

            return ModelProductionValidationResult.Ok(entry, fullPath, actualHash);
        }

        private static bool DefaultWarmup(string modelPath)
        {
            try
            {
                using var session = new InferenceSession(modelPath);
                return session.InputMetadata.Count > 0 || session.OutputMetadata.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsApprovedForProduction(ModelPackageManifest manifest)
        {
            return string.Equals(
                manifest.Approval?.Status,
                ModelApprovalStatuses.Approved,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string NormalizeName(string value)
        {
            string name = Path.GetFileName(value.Trim());
            if (name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            return name;
        }

        private static bool IsPathLike(string value)
        {
            return Path.IsPathRooted(value) ||
                   value.Contains(Path.DirectorySeparatorChar) ||
                   value.Contains(Path.AltDirectorySeparatorChar);
        }

        private static string GetFullPathSafe(string value)
        {
            try
            {
                return Path.GetFullPath(value);
            }
            catch
            {
                return value;
            }
        }
    }
}
