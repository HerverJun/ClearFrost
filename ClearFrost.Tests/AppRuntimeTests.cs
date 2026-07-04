using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Models;
using ClearFrost.Services;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests
{
#pragma warning disable CS0067
    [Collection(TestCollections.SqliteGlobalPool)]
    public class AppRuntimeTests
    {
        [Fact]
        public async Task DisposeAsync_会先排空后台任务再释放依赖()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig { StoragePath = tempDir };
                using var cameraManager = new CameraManager(true);
                var cameraService = new FakeCameraService(order);
                var plcService = new FakePlcService(order);
                var detectionService = new FakeDetectionService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var statisticsService = new FakeStatisticsService(order);
                var databaseService = new FakeDatabaseService(order);
                using var imageSaveQueue = new ImageSaveQueue();
                using var detectionRecordQueue = new DetectionRecordQueue(databaseService);
                var webUiController = new WebUIController();

                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    cameraService,
                    plcService,
                    detectionService,
                    storageService,
                    statisticsService,
                    databaseService,
                    imageSaveQueue,
                    detectionRecordQueue,
                    webUiController);

                string imagePath = Path.Combine(tempDir, "queued", "frame.jpg");
                using (var mat = new Mat(4, 4, MatType.CV_8UC1, Scalar.All(180)))
                {
                    runtime.ImageSaveQueue.Enqueue(mat, imagePath).Should().BeTrue();
                }

                runtime.DetectionRecordQueue.Enqueue(new DetectionPersistencePayload
                {
                    Timestamp = DateTime.Now,
                    IsQualified = true,
                    ModelName = "queued-model",
                    ActualCount = 1
                }).Should().BeTrue();

                await runtime.DisposeAsync();

                File.Exists(imagePath).Should().BeTrue();
                order.Should().ContainInOrder(
                    "plc-stop-monitoring",
                    "camera-stop-capture",
                    "camera-close",
                    "statistics-save-all",
                    "db-save",
                    "detection-dispose",
                    "statistics-dispose",
                    "db-dispose",
                    "plc-dispose",
                    "camera-dispose",
                    "storage-dispose");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_默认检测服务使用配置GpuIndex()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var appConfig = new AppConfig
            {
                StoragePath = tempDir,
                EnableGpu = true,
                GpuIndex = 2
            };

            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                cameraService: null,
                plcService: null,
                detectionService: null,
                storageService: null,
                statisticsService: null,
                databaseService: null,
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: null);

            try
            {
                runtime.DetectionService.RuntimeStatus.GpuRequested.Should().BeTrue();
                runtime.DetectionService.RuntimeStatus.GpuDeviceId.Should().Be(2);
            }
            finally
            {
                await runtime.DisposeAsync();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_统一创建Replay闭环服务和生产Gate()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    RequireApprovedModelsForProduction = true
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    new FakeStorageService(tempDir, order),
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                runtime.DecisionEvaluator.Should().NotBeNull();
                runtime.ReplayPolicy.Should().NotBeNull();
                runtime.ManualReviewStore.Should().NotBeNull();
                runtime.ReplayDatasetStore.Should().NotBeNull();
                runtime.ReplayRunStore.Should().NotBeNull();
                runtime.ModelApprovalEvidenceStore.Should().NotBeNull();
                runtime.ReplayProductionGate.Should().NotBeNull();
                runtime.ReplayCoordinator.Should().NotBeNull();
                runtime.ReplayAssetCoordinator.Should().NotBeNull();
                runtime.ReplayDatasetLifecycleService.Should().NotBeNull();
                runtime.ReplayIntegrityScanner.Should().NotBeNull();
                runtime.ReplayApplicationService.Should().NotBeNull();
                runtime.ReplayApprovalApplicationService.Should().NotBeNull();
                runtime.StartupDiagnostics.CurrentReport.Items.Should().Contain(item =>
                    item.Name == "Replay evidence gate" &&
                    item.Status == StartupDiagnosticStatus.Fail &&
                    item.IsBlocking &&
                    item.Message.Contains("Primary model reference is empty", StringComparison.OrdinalIgnoreCase));

                await runtime.DisposeAsync();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_为当前Primary批准包执行一次性Legacy迁移()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string packageRoot = Path.Combine(tempDir, "models");
                string modelPath = CreateApprovedPackage(packageRoot, "legacy-current", "1");
                string modelHash = ComputeSha256(modelPath);
                var reference = new ProductionModelReference
                {
                    Type = ProductionModelReferenceType.ApprovedPackage,
                    ModelId = "legacy-current",
                    Version = "1",
                    Sha256 = modelHash
                };
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    ModelPackageDirectory = packageRoot,
                    RequireApprovedModelsForProduction = true,
                    CurrentModelReference = reference,
                    CurrentModelFileName = Path.GetFileName(modelPath)
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    new FakeStorageService(tempDir, order),
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                ModelRegistryEntry entry = runtime.ModelRegistry.Resolve(modelPath)!;
                entry.Manifest!.Approval.LegacyMigration.Should().NotBeNull();
                entry.Manifest.Approval.LegacyMigration!.ModelRole.Should().Be("Primary");
                entry.Manifest.Approval.LegacyMigration.ConfigReference.Should().Be(reference.ToSelectionValue());
                entry.Manifest.Approval.LegacyMigration.ModelHash.Should().Be(modelHash);
                entry.Manifest.Approval.ReplayEvidenceId.Should().BeEmpty();
                runtime.StartupDiagnostics.CurrentReport.Items.Should().Contain(item =>
                    item.Name == "Replay evidence gate" &&
                    item.Status == StartupDiagnosticStatus.Pass &&
                    item.IsBlocking);

                await runtime.DisposeAsync();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_默认配置使用Cpu检测服务()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var appConfig = new AppConfig
            {
                StoragePath = tempDir
            };

            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                cameraService: null,
                plcService: null,
                detectionService: null,
                storageService: null,
                statisticsService: null,
                databaseService: null,
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: null);

            try
            {
                runtime.DetectionService.RuntimeStatus.GpuRequested.Should().BeFalse();
                runtime.DetectionService.RuntimeStatus.GpuActive.Should().BeFalse();
            }
            finally
            {
                await runtime.DisposeAsync();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task RefreshModelRegistry_运行中新加入Onnx会被重新发现()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string onnxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX");
            string modelName = $"runtime-refresh-{Guid.NewGuid():N}.onnx";
            string modelPath = Path.Combine(onnxDir, modelName);
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(onnxDir);

            var appConfig = new AppConfig { StoragePath = tempDir };
            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                cameraService: null,
                plcService: null,
                detectionService: null,
                storageService: null,
                statisticsService: null,
                databaseService: null,
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: null);

            try
            {
                runtime.ModelRegistry.Resolve(modelName).Should().BeNull();

                File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });

                runtime.RefreshModelRegistry();

                ModelRegistryEntry? entry = runtime.ModelRegistry.Resolve(modelName);
                entry.Should().NotBeNull();
                entry!.UsedModelName.Should().Be(modelName);
                entry.ModelPath.Should().Be(modelPath);
            }
            finally
            {
                if (File.Exists(modelPath))
                {
                    File.Delete(modelPath);
                }

                await runtime.DisposeAsync();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        private static string CreateApprovedPackage(string packageRoot, string modelId, string version)
        {
            string packageDir = Path.Combine(packageRoot, modelId);
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 3, 1, 4, (byte)modelId.Length, (byte)version.Length });
            string hash = ComputeSha256(modelPath);
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = modelId,
                    Version = version,
                    ModelFileName = "model.onnx",
                    ModelHash = hash,
                    Labels = new List<string> { "part" },
                    TaskType = "Detect",
                    InputWidth = 640,
                    InputHeight = 640,
                    Approval = new ModelApprovalMetadata
                    {
                        Status = ModelApprovalStatuses.Approved,
                        ApprovedBy = "qa",
                        ApprovedAt = DateTimeOffset.UtcNow
                    }
                }));
            return modelPath;
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        private sealed class FakeCameraService : ICameraService
        {
            private readonly List<string> _order;

            public FakeCameraService(List<string> order) => _order = order;

            public event Action<Mat>? FrameCaptured;
            public event Action<bool>? ConnectionChanged;
            public event Action<string>? ErrorOccurred;

            public bool IsOpen => false;
            public string CameraName => "Fake";
            public string? LastError => null;
            public Mat? LastFrame => null;
            public bool IsGrabbing => false;

            public bool Open(string serialNumber, string manufacturer) => true;
            public void Close() => _order.Add("camera-close");
            public void StartCapture() => _order.Add("camera-start-capture");
            public void StopCapture() => _order.Add("camera-stop-capture");
            public void TriggerOnce() { }
            public Mat? CaptureFrame(int timeoutMs = 3000) => null;
            public void SetExposure(double exposureUs) { }
            public void SetGain(double gain) { }
            public void Dispose() => _order.Add("camera-dispose");
        }

        private sealed class FakePlcService : IPlcService
        {
            private readonly List<string> _order;

            public FakePlcService(List<string> order) => _order = order;

            public event Action<bool>? ConnectionChanged;
            public event Action? TriggerReceived;
            public event Action<PlcTriggerContext>? TriggerContextReceived;
            public event Action<string>? ErrorOccurred;

            public bool IsConnected => false;
            public string ProtocolName => "Fake";
            public string? LastError => null;

            public Task<bool> ConnectAsync(PlcConnectionOptions options)
                => Task.FromResult(true);

            public void Disconnect() => _order.Add("plc-disconnect");
            public bool StartMonitoring(
                string triggerAddress,
                int pollingIntervalMs = 500,
                int triggerDelayMs = 800,
                PlcMonitoringOptions? options = null)
            {
                _order.Add("plc-start-monitoring");
                return true;
            }
            public void StopMonitoring() => _order.Add("plc-stop-monitoring");
            public Task StopMonitoringAsync(System.Threading.CancellationToken cancellationToken = default)
            {
                StopMonitoring();
                return Task.CompletedTask;
            }
            public Task<bool> WriteResultAsync(string resultAddress, bool isQualified) => Task.FromResult(true);
            public Task<bool> WriteResultAsync(string resultAddress, short valueToWrite) => Task.FromResult(true);
            public Task<bool> WriteReleaseSignalAsync(string resultAddress) => Task.FromResult(true);
            public Task<(bool Success, short Value)> ReadWordAsync(string address)
                => Task.FromResult((true, (short)1));
            public Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName)
                => Task.FromResult((true, string.Empty));
            public void Dispose() => _order.Add("plc-dispose");
        }

        private sealed class FakeDetectionService : IDetectionService
        {
            private readonly List<string> _order;

            public FakeDetectionService(List<string> order) => _order = order;

            public event Action<DetectionResultData>? DetectionCompleted;
            public event Action<string>? ModelLoaded;
            public event Action<string>? ErrorOccurred;

            public bool IsModelLoaded => true;
            public string CurrentModelName => "fake-model";
            public IReadOnlyList<string> AvailableModels => Array.Empty<string>();
            public long LastInferenceMs => 0;
            public DetectionRuntimeStatus RuntimeStatus { get; } = new DetectionRuntimeStatus();
            public DetectionRuntimeModelSnapshot RuntimeModelSnapshot { get; } = new DetectionRuntimeModelSnapshot();

            public Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
            public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
            public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(true);
            public Task<DetectionResultData> DetectAsync(
                Mat image,
                float confidence,
                float iouThreshold,
                InspectionFallbackGoal? fallbackGoal = null,
                MultiModelCandidateEvaluator? candidateEvaluator = null)
                => Task.FromResult(new DetectionResultData());
            public Task<DetectionResultData> DetectAsync(
                Bitmap image,
                float confidence,
                float iouThreshold,
                InspectionFallbackGoal? fallbackGoal = null,
                MultiModelCandidateEvaluator? candidateEvaluator = null)
                => Task.FromResult(new DetectionResultData());
            public Bitmap GenerateResultImage(Bitmap original, List<YoloResult> results, string[] labels) => new Bitmap(original);
            public void SetTaskMode(int taskType) { }
            public void SetEnableFallback(bool enabled) { }
            public Task<bool> LoadAuxiliary1ModelAsync(string modelPath) => Task.FromResult(true);
            public Task<bool> LoadAuxiliary2ModelAsync(string modelPath) => Task.FromResult(true);
            public void UnloadPrimaryModel() { }
            public void UnloadAuxiliary1Model() { }
            public void UnloadAuxiliary2Model() { }
            public string[] GetLabels() => Array.Empty<string>();
            public object? GetLastMetrics() => null;
            public void Dispose() => _order.Add("detection-dispose");
        }

        private sealed class FakeStorageService : IStorageService
        {
            private readonly List<string> _order;

            public FakeStorageService(string basePath, List<string> order)
            {
                _order = order;
                ImageBasePath = Path.Combine(basePath, "Images");
                LogBasePath = Path.Combine(basePath, "Logs");
                SystemPath = Path.Combine(basePath, "System");
                BaseStoragePath = basePath;
            }

            public string ImageBasePath { get; }
            public string LogBasePath { get; }
            public string SystemPath { get; }
            public string BaseStoragePath { get; private set; }

            public void SaveDetectionImage(Bitmap bitmap, bool isQualified) { }
            public void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified) { }
            public void WriteDetectionLog(string content, bool isQualified) { }
            public void WriteStartupLog(string action, string? serialNumber = null) { }
            public void WriteErrorLog(string message) { }
            public void CleanOldData(int retainDays) { }
            public double GetDiskFreeSpaceGb() => 100.0;
            public double PerformEmergencyCleanup() => 100.0;
            public void EnsureDirectoriesExist() { }
            public void UpdateStoragePath(string storagePath) => BaseStoragePath = storagePath;
            public void Dispose() => _order.Add("storage-dispose");
        }

        private sealed class FakeStatisticsService : IStatisticsService
        {
            private readonly List<string> _order;
            private readonly StatisticsHistory _history = new StatisticsHistory();
            private readonly DetectionStatistics _stats = new DetectionStatistics();

            public FakeStatisticsService(List<string> order)
            {
                _order = order;
                string basePath = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeStats", Guid.NewGuid().ToString("N"));
                _history.SetSavePath(basePath);
                _stats.SetSavePath(basePath);
            }

            public event Action<StatisticsSnapshot>? StatisticsUpdated;
            public event Action? DayReset;

            public StatisticsSnapshot Current => new StatisticsSnapshot();
            public int TodayQualified => 0;
            public int TodayUnqualified => 0;
            public int TodayTotal => 0;
            public IReadOnlyList<DailyStatisticsRecord> History => Array.Empty<DailyStatisticsRecord>();

            public void RecordDetection(bool isQualified) { }
            public void ResetToday() { }
            public bool CheckAndResetForNewDay() => false;
            public void SaveAll() => _order.Add("statistics-save-all");
            public void ClearHistory() { }
            public void LoadAll() { }
            public void UpdateStoragePath(string basePath) { }
            public (StatisticsHistory history, DetectionStatistics stats) GetStatisticsData() => (_history, _stats);
            public void Dispose() => _order.Add("statistics-dispose");
        }

        private sealed class FakeDatabaseService : IDatabaseService
        {
            private readonly List<string> _order;

            public FakeDatabaseService(List<string> order) => _order = order;

            public Task InitializeAsync() => Task.CompletedTask;

            public async Task SaveDetectionRecordAsync(DetectionRecord record)
            {
                await Task.Delay(30);
                _order.Add("db-save");
            }

            public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
                => Task.FromResult(new List<DetectionRecord>());

            public Task<DetectionRecord?> GetDetectionRecordByIdAsync(long id)
                => Task.FromResult<DetectionRecord?>(null);

            public Task<List<DetectionRecord>> GetDetectionRecordsByInspectionIdAsync(string inspectionId)
                => Task.FromResult(new List<DetectionRecord>());

            public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
                => Task.FromResult(new List<DetectionTraceRecord>());

            public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
                => Task.FromResult(new DetectionTracePage());

            public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query)
                => Task.FromResult(new List<DetectionRecord>());

            public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
                => Task.FromResult(new List<string>());

            public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
                => Task.FromResult(new List<string>());

            public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
                => Task.FromResult((0, 0, 0));

            public Task<int> CleanupOldRecordsAsync(int daysToKeep)
                => Task.FromResult(0);

            public void Dispose() => _order.Add("db-dispose");
        }
    }
#pragma warning restore CS0067
}
