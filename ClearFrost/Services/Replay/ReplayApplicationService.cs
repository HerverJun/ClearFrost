using System;
using System.Collections.Generic;
using System.Linq;
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

        public ReplayApplicationService(
            IReplayDatasetStore datasetStore,
            IReplayInferenceRunner inferenceRunner,
            IReplayModelValidator modelValidator,
            IReplayRunStore runStore)
        {
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _inferenceRunner = inferenceRunner ?? throw new ArgumentNullException(nameof(inferenceRunner));
            _modelValidator = modelValidator ?? throw new ArgumentNullException(nameof(modelValidator));
            _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
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
                Status = ReplayRunStatuses.Running,
                DatasetId = dataset.DatasetId,
                DatasetHash = dataset.DatasetHash,
                BaselineModel = request.BaselineModel,
                CandidateModel = request.CandidateModel,
                StartedAt = DateTimeOffset.UtcNow
            };

            await _runStore.RecordRunStartedAsync(report, cancellationToken).ConfigureAwait(false);
            Publish(progress, runId, "validate", 0, dataset.Samples.Count, "Validating replay models");

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
                    request.BaselineModel,
                    dataset,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                Dictionary<string, ReplayInferenceOutput> candidateOutputs = await RunModelAsync(
                    runId,
                    "candidate",
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
                Publish(progress, runId, "completed", dataset.Samples.Count, dataset.Samples.Count, "Replay completed");
                return report;
            }
            catch (OperationCanceledException)
            {
                await _runStore.RecordRunCanceledAsync(runId, CancellationToken.None).ConfigureAwait(false);
                Publish(progress, runId, "canceled", 0, dataset.Samples.Count, "Replay canceled");
                throw;
            }
            catch (Exception ex)
            {
                await _runStore.RecordRunFailedAsync(runId, ex.Message, CancellationToken.None).ConfigureAwait(false);
                Publish(progress, runId, "failed", 0, dataset.Samples.Count, ex.Message);
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
                    Status = ReplayRunStatuses.Running,
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
                        StringComparison.Ordinal)
                };
                comparison.Classification = ReplayMetrics.Classify(comparison);
                comparisons.Add(comparison);
            }

            return comparisons;
        }

        private static void Publish(
            IProgress<ReplayRunProgress>? progress,
            string runId,
            string phase,
            int completed,
            int total,
            string message)
        {
            progress?.Report(new ReplayRunProgress
            {
                RunId = runId,
                Status = string.Equals(phase, "completed", StringComparison.OrdinalIgnoreCase)
                    ? ReplayRunStatuses.Completed
                    : string.Equals(phase, "failed", StringComparison.OrdinalIgnoreCase)
                        ? ReplayRunStatuses.Failed
                        : string.Equals(phase, "canceled", StringComparison.OrdinalIgnoreCase)
                            ? ReplayRunStatuses.Canceled
                            : ReplayRunStatuses.Running,
                Phase = phase,
                CompletedSamples = completed,
                TotalSamples = total,
                Message = message
            });
        }
    }

    public sealed class ReplayAcceptancePolicy
    {
        public ReplayApprovalDecision Evaluate(ReplayRunReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            var reasons = new List<string>();
            if (!string.Equals(report.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
            {
                reasons.Add($"Replay status is {report.Status}.");
            }

            if (report.Metrics.CandidateNewMissedDetectionCount > 0)
            {
                reasons.Add($"Candidate introduced missed detections: {report.Metrics.CandidateNewMissedDetectionCount}.");
            }

            if (report.Metrics.CandidateNewFalseRejectCount > 0)
            {
                reasons.Add($"Candidate introduced false rejects: {report.Metrics.CandidateNewFalseRejectCount}.");
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
    }
}
