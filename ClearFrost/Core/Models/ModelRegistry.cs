using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace ClearFrost.Core.Models
{
    public sealed class ModelRegistryScanOptions
    {
        public string PackageDirectory { get; init; } = string.Empty;
        public string OnnxDirectory { get; init; } = string.Empty;
        public bool StrictPackageMode { get; init; }
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

        private readonly List<ModelRegistryEntry> _entries = new List<ModelRegistryEntry>();

        public IReadOnlyList<ModelRegistryEntry> Entries => _entries;

        public bool HasBlockingErrors => _entries.Any(e => e.Status == ModelRegistryStatus.Blocked);

        public IReadOnlyList<ModelRegistryEntry> Scan(ModelRegistryScanOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            _entries.Clear();
            ScanPackages(options);
            ScanBareOnnx(options.OnnxDirectory);
            return Entries;
        }

        public ModelRegistryEntry? Resolve(string? usedModelName)
        {
            if (string.IsNullOrWhiteSpace(usedModelName))
            {
                return null;
            }

            string normalized = NormalizeName(usedModelName);
            if (IsPathLike(usedModelName))
            {
                string fullPath = GetFullPathSafe(usedModelName);
                return _entries.FirstOrDefault(e =>
                    string.Equals(GetFullPathSafe(e.ModelPath), fullPath, StringComparison.OrdinalIgnoreCase));
            }

            var candidates = _entries
                .Where(e =>
                    string.Equals(NormalizeName(e.UsedModelName), normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(e.ModelId), normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(Path.GetFileName(e.ModelPath)), normalized, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => GetFullPathSafe(e.ModelPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return candidates.Count == 1 ? candidates[0] : null;
        }

        private void ScanPackages(ModelRegistryScanOptions options)
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
                        _entries.Add(new ModelRegistryEntry
                        {
                            ModelId = Path.GetFileName(directory),
                            UsedModelName = Path.GetFileName(onnxFiles[0]),
                            ModelPath = onnxFiles[0],
                            IsPackage = true,
                            Status = options.StrictPackageMode ? ModelRegistryStatus.Blocked : ModelRegistryStatus.Warning,
                            Message = options.StrictPackageMode ? "Model package manifest is missing." : "Manifest missing; package kept as warning for compatibility."
                        });
                    }

                    continue;
                }

                _entries.Add(ValidatePackage(directory, manifestPath, options));
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
                    if (options.StrictPackageMode)
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
                if (options.StrictPackageMode)
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
                Manifest = manifest
            };
        }

        private void ScanBareOnnx(string onnxDirectory)
        {
            if (string.IsNullOrWhiteSpace(onnxDirectory) || !Directory.Exists(onnxDirectory))
            {
                return;
            }

            foreach (string modelPath in Directory.EnumerateFiles(onnxDirectory, "*.onnx", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(modelPath);
                _entries.Add(new ModelRegistryEntry
                {
                    ModelId = Path.GetFileNameWithoutExtension(fileName),
                    Version = "legacy",
                    ModelHash = ComputeSha256(modelPath),
                    UsedModelName = fileName,
                    ModelPath = modelPath,
                    IsPackage = false,
                    Status = ModelRegistryStatus.Warning,
                    Message = "Bare ONNX model discovered; kept for legacy compatibility."
                });
            }
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
