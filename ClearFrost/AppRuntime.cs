using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Core.Security;
using ClearFrost.Hardware;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Services.Replay;
using ClearFrost.Yolo;
using Microsoft.Data.Sqlite;

namespace ClearFrost
{
    internal sealed class DiagnosticPackageExportSummary
    {
        public string PackagePath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string PackageSha256 { get; init; } = string.Empty;
        public string IndexSha256 { get; init; } = string.Empty;
        public int IntegrityEntryCount { get; init; }
        public int VerifiedEntryCount { get; init; }
        public int IntegrityFindingCount { get; init; }
        public string IntegrityStatus { get; init; } = string.Empty;
        public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.Now;
        public DateTimeOffset VerifiedAt { get; init; } = DateTimeOffset.Now;
    }

    internal sealed class DiagnosticPackageHistoryItem
    {
        public string PackagePath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTimeOffset LastWriteTime { get; init; }
        public string IntegrityStatus { get; init; } = "Pending";
    }

    internal sealed class FieldHandoffReportHistoryItem
    {
        public string ReportPath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTimeOffset LastWriteTime { get; init; }
        public DateTimeOffset GeneratedAt { get; init; }
        public string OverallStatus { get; init; } = "Pending";
        public int ShiftTaskCount { get; init; }
    }

    internal sealed class FieldEvidenceRetentionSummary
    {
        public string EvidenceType { get; init; } = string.Empty;
        public string DirectoryPath { get; init; } = string.Empty;
        public string SearchPattern { get; init; } = string.Empty;
        public int KeepLatest { get; init; }
        public int BeforeCount { get; init; }
        public int DeletedCount { get; init; }
        public int FailedCount { get; init; }
        public long FreedBytes { get; init; }
        public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.Now;
        public IReadOnlyList<string> DeletedFiles { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> FailedFiles { get; init; } = Array.Empty<string>();
    }

    internal sealed class AppRuntime : IAsyncDisposable, IDisposable
    {
        private static readonly TimeSpan QueueFlushTimeout = TimeSpan.FromSeconds(10);
        private const int DiagnosticPackageRetentionKeepLatest = 20;
        private const int FieldHandoffReportRetentionKeepLatest = 20;
        private const string DiagnosticPackageRetentionPattern = "ClearFrost_Diagnostics_*.zip";
        private const string FieldHandoffReportRetentionPattern = "handoff-*.md";

        private readonly object _storageRefreshLock = new object();
        private readonly object _auditChainStatusLock = new object();
        private FieldAuditChainStatus _lastAuditChainStatus = new FieldAuditChainStatus();
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
            DiagnosticPackageExporter? diagnosticPackageExporter = null,
            FieldHandoffReportExporter? fieldHandoffReportExporter = null)
        {
            AppConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            CameraManager = cameraManager ?? throw new ArgumentNullException(nameof(cameraManager));
            CameraService = cameraService ?? new CameraService(CameraManager);
            PlcService = plcService ?? new PlcService();
            DetectionService = detectionService ?? new DetectionService(appConfig.EnableGpu, appConfig.GpuIndex);
            StorageService = storageService ?? new StorageService(appConfig.StoragePath);
            StatisticsService = statisticsService ?? new StatisticsService(StorageService.BaseStoragePath);
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
            ReplayAssetCoordinator = new ReplayAssetChangeCoordinator();
            ReplayModelValidator = new ReplayModelValidator();
            ReplayInferenceRunner = new ProductionReplayInferenceRunner(
                detectionServiceFactory: () => new DetectionService(appConfig.EnableGpu, appConfig.GpuIndex),
                decisionEvaluator: DecisionEvaluator,
                useGpu: appConfig.EnableGpu,
                gpuIndex: appConfig.GpuIndex);
            TryMigrateLegacyReplayApproval();
            ApplyStorageBoundProductionServices(
                CreateStorageBoundProductionServiceGraph(
                    StorageBoundProductionServicePaths.FromBasePath(StorageService.BaseStoragePath),
                    markNonTerminalRunsInterrupted: true));
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
            FieldHandoffReportExporter = fieldHandoffReportExporter ?? new FieldHandoffReportExporter();
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

        public MaintenanceAdviceResolutionStore MaintenanceAdviceResolutionStore { get; private set; } = null!;

        public IInspectionDecisionEvaluator DecisionEvaluator { get; }

        public ReplayAcceptancePolicy ReplayPolicy { get; }

        public IManualReviewStore ManualReviewStore { get; private set; } = null!;

        public IReplayDatasetStore ReplayDatasetStore { get; private set; } = null!;

        public IReplayRunStore ReplayRunStore { get; private set; } = null!;

        public IReplayInferenceRunner ReplayInferenceRunner { get; }

        public IReplayModelValidator ReplayModelValidator { get; }

        public ReplayApplicationService ReplayApplicationService { get; private set; } = null!;

        public ReplayRunCoordinator ReplayCoordinator { get; private set; } = null!;

        internal ReplayAssetChangeCoordinator ReplayAssetCoordinator { get; }

        internal ReplayDatasetLifecycleService ReplayDatasetLifecycleService { get; private set; } = null!;

        public IModelApprovalEvidenceStore ModelApprovalEvidenceStore { get; private set; } = null!;

        public ReplayApprovalEvidenceProductionGate ReplayProductionGate { get; private set; } = null!;

        public ReplayIntegrityScanner ReplayIntegrityScanner { get; private set; } = null!;

        internal ReplayApprovalApplicationService ReplayApprovalApplicationService { get; private set; } = null!;

        public HealthMonitor HealthMonitor { get; }

        public StartupDiagnostics StartupDiagnostics { get; }

        public DiagnosticPackageExporter DiagnosticPackageExporter { get; }

        public FieldHandoffReportExporter FieldHandoffReportExporter { get; }

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
            try
            {
                return ModelRegistry.Scan(CreateModelRegistryScanOptions(AppConfig));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 刷新模型注册表失败（可能是文件占用等原因）: {ex.Message}");
                return ModelRegistry?.Entries ?? Array.Empty<ModelRegistryEntry>();
            }
        }

        public void RefreshStoragePath()
        {
            lock (_storageRefreshLock)
            {
                string requestedStoragePath = AppConfig.StoragePath ?? string.Empty;
                string oldStoragePath = StorageService.BaseStoragePath;
                string oldAuditOutbox = OperationAuditService.OutboxDirectory;
                StorageBoundProductionServiceGraph oldGraph = CaptureStorageBoundProductionServices(
                    StorageBoundProductionServicePaths.FromBasePath(oldStoragePath));
                StorageBoundProductionServiceGraph? newGraph = null;
                ReplayRunCoordinator? coordinatorToDispose = null;
                bool newGraphApplied = false;

                try
                {
                    if (ReplayCoordinator.IsReplayRunning || ReplayCoordinator.IsProductionRunning)
                    {
                        throw new InvalidOperationException("StoragePath refresh is blocked while production or Replay is running.");
                    }

                    StorageBoundProductionServicePaths newPaths =
                        ResolveStorageRefreshPaths(requestedStoragePath);
                    EnsureStorageRefreshPathsSafeForWrite(newPaths);
                    newGraph = CreateStorageBoundProductionServiceGraph(
                        newPaths,
                        markNonTerminalRunsInterrupted: false);

                    StorageService.UpdateStoragePath(newPaths.BaseStoragePath);
                    if (!IsSamePath(StorageService.BaseStoragePath, newPaths.BaseStoragePath))
                    {
                        throw new InvalidOperationException(
                            $"StorageService resolved a different path. Expected={newPaths.BaseStoragePath}; Actual={StorageService.BaseStoragePath}");
                    }

                    StatisticsService.UpdateStoragePath(StorageService.BaseStoragePath);
                    OperationAuditService.UpdateOutboxDirectory(newPaths.AuditOutboxPath);
                    ApplyStorageBoundProductionServices(newGraph);
                    newGraphApplied = true;

                    StartupDiagnostics.Run(
                        AppConfig,
                        StorageService,
                        ModelRegistry,
                        (role, entry, reference) => ReplayProductionGate.Validate(role, entry, reference));

                    if (!AppendStoragePathRefreshAudit(
                            OperationAuditStatus.Succeeded,
                            oldStoragePath,
                            newPaths.BaseStoragePath,
                            string.Empty))
                    {
                        throw new InvalidOperationException("StoragePath refresh succeeded but audit evidence could not be written.");
                    }

                    coordinatorToDispose = oldGraph.ReplayCoordinator;
                }
                catch (Exception ex)
                {
                    try
                    {
                        StorageService.UpdateStoragePath(oldStoragePath);
                        StatisticsService.UpdateStoragePath(StorageService.BaseStoragePath);
                        OperationAuditService.UpdateOutboxDirectory(oldAuditOutbox);
                        ApplyStorageBoundProductionServices(oldGraph);
                        StartupDiagnostics.Run(
                            AppConfig,
                            StorageService,
                            ModelRegistry,
                            (role, entry, reference) => ReplayProductionGate.Validate(role, entry, reference));
                        StartupDiagnostics.ReportStoragePathRefreshFailure(
                            requestedStoragePath,
                            StorageService.BaseStoragePath,
                            ex.Message);
                    }
                    catch (Exception rollbackEx)
                    {
                        Debug.WriteLine($"[AppRuntime] StoragePath refresh rollback failed: {rollbackEx.Message}");
                        StartupDiagnostics.ReportStoragePathRefreshFailure(
                            requestedStoragePath,
                            oldStoragePath,
                            $"Refresh failed: {ex.Message}; rollback failed: {rollbackEx.Message}");
                    }

                    AppendStoragePathRefreshAudit(
                        OperationAuditStatus.Failed,
                        oldStoragePath,
                        requestedStoragePath,
                        ex.Message);
                    StorageService.WriteErrorLog(
                        $"StoragePathRefresh failed. Requested={requestedStoragePath}; Active={StorageService.BaseStoragePath}; Error={ex.Message}");

                    if (newGraphApplied && newGraph != null && !ReferenceEquals(newGraph.ReplayCoordinator, oldGraph.ReplayCoordinator))
                    {
                        try
                        {
                            newGraph.ReplayCoordinator.Dispose();
                        }
                        catch (Exception disposeEx)
                        {
                            Debug.WriteLine($"[AppRuntime] 释放失败的 ReplayCoordinator 失败: {disposeEx.Message}");
                        }
                    }

                    throw new InvalidOperationException(
                        $"StoragePath refresh failed; runtime storage-bound services remain on {StorageService.BaseStoragePath}.",
                        ex);
                }
                finally
                {
                    if (coordinatorToDispose != null && !ReferenceEquals(coordinatorToDispose, ReplayCoordinator))
                    {
                        try
                        {
                            coordinatorToDispose.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[AppRuntime] 释放旧 ReplayCoordinator 失败: {ex.Message}");
                        }
                    }
                }
            }
        }

