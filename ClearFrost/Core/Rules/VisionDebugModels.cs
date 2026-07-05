// ============================================================================
// 文件名: VisionDebugModels.cs
// 描述:   视觉算法调试快照模型
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClearFrost.Core.DeepLearning;
using ClearFrost.Yolo;

namespace ClearFrost.Core.Rules
{
    public sealed class VisionDebugSnapshot
    {
        public bool Succeeded { get; set; } = true;
        public string ErrorCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public string ModelName { get; set; } = string.Empty;
        public YoloPreprocessingMode PreprocessingMode { get; set; } = YoloPreprocessingMode.StandardLetterBox;
        public float Confidence { get; set; }
        public float IouThreshold { get; set; }
        public float[]? Roi { get; set; }
        public bool RoiEnabled => Roi != null && Roi.Length == 4;
        public List<VisionDebugDetectionBox> AllDetections { get; set; } = new List<VisionDebugDetectionBox>();
        public List<VisionDebugDetectionBox> RoiIncludedDetections { get; set; } = new List<VisionDebugDetectionBox>();
        public List<VisionDebugDetectionBox> RoiExcludedDetections { get; set; } = new List<VisionDebugDetectionBox>();
        public List<VisionDebugCategoryStat> CategoryStats { get; set; } = new List<VisionDebugCategoryStat>();
        public InspectionJudgeResult JudgeResult { get; set; } = new InspectionJudgeResult();
        public List<VisionDebugRuleResult> RuleResults { get; set; } = new List<VisionDebugRuleResult>();
        public DeepLearningTraceSummary DeepLearningSummary { get; set; } = new DeepLearningTraceSummary();
        public ClassificationResultSummary ClassificationSummary { get; set; } = new ClassificationResultSummary();
        public SegmentationResultSummary SegmentationSummary { get; set; } = new SegmentationResultSummary();
        public ObbResultSummary ObbSummary { get; set; } = new ObbResultSummary();
        public PoseResultSummary PoseSummary { get; set; } = new PoseResultSummary();
        public bool FinalOk { get; set; }
        public string FinalResult => FinalOk ? "OK" : "NG";
        public string PrimaryFailureReason { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
        public VisionDebugComparison? Comparison { get; set; }
        public VisionDebugParameterComparison? ParameterComparison { get; set; }
        public string ImageSourceKind { get; set; } = "Original";
        public string ImageSourceWarning { get; set; } = string.Empty;

        public static VisionDebugSnapshot From(
            InspectionDecisionRequest request,
            IReadOnlyList<YoloResult> allDetections,
            IReadOnlyList<YoloResult> roiIncluded,
            IReadOnlyList<YoloResult> roiExcluded,
            InspectionJudgeResult judgeResult,
            bool succeeded,
            string errorCode,
            string message)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            IReadOnlyList<string> labels = request.Labels ?? Array.Empty<string>();
            List<VisionDebugDetectionBox> allBoxes = BuildBoxes(allDetections, labels, roiIncluded, roiExcluded);
            var includedKeys = new HashSet<string>(roiIncluded.Select(BoxKey), StringComparer.Ordinal);
            var excludedKeys = new HashSet<string>(roiExcluded.Select(BoxKey), StringComparer.Ordinal);
            DeepLearningTraceSummary deepLearningSummary = DeepLearningResultSummarizer.CreateTraceSummary(allDetections, labels);

            return new VisionDebugSnapshot
            {
                Succeeded = succeeded,
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty,
                ImageWidth = request.ImageWidth,
                ImageHeight = request.ImageHeight,
                ModelName = request.ModelName ?? string.Empty,
                PreprocessingMode = request.PreprocessingMode,
                Confidence = request.Confidence,
                IouThreshold = request.IouThreshold,
                Roi = request.Roi == null ? null : request.Roi.ToArray(),
                AllDetections = allBoxes,
                RoiIncludedDetections = allBoxes.Where(box => includedKeys.Contains(box.SourceKey)).ToList(),
                RoiExcludedDetections = allBoxes.Where(box => excludedKeys.Contains(box.SourceKey)).ToList(),
                CategoryStats = BuildCategoryStats(allBoxes),
                JudgeResult = judgeResult,
                RuleResults = BuildRuleResults(judgeResult, allBoxes, roiIncluded),
                DeepLearningSummary = deepLearningSummary,
                ClassificationSummary = deepLearningSummary.Classification,
                SegmentationSummary = deepLearningSummary.Segmentation,
                ObbSummary = deepLearningSummary.Obb,
                PoseSummary = deepLearningSummary.Pose,
                FinalOk = succeeded && judgeResult.IsQualified,
                PrimaryFailureReason = ResolvePrimaryFailureReason(succeeded, message ?? string.Empty, judgeResult),
                ElapsedMs = request.ElapsedMs
            };
        }

