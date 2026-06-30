using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Services.Replay;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

#pragma warning disable CS0067
public class ReplayClosureTests
{
    [Fact]
    public async Task DeterministicFixtureReplay_生成8样本并精确报告差异指标()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions);
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);

            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-required-matrix",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            report.Status.Should().Be(ReplayRunStatuses.Completed);
            report.Metrics.SampleCount.Should().Be(8);
            report.Metrics.CandidateNewMissedDetectionCount.Should().Be(1);
            report.Metrics.CandidateFixedMissedDetectionCount.Should().Be(1);
            report.Metrics.CandidateNewFalseRejectCount.Should().Be(1);
            report.Metrics.CandidateFixedFalseRejectCount.Should().Be(1);
            report.Metrics.ChangedDecisionCount.Should().Be(4);
            File.Exists(report.ReportJsonPath).Should().BeTrue();
            File.Exists(report.ReportCsvPath).Should().BeTrue();

            runner.CallCount(fixture.BaselineModel.ModelId).Should().Be(8);
            runner.CallCount(fixture.CandidateModel.ModelId).Should().Be(8);
            runner.Events.Should().ContainInOrder(
                $"create:{fixture.BaselineModel.ModelId}",
                $"dispose:{fixture.BaselineModel.ModelId}",
                $"create:{fixture.CandidateModel.ModelId}");

            ReplayApprovalDecision decision = new ReplayAcceptancePolicy().Evaluate(report);
            decision.Approved.Should().BeFalse();
            decision.Reasons.Should().Contain(reason => reason.Contains("missed detections", StringComparison.OrdinalIgnoreCase));
            decision.Reasons.Should().Contain(reason => reason.Contains("false rejects", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayEvidence_批准后生产激活接受_篡改Dataset后拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions);
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);

            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-approval",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            ReplayApprovalDecision policy = new ReplayAcceptancePolicy().Evaluate(report);
            policy.Approved.Should().BeTrue();

            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "evidence"));
            ModelApprovalEvidence evidence = evidenceStore.SaveEvidence(report, "qa01", fixture.Dataset.RootDirectory);

            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: false));
            ModelRegistryEntry candidateBeforeApproval = registry.Resolve(fixture.CandidateModel.ModelPath)!;
            var acceptance = new ModelAcceptanceService(Path.Combine(tempDir, "unused-state.json"));
            acceptance.ApprovePackageWithReplayEvidence(candidateBeforeApproval, evidence).Succeeded.Should().BeTrue();

            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            ModelRegistryEntry approvedCandidate = registry.Resolve(fixture.CandidateModel.ModelPath)!;
            approvedCandidate.ApprovedForProduction.Should().BeTrue();
            approvedCandidate.Manifest!.Approval.ReplayEvidenceId.Should().Be(evidence.EvidenceId);
            approvedCandidate.Manifest.Approval.ReplayEvidenceHash.Should().Be(evidence.EvidenceHash);

            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            var evidenceGate = new ReplayApprovalEvidenceProductionGate(evidenceStore, fixture.DatasetStore);
            var activation = new ProductionModelActivationService(
                config,
                registry,
                recipeManager,
                detection,
                () => registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true)),
                () => true,
                () => null,
                () => "op",
                () => "Engineer",
                evidenceGate.Validate);

            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(approvedCandidate);
            ProductionModelActivationResult activationResult = await activation.ActivatePrimaryAsync(
                reference.ToSelectionValue(),
                "replay-approved-switch",
                useGpu: false,
                gpuIndex: 0);

            activationResult.Succeeded.Should().BeTrue();
            activation.EnsureReadyForProduction().Succeeded.Should().BeTrue();

            await File.AppendAllTextAsync(fixture.Dataset.Samples[0].ImagePath, "tampered");

            ProductionModelReadinessResult readinessAfterTamper = activation.EnsureReadyForProduction();
            readinessAfterTamper.Succeeded.Should().BeFalse();
            readinessAfterTamper.ErrorCode.Should().Be("ReplayEvidenceDatasetHashMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayApplicationService_异常和取消都会释放当前模型会话()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions)
            {
                ThrowOnModelId = fixture.CandidateModel.ModelId,
                ThrowOnSampleId = "S2"
            };
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);

            Func<Task> act = () => service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-exception",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            await act.Should().ThrowAsync<InvalidOperationException>();
            runner.Events.Should().Contain($"dispose:{fixture.BaselineModel.ModelId}");
            runner.Events.Should().Contain($"dispose:{fixture.CandidateModel.ModelId}");

            using var cts = new CancellationTokenSource();
            var cancelRunner = new DeterministicReplayRunner(fixture.Decisions)
            {
                CancellationSource = cts,
                CancelAfterModelId = fixture.BaselineModel.ModelId,
                CancelAfterCallCount = 2
            };
            var cancelService = new ReplayApplicationService(
                fixture.DatasetStore,
                cancelRunner,
                new PassingReplayModelValidator(),
                new SqliteReplayRunStore(Path.Combine(tempDir, "replay-cancel.db"), Path.Combine(tempDir, "reports-cancel")));

            Func<Task> cancelAct = () => cancelService.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-cancel",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            }, cancellationToken: cts.Token);

            await cancelAct.Should().ThrowAsync<OperationCanceledException>();
            cancelRunner.Events.Should().Contain($"dispose:{fixture.BaselineModel.ModelId}");
            cancelRunner.Events.Should().NotContain($"create:{fixture.CandidateModel.ModelId}");

            var resumeRunner = new DeterministicReplayRunner(fixture.Decisions);
            var resumeService = new ReplayApplicationService(
                fixture.DatasetStore,
                resumeRunner,
                new PassingReplayModelValidator(),
                new SqliteReplayRunStore(Path.Combine(tempDir, "replay-resume.db"), Path.Combine(tempDir, "reports-resume")));
            ReplayRunReport resumed = await resumeService.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-cancel-resume",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });
            resumed.Status.Should().Be(ReplayRunStatuses.Completed);
            resumeRunner.CallCount(fixture.BaselineModel.ModelId).Should().Be(8);
            resumeRunner.CallCount(fixture.CandidateModel.ModelId).Should().Be(8);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static Dictionary<string, string> CandidateMatrixWithRegressions()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["S1"] = ReplayDecisions.OK,
            ["S2"] = ReplayDecisions.NG,
            ["S3"] = ReplayDecisions.NG,
            ["S4"] = ReplayDecisions.OK,
            ["S5"] = ReplayDecisions.OK,
            ["S6"] = ReplayDecisions.NG,
            ["S7"] = ReplayDecisions.OK,
            ["S8"] = ReplayDecisions.NG
        };
    }

    private static Dictionary<string, string> CandidateMatrixWithoutRegressions()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["S1"] = ReplayDecisions.OK,
            ["S2"] = ReplayDecisions.NG,
            ["S3"] = ReplayDecisions.NG,
            ["S4"] = ReplayDecisions.NG,
            ["S5"] = ReplayDecisions.OK,
            ["S6"] = ReplayDecisions.OK,
            ["S7"] = ReplayDecisions.OK,
            ["S8"] = ReplayDecisions.NG
        };
    }

    private static ModelRegistryScanOptions ScanOptions(string packageRoot, bool requireProductionApproval)
    {
        return new ModelRegistryScanOptions
        {
            PackageDirectory = packageRoot,
            RequireProductionApproval = requireProductionApproval,
            Warmup = (_, _) => true
        };
    }

    private static string CreatePackage(string packageRoot, string modelId, string version, bool approved)
    {
        string packageDir = Path.Combine(packageRoot, modelId);
        Directory.CreateDirectory(packageDir);
        string modelPath = Path.Combine(packageDir, "model.onnx");
        File.WriteAllBytes(modelPath, new byte[] { (byte)modelId.Length, (byte)version.Length, 1, 2, 3 });
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
                    Status = approved ? ModelApprovalStatuses.Approved : ModelApprovalStatuses.Pending,
                    ApprovedBy = approved ? "qa" : string.Empty,
                    ApprovedAt = approved ? DateTimeOffset.UtcNow : null
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

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(ReplayClosureTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class ReplayFixture
    {
        private ReplayFixture() { }

        public string PackageRoot { get; private init; } = string.Empty;
        public ReplayDatasetSnapshot Dataset { get; private init; } = new ReplayDatasetSnapshot();
        public FileReplayDatasetStore DatasetStore { get; private init; } = null!;
        public SqliteReplayRunStore RunStore { get; private init; } = null!;
        public ReplayModelIdentity BaselineModel { get; private init; } = new ReplayModelIdentity();
        public ReplayModelIdentity CandidateModel { get; private init; } = new ReplayModelIdentity();
        public Dictionary<string, Dictionary<string, string>> Decisions { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<ReplayFixture> CreateAsync(string tempDir, Dictionary<string, string> candidateDecisions)
        {
            string imageSourceDir = Path.Combine(tempDir, "source-images");
            Directory.CreateDirectory(imageSourceDir);
            List<DetectionRecord> records = new();
            Dictionary<string, ReplayManualReviewRecord> reviews = new(StringComparer.OrdinalIgnoreCase);
            string[] groundTruth = { "OK", "NG", "NG", "NG", "OK", "OK", "OK", "NG" };
            string[] baseline = { "OK", "NG", "OK", "NG", "NG", "OK", "OK", "NG" };

            for (int i = 0; i < 8; i++)
            {
                string sampleId = $"S{i + 1}";
                string imagePath = Path.Combine(imageSourceDir, $"{sampleId}.png");
                using (var image = new Mat(32, 32, MatType.CV_8UC3, new Scalar(20 + i * 20, 80, 160)))
                {
                    Cv2.Rectangle(image, new Rect(4 + i, 4, 12, 12), new Scalar(250, 250, 250), thickness: 1);
                    Cv2.ImWrite(imagePath, image);
                }

                string inspectionId = $"CF-FIXTURE-{sampleId}";
                records.Add(new DetectionRecord
                {
                    Id = i + 1,
                    Timestamp = new DateTime(2026, 6, 30, 8, 0, 0).AddMinutes(i),
                    IsQualified = baseline[i] == ReplayDecisions.OK,
                    InspectionId = inspectionId,
                    ImagePath = imagePath,
                    RecipeId = "recipe-clearfrost-fixture",
                    RecipeVersion = "20260630080000000",
                    ModelId = "baseline-model",
                    ModelVersion = "1",
                    ModelName = "baseline-model",
                    RuleSummary = "fixture rule",
                    RuleSetJson = JsonSerializer.Serialize(CreateRuleSet(), ReplayJson.Options),
                    ResultJson = "{}"
                });
                reviews[inspectionId] = new ReplayManualReviewRecord
                {
                    SampleId = sampleId,
                    InspectionId = inspectionId,
                    GroundTruth = groundTruth[i],
                    ReviewerId = "qa01",
                    Revision = 1,
                    ReviewedAt = new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero).AddMinutes(i)
                };
            }

            string packageRoot = Path.Combine(tempDir, "models");
            string baselinePath = CreatePackage(packageRoot, "baseline-model", "1", approved: true);
            string candidatePath = CreatePackage(packageRoot, "candidate-model", "2", approved: false);
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(packageRoot, requireProductionApproval: false));
            ReplayModelIdentity baselineModel = ReplayModelIdentity.FromRegistryEntry(registry.Resolve(baselinePath)!);
            ReplayModelIdentity candidateModel = ReplayModelIdentity.FromRegistryEntry(registry.Resolve(candidatePath)!);

            var database = new FakeDatabaseService(records);
            var datasetStore = new FileReplayDatasetStore(database, Path.Combine(tempDir, "datasets"));
            ReplayDatasetSnapshot dataset = await datasetStore.CreateSnapshotAsync(new ReplayDatasetCreateRequest
            {
                DatasetId = "fixture-dataset",
                Query = new DetectionReplayQuery { Limit = 8 },
                Recipe = new ReplayRecipeSnapshot
                {
                    RecipeId = "recipe-clearfrost-fixture",
                    RecipeVersion = "20260630080000000",
                    Confidence = 0.5f,
                    IouThreshold = 0.45f,
                    Roi = new[] { 0.1f, 0.1f, 0.8f, 0.8f },
                    RuleSet = CreateRuleSet(),
                    RuleSetJson = JsonSerializer.Serialize(CreateRuleSet(), ReplayJson.Options)
                },
                BaselineModel = baselineModel,
                CandidateModel = candidateModel,
                ManualReviewsByInspectionId = reviews
            });

            return new ReplayFixture
            {
                PackageRoot = packageRoot,
                Dataset = dataset,
                DatasetStore = datasetStore,
                RunStore = new SqliteReplayRunStore(Path.Combine(tempDir, "replay.db"), Path.Combine(tempDir, "reports")),
                BaselineModel = baselineModel,
                CandidateModel = candidateModel,
                Decisions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [baselineModel.ModelId] = Enumerable.Range(0, 8)
                        .ToDictionary(index => $"S{index + 1}", index => baseline[index], StringComparer.OrdinalIgnoreCase),
                    [candidateModel.ModelId] = candidateDecisions
                }
            };
        }

        private static InspectionRuleSet CreateRuleSet()
        {
            return new InspectionRuleSet
            {
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "fixture-count",
                        Type = InspectionRuleTypes.Count,
                        Label = "part",
                        Operator = InspectionRuleOperators.GreaterThanOrEqual,
                        Count = 1
                    }
                }
            };
        }
    }

    private sealed class PassingReplayModelValidator : IReplayModelValidator
    {
        public Task<ReplayModelValidationResult> ValidateAsync(
            ReplayModelIdentity model,
            ReplayModelValidationOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ReplayModelValidationResult.Ok());
        }
    }

    private sealed class DeterministicReplayRunner : IReplayInferenceRunner
    {
        private readonly Dictionary<string, Dictionary<string, string>> _decisions;
        private readonly Dictionary<string, int> _callCounts = new(StringComparer.OrdinalIgnoreCase);

        public DeterministicReplayRunner(Dictionary<string, Dictionary<string, string>> decisions)
        {
            _decisions = decisions;
        }

        public List<string> Events { get; } = new();
        public string ThrowOnModelId { get; init; } = string.Empty;
        public string ThrowOnSampleId { get; init; } = string.Empty;
        public CancellationTokenSource? CancellationSource { get; init; }
        public string CancelAfterModelId { get; init; } = string.Empty;
        public int CancelAfterCallCount { get; init; }

        public int CallCount(string modelId)
        {
            return _callCounts.TryGetValue(modelId, out int count) ? count : 0;
        }

        public Task<IReplayInferenceSession> CreateSessionAsync(
            ReplayModelIdentity model,
            ReplayRecipeSnapshot recipe,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"create:{model.ModelId}");
            return Task.FromResult<IReplayInferenceSession>(new Session(this, model));
        }

        private sealed class Session : IReplayInferenceSession
        {
            private readonly DeterministicReplayRunner _owner;

            public Session(DeterministicReplayRunner owner, ReplayModelIdentity model)
            {
                _owner = owner;
                Model = model;
            }

            public ReplayModelIdentity Model { get; }

            public Task<ReplayInferenceOutput> RunAsync(
                ReplayDatasetSample sample,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _owner._callCounts[Model.ModelId] = _owner.CallCount(Model.ModelId) + 1;
                _owner.Events.Add($"run:{Model.ModelId}:{sample.SampleId}");
                if (_owner.CancellationSource != null &&
                    _owner.CancelAfterCallCount > 0 &&
                    string.Equals(_owner.CancelAfterModelId, Model.ModelId, StringComparison.OrdinalIgnoreCase) &&
                    _owner.CallCount(Model.ModelId) >= _owner.CancelAfterCallCount)
                {
                    _owner.CancellationSource.Cancel();
                }

                if (string.Equals(_owner.ThrowOnModelId, Model.ModelId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_owner.ThrowOnSampleId, sample.SampleId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("fixture inference failed");
                }

                string decision = _owner._decisions[Model.ModelId][sample.SampleId];
                return Task.FromResult(new ReplayInferenceOutput
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    Decision = decision,
                    Confidence = 0.9f,
                    ElapsedMs = 1,
                    ModelId = Model.ModelId,
                    ModelVersion = Model.Version,
                    ModelHash = Model.Sha256,
                    RuleSummary = $"fixture {decision}"
                });
            }

            public ValueTask DisposeAsync()
            {
                _owner.Events.Add($"dispose:{Model.ModelId}");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeDatabaseService : IDatabaseService
    {
        private readonly List<DetectionRecord> _records;

        public FakeDatabaseService(List<DetectionRecord> records)
        {
            _records = records;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task SaveDetectionRecordAsync(DetectionRecord record) => Task.CompletedTask;
        public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
            => Task.FromResult(_records.Take(limit).ToList());
        public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
            => Task.FromResult(new List<DetectionTraceRecord>());
        public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
            => Task.FromResult(new DetectionTracePage());
        public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query)
        {
            IEnumerable<DetectionRecord> result = _records;
            if (query.IsQualified.HasValue)
            {
                result = result.Where(record => record.IsQualified == query.IsQualified.Value);
            }

            return Task.FromResult(result.Take(query.Limit <= 0 ? 100 : query.Limit).ToList());
        }

        public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
            => Task.FromResult(new List<string>());
        public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
            => Task.FromResult(new List<string>());
        public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
            => Task.FromResult((_records.Count, _records.Count(r => r.IsQualified), _records.Count(r => !r.IsQualified)));
        public Task<int> CleanupOldRecordsAsync(int daysToKeep) => Task.FromResult(0);
        public void Dispose() { }
    }

    private sealed class FakeDetectionService : IDetectionService
    {
        private string _primaryPath = string.Empty;

        public event Action<DetectionResultData>? DetectionCompleted;
        public event Action<string>? ModelLoaded;
        public event Action<string>? ErrorOccurred;

        public bool IsModelLoaded => !string.IsNullOrWhiteSpace(_primaryPath);
        public string CurrentModelName => Path.GetFileNameWithoutExtension(_primaryPath);
        public IReadOnlyList<string> AvailableModels => Array.Empty<string>();
        public long LastInferenceMs => 0;
        public DetectionRuntimeStatus RuntimeStatus { get; } = new DetectionRuntimeStatus();
        public DetectionRuntimeModelSnapshot RuntimeModelSnapshot => new DetectionRuntimeModelSnapshot
        {
            Primary = new DetectionModelSlotSnapshot
            {
                Role = ModelRole.Primary,
                IsLoaded = !string.IsNullOrWhiteSpace(_primaryPath),
                ModelPath = _primaryPath
            }
        };

        public Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0)
        {
            _primaryPath = Path.GetFullPath(modelPath);
            return Task.FromResult(true);
        }

        public void UnloadPrimaryModel() => _primaryPath = string.Empty;
        public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(false);
        public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(false);
        public Task<DetectionResultData> DetectAsync(Mat image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null) => Task.FromResult(new DetectionResultData());
        public Task<DetectionResultData> DetectAsync(System.Drawing.Bitmap image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null) => Task.FromResult(new DetectionResultData());
        public System.Drawing.Bitmap GenerateResultImage(System.Drawing.Bitmap original, List<YoloResult> results, string[] labels) => new System.Drawing.Bitmap(original);
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
}
#pragma warning restore CS0067
