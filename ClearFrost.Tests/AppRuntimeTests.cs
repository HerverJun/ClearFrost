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
        public async Task ApplyRuntimeStoragePath_刷新存储和统计服务路径()
        {
            string firstDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string secondDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(firstDir);
            Directory.CreateDirectory(secondDir);

            var order = new List<string>();
            var appConfig = new AppConfig { StoragePath = firstDir };
            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                new FakeCameraService(order),
                new FakePlcService(order),
                new FakeDetectionService(order),
                storageService: null,
                statisticsService: null,
                databaseService: new FakeDatabaseService(order),
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: new WebUIController());

            try
            {
                appConfig.StoragePath = secondDir;

                runtime.ApplyRuntimeStoragePath();
                runtime.StatisticsService.RecordDetection(true);
                runtime.StatisticsService.SaveAll();

                var storageService = (StorageService)runtime.StorageService;
                storageService.BaseStoragePath.Should().Be(secondDir);
                Directory.Exists(Path.Combine(secondDir, "Images")).Should().BeTrue();
                File.Exists(Path.Combine(secondDir, "System", "statistics.json")).Should().BeTrue();
            }
            finally
            {
                await runtime.DisposeAsync();
                if (Directory.Exists(firstDir))
                {
                    Directory.Delete(firstDir, true);
                }

                if (Directory.Exists(secondDir))
                {
                    Directory.Delete(secondDir, true);
                }
            }
        }

        [Fact]
        public async Task RefreshModelRegistry_运行中新增模型包_刷新追溯注册表()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string packageRoot = Path.Combine(tempDir, "models");
            Directory.CreateDirectory(packageRoot);

            var order = new List<string>();
            var appConfig = new AppConfig
            {
                StoragePath = tempDir,
                ModelPackageDirectory = packageRoot
            };
            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                new FakeCameraService(order),
                new FakePlcService(order),
                new FakeDetectionService(order),
                storageService: null,
                statisticsService: null,
                databaseService: new FakeDatabaseService(order),
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: new WebUIController());

            try
            {
                runtime.ModelRegistry.Resolve("pkg-runtime").Should().BeNull();

                string packageDir = Path.Combine(packageRoot, "pkg-runtime");
                Directory.CreateDirectory(packageDir);
                string modelPath = Path.Combine(packageDir, "runtime.onnx");
                File.WriteAllBytes(modelPath, new byte[] { 7, 8, 9 });
                WriteModelPackageManifest(packageDir, modelPath);

                runtime.RefreshModelRegistry();

                ModelRegistryEntry? entry = runtime.ModelRegistry.Resolve("pkg-runtime");
                entry.Should().NotBeNull();
                entry!.ModelId.Should().Be("pkg-runtime");
                entry.ModelHash.Should().Be(ComputeSha256(modelPath));
                runtime.ModelRegistry.Resolve("runtime.onnx").Should().BeSameAs(entry);
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
        public async Task ImportModelPackage_导入后刷新注册表和启动诊断()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string packageRoot = Path.Combine(tempDir, "models");
            string onnxDir = Path.Combine(tempDir, "ONNX");
            Directory.CreateDirectory(tempDir);
            string sourcePath = Path.Combine(tempDir, "incoming.onnx");
            File.WriteAllBytes(sourcePath, new byte[] { 4, 5, 6 });

            var order = new List<string>();
            var appConfig = new AppConfig
            {
                StoragePath = tempDir,
                ModelPackageDirectory = packageRoot
            };
            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                new FakeCameraService(order),
                new FakePlcService(order),
                new FakeDetectionService(order),
                storageService: null,
                statisticsService: null,
                databaseService: new FakeDatabaseService(order),
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: new WebUIController());

            try
            {
                ModelPackageImportResult result = runtime.ImportModelPackage(new ModelPackageImportOptions
                {
                    SourceModelPath = sourcePath,
                    OnnxDirectory = onnxDir,
                    ModelId = "runtime-import",
                    Version = "2026.05",
                    Labels = new[] { "screw" },
                    Warmup = (_, _) => true
                });

                result.Success.Should().BeTrue();
                runtime.ModelRegistry.Resolve("runtime-import").Should().NotBeNull();
                File.Exists(Path.Combine(onnxDir, "incoming.onnx")).Should().BeTrue();
                runtime.StartupDiagnostics.CurrentReport.Items.Should().Contain(i =>
                    i.Name == "Model registry" &&
                    i.Status != StartupDiagnosticStatus.Fail);
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

        private static void WriteModelPackageManifest(string packageDir, string modelPath)
        {
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-runtime",
                    Version = "2026.05",
                    ModelFileName = Path.GetFileName(modelPath),
                    ModelHash = ComputeSha256(modelPath),
                    Labels = new List<string> { "screw" }
                }));
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
            public bool IsMockCamera => false;

            public bool Open(string serialNumber, string manufacturer) => true;
            public void Close() => _order.Add("camera-close");
            public void StartCapture() => _order.Add("camera-start-capture");
            public void StopCapture() => _order.Add("camera-stop-capture");
            public void TriggerOnce() { }
            public Mat? CaptureFrame(int timeoutMs = 3000) => null;
            public void SetExposure(double exposureUs) { }
            public void SetGain(double gain) { }
            public bool SetPixelFormat(string pixelFormat) => true;
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
            public void StartMonitoring(
                string triggerAddress,
                int pollingIntervalMs = 500,
                int triggerDelayMs = 800,
                PlcMonitoringOptions? options = null)
                => _order.Add("plc-start-monitoring");
            public void StopMonitoring() => _order.Add("plc-stop-monitoring");
            public Task<bool> WriteResultAsync(string resultAddress, bool isQualified) => Task.FromResult(true);
            public Task<bool> WriteResultAsync(string resultAddress, short valueToWrite) => Task.FromResult(true);
            public Task<bool> WriteReleaseSignalAsync(string resultAddress) => Task.FromResult(true);
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
            }

            public string ImageBasePath { get; }
            public string LogBasePath { get; }
            public string SystemPath { get; }

            public void SaveDetectionImage(Bitmap bitmap, bool isQualified) { }
            public void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified) { }
            public void WriteDetectionLog(string content, bool isQualified) { }
            public void WriteStartupLog(string action, string? serialNumber = null) { }
            public void WriteErrorLog(string message) { }
            public void WriteAuditLog(string category, string action, string detail, bool success = true) { }
            public void CleanOldData(int retainDays) { }
            public double GetDiskFreeSpaceGb() => 100.0;
            public double PerformEmergencyCleanup() => 100.0;
            public void EnsureDirectoriesExist() { }
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

            public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
                => Task.FromResult(new List<DetectionTraceRecord>());

            public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
                => Task.FromResult(new DetectionTracePage());

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
