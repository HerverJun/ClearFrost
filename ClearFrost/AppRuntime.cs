using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Services.Replay;
using ClearFrost.Yolo;
using Microsoft.Data.Sqlite;

namespace ClearFrost
{
    internal sealed class AppRuntime : IAsyncDisposable, IDisposable
    {
        private static readonly TimeSpan QueueFlushTimeout = TimeSpan.FromSeconds(10);

        private bool _stopRequested;
        private bool _disposed;

        public AppRuntime(AppConfig appConfig)
            : this(
                appConfig,
                CreateCameraManager(appConfig),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null)
        {
        }

        internal AppRuntime(
            AppConfig appConfig,
            CameraManager cameraManager,
            ICameraService? cameraService,
            IPlcService? plcService,
            IDetectionService? detectionService,
            IStorageService? storageService,
            IStatisticsService? statisticsService,
            IDatabaseService? databaseService,
            ImageSaveQueue? imageSaveQueue,
            DetectionRecordQueue? detectionRecordQueue,
            WebUIController? webUIController,
            RecipeManager? recipeManager = null,
            ModelRegistry? modelRegistry = null,
            StartupDiagnostics? startupDiagnostics = null,
            DiagnosticPackageExporter? diagnosticPackageExporter = null)
        {
            AppConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            CameraManager = cameraManager ?? throw new ArgumentNullException(nameof(cameraManager));
            CameraService = cameraService ?? new CameraService(CameraManager);
            PlcService = plcService ?? new PlcService();
            DetectionService = detectionService ?? new DetectionService(appConfig.EnableGpu, appConfig.GpuIndex);
            StorageService = storageService ?? new StorageService(appConfig.StoragePath);
            StatisticsService = statisticsService ?? new StatisticsService(StorageService.SystemPath.Replace("\\System", ""));
            DatabaseService = databaseService ?? new SqliteDatabaseService();
            ImageSaveQueue = imageSaveQueue ?? new ImageSaveQueue();
            DetectionRecordQueue = detectionRecordQueue ?? new DetectionRecordQueue(DatabaseService);
            RecipeManager = recipeManager ?? new RecipeManager();
            RecipeManager.LoadOrCreateDefault(appConfig);
            ModelRegistry = modelRegistry ?? new ModelRegistry();
            RefreshModelRegistry();
            OperationAuditService = new OperationAuditService(Path.Combine(StorageService.LogBasePath, "Outbox"));
            DecisionEvaluator = new InspectionDecisionEvaluator();
            ReplayPolicy = new ReplayAcceptancePolicy();
            ManualReviewStore = new SqliteManualReviewStore(
                DatabaseService,
                Path.Combine(StorageService.SystemPath, "manual-review.db"),
                OperationAuditService,
                () => AppConfig.CurrentOperatorId,
                () => AppConfig.CurrentOperatorRole);
            ReplayDatasetStore = new FileReplayDatasetStore(
                DatabaseService,
                Path.Combine(StorageService.SystemPath, "ReplayDatasets"));
            ReplayRunStore = new SqliteReplayRunStore(
                Path.Combine(StorageService.SystemPath, "replay-runs.db"),
                Path.Combine(StorageService.SystemPath, "ReplayReports"));
            ReplayRunStore.MarkNonTerminalRunsInterruptedAsync("default").GetAwaiter().GetResult();
            ModelApprovalEvidenceStore = new FileModelApprovalEvidenceStore(
                Path.Combine(StorageService.SystemPath, "ReplayEvidence"),
                ReplayPolicy);
            ReplayAssetCoordinator = new ReplayAssetChangeCoordinator();
            TryMigrateLegacyReplayApproval();
            ReplayProductionGate = new ReplayApprovalEvidenceProductionGate(
                ModelApprovalEvidenceStore,
                ReplayDatasetStore,
                ReplayRunStore);
            ReplayIntegrityScanner = new ReplayIntegrityScanner(
                ModelRegistry,
                ReplayProductionGate,
                ReplayDatasetStore,
                ReplayRunStore,
                ModelApprovalEvidenceStore,
                OperationAuditService);
            ReplayModelValidator = new ReplayModelValidator();
            ReplayInferenceRunner = new ProductionReplayInferenceRunner(
                detectionServiceFactory: () => new DetectionService(appConfig.EnableGpu, appConfig.GpuIndex),
                decisionEvaluator: DecisionEvaluator,
                useGpu: appConfig.EnableGpu,
                gpuIndex: appConfig.GpuIndex);
            ReplayApplicationService = new ReplayApplicationService(
                ReplayDatasetStore,
                ReplayInferenceRunner,
                ReplayModelValidator,
                ReplayRunStore,
                ReplayPolicy);
            ReplayCoordinator = new ReplayRunCoordinator(ReplayApplicationService);
            ReplayDatasetLifecycleService = new ReplayDatasetLifecycleService(
                ReplayDatasetStore,
                ReplayRunStore,
                ModelApprovalEvidenceStore,
                ReplayAssetCoordinator,
                OperationAuditService,
                () => AppConfig.CurrentOperatorId,
                () => AppConfig.CurrentOperatorRole,
                datasetId => ReplayCoordinator.IsDatasetActive(datasetId));
            ReplayApprovalApplicationService = new ReplayApprovalApplicationService(
                ModelRegistry,
                () => RefreshModelRegistry(),
                ReplayRunStore,
                ReplayDatasetStore,
                ModelApprovalEvidenceStore,
                ReplayProductionGate,
                ReplayPolicy,
                OperationAuditService,
                () => AppConfig.CurrentOperatorId,
                () => AppConfig.CurrentOperatorRole,
                ReplayAssetCoordinator);
            HealthMonitor = new HealthMonitor(
                CameraService,
                PlcService,
                DetectionService,
                StorageService,
                ImageSaveQueue,
                DetectionRecordQueue);
            WebUIController = webUIController ?? new WebUIController();
            StartupDiagnostics = startupDiagnostics ?? new StartupDiagnostics();
            StartupDiagnostics.Run(
                AppConfig,
                StorageService,
                ModelRegistry,
                (role, entry, reference) => ReplayProductionGate.Validate(role, entry, reference));
            DiagnosticPackageExporter = diagnosticPackageExporter ?? new DiagnosticPackageExporter();
        }