        private StorageBoundProductionServiceGraph CaptureStorageBoundProductionServices(
            StorageBoundProductionServicePaths paths)
        {
            return new StorageBoundProductionServiceGraph(
                paths,
                MaintenanceAdviceResolutionStore,
                ManualReviewStore,
                ReplayDatasetStore,
                ReplayRunStore,
                ModelApprovalEvidenceStore,
                ReplayProductionGate,
                ReplayIntegrityScanner,
                ReplayApplicationService,
                ReplayCoordinator,
                ReplayDatasetLifecycleService,
                ReplayApprovalApplicationService);
        }

        private StorageBoundProductionServiceGraph CreateStorageBoundProductionServiceGraph(
            StorageBoundProductionServicePaths paths,
            bool markNonTerminalRunsInterrupted)
        {
            var maintenanceAdviceResolutionStore =
                new MaintenanceAdviceResolutionStore(paths.MaintenanceAdviceResolutionPath);
            var manualReviewStore = new SqliteManualReviewStore(
                DatabaseService,
                paths.ManualReviewDbPath,
                OperationAuditService,
                () => AppConfig.CurrentOperatorId,
                () => AppConfig.CurrentOperatorRole);
            var replayDatasetStore = new FileReplayDatasetStore(
                DatabaseService,
                paths.ReplayDatasetRoot);
            var replayRunStore = new SqliteReplayRunStore(
                paths.ReplayRunDbPath,
                paths.ReplayReportRoot);
            if (markNonTerminalRunsInterrupted)
            {
                replayRunStore.MarkNonTerminalRunsInterruptedAsync("default").GetAwaiter().GetResult();
            }

            var modelApprovalEvidenceStore = new FileModelApprovalEvidenceStore(
                paths.ReplayEvidenceRoot,
                ReplayPolicy);
            var replayProductionGate = new ReplayApprovalEvidenceProductionGate(
                modelApprovalEvidenceStore,
                replayDatasetStore,
                replayRunStore);
            var replayIntegrityScanner = new ReplayIntegrityScanner(
                ModelRegistry,
                replayProductionGate,
                replayDatasetStore,
                replayRunStore,
                modelApprovalEvidenceStore,
                OperationAuditService);
            var replayApplicationService = new ReplayApplicationService(
                replayDatasetStore,
                ReplayInferenceRunner,
                ReplayModelValidator,
                replayRunStore,
                ReplayPolicy);
            var replayCoordinator = new ReplayRunCoordinator(replayApplicationService);
            var replayDatasetLifecycleService = new ReplayDatasetLifecycleService(
                replayDatasetStore,
                replayRunStore,
                modelApprovalEvidenceStore,
                ReplayAssetCoordinator,
                OperationAuditService,
                () => AppConfig.CurrentOperatorId,
                () => AppConfig.CurrentOperatorRole,
                datasetId => replayCoordinator.IsDatasetActive(datasetId));
            var replayApprovalApplicationService = new ReplayApprovalApplicationService(
                ModelRegistry,
                () => RefreshModelRegistry(),
                replayRunStore,
                replayDatasetStore,
                modelApprovalEvidenceStore,
                replayProductionGate,
                ReplayPolicy,
                OperationAuditService,
                () => AppConfig.CurrentOperatorId,
                () => AppConfig.CurrentOperatorRole,
                ReplayAssetCoordinator);

            return new StorageBoundProductionServiceGraph(
                paths,
                maintenanceAdviceResolutionStore,
                manualReviewStore,
                replayDatasetStore,
                replayRunStore,
                modelApprovalEvidenceStore,
                replayProductionGate,
                replayIntegrityScanner,
                replayApplicationService,
                replayCoordinator,
                replayDatasetLifecycleService,
                replayApprovalApplicationService);
        }

        private void ApplyStorageBoundProductionServices(StorageBoundProductionServiceGraph graph)
        {
            MaintenanceAdviceResolutionStore = graph.MaintenanceAdviceResolutionStore;
            ManualReviewStore = graph.ManualReviewStore;
            ReplayDatasetStore = graph.ReplayDatasetStore;
            ReplayRunStore = graph.ReplayRunStore;
            ModelApprovalEvidenceStore = graph.ModelApprovalEvidenceStore;
            ReplayProductionGate = graph.ReplayProductionGate;
            ReplayIntegrityScanner = graph.ReplayIntegrityScanner;
            ReplayApplicationService = graph.ReplayApplicationService;
            ReplayCoordinator = graph.ReplayCoordinator;
            ReplayDatasetLifecycleService = graph.ReplayDatasetLifecycleService;
            ReplayApprovalApplicationService = graph.ReplayApprovalApplicationService;
        }

        private StorageBoundProductionServicePaths ResolveStorageRefreshPaths(string storagePath)
        {
            string requestedPath = string.IsNullOrWhiteSpace(storagePath)
                ? @"C:\GreeVisionData"
                : storagePath.Trim();
            string fullPath = Path.GetFullPath(requestedPath);
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"StoragePath root is not available: {root}");
            }

