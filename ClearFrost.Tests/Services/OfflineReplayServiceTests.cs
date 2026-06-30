using System.Drawing;
using ClearFrost.Core.Rules;
using ClearFrost.Interfaces;
using ClearFrost.Models;
using ClearFrost.Services;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

#pragma warning disable CS0067
public class OfflineReplayServiceTests
{
    [Fact]
    public async Task ReplayAsync_用当前模型和规则返回差异与原因()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string imagePath = Path.Combine(tempDir, "sample.jpg");
            using (var image = new Mat(32, 32, MatType.CV_8UC3, Scalar.All(100)))
            {
                Cv2.ImWrite(imagePath, image);
            }

            var database = new FakeDatabaseService(new DetectionRecord
            {
                Id = 10,
                InspectionId = "CF-REPLAY-OK-NG",
                Timestamp = DateTime.Now,
                IsQualified = true,
                ImagePath = imagePath,
                RuleSummary = "old ok",
                RecipeVersion = "r1",
                ModelName = "old.onnx"
            });
            var detection = new FakeDetectionService
            {
                Result = new DetectionResultData
                {
                    Results = new List<YoloResult> { Detection(16, 16, 8, 8, 0.91f, 0) },
                    UsedModelName = "new.onnx",
                    UsedModelLabels = new[] { "part" }
                }
            };
            var service = new OfflineReplayService(database, detection, CreateRuleSet);

            OfflineReplayBatchResult batch = await service.ReplayAsync(
                new DetectionReplayQuery { RecipeVersion = "r1" },
                confidence: 0.5f,
                iouThreshold: 0.3f);

            batch.InputCount.Should().Be(1);
            batch.ReplayedCount.Should().Be(1);
            batch.DifferenceCount.Should().Be(1);
            OfflineReplayResult result = batch.Results.Single();
            result.OriginalIsQualified.Should().BeTrue();
            result.NewIsQualified.Should().BeFalse();
            result.Difference.Should().Be("ResultChanged");
            result.Confidence.Should().BeApproximately(0.91, 0.001);
            result.RuleReason.Should().Contain("数量");
            result.ModelName.Should().Be("new.onnx");
            database.LastQuery!.RecipeVersion.Should().Be("r1");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayAsync_历史图片缺失时返回错误但不中断批次()
    {
        var database = new FakeDatabaseService(new DetectionRecord
        {
            Id = 11,
            InspectionId = "CF-REPLAY-MISSING",
            Timestamp = DateTime.Now,
            IsQualified = false,
            ImagePath = @"C:\missing\sample.jpg",
            ModelName = "old.onnx"
        });
        var service = new OfflineReplayService(database, new FakeDetectionService(), CreateRuleSet);

        OfflineReplayBatchResult batch = await service.ReplayAsync(new DetectionReplayQuery(), 0.5f, 0.3f);

        batch.InputCount.Should().Be(1);
        batch.ReplayedCount.Should().Be(0);
        batch.Errors.Should().ContainSingle(error => error.Contains("历史样本图片不存在"));
        batch.Results.Single().Status.Should().Be("ImageMissing");
    }

    private static InspectionRuleSet CreateRuleSet()
    {
        return new InspectionRuleSet
        {
            Rules = new List<InspectionRule>
            {
                new InspectionRule
                {
                    Name = "part-count",
                    Type = InspectionRuleTypes.Count,
                    Label = "part",
                    Operator = InspectionRuleOperators.Equal,
                    Count = 2
                }
            }
        };
    }

    private static YoloResult Detection(float centerX, float centerY, float width, float height, float confidence, int classId)
    {
        var result = new YoloResult();
        result.SetDetectionData(centerX, centerY, width, height, confidence, classId);
        return result;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostReplayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeDatabaseService : IDatabaseService
    {
        private readonly List<DetectionRecord> _records;

        public FakeDatabaseService(params DetectionRecord[] records)
        {
            _records = records.ToList();
        }

        public DetectionReplayQuery? LastQuery { get; private set; }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task SaveDetectionRecordAsync(DetectionRecord record) => Task.CompletedTask;
        public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
            => Task.FromResult(new List<DetectionRecord>());
        public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
            => Task.FromResult(new List<DetectionTraceRecord>());
        public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
            => Task.FromResult(new DetectionTracePage());
        public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query)
        {
            LastQuery = query;
            return Task.FromResult(_records);
        }
        public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
            => Task.FromResult(new List<string>());
        public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
            => Task.FromResult(new List<string>());
        public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
            => Task.FromResult((0, 0, 0));
        public Task<int> CleanupOldRecordsAsync(int daysToKeep)
            => Task.FromResult(0);
        public void Dispose() { }
    }

    private sealed class FakeDetectionService : IDetectionService
    {
        public event Action<DetectionResultData>? DetectionCompleted;
        public event Action<string>? ModelLoaded;
        public event Action<string>? ErrorOccurred;

        public DetectionResultData Result { get; init; } = new DetectionResultData
        {
            Results = new List<YoloResult>(),
            UsedModelName = "fake.onnx",
            UsedModelLabels = new[] { "part" }
        };

        public bool IsModelLoaded => true;
        public string CurrentModelName => "fake.onnx";
        public IReadOnlyList<string> AvailableModels => Array.Empty<string>();
        public long LastInferenceMs => 0;
        public DetectionRuntimeStatus RuntimeStatus { get; } = new DetectionRuntimeStatus();

        public Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
        public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
        public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(true);
        public Task<DetectionResultData> DetectAsync(Mat image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null)
            => Task.FromResult(Result);
        public Task<DetectionResultData> DetectAsync(Bitmap image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null)
            => Task.FromResult(Result);
        public Bitmap GenerateResultImage(Bitmap original, List<YoloResult> results, string[] labels) => new Bitmap(original);
        public void SetTaskMode(int taskType) { }
        public void SetEnableFallback(bool enabled) { }
        public Task<bool> LoadAuxiliary1ModelAsync(string modelPath) => Task.FromResult(true);
        public Task<bool> LoadAuxiliary2ModelAsync(string modelPath) => Task.FromResult(true);
        public void UnloadAuxiliary1Model() { }
        public void UnloadAuxiliary2Model() { }
        public string[] GetLabels() => new[] { "part" };
        public object? GetLastMetrics() => null;
        public void Dispose() { }
    }
}
#pragma warning restore CS0067
