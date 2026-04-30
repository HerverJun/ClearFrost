using System.Drawing;
using ClearFrost.Core.Inspection;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Models;
using ClearFrost.Services;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

#pragma warning disable CS0067
public class HealthMonitorTests
{
    [Fact]
    public void GetSnapshot_包含最近错误和队列状态()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostHealthTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(new RecordingDatabaseService());
            var monitor = new HealthMonitor(
                new FakeCameraService(),
                new FakePlcService(),
                new FakeDetectionService(),
                new FakeStorageService(tempDir),
                imageQueue,
                recordQueue);

            monitor.RecordError("PLC", "写入失败", "CF-1");
            monitor.RecordInspection(new InspectionContext
            {
                InspectionId = "CF-1",
                TotalMs = 123
            });

            HealthSnapshot snapshot = monitor.GetSnapshot();

            snapshot.HealthLevel.Should().Be(HealthLevel.Warning);
            snapshot.LastInspectionId.Should().Be("CF-1");
            snapshot.LastInspectionTotalMs.Should().Be(123);
            snapshot.ImageQueueLength.Should().Be(imageQueue.PendingCount);
            snapshot.RecordQueueLength.Should().Be(recordQueue.PendingCount);
            snapshot.RecentErrors.Should().Contain(e => e.Source == "PLC" && e.InspectionId == "CF-1");
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
        public event Action<Mat>? FrameCaptured;
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? ErrorOccurred;

        public bool IsOpen => true;
        public string CameraName => "Fake";
        public string? LastError => null;
        public Mat? LastFrame => null;
        public bool IsGrabbing => true;

        public bool Open(string serialNumber, string manufacturer) => true;
        public void Close() { }
        public void StartCapture() { }
        public void StopCapture() { }
        public void TriggerOnce() { }
        public Mat? CaptureFrame(int timeoutMs = 3000) => null;
        public void SetExposure(double exposureUs) { }
        public void SetGain(double gain) { }
        public void Dispose() { }
    }

    private sealed class FakePlcService : IPlcService
    {
        public event Action<bool>? ConnectionChanged;
        public event Action? TriggerReceived;
        public event Action<PlcTriggerContext>? TriggerContextReceived;
        public event Action<string>? ErrorOccurred;

        public bool IsConnected => true;
        public string ProtocolName => "Fake";
        public string? LastError => null;

        public Task<bool> ConnectAsync(PlcConnectionOptions options) => Task.FromResult(true);
        public void Disconnect() { }
        public void StartMonitoring(
            string triggerAddress,
            int pollingIntervalMs = 500,
            int triggerDelayMs = 800,
            PlcMonitoringOptions? options = null) { }
        public void StopMonitoring() { }
        public Task<bool> WriteResultAsync(string resultAddress, bool isQualified) => Task.FromResult(true);
        public Task<bool> WriteResultAsync(string resultAddress, short valueToWrite) => Task.FromResult(true);
        public Task<bool> WriteReleaseSignalAsync(string resultAddress) => Task.FromResult(true);
        public void Dispose() { }
    }

    private sealed class FakeDetectionService : IDetectionService
    {
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
        public void Dispose() { }
    }

    private sealed class FakeStorageService : IStorageService
    {
        public FakeStorageService(string basePath)
        {
            ImageBasePath = Path.Combine(basePath, "Images");
            LogBasePath = Path.Combine(basePath, "Logs");
            SystemPath = Path.Combine(basePath, "System");
            EnsureDirectoriesExist();
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
        public void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(ImageBasePath);
            Directory.CreateDirectory(LogBasePath);
            Directory.CreateDirectory(SystemPath);
        }
        public void Dispose() { }
    }

    private sealed class RecordingDatabaseService : IDatabaseService
    {
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SaveDetectionRecordAsync(DetectionRecord record) => Task.CompletedTask;
        public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
            => Task.FromResult(new List<DetectionRecord>());
        public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date) => Task.FromResult((0, 0, 0));
        public Task<int> CleanupOldRecordsAsync(int daysToKeep) => Task.FromResult(0);
        public void Dispose() { }
    }
}
#pragma warning restore CS0067