        private static List<VisionDebugDetectionBox> BuildBoxes(
            IReadOnlyList<YoloResult> allDetections,
            IReadOnlyList<string> labels,
            IReadOnlyList<YoloResult> roiIncluded,
            IReadOnlyList<YoloResult> roiExcluded)
        {
            var includedKeys = new HashSet<string>(roiIncluded.Select(BoxKey), StringComparer.Ordinal);
            var excludedKeys = new HashSet<string>(roiExcluded.Select(BoxKey), StringComparer.Ordinal);
            var boxes = new List<VisionDebugDetectionBox>();
            int index = 1;
            foreach (YoloResult detection in allDetections)
            {
                string key = BoxKey(detection);
                bool inRoi = includedKeys.Contains(key);
                bool filteredOut = excludedKeys.Contains(key);
                boxes.Add(VisionDebugDetectionBox.FromYoloResult(index, detection, ResolveLabel(detection, labels), inRoi, filteredOut, key));
                index++;
            }

            return boxes;
        }

        private static List<VisionDebugCategoryStat> BuildCategoryStats(IReadOnlyList<VisionDebugDetectionBox> boxes)
        {
            return boxes
                .GroupBy(box => new { box.ClassId, box.Label })
                .OrderBy(group => group.Key.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.ClassId)
                .Select(group => new VisionDebugCategoryStat
                {
                    ClassId = group.Key.ClassId,
                    Label = group.Key.Label,
                    TotalCount = group.Count(),
                    RoiIncludedCount = group.Count(box => box.InRoi),
                    RoiExcludedCount = group.Count(box => box.FilteredOutByRoi)
                })
                .ToList();
        }

