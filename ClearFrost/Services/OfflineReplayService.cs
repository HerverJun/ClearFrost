using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Rules;
using ClearFrost.Interfaces;
using ClearFrost.Yolo;
using OpenCvSharp;

namespace ClearFrost.Services
{
    public sealed class OfflineReplayBatchResult
    {
        public IReadOnlyList<OfflineReplayResult> Results { get; init; } = Array.Empty<OfflineReplayResult>();
        public int InputCount { get; init; }
        public int ReplayedCount { get; init; }
        public int DifferenceCount { get; init; }
        public int SuccessCount { get; init; }
        public int InferenceFailedCount { get; init; }
        public int RuleFailedCount { get; init; }
        public int ImageMissingCount { get; init; }
        public int ImageReadFailedCount { get; init; }
        public double PassRate { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    public sealed class OfflineReplayResult
    {
        public long RecordId { get; init; }
        public string InspectionId { get; init; } = string.Empty;
        public string ImagePath { get; init; } = string.Empty;
        public bool OriginalIsQualified { get; init; }
        public bool? NewIsQualified { get; init; }
        public string Difference { get; init; } = string.Empty;
        public double Confidence { get; init; }
        public long ElapsedMs { get; init; }
        public string RuleReason { get; init; } = string.Empty;
        public string OriginalRuleSummary { get; init; } = string.Empty;
        public string NewRuleSummary { get; init; } = string.Empty;
        public string ModelName { get; init; } = string.Empty;
        public string RecipeVersion { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
    }

    public sealed class OfflineReplayService
    {
        private readonly IDatabaseService _databaseService;
        private readonly IDetectionService _detectionService;
        private readonly Func<InspectionRuleSet> _ruleSetProvider;
        private readonly Action<string>? _diagnosticLog;

        public OfflineReplayService(
            IDatabaseService databaseService,
            IDetectionService detectionService,
            Func<InspectionRuleSet> ruleSetProvider,
            Action<string>? diagnosticLog = null)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
            _ruleSetProvider = ruleSetProvider ?? throw new ArgumentNullException(nameof(ruleSetProvider));
            _diagnosticLog = diagnosticLog;
        }

        public async Task<OfflineReplayBatchResult> ReplayAsync(
            DetectionReplayQuery query,
            float confidence,
            float iouThreshold,
            CancellationToken cancellationToken = default)
        {
            query ??= new DetectionReplayQuery();
            List<DetectionRecord> records = await _databaseService.GetReplayRecordsAsync(query).ConfigureAwait(false);
            var results = new List<OfflineReplayResult>(records.Count);
            var errors = new List<string>();

            foreach (DetectionRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                OfflineReplayResult result = await ReplayRecordAsync(
                    record,
                    confidence,
                    iouThreshold,
                    cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    errors.Add($"{record.InspectionId}: {result.ErrorMessage}");
                }
            }

            int successCount = results.Count(result => string.Equals(result.Status, "Replayed", StringComparison.Ordinal));
            int passCount = results.Count(result =>
                string.Equals(result.Status, "Replayed", StringComparison.Ordinal) &&
                result.NewIsQualified == true);

            return new OfflineReplayBatchResult
            {
                Results = results,
                InputCount = records.Count,
                ReplayedCount = successCount,
                SuccessCount = successCount,
                DifferenceCount = results.Count(result =>
                    string.Equals(result.Status, "Replayed", StringComparison.Ordinal) &&
                    string.Equals(result.Difference, "ResultChanged", StringComparison.Ordinal)),
                InferenceFailedCount = results.Count(result => string.Equals(result.Status, "InferenceFailed", StringComparison.Ordinal)),
                RuleFailedCount = results.Count(result => string.Equals(result.Status, "RuleFailed", StringComparison.Ordinal)),
                ImageMissingCount = results.Count(result => string.Equals(result.Status, "ImageMissing", StringComparison.Ordinal)),
                ImageReadFailedCount = results.Count(result => string.Equals(result.Status, "ImageReadFailed", StringComparison.Ordinal)),
                PassRate = successCount == 0 ? 0 : passCount / (double)successCount,
                Errors = errors
            };
        }

        private async Task<OfflineReplayResult> ReplayRecordAsync(
            DetectionRecord record,
            float confidence,
            float iouThreshold,
            CancellationToken cancellationToken)
        {
            string imagePath = ResolveReplayImagePath(record);
            if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
            {
                string message = "历史样本图片不存在";
                _diagnosticLog?.Invoke($"[OfflineReplay] {record.InspectionId}: {message} {imagePath}");
                return BuildFailedResult(record, imagePath, "ImageMissing", message);
            }

            Mat? image = null;
            try
            {
                byte[] imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
                image = Cv2.ImDecode(imageBytes, ImreadModes.Color);
                if (image == null || image.Empty())
                {
                    return BuildFailedResult(record, imagePath, "ImageReadFailed", "历史样本图片读取失败");
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                DetectionResultData detection;
                using (await DetectionRuntimeConcurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    detection = await _detectionService.DetectAsync(
                        image,
                        confidence,
                        iouThreshold).ConfigureAwait(false);
                }
                stopwatch.Stop();

                if (detection.HasError)
                {
                    return new OfflineReplayResult
                    {
                        RecordId = record.Id,
                        InspectionId = record.InspectionId,
                        ImagePath = imagePath,
                        OriginalIsQualified = record.IsQualified,
                        NewIsQualified = null,
                        Difference = "InferenceFailed",
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                        OriginalRuleSummary = record.RuleSummary,
                        ModelName = detection.UsedModelName ?? _detectionService.CurrentModelName,
                        RecipeVersion = record.RecipeVersion,
                        Status = "InferenceFailed",
                        ErrorMessage = detection.ErrorMessage
                    };
                }

                List<YoloResult> detections = detection.Results ?? new List<YoloResult>();
                string[] labels = detection.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                InspectionJudgeResult judgeResult;
                try
                {
                    judgeResult = InspectionRuleEngine.Evaluate(_ruleSetProvider(), detections, labels);
                }
                catch (Exception ex)
                {
                    return new OfflineReplayResult
                    {
                        RecordId = record.Id,
                        InspectionId = record.InspectionId,
                        ImagePath = imagePath,
                        OriginalIsQualified = record.IsQualified,
                        NewIsQualified = null,
                        Difference = "RuleFailed",
                        Confidence = detections.Count == 0 ? 0 : detections.Max(item => item.Confidence),
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                        OriginalRuleSummary = record.RuleSummary,
                        ModelName = detection.UsedModelName ?? _detectionService.CurrentModelName,
                        RecipeVersion = record.RecipeVersion,
                        Status = "RuleFailed",
                        ErrorMessage = ex.Message
                    };
                }

                bool newQualified = !detection.HasError && judgeResult.IsQualified;
                string difference = record.IsQualified == newQualified ? "NoChange" : "ResultChanged";

                return new OfflineReplayResult
                {
                    RecordId = record.Id,
                    InspectionId = record.InspectionId,
                    ImagePath = imagePath,
                    OriginalIsQualified = record.IsQualified,
                    NewIsQualified = newQualified,
                    Difference = difference,
                    Confidence = detections.Count == 0 ? 0 : detections.Max(item => item.Confidence),
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    RuleReason = string.IsNullOrWhiteSpace(judgeResult.PrimaryReason) ? judgeResult.Summary : judgeResult.PrimaryReason,
                    OriginalRuleSummary = record.RuleSummary,
                    NewRuleSummary = judgeResult.Summary,
                    ModelName = detection.UsedModelName ?? _detectionService.CurrentModelName,
                    RecipeVersion = record.RecipeVersion,
                    Status = "Replayed",
                    ErrorMessage = string.Empty
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OpenCVException ex)
            {
                _diagnosticLog?.Invoke($"[OfflineReplay] {record.InspectionId}: {ex.Message}");
                return BuildFailedResult(record, imagePath, "ImageReadFailed", ex.Message);
            }
            catch (Exception ex)
            {
                _diagnosticLog?.Invoke($"[OfflineReplay] {record.InspectionId}: {ex.Message}");
                return BuildFailedResult(record, imagePath, "ReplayFailed", ex.Message);
            }
            finally
            {
                image?.Dispose();
            }
        }

        private static OfflineReplayResult BuildFailedResult(
            DetectionRecord record,
            string imagePath,
            string status,
            string message)
        {
            return new OfflineReplayResult
            {
                RecordId = record.Id,
                InspectionId = record.InspectionId,
                ImagePath = imagePath,
                OriginalIsQualified = record.IsQualified,
                NewIsQualified = null,
                Difference = status,
                OriginalRuleSummary = record.RuleSummary,
                ModelName = record.ModelName,
                RecipeVersion = record.RecipeVersion,
                Status = status,
                ErrorMessage = message
            };
        }

        private static string ResolveReplayImagePath(DetectionRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.ImagePath))
            {
                return record.ImagePath;
            }

            if (!string.IsNullOrWhiteSpace(record.TraceImagePath))
            {
                return record.TraceImagePath;
            }

            return record.RenderedImagePath ?? string.Empty;
        }
    }
}
