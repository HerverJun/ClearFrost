using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using ClearFrost.Core.DeepLearning;
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

        public ProductionModelResolutionResult ResolveReference(
            ProductionModelReference? reference,
            bool requireProductionApproval)
        {
            ProductionModelReference normalizedReference = reference?.Clone() ?? ProductionModelReference.Empty();
            if (normalizedReference.IsEmpty)
            {
                return ProductionModelResolutionResult.Fail(
                    normalizedReference,
                    "ModelReferenceEmpty",
                    "模型引用为空。");
            }

            return normalizedReference.Type switch
            {
                ProductionModelReferenceType.ApprovedPackage =>
                    ResolveApprovedReference(normalizedReference),
                ProductionModelReferenceType.LegacyOnnx =>
                    ResolveLegacyReference(normalizedReference, requireProductionApproval),
                _ => ProductionModelResolutionResult.Fail(
                    normalizedReference,
                    "ModelReferenceTypeUnsupported",
                    $"不支持的模型引用类型: {normalizedReference.Type}")
            };
        }

        public ProductionModelResolutionResult MigrateLegacyReference(
            string? legacyValue,
            bool requireProductionApproval)
        {
            string raw = legacyValue?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return ProductionModelResolutionResult.Ok(
                    ProductionModelReference.Empty(),
                    new ModelRegistryEntry(),
                    string.Empty);
            }

            if (ProductionModelReference.TryParseSelectionValue(raw, out ProductionModelReference parsed) &&
                !parsed.IsEmpty)
            {
                return ResolveReference(parsed, requireProductionApproval);
            }

            return requireProductionApproval
                ? MigrateLegacyToApprovedReference(raw)
                : MigrateLegacyToOnnxReference(raw);
        }

        public IReadOnlyList<ProductionModelSelectionOption> GetProductionSelectionOptions(
            bool requireProductionApproval)
        {
            IEnumerable<ModelRegistryEntry> candidates = Entries;
            candidates = requireProductionApproval
                ? candidates.Where(e =>
                    e.IsPackage &&
                    e.Status == ModelRegistryStatus.Ready &&
                    e.ApprovedForProduction)
                : candidates.Where(e =>
                    e.Status != ModelRegistryStatus.Blocked &&
                    (e.IsPackage || !string.IsNullOrWhiteSpace(e.UsedModelName)));

            return candidates
                .Select(ToSelectionOption)
                .Where(option => !string.IsNullOrWhiteSpace(option.Value))
                .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(option => option.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private ProductionModelResolutionResult ResolveApprovedReference(ProductionModelReference reference)
        {
            string modelId = reference.ModelId?.Trim() ?? string.Empty;
            string version = reference.Version?.Trim() ?? string.Empty;
            string sha256 = reference.Sha256?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelId) ||
                string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(sha256))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelIdentityIncomplete",
                    "批准模型身份不完整。");
            }

            var matches = Entries
                .Where(e =>
                    e.IsPackage &&
                    string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Version, version, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.ModelHash, sha256, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelIdentityMissing",
                    $"Registry 未找到批准模型身份: {reference}");
            }

            if (matches.Count > 1)
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelIdentityDuplicate",
                    $"Registry 存在重复批准模型身份: {reference}");
            }

            ModelRegistryEntry entry = matches[0];
            if (entry.Status != ModelRegistryStatus.Ready)
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelNotReady",
                    entry.Message);
            }

            if (!entry.ApprovedForProduction)
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelNotApproved",
                    $"模型未批准: {entry.ApprovalStatus}");
            }

            if (!File.Exists(entry.ModelPath))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelFileMissing",
                    $"模型文件不存在: {entry.ModelPath}");
            }

            if (!TryComputeModelSha256(entry.ModelPath, out string actualHash, out string hashError))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelPathUnsafe",
                    hashError);
            }

            if (!string.Equals(actualHash, sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualHash, entry.ModelHash, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelHashMismatch",
                    "模型文件 SHA-256 与持久化身份或 Registry 不一致。");
            }

            string expectedHash = entry.Manifest?.EffectiveHash ?? string.Empty;
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "ApprovedModelManifestHashMismatch",
                    "模型文件 SHA-256 与 manifest 不一致。");
            }

            return ProductionModelResolutionResult.Ok(reference, entry, entry.ModelPath);
        }

        private ProductionModelResolutionResult ResolveLegacyReference(
            ProductionModelReference reference,
            bool requireProductionApproval)
        {
            if (requireProductionApproval)
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "LegacyModelNotAllowed",
                    "生产准入开启时禁止使用裸 ONNX 模型引用。");
            }

            string fileName = Path.GetFileName(reference.LegacyFileName?.Trim() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "LegacyModelFileNameEmpty",
                    "Legacy ONNX 文件名为空。");
            }

            var matches = Entries
                .Where(e =>
                    !e.IsPackage &&
                    string.Equals(e.UsedModelName, fileName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => GetFullPathSafe(e.ModelPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (matches.Count == 0)
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "LegacyModelMissing",
                    $"Registry 未找到裸 ONNX 模型: {fileName}");
            }

            if (matches.Count > 1)
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "LegacyModelAmbiguous",
                    $"裸 ONNX 模型文件名不唯一: {fileName}");
            }

            ModelRegistryEntry entry = matches[0];
            if (!File.Exists(entry.ModelPath))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "LegacyModelFileMissing",
                    $"模型文件不存在: {entry.ModelPath}");
            }

            if (!TryComputeModelSha256(entry.ModelPath, out string actualHash, out string hashError))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "LegacyModelPathUnsafe",
                    hashError);
            }

            if (!string.IsNullOrWhiteSpace(reference.Sha256) &&
                !string.Equals(reference.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelResolutionResult.Fail(
                    reference,
                    "LegacyModelHashMismatch",
                    "Legacy ONNX 文件 SHA-256 与持久化身份不一致。");
            }

            ProductionModelReference resolvedReference = ProductionModelReference.FromLegacyOnnx(fileName, actualHash);
            return ProductionModelResolutionResult.Ok(resolvedReference, entry, entry.ModelPath);
        }

        private ProductionModelResolutionResult MigrateLegacyToApprovedReference(string legacyValue)
        {
            var matches = FindLegacyCandidates(legacyValue)
                .Where(e =>
                    e.IsPackage &&
                    e.Status == ModelRegistryStatus.Ready &&
                    e.ApprovedForProduction)
                .GroupBy(e => $"{e.ModelId}\n{e.Version}\n{e.ModelHash}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            ProductionModelReference inputReference = ProductionModelReference.FromLegacyOnnx(legacyValue);
            if (matches.Count == 0)
            {
                return ProductionModelResolutionResult.Fail(
                    inputReference,
                    "LegacyModelCannotMapToApproved",
                    $"旧模型配置无法唯一映射到批准模型包: {legacyValue}");
            }

            if (matches.Count > 1)
            {
                return ProductionModelResolutionResult.Fail(
                    inputReference,
                    "LegacyModelApprovedMappingAmbiguous",
                    $"旧模型配置匹配多个批准模型包: {legacyValue}");
            }

            ProductionModelReference approved = ProductionModelReference.FromApprovedPackage(matches[0]);
            return ResolveApprovedReference(approved);
        }

        private ProductionModelResolutionResult MigrateLegacyToOnnxReference(string legacyValue)
        {
            var matches = FindLegacyCandidates(legacyValue)
                .Where(e => !e.IsPackage)
                .GroupBy(e => GetFullPathSafe(e.ModelPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            ProductionModelReference inputReference = ProductionModelReference.FromLegacyOnnx(legacyValue);
            if (matches.Count == 0)
            {
                return ProductionModelResolutionResult.Fail(
                    inputReference,
                    "LegacyModelMissing",
                    $"旧模型配置未在 Registry 中找到: {legacyValue}");
            }

            if (matches.Count > 1)
            {
                return ProductionModelResolutionResult.Fail(
                    inputReference,
                    "LegacyModelAmbiguous",
                    $"旧模型配置匹配多个裸 ONNX 条目: {legacyValue}");
            }

            ModelRegistryEntry entry = matches[0];
            if (!File.Exists(entry.ModelPath))
            {
                return ProductionModelResolutionResult.Fail(
                    inputReference,
                    "LegacyModelFileMissing",
                    $"模型文件不存在: {entry.ModelPath}");
            }

            if (!TryComputeModelSha256(entry.ModelPath, out string actualHash, out string hashError))
            {
                return ProductionModelResolutionResult.Fail(
                    inputReference,
                    "LegacyModelPathUnsafe",
                    hashError);
            }

            ProductionModelReference legacyReference = ProductionModelReference.FromLegacyOnnx(entry.UsedModelName, actualHash);
            return ProductionModelResolutionResult.Ok(legacyReference, entry, entry.ModelPath);
        }

        private IReadOnlyList<ModelRegistryEntry> FindLegacyCandidates(string legacyValue)
        {
            string raw = legacyValue?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<ModelRegistryEntry>();
            }

            string fullPath = IsPathLike(raw) ? GetFullPathSafe(raw) : string.Empty;
            string fileName = Path.GetFileName(raw);
            string normalized = NormalizeName(raw);

            return Entries
                .Where(e =>
                    (!string.IsNullOrWhiteSpace(fullPath) &&
                     string.Equals(GetFullPathSafe(e.ModelPath), fullPath, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(e.UsedModelName, fileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(e.ModelPath), fileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(e.ModelId), normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static ProductionModelSelectionOption ToSelectionOption(ModelRegistryEntry entry)
        {
            ProductionModelReference reference = entry.IsPackage
                ? ProductionModelReference.FromApprovedPackage(entry)
                : ProductionModelReference.FromLegacyOnnx(entry.UsedModelName, entry.ModelHash);
            string fileName = Path.GetFileName(entry.ModelPath);
            string text = entry.IsPackage
                ? $"{entry.ModelId} / {entry.Version} / {fileName}"
                : fileName;

            return new ProductionModelSelectionOption
            {
                Value = reference.ToSelectionValue(),
                Text = text,
                ModelId = entry.ModelId ?? string.Empty,
                Version = entry.Version ?? string.Empty,
                Sha256 = entry.ModelHash ?? string.Empty,
                FileName = fileName,
                TaskType = ResolveTaskType(entry),
                PostprocessorKey = ResolvePostprocessorKey(entry),
                ScoreNormalization = ResolveScoreNormalization(entry),
                PostprocessOptions = CopyPostprocessOptions(ResolvePostprocessOptions(entry)),
                InputWidth = ResolveInputWidth(entry),
                InputHeight = ResolveInputHeight(entry),
                LabelCount = ResolveLabels(entry).Count(label => !string.IsNullOrWhiteSpace(label)),
                IsApprovedPackage = entry.IsPackage
            };
        }

        private static string ResolveTaskType(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveTaskType();
        }

        private static string ResolvePostprocessorKey(ModelRegistryEntry entry)
        {
            return entry.GetEffectivePostprocessorKey();
        }

        private static string ResolveScoreNormalization(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveScoreNormalization();
        }

        private static IReadOnlyList<string> ResolveLabels(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveLabels();
        }

        private static int ResolveInputWidth(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveInputWidth();
        }

        private static int ResolveInputHeight(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveInputHeight();
        }

        private static IReadOnlyDictionary<string, string>? ResolvePostprocessOptions(ModelRegistryEntry entry)
        {
            return entry.GetEffectivePostprocessOptions();
        }

        private void ScanPackages(ModelRegistryScanOptions options, List<ModelRegistryEntry> entries)
        {
            string packageDirectory = options.PackageDirectory;
            if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
            {
                return;
            }

            if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(packageDirectory))
            {
                entries.Add(CreateBlockedPackageEntry(
                    Path.GetFileName(Path.GetFullPath(packageDirectory)),
                    packageDirectory,
                    "Model package root is a reparse point."));
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(packageDirectory))
            {
                if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory))
                {
                    entries.Add(CreateBlockedPackageEntry(
                        Path.GetFileName(directory),
                        directory,
                        "Model package directory is a reparse point."));
                    continue;
                }

                string manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    string[] onnxFiles = Directory.GetFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly);
                    if (onnxFiles.Length > 0)
                    {
                        if (ModelPackagePathGuard.HasReparsePoint(new FileInfo(onnxFiles[0])))
                        {
                            entries.Add(CreateBlockedPackageEntry(
                                Path.GetFileName(directory),
                                onnxFiles[0],
                                "Model package ONNX file is a reparse point."));
                            continue;
                        }

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
                if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory))
                {
                    failures.Add("Model package directory path contains a reparse point.");
                }
                else if (ModelPackagePathGuard.HasReparsePoint(new FileInfo(manifestPath)))
                {
                    failures.Add("Manifest file is a reparse point.");
                }
                else
                {
                    using var stream = new FileStream(
                        manifestPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.SequentialScan);

                    if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory) ||
                        ModelPackagePathGuard.HasReparsePoint(new FileInfo(manifestPath)))
                    {
                        failures.Add("Manifest file path became unsafe before read.");
                    }
                    else
                    {
                        manifest = JsonSerializer.Deserialize<ModelPackageManifest>(stream, JsonOptions);
                    }
                }
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
            bool modelPathResolved = ModelPackagePathGuard.TryResolveModelPath(
                directory,
                modelFileName,
                out string modelPath,
                out string modelPathError);
            string computedHash = string.Empty;

            if (!modelPathResolved)
            {
                failures.Add(modelPathError);
            }
            else if (ModelPackagePathGuard.ModelPathHasReparsePoint(directory, modelPath))
            {
                failures.Add("Model file path contains a reparse point.");
            }
            else if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(Path.GetDirectoryName(modelPath) ?? string.Empty))
            {
                failures.Add("Model file directory path contains a reparse point.");
            }
            else if (!File.Exists(modelPath))
            {
                failures.Add($"Model file is missing: {modelFileName}");
            }
            else if (ModelPackagePathGuard.HasReparsePoint(new FileInfo(modelPath)))
            {
                failures.Add("Model file is a reparse point.");
            }
            else if (!TryComputeModelSha256(modelPath, out computedHash, out string hashError))
            {
                failures.Add(hashError);
            }
            else
            {
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

            bool manifestApproved = IsApprovedForProduction(manifest);

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

                if (!manifestApproved)
                {
                    failures.Add("Model is not approved for production.");
                }
            }

            ValidateDeepLearningPostprocessorConfiguration(manifest, options, warnings, failures);

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
                PostprocessorKey = manifest.PostprocessorKey ?? string.Empty,
                ScoreNormalization = manifest.ScoreNormalization ?? string.Empty,
                PostprocessOptions = CopyPostprocessOptions(manifest.PostprocessOptions),
                InputWidth = manifest.InputWidth,
                InputHeight = manifest.InputHeight,
                ApprovalStatus = manifest.Approval?.Status ?? ModelApprovalStatuses.Pending,
                ApprovedForProduction = status == ModelRegistryStatus.Ready && manifestApproved
            };
        }

        private static void ValidateDeepLearningPostprocessorConfiguration(
            ModelPackageManifest manifest,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            string postprocessorKey = (manifest.PostprocessorKey ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(postprocessorKey) &&
                !DeepLearningPostprocessorConfiguration.IsKnownPostprocessorKey(postprocessorKey))
            {
                AddPackageConfigurationIssue(
                    options,
                    warnings,
                    failures,
                    $"Unknown deep learning postprocessor key: {postprocessorKey}. Known postprocessor keys: {FormatKnownPostprocessorKeys()}.");
            }

            string scoreNormalization = (manifest.ScoreNormalization ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(scoreNormalization) &&
                !DeepLearningPostprocessorConfiguration.TryParseScoreNormalization(scoreNormalization, out _))
            {
                AddPackageConfigurationIssue(
                    options,
                    warnings,
                    failures,
                    $"Unknown score normalization: {scoreNormalization}. Known score normalizations: {FormatKnownScoreNormalizations()}.");
            }

            ValidatePostprocessOptions(manifest.PostprocessOptions, postprocessorKey, manifest.TaskType, options, warnings, failures);
        }

        private static void ValidatePostprocessOptions(
            IDictionary<string, string>? postprocessOptions,
            string postprocessorKey,
            string? taskType,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            if (postprocessOptions == null || postprocessOptions.Count == 0)
            {
                return;
            }

            var seenKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in postprocessOptions)
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    AddPackageConfigurationIssue(
                        options,
                        warnings,
                        failures,
                        "Postprocess option key is empty.");
                    continue;
                }

                if (seenKeys.TryGetValue(key, out string? existingKey))
                {
                    AddPackageConfigurationIssue(
                        options,
                        warnings,
                        failures,
                        $"Duplicate postprocess option key: {key} conflicts with {existingKey ?? key}.");
                    continue;
                }

                seenKeys[key] = key;
                ValidateKnownPostprocessOptionValue(key, pair.Value, postprocessorKey, taskType, options, warnings, failures);
            }
        }

        private static void ValidateKnownPostprocessOptionValue(
            string key,
            string? value,
            string postprocessorKey,
            string? taskType,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            string normalizedKey = key.Trim().ToLowerInvariant();
            string normalizedValue = (value ?? string.Empty).Trim();

            switch (normalizedKey)
            {
                case "apply_nms":
                case "nms":
                case "normalized_boxes":
                case "boxes_normalized":
                case "normalized_coordinates":
                case "coordinates_normalized":
                    ValidateBooleanPostprocessOption(key, normalizedValue, options, warnings, failures);
                    break;
                case "label_map":
                case "is_label_map":
                    ValidateLabelMapHintPostprocessOption(key, normalizedValue, options, warnings, failures);
                    break;
                case "output_type":
                case "output_format":
                case "segmentation_format":
                case "mask_format":
                    if (IsSemanticSegmentationOptionScope(postprocessorKey, taskType) ||
                        IsLabelMapHintValue(normalizedValue))
                    {
                        ValidateSemanticOutputHintPostprocessOption(key, normalizedValue, options, warnings, failures);
                    }

                    break;
                case "top_k":
                case "topk":
                case "max_results":
                case "classification_limit":
                case "limit":
                    ValidateNonNegativeIntegerPostprocessOption(key, normalizedValue, options, warnings, failures);
                    break;
                case "score_index":
                case "confidence_index":
                case "conf_index":
                case "class_index":
                case "class_id_index":
                case "label_index":
                case "class_id":
                case "default_class_id":
                case "foreground_class_id":
                    ValidateNonNegativeIntegerPostprocessOption(key, normalizedValue, options, warnings, failures);
                    break;
                case "class_id_offset":
                case "class_offset":
                case "label_offset":
                    ValidateIntegerPostprocessOption(key, normalizedValue, options, warnings, failures);
                    break;
                case "background_class_id":
                case "background_index":
                case "ignore_class_id":
                case "ignored_class_id":
                case "no_object_class_id":
                case "no_object_index":
                    ValidateIgnoredClassPostprocessOption(key, normalizedValue, options, warnings, failures);
                    break;
                case "box_format":
                case "bbox_format":
                case "coordinate_format":
                    ValidateEnumPostprocessOption(
                        key,
                        normalizedValue,
                        new[] { "xyxy", "xywh", "cxcywh", "center", "center-xywh", "yxyx", "yminxminymaxxmax", "tensorflow", "tf" },
                        "box format",
                        options,
                        warnings,
                        failures);
                    break;
                case "box_units":
                case "coordinate_units":
                case "coordinates":
                    ValidateEnumPostprocessOption(
                        key,
                        normalizedValue,
                        new[] { "normalized", "relative", "pixel", "pixels", "absolute", "raw" },
                        "coordinate units",
                        options,
                        warnings,
                        failures);
                    break;
                case "segmentation_layout":
                case "mask_layout":
                case "output_layout":
                case "layout":
                    ValidateNormalizedEnumPostprocessOption(
                        key,
                        normalizedValue,
                        new[] { "chw", "hwc", "nchw", "nhwc", "bhw" },
                        "tensor layout",
                        options,
                        warnings,
                        failures);
                    break;
            }
        }

        private static void ValidateBooleanPostprocessOption(
            string key,
            string value,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            string normalized = value.Trim().ToLowerInvariant();
            if (normalized is "1" or "true" or "yes" or "on" or "enabled" or
                "0" or "false" or "no" or "off" or "disabled" or "none")
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Invalid boolean postprocess option {key}: {value}.");
        }

        private static void ValidateLabelMapHintPostprocessOption(
            string key,
            string value,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            if (IsLabelMapHintValue(value) || IsFalseLikeValue(value))
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Invalid label map hint postprocess option {key}: {value}. Allowed values: true, false, label-map, class-map, class-id, class-ids, class-index, class-indices.");
        }

        private static void ValidateSemanticOutputHintPostprocessOption(
            string key,
            string value,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            string normalized = NormalizeSemanticHintValue(value);
            if (IsLabelMapHintValue(value) ||
                normalized is "logit" or "logits" or "score" or "scores" or "score-map" or "scoremap" or
                    "probability" or "probabilities" or "probability-map" or "probabilitymap" or
                    "semantic-map" or "semanticmap" or "mask" or "mask-map" or "maskmap")
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Invalid semantic output hint postprocess option {key}: {value}. Allowed values include label-map, class-map, logits, probabilities, score-map, semantic-map, and mask.");
        }

        private static bool IsSemanticSegmentationOptionScope(string? postprocessorKey, string? taskType)
        {
            string key = NormalizeSemanticHintValue(postprocessorKey);
            if (key is "semantic-segmentation" or "semanticsegmentation" or "multiclass-segmentation" or
                "multiclasssegmentation" or "multi-class-segmentation" or "multiclass-segmentation" or
                "segmentation" or "deeplab" or "unet-segmentation" or "unetsegmentation")
            {
                return true;
            }

            string task = NormalizeSemanticHintValue(taskType);
            return task is "segmentation" or "segment" or "semantic-segmentation" or "semanticsegmentation" or "semantic";
        }

        private static bool IsLabelMapHintValue(string? value)
        {
            string normalized = NormalizeSemanticHintValue(value);
            return normalized is "1" or "true" or "yes" or "on" or "enabled" or
                "label-map" or "labelmap" or "class-map" or "classmap" or
                "class-id" or "classid" or "class-ids" or "classids" or
                "class-index" or "classindex" or "class-indices" or "classindices";
        }

        private static bool IsFalseLikeValue(string? value)
        {
            string normalized = NormalizeSemanticHintValue(value);
            return normalized is "0" or "false" or "no" or "off" or "disabled" or "none";
        }

        private static string NormalizeSemanticHintValue(string? value)
        {
            return (value ?? string.Empty).Trim().Replace("_", "-").ToLowerInvariant();
        }

        private static void ValidateIntegerPostprocessOption(
            string key,
            string value,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            if (int.TryParse(value, out _))
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Postprocess option {key} must be an integer.");
        }

        private static void ValidateNonNegativeIntegerPostprocessOption(
            string key,
            string value,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            if (int.TryParse(value, out int parsed) && parsed >= 0)
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Postprocess option {key} must be a non-negative integer.");
        }

        private static void ValidateIgnoredClassPostprocessOption(
            string key,
            string value,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            string normalized = value.Trim();
            if (normalized.Equals("last", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("final", StringComparison.OrdinalIgnoreCase) ||
                int.TryParse(normalized, out int classId) && classId >= 0)
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Postprocess option {key} must be a non-negative integer, last, or final.");
        }

        private static void ValidateEnumPostprocessOption(
            string key,
            string value,
            IReadOnlyCollection<string> allowedValues,
            string valueKind,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            string normalized = value.Trim().ToLowerInvariant();
            if (allowedValues.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Invalid {valueKind} postprocess option {key}: {value}. Allowed values: {string.Join(", ", allowedValues)}.");
        }

        private static void ValidateNormalizedEnumPostprocessOption(
            string key,
            string value,
            IReadOnlyCollection<string> allowedValues,
            string valueKind,
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures)
        {
            string normalized = value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            if (allowedValues.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            AddPackageConfigurationIssue(
                options,
                warnings,
                failures,
                $"Invalid {valueKind} postprocess option {key}: {value}. Allowed values: {string.Join(", ", allowedValues)}.");
        }

        private static string FormatKnownPostprocessorKeys()
        {
            return string.Join(", ", DeepLearningPostprocessorConfiguration.KnownPostprocessorKeys
                .Concat(new[] { "yolo", "yolov8" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        }

        private static string FormatKnownScoreNormalizations()
        {
            return string.Join(", ", DeepLearningPostprocessorConfiguration.KnownScoreNormalizationValues
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }

        private static IReadOnlyDictionary<string, string> CopyPostprocessOptions(IReadOnlyDictionary<string, string>? options)
        {
            if (options == null || options.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in options)
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || copy.ContainsKey(key))
                {
                    continue;
                }

                copy[key] = pair.Value ?? string.Empty;
            }

            return copy;
        }

        private static void AddPackageConfigurationIssue(
            ModelRegistryScanOptions options,
            List<string> warnings,
            List<string> failures,
            string message)
        {
            if (options.StrictPackageMode || options.RequireProductionApproval)
            {
                failures.Add(message);
            }
            else
            {
                warnings.Add(message);
            }
        }

        private void ScanBareOnnx(string onnxDirectory, bool requireProductionApproval, List<ModelRegistryEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(onnxDirectory) || !Directory.Exists(onnxDirectory))
            {
                return;
            }

            if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(onnxDirectory))
            {
                entries.Add(new ModelRegistryEntry
                {
                    ModelId = Path.GetFileName(Path.GetFullPath(onnxDirectory)),
                    Version = "legacy",
                    ModelPath = onnxDirectory,
                    IsPackage = false,
                    Status = ModelRegistryStatus.Blocked,
                    Message = "Bare ONNX root is a reparse point.",
                    ApprovalStatus = ModelApprovalStatuses.Legacy,
                    ApprovedForProduction = false
                });
                return;
            }

            foreach (string modelPath in Directory.EnumerateFiles(onnxDirectory, "*.onnx", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(modelPath);
                if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(Path.GetDirectoryName(modelPath) ?? string.Empty) ||
                    ModelPackagePathGuard.HasReparsePoint(new FileInfo(modelPath)))
                {
                    entries.Add(new ModelRegistryEntry
                    {
                        ModelId = Path.GetFileNameWithoutExtension(fileName),
                        Version = "legacy",
                        UsedModelName = fileName,
                        ModelPath = modelPath,
                        IsPackage = false,
                        Status = ModelRegistryStatus.Blocked,
                        Message = "Bare ONNX model file is a reparse point.",
                        ApprovalStatus = ModelApprovalStatuses.Legacy,
                        ApprovedForProduction = false
                    });
                    continue;
                }

                if (!TryComputeModelSha256(modelPath, out string modelHash, out string hashError))
                {
                    entries.Add(new ModelRegistryEntry
                    {
                        ModelId = Path.GetFileNameWithoutExtension(fileName),
                        Version = "legacy",
                        UsedModelName = fileName,
                        ModelPath = modelPath,
                        IsPackage = false,
                        Status = ModelRegistryStatus.Blocked,
                        Message = hashError,
                        ApprovalStatus = ModelApprovalStatuses.Legacy,
                        ApprovedForProduction = false
                    });
                    continue;
                }

                entries.Add(new ModelRegistryEntry
                {
                    ModelId = Path.GetFileNameWithoutExtension(fileName),
                    Version = "legacy",
                    ModelHash = modelHash,
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

        private static ModelRegistryEntry CreateBlockedPackageEntry(
            string modelId,
            string modelPath,
            string message)
        {
            return new ModelRegistryEntry
            {
                ModelId = string.IsNullOrWhiteSpace(modelId) ? "blocked-package" : modelId,
                Version = string.Empty,
                UsedModelName = Path.GetFileName(modelPath),
                ModelPath = modelPath,
                IsPackage = true,
                Status = ModelRegistryStatus.Blocked,
                Message = message,
                ApprovalStatus = ModelApprovalStatuses.Pending,
                ApprovedForProduction = false
            };
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

            if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(Path.GetDirectoryName(fullPath) ?? string.Empty) ||
                ModelPackagePathGuard.HasReparsePoint(new FileInfo(fullPath)))
            {
                return ModelProductionValidationResult.Fail("ProductionModelPathUnsafe", "模型文件路径包含链接，禁止进入生产。");
            }

            if (entry.Status != ModelRegistryStatus.Ready)
            {
                return ModelProductionValidationResult.Fail("ProductionModelRegistryBlocked", entry.Message);
            }

            if (!entry.ApprovedForProduction)
            {
                return ModelProductionValidationResult.Fail("ProductionModelNotApproved", $"模型未批准: {entry.ApprovalStatus}");
            }

            IReadOnlyList<string> labels = ResolveLabels(entry);
            if (labels.Count == 0 || labels.All(string.IsNullOrWhiteSpace))
            {
                return ModelProductionValidationResult.Fail("ProductionModelLabelsMissing", "模型类别元数据缺失。");
            }

            if (ResolveInputWidth(entry) <= 0 || ResolveInputHeight(entry) <= 0)
            {
                return ModelProductionValidationResult.Fail("ProductionModelInputSizeMissing", "模型输入尺寸元数据缺失。");
            }

            if (string.IsNullOrWhiteSpace(ResolveTaskType(entry)))
            {
                return ModelProductionValidationResult.Fail("ProductionModelTaskTypeMissing", "模型任务类型元数据缺失。");
            }

            if (!TryComputeModelSha256(fullPath, out string actualHash, out string hashError))
            {
                return ModelProductionValidationResult.Fail("ProductionModelPathUnsafe", hashError);
            }

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

        private static bool TryComputeModelSha256(string path, out string sha256, out string error)
        {
            sha256 = string.Empty;
            error = string.Empty;

            try
            {
                string fullPath = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) ||
                    ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory))
                {
                    error = "Model file directory path contains a reparse point.";
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                if (!file.Exists)
                {
                    error = "Model file is missing.";
                    return false;
                }

                if (ModelPackagePathGuard.HasReparsePoint(file))
                {
                    error = "Model file is a reparse point.";
                    return false;
                }

                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                file = new FileInfo(fullPath);
                file.Refresh();
                if (ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory) ||
                    ModelPackagePathGuard.HasReparsePoint(file))
                {
                    error = "Model file path became unsafe before hash.";
                    return false;
                }

                using var sha256Algorithm = SHA256.Create();
                sha256 = Convert.ToHexString(sha256Algorithm.ComputeHash(stream)).ToLowerInvariant();
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                error = $"Model hash failed: {ex.Message}";
                return false;
            }
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
