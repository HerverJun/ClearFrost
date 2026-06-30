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

        public ReplayApprovalEvidenceProductionGate(
            IModelApprovalEvidenceStore evidenceStore,
            IReplayDatasetStore datasetStore)
        {
            _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
        }

        public ProductionModelReadinessResult Validate(ModelRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Manifest?.Approval == null)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceMissing",
                    "Model manifest approval metadata is missing.");
            }

            ModelApprovalEvidenceValidationResult result = _evidenceStore.ValidateEvidence(
                ReplayModelIdentity.FromRegistryEntry(entry),
                entry.Manifest.Approval.ReplayEvidenceId,
                entry.Manifest.Approval.ReplayEvidenceHash,
                _datasetStore);

            return result.Succeeded
                ? ProductionModelReadinessResult.Ok()
                : ProductionModelReadinessResult.Fail(result.ErrorCode, result.Message);
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

            ModelPackageManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                    File.ReadAllText(model.ManifestPath),
                    ReplayJson.Options) ?? new ModelPackageManifest();
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
    }

    public sealed class ProductionReplayInferenceRunner : IReplayInferenceRunner
    {
        private readonly Func<IDetectionService> _detectionServiceFactory;
        private readonly bool _useGpu;
        private readonly int _gpuIndex;

        public ProductionReplayInferenceRunner(
            Func<IDetectionService>? detectionServiceFactory = null,
            bool useGpu = false,
            int gpuIndex = 0)
        {
            _detectionServiceFactory = detectionServiceFactory ?? (() => new DetectionService(useGpu, gpuIndex));
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

                return new ProductionReplayInferenceSession(detectionService, model, recipe);
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
            private bool _disposed;

            public ProductionReplayInferenceSession(
                IDetectionService detectionService,
                ReplayModelIdentity model,
                ReplayRecipeSnapshot recipe)
            {
                _detectionService = detectionService;
                Model = model;
                _recipe = recipe;
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
                InspectionJudgeResult judge = InspectionRuleEngine.Evaluate(
                    _recipe.GetRuleSet(),
                    detection.Results ?? new System.Collections.Generic.List<YoloResult>(),
                    labels);

                return new ReplayInferenceOutput
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    Decision = judge.IsQualified ? ReplayDecisions.OK : ReplayDecisions.NG,
                    Confidence = detection.Results?.Count > 0 ? detection.Results.Max(result => result.Confidence) : 0,
                    ElapsedMs = detection.ElapsedMs,
                    ModelId = Model.ModelId,
                    ModelVersion = Model.Version,
                    ModelHash = Model.Sha256,
                    RuleSummary = judge.Summary
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
