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
                RecipeHash = FileReplayDatasetStore.ComputeRecipeHash(dataset.Recipe),
                RuleSetHash = FileReplayDatasetStore.ComputeRuleSetHash(dataset.Recipe.RuleSetJson),
                PolicyHash = _policy.PolicyHash,
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
                ReplayInferenceOutput output = await session.RunAsync(sample, cancellationToken).ConfigureAwait(false);
                outputs[sample.SampleId] = output;

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
                if (!baselineOutputs.TryGetValue(sample.SampleId, out ReplayInferenceOutput? baseline))
                {
                    throw new InvalidOperationException($"Baseline output missing for sample {sample.SampleId}.");
                }

                if (!candidateOutputs.TryGetValue(sample.SampleId, out ReplayInferenceOutput? candidate))
                {
                    throw new InvalidOperationException($"Candidate output missing for sample {sample.SampleId}.");
                }

                var comparison = new ReplaySampleComparison
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    GroundTruth = ReplayMetrics.Normalize(sample.GroundTruth),
                    BaselineDecision = ReplayMetrics.Normalize(baseline.Decision),
                    CandidateDecision = ReplayMetrics.Normalize(candidate.Decision),
                    DecisionChanged = !string.Equals(
                        ReplayMetrics.Normalize(baseline.Decision),
                        ReplayMetrics.Normalize(candidate.Decision),
                        StringComparison.Ordinal),
                    BaselineElapsedMs = baseline.ElapsedMs,
                    CandidateElapsedMs = candidate.ElapsedMs,
                    BaselineRuleSummary = baseline.RuleSummary,
                    CandidateRuleSummary = candidate.RuleSummary
                };
                comparison.Classification = ReplayMetrics.Classify(comparison);
                comparisons.Add(comparison);
            }

            return comparisons;
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
            if (report == null) throw new ArgumentNullException(nameof(report));

            var reasons = new List<string>();
            if (!string.Equals(report.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
            {
                reasons.Add($"Replay status is {report.Status}.");
            }

            if (report.Metrics.CandidateNewMissedDetectionCount > _options.MaximumNewMissedDetections)
            {
                reasons.Add($"Candidate introduced missed detections: {report.Metrics.CandidateNewMissedDetectionCount}.");
            }

            if (_options.MaximumNewFalseRejects.HasValue &&
                report.Metrics.CandidateNewFalseRejectCount > _options.MaximumNewFalseRejects.Value)
            {
                reasons.Add($"Candidate introduced false rejects: {report.Metrics.CandidateNewFalseRejectCount}.");
            }

            if (_options.MinimumCandidateAccuracy.HasValue &&
                report.Metrics.CandidateAccuracy < _options.MinimumCandidateAccuracy.Value)
            {
                reasons.Add($"Candidate accuracy {report.Metrics.CandidateAccuracy:P2} is below policy {_options.MinimumCandidateAccuracy.Value:P2}.");
            }

            if (_options.MaximumCandidateP95ElapsedMs.HasValue &&
                report.Metrics.CandidateP95ElapsedMs > _options.MaximumCandidateP95ElapsedMs.Value)
            {
                reasons.Add($"Candidate P95 latency {report.Metrics.CandidateP95ElapsedMs}ms exceeds policy {_options.MaximumCandidateP95ElapsedMs.Value}ms.");
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

        public static string ComputePolicyHash(ReplayAcceptancePolicyOptions options)
        {
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
                options ?? ReplayAcceptancePolicyOptions.ProductionDefault(),
                ReplayJson.Options));
        }
    }

    public sealed class ReplayAcceptancePolicyOptions
    {
        public int Version { get; set; } = 1;
        public int MaximumNewMissedDetections { get; set; }
        public int? MaximumNewFalseRejects { get; set; }
        public double? MinimumCandidateAccuracy { get; set; }
        public long? MaximumCandidateP95ElapsedMs { get; set; }

        public static ReplayAcceptancePolicyOptions ProductionDefault()
        {
            return new ReplayAcceptancePolicyOptions
            {
                Version = 1,
                MaximumNewMissedDetections = 0
            };
        }
    }
}