        private static List<VisionDebugRuleResult> BuildRuleResults(
            InspectionJudgeResult judgeResult,
            IReadOnlyList<VisionDebugDetectionBox> allBoxes,
            IReadOnlyList<YoloResult> roiIncluded)
        {
            Dictionary<string, int> allBoxIndexes = allBoxes
                .GroupBy(box => box.SourceKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
            List<int> includedIndexes = roiIncluded
                .Select(BoxKey)
                .Where(key => allBoxIndexes.ContainsKey(key))
                .Select(key => allBoxIndexes[key])
                .ToList();

            return (judgeResult.RuleResults ?? Array.Empty<InspectionRuleResult>())
                .Select(result => new VisionDebugRuleResult
                {
                    RuleId = result.RuleId,
                    RuleName = result.RuleName,
                    RuleType = result.RuleType,
                    IsMatch = result.IsMatch,
                    Expected = result.Expected,
                    Actual = result.Actual,
                    Reason = result.Reason,
                    Message = result.Message,
                    AssociatedBoxIndexes = MapAssociatedIndexes(result.AssociatedBoxIndexes, includedIndexes),
                    AssociationSummary = BuildAssociationSummary(result.AssociationSummary, result.AssociatedBoxIndexes, includedIndexes)
                })
                .ToList();
        }

        private static List<int> MapAssociatedIndexes(
            IReadOnlyList<int> filteredIndexes,
            IReadOnlyList<int> includedIndexes)
        {
            if (filteredIndexes == null || filteredIndexes.Count == 0)
            {
                return new List<int>();
            }

            return filteredIndexes
                .Select(index => index > 0 && index <= includedIndexes.Count ? includedIndexes[index - 1] : index)
                .Distinct()
                .ToList();
        }

        private static string BuildAssociationSummary(
            string associationSummary,
            IReadOnlyList<int> filteredIndexes,
            IReadOnlyList<int> includedIndexes)
        {
            List<int> mapped = MapAssociatedIndexes(filteredIndexes, includedIndexes);
            if (mapped.Count == 0)
            {
                return string.IsNullOrWhiteSpace(associationSummary) ? "关联目标框: 无" : associationSummary;
            }

            string mappedText = $"关联目标框: {string.Join(", ", mapped.Select(index => $"#{index}"))}";
            return string.IsNullOrWhiteSpace(associationSummary)
                ? mappedText
                : $"{associationSummary}；{mappedText}";
        }

        private static string ResolvePrimaryFailureReason(bool succeeded, string message, InspectionJudgeResult judgeResult)
        {
            if (!succeeded)
            {
                return string.IsNullOrWhiteSpace(message) ? "调试判定失败，按 NG 处理" : message;
            }

            if (judgeResult.IsQualified)
            {
                return string.Empty;
            }

            return !string.IsNullOrWhiteSpace(judgeResult.PrimaryReason)
                ? judgeResult.PrimaryReason
                : judgeResult.RuleResults.FirstOrDefault(result => !result.IsMatch)?.Message
                    ?? judgeResult.Summary
                    ?? "规则判定 NG";
        }

        private static string ResolveLabel(YoloResult detection, IReadOnlyList<string> labels)
        {
            return detection.ClassId >= 0 && detection.ClassId < labels.Count
                ? labels[detection.ClassId]
                : $"Class_{detection.ClassId}";
        }

        private static string BoxKey(YoloResult result)
        {
            string angle = result.Angle.HasValue
                ? result.Angle.Value.ToString("R", CultureInfo.InvariantCulture)
                : string.Empty;
            return string.Join(
                "|",
                result.ClassId.ToString(CultureInfo.InvariantCulture),
                result.CenterX.ToString("R", CultureInfo.InvariantCulture),
                result.CenterY.ToString("R", CultureInfo.InvariantCulture),
                result.Width.ToString("R", CultureInfo.InvariantCulture),
                result.Height.ToString("R", CultureInfo.InvariantCulture),
                result.Confidence.ToString("R", CultureInfo.InvariantCulture),
                angle,
                result.DataKind.ToString());
        }
    }

    public sealed class VisionDebugDetectionBox
    {
        public int Index { get; set; }
        public int ClassId { get; set; }
        public string Label { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float? Angle { get; set; }
        public string DataKind { get; set; } = string.Empty;
        public bool HasMask { get; set; }
        public double MaskArea { get; set; }
        public double MaskCoverage { get; set; }
        public int KeyPointCount { get; set; }
        public float MaxKeyPointConfidence { get; set; }
        public float MinKeyPointConfidence { get; set; }
        public int LowConfidenceKeyPointCount { get; set; }
        public bool InRoi { get; set; }
        public bool FilteredOutByRoi { get; set; }
        public string SourceKey { get; set; } = string.Empty;

        public static VisionDebugDetectionBox FromYoloResult(
            int index,
            YoloResult result,
            string label,
            bool inRoi,
            bool filteredOutByRoi,
            string sourceKey)
        {
            MaskMeasurement mask = DeepLearningResultSummarizer.MeasureMask(result.MaskData);
            PosePoint[] keyPoints = result.KeyPoints ?? Array.Empty<PosePoint>();
            return new VisionDebugDetectionBox
            {
                Index = index,
                ClassId = result.ClassId,
                Label = label ?? string.Empty,
                Confidence = result.Confidence,
                CenterX = result.CenterX,
                CenterY = result.CenterY,
                X = result.Left,
                Y = result.Top,
                Width = result.Width,
                Height = result.Height,
                Angle = result.Angle,
                DataKind = result.DataKind.ToString(),
                HasMask = mask.HasMask,
                MaskArea = mask.Area,
                MaskCoverage = mask.Coverage,
                KeyPointCount = keyPoints.Length,
                MaxKeyPointConfidence = keyPoints.Select(point => point.Score).DefaultIfEmpty(0f).Max(),
                MinKeyPointConfidence = keyPoints.Select(point => point.Score).DefaultIfEmpty(0f).Min(),
                LowConfidenceKeyPointCount = keyPoints.Count(point => point.Score < DeepLearningResultSummarizer.DefaultLowKeyPointConfidence),
                InRoi = inRoi,
                FilteredOutByRoi = filteredOutByRoi,
                SourceKey = sourceKey ?? string.Empty
            };
        }
    }

