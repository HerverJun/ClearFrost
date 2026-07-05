using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Models;
using ClearFrost.Core.Rules;
using ClearFrost.Interfaces;
using ClearFrost.Yolo;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace ClearFrost.Services.Replay
{
    internal sealed class ReplayApprovalEvidenceProductionGate
    {
        private readonly IModelApprovalEvidenceStore _evidenceStore;
        private readonly IReplayDatasetStore _datasetStore;
        private readonly IReplayRunStore _runStore;

        public ReplayApprovalEvidenceProductionGate(
            IModelApprovalEvidenceStore evidenceStore,
            IReplayDatasetStore datasetStore,
            IReplayRunStore runStore)
        {
            _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        }

        public ProductionModelReadinessResult Validate(
            ModelRole role,
            ModelRegistryEntry entry,
            ProductionModelReference? currentReference)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (role != ModelRole.Primary && role != ModelRole.Auxiliary1 && role != ModelRole.Auxiliary2)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceSlotMissing",
                    "Production evidence validation requires an explicit model slot role.");
            }

            ProductionModelReference expectedReference = ProductionModelReference.FromApprovedPackage(entry);
            ProductionModelReference slotReference = currentReference?.Clone() ?? ProductionModelReference.Empty();
            if (slotReference.IsEmpty ||
                !slotReference.IdentityEquals(expectedReference))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceConfigReferenceMismatch",
                    "Production evidence validation is not bound to the current AppConfig slot reference.");
            }

            if (entry.Manifest?.Approval == null)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceMissing",
                    "Model manifest approval metadata is missing.");
            }

            if (string.IsNullOrWhiteSpace(entry.Manifest.Approval.ReplayEvidenceId))
            {
                return ValidateLegacyApproval(role, entry, entry.Manifest.Approval, slotReference, expectedReference);
            }

            return ValidateEvidenceBacked(entry);
        }

        public ProductionModelReadinessResult ValidateEvidenceBacked(ModelRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Manifest?.Approval == null)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceMissing",
                    "Model manifest approval metadata is missing.");
            }

            if (string.IsNullOrWhiteSpace(entry.Manifest.Approval.ReplayEvidenceId))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceMissing",
                    "Replay approval evidence id is missing.");
            }

            ModelApprovalEvidenceValidationResult result = _evidenceStore.ValidateEvidence(
                ReplayModelIdentity.FromRegistryEntry(entry),
                entry.Manifest.Approval.ReplayEvidenceId,
                entry.Manifest.Approval.ReplayEvidenceHash,
                _datasetStore,
                _runStore);

            return result.Succeeded
                ? ProductionModelReadinessResult.Ok()
                : ProductionModelReadinessResult.Fail(result.ErrorCode, result.Message);
        }

        private ProductionModelReadinessResult ValidateLegacyApproval(
            ModelRole role,
            ModelRegistryEntry entry,
            ModelApprovalMetadata approval,
            ProductionModelReference slotReference,
            ProductionModelReference expectedReference)
        {
            ModelApprovalLegacyMigration? legacy = approval.LegacyMigration;
            if (legacy == null)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceMissing",
                    "Replay approval evidence id is missing.");
            }

            if (!string.Equals(approval.Status, ModelApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyApprovalStatusInvalid",
                    "Legacy migration can only authorize an already approved manifest.");
            }

            if (!string.Equals(legacy.ModelRole, role.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyRoleMismatch",
                    "Legacy migration is only valid for the original model slot.");
            }

            string expectedConfigReference = expectedReference.ToSelectionValue();
            if (slotReference.IsEmpty ||
                !slotReference.IdentityEquals(expectedReference) ||
                !string.Equals(slotReference.ToSelectionValue(), legacy.ConfigReference, StringComparison.Ordinal))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyConfigReferenceMismatch",
                    "Legacy migration is not bound to the current AppConfig model slot reference.");
            }

            if (!string.Equals(legacy.ConfigReference, expectedConfigReference, StringComparison.Ordinal) ||
                !string.Equals(legacy.ModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(legacy.Version, entry.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(legacy.ModelHash, entry.ModelHash, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyIdentityMismatch",
                    "Legacy migration identity does not match the registry entry.");
            }

            if (string.IsNullOrWhiteSpace(entry.ModelPath) || !File.Exists(entry.ModelPath))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyModelFileMissing",
                    "Legacy-approved model file is missing.");
            }

            if (!IsSafeReplayModelFileForRead(entry.ModelPath))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyModelHashUnavailable",
                    "Legacy-approved model file path contains a reparse point.");
            }

            string actualHash;
            try
            {
                actualHash = FileReplayDatasetStore.ComputeSha256(entry.ModelPath);
            }
            catch (Exception ex)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyModelHashUnavailable",
                    ex.Message);
            }

            string manifestHash;
            try
            {
                manifestHash = ComputeLegacyManifestHash(entry.ManifestPath);
            }
            catch (Exception ex)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyManifestHashUnavailable",
                    ex.Message);
            }

            if (string.IsNullOrWhiteSpace(legacy.ModelHash) ||
                string.IsNullOrWhiteSpace(legacy.ManifestHash) ||
                !string.Equals(actualHash, legacy.ModelHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualHash, entry.ModelHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(legacy.ManifestHash, manifestHash, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyHashMismatch",
                    "Legacy migration hash no longer matches the model file or manifest.");
            }

            if (legacy.MigratedAt == default)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayLegacyMigrationTimestampMissing",
                    "Legacy migration timestamp is missing.");
            }

            return ProductionModelReadinessResult.Ok();
        }

        internal static string ComputeLegacyManifestHash(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new FileNotFoundException("Legacy-approved manifest file is missing.", manifestPath);
            }

            string fullPath = Path.GetFullPath(manifestPath);
            if (!IsSafeManifestFileForRead(fullPath))
            {
                throw new IOException("Legacy-approved manifest file path contains a reparse point.");
            }

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (!IsSafeManifestFileForRead(fullPath))
            {
                throw new IOException("Legacy-approved manifest file path became unsafe before read.");
            }

            ModelPackageManifest manifest =
                JsonSerializer.Deserialize<ModelPackageManifest>(stream, ReplayJson.Options) ??
                new ModelPackageManifest();

            if (!IsSafeManifestFileForRead(fullPath))
            {
                throw new IOException("Legacy-approved manifest file path became unsafe after read.");
            }

            return ComputeLegacyManifestHash(manifest);
        }

        internal static string ComputeLegacyManifestHash(ModelPackageManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            string json = JsonSerializer.Serialize(manifest, ReplayJson.Options);
            ModelPackageManifest canonical = JsonSerializer.Deserialize<ModelPackageManifest>(
                json,
                ReplayJson.Options) ?? new ModelPackageManifest();
            if (canonical.Approval?.LegacyMigration != null)
            {
                canonical.Approval.LegacyMigration.ManifestHash = string.Empty;
            }

            return FileReplayDatasetStore.ComputeSha256(
                JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }

        private static bool IsSafeManifestFileForRead(string manifestPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(manifestPath);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) ||
                    ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                return file.Exists && !ModelPackagePathGuard.HasReparsePoint(file);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeReplayModelFileForRead(string modelPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(modelPath);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) ||
                    ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                return file.Exists && !ModelPackagePathGuard.HasReparsePoint(file);
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class ReplayModelValidator : IReplayModelValidator
    {
        private readonly Func<string, ModelPackageManifest, bool> _warmup;

        public ReplayModelValidator(Func<string, ModelPackageManifest, bool>? warmup = null)
        {
            _warmup = warmup ?? DefaultWarmup;
        }

        public Task<ReplayModelValidationResult> ValidateAsync(
            ReplayModelIdentity model,
            ReplayModelValidationOptions options,
            CancellationToken cancellationToken = default)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            options ??= new ReplayModelValidationOptions();
            cancellationToken.ThrowIfCancellationRequested();

            if (!model.IsPackage)
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelPackageRequired",
                    "Replay requires a model package with manifest metadata."));
            }

            if (string.IsNullOrWhiteSpace(model.ModelPath) || !File.Exists(model.ModelPath))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelFileMissing",
                    $"Model file does not exist: {model.ModelPath}"));
            }

            if (string.IsNullOrWhiteSpace(model.ManifestPath) || !File.Exists(model.ManifestPath))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelManifestMissing",
                    $"Model manifest does not exist: {model.ManifestPath}"));
            }

            if (ModelPackagePathGuard.HasReparsePoint(new FileInfo(model.ManifestPath)))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelManifestReparsePoint",
                    "Model manifest file is a reparse point."));
            }

            if (ModelPackagePathGuard.HasReparsePoint(new FileInfo(model.ModelPath)))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelFileReparsePoint",
                    "Model file is a reparse point."));
            }

            if (!IsSafeManifestFileForRead(model.ManifestPath))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelManifestReparsePoint",
                    "Model manifest path contains a reparse point."));
            }

            if (!IsSafeReplayModelFileForRead(model.ModelPath))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelPathReparsePoint",
                    "Model file path contains a reparse point."));
            }

            ModelPackageManifest manifest;
            try
            {
                manifest = ReadReplayModelManifest(model.ManifestPath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelManifestParseFailed", ex.Message));
            }

            if (string.IsNullOrWhiteSpace(manifest.ModelId) ||
                !string.Equals(manifest.ModelId, model.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelIdMismatch", "ModelId is missing or does not match manifest."));
            }

            if (string.IsNullOrWhiteSpace(manifest.Version) ||
                !string.Equals(manifest.Version, model.Version, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelVersionMismatch", "Version is missing or does not match manifest."));
            }

            ReplayModelValidationResult? pathResult = ValidateManifestModelPath(model, manifest);
            if (pathResult != null)
            {
                return Task.FromResult(pathResult);
            }

            if (!IsSafeReplayModelFileForRead(model.ModelPath))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelPathReparsePoint",
                    "Model file path contains a reparse point."));
            }

            string actualHash = FileReplayDatasetStore.ComputeSha256(model.ModelPath);
            string expectedHash = manifest.EffectiveHash;
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(model.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelHashMismatch", "Model SHA-256 does not match manifest or identity."));
            }

            if (manifest.Labels == null || manifest.Labels.Count == 0 || manifest.Labels.All(string.IsNullOrWhiteSpace))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelLabelsMissing", "Model labels are missing."));
            }

            if (string.IsNullOrWhiteSpace(manifest.TaskType))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelTaskTypeMissing", "Model task type is missing."));
            }

            if (manifest.InputWidth <= 0 || manifest.InputHeight <= 0)
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelInputSizeMissing", "Model input width/height are missing."));
            }

            string approvalStatus = manifest.Approval?.Status ?? ModelApprovalStatuses.Pending;
            if (!options.AllowPendingApproval &&
                !string.Equals(approvalStatus, ModelApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail(
                    "ReplayModelNotApproved",
                    $"Model is not approved for production: {approvalStatus}."));
            }

            if (options.RequireWarmup && !_warmup(model.ModelPath, manifest))
            {
                return Task.FromResult(ReplayModelValidationResult.Fail("ReplayModelWarmupFailed", "Model warmup failed."));
            }

            return Task.FromResult(ReplayModelValidationResult.Ok());
        }

        private static bool DefaultWarmup(string modelPath, ModelPackageManifest manifest)
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

        private static ReplayModelValidationResult? ValidateManifestModelPath(
            ReplayModelIdentity model,
            ModelPackageManifest manifest)
        {
            string? packageDirectory = Path.GetDirectoryName(model.ManifestPath);
            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                return ReplayModelValidationResult.Fail(
                    "ReplayModelPackageDirectoryInvalid",
                    "Model manifest directory is invalid.");
            }

            string modelFileName = string.IsNullOrWhiteSpace(manifest.ModelFileName)
                ? "model.onnx"
                : manifest.ModelFileName.Trim();
            if (!ModelPackagePathGuard.TryResolveModelPath(
                    packageDirectory,
                    modelFileName,
                    out string declaredModelPath,
                    out string error,
                    "Manifest ModelFileName"))
            {
                return ReplayModelValidationResult.Fail(
                    "ReplayModelManifestPathInvalid",
                    error);
            }

            string actualModelPath = ModelPackagePathGuard.GetFullPathSafe(model.ModelPath);
            if (!string.Equals(declaredModelPath, actualModelPath, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayModelValidationResult.Fail(
                    "ReplayModelManifestPathMismatch",
                    "Model path does not match manifest ModelFileName.");
            }

            if (ModelPackagePathGuard.ModelPathHasReparsePoint(packageDirectory, declaredModelPath))
            {
                return ReplayModelValidationResult.Fail(
                    "ReplayModelPathReparsePoint",
                    "Model file path contains a reparse point.");
            }

            return null;
        }

        private static ModelPackageManifest ReadReplayModelManifest(string manifestPath)
        {
            string fullPath = Path.GetFullPath(manifestPath);
            if (!IsSafeManifestFileForRead(fullPath))
            {
                throw new IOException("Model manifest path contains a reparse point.");
            }

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (!IsSafeManifestFileForRead(fullPath))
            {
                throw new IOException("Model manifest path became unsafe before read.");
            }

            ModelPackageManifest manifest =
                JsonSerializer.Deserialize<ModelPackageManifest>(stream, ReplayJson.Options) ??
                new ModelPackageManifest();

            if (!IsSafeManifestFileForRead(fullPath))
            {
                throw new IOException("Model manifest path became unsafe after read.");
            }

            return manifest;
        }

        private static bool IsSafeManifestFileForRead(string manifestPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(manifestPath);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) ||
                    ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                return file.Exists && !ModelPackagePathGuard.HasReparsePoint(file);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeReplayModelFileForRead(string modelPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(modelPath);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) ||
                    ModelPackagePathGuard.DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                return file.Exists && !ModelPackagePathGuard.HasReparsePoint(file);
            }
            catch
            {
                return false;
            }
        }

    }

    public sealed class ProductionReplayInferenceRunner : IReplayInferenceRunner
    {
        private readonly Func<IDetectionService> _detectionServiceFactory;
        private readonly IInspectionDecisionEvaluator _decisionEvaluator;
        private readonly bool _useGpu;
        private readonly int _gpuIndex;

        public ProductionReplayInferenceRunner(
            Func<IDetectionService>? detectionServiceFactory = null,
            IInspectionDecisionEvaluator? decisionEvaluator = null,
            bool useGpu = false,
            int gpuIndex = 0)
        {
            _detectionServiceFactory = detectionServiceFactory ?? (() => new DetectionService(useGpu, gpuIndex));
            _decisionEvaluator = decisionEvaluator ?? new InspectionDecisionEvaluator();
            _useGpu = useGpu;
            _gpuIndex = Math.Max(0, gpuIndex);
        }

        public async Task<IReplayInferenceSession> CreateSessionAsync(
            ReplayModelIdentity model,
            ReplayRecipeSnapshot recipe,
            CancellationToken cancellationToken = default)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));

            IDetectionService detectionService = _detectionServiceFactory();
            try
            {
                detectionService.SetEnableFallback(false);
                detectionService.SetTaskMode((int)MapTaskType(model.TaskType));
                bool loaded = await detectionService.LoadModelAsync(model.ModelPath, _useGpu, _gpuIndex)
                    .ConfigureAwait(false);
                if (!loaded)
                {
                    throw new InvalidOperationException($"Replay model load failed: {model.ModelPath}");
                }

                return new ProductionReplayInferenceSession(detectionService, model, recipe, _decisionEvaluator);
            }
            catch
            {
                detectionService.Dispose();
                throw;
            }
        }

        private static YoloTaskType MapTaskType(string taskType)
        {
            return (taskType ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "classify" or "classification" => YoloTaskType.Classify,
                "segment" or "segmentation" => YoloTaskType.SegmentWithMask,
                "pose" => YoloTaskType.PoseWithKeypoints,
                "obb" => YoloTaskType.Obb,
                _ => YoloTaskType.Detect
            };
        }

        private sealed class ProductionReplayInferenceSession : IReplayInferenceSession
        {
            private readonly IDetectionService _detectionService;
            private readonly ReplayRecipeSnapshot _recipe;
            private readonly IInspectionDecisionEvaluator _decisionEvaluator;
            private bool _disposed;

            public ProductionReplayInferenceSession(
                IDetectionService detectionService,
                ReplayModelIdentity model,
                ReplayRecipeSnapshot recipe,
                IInspectionDecisionEvaluator decisionEvaluator)
            {
                _detectionService = detectionService;
                Model = model;
                _recipe = recipe;
                _decisionEvaluator = decisionEvaluator;
            }

            public ReplayModelIdentity Model { get; }

            public async Task<ReplayInferenceOutput> RunAsync(
                ReplayDatasetSample sample,
                CancellationToken cancellationToken = default)
            {
                if (sample == null) throw new ArgumentNullException(nameof(sample));
                cancellationToken.ThrowIfCancellationRequested();

                using Mat image = Cv2.ImRead(sample.ImagePath, ImreadModes.Color);
                if (image.Empty())
                {
                    throw new InvalidOperationException($"Replay image could not be read: {sample.ImagePath}");
                }

                DetectionResultData detection = await _detectionService.DetectAsync(
                    image,
                    _recipe.Confidence,
                    _recipe.IouThreshold,
                    fallbackGoal: null,
                    candidateEvaluator: null).ConfigureAwait(false);

                if (detection.HasError)
                {
                    throw new InvalidOperationException(detection.ErrorMessage);
                }

                string[] labels = detection.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                InspectionDecisionResult decision = _decisionEvaluator.Evaluate(new InspectionDecisionRequest
                {
                    RuleSet = _recipe.GetRuleSet(),
                    Detections = detection.Results ?? new System.Collections.Generic.List<YoloResult>(),
                    Labels = labels,
                    ImageWidth = image.Width,
                    ImageHeight = image.Height,
                    Roi = _recipe.Roi
                });
                if (!decision.Succeeded)
                {
                    throw new InvalidOperationException($"{decision.ErrorCode}: {decision.Message}");
                }

                return new ReplayInferenceOutput
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    Decision = decision.JudgeResult.IsQualified ? ReplayDecisions.OK : ReplayDecisions.NG,
                    Confidence = detection.Results?.Count > 0 ? detection.Results.Max(result => result.Confidence) : 0,
                    ElapsedMs = detection.ElapsedMs,
                    ModelId = Model.ModelId,
                    ModelVersion = Model.Version,
                    ModelHash = Model.Sha256,
                    RuleSummary = decision.JudgeResult.Summary
                };
            }

            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _detectionService.Dispose();
                    _disposed = true;
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