        public AppConfig AppConfig { get; }

        public CameraManager CameraManager { get; }

        public ICameraService CameraService { get; }

        public IPlcService PlcService { get; }

        public IDetectionService DetectionService { get; }

        public IStorageService StorageService { get; }

        public IStatisticsService StatisticsService { get; }

        public IDatabaseService DatabaseService { get; }

        public ImageSaveQueue ImageSaveQueue { get; }

        public DetectionRecordQueue DetectionRecordQueue { get; }

        public RecipeManager RecipeManager { get; }

        public ModelRegistry ModelRegistry { get; }

        public OperationAuditService OperationAuditService { get; }

        public IInspectionDecisionEvaluator DecisionEvaluator { get; }

        public ReplayAcceptancePolicy ReplayPolicy { get; }

        public IManualReviewStore ManualReviewStore { get; }

        public IReplayDatasetStore ReplayDatasetStore { get; }

        public IReplayRunStore ReplayRunStore { get; }

        public IReplayInferenceRunner ReplayInferenceRunner { get; }

        public IReplayModelValidator ReplayModelValidator { get; }

        public ReplayApplicationService ReplayApplicationService { get; }

        public ReplayRunCoordinator ReplayCoordinator { get; }

        internal ReplayAssetChangeCoordinator ReplayAssetCoordinator { get; }

        internal ReplayDatasetLifecycleService ReplayDatasetLifecycleService { get; }

        public IModelApprovalEvidenceStore ModelApprovalEvidenceStore { get; }

        public ReplayApprovalEvidenceProductionGate ReplayProductionGate { get; }

        public ReplayIntegrityScanner ReplayIntegrityScanner { get; }

        internal ReplayApprovalApplicationService ReplayApprovalApplicationService { get; }

        public HealthMonitor HealthMonitor { get; }

        public StartupDiagnostics StartupDiagnostics { get; }

        public DiagnosticPackageExporter DiagnosticPackageExporter { get; }

        public WebUIController WebUIController { get; }

        public bool IsStartupReady => StartupDiagnostics.CurrentReport.IsReady;

        public string StartupBlockingSummary =>
            string.Join(
                "; ",
                StartupDiagnostics.CurrentReport.Items
                    .Where(i => i.Status == StartupDiagnosticStatus.Fail && i.IsBlocking)
                    .Select(i => string.IsNullOrWhiteSpace(i.Details)
                        ? $"{i.Name}: {i.Message}"
                        : $"{i.Name}: {i.Message} {i.Details}"));

        public IReadOnlyList<ModelRegistryEntry> RefreshModelRegistry()
        {
            return ModelRegistry.Scan(CreateModelRegistryScanOptions(AppConfig));
        }

        public async Task<string> ExportDiagnosticPackageAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            var recentRecords = await DatabaseService.GetRecordsAsync(limit: 100).ConfigureAwait(false);
            return await DiagnosticPackageExporter.ExportAsync(
                new DiagnosticPackageRequest
                {
                    OutputDirectory = outputDirectory,
                    AppConfig = AppConfig,
                    Recipe = RecipeManager.CurrentRecipe,
                    ModelEntries = ModelRegistry.Entries,
                    StartupDiagnostics = StartupDiagnostics.CurrentReport,
                    HealthSnapshot = HealthMonitor.GetSnapshot(),
                    RecentRecords = recentRecords,
                    LogsDirectory = StorageService.LogBasePath
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;

            try
            {
                using var replayStopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                replayStopCts.CancelAfter(QueueFlushTimeout);
                await ReplayCoordinator.CancelAndWaitAsync(replayStopCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 取消 Replay 失败: {ex.Message}");
            }

            try
            {
                PlcService.StopMonitoring();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 停止 PLC 监听失败: {ex.Message}");
            }

            try
            {
                CameraService.StopCapture();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 停止相机采集失败: {ex.Message}");
            }

            try
            {
                if (CameraService is CameraService concreteCameraService)
                {
                    concreteCameraService.ReleaseCurrentCamera();
                }
                else
                {
                    CameraService.Close();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 关闭相机失败: {ex.Message}");
            }

            try
            {
                StatisticsService.SaveAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 保存统计失败: {ex.Message}");
            }

            await FlushQueuesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task FlushQueuesAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine(
                $"[AppRuntime] 准备排空保存队列: Images={ImageSaveQueue.PendingCount}/{ImageSaveQueue.Capacity}, " +
                $"ImageBuffer={FormatBytes(ImageSaveQueue.PendingBytes)}/{FormatBytes(ImageSaveQueue.MaxBufferedBytes)}, " +
                $"ImageDropped={ImageSaveQueue.DroppedCount}, ImageFailed={ImageSaveQueue.FailedCount}, " +
                $"Records={DetectionRecordQueue.PendingCount}/{DetectionRecordQueue.Capacity}, " +
                $"RecordDropped={DetectionRecordQueue.DroppedCount}, RecordFailed={DetectionRecordQueue.FailedCount}");

            try
            {
                using var recordFlushCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                recordFlushCts.CancelAfter(QueueFlushTimeout);
                await DetectionRecordQueue.StopAsync(recordFlushCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine(
                    $"[AppRuntime] 数据库记录队列排空超时: Pending={DetectionRecordQueue.PendingCount}, " +
                    $"Saved={DetectionRecordQueue.SavedCount}, Dropped={DetectionRecordQueue.DroppedCount}, Failed={DetectionRecordQueue.FailedCount}");
            }

            try
            {
                using var imageFlushCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                imageFlushCts.CancelAfter(QueueFlushTimeout);
                await ImageSaveQueue.StopAsync(imageFlushCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine(
                    $"[AppRuntime] 图像保存队列排空超时: Pending={ImageSaveQueue.PendingCount}, " +
                    $"Buffer={FormatBytes(ImageSaveQueue.PendingBytes)}/{FormatBytes(ImageSaveQueue.MaxBufferedBytes)}, " +
                    $"Saved={ImageSaveQueue.SavedCount}, Dropped={ImageSaveQueue.DroppedCount}, Failed={ImageSaveQueue.FailedCount}");
            }

            Debug.WriteLine(
                $"[AppRuntime] 保存队列排空结束: Images={ImageSaveQueue.PendingCount}, Records={DetectionRecordQueue.PendingCount}");
        }

        private static string FormatBytes(long bytes)
        {
            return $"{bytes / 1024d / 1024d:F1}MB";
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                using var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await StopAsync(disposeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] StopAsync during dispose failed: {ex.Message}");
            }

            try
            {
                ReplayCoordinator.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 ReplayCoordinator 失败: {ex.Message}");
            }

            try
            {
                ReplayAssetCoordinator.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 ReplayAssetCoordinator 失败: {ex.Message}");
            }

            try
            {
                DetectionService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 DetectionService 失败: {ex.Message}");
            }

            try
            {
                StatisticsService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 StatisticsService 失败: {ex.Message}");
            }

            try
            {
                DetectionRecordQueue.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 DetectionRecordQueue 失败: {ex.Message}");
            }

            try
            {
                DatabaseService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 DatabaseService 失败: {ex.Message}");
            }

            try
            {
                ImageSaveQueue.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 ImageSaveQueue 失败: {ex.Message}");
            }

            try
            {
                WebUIController.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 WebUIController 失败: {ex.Message}");
            }

            try
            {
                PlcService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 PlcService 失败: {ex.Message}");
            }

            try
            {
                CameraService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 CameraService 失败: {ex.Message}");
            }

            try
            {
                StorageService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 StorageService 失败: {ex.Message}");
            }

            try
            {
                SqliteConnection.ClearAllPools();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 清理 SQLite 连接池失败: {ex.Message}");
            }

            try
            {
                CameraManager.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 CameraManager 失败: {ex.Message}");
            }

            try
            {
                WindowHelpers.RestoreSleep();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 恢复休眠策略失败: {ex.Message}");
            }

            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static CameraManager CreateCameraManager(AppConfig appConfig)
        {
            var manager = new CameraManager(appConfig.IsDebugMode);
            manager.LoadFromConfig(appConfig);
            return manager;
        }

        private void TryMigrateLegacyReplayApproval()
        {
            if (!AppConfig.RequireApprovedModelsForProduction)
            {
                return;
            }

            foreach ((ModelRole Role, ProductionModelReference? Reference) slot in GetConfiguredModelSlots())
            {
                TryMigrateLegacyReplayApprovalSlot(slot.Role, slot.Reference);
            }
        }

        private void TryMigrateLegacyReplayApprovalSlot(
            ModelRole role,
            ProductionModelReference? reference)
        {
            ProductionModelReference currentReference = reference?.Clone() ?? ProductionModelReference.Empty();
            if (currentReference.IsEmpty || currentReference.Type != ProductionModelReferenceType.ApprovedPackage)
            {
                return;
            }

            ProductionModelResolutionResult resolved = ModelRegistry.ResolveReference(
                currentReference,
                requireProductionApproval: true);
            if (!resolved.Succeeded ||
                resolved.Entry == null ||
                resolved.Entry.Manifest == null ||
                string.IsNullOrWhiteSpace(resolved.Entry.ManifestPath))
            {
                return;
            }

            ModelRegistryEntry entry = resolved.Entry;
            ModelApprovalMetadata? approval = entry.Manifest.Approval;
            if (approval == null ||
                !string.Equals(approval.Status, ModelApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(approval.ReplayEvidenceId) ||
                approval.LegacyMigration != null)
            {
                return;
            }

            if (!File.Exists(entry.ManifestPath) || string.IsNullOrWhiteSpace(entry.ModelPath) || !File.Exists(entry.ModelPath))
            {
                return;
            }

            string actualHash;
            try
            {
                actualHash = FileReplayDatasetStore.ComputeSha256(entry.ModelPath);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(actualHash) ||
                !string.Equals(actualHash, currentReference.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualHash, entry.ModelHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualHash, entry.Manifest.EffectiveHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ModelPackageManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                    File.ReadAllText(entry.ManifestPath),
                    ReplayJson.Options) ?? new ModelPackageManifest();
            }
            catch
            {
                return;
            }

            if (manifest.Approval == null ||
                !string.Equals(manifest.Approval.Status, ModelApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(manifest.Approval.ReplayEvidenceId) ||
                manifest.Approval.LegacyMigration != null)
            {
                return;
            }

            string configReference = currentReference.ToSelectionValue();
            manifest.Approval.LegacyMigration = new ModelApprovalLegacyMigration
            {
                MigrationId = $"legacy-{entry.ModelId}-{entry.Version}-{role}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
                ModelRole = role.ToString(),
                ModelId = entry.ModelId,
                Version = entry.Version,
                ModelHash = actualHash,
                ManifestHash = string.Empty,
                ConfigReference = configReference,
                MigratedAt = DateTimeOffset.UtcNow
            };
            manifest.Approval.LegacyMigration.ManifestHash =
                ReplayApprovalEvidenceProductionGate.ComputeLegacyManifestHash(manifest);

            try
            {
                AtomicFileWriter.WriteAllText(entry.ManifestPath, JsonSerializer.Serialize(manifest, ReplayJson.Options));
                RefreshModelRegistry();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] Legacy Replay approval migration skipped: {ex.Message}");
            }
        }

        private IEnumerable<(ModelRole Role, ProductionModelReference? Reference)> GetConfiguredModelSlots()
        {
            yield return (ModelRole.Primary, AppConfig.CurrentModelReference);
            yield return (ModelRole.Auxiliary1, AppConfig.Auxiliary1ModelReference);
            yield return (ModelRole.Auxiliary2, AppConfig.Auxiliary2ModelReference);
        }

        private static ModelRegistryScanOptions CreateModelRegistryScanOptions(AppConfig appConfig)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string packageDirectory = Path.IsPathRooted(appConfig.ModelPackageDirectory)
                ? appConfig.ModelPackageDirectory
                : Path.Combine(baseDirectory, appConfig.ModelPackageDirectory);

            return new ModelRegistryScanOptions
            {
                PackageDirectory = packageDirectory,
                OnnxDirectory = Path.Combine(baseDirectory, "ONNX"),
                StrictPackageMode = appConfig.StrictModelPackageMode,
                RequireProductionApproval = appConfig.RequireApprovedModelsForProduction
            };
        }
    }
}