    public sealed class VisionDebugCategoryStat
    {
        public int ClassId { get; set; }
        public string Label { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public int RoiIncludedCount { get; set; }
        public int RoiExcludedCount { get; set; }
    }

    public sealed class VisionDebugRuleResult
    {
        public string RuleId { get; set; } = string.Empty;
        public string RuleName { get; set; } = string.Empty;
        public string RuleType { get; set; } = string.Empty;
        public bool IsMatch { get; set; }
        public string Expected { get; set; } = string.Empty;
        public string Actual { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<int> AssociatedBoxIndexes { get; set; } = new List<int>();
        public string AssociationSummary { get; set; } = string.Empty;
    }

    public sealed class VisionDebugComparison
    {
        public long RecordId { get; set; }
        public string InspectionId { get; set; } = string.Empty;
        public bool? OldIsQualified { get; set; }
        public string OldResult => OldIsQualified.HasValue ? (OldIsQualified.Value ? "OK" : "NG") : string.Empty;
        public bool NewIsQualified { get; set; }
        public string NewResult => NewIsQualified ? "OK" : "NG";
        public string OldPrimaryReason { get; set; } = string.Empty;
        public string NewPrimaryReason { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public bool UsedRenderedImage { get; set; }
        public string ImageSourceKind { get; set; } = "Original";
        public string ImageWarning { get; set; } = string.Empty;
    }

    public sealed class VisionDebugParameterComparison
    {
        public bool HasDifferences => Items.Any(item => item.IsDifferent);
        public List<VisionDebugParameterDiff> Items { get; set; } = new List<VisionDebugParameterDiff>();
    }

    public sealed class VisionDebugParameterDiff
    {
        public string Field { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ProductionValue { get; set; } = string.Empty;
        public string TrialValue { get; set; } = string.Empty;
        public bool IsDifferent { get; set; }
    }

    public sealed class VisionDebugBatchReplayItem
    {
        public long RecordId { get; set; }
        public string InspectionId { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public bool? OldIsQualified { get; set; }
        public bool? NewIsQualified { get; set; }
        public string OldResult => OldIsQualified.HasValue ? (OldIsQualified.Value ? "OK" : "NG") : string.Empty;
        public string NewResult => NewIsQualified.HasValue ? (NewIsQualified.Value ? "OK" : "NG") : string.Empty;
        public string Status { get; set; } = string.Empty;
        public string FailureReason { get; set; } = string.Empty;
        public string OldPrimaryReason { get; set; } = string.Empty;
        public string NewPrimaryReason { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public bool ImageMissing { get; set; }
        public bool UsedRenderedImage { get; set; }
        public string ImageSourceKind { get; set; } = "Original";
        public string ImageWarning { get; set; } = string.Empty;
    }

    public sealed class VisionDebugBatchReplaySummary
    {
        public int RequestedLimit { get; set; }
        public int EffectiveLimit { get; set; }
        public int TotalRecords { get; set; }
        public int CompletedCount { get; set; }
        public int OldOkCount { get; set; }
        public int OldNgCount { get; set; }
        public int NewOkCount { get; set; }
        public int NewNgCount { get; set; }
        public int ChangedCount { get; set; }
        public int NgToOkCount { get; set; }
        public int OkToNgCount { get; set; }
        public int MissingImageCount { get; set; }
        public int FailedCount { get; set; }
        public int RenderedFallbackCount { get; set; }
        public Dictionary<string, int> FailureReasonStats { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<VisionDebugBatchReplayItem> Items { get; set; } = new List<VisionDebugBatchReplayItem>();
    }
}
