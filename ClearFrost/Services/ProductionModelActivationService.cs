using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    internal sealed class ProductionModelReadinessResult
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
        private readonly object _faultLock = new object();
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
            Func<string> operatorRoleProvider)
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
            return await ActivateSlotAsync(
                ModelRole.Primary,
                selectionValue,
                operation,
                useGpu,
                gpuIndex,
                allowEmpty: false,
                cancellationToken).ConfigureAwait(false);
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

            return await ActivateSlotAsync(
                slot == 1 ? ModelRole.Auxiliary1 : ModelRole.Auxiliary2,
                selectionValue,
                operation,
                useGpu,
                gpuIndex,
                allowEmpty: true,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<ProductionModelActivationResult> LoadConfiguredModelsAsync(
            string operation,
            bool useGpu,
            int gpuIndex,
            CancellationToken cancellationToken = default)
        {
            ClearFault();
            _refreshRegistry();
            AppConfig previousConfig = CaptureConfig();
            DetectionRuntimeModelSnapshot previousRuntime = _detectionService.RuntimeModelSnapshot;

            List<string> compensationFailures = new List<string>();
            bool changedConfig = false;

            ProductionModelResolutionResult primary = ResolveConfiguredOrLegacy(
                _config.CurrentModelReference,
                _config.CurrentModelFileName,
                allowEmpty: false,
                out bool primaryMigrated);
            if (!primary.Succeeded)
            {
                return ProductionModelActivationResult.Fail(primary.ErrorCode, primary.Message, primary.Reference);
            }

            changedConfig |= primaryMigrated || !ReferenceEqualsByIdentity(_config.CurrentModelReference, primary.Reference);
            ApplyReferenceToConfig(ModelRole.Primary, primary);

            ProductionModelResolutionResult aux1 = ResolveConfiguredOrLegacy(
                _config.Auxiliary1ModelReference,
                _config.Auxiliary1ModelPath,
                allowEmpty: true,
                out bool aux1Migrated);
            if (!aux1.Succeeded)
            {
                return ProductionModelActivationResult.Fail(aux1.ErrorCode, aux1.Message, aux1.Reference);
            }

            changedConfig |= aux1Migrated || !ReferenceEqualsByIdentity(_config.Auxiliary1ModelReference, aux1.Reference);
            ApplyReferenceToConfig(ModelRole.Auxiliary1, aux1);

            ProductionModelResolutionResult aux2 = ResolveConfiguredOrLegacy(
                _config.Auxiliary2ModelReference,
                _config.Auxiliary2ModelPath,
                allowEmpty: true,
                out bool aux2Migrated);
            if (!aux2.Succeeded)
            {
                return ProductionModelActivationResult.Fail(aux2.ErrorCode, aux2.Message, aux2.Reference);
            }

            changedConfig |= aux2Migrated || !ReferenceEqualsByIdentity(_config.Auxiliary2ModelReference, aux2.Reference);
            ApplyReferenceToConfig(ModelRole.Auxiliary2, aux2);

            try
            {
                if (!await LoadResolvedSlotAsync(ModelRole.Primary, primary.ModelPath, useGpu, gpuIndex, cancellationToken).ConfigureAwait(false))
                {
                    RestoreConfigOnly(previousConfig);
                    return ProductionModelActivationResult.Fail(
                        "RuntimeModelLoadFailed",
                        $"模型加载失败: {Path.GetFileName(primary.ModelPath)}",
                        primary.Reference);
                }

                if (!aux1.Reference.IsEmpty &&
                    !await LoadResolvedSlotAsync(ModelRole.Auxiliary1, aux1.ModelPath, useGpu, gpuIndex, cancellationToken).ConfigureAwait(false))
                {
                    RestoreConfigOnly(previousConfig);
                    return ProductionModelActivationResult.Fail(
                        "RuntimeAuxiliary1LoadFailed",
                        $"辅助模型1加载失败: {Path.GetFileName(aux1.ModelPath)}",
                        aux1.Reference);
                }

                if (aux1.Reference.IsEmpty)
                {
                    _detectionService.UnloadAuxiliary1Model();
                }

                if (!aux2.Reference.IsEmpty &&
                    !await LoadResolvedSlotAsync(ModelRole.Auxiliary2, aux2.ModelPath, useGpu, gpuIndex, cancellationToken).ConfigureAwait(false))
                {
                    RestoreConfigOnly(previousConfig);
                    return ProductionModelActivationResult.Fail(
                        "RuntimeAuxiliary2LoadFailed",
                        $"辅助模型2加载失败: {Path.GetFileName(aux2.ModelPath)}",
                        aux2.Reference);
                }

                if (aux2.Reference.IsEmpty)
                {
                    _detectionService.UnloadAuxiliary2Model();
                }

                if (changedConfig || RecipeModelReferencesDiffer(_recipeManager.CurrentRecipe))
                {
                    CommitConfigAndRecipe(operation);
                }

                ProductionModelReadinessResult readiness = EnsureReadyForProduction();
                if (!readiness.Succeeded)
                {
                    throw new InvalidOperationException($"{readiness.ErrorCode}: {readiness.Message}");
                }

                return ProductionModelActivationResult.Ok($"{operation}完成", primary.Reference, primary.ModelPath);
            }
            catch (Exception ex)
            {
                await CompensateAsync(previousConfig, previousRuntime, useGpu, gpuIndex, compensationFailures).ConfigureAwait(false);
                if (compensationFailures.Count > 0)
                {
                    MarkFaulted(ex.Message, compensationFailures);
                    return ProductionModelActivationResult.Fail(
                        "ModelActivationFaulted",
                        $"模型激活提交失败且补偿失败: {ex.Message}",
                        primary.Reference,
                        isFaulted: true,
                        compensationFailures);
                }

                return ProductionModelActivationResult.Fail(
                    "ModelActivationCommitFailed",
                    $"模型激活提交失败，已恢复激活前状态: {ex.Message}",
                    primary.Reference);
            }
        }

        public ProductionModelReadinessResult EnsureReadyForProduction()
        {
            if (IsFaulted)
            {
                return ProductionModelReadinessResult.Fail("ModelActivationFaulted", FaultMessage);
            }

            _refreshRegistry();

            ProductionModelResolutionResult primary = _registry.ResolveReference(
                _config.CurrentModelReference,
                _config.RequireApprovedModelsForProduction);
            if (!primary.Succeeded)
            {
                return ProductionModelReadinessResult.Fail(primary.ErrorCode, primary.Message);
            }

            if (primary.Reference.IsEmpty)
            {
                return ProductionModelReadinessResult.Fail("PrimaryModelReferenceEmpty", "主模型引用为空。");
            }

            ProductionModelReadinessResult runtimeCheck = CheckRuntimeSlot(ModelRole.Primary, primary);
            if (!runtimeCheck.Succeeded)
            {
                return runtimeCheck;
            }

            foreach ((ModelRole Role, ProductionModelReference? Reference) slot in new[]
            {
                (ModelRole.Auxiliary1, _config.Auxiliary1ModelReference),
                (ModelRole.Auxiliary2, _config.Auxiliary2ModelReference)
            })
            {
                if (slot.Reference == null || slot.Reference.IsEmpty)
                {
                    DetectionModelSlotSnapshot runtimeSlot = GetRuntimeSlot(slot.Role);
                    if (runtimeSlot.IsLoaded)
                    {
                        return ProductionModelReadinessResult.Fail(
                            "RuntimeAuxiliaryNotConfigured",
                            $"{slot.Role} 已加载但 AppConfig 未配置引用。");
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
            }

            Recipe recipe = _recipeManager.CurrentRecipe ?? new Recipe();
            if (!ReferenceEqualsByIdentity(recipe.CurrentModelReference, _config.CurrentModelReference) ||
                !ReferenceEqualsByIdentity(recipe.Auxiliary1ModelReference, _config.Auxiliary1ModelReference) ||
                !ReferenceEqualsByIdentity(recipe.Auxiliary2ModelReference, _config.Auxiliary2ModelReference))
            {
                return ProductionModelReadinessResult.Fail(
                    "RecipeModelReferenceMismatch",
                    "Recipe 模型引用快照与 AppConfig 不一致。");
            }

            return ProductionModelReadinessResult.Ok();
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
            ClearFault();
            _refreshRegistry();
            AppConfig previousConfig = CaptureConfig();
            DetectionRuntimeModelSnapshot previousRuntime = _detectionService.RuntimeModelSnapshot;
            List<string> compensationFailures = new List<string>();

            if (!ProductionModelReference.TryParseSelectionValue(selectionValue, out ProductionModelReference reference))
            {
                ProductionModelResolutionResult migrated = _registry.MigrateLegacyReference(
                    selectionValue,
                    _config.RequireApprovedModelsForProduction);
                if (!migrated.Succeeded)
                {
                    return ProductionModelActivationResult.Fail(migrated.ErrorCode, migrated.Message, migrated.Reference);
                }

                reference = migrated.Reference;
            }

            if (reference.IsEmpty && !allowEmpty)
            {
                return ProductionModelActivationResult.Fail("PrimaryModelReferenceEmpty", "主模型不能为空。", reference);
            }

            ProductionModelResolutionResult resolved = reference.IsEmpty
                ? ProductionModelResolutionResult.Ok(reference, new ModelRegistryEntry(), string.Empty)
                : _registry.ResolveReference(reference, _config.RequireApprovedModelsForProduction);
            if (!resolved.Succeeded)
            {
                return ProductionModelActivationResult.Fail(resolved.ErrorCode, resolved.Message, reference);
            }

            try
            {
                if (reference.IsEmpty)
                {
                    UnloadSlot(role);
                }
                else if (!await LoadResolvedSlotAsync(role, resolved.ModelPath, useGpu, gpuIndex, cancellationToken).ConfigureAwait(false))
                {
                    return ProductionModelActivationResult.Fail(
                        "RuntimeModelLoadFailed",
                        $"模型加载失败: {Path.GetFileName(resolved.ModelPath)}",
                        resolved.Reference);
                }

                ApplyReferenceToConfig(role, resolved);
                CommitConfigAndRecipe(operation);

                ProductionModelReadinessResult readiness = EnsureReadyForProduction();
                if (!readiness.Succeeded)
                {
                    throw new InvalidOperationException($"{readiness.ErrorCode}: {readiness.Message}");
                }

                return ProductionModelActivationResult.Ok($"{operation}完成", resolved.Reference, resolved.ModelPath);
            }
            catch (Exception ex)
            {
                await CompensateAsync(previousConfig, previousRuntime, useGpu, gpuIndex, compensationFailures).ConfigureAwait(false);
                if (compensationFailures.Count > 0)
                {
                    MarkFaulted(ex.Message, compensationFailures);
                    return ProductionModelActivationResult.Fail(
                        "ModelActivationFaulted",
                        $"模型激活提交失败且补偿失败: {ex.Message}",
                        resolved.Reference,
                        isFaulted: true,
                        compensationFailures);
                }

                return ProductionModelActivationResult.Fail(
                    "ModelActivationCommitFailed",
                    $"模型激活提交失败，已恢复激活前状态: {ex.Message}",
                    resolved.Reference);
            }
        }

        private ProductionModelResolutionResult ResolveConfiguredOrLegacy(
            ProductionModelReference? reference,
            string legacyValue,
            bool allowEmpty,
            out bool migrated)
        {
            migrated = false;
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
                    "主模型引用为空。");
            }

            migrated = true;
            return _registry.MigrateLegacyReference(legacyValue, _config.RequireApprovedModelsForProduction);
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
            if (role == ModelRole.Auxiliary1)
            {
                _detectionService.UnloadAuxiliary1Model();
            }
            else if (role == ModelRole.Auxiliary2)
            {
                _detectionService.UnloadAuxiliary2Model();
            }
        }

        private void ApplyReferenceToConfig(ModelRole role, ProductionModelResolutionResult resolved)
        {
            ProductionModelReference reference = resolved.Reference.Clone();
            string fileName = string.IsNullOrWhiteSpace(resolved.Entry?.UsedModelName)
                ? Path.GetFileName(resolved.ModelPath)
                : resolved.Entry!.UsedModelName;

            switch (role)
            {
                case ModelRole.Primary:
                    _config.CurrentModelReference = reference;
                    _config.CurrentModelFileName = reference.IsEmpty ? string.Empty : fileName;
                    break;
                case ModelRole.Auxiliary1:
                    _config.Auxiliary1ModelReference = reference;
                    _config.Auxiliary1ModelPath = reference.IsEmpty ? string.Empty : fileName;
                    break;
                case ModelRole.Auxiliary2:
                    _config.Auxiliary2ModelReference = reference;
                    _config.Auxiliary2ModelPath = reference.IsEmpty ? string.Empty : fileName;
                    break;
            }
        }

        private void CommitConfigAndRecipe(string operation)
        {
            if (!_saveConfig())
            {
                throw new InvalidOperationException(_config.LastError ?? "配置保存失败");
            }

            _recipeManager.SaveNewVersion(
                _config,
                _roiSnapshotProvider(),
                _operatorIdProvider(),
                _operatorRoleProvider(),
                operation);
        }

        private async Task CompensateAsync(
            AppConfig previousConfig,
            DetectionRuntimeModelSnapshot previousRuntime,
            bool useGpu,
            int gpuIndex,
            List<string> failures)
        {
            try
            {
                _config.CopyFrom(previousConfig);
                if (!_saveConfig())
                {
                    failures.Add(_config.LastError ?? "恢复旧 AppConfig 保存失败");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"恢复旧 AppConfig 失败: {ex.Message}");
            }

            await RestoreRuntimeSlotAsync(previousRuntime.Primary, useGpu, gpuIndex, failures).ConfigureAwait(false);
            await RestoreRuntimeSlotAsync(previousRuntime.Auxiliary1, useGpu, gpuIndex, failures).ConfigureAwait(false);
            await RestoreRuntimeSlotAsync(previousRuntime.Auxiliary2, useGpu, gpuIndex, failures).ConfigureAwait(false);
        }

        private async Task RestoreRuntimeSlotAsync(
            DetectionModelSlotSnapshot slot,
            bool useGpu,
            int gpuIndex,
            List<string> failures)
        {
            try
            {
                if (slot.Role == ModelRole.Primary)
                {
                    if (!slot.IsLoaded || string.IsNullOrWhiteSpace(slot.ModelPath))
                    {
                        if (_detectionService.RuntimeModelSnapshot.Primary.IsLoaded)
                        {
                            failures.Add("无法卸载已加载的主模型以恢复为空运行时。");
                        }

                        return;
                    }

                    bool restored = await _detectionService.LoadModelAsync(slot.ModelPath, useGpu, gpuIndex).ConfigureAwait(false);
                    if (!restored)
                    {
                        failures.Add($"恢复旧主模型失败: {slot.ModelPath}");
                    }

                    return;
                }

                if (!slot.IsLoaded || string.IsNullOrWhiteSpace(slot.ModelPath))
                {
                    UnloadSlot(slot.Role);
                    return;
                }

                bool ok = await LoadResolvedSlotAsync(slot.Role, slot.ModelPath, useGpu, gpuIndex, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    failures.Add($"恢复旧{slot.Role}失败: {slot.ModelPath}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"恢复旧{slot.Role}异常: {ex.Message}");
            }
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
                    $"{role} 运行时模型未加载。");
            }

            string expectedPath = NormalizePath(resolved.ModelPath);
            string actualPath = NormalizePath(slot.ModelPath);
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
            {
                return ProductionModelReadinessResult.Fail(
                    "RuntimeModelPathMismatch",
                    $"{role} 运行时路径与 AppConfig 引用解析路径不一致。Expected={expectedPath}; Actual={actualPath}");
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

        private bool RecipeModelReferencesDiffer(Recipe? recipe)
        {
            if (recipe == null)
            {
                return true;
            }

            return !ReferenceEqualsByIdentity(recipe.CurrentModelReference, _config.CurrentModelReference) ||
                   !ReferenceEqualsByIdentity(recipe.Auxiliary1ModelReference, _config.Auxiliary1ModelReference) ||
                   !ReferenceEqualsByIdentity(recipe.Auxiliary2ModelReference, _config.Auxiliary2ModelReference);
        }

        private static bool ReferenceEqualsByIdentity(
            ProductionModelReference? left,
            ProductionModelReference? right)
        {
            return (left ?? ProductionModelReference.Empty()).IdentityEquals(right ?? ProductionModelReference.Empty());
        }

        private AppConfig CaptureConfig()
        {
            return AppConfig.FromJson(_config.ToPortableJson());
        }

        private void RestoreConfigOnly(AppConfig previousConfig)
        {
            _config.CopyFrom(previousConfig);
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
                _faulted = true;
                _faultMessage = $"原始失败: {originalFailure}; 补偿失败: {string.Join("; ", compensationFailures)}";
            }
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
    }
}