            return StorageBoundProductionServicePaths.FromBasePath(fullPath);
        }

        private static void EnsureStorageRefreshPathsSafeForWrite(StorageBoundProductionServicePaths paths)
        {
            EnsureEvidenceDirectorySafeForWrite(paths.BaseStoragePath, "存储根目录");
            EnsureEvidenceDirectorySafeForWrite(paths.ImageBasePath, "图像目录");
            EnsureEvidenceDirectorySafeForWrite(paths.LogBasePath, "日志目录");
            EnsureEvidenceDirectorySafeForWrite(paths.SystemPath, "系统证据目录");
            EnsureEvidenceDirectorySafeForWrite(paths.AuditOutboxPath, "审计 outbox 目录");
            EnsureEvidenceDirectorySafeForWrite(paths.DiagnosticPackagePath, "诊断包目录");
            EnsureEvidenceDirectorySafeForWrite(paths.HandoffReportPath, "交接报告目录");
            EnsureEvidenceDirectorySafeForWrite(paths.ReplayDatasetRoot, "Replay dataset 目录");
            EnsureEvidenceDirectorySafeForWrite(paths.ReplayReportRoot, "Replay report 目录");
            EnsureEvidenceDirectorySafeForWrite(paths.ReplayEvidenceRoot, "Replay approval evidence 目录");
            EnsureProbeFile(paths.SystemPath, "系统证据目录");
            EnsureProbeFile(paths.AuditOutboxPath, "审计 outbox 目录");
            EnsureProbeFile(paths.ReplayDatasetRoot, "Replay dataset 目录");
            EnsureProbeFile(paths.ReplayReportRoot, "Replay report 目录");
            EnsureProbeFile(paths.ReplayEvidenceRoot, "Replay approval evidence 目录");
        }

        private static void EnsureProbeFile(string directory, string displayName)
        {
            string probePath = Path.Combine(directory, $".storage-refresh-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.Write("ok");
                }

                var probe = new FileInfo(probePath);
                probe.Refresh();
                if (probe.Exists && HasReparsePoint(probe))
                {
                    throw new IOException($"{displayName}探针文件是链接文件: {probePath}");
                }
            }
            finally
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
        }

        private bool AppendStoragePathRefreshAudit(
            OperationAuditStatus status,
            string oldPath,
            string newPath,
            string failure)
        {
            try
            {
                return OperationAuditService.AppendAsync(new OperationAuditRecord
                {
                    Operation = "StoragePathRefresh",
                    Status = status,
                    OperatorId = ResolveCurrentOperatorId(),
                    Role = AppConfig.CurrentOperatorRole,
                    Details = $"OldPath={oldPath}; NewPath={newPath}",
                    FailureBlocker = status == OperationAuditStatus.Failed
                        ? failure ?? string.Empty
                        : string.Empty
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] StoragePathRefresh 审计写入失败: {ex.Message}");
                return false;
            }
        }

        private static bool IsSamePath(string left, string right)
        {
            string normalizedLeft = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class StorageBoundProductionServicePaths
        {
            private StorageBoundProductionServicePaths(string baseStoragePath)
            {
                BaseStoragePath = Path.GetFullPath(baseStoragePath);
                ImageBasePath = Path.Combine(BaseStoragePath, "Images");
                LogBasePath = Path.Combine(BaseStoragePath, "Logs");
                SystemPath = Path.Combine(BaseStoragePath, "System");
                AuditOutboxPath = Path.Combine(LogBasePath, "Outbox");
                DiagnosticPackagePath = Path.Combine(LogBasePath, "Diagnostics");
                HandoffReportPath = Path.Combine(LogBasePath, "HandoffReports");
                MaintenanceAdviceResolutionPath = Path.Combine(SystemPath, "maintenance-advice-resolution.json");
                ManualReviewDbPath = Path.Combine(SystemPath, "manual-review.db");
                ReplayDatasetRoot = Path.Combine(SystemPath, "ReplayDatasets");
                ReplayRunDbPath = Path.Combine(SystemPath, "replay-runs.db");
                ReplayReportRoot = Path.Combine(SystemPath, "ReplayReports");
                ReplayEvidenceRoot = Path.Combine(SystemPath, "ReplayEvidence");
            }

            public string BaseStoragePath { get; }
            public string ImageBasePath { get; }
            public string LogBasePath { get; }
            public string SystemPath { get; }
            public string AuditOutboxPath { get; }
            public string DiagnosticPackagePath { get; }
            public string HandoffReportPath { get; }
            public string MaintenanceAdviceResolutionPath { get; }
            public string ManualReviewDbPath { get; }
            public string ReplayDatasetRoot { get; }
            public string ReplayRunDbPath { get; }
            public string ReplayReportRoot { get; }
            public string ReplayEvidenceRoot { get; }

            public static StorageBoundProductionServicePaths FromBasePath(string baseStoragePath)
            {
                return new StorageBoundProductionServicePaths(baseStoragePath);
            }
        }

        private sealed class StorageBoundProductionServiceGraph
        {
            public StorageBoundProductionServiceGraph(
                StorageBoundProductionServicePaths paths,
                MaintenanceAdviceResolutionStore maintenanceAdviceResolutionStore,
                IManualReviewStore manualReviewStore,
                IReplayDatasetStore replayDatasetStore,
                IReplayRunStore replayRunStore,
                IModelApprovalEvidenceStore modelApprovalEvidenceStore,
                ReplayApprovalEvidenceProductionGate replayProductionGate,
                ReplayIntegrityScanner replayIntegrityScanner,
                ReplayApplicationService replayApplicationService,
                ReplayRunCoordinator replayCoordinator,
                ReplayDatasetLifecycleService replayDatasetLifecycleService,
                ReplayApprovalApplicationService replayApprovalApplicationService)
            {
                Paths = paths;
                MaintenanceAdviceResolutionStore = maintenanceAdviceResolutionStore;
                ManualReviewStore = manualReviewStore;
                ReplayDatasetStore = replayDatasetStore;
                ReplayRunStore = replayRunStore;
                ModelApprovalEvidenceStore = modelApprovalEvidenceStore;
                ReplayProductionGate = replayProductionGate;
                ReplayIntegrityScanner = replayIntegrityScanner;
                ReplayApplicationService = replayApplicationService;
                ReplayCoordinator = replayCoordinator;
                ReplayDatasetLifecycleService = replayDatasetLifecycleService;
                ReplayApprovalApplicationService = replayApprovalApplicationService;
            }

            public StorageBoundProductionServicePaths Paths { get; }
            public MaintenanceAdviceResolutionStore MaintenanceAdviceResolutionStore { get; }
            public IManualReviewStore ManualReviewStore { get; }
            public IReplayDatasetStore ReplayDatasetStore { get; }
            public IReplayRunStore ReplayRunStore { get; }
            public IModelApprovalEvidenceStore ModelApprovalEvidenceStore { get; }
            public ReplayApprovalEvidenceProductionGate ReplayProductionGate { get; }
            public ReplayIntegrityScanner ReplayIntegrityScanner { get; }
            public ReplayApplicationService ReplayApplicationService { get; }
            public ReplayRunCoordinator ReplayCoordinator { get; }
            public ReplayDatasetLifecycleService ReplayDatasetLifecycleService { get; }
            public ReplayApprovalApplicationService ReplayApprovalApplicationService { get; }
        }

        public async Task<DiagnosticPackageExportSummary> ExportDiagnosticPackageAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            HealthSnapshot healthSnapshot = HealthMonitor.GetSnapshot();
            OperationAuditChainVerificationResult auditChain =
                await VerifyOperationAuditChainAsync(cancellationToken).ConfigureAwait(false);
            FieldDiagnosticsSnapshot fieldDiagnostics = BuildFieldDiagnosticsSnapshot(healthSnapshot);
            int startupBlockerCount = CountStartupBlockers(StartupDiagnostics.CurrentReport);
            int maintenanceAdviceCount = fieldDiagnostics.MaintenanceAdvice?.Count ?? 0;
            string packagePath = outputDirectory;

            try
            {
                string directory = ResolveDiagnosticOutputDirectory(outputDirectory);
                EnsureEvidenceDirectorySafeForWrite(directory, "诊断包目录");
                var recentRecords = await DatabaseService.GetRecordsAsync(limit: 100).ConfigureAwait(false);
                packagePath = await DiagnosticPackageExporter.ExportAsync(
                    new DiagnosticPackageRequest
                    {
                        OutputDirectory = directory,
                        AppConfig = AppConfig,
                        Recipe = RecipeManager.CurrentRecipe,
                        CurrentRecipeVersion = RecipeManager.GetCurrentVersionInfo(),
                        ModelEntries = ModelRegistry.Entries,
                        RuntimeModelSnapshot = DetectionService.RuntimeModelSnapshot,
                        StartupDiagnostics = StartupDiagnostics.CurrentReport,
                        HealthSnapshot = healthSnapshot,
                        FieldDiagnostics = fieldDiagnostics,
                        OperationAuditChainVerification = auditChain,
                        RecentRecords = recentRecords,
                        LogsDirectory = StorageService.LogBasePath
                    },
                    cancellationToken).ConfigureAwait(false);

                DiagnosticPackageIntegrityVerificationResult verification =
                    await new DiagnosticPackageIntegrityVerifier().VerifyAsync(packagePath).ConfigureAwait(false);
                DiagnosticPackageExportSummary summary = BuildDiagnosticPackageExportSummary(packagePath, verification);
                if (!verification.Succeeded)
                {
                    TryDeleteFieldEvidenceFile(packagePath);
                    throw new InvalidOperationException(BuildDiagnosticPackageVerificationFailureMessage(verification));
                }

                await AuditDiagnosticPackageExportAsync(
                    correlationId,
                    OperationAuditStatus.Succeeded,
                    summary,
                    startupBlockerCount,
                    maintenanceAdviceCount,
                    fieldDiagnostics.Queues,
                    string.Empty).ConfigureAwait(false);
                await ApplyDiagnosticPackageRetentionAsync(
                    correlationId,
                    directory,
                    cancellationToken).ConfigureAwait(false);
                return summary;
            }
            catch (Exception ex)
            {
                await AuditDiagnosticPackageExportAsync(
                    correlationId,
                    OperationAuditStatus.Failed,
                    new DiagnosticPackageExportSummary
                    {
                        PackagePath = packagePath
                    },
                    startupBlockerCount,
                    maintenanceAdviceCount,
                    fieldDiagnostics.Queues,
                    ex.Message).ConfigureAwait(false);
                throw;
            }
        }

        public IReadOnlyList<DiagnosticPackageHistoryItem> QueryDiagnosticPackageHistory(
            string outputDirectory,
            int limit = 8)
        {
            string directory = ResolveDiagnosticOutputDirectory(outputDirectory);
            int cappedLimit = Math.Clamp(limit <= 0 ? 8 : limit, 1, 50);
            if (!Directory.Exists(directory))
            {
                return Array.Empty<DiagnosticPackageHistoryItem>();
            }

            if (DirectoryPathHasReparsePoint(directory))
            {
                return Array.Empty<DiagnosticPackageHistoryItem>();
            }

            return Directory
                .EnumerateFiles(directory, "ClearFrost_Diagnostics_*.zip", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => IsSafeTopLevelEvidenceFile(directory, info))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(cappedLimit)
                .Select(info => new DiagnosticPackageHistoryItem
                {
                    PackagePath = info.FullName,
                    FileName = info.Name,
                    SizeBytes = info.Length,
                    LastWriteTime = new DateTimeOffset(info.LastWriteTime),
                    IntegrityStatus = "Pending"
                })
                .ToList();
        }

        public async Task<DiagnosticPackageExportSummary> VerifyDiagnosticPackageAsync(
            string outputDirectory,
            string packagePath,
            CancellationToken cancellationToken = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            string normalizedPath = packagePath ?? string.Empty;

            try
            {
                normalizedPath = ResolveDiagnosticPackagePath(outputDirectory, normalizedPath);
                DiagnosticPackageIntegrityVerificationResult verification =
                    await new DiagnosticPackageIntegrityVerifier()
                        .VerifyAsync(normalizedPath, cancellationToken)
                        .ConfigureAwait(false);
                DiagnosticPackageExportSummary summary = BuildDiagnosticPackageExportSummary(normalizedPath, verification);
                await AuditDiagnosticPackageVerificationAsync(
                    correlationId,
                    verification.Succeeded ? OperationAuditStatus.Succeeded : OperationAuditStatus.Failed,
                    summary,
                    BuildDiagnosticPackageVerificationFindingSummary(verification)).ConfigureAwait(false);
                return summary;
            }
            catch (Exception ex)
            {
                await AuditDiagnosticPackageVerificationAsync(
                    correlationId,
                    OperationAuditStatus.Failed,
                    new DiagnosticPackageExportSummary
                    {
                        PackagePath = normalizedPath
                    },
                    ex.Message).ConfigureAwait(false);
                throw;
            }
        }

        public async Task<FieldHandoffReportSummary> ExportFieldHandoffReportAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            string requestedDirectory = outputDirectory ?? string.Empty;
            string reportPath = requestedDirectory;

            try
            {
                string directory = ResolveHandoffReportOutputDirectory(requestedDirectory);
                EnsureEvidenceDirectorySafeForWrite(directory, "交接报告目录");
                string diagnosticsDirectory = Path.Combine(StorageService.LogBasePath, "Diagnostics");
                OperationAuditChainVerificationResult auditChain =
                    await VerifyOperationAuditChainAsync(cancellationToken).ConfigureAwait(false);
                FieldDiagnosticsSnapshot snapshot = BuildFieldDiagnosticsSnapshot();
                IReadOnlyList<DiagnosticPackageHistoryItem> diagnosticPackages =
                    Directory.Exists(diagnosticsDirectory)
                        ? QueryDiagnosticPackageHistory(diagnosticsDirectory, 5)
                        : Array.Empty<DiagnosticPackageHistoryItem>();
                IReadOnlyList<MaintenanceAdviceResolutionRecord> maintenanceHistory =
                    QueryMaintenanceAdviceHistory(12);
                IReadOnlyList<OperationAuditRecord> recentAudits =
                    await QueryFieldHandoffAuditRecordsAsync(cancellationToken).ConfigureAwait(false);

                FieldHandoffReportSummary summary = await FieldHandoffReportExporter.ExportAsync(
                    new FieldHandoffReportRequest
                    {
                        OutputDirectory = directory,
                        FieldDiagnostics = snapshot,
                        DiagnosticPackages = diagnosticPackages,
                        MaintenanceAdviceHistory = maintenanceHistory,
                        RecentAuditRecords = recentAudits,
                        AuditChainVerification = auditChain,
                        OperatorId = ResolveCurrentOperatorId(),
                        Role = AppConfig.CurrentOperatorRole
                    },
                    cancellationToken).ConfigureAwait(false);

                reportPath = summary.ReportPath;
                await AuditFieldHandoffReportExportAsync(
                    correlationId,
                    OperationAuditStatus.Succeeded,
                    summary,
                    string.Empty).ConfigureAwait(false);
                await ApplyFieldHandoffReportRetentionAsync(
                    correlationId,
                    directory,
                    cancellationToken).ConfigureAwait(false);
                return summary;
            }
            catch (Exception ex)
            {
                await AuditFieldHandoffReportExportAsync(
                    correlationId,
                    OperationAuditStatus.Failed,
                    new FieldHandoffReportSummary
                    {
                        ReportPath = reportPath,
                        Message = ex.Message
                    },
                    ex.Message).ConfigureAwait(false);
                throw;
            }
        }

        public IReadOnlyList<FieldHandoffReportHistoryItem> QueryFieldHandoffReportHistory(
            string outputDirectory,
            int limit = 8)
        {
            string directory = ResolveHandoffReportOutputDirectory(outputDirectory);
            int cappedLimit = Math.Clamp(limit <= 0 ? 8 : limit, 1, 50);
            if (!Directory.Exists(directory))
            {
                return Array.Empty<FieldHandoffReportHistoryItem>();
            }

            if (DirectoryPathHasReparsePoint(directory))
            {
                return Array.Empty<FieldHandoffReportHistoryItem>();
            }

            return Directory
                .EnumerateFiles(directory, "handoff-*.md", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => IsSafeTopLevelEvidenceFile(directory, info))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(cappedLimit)
                .Select(info => BuildFieldHandoffReportHistoryItem(directory, info))
                .Where(item => item != null)
                .Cast<FieldHandoffReportHistoryItem>()
                .ToList();
        }

        public FieldDiagnosticsSnapshot BuildFieldDiagnosticsSnapshot(HealthSnapshot? healthSnapshot = null)
        {
            FieldDiagnosticsSnapshot snapshot = FieldDiagnosticsSnapshotFactory.Create(
                healthSnapshot ?? HealthMonitor.GetSnapshot(),
                StartupDiagnostics.CurrentReport,
                ModelRegistry.Entries,
                DetectionService.RuntimeModelSnapshot,
                DetectionService.CurrentModelName,
                DetectionService.GetLastMetrics(),
                RecipeManager.CurrentRecipe,
                GetLastAuditChainStatus());
            return EnrichMaintenanceAdviceSnapshot(snapshot);
        }

        public async Task<OperationAuditChainVerificationResult> VerifyOperationAuditChainAsync(
            CancellationToken cancellationToken = default)
        {
            OperationAuditChainVerificationResult result =
                await OperationAuditService.VerifyChainAsync(cancellationToken).ConfigureAwait(false);
            StoreAuditChainStatus(result);
            return result;
        }

        private FieldAuditChainStatus GetLastAuditChainStatus()
        {
            lock (_auditChainStatusLock)
            {
                return _lastAuditChainStatus;
            }
        }

        private void StoreAuditChainStatus(OperationAuditChainVerificationResult result)
        {
            FieldAuditChainStatus status =
                FieldDiagnosticsSnapshotFactory.BuildAuditChainStatus(result, DateTimeOffset.Now);
            lock (_auditChainStatusLock)
            {
                _lastAuditChainStatus = status;
            }
        }

        public IReadOnlyList<MaintenanceAdviceResolutionRecord> QueryMaintenanceAdviceHistory(int limit = 12)
        {
            return MaintenanceAdviceResolutionStore.QueryRecent(limit);
        }

        public async Task<MaintenanceAdviceActionResult> HandleMaintenanceAdviceActionAsync(
            string adviceId,
            string action,
            string notes,
            CancellationToken cancellationToken = default)
        {
            FieldDiagnosticsSnapshot currentSnapshot = FieldDiagnosticsSnapshotFactory.Create(
                HealthMonitor.GetSnapshot(),
                StartupDiagnostics.CurrentReport,
                ModelRegistry.Entries,
                DetectionService.RuntimeModelSnapshot,
                DetectionService.CurrentModelName,
                DetectionService.GetLastMetrics(),
                RecipeManager.CurrentRecipe);
            FieldMaintenanceAdvice? currentAdvice = currentSnapshot.MaintenanceAdvice.FirstOrDefault(advice =>
                string.Equals(advice.AdviceId, adviceId, StringComparison.OrdinalIgnoreCase));
            MaintenanceAdviceResolutionRecord? existing = MaintenanceAdviceResolutionStore.Find(adviceId);
            string normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();

            if (string.Equals(normalizedAction, "acknowledge", StringComparison.OrdinalIgnoreCase))
            {
                if (currentAdvice == null)
                {
                    return await BuildFailedMaintenanceAdviceActionAsync(
                        adviceId,
                        "MaintenanceAdviceNotActive",
                        "维护建议已不在当前诊断快照中，无需标记处理。",
                        cancellationToken).ConfigureAwait(false);
                }

                string message = string.IsNullOrWhiteSpace(notes)
                    ? "维护建议已标记为已处理，等待复检。"
                    : $"维护建议已标记为已处理: {notes.Trim()}";
                MaintenanceAdviceResolutionRecord record = await MaintenanceAdviceResolutionStore.AppendAsync(
                    currentAdvice,
                    MaintenanceAdviceResolutionStatuses.Acknowledged,
                    ResolveCurrentOperatorId(),
                    AppConfig.CurrentOperatorRole,
                    notes,
                    message,
                    cancellationToken).ConfigureAwait(false);

                await AuditMaintenanceAdviceActionAsync(
                    record,
                    OperationAuditStatus.Succeeded,
                    false,
                    message).ConfigureAwait(false);
                return BuildMaintenanceAdviceActionResult(true, false, record, message);
            }

            if (string.Equals(normalizedAction, "recheck", StringComparison.OrdinalIgnoreCase))
            {
                FieldMaintenanceAdvice adviceForRecord = currentAdvice ?? ExistingRecordToAdvice(existing);
                if (string.IsNullOrWhiteSpace(adviceForRecord.AdviceId))
                {
                    return await BuildFailedMaintenanceAdviceActionAsync(
                        adviceId,
                        "MaintenanceAdviceRecordMissing",
                        "未找到维护建议处理记录，无法复检。",
                        cancellationToken).ConfigureAwait(false);
                }

                bool cleared = currentAdvice == null;
                string status = cleared
                    ? MaintenanceAdviceResolutionStatuses.RecheckPassed
                    : MaintenanceAdviceResolutionStatuses.RecheckFailed;
                string message = cleared
                    ? "维护建议复检通过，当前诊断快照已无该问题。"
                    : "维护建议复检未通过，当前诊断快照仍存在该问题。";
                MaintenanceAdviceResolutionRecord record = await MaintenanceAdviceResolutionStore.AppendAsync(
                    adviceForRecord,
                    status,
                    ResolveCurrentOperatorId(),
                    AppConfig.CurrentOperatorRole,
                    notes,
                    message,
                    cancellationToken).ConfigureAwait(false);

                await AuditMaintenanceAdviceActionAsync(
                    record,
                    cleared ? OperationAuditStatus.Succeeded : OperationAuditStatus.Failed,
                    cleared,
                    message).ConfigureAwait(false);
                return BuildMaintenanceAdviceActionResult(true, cleared, record, message);
            }

            return await BuildFailedMaintenanceAdviceActionAsync(
                adviceId,
                "MaintenanceAdviceActionUnsupported",
                $"不支持的维护建议动作: {action}",
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<ShiftTaskActionResult> HandleShiftTaskActionAsync(
            string taskId,
            string linkedAdviceId,
            string action,
            string notes,
            CancellationToken cancellationToken = default)
        {
            FieldDiagnosticsSnapshot snapshot = BuildFieldDiagnosticsSnapshot();
            FieldShiftTask? task = snapshot.ShiftTasks.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(taskId) &&
                 string.Equals(item.TaskId, taskId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(linkedAdviceId) &&
                 string.Equals(item.LinkedAdviceId, linkedAdviceId, StringComparison.OrdinalIgnoreCase)));

            string adviceId = string.IsNullOrWhiteSpace(task?.LinkedAdviceId)
                ? linkedAdviceId ?? string.Empty
                : task.LinkedAdviceId;
            string resolvedTaskId = string.IsNullOrWhiteSpace(task?.TaskId)
                ? taskId ?? string.Empty
                : task.TaskId;
            string normalizedActionForAudit = action ?? string.Empty;

            if (task == null || string.IsNullOrWhiteSpace(adviceId))
            {
                string message = "班次待办已不在当前诊断快照中，或缺少可处理的维护建议标识。";
                var result = BuildFailedShiftTaskActionResult(resolvedTaskId, adviceId, message);
                await AuditShiftTaskActionAsync(
                    result,
                    OperationAuditStatus.Failed,
                    normalizedActionForAudit,
                    message).ConfigureAwait(false);
                return result;
            }

            MaintenanceAdviceActionResult maintenanceResult = await HandleMaintenanceAdviceActionAsync(
                adviceId,
                normalizedActionForAudit,
                notes,
                cancellationToken).ConfigureAwait(false);
            FieldDiagnosticsSnapshot updatedSnapshot = BuildFieldDiagnosticsSnapshot();
            bool taskCleared = !updatedSnapshot.ShiftTasks.Any(item =>
                string.Equals(item.TaskId, resolvedTaskId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.LinkedAdviceId, adviceId, StringComparison.OrdinalIgnoreCase));
            string normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            bool actionSucceeded = maintenanceResult.Succeeded &&
                (!string.Equals(normalizedAction, "recheck", StringComparison.OrdinalIgnoreCase) || maintenanceResult.Cleared);
            var shiftResult = new ShiftTaskActionResult
            {
                Succeeded = maintenanceResult.Succeeded,
                Cleared = taskCleared,
                TaskId = resolvedTaskId,
                LinkedAdviceId = adviceId,
                Status = maintenanceResult.Status,
                Message = maintenanceResult.Message,
                Record = maintenanceResult.Record,
                Tasks = updatedSnapshot.ShiftTasks,
                History = maintenanceResult.History
            };

            await AuditShiftTaskActionAsync(
                shiftResult,
                actionSucceeded ? OperationAuditStatus.Succeeded : OperationAuditStatus.Failed,
                normalizedActionForAudit,
                maintenanceResult.Message).ConfigureAwait(false);
            return shiftResult;
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
                await PlcService.StopMonitoringAsync(cancellationToken).ConfigureAwait(false);
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

        private Task<bool> AuditDiagnosticPackageExportAsync(
            string correlationId,
            OperationAuditStatus status,
            DiagnosticPackageExportSummary summary,
            int startupBlockerCount,
            int maintenanceAdviceCount,
            FieldQueueStatus queueStatus,
            string errorMessage)
        {
            var details = new List<string>
            {
                $"Path={summary.PackagePath}",
                $"SizeBytes={summary.SizeBytes}",
                $"PackageSha256={summary.PackageSha256}",
                $"IndexSha256={summary.IndexSha256}",
                $"IntegrityEntries={summary.IntegrityEntryCount}",
                $"VerifiedEntries={summary.VerifiedEntryCount}",
                $"IntegrityStatus={summary.IntegrityStatus}",
                $"IntegrityFindings={summary.IntegrityFindingCount}",
                $"StartupBlockers={startupBlockerCount}",
                $"MaintenanceAdvice={maintenanceAdviceCount}",
                $"QueueBacklog={queueStatus.BacklogLevel}",
                $"ImageQueue={queueStatus.ImagePending}/{queueStatus.ImageCapacity}",
                $"RecordQueue={queueStatus.RecordPending}/{queueStatus.RecordCapacity}"
            };

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                details.Add($"Error={errorMessage}");
            }

            return OperationAuditService.AppendAsync(new OperationAuditRecord
            {
                CorrelationId = correlationId,
                Operation = "DiagnosticPackageExport",
                Status = status,
                OperatorId = ResolveCurrentOperatorId(),
                Role = AppConfig.CurrentOperatorRole,
                Reason = "导出现场诊断包",
                Details = string.Join("; ", details),
                FailureBlocker = status == OperationAuditStatus.Failed ? "DiagnosticPackageExportFailed" : string.Empty
            });
        }

        private Task<bool> AuditDiagnosticPackageVerificationAsync(
            string correlationId,
            OperationAuditStatus status,
            DiagnosticPackageExportSummary summary,
            string message)
        {
            var details = new List<string>
            {
                $"Path={summary.PackagePath}",
                $"SizeBytes={summary.SizeBytes}",
                $"PackageSha256={summary.PackageSha256}",
                $"IndexSha256={summary.IndexSha256}",
                $"IntegrityEntries={summary.IntegrityEntryCount}",
                $"VerifiedEntries={summary.VerifiedEntryCount}",
                $"IntegrityStatus={summary.IntegrityStatus}",
                $"IntegrityFindings={summary.IntegrityFindingCount}"
            };

            if (!string.IsNullOrWhiteSpace(message))
            {
                details.Add($"Message={message}");
            }

            return OperationAuditService.AppendAsync(new OperationAuditRecord
            {
                CorrelationId = correlationId,
                Operation = "DiagnosticPackageVerify",
                Status = status,
                OperatorId = ResolveCurrentOperatorId(),
                Role = AppConfig.CurrentOperatorRole,
                Reason = "复核现场诊断包完整性",
                Details = string.Join("; ", details),
                FailureBlocker = status == OperationAuditStatus.Failed ? "DiagnosticPackageVerificationFailed" : string.Empty
            });
        }

        private async Task<IReadOnlyList<OperationAuditRecord>> QueryFieldHandoffAuditRecordsAsync(
            CancellationToken cancellationToken)
        {
            OperationAuditQueryResult result = await OperationAuditService.QueryAsync(
                new OperationAuditQuery
                {
                    Limit = 80
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.ErrorMessage);
            }

            return result.Records
                .Where(record =>
                    string.Equals(record.Operation, "DiagnosticPackageExport", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Operation, "DiagnosticPackageVerify", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Operation, "FieldEvidenceRetention", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Operation, "MaintenanceAdviceAction", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Operation, "ShiftTaskAction", StringComparison.OrdinalIgnoreCase))
                .Take(40)
                .ToList();
        }

        private Task<bool> AuditFieldHandoffReportExportAsync(
            string correlationId,
            OperationAuditStatus status,
            FieldHandoffReportSummary summary,
            string errorMessage)
        {
            var details = new List<string>
            {
                $"Path={summary.ReportPath}",
                $"SizeBytes={summary.SizeBytes}",
                $"OverallStatus={summary.OverallStatus}",
                $"ActiveAdvice={summary.ActiveAdviceCount}",
                $"ShiftTasks={summary.ShiftTaskCount}",
                $"FailedRechecks={summary.FailedRecheckCount}",
                $"DiagnosticPackages={summary.DiagnosticPackageCount}",
                $"RecentAudits={summary.RecentAuditCount}",
                $"AuditChainStatus={summary.AuditChainStatus}",
                $"AuditChainVerified={summary.AuditChainVerifiedRecords}",
                $"AuditChainFindings={summary.AuditChainFindingCount}"
            };

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                details.Add($"Error={errorMessage}");
            }

            return OperationAuditService.AppendAsync(new OperationAuditRecord
            {
                CorrelationId = correlationId,
                Operation = "FieldHandoffReportExport",
                Status = status,
                OperatorId = ResolveCurrentOperatorId(),
                Role = AppConfig.CurrentOperatorRole,
                Reason = "导出现场交接报告",
                Details = string.Join("; ", details),
                FailureBlocker = status == OperationAuditStatus.Failed ? "FieldHandoffReportExportFailed" : string.Empty
            });
        }

        private async Task ApplyDiagnosticPackageRetentionAsync(
            string correlationId,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            await ApplyFieldEvidenceRetentionAndAuditAsync(
                correlationId,
                "DiagnosticPackage",
                outputDirectory,
                DiagnosticPackageRetentionPattern,
                DiagnosticPackageRetentionKeepLatest,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ApplyFieldHandoffReportRetentionAsync(
            string correlationId,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            await ApplyFieldEvidenceRetentionAndAuditAsync(
                correlationId,
                "FieldHandoffReport",
                outputDirectory,
                FieldHandoffReportRetentionPattern,
                FieldHandoffReportRetentionKeepLatest,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ApplyFieldEvidenceRetentionAndAuditAsync(
            string correlationId,
            string evidenceType,
            string directory,
            string searchPattern,
            int keepLatest,
            CancellationToken cancellationToken)
        {
            FieldEvidenceRetentionSummary summary;
            try
            {
                summary = ApplyFieldEvidenceRetention(
                    evidenceType,
                    directory,
                    searchPattern,
                    keepLatest,
                    cancellationToken);
                await AuditFieldEvidenceRetentionAsync(
                    correlationId,
                    summary.FailedCount == 0 ? OperationAuditStatus.Succeeded : OperationAuditStatus.Failed,
                    summary,
                    string.Empty).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException ||
                                       ex is UnauthorizedAccessException ||
                                       ex is ArgumentException ||
                                       ex is InvalidOperationException ||
                                       ex is OperationCanceledException)
            {
                summary = new FieldEvidenceRetentionSummary
                {
                    EvidenceType = evidenceType,
                    DirectoryPath = directory ?? string.Empty,
                    SearchPattern = searchPattern ?? string.Empty,
                    KeepLatest = keepLatest,
                    FailedCount = 1,
                    FailedFiles = new[] { ex.Message }
                };
                await AuditFieldEvidenceRetentionAsync(
                    correlationId,
                    OperationAuditStatus.Failed,
                    summary,
                    ex.Message).ConfigureAwait(false);
            }
        }

        internal FieldEvidenceRetentionSummary ApplyFieldEvidenceRetention(
            string evidenceType,
            string directory,
            string searchPattern,
            int keepLatest,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("证据目录为空。", nameof(directory));
            }

            if (string.IsNullOrWhiteSpace(searchPattern))
            {
                throw new ArgumentException("证据文件匹配模式为空。", nameof(searchPattern));
            }

            string fullDirectory = Path.GetFullPath(directory);
            int effectiveKeepLatest = Math.Max(1, keepLatest);
            if (!Directory.Exists(fullDirectory))
            {
                return new FieldEvidenceRetentionSummary
                {
                    EvidenceType = evidenceType ?? string.Empty,
                    DirectoryPath = fullDirectory,
                    SearchPattern = searchPattern,
                    KeepLatest = effectiveKeepLatest
                };
            }

            if (DirectoryPathHasReparsePoint(fullDirectory))
            {
                throw new InvalidOperationException($"现场证据目录不能是链接目录: {fullDirectory}");
            }

            List<FileInfo> files = Directory
                .EnumerateFiles(fullDirectory, searchPattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => IsSafeTopLevelEvidenceFile(fullDirectory, info))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> deletedFiles = new List<string>();
            List<string> failedFiles = new List<string>();
            long freedBytes = 0;

            foreach (FileInfo file in files.Skip(effectiveKeepLatest))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    long length = file.Exists ? file.Length : 0;
                    if (TryDeleteFieldEvidenceFile(file.FullName))
                    {
                        deletedFiles.Add(file.Name);
                        freedBytes += length;
                    }
                    else
                    {
                        failedFiles.Add($"{file.Name}:文件路径不安全或已不存在");
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    failedFiles.Add($"{file.Name}:{ex.Message}");
                }
            }

            return new FieldEvidenceRetentionSummary
            {
                EvidenceType = evidenceType ?? string.Empty,
                DirectoryPath = fullDirectory,
                SearchPattern = searchPattern,
                KeepLatest = effectiveKeepLatest,
                BeforeCount = files.Count,
                DeletedCount = deletedFiles.Count,
                FailedCount = failedFiles.Count,
                FreedBytes = freedBytes,
                DeletedFiles = deletedFiles,
                FailedFiles = failedFiles
            };
        }

        private Task<bool> AuditFieldEvidenceRetentionAsync(
            string correlationId,
            OperationAuditStatus status,
            FieldEvidenceRetentionSummary summary,
            string errorMessage)
        {
            var details = new List<string>
            {
                $"EvidenceType={summary.EvidenceType}",
                $"Directory={summary.DirectoryPath}",
                $"Pattern={summary.SearchPattern}",
                $"KeepLatest={summary.KeepLatest}",
                $"BeforeCount={summary.BeforeCount}",
                $"DeletedCount={summary.DeletedCount}",
                $"FailedCount={summary.FailedCount}",
                $"FreedBytes={summary.FreedBytes}"
            };

            if (summary.DeletedFiles.Count > 0)
            {
                details.Add($"Deleted={string.Join(",", summary.DeletedFiles.Take(8))}");
            }

            if (summary.FailedFiles.Count > 0)
            {
                details.Add($"Failed={string.Join(",", summary.FailedFiles.Take(3))}");
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                details.Add($"Error={errorMessage}");
            }

            return OperationAuditService.AppendAsync(new OperationAuditRecord
            {
                CorrelationId = correlationId,
                Operation = "FieldEvidenceRetention",
                Status = status,
                OperatorId = ResolveCurrentOperatorId(),
                Role = AppConfig.CurrentOperatorRole,
                Reason = "现场证据保留策略清理",
                Details = string.Join("; ", details),
                FailureBlocker = status == OperationAuditStatus.Failed ? "FieldEvidenceRetentionFailed" : string.Empty
            });
        }

        private FieldDiagnosticsSnapshot EnrichMaintenanceAdviceSnapshot(FieldDiagnosticsSnapshot snapshot)
        {
            IReadOnlyList<MaintenanceAdviceResolutionRecord> history =
                MaintenanceAdviceResolutionStore.QueryRecent(12);
            IReadOnlyDictionary<string, DateTimeOffset> firstSeenByAdviceId =
                MaintenanceAdviceResolutionStore.CaptureFirstSeenTimes(snapshot.MaintenanceAdvice);
            IReadOnlyDictionary<string, MaintenanceAdviceResolutionRecord> latestByAdviceId = history
                .GroupBy(record => record.AdviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(record => record.ActionAt).First(),
                    StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<FieldMaintenanceAdvice> advice = snapshot.MaintenanceAdvice
                .Select(item => EnrichMaintenanceAdvice(item, latestByAdviceId, firstSeenByAdviceId))
                .ToList();
            IReadOnlyList<FieldShiftTask> shiftTasks = FieldShiftTaskBuilder.Build(advice, history);

            return new FieldDiagnosticsSnapshot
            {
                UpdatedAt = snapshot.UpdatedAt,
                OverallLevel = snapshot.OverallLevel,
                CameraStatus = snapshot.CameraStatus,
                PlcStatus = snapshot.PlcStatus,
                CurrentModelName = snapshot.CurrentModelName,
                ModelStatus = snapshot.ModelStatus,
                StorageStatus = snapshot.StorageStatus,
                DatabaseStatus = snapshot.DatabaseStatus,
                RecipeId = snapshot.RecipeId,
                RecipeVersion = snapshot.RecipeVersion,
                RecipeTargetLabel = snapshot.RecipeTargetLabel,
                RecipeTargetCount = snapshot.RecipeTargetCount,
                LastInspectionId = snapshot.LastInspectionId,
                LastInspectionTotalMs = snapshot.LastInspectionTotalMs,
                RecentInspectionP95Ms = snapshot.RecentInspectionP95Ms,
                RecentInspectionP99Ms = snapshot.RecentInspectionP99Ms,
                ImageQueueLength = snapshot.ImageQueueLength,
                ImageQueueCapacity = snapshot.ImageQueueCapacity,
                RecordQueueLength = snapshot.RecordQueueLength,
                RecordQueueCapacity = snapshot.RecordQueueCapacity,
                FreeDiskGb = snapshot.FreeDiskGb,
                MemoryMb = snapshot.MemoryMb,
                HealthSnapshot = snapshot.HealthSnapshot,
                StartupDiagnostics = snapshot.StartupDiagnostics,
                Queues = snapshot.Queues,
                ModelProbe = snapshot.ModelProbe,
                AuditChain = snapshot.AuditChain,
                Components = snapshot.Components,
                MaintenanceAdvice = advice,
                MaintenanceAdviceHistory = history,
                ShiftTasks = shiftTasks,
                RecentInspectionTimings = snapshot.RecentInspectionTimings,
                RecentErrors = snapshot.RecentErrors
            };
        }

        private static FieldMaintenanceAdvice EnrichMaintenanceAdvice(
            FieldMaintenanceAdvice advice,
            IReadOnlyDictionary<string, MaintenanceAdviceResolutionRecord> latestByAdviceId,
            IReadOnlyDictionary<string, DateTimeOffset> firstSeenByAdviceId)
        {
            firstSeenByAdviceId.TryGetValue(advice.AdviceId, out DateTimeOffset firstSeenAt);
            latestByAdviceId.TryGetValue(advice.AdviceId, out MaintenanceAdviceResolutionRecord? record);

            return new FieldMaintenanceAdvice
            {
                AdviceId = advice.AdviceId,
                Source = advice.Source,
                Level = advice.Level,
                Title = advice.Title,
                Evidence = advice.Evidence,
                Advice = advice.Advice,
                Code = advice.Code,
                ResolutionStatus = record?.Status ?? advice.ResolutionStatus,
                FirstSeenAt = firstSeenAt == default ? advice.FirstSeenAt : firstSeenAt,
                LastActionAt = record?.ActionAt ?? advice.LastActionAt,
                LastActionBy = record?.OperatorId ?? advice.LastActionBy,
                LastActionMessage = record?.Message ?? advice.LastActionMessage
            };
        }

        private async Task<MaintenanceAdviceActionResult> BuildFailedMaintenanceAdviceActionAsync(
            string adviceId,
            string errorCode,
            string message,
            CancellationToken cancellationToken)
        {
            var record = new MaintenanceAdviceResolutionRecord
            {
                AdviceId = adviceId ?? string.Empty,
                Code = errorCode,
                Source = "MaintenanceAdvice",
                Title = "维护建议动作失败",
                Status = "Failed",
                OperatorId = ResolveCurrentOperatorId(),
                Role = AppConfig.CurrentOperatorRole,
                Message = message,
                ActionAt = DateTimeOffset.Now
            };

            await AuditMaintenanceAdviceActionAsync(
                record,
                OperationAuditStatus.Failed,
                false,
                message).ConfigureAwait(false);
            return new MaintenanceAdviceActionResult
            {
                Succeeded = false,
                Cleared = false,
                AdviceId = adviceId ?? string.Empty,
                Status = "Failed",
                Message = message,
                Record = record,
                History = MaintenanceAdviceResolutionStore.QueryRecent(12)
            };
        }

        private MaintenanceAdviceActionResult BuildMaintenanceAdviceActionResult(
            bool succeeded,
            bool cleared,
            MaintenanceAdviceResolutionRecord record,
            string message)
        {
            return new MaintenanceAdviceActionResult
            {
                Succeeded = succeeded,
                Cleared = cleared,
                AdviceId = record.AdviceId,
                Status = record.Status,
                Message = message,
                Record = record,
                History = MaintenanceAdviceResolutionStore.QueryRecent(12)
            };
        }

        private ShiftTaskActionResult BuildFailedShiftTaskActionResult(
            string taskId,
            string linkedAdviceId,
            string message)
        {
            return new ShiftTaskActionResult
            {
                Succeeded = false,
                Cleared = false,
                TaskId = taskId ?? string.Empty,
                LinkedAdviceId = linkedAdviceId ?? string.Empty,
                Status = "Failed",
                Message = message,
                Tasks = BuildFieldDiagnosticsSnapshot().ShiftTasks,
                History = MaintenanceAdviceResolutionStore.QueryRecent(12)
            };
        }

        private Task<bool> AuditMaintenanceAdviceActionAsync(
            MaintenanceAdviceResolutionRecord record,
            OperationAuditStatus status,
            bool cleared,
            string message)
        {
            return OperationAuditService.AppendAsync(new OperationAuditRecord
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                Operation = "MaintenanceAdviceAction",
                Status = status,
                OperatorId = ResolveCurrentOperatorId(),
                Role = AppConfig.CurrentOperatorRole,
                Reason = "维护建议处理/复检",
                Details = string.Join(
                    "; ",
                    new[]
                    {
                        $"AdviceId={record.AdviceId}",
                        $"Code={record.Code}",
                        $"Source={record.Source}",
                        $"AdviceStatus={record.Status}",
                        $"Cleared={cleared}",
                        $"Message={message}"
                    }),
                FailureBlocker = status == OperationAuditStatus.Failed ? "MaintenanceAdviceRecheckFailed" : string.Empty
            });
        }

        private Task<bool> AuditShiftTaskActionAsync(
            ShiftTaskActionResult result,
            OperationAuditStatus status,
            string action,
            string message)
        {
            return OperationAuditService.AppendAsync(new OperationAuditRecord
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                Operation = "ShiftTaskAction",
                Status = status,
                OperatorId = ResolveCurrentOperatorId(),
                Role = AppConfig.CurrentOperatorRole,
                Reason = "班次待办处理/复检",
                Details = string.Join(
                    "; ",
                    new[]
                    {
                        $"TaskId={result.TaskId}",
                        $"LinkedAdviceId={result.LinkedAdviceId}",
                        $"Action={action}",
                        $"TaskStatus={result.Status}",
                        $"Cleared={result.Cleared}",
                        $"Message={message}"
                    }),
                FailureBlocker = status == OperationAuditStatus.Failed ? "ShiftTaskActionFailed" : string.Empty
            });
        }

        private static FieldMaintenanceAdvice ExistingRecordToAdvice(MaintenanceAdviceResolutionRecord? record)
        {
            if (record == null)
            {
                return new FieldMaintenanceAdvice();
            }

            return new FieldMaintenanceAdvice
            {
                AdviceId = record.AdviceId,
                Source = record.Source,
                Level = "warning",
                Title = record.Title,
                Evidence = record.Evidence,
                Advice = record.Advice,
                Code = record.Code,
                ResolutionStatus = record.Status,
                FirstSeenAt = record.ActionAt,
                LastActionAt = record.ActionAt,
                LastActionBy = record.OperatorId,
                LastActionMessage = record.Message
            };
        }

        private string ResolveCurrentOperatorId()
        {
            if (!string.IsNullOrWhiteSpace(AppConfig.CurrentOperatorId))
            {
                return AppConfig.CurrentOperatorId.Trim();
            }

            return string.IsNullOrWhiteSpace(Environment.UserName)
                ? "unknown"
                : Environment.UserName;
        }

        private static int CountStartupBlockers(StartupDiagnosticReport? report)
        {
            return report?.Items?.Count(item => item.Status == StartupDiagnosticStatus.Fail && item.IsBlocking) ?? 0;
        }

        private static DiagnosticPackageExportSummary BuildDiagnosticPackageExportSummary(
            string packagePath,
            DiagnosticPackageIntegrityVerificationResult verification)
        {
            FileInfo info = new FileInfo(packagePath);
            return new DiagnosticPackageExportSummary
            {
                PackagePath = packagePath,
                FileName = Path.GetFileName(packagePath),
                SizeBytes = info.Exists ? info.Length : 0,
                PackageSha256 = verification.PackageSha256,
                IndexSha256 = verification.IndexSha256,
                IntegrityEntryCount = verification.IndexEntryCount,
                VerifiedEntryCount = verification.VerifiedEntryCount,
                IntegrityFindingCount = verification.Findings.Count,
                IntegrityStatus = verification.Status,
                ExportedAt = info.Exists ? new DateTimeOffset(info.LastWriteTime) : DateTimeOffset.Now,
                VerifiedAt = DateTimeOffset.Now
            };
        }

        private static FieldHandoffReportHistoryItem? BuildFieldHandoffReportHistoryItem(string directory, FileInfo info)
        {
            if (!TryReadFieldHandoffReportHeader(directory, info, out IReadOnlyList<string> headerLines))
            {
                return null;
            }

            info.Refresh();
            if (!IsSafeFieldHandoffReportFileForRead(directory, info))
            {
                return null;
            }

            DateTimeOffset lastWriteTime = new DateTimeOffset(info.LastWriteTime);
            string overallStatus = "Pending";
            DateTimeOffset generatedAt = lastWriteTime;
            int shiftTaskCount = 0;

            foreach (string line in headerLines)
            {
                if (line.StartsWith("- 交接结论:", StringComparison.Ordinal))
                {
                    overallStatus = line["- 交接结论:".Length..].Trim();
                }
                else if (line.StartsWith("- 生成时间:", StringComparison.Ordinal) &&
                         DateTimeOffset.TryParse(line["- 生成时间:".Length..].Trim(), out DateTimeOffset parsed))
                {
                    generatedAt = parsed;
                }
                else if (line.StartsWith("- 班次待办:", StringComparison.Ordinal) &&
                         int.TryParse(line["- 班次待办:".Length..].Trim(), out int parsedCount))
                {
                    shiftTaskCount = parsedCount;
                }
            }

            return new FieldHandoffReportHistoryItem
            {
                ReportPath = info.FullName,
                FileName = info.Name,
                SizeBytes = info.Length,
                LastWriteTime = lastWriteTime,
                GeneratedAt = generatedAt,
                OverallStatus = string.IsNullOrWhiteSpace(overallStatus) ? "Pending" : overallStatus,
                ShiftTaskCount = shiftTaskCount
            };
        }

        internal static bool TryReadFieldHandoffReportHeader(
            string directory,
            FileInfo file,
            out IReadOnlyList<string> headerLines)
        {
            headerLines = Array.Empty<string>();
            if (!IsSafeFieldHandoffReportFileForRead(directory, file))
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(file.FullName);
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);

                if (!IsSafeFieldHandoffReportFileForRead(directory, new FileInfo(fullPath)))
                {
                    return false;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = new List<string>();
                while (lines.Count < 12 && !reader.EndOfStream)
                {
                    string? line = reader.ReadLine();
                    if (line != null)
                    {
                        lines.Add(line);
                    }
                }

                if (!IsSafeFieldHandoffReportFileForRead(directory, new FileInfo(fullPath)))
                {
                    return false;
                }

                headerLines = lines;
                return true;
            }
            catch (Exception ex) when (IsRecoverableEvidencePathException(ex))
            {
                Debug.WriteLine($"[AppRuntime] 解析现场交接报告摘要失败: {ex.Message}");
                return false;
            }
        }

        private static string BuildDiagnosticPackageVerificationFindingSummary(
            DiagnosticPackageIntegrityVerificationResult verification)
        {
            if (verification.Findings.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ", ",
                verification.Findings
                    .Take(5)
                    .Select(finding => string.IsNullOrWhiteSpace(finding.EntryName)
                        ? finding.ErrorCode
                        : $"{finding.ErrorCode}:{finding.EntryName}"));
        }

        private static string BuildDiagnosticPackageVerificationFailureMessage(
            DiagnosticPackageIntegrityVerificationResult verification)
        {
            string findings = string.Join(
                "; ",
                verification.Findings
                    .Take(3)
                    .Select(finding => string.IsNullOrWhiteSpace(finding.EntryName)
                        ? finding.ErrorCode
                        : $"{finding.ErrorCode}:{finding.EntryName}"));
            return $"诊断包导出后完整性自检失败: Status={verification.Status}, Findings={verification.Findings.Count}, {findings}";
        }

        internal static bool TryDeleteFieldEvidenceFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }

                string fullPath = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) || DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                if (!file.Exists || HasReparsePoint(file))
                {
                    return false;
                }

                file.Delete();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 删除现场证据文件失败: {ex.Message}");
                return false;
            }
        }

        private static string ResolveDiagnosticOutputDirectory(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("诊断包目录为空。", nameof(outputDirectory));
            }

            return Path.GetFullPath(outputDirectory);
        }

        private static string ResolveHandoffReportOutputDirectory(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("交接报告目录为空。", nameof(outputDirectory));
            }

            return Path.GetFullPath(outputDirectory);
        }

        private static string ResolveDiagnosticPackagePath(string outputDirectory, string packagePath)
        {
            string directory = ResolveDiagnosticOutputDirectory(outputDirectory);
            if (Directory.Exists(directory) && DirectoryPathHasReparsePoint(directory))
            {
                throw new UnauthorizedAccessException("不能复核链接诊断包目录。");
            }

            if (string.IsNullOrWhiteSpace(packagePath))
            {
                throw new ArgumentException("诊断包路径为空。", nameof(packagePath));
            }

            string candidate = Path.IsPathRooted(packagePath)
                ? Path.GetFullPath(packagePath)
                : Path.GetFullPath(Path.Combine(directory, packagePath));
            if (!IsSameOrChildPath(candidate, directory))
            {
                throw new UnauthorizedAccessException("只能复核诊断包目录内的文件。");
            }

            if (!string.Equals(Path.GetExtension(candidate), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("只能复核 .zip 诊断包。");
            }

            string candidateDirectory = Path.GetDirectoryName(candidate) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(candidateDirectory) &&
                Directory.Exists(candidateDirectory) &&
                DirectoryPathHasReparsePoint(candidateDirectory))
            {
                throw new UnauthorizedAccessException("不能复核链接诊断包目录。");
            }

            if (File.Exists(candidate) && HasReparsePoint(new FileInfo(candidate)))
            {
                throw new UnauthorizedAccessException("不能复核链接诊断包文件。");
            }

            return candidate;
        }

        private static void EnsureEvidenceDirectorySafeForWrite(string directory, string displayName)
        {
            EnsureExistingDirectoryAncestorsHaveNoReparsePoint(directory, displayName);
            Directory.CreateDirectory(directory);
            if (DirectoryPathHasReparsePoint(directory))
            {
                throw new InvalidOperationException($"{displayName}不能是链接目录: {directory}");
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null)
            {
                current.Refresh();
                if (current.Exists && HasReparsePoint(current))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private static void EnsureExistingDirectoryAncestorsHaveNoReparsePoint(string directory, string displayName)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null && !current.Exists)
            {
                current = current.Parent;
            }

            while (current != null)
            {
                current.Refresh();
                if (current.Exists && HasReparsePoint(current))
                {
                    throw new InvalidOperationException($"{displayName}不能包含链接目录: {current.FullName}");
                }

                current = current.Parent;
            }
        }

        private static bool IsSafeTopLevelEvidenceFile(string directory, FileInfo file)
        {
            try
            {
                string fullDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(fullDirectory) ||
                    !Directory.Exists(fullDirectory) ||
                    DirectoryPathHasReparsePoint(fullDirectory))
                {
                    return false;
                }

                string fullPath = Path.GetFullPath(file.FullName);
                string fileDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (!string.Equals(
                        Path.GetFullPath(fileDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        fullDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                file.Refresh();
                return file.Exists && !HasReparsePoint(file);
            }
            catch (Exception ex) when (IsRecoverableEvidencePathException(ex))
            {
                return false;
            }
        }

        private static bool IsSafeFieldHandoffReportFileForRead(string directory, FileInfo file)
        {
            return IsSafeTopLevelEvidenceFile(directory, file) &&
                file.Name.StartsWith("handoff-", StringComparison.OrdinalIgnoreCase) &&
                file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameOrChildPath(string candidatePath, string rootPath)
        {
            string candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool IsRecoverableEvidencePathException(Exception ex)
        {
            return ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException;
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

            if (!TryLoadLegacyReplayApprovalManifest(
                    entry,
                    out ModelPackageManifest manifest,
                    out string manifestSkipReason))
            {
                Debug.WriteLine($"[AppRuntime] Legacy Replay approval migration skipped: {manifestSkipReason}");
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

        private static bool TryLoadLegacyReplayApprovalManifest(
            ModelRegistryEntry entry,
            out ModelPackageManifest manifest,
            out string skipReason)
        {
            manifest = new ModelPackageManifest();
            skipReason = string.Empty;

            if (!TryReadLegacyReplayApprovalManifest(
                    entry,
                    out manifest,
                    out string packageDirectory,
                    out skipReason))
            {
                return false;
            }

            if (!string.Equals(manifest.ModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Version, entry.Version, StringComparison.OrdinalIgnoreCase))
            {
                skipReason = "candidate manifest identity no longer matches the registry entry.";
                return false;
            }

            string expectedHash = manifest.EffectiveHash;
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !string.Equals(expectedHash, entry.ModelHash, StringComparison.OrdinalIgnoreCase))
            {
                skipReason = "candidate manifest hash metadata no longer matches the registry entry.";
                return false;
            }

            if (!ModelContractMatchesRegistry(entry, manifest))
            {
                skipReason = $"candidate manifest model contract no longer matches the registry entry: {DescribeModelContractMismatch(entry, manifest)}";
                return false;
            }

            string modelFileName = string.IsNullOrWhiteSpace(manifest.ModelFileName)
                ? "model.onnx"
                : manifest.ModelFileName.Trim();
            if (!ModelPackagePathGuard.TryResolveModelPath(
                    packageDirectory,
                    modelFileName,
                    out string declaredModelPath,
                    out string pathError,
                    "manifest ModelFileName"))
            {
                skipReason = pathError;
                return false;
            }

            if (!string.Equals(declaredModelPath, ModelPackagePathGuard.GetFullPathSafe(entry.ModelPath), StringComparison.OrdinalIgnoreCase))
            {
                skipReason = "candidate model path no longer matches manifest ModelFileName.";
                return false;
            }

            if (ModelPackagePathGuard.ModelPathHasReparsePoint(packageDirectory, declaredModelPath))
            {
                skipReason = "candidate model file path contains a reparse point.";
                return false;
            }

            return true;
        }

        private static bool TryReadLegacyReplayApprovalManifest(
            ModelRegistryEntry entry,
            out ModelPackageManifest manifest,
            out string packageDirectory,
            out string skipReason)
        {
            manifest = new ModelPackageManifest();
            packageDirectory = string.Empty;
            skipReason = string.Empty;

            if (!TryResolveLegacyReplayApprovalAssetPaths(
                    entry,
                    out string manifestPath,
                    out string modelPath,
                    out packageDirectory,
                    out skipReason))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    manifestPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);

                if (!AreLegacyReplayApprovalAssetPathsSafe(
                        manifestPath,
                        modelPath,
                        packageDirectory,
                        out skipReason))
                {
                    return false;
                }

                manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                    stream,
                    ReplayJson.Options) ?? new ModelPackageManifest();

                return AreLegacyReplayApprovalAssetPathsSafe(
                    manifestPath,
                    modelPath,
                    packageDirectory,
                    out skipReason);
            }
            catch (Exception ex) when (IsRecoverableEvidencePathException(ex) || ex is JsonException)
            {
                skipReason = $"candidate manifest parse failed: {ex.Message}";
                return false;
            }
        }

        private static bool TryResolveLegacyReplayApprovalAssetPaths(
            ModelRegistryEntry entry,
            out string manifestPath,
            out string modelPath,
            out string packageDirectory,
            out string skipReason)
        {
            manifestPath = string.Empty;
            modelPath = string.Empty;
            packageDirectory = string.Empty;
            skipReason = string.Empty;

            if (string.IsNullOrWhiteSpace(entry.ManifestPath) ||
                string.IsNullOrWhiteSpace(entry.ModelPath))
            {
                skipReason = "candidate manifest/model file is missing.";
                return false;
            }

            try
            {
                manifestPath = Path.GetFullPath(entry.ManifestPath);
                modelPath = Path.GetFullPath(entry.ModelPath);
                packageDirectory = Path.GetDirectoryName(manifestPath) ?? string.Empty;
            }
            catch (Exception ex) when (IsRecoverableEvidencePathException(ex))
            {
                skipReason = $"candidate manifest/model path is invalid: {ex.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                skipReason = "candidate package directory is invalid.";
                return false;
            }

            return AreLegacyReplayApprovalAssetPathsSafe(
                manifestPath,
                modelPath,
                packageDirectory,
                out skipReason);
        }

        private static bool AreLegacyReplayApprovalAssetPathsSafe(
            string manifestPath,
            string modelPath,
            string packageDirectory,
            out string skipReason)
        {
            skipReason = string.Empty;

            if (!File.Exists(manifestPath) || !File.Exists(modelPath))
            {
                skipReason = "candidate manifest/model file is missing.";
                return false;
            }

            if (!string.Equals(Path.GetFileName(manifestPath), "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                skipReason = "candidate manifest file name is invalid.";
                return false;
            }

            if (DirectoryPathHasReparsePoint(packageDirectory))
            {
                skipReason = "candidate package directory is a reparse point.";
                return false;
            }

            if (HasReparsePoint(new FileInfo(manifestPath)))
            {
                skipReason = "candidate manifest file is a reparse point.";
                return false;
            }

            if (HasReparsePoint(new FileInfo(modelPath)))
            {
                skipReason = "candidate model file is a reparse point.";
                return false;
            }

            if (!IsSameOrChildPath(modelPath, packageDirectory))
            {
                skipReason = "candidate model file is outside the package directory.";
                return false;
            }

            return true;
        }

        private static bool ModelContractMatchesRegistry(ModelRegistryEntry entry, ModelPackageManifest manifest)
        {
            IReadOnlyList<string> manifestLabels = manifest.Labels != null
                ? manifest.Labels
                : Array.Empty<string>();
            IReadOnlyList<string> entryLabels = ResolveEffectiveEntryLabels(entry);
            int entryInputWidth = ResolveEffectiveEntryInputWidth(entry);
            int entryInputHeight = ResolveEffectiveEntryInputHeight(entry);
            string entryTaskType = ResolveEffectiveEntryTaskType(entry);
            string entryPostprocessorKey = ResolveEffectiveEntryPostprocessorKey(entry);
            string entryScoreNormalization = ResolveEffectiveEntryScoreNormalization(entry);
            IReadOnlyDictionary<string, string>? entryPostprocessOptions = ResolveEffectiveEntryPostprocessOptions(entry);
            return manifest.InputWidth == entryInputWidth &&
                   manifest.InputHeight == entryInputHeight &&
                   string.Equals(manifest.TaskType ?? string.Empty, entryTaskType, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(manifest.PostprocessorKey ?? string.Empty, entryPostprocessorKey, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(manifest.ScoreNormalization ?? string.Empty, entryScoreNormalization, StringComparison.OrdinalIgnoreCase) &&
                   DictionaryMatches(manifest.PostprocessOptions, entryPostprocessOptions) &&
                   manifestLabels.Count == entryLabels.Count &&
                   manifestLabels.Zip(entryLabels, (left, right) => string.Equals(left, right, StringComparison.Ordinal)).All(match => match);
        }

        private static string DescribeModelContractMismatch(ModelRegistryEntry entry, ModelPackageManifest manifest)
        {
            var mismatches = new List<string>();
            int entryInputWidth = ResolveEffectiveEntryInputWidth(entry);
            int entryInputHeight = ResolveEffectiveEntryInputHeight(entry);
            if (manifest.InputWidth != entryInputWidth || manifest.InputHeight != entryInputHeight)
            {
                mismatches.Add($"InputSize manifest={manifest.InputWidth}x{manifest.InputHeight}, registry={entryInputWidth}x{entryInputHeight}");
            }

            AddStringMismatch(mismatches, "TaskType", manifest.TaskType, ResolveEffectiveEntryTaskType(entry));
            AddStringMismatch(mismatches, "PostprocessorKey", manifest.PostprocessorKey, ResolveEffectiveEntryPostprocessorKey(entry));
            AddStringMismatch(mismatches, "ScoreNormalization", manifest.ScoreNormalization, ResolveEffectiveEntryScoreNormalization(entry));

            IReadOnlyDictionary<string, string> manifestOptions = NormalizeDictionary(manifest.PostprocessOptions);
            IReadOnlyDictionary<string, string> entryOptions = NormalizeDictionary(ResolveEffectiveEntryPostprocessOptions(entry));
            if (!DictionaryMatches(manifestOptions, entryOptions))
            {
                mismatches.Add(DescribeDictionaryMismatch("PostprocessOptions", manifestOptions, entryOptions));
            }

            IReadOnlyList<string> manifestLabels = manifest.Labels != null
                ? manifest.Labels
                : Array.Empty<string>();
            IReadOnlyList<string> entryLabels = ResolveEffectiveEntryLabels(entry);
            if (manifestLabels.Count != entryLabels.Count)
            {
                mismatches.Add($"Labels count manifest={manifestLabels.Count}, registry={entryLabels.Count}");
            }
            else
            {
                for (int index = 0; index < manifestLabels.Count; index++)
                {
                    if (!string.Equals(manifestLabels[index], entryLabels[index], StringComparison.Ordinal))
                    {
                        mismatches.Add($"Labels[{index}] manifest={manifestLabels[index]}, registry={entryLabels[index]}");
                        break;
                    }
                }
            }

            return mismatches.Count == 0
                ? "unknown contract field"
                : string.Join("; ", mismatches);
        }

        private static string ResolveEffectiveEntryTaskType(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveTaskType();
        }

        private static string ResolveEffectiveEntryPostprocessorKey(ModelRegistryEntry entry)
        {
            return entry.GetEffectivePostprocessorKey();
        }

        private static string ResolveEffectiveEntryScoreNormalization(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveScoreNormalization();
        }

        private static int ResolveEffectiveEntryInputWidth(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveInputWidth();
        }

        private static int ResolveEffectiveEntryInputHeight(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveInputHeight();
        }

        private static IReadOnlyList<string> ResolveEffectiveEntryLabels(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveLabels();
        }

        private static IReadOnlyDictionary<string, string>? ResolveEffectiveEntryPostprocessOptions(ModelRegistryEntry entry)
        {
            return entry.GetEffectivePostprocessOptions();
        }

        private static void AddStringMismatch(List<string> mismatches, string fieldName, string? manifestValue, string? entryValue)
        {
            string left = manifestValue ?? string.Empty;
            string right = entryValue ?? string.Empty;
            if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{fieldName} manifest={left}, registry={right}");
            }
        }

        private static string DescribeDictionaryMismatch(
            string fieldName,
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
        {
            foreach (KeyValuePair<string, string> pair in left)
            {
                if (!right.TryGetValue(pair.Key, out string? rightValue))
                {
                    return $"{fieldName} missing registry key={pair.Key}";
                }

                if (!string.Equals(pair.Value ?? string.Empty, rightValue ?? string.Empty, StringComparison.Ordinal))
                {
                    return $"{fieldName}[{pair.Key}] manifest={pair.Value ?? string.Empty}, registry={rightValue ?? string.Empty}";
                }
            }

            foreach (KeyValuePair<string, string> pair in right)
            {
                if (!left.ContainsKey(pair.Key))
                {
                    return $"{fieldName} unexpected registry key={pair.Key}";
                }
            }

            return $"{fieldName} differs";
        }

        private static bool DictionaryMatches(
            IReadOnlyDictionary<string, string>? left,
            IReadOnlyDictionary<string, string>? right)
        {
            IReadOnlyDictionary<string, string> normalizedLeft = NormalizeDictionary(left);
            IReadOnlyDictionary<string, string> normalizedRight = NormalizeDictionary(right);
            if (normalizedLeft.Count != normalizedRight.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> pair in normalizedLeft)
            {
                if (!normalizedRight.TryGetValue(pair.Key, out string? rightValue) ||
                    !string.Equals(pair.Value ?? string.Empty, rightValue ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyDictionary<string, string> NormalizeDictionary(IReadOnlyDictionary<string, string>? value)
        {
            if (value == null || value.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in value)
            {
                string key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || normalized.ContainsKey(key))
                {
                    continue;
                }

                normalized[key] = pair.Value ?? string.Empty;
            }

            return normalized;
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
            string onnxDirectory = Path.IsPathRooted(appConfig.OnnxModelDirectory)
                ? appConfig.OnnxModelDirectory
                : Path.Combine(baseDirectory, appConfig.OnnxModelDirectory);

            return new ModelRegistryScanOptions
            {
                PackageDirectory = packageDirectory,
                OnnxDirectory = onnxDirectory,
                StrictPackageMode = appConfig.StrictModelPackageMode,
                RequireProductionApproval = appConfig.RequireApprovedModelsForProduction
            };
        }
    }
}
