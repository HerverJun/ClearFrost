using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClearFrost.Services.Replay
{
    public sealed class ReplayApplicationService
    {
        private readonly IReplayDatasetStore _datasetStore;
        private readonly IReplayInferenceRunner _inferenceRunner;
        private readonly IReplayModelValidator _modelValidator;
        private readonly IReplayRunStore _runStore;
        private readonly ReplayAcceptancePolicy _policy;

        public ReplayApplicationService(
            IReplayDatasetStore datasetStore,
            IReplayInferenceRunner inferenceRunner,
            IReplayModelValidator modelValidator,
            IReplayRunStore runStore,
            ReplayAcceptancePolicy? policy = null)
        {
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _inferenceRunner = inferenceRunner ?? throw new ArgumentNullException(nameof(inferenceRunner));
            _modelValidator = modelValidator ?? throw new ArgumentNullException(nameof(modelValidator));
            _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
            _policy = policy ?? new ReplayAcceptancePolicy();
        }

        public async Task<ReplayRunReport> RunComparisonAsync(
            ReplayComparisonRequest request,
            IProgress<ReplayRunProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string runId = string.IsNullOrWhiteSpace(request.RunId)
                ? $"replay-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"
                : request.RunId.Trim();

            ReplayDatasetSnapshot dataset = await _datasetStore.LoadSnapshotAsync(
                request.DatasetId,
                cancellationToken).ConfigureAwait(false);

            var report = new ReplayRunReport
            {
                RunId = runId,
                Status = ReplayRunStatuses.Preparing,
                DatasetId = dataset.DatasetId,
                DatasetHash = dataset.DatasetHash,
                BaselineModel = request.BaselineModel,
                CandidateModel = request.CandidateModel,
                StartedAt = DateTimeOffset.UtcNow,
                PolicyVersion = _policy.Options.Version,
                RecipeHash = FileReplayDatasetStore.ComputeRecipeHash(dataset.Recipe),
                RuleSetHash = FileReplayDatasetStore.ComputeRuleSetHash(dataset.Recipe.RuleSetJson),
                PolicyHash = _policy.PolicyHash,
                PolicySnapshot = _policy.Options.Clone(),
                BaselineModelHash = request.BaselineModel.Sha256,
                CandidateModelHash = request.CandidateModel.Sha256
            };

            await _runStore.RecordRunStartedAsync(report, cancellationToken).ConfigureAwait(false);
            Publish(progress, runId, ReplayRunStatuses.Preparing, "validate", 0, dataset.Samples.Count, "Validating replay models");

            try
            {
                await EnsureModelValidAsync(
                    request.BaselineModel,
                    new ReplayModelValidationOptions { AllowPendingApproval = false, RequireWarmup = true },
                    cancellationToken).ConfigureAwait(false);

                await EnsureModelValidAsync(
                    request.CandidateModel,
                    new ReplayModelValidationOptions { AllowPendingApproval = true, RequireWarmup = true },
                    cancellationToken).ConfigureAwait(false);

                Dictionary<string, ReplayInferenceOutput> baselineOutputs = await RunModelAsync(
                    runId,
                    "baseline",
                    ReplayRunStatuses.BaselineRunning,
                    request.BaselineModel,
                    dataset,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                Dictionary<string, ReplayInferenceOutput> candidateOutputs = await RunModelAsync(
                    runId,
                    "candidate",
                    ReplayRunStatuses.CandidateRunning,
                    request.CandidateModel,
                    dataset,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                IReadOnlyList<ReplaySampleComparison> comparisons = BuildComparisons(
                    dataset,
                    baselineOutputs,
                    candidateOutputs);

                report.Status = ReplayRunStatuses.Completed;
                report.CompletedAt = DateTimeOffset.UtcNow;
                report.Samples = comparisons;
                report.Metrics = ReplayMetrics.Compute(comparisons);

                report = await _runStore.SaveReportAsync(report, cancellationToken).ConfigureAwait(false);
                Publish(progress, runId, ReplayRunStatuses.Completed, "completed", dataset.Samples.Count, dataset.Samples.Count, "Replay completed");
                return report;
            }
            catch (OperationCanceledException)
            {
                await _runStore.RecordRunCanceledAsync(runId, CancellationToken.None).ConfigureAwait(false);
                Publish(progress, runId, ReplayRunStatuses.Canceled, "canceled", 0, dataset.Samples.Count, "Replay canceled");
                throw;
            }
            catch (Exception ex)
            {
                await _runStore.RecordRunFailedAsync(runId, ex.Message, CancellationToken.None).ConfigureAwait(false);
                Publish(progress, runId, ReplayRunStatuses.Failed, "failed", 0, dataset.Samples.Count, ex.Message);
                throw;
            }
        }

        private async Task EnsureModelValidAsync(
            ReplayModelIdentity model,
            ReplayModelValidationOptions options,
            CancellationToken cancellationToken)
        {
            ReplayModelValidationResult result = await _modelValidator.ValidateAsync(
                model,
                options,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.ErrorCode)
                        ? result.Message
                        : $"{result.ErrorCode}: {result.Message}");
            }
        }

        private async Task<Dictionary<string, ReplayInferenceOutput>> RunModelAsync(
            string runId,
            string phase,
            string status,
            ReplayModelIdentity model,
            ReplayDatasetSnapshot dataset,
            IProgress<ReplayRunProgress>? progress,
            CancellationToken cancellationToken)
        {
            var outputs = new Dictionary<string, ReplayInferenceOutput>(StringComparer.OrdinalIgnoreCase);
            await using IReplayInferenceSession session = await _inferenceRunner.CreateSessionAsync(
                model,
                dataset.Recipe,
                cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < dataset.Samples.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplayDatasetSample sample = dataset.Samples[i];
                try
                {
                    ReplayInferenceOutput output = await session.RunAsync(sample, cancellationToken).ConfigureAwait(false);
                    outputs[sample.SampleId] = output;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outputs[sample.SampleId] = new ReplayInferenceOutput
                    {
                        SampleId = sample.SampleId,
                        InspectionId = sample.InspectionId,
                        Decision = string.Empty,
                        ElapsedMs = -1,
                        ModelId = model.ModelId,
                        ModelVersion = model.Version,
                        ModelHash = model.Sha256,
                        ErrorMessage = $"{phase}: {ex.Message}"
                    };
                }

                var replayProgress = new ReplayRunProgress
                {
                    RunId = runId,
                    Status = status,
                    Phase = phase,
                    CompletedSamples = i + 1,
                    TotalSamples = dataset.Samples.Count,
                    Message = $"{phase} {i + 1}/{dataset.Samples.Count}"
                };
                progress?.Report(replayProgress);
                await _runStore.RecordRunProgressAsync(replayProgress, cancellationToken).ConfigureAwait(false);
            }

            return outputs;
        }

        private static IReadOnlyList<ReplaySampleComparison> BuildComparisons(
            ReplayDatasetSnapshot dataset,
            IReadOnlyDictionary<string, ReplayInferenceOutput> baselineOutputs,
            IReadOnlyDictionary<string, ReplayInferenceOutput> candidateOutputs)
        {
            var comparisons = new List<ReplaySampleComparison>(dataset.Samples.Count);
            foreach (ReplayDatasetSample sample in dataset.Samples.OrderBy(item => item.SampleId, StringComparer.OrdinalIgnoreCase))
            {
                baselineOutputs.TryGetValue(sample.SampleId, out ReplayInferenceOutput? baseline);
                candidateOutputs.TryGetValue(sample.SampleId, out ReplayInferenceOutput? candidate);
                var invalidReasons = new List<string>();
                string groundTruth = NormalizeOrMarkInvalid(
                    sample.GroundTruth,
                    $"Ground truth invalid: {sample.GroundTruth}",
                    invalidReasons);
                string baselineDecision = NormalizeOutputDecisionOrMarkInvalid(
                    baseline,
                    "Baseline",
                    sample.SampleId,
                    invalidReasons);
                string candidateDecision = NormalizeOutputDecisionOrMarkInvalid(
                    candidate,
                    "Candidate",
                    sample.SampleId,
                    invalidReasons);

                var comparison = new ReplaySampleComparison
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    GroundTruth = groundTruth,
                    BaselineDecision = baselineDecision,
                    CandidateDecision = candidateDecision,
                    DecisionChanged = invalidReasons.Count == 0 &&
                        !string.Equals(baselineDecision, candidateDecision, StringComparison.Ordinal),
                    BaselineElapsedMs = baseline?.ElapsedMs ?? -1,
                    CandidateElapsedMs = candidate?.ElapsedMs ?? -1,
                    BaselineRuleSummary = baseline?.RuleSummary ?? string.Empty,
                    CandidateRuleSummary = candidate?.RuleSummary ?? string.Empty,
                    IsValid = invalidReasons.Count == 0,
                    InvalidReason = string.Join("; ", invalidReasons)
                };
                comparison.Classification = ReplayMetrics.Classify(comparison);
                comparisons.Add(comparison);
            }

            return comparisons;
        }

        private static string NormalizeOutputDecisionOrMarkInvalid(
            ReplayInferenceOutput? output,
            string label,
            string sampleId,
            ICollection<string> invalidReasons)
        {
            if (output == null)
            {
                invalidReasons.Add($"{label} output missing for sample {sampleId}.");
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(output.ErrorMessage))
            {
                invalidReasons.Add($"{label} inference failed: {output.ErrorMessage}");
                return string.Empty;
            }

            return NormalizeOrMarkInvalid(
                output.Decision,
                $"{label} decision invalid: {output.Decision}",
                invalidReasons);
        }

        private static string NormalizeOrMarkInvalid(
            string value,
            string reason,
            ICollection<string> invalidReasons)
        {
            if (ReplayMetrics.TryNormalizeDecision(value, out string normalized))
            {
                return normalized;
            }

            invalidReasons.Add(reason);
            return string.Empty;
        }

        private static void Publish(
            IProgress<ReplayRunProgress>? progress,
            string runId,
            string status,
            string phase,
            int completed,
            int total,
            string message)
        {
            progress?.Report(new ReplayRunProgress
            {
                RunId = runId,
                Status = status,
                Phase = phase,
                CompletedSamples = completed,
                TotalSamples = total,
                Message = message
            });
        }
    }

    public sealed class ReplayRunCoordinator : IDisposable
    {
        private readonly ReplayApplicationService _replayService;
        private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private CancellationTokenSource? _replayCts;
        private Task<ReplayRunReport>? _currentTask;
        private ReplayRunProgress? _currentRun;
        private bool _productionRunning;
        private bool _disposed;

        public ReplayRunCoordinator(ReplayApplicationService replayService)
        {
            _replayService = replayService ?? throw new ArgumentNullException(nameof(replayService));
        }

        public ReplayRunProgress? CurrentRun
        {
            get
            {
                lock (_sync)
                {
                    return _currentRun;
                }
            }
        }

        public bool IsProductionRunning
        {
            get
            {
                lock (_sync)
                {
                    return _productionRunning;
                }
            }
        }

        public bool IsReplayRunning
        {
            get
            {
                lock (_sync)
                {
                    return _currentTask != null && !_currentTask.IsCompleted;
                }
            }
        }

        public async Task<bool> TryBeginProductionAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!await _lifecycleGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            lock (_sync)
            {
                _productionRunning = true;
            }

            return true;
        }

        public void EndProduction()
        {
            bool release;
            lock (_sync)
            {
                release = _productionRunning;
                _productionRunning = false;
            }

            if (release)
            {
                _lifecycleGate.Release();
            }
        }

        public async Task<ReplayRunReport> StartAsync(
            ReplayComparisonRequest request,
            IProgress<ReplayRunProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!await _lifecycleGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(IsProductionRunning
                    ? "ReplayProductionBusy"
                    : "ReplayAlreadyRunning");
            }

            CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var coordinatorProgress = new Progress<ReplayRunProgress>(item =>
            {
                lock (_sync)
                {
                    _currentRun = item;
                }

                progress?.Report(item);
            });

            Task<ReplayRunReport> task;
            lock (_sync)
            {
                _replayCts = linkedCts;
                _currentRun = new ReplayRunProgress
                {
                    RunId = request.RunId,
                    Status = ReplayRunStatuses.Preparing,
                    Phase = "queued",
                    Message = "Replay queued."
                };
                task = RunWithLifecycleGateAsync(request, coordinatorProgress, linkedCts);
                _currentTask = task;
            }

            return await task.ConfigureAwait(false);
        }

        public void Cancel()
        {
            lock (_sync)
            {
                _replayCts?.Cancel();
            }
        }

        public async Task CancelAndWaitAsync(CancellationToken cancellationToken = default)
        {
            Task<ReplayRunReport>? task;
            lock (_sync)
            {
                _replayCts?.Cancel();
                task = _currentTask;
            }

            if (task == null)
            {
                return;
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(() => Cancel());
            try
            {
                Task completed = await Task.WhenAny(
                    task,
                    Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
                if (!ReferenceEquals(completed, task))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Cancel();
            _replayCts?.Dispose();
            _lifecycleGate.Dispose();
        }

        private async Task<ReplayRunReport> RunWithLifecycleGateAsync(
            ReplayComparisonRequest request,
            IProgress<ReplayRunProgress> progress,
            CancellationTokenSource linkedCts)
        {
            try
            {
                using (await ClearFrost.Services.DetectionRuntimeConcurrencyGate.EnterAsync(linkedCts.Token).ConfigureAwait(false))
                {
                    return await _replayService.RunComparisonAsync(request, progress, linkedCts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_replayCts, linkedCts))
                    {
                        _replayCts = null;
                    }

                    _currentTask = null;
                }

                linkedCts.Dispose();
                _lifecycleGate.Release();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReplayRunCoordinator));
            }
        }
    }

    public sealed class ReplayAcceptancePolicy
    {
        private readonly ReplayAcceptancePolicyOptions _options;

        public ReplayAcceptancePolicy(ReplayAcceptancePolicyOptions? options = null)
        {
            _options = options ?? ReplayAcceptancePolicyOptions.ProductionDefault();
        }

        public ReplayAcceptancePolicyOptions Options => _options;

        public string PolicyHash => ComputePolicyHash(_options);

        public ReplayApprovalDecision Evaluate(ReplayRunReport report)
        {
            return Evaluate(report, _options);
        }

        public ReplayApprovalDecision Evaluate(ReplayRunReport report, ReplayAcceptancePolicyOptions options)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            options ??= ReplayAcceptancePolicyOptions.ProductionDefault();

            var reasons = new List<string>();
            if (!ReplayAcceptancePolicyOptions.IsSupportedVersion(options.Version))
            {
                reasons.Add($"Replay policy version is not supported: {options.Version}.");
                return new ReplayApprovalDecision
                {
                    Approved = false,
                    Reasons = reasons
                };
            }

            if (!string.Equals(report.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
            {
                reasons.Add($"Replay status is {report.Status}.");
            }

            ReplayComparisonMetrics metrics = report.Metrics ?? new ReplayComparisonMetrics();
            int validSampleCount = GetValidSampleCount(metrics, options.Version);
            int totalSampleCount = GetTotalSampleCount(metrics, validSampleCount);
            if (validSampleCount <= 0)
            {
                reasons.Add("Replay has zero valid samples.");
            }

            if (validSampleCount < options.MinimumValidSamples)
            {
                reasons.Add($"Replay valid samples {validSampleCount} below policy {options.MinimumValidSamples}.");
            }

            if (metrics.InvalidSampleCount > options.MaximumInvalidSampleCount)
            {
                reasons.Add($"Replay invalid samples {metrics.InvalidSampleCount} exceed policy {options.MaximumInvalidSampleCount}.");
            }

            if (options.MaximumInvalidSampleRate.HasValue &&
                Rate(metrics.InvalidSampleCount, totalSampleCount) > options.MaximumInvalidSampleRate.Value)
            {
                reasons.Add("Replay invalid sample rate exceeds policy.");
            }

            if (metrics.CandidateNewMissedDetectionCount > options.MaximumNewMissedDetections)
            {
                reasons.Add($"Candidate introduced missed detections: {metrics.CandidateNewMissedDetectionCount}.");
            }

            if (options.MaximumCandidateMissedDetectionRate.HasValue &&
                GetCandidateMissedDetectionRate(report, metrics, options.Version) > options.MaximumCandidateMissedDetectionRate.Value)
            {
                reasons.Add("Candidate missed detection rate exceeds policy.");
            }

            if (options.MaximumNewFalseRejects.HasValue &&
                metrics.CandidateNewFalseRejectCount > options.MaximumNewFalseRejects.Value)
            {
                reasons.Add($"Candidate introduced false rejects: {metrics.CandidateNewFalseRejectCount}.");
            }

            if (options.MaximumFalseRejectRateIncrease.HasValue &&
                GetFalseRejectRateIncrease(report, metrics, options.Version) > options.MaximumFalseRejectRateIncrease.Value)
            {
                reasons.Add("Candidate false reject rate increase exceeds policy.");
            }

            if (options.MinimumCandidateAccuracy.HasValue &&
                metrics.CandidateAccuracy < options.MinimumCandidateAccuracy.Value)
            {
                reasons.Add($"Candidate accuracy {metrics.CandidateAccuracy:P2} is below policy {options.MinimumCandidateAccuracy.Value:P2}.");
            }

            if (options.MaximumCandidateP95ElapsedMs.HasValue &&
                metrics.CandidateP95ElapsedMs > options.MaximumCandidateP95ElapsedMs.Value)
            {
                reasons.Add($"Candidate P95 latency {metrics.CandidateP95ElapsedMs}ms exceeds policy {options.MaximumCandidateP95ElapsedMs.Value}ms.");
            }

            if (options.MaximumP95InferenceIncreaseRatio.HasValue)
            {
                if (metrics.BaselineP95ElapsedMs <= 0 && metrics.CandidateP95ElapsedMs > 0)
                {
                    reasons.Add("Candidate P95 latency ratio is undefined because baseline P95 is zero.");
                }
                else if (metrics.BaselineP95ElapsedMs > 0 &&
                         metrics.CandidateP95ElapsedMs / (double)metrics.BaselineP95ElapsedMs > options.MaximumP95InferenceIncreaseRatio.Value)
                {
                    reasons.Add("Candidate P95 latency increase ratio exceeds policy.");
                }
            }

            if (!AreFinite(metrics, options))
            {
                reasons.Add("Replay policy or metrics contain NaN/Infinity.");
            }

            if (report.Errors.Count > 0)
            {
                reasons.Add($"Replay report contains {report.Errors.Count} errors.");
            }

            return new ReplayApprovalDecision
            {
                Approved = reasons.Count == 0,
                Reasons = reasons
            };
        }

        private static int GetValidSampleCount(ReplayComparisonMetrics metrics, int policyVersion)
        {
            if (policyVersion >= 2)
            {
                return metrics.ValidSampleCount > 0 || metrics.TotalSampleCount > 0
                    ? metrics.ValidSampleCount
                    : metrics.SampleCount;
            }

            return metrics.SampleCount;
        }

        private static int GetTotalSampleCount(ReplayComparisonMetrics metrics, int validSampleCount)
        {
            return metrics.TotalSampleCount > 0
                ? metrics.TotalSampleCount
                : Math.Max(1, validSampleCount + metrics.InvalidSampleCount);
        }

        private static double GetCandidateMissedDetectionRate(
            ReplayRunReport report,
            ReplayComparisonMetrics metrics,
            int policyVersion)
        {
            if (policyVersion >= 2)
            {
                return metrics.CandidateMissedDetectionRate;
            }

            return Rate(metrics.CandidateNewMissedDetectionCount, CountGroundTruth(report, ReplayDecisions.NG, validOnly: false));
        }

        private static double GetFalseRejectRateIncrease(
            ReplayRunReport report,
            ReplayComparisonMetrics metrics,
            int policyVersion)
        {
            if (policyVersion >= 2)
            {
                return metrics.FalseRejectRateIncrease;
            }

            return Rate(metrics.CandidateNewFalseRejectCount, CountGroundTruth(report, ReplayDecisions.OK, validOnly: false));
        }

        private static int CountGroundTruth(ReplayRunReport report, string decision, bool validOnly)
        {
            return report.Samples.Count(sample =>
                (!validOnly || sample.IsValid) &&
                string.Equals(sample.GroundTruth, decision, StringComparison.OrdinalIgnoreCase));
        }

        private static double Rate(int numerator, int denominator)
        {
            return denominator <= 0 ? 0 : numerator / (double)denominator;
        }

        private static bool AreFinite(ReplayComparisonMetrics metrics, ReplayAcceptancePolicyOptions options)
        {
            static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
            static bool NullableFinite(double? value) => !value.HasValue || Finite(value.Value);
            return Finite(metrics.BaselineAccuracy) &&
                   Finite(metrics.CandidateAccuracy) &&
                   Finite(metrics.BaselineMissedDetectionRate) &&
                   Finite(metrics.CandidateMissedDetectionRate) &&
                   Finite(metrics.BaselineFalseRejectRate) &&
                   Finite(metrics.CandidateFalseRejectRate) &&
                   Finite(metrics.FalseRejectRateIncrease) &&
                   NullableFinite(options.MinimumCandidateAccuracy) &&
                   NullableFinite(options.MaximumInvalidSampleRate) &&
                   NullableFinite(options.MaximumCandidateMissedDetectionRate) &&
                   NullableFinite(options.MaximumFalseRejectRateIncrease) &&
                   NullableFinite(options.MaximumP95InferenceIncreaseRatio);
        }

        public static string ComputePolicyHash(ReplayAcceptancePolicyOptions options)
        {
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                options ?? ReplayAcceptancePolicyOptions.ProductionDefault(),
                ReplayJson.Options));
        }
    }

    public sealed class ReplayAcceptancePolicyOptions
    {
        public int Version { get; set; } = 2;
        public int MinimumValidSamples { get; set; } = 1;
        public int MaximumInvalidSampleCount { get; set; } = int.MaxValue;
        public double? MaximumInvalidSampleRate { get; set; }
        public int MaximumNewMissedDetections { get; set; }
        public double? MaximumCandidateMissedDetectionRate { get; set; }
        public int? MaximumNewFalseRejects { get; set; }
        public double? MaximumFalseRejectRateIncrease { get; set; }
        public double? MinimumCandidateAccuracy { get; set; }
        public long? MaximumCandidateP95ElapsedMs { get; set; }
        public double? MaximumP95InferenceIncreaseRatio { get; set; }

        public ReplayAcceptancePolicyOptions Clone()
        {
            return new ReplayAcceptancePolicyOptions
            {
                Version = Version,
                MinimumValidSamples = MinimumValidSamples,
                MaximumInvalidSampleCount = MaximumInvalidSampleCount,
                MaximumInvalidSampleRate = MaximumInvalidSampleRate,
                MaximumNewMissedDetections = MaximumNewMissedDetections,
                MaximumCandidateMissedDetectionRate = MaximumCandidateMissedDetectionRate,
                MaximumNewFalseRejects = MaximumNewFalseRejects,
                MaximumFalseRejectRateIncrease = MaximumFalseRejectRateIncrease,
                MinimumCandidateAccuracy = MinimumCandidateAccuracy,
                MaximumCandidateP95ElapsedMs = MaximumCandidateP95ElapsedMs,
                MaximumP95InferenceIncreaseRatio = MaximumP95InferenceIncreaseRatio
            };
        }

        public static bool IsSupportedVersion(int version)
        {
            return version == 1 || version == 2;
        }

        public static ReplayAcceptancePolicyOptions ProductionDefault()
        {
            return new ReplayAcceptancePolicyOptions
            {
                Version = 2,
                MinimumValidSamples = 1,
                MaximumNewMissedDetections = 0
            };
        }
    }
}
