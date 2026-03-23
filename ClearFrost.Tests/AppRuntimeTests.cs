using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using ClearFrost.Config;
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
            public event Action<string>? ErrorOccurred;

            public bool IsConnected => false;
            public string ProtocolName => "Fake";
            public string? LastError => null;

            public Task<bool> ConnectAsync(string protocol, string ip, int port, string driverProvider = "Hsl")
                => Task.FromResult(true);

            public void Disconnect() => _order.Add("plc-disconnect");
            public void StartMonitoring(short triggerAddress, int pollingIntervalMs = 500, int triggerDelayMs = 800)
                => _order.Add("plc-start-monitoring");
            public void StopMonitoring() => _order.Add("plc-stop-monitoring");
            public Task<bool> WriteResultAsync(short resultAddress, bool isQualified) => Task.FromResult(true);
            public Task<bool> WriteResultAsync(short resultAddress, short valueToWrite) => Task.FromResult(true);
            public Task<bool> WriteReleaseSignalAsync(short resultAddress) => Task.FromResult(true);
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

            public Task<bool> LoadModelAsync(string modelPath, bool useGpu) => Task.FromResult(true);
            public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu) => Task.FromResult(true);
            public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(true);
            public Task<DetectionResultData> DetectAsync(Mat image, float confidence, float iouThreshold, string? targetLabel = null, int targetCount = 0)
                => Task.FromResult(new DetectionResultData());
            public Task<DetectionResultData> DetectAsync(Bitmap image, float confidence, float iouThreshold, string? targetLabel = null, int targetCount = 0)
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
            public void CleanOldData(int retainDays) { }
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

            public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
                => Task.FromResult((0, 0, 0));

            public Task<int> CleanupOldRecordsAsync(int daysToKeep)
                => Task.FromResult(0);

            public void Dispose() => _order.Add("db-dispose");
        }
    }
#pragma warning restore CS0067
}
