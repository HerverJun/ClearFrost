using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Interfaces;
using ClearFrost.Yolo;

namespace ClearFrost.Services
{
    internal sealed class ProductionModelActivationResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public bool IsFaulted { get; init; }
        public IReadOnlyList<string> CompensationFailures { get; init; } = Array.Empty<string>();
        public ProductionModelReference Reference { get; init; } = ProductionModelReference.Empty();
        public string ModelPath { get; init; } = string.Empty;

        public static ProductionModelActivationResult Ok(string message, ProductionModelReference reference, string modelPath)
        {
            return new ProductionModelActivationResult
            {
                Succeeded = true,
                Message = message ?? string.Empty,
                Reference = reference.Clone(),
                ModelPath = NormalizePath(modelPath)
            };
        }

        public static ProductionModelActivationResult Fail(
            string errorCode,
            string message,
            ProductionModelReference? reference = null,
            bool isFaulted = false,
            IReadOnlyList<string>? compensationFailures = null)
        {
            return new ProductionModelActivationResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty,
                Reference = reference?.Clone() ?? ProductionModelReference.Empty(),
                IsFaulted = isFaulted,
                CompensationFailures = compensationFailures ?? Array.Empty<string>()
            };
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
            }
            catch
            {
                return path ?? string.Empty;
            }
        }
    }

    public sealed class ProductionModelReadinessResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;

        public static ProductionModelReadinessResult Ok()
        {
            return new ProductionModelReadinessResult { Succeeded = true };
        }

        public static ProductionModelReadinessResult Fail(string errorCode, string message)
        {
            return new ProductionModelReadinessResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty
            };
        }
    }

    internal sealed class ProductionModelActivationService
    {
        private readonly AppConfig _config;
        private readonly ModelRegistry _registry;
        private readonly RecipeManager _recipeManager;
        private readonly IDetectionService _detectionService;
        private readonly Func<IReadOnlyList<ModelRegistryEntry>> _refreshRegistry;
        private readonly Func<bool> _saveConfig;
        private readonly Func<float[]?> _roiSnapshotProvider;
        private readonly Func<string> _operatorIdProvider;
        private readonly Func<string> _operatorRoleProvider;
        private readonly Func<ModelRole, ModelRegistryEntry, ProductionModelReference, ProductionModelReadinessResult>? _approvalEvidenceValidator;
        private readonly object _faultLock = new object();
        private readonly SemaphoreSlim _activationGate = new SemaphoreSlim(1, 1);
        private bool _faulted;
        private string _faultMessage = string.Empty;

        public ProductionModelActivationService(
            AppConfig config,
            ModelRegistry registry,
            RecipeManager recipeManager,
            IDetectionService detectionService,
            Func<IReadOnlyList<ModelRegistryEntry>> refreshRegistry,
            Func<bool> saveConfig,
            Func<float[]?> roiSnapshotProvider,
            Func<string> operatorIdProvider,
            Func<string> operatorRoleProvider,
            Func<ModelRole, ModelRegistryEntry, ProductionModelReference, ProductionModelReadinessResult>? approvalEvidenceValidator = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _recipeManager = recipeManager ?? throw new ArgumentNullException(nameof(recipeManager));
            _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
            _refreshRegistry = refreshRegistry ?? throw new ArgumentNullException(nameof(refreshRegistry));
            _saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
            _roiSnapshotProvider = roiSnapshotProvider ?? throw new ArgumentNullException(nameof(roiSnapshotProvider));
            _operatorIdProvider = operatorIdProvider ?? throw new ArgumentNullException(nameof(operatorIdProvider));
            _operatorRoleProvider = operatorRoleProvider ?? throw new ArgumentNullException(nameof(operatorRoleProvider));
            _approvalEvidenceValidator = approvalEvidenceValidator;
        }

        public bool IsFaulted
        {
            get
            {
                lock (_faultLock)
                {
                    return _faulted;
                }
            }
        }

        public string FaultMessage
        {
            get
            {
                lock (_faultLock)
                {
                    return _faultMessage;
                }
            }
        }

        public IReadOnlyList<ProductionModelSelectionOption> GetSelectionOptions()
        {
            _refreshRegistry();
            return _registry.GetProductionSelectionOptions(_config.RequireApprovedModelsForProduction);
        }

        public async Task<ProductionModelActivationResult> ActivatePrimaryAsync(
            string selectionValue,
            string operation,
            bool useGpu,
            int gpuIndex,
            CancellationToken cancellationToken = default)
        {
            await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ActivateSlotAsync(
                    ModelRole.Primary,
                    selectionValue,
                    operation,
                    useGpu,
                    gpuIndex,
                    allowEmpty: false,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _activationGate.Release();
            }
        }

        public async Task<ProductionModelActivationResult> ActivateAuxiliaryAsync(
            int slot,
            string selectionValue,
            string operation,
            bool useGpu,
            int gpuIndex,
            CancellationToken cancellationToken = default)
        {
            if (slot != 1 && slot != 2)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ActivateSlotAsync(
                    slot == 1 ? ModelRole.Auxiliary1 : ModelRole.Auxiliary2,
                    selectionValue,
                    operation,
                    useGpu,
                    gpuIndex,
                    allowEmpty: true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _activationGate.Release();
            }
        }

        public async Task<ProductionModelActivationResult> LoadConfiguredModelsAsync(
            string operation,
            bool useGpu,
            int gpuIndex,
            CancellationToken cancellationToken = default)
        {
            await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _refreshRegistry();
                if (!TryResolveConfiguredCandidates(out IReadOnlyList<SlotCandidate> candidates, out ProductionModelActivationResult failure))
                {
                    return failure;
                }

                SlotCandidate primary = candidates.First(candidate => candidate.Role == ModelRole.Primary);
                return await ExecuteActivationTransactionAsync(
                    candidates,
                    primary,
                    operation,
                    useGpu,
                    gpuIndex,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _activationGate.Release();
            }
        }

        public ProductionModelReadinessResult EnsureReadyForProduction()
        {
            if (IsFaulted)
            {
                return ProductionModelReadinessResult.Fail("ModelActivationFaulted", FaultMessage);
            }

            return EnsureActualConsistency();
        }

        private async Task<ProductionModelActivationResult> ActivateSlotAsync(
            ModelRole role,
            string selectionValue,
            string operation,
            bool useGpu,
            int gpuIndex,
            bool allowEmpty,
            CancellationToken cancellationToken)
        {
            _refreshRegistry();

            ProductionModelResolutionResult selected = ResolveSelectionValue(selectionValue, allowEmpty);
            if (!selected.Succeeded)
            {
                return FailPreservingFault(selected.ErrorCode, selected.Message, selected.Reference);
            }

            if (!TryBuildSlotActivationCandidates(role, selected, out IReadOnlyList<SlotCandidate> candidates, out ProductionModelActivationResult failure))
            {
                return failure;
            }

            SlotCandidate target = candidates.First(candidate => candidate.Role == role);
            return await ExecuteActivationTransactionAsync(
                candidates,
                target,
                operation,
                useGpu,
                gpuIndex,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<ProductionModelActivationResult> ExecuteActivationTransactionAsync(
            IReadOnlyList<SlotCandidate> candidates,
            SlotCandidate resultSlot,
            string operation,
            bool useGpu,
            int gpuIndex,
            CancellationToken cancellationToken)
        {
            bool wasFaulted = IsFaulted;
            AppConfig previousConfig = CaptureConfig();
            RecipeTransactionSnapshot previousRecipe = _recipeManager.CaptureTransactionSnapshot();
            DetectionRuntimeModelSnapshot previousRuntime = _detectionService.RuntimeModelSnapshot;
            var transitionState = new RuntimeTransitionState();
            var compensationFailures = new List<string>();
            bool configTouched = false;

            try
            {
                await TransitionRuntimeAsync(candidates, useGpu, gpuIndex, transitionState, cancellationToken)
                    .ConfigureAwait(false);

                ProductionModelReadinessResult preCommit = ValidateCandidatesBeforeCommit(candidates);
                if (!preCommit.Succeeded)
                {
                    throw ActivationTransactionException.FromReadiness(preCommit, resultSlot.Reference);
                }

                AppConfig candidateConfig = CaptureConfig();
                foreach (SlotCandidate candidate in candidates)
                {
                    ApplyReferenceToConfig(candidateConfig, candidate);
                }

                _config.CopyFrom(candidateConfig);
                configTouched = true;
                if (!_saveConfig())
                {
                    throw new ActivationTransactionException(
                        "AppConfigCommitFailed",
                        _config.LastError ?? "AppConfig save failed.",
                        resultSlot.Reference);
                }

                _recipeManager.SaveNewVersionForActivationTransaction(
                    _config,
                    _roiSnapshotProvider(),
                    _operatorIdProvider(),
                    _operatorRoleProvider(),
                    operation,
                    previousRecipe);

                ProductionModelReadinessResult consistency = EnsureActualConsistency();
                if (!consistency.Succeeded)
                {
                    throw ActivationTransactionException.FromReadiness(consistency, resultSlot.Reference);
                }

                ClearFault();
                return ProductionModelActivationResult.Ok(
                    $"{operation} completed.",
                    resultSlot.Reference,
                    resultSlot.ModelPath);
            }
            catch (Exception ex)
            {
                ActivationTransactionException failure = ActivationTransactionException.Wrap(ex, resultSlot.Reference);
                if (transitionState.RuntimeChanged || configTouched)
                {
                    await CompensateAsync(
                        previousConfig,
                        previousRecipe,
                        previousRuntime,
                        configTouched,
                        useGpu,
                        gpuIndex,
                        compensationFailures).ConfigureAwait(false);
                }

                if (compensationFailures.Count > 0)
                {
                    MarkFaulted(failure.Message, compensationFailures);
                    return ProductionModelActivationResult.Fail(
                        "ModelActivationFaulted",
                        $"Model activation failed and compensation failed: {failure.Message}",
                        failure.Reference,
                        isFaulted: true,
                        compensationFailures);
                }

                if (wasFaulted)
                {
                    AppendFaultedFailure(failure.Message);
                    return ProductionModelActivationResult.Fail(
                        failure.ErrorCode,
                        $"Model recovery attempt failed; existing fault remains latched: {failure.Message}",
                        failure.Reference,
                        isFaulted: true);
                }

                return ProductionModelActivationResult.Fail(
                    failure.ErrorCode,
                    transitionState.RuntimeChanged || configTouched
                        ? $"Model activation failed; previous state restored: {failure.Message}"
                        : failure.Message,
                    failure.Reference);
            }
        }

        private bool TryResolveConfiguredCandidates(
            out IReadOnlyList<SlotCandidate> candidates,
            out ProductionModelActivationResult failure)
        {
            var resolved = new List<SlotCandidate>();

            if (!TryResolveConfiguredSlot(
                    ModelRole.Primary,
                    _config.CurrentModelReference,
                    _config.CurrentModelFileName,
                    allowEmpty: false,
                    resolved,
                    out failure))
            {
                candidates = Array.Empty<SlotCandidate>();
                return false;
            }

            if (!TryResolveConfiguredSlot(
                    ModelRole.Auxiliary1,
                    _config.Auxiliary1ModelReference,
                    _config.Auxiliary1ModelPath,
                    allowEmpty: true,
                    resolved,
                    out failure))
            {
                candidates = Array.Empty<SlotCandidate>();
                return false;
            }

            if (!TryResolveConfiguredSlot(
                    ModelRole.Auxiliary2,
                    _config.Auxiliary2ModelReference,
                    _config.Auxiliary2ModelPath,
                    allowEmpty: true,
                    resolved,
                    out failure))
            {
                candidates = Array.Empty<SlotCandidate>();
                return false;
            }

            candidates = resolved;
            failure = ProductionModelActivationResult.Ok(string.Empty, ProductionModelReference.Empty(), string.Empty);
            return true;
        }

        private bool TryBuildSlotActivationCandidates(
            ModelRole targetRole,
            ProductionModelResolutionResult selected,
            out IReadOnlyList<SlotCandidate> candidates,
            out ProductionModelActivationResult failure)
        {
            var resolved = new List<SlotCandidate>();
            foreach (ModelRole role in new[] { ModelRole.Primary, ModelRole.Auxiliary1, ModelRole.Auxiliary2 })
            {
                if (role == targetRole)
                {
                    resolved.Add(new SlotCandidate(role, selected));
                    continue;
                }

                ProductionModelReference? reference = role switch
                {
                    ModelRole.Primary => _config.CurrentModelReference,
                    ModelRole.Auxiliary1 => _config.Auxiliary1ModelReference,
                    ModelRole.Auxiliary2 => _config.Auxiliary2ModelReference,
                    _ => ProductionModelReference.Empty()
                };
                string legacyValue = role switch
                {
                    ModelRole.Primary => _config.CurrentModelFileName,
                    ModelRole.Auxiliary1 => _config.Auxiliary1ModelPath,
                    ModelRole.Auxiliary2 => _config.Auxiliary2ModelPath,
                    _ => string.Empty
                };

                if (!TryResolveConfiguredSlot(
                        role,
                        reference,
                        legacyValue,
                        allowEmpty: role != ModelRole.Primary,
                        resolved,
                        out failure))
                {
                    candidates = Array.Empty<SlotCandidate>();
                    return false;
                }
            }

            candidates = resolved;
            failure = ProductionModelActivationResult.Ok(string.Empty, ProductionModelReference.Empty(), string.Empty);
            return true;
        }

        private bool TryResolveConfiguredSlot(
            ModelRole role,
            ProductionModelReference? reference,
            string legacyValue,
            bool allowEmpty,
            List<SlotCandidate> candidates,
            out ProductionModelActivationResult failure)
        {
            ProductionModelResolutionResult resolved = ResolveConfiguredOrLegacy(reference, legacyValue, allowEmpty);
            if (!resolved.Succeeded)
            {
                failure = FailPreservingFault(resolved.ErrorCode, resolved.Message, resolved.Reference);
                return false;
            }

            candidates.Add(new SlotCandidate(role, resolved));
            failure = ProductionModelActivationResult.Ok(string.Empty, ProductionModelReference.Empty(), string.Empty);
            return true;
        }

        private ProductionModelResolutionResult ResolveSelectionValue(string selectionValue, bool allowEmpty)
        {
            if (!ProductionModelReference.TryParseSelectionValue(selectionValue, out ProductionModelReference reference))
            {
                ProductionModelResolutionResult migrated = _registry.MigrateLegacyReference(
                    selectionValue,
                    _config.RequireApprovedModelsForProduction);
                if (!migrated.Succeeded)
                {
                    return migrated;
                }

                reference = migrated.Reference;
            }

            if (reference.IsEmpty)
            {
                if (!allowEmpty)
                {
                    return ProductionModelResolutionResult.Fail(
                        reference,
                        "PrimaryModelReferenceEmpty",
                        "Primary model reference cannot be empty.");
                }

                return ProductionModelResolutionResult.Ok(reference, new ModelRegistryEntry(), string.Empty);
            }

            return _registry.ResolveReference(reference, _config.RequireApprovedModelsForProduction);
        }

        private ProductionModelResolutionResult ResolveConfiguredOrLegacy(
            ProductionModelReference? reference,
            string legacyValue,
            bool allowEmpty)
        {
            ProductionModelReference current = reference?.Clone() ?? ProductionModelReference.Empty();
            if (!current.IsEmpty)
            {
                return _registry.ResolveReference(current, _config.RequireApprovedModelsForProduction);
            }

            if (string.IsNullOrWhiteSpace(legacyValue))
            {
                if (allowEmpty)
                {
                    return ProductionModelResolutionResult.Ok(current, new ModelRegistryEntry(), string.Empty);
                }

                return ProductionModelResolutionResult.Fail(
                    current,
                    "PrimaryModelReferenceEmpty",
                    "Primary model reference cannot be empty.");
            }

            return _registry.MigrateLegacyReference(legacyValue, _config.RequireApprovedModelsForProduction);
        }

        private async Task TransitionRuntimeAsync(
            IReadOnlyList<SlotCandidate> candidates,
            bool useGpu,
            int gpuIndex,
            RuntimeTransitionState transitionState,
            CancellationToken cancellationToken)
        {
            foreach (SlotCandidate candidate in candidates.OrderBy(candidate => candidate.Role == ModelRole.Primary ? 0 : candidate.Role == ModelRole.Auxiliary1 ? 1 : 2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DetectionModelSlotSnapshot current = GetRuntimeSlot(candidate.Role);
                if (candidate.Reference.IsEmpty)
                {
                    if (current.IsLoaded)
                    {
                        UnloadSlot(candidate.Role);
                        transitionState.RuntimeChanged = true;
                    }

                    ProductionModelReadinessResult emptyCheck = CheckRuntimeSlotEmpty(candidate.Role);
                    if (!emptyCheck.Succeeded)
                    {
                        throw ActivationTransactionException.FromReadiness(emptyCheck, candidate.Reference);
                    }

                    continue;
                }

                if (current.IsLoaded &&
                    string.Equals(NormalizePath(current.ModelPath), NormalizePath(candidate.ModelPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool loaded = await LoadResolvedSlotAsync(candidate.Role, candidate.ModelPath, useGpu, gpuIndex, cancellationToken)
                    .ConfigureAwait(false);
                if (!loaded)
                {
                    throw new ActivationTransactionException(
                        $"Runtime{candidate.Role}LoadFailed",
                        $"{candidate.Role} runtime model load failed: {candidate.ModelPath}",
                        candidate.Reference);
                }

                transitionState.RuntimeChanged = true;
            }
        }

        private ProductionModelReadinessResult ValidateCandidatesBeforeCommit(IReadOnlyList<SlotCandidate> candidates)
        {
            _refreshRegistry();
            foreach (SlotCandidate candidate in candidates)
            {
                if (candidate.Role == ModelRole.Primary && candidate.Reference.IsEmpty)
                {
                    return ProductionModelReadinessResult.Fail(
                        "PrimaryModelReferenceEmpty",
                        "Primary model reference cannot be empty.");
                }

                if (candidate.Reference.IsEmpty)
                {
                    ProductionModelReadinessResult emptyCheck = CheckRuntimeSlotEmpty(candidate.Role);
                    if (!emptyCheck.Succeeded)
                    {
                        return emptyCheck;
                    }

                    continue;
                }

                ProductionModelResolutionResult resolved = _registry.ResolveReference(
                    candidate.Reference,
                    _config.RequireApprovedModelsForProduction);
                if (!resolved.Succeeded)
                {
                    return ProductionModelReadinessResult.Fail(resolved.ErrorCode, resolved.Message);
                }

                if (!ReferenceEqualsByIdentity(resolved.Reference, candidate.Reference) ||
                    !string.Equals(NormalizePath(resolved.ModelPath), NormalizePath(candidate.ModelPath), StringComparison.OrdinalIgnoreCase))
                {
                    return ProductionModelReadinessResult.Fail(
                        "CandidateModelReferenceChanged",
                        $"{candidate.Role} candidate no longer resolves to the same registry identity/path.");
                }

                ProductionModelReadinessResult runtimeCheck = CheckRuntimeSlot(candidate.Role, resolved);
                if (!runtimeCheck.Succeeded)
                {
                    return runtimeCheck;
                }

                ProductionModelReadinessResult evidenceCheck = ValidateApprovalEvidence(
                    candidate.Role,
                    resolved.Entry,
                    candidate.Reference);
                if (!evidenceCheck.Succeeded)
                {
                    return evidenceCheck;
                }
            }

            return ProductionModelReadinessResult.Ok();
        }

        private ProductionModelReadinessResult EnsureActualConsistency()
        {
            _refreshRegistry();

            ProductionModelReadinessResult configRuntime = ValidateConfigAndRuntime();
            if (!configRuntime.Succeeded)
            {
                return configRuntime;
            }

            Recipe recipe = _recipeManager.CurrentRecipe ?? new Recipe();
            if (!RecipeReferencesMatch(recipe, _config))
            {
                return ProductionModelReadinessResult.Fail(
                    "RecipeModelReferenceMismatch",
                    "CurrentRecipe model references do not match AppConfig.");
            }

            Recipe? persistedRecipe = TryReadPersistedRecipe(out string persistedError);
            if (persistedRecipe == null)
            {
                return ProductionModelReadinessResult.Fail(
                    "RecipePersistenceUnavailable",
                    persistedError);
            }

            if (!RecipeReferencesMatch(persistedRecipe, _config))
            {
                return ProductionModelReadinessResult.Fail(
                    "PersistedRecipeModelReferenceMismatch",
                    "Persisted recipe model references do not match AppConfig.");
            }

            return ProductionModelReadinessResult.Ok();
        }

        private ProductionModelReadinessResult ValidateConfigAndRuntime()
        {
            ProductionModelResolutionResult primary = _registry.ResolveReference(
                _config.CurrentModelReference,
                _config.RequireApprovedModelsForProduction);
            if (!primary.Succeeded)
            {
                return ProductionModelReadinessResult.Fail(primary.ErrorCode, primary.Message);
            }

            if (primary.Reference.IsEmpty)
            {
                return ProductionModelReadinessResult.Fail("PrimaryModelReferenceEmpty", "Primary model reference cannot be empty.");
            }

            ProductionModelReadinessResult runtimeCheck = CheckRuntimeSlot(ModelRole.Primary, primary);
            if (!runtimeCheck.Succeeded)
            {
                return runtimeCheck;
            }

            ProductionModelReadinessResult evidenceCheck = ValidateApprovalEvidence(
                ModelRole.Primary,
                primary.Entry,
                _config.CurrentModelReference);
            if (!evidenceCheck.Succeeded)
            {
                return evidenceCheck;
            }

            foreach ((ModelRole Role, ProductionModelReference? Reference) slot in new[]
            {
                (ModelRole.Auxiliary1, _config.Auxiliary1ModelReference),
                (ModelRole.Auxiliary2, _config.Auxiliary2ModelReference)
            })
            {
                if (slot.Reference == null || slot.Reference.IsEmpty)
                {
                    runtimeCheck = CheckRuntimeSlotEmpty(slot.Role);
                    if (!runtimeCheck.Succeeded)
                    {
                        return runtimeCheck;
                    }

                    continue;
                }

                ProductionModelResolutionResult resolved = _registry.ResolveReference(
                    slot.Reference,
                    _config.RequireApprovedModelsForProduction);
                if (!resolved.Succeeded)
                {
                    return ProductionModelReadinessResult.Fail(resolved.ErrorCode, resolved.Message);
                }

                runtimeCheck = CheckRuntimeSlot(slot.Role, resolved);
                if (!runtimeCheck.Succeeded)
                {
                    return runtimeCheck;
                }

                evidenceCheck = ValidateApprovalEvidence(
                    slot.Role,
                    resolved.Entry,
                    slot.Reference);
                if (!evidenceCheck.Succeeded)
                {
                    return evidenceCheck;
                }
            }

            return ProductionModelReadinessResult.Ok();
        }

        private ProductionModelReadinessResult ValidateApprovalEvidence(
            ModelRole role,
            ModelRegistryEntry? entry,
            ProductionModelReference? reference)
        {
            if (!_config.RequireApprovedModelsForProduction)
            {
                return ProductionModelReadinessResult.Ok();
            }

            if (entry == null)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceEntryMissing",
                    "Production approval is enabled but the registry entry is missing.");
            }

            if (!entry.IsPackage)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidencePackageRequired",
                    "Production approval is enabled and requires a manifest-backed package.");
            }

            if (_approvalEvidenceValidator == null)
            {
                return ProductionModelReadinessResult.Fail(
                    "ReplayEvidenceGateMissing",
                    "Production approval is enabled but Replay evidence gate is not configured.");
            }

            return _approvalEvidenceValidator(role, entry, reference?.Clone() ?? ProductionModelReference.Empty());
        }

        private async Task<bool> LoadResolvedSlotAsync(
            ModelRole role,
            string modelPath,
            bool useGpu,
            int gpuIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return role switch
            {
                ModelRole.Primary => await _detectionService.LoadModelAsync(modelPath, useGpu, gpuIndex).ConfigureAwait(false),
                ModelRole.Auxiliary1 => await _detectionService.LoadAuxiliary1ModelAsync(modelPath).ConfigureAwait(false),
                ModelRole.Auxiliary2 => await _detectionService.LoadAuxiliary2ModelAsync(modelPath).ConfigureAwait(false),
                _ => false
            };
        }

        private void UnloadSlot(ModelRole role)
        {
            switch (role)
            {
                case ModelRole.Primary:
                    _detectionService.UnloadPrimaryModel();
                    break;
                case ModelRole.Auxiliary1:
                    _detectionService.UnloadAuxiliary1Model();
                    break;
                case ModelRole.Auxiliary2:
                    _detectionService.UnloadAuxiliary2Model();
                    break;
            }
        }

        private void ApplyReferenceToConfig(AppConfig config, SlotCandidate candidate)
        {
            ProductionModelReference reference = candidate.Reference.Clone();
            string fileName = candidate.DisplayFileName;

            switch (candidate.Role)
            {
                case ModelRole.Primary:
                    config.CurrentModelReference = reference;
                    config.CurrentModelFileName = reference.IsEmpty ? string.Empty : fileName;
                    break;
                case ModelRole.Auxiliary1:
                    config.Auxiliary1ModelReference = reference;
                    config.Auxiliary1ModelPath = reference.IsEmpty ? string.Empty : fileName;
                    break;
                case ModelRole.Auxiliary2:
                    config.Auxiliary2ModelReference = reference;
                    config.Auxiliary2ModelPath = reference.IsEmpty ? string.Empty : fileName;
                    break;
            }
        }

        private async Task CompensateAsync(
            AppConfig previousConfig,
            RecipeTransactionSnapshot previousRecipe,
            DetectionRuntimeModelSnapshot previousRuntime,
            bool configTouched,
            bool useGpu,
            int gpuIndex,
            List<string> failures)
        {
            await RestoreRuntimeSlotAsync(previousRuntime.Primary, useGpu, gpuIndex, failures).ConfigureAwait(false);
            await RestoreRuntimeSlotAsync(previousRuntime.Auxiliary1, useGpu, gpuIndex, failures).ConfigureAwait(false);
            await RestoreRuntimeSlotAsync(previousRuntime.Auxiliary2, useGpu, gpuIndex, failures).ConfigureAwait(false);

            try
            {
                _config.CopyFrom(previousConfig);
                if (configTouched && !_saveConfig())
                {
                    failures.Add($"Config restore save failed: {_config.LastError ?? "unknown error"}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"Config restore failed: {ex.Message}");
            }

            IReadOnlyList<string> recipeRestoreFailures = _recipeManager.RestoreTransactionSnapshot(previousRecipe);
            failures.AddRange(recipeRestoreFailures.Select(failure => $"Recipe restore failed: {failure}"));

            VerifyCompensation(previousConfig, previousRecipe, previousRuntime, failures);
        }

        private async Task RestoreRuntimeSlotAsync(
            DetectionModelSlotSnapshot slot,
            bool useGpu,
            int gpuIndex,
            List<string> failures)
        {
            try
            {
                if (!slot.IsLoaded || string.IsNullOrWhiteSpace(slot.ModelPath))
                {
                    UnloadSlot(slot.Role);
                    ProductionModelReadinessResult emptyCheck = CheckRuntimeSlotEmpty(slot.Role);
                    if (!emptyCheck.Succeeded)
                    {
                        failures.Add($"{slot.Role} runtime restore-to-empty failed: {emptyCheck.Message}");
                    }

                    return;
                }

                bool ok = await LoadResolvedSlotAsync(slot.Role, slot.ModelPath, useGpu, gpuIndex, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    failures.Add($"{slot.Role} runtime restore load failed: {slot.ModelPath}");
                    return;
                }

                DetectionModelSlotSnapshot restored = GetRuntimeSlot(slot.Role);
                if (!restored.IsLoaded ||
                    !string.Equals(NormalizePath(restored.ModelPath), NormalizePath(slot.ModelPath), StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{slot.Role} runtime restore path mismatch. Expected={NormalizePath(slot.ModelPath)}; Actual={NormalizePath(restored.ModelPath)}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{slot.Role} runtime restore exception: {ex.Message}");
            }
        }

        private void VerifyCompensation(
            AppConfig previousConfig,
            RecipeTransactionSnapshot previousRecipe,
            DetectionRuntimeModelSnapshot previousRuntime,
            List<string> failures)
        {
            if (!ReferenceEqualsByIdentity(_config.CurrentModelReference, previousConfig.CurrentModelReference) ||
                !ReferenceEqualsByIdentity(_config.Auxiliary1ModelReference, previousConfig.Auxiliary1ModelReference) ||
                !ReferenceEqualsByIdentity(_config.Auxiliary2ModelReference, previousConfig.Auxiliary2ModelReference))
            {
                failures.Add("Config model references do not match activation snapshot after compensation.");
            }

            VerifyRuntimeSnapshot(previousRuntime, failures);
            VerifyRecipeSnapshot(previousRecipe, failures);
        }

        private void VerifyRuntimeSnapshot(DetectionRuntimeModelSnapshot expected, List<string> failures)
        {
            foreach (DetectionModelSlotSnapshot slot in new[] { expected.Primary, expected.Auxiliary1, expected.Auxiliary2 })
            {
                DetectionModelSlotSnapshot actual = GetRuntimeSlot(slot.Role);
                if (slot.IsLoaded != actual.IsLoaded ||
                    !string.Equals(NormalizePath(slot.ModelPath), NormalizePath(actual.ModelPath), StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{slot.Role} runtime snapshot mismatch after compensation. ExpectedLoaded={slot.IsLoaded}, ExpectedPath={NormalizePath(slot.ModelPath)}; ActualLoaded={actual.IsLoaded}, ActualPath={NormalizePath(actual.ModelPath)}");
                }
            }
        }

        private void VerifyRecipeSnapshot(RecipeTransactionSnapshot expected, List<string> failures)
        {
            IReadOnlyList<string> recipeVerificationFailures = _recipeManager.VerifyTransactionSnapshot(expected);
            failures.AddRange(recipeVerificationFailures.Select(failure => $"Recipe compensation verification failed: {failure}"));
        }

        private ProductionModelReadinessResult CheckRuntimeSlot(
            ModelRole role,
            ProductionModelResolutionResult resolved)
        {
            DetectionModelSlotSnapshot slot = GetRuntimeSlot(role);
            if (!slot.IsLoaded)
            {
                return ProductionModelReadinessResult.Fail(
                    "RuntimeModelNotLoaded",
                    $"{role} runtime model is not loaded.");
            }

            string expectedPath = NormalizePath(resolved.ModelPath);
            string actualPath = NormalizePath(slot.ModelPath);
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelReadinessResult.Fail(
                    "RuntimeModelPathMismatch",
                    $"{role} runtime path does not match resolved AppConfig reference. Expected={expectedPath}; Actual={actualPath}");
            }

            return ProductionModelReadinessResult.Ok();
        }

        private ProductionModelReadinessResult CheckRuntimeSlotEmpty(ModelRole role)
        {
            DetectionModelSlotSnapshot slot = GetRuntimeSlot(role);
            if (slot.IsLoaded || !string.IsNullOrWhiteSpace(slot.ModelPath))
            {
                return ProductionModelReadinessResult.Fail(
                    "RuntimeModelUnexpectedlyLoaded",
                    $"{role} runtime slot must be empty. Actual={NormalizePath(slot.ModelPath)}");
            }

            return ProductionModelReadinessResult.Ok();
        }

        private DetectionModelSlotSnapshot GetRuntimeSlot(ModelRole role)
        {
            DetectionRuntimeModelSnapshot snapshot = _detectionService.RuntimeModelSnapshot;
            return role switch
            {
                ModelRole.Primary => snapshot.Primary,
                ModelRole.Auxiliary1 => snapshot.Auxiliary1,
                ModelRole.Auxiliary2 => snapshot.Auxiliary2,
                _ => new DetectionModelSlotSnapshot()
            };
        }

        private ProductionModelActivationResult FailPreservingFault(
            string errorCode,
            string message,
            ProductionModelReference? reference)
        {
            bool faulted = IsFaulted;
            return ProductionModelActivationResult.Fail(
                errorCode,
                faulted ? $"{message}; existing fault remains latched: {FaultMessage}" : message,
                reference,
                isFaulted: faulted);
        }

        private Recipe? TryReadPersistedRecipe(out string error)
        {
            error = string.Empty;
            try
            {
                if (!File.Exists(_recipeManager.RecipePath))
                {
                    error = $"Recipe file not found: {_recipeManager.RecipePath}";
                    return null;
                }

                string json = File.ReadAllText(_recipeManager.RecipePath);
                return JsonSerializer.Deserialize<Recipe>(json);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private AppConfig CaptureConfig()
        {
            return AppConfig.FromJson(_config.ToPortableJson());
        }

        private void ClearFault()
        {
            lock (_faultLock)
            {
                _faulted = false;
                _faultMessage = string.Empty;
            }
        }

        private void MarkFaulted(string originalFailure, IReadOnlyList<string> compensationFailures)
        {
            lock (_faultLock)
            {
                string message = $"Original failure: {originalFailure}; compensation failures: {string.Join("; ", compensationFailures)}";
                _faultMessage = _faulted && !string.IsNullOrWhiteSpace(_faultMessage)
                    ? $"{_faultMessage}; {message}"
                    : message;
                _faulted = true;
            }
        }

        private void AppendFaultedFailure(string failure)
        {
            lock (_faultLock)
            {
                if (!_faulted)
                {
                    return;
                }

                _faultMessage = string.IsNullOrWhiteSpace(_faultMessage)
                    ? $"Recovery attempt failed: {failure}"
                    : $"{_faultMessage}; recovery attempt failed: {failure}";
            }
        }

        private static bool RecipeReferencesMatch(Recipe recipe, AppConfig config)
        {
            return ReferenceEqualsByIdentity(recipe.CurrentModelReference, config.CurrentModelReference) &&
                   ReferenceEqualsByIdentity(recipe.Auxiliary1ModelReference, config.Auxiliary1ModelReference) &&
                   ReferenceEqualsByIdentity(recipe.Auxiliary2ModelReference, config.Auxiliary2ModelReference);
        }

        private static bool RecipeReferencesMatch(Recipe left, Recipe right)
        {
            return ReferenceEqualsByIdentity(left.CurrentModelReference, right.CurrentModelReference) &&
                   ReferenceEqualsByIdentity(left.Auxiliary1ModelReference, right.Auxiliary1ModelReference) &&
                   ReferenceEqualsByIdentity(left.Auxiliary2ModelReference, right.Auxiliary2ModelReference);
        }

        private static bool ReferenceEqualsByIdentity(
            ProductionModelReference? left,
            ProductionModelReference? right)
        {
            return (left ?? ProductionModelReference.Empty()).IdentityEquals(right ?? ProductionModelReference.Empty());
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private sealed class SlotCandidate
        {
            public SlotCandidate(ModelRole role, ProductionModelResolutionResult resolution)
            {
                Role = role;
                Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
            }

            public ModelRole Role { get; }
            public ProductionModelResolutionResult Resolution { get; }
            public ProductionModelReference Reference => Resolution.Reference;
            public string ModelPath => Resolution.ModelPath ?? string.Empty;
            public string DisplayFileName
            {
                get
                {
                    if (Reference.IsEmpty)
                    {
                        return string.Empty;
                    }

                    return string.IsNullOrWhiteSpace(Resolution.Entry?.UsedModelName)
                        ? Path.GetFileName(ModelPath)
                        : Resolution.Entry!.UsedModelName;
                }
            }
        }

        private sealed class RuntimeTransitionState
        {
            public bool RuntimeChanged { get; set; }
        }

        private sealed class ActivationTransactionException : Exception
        {
            public ActivationTransactionException(
                string errorCode,
                string message,
                ProductionModelReference? reference)
                : base(message)
            {
                ErrorCode = errorCode ?? string.Empty;
                Reference = reference?.Clone() ?? ProductionModelReference.Empty();
            }

            public string ErrorCode { get; }
            public ProductionModelReference Reference { get; }

            public static ActivationTransactionException FromReadiness(
                ProductionModelReadinessResult result,
                ProductionModelReference? reference)
            {
                return new ActivationTransactionException(
                    result.ErrorCode,
                    result.Message,
                    reference);
            }

            public static ActivationTransactionException Wrap(Exception exception, ProductionModelReference? reference)
            {
                return exception as ActivationTransactionException ??
                    new ActivationTransactionException(
                        "ModelActivationFailed",
                        exception.Message,
                        reference);
            }
        }
    }
}
