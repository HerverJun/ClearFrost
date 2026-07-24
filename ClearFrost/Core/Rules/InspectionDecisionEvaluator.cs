// ============================================================================
// 文件名: InspectionDecisionEvaluator.cs
// 描述:   生产与 Replay 共用的检测判定入口
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClearFrost.Yolo;

namespace ClearFrost.Core.Rules
{
    public sealed class InspectionDecisionRequest
    {
        public InspectionRuleSet RuleSet { get; init; } = new InspectionRuleSet();
        public IReadOnlyList<YoloResult> Detections { get; init; } = Array.Empty<YoloResult>();
        public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }
        public float[]? Roi { get; init; }
        public string ModelName { get; init; } = string.Empty;
        public YoloPreprocessingMode PreprocessingMode { get; init; } = YoloPreprocessingMode.StandardLetterBox;
        public float Confidence { get; init; }
        public float IouThreshold { get; init; }
        public long ElapsedMs { get; init; }
    }

    public sealed class InspectionDecisionResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public InspectionJudgeResult JudgeResult { get; init; } = new InspectionJudgeResult();
        public IReadOnlyList<YoloResult> FilteredDetections { get; init; } = Array.Empty<YoloResult>();
    }

    public interface IInspectionDecisionEvaluator
    {
        InspectionDecisionResult Evaluate(InspectionDecisionRequest request);

        VisionDebugSnapshot Explain(InspectionDecisionRequest request);

        VisionDebugSnapshot EvaluateWithDebug(InspectionDecisionRequest request);

        MultiModelCandidateEvaluator CreateCandidateEvaluator(
            InspectionRuleSet ruleSet,
            int imageWidth,
            int imageHeight,
            float[]? roi);
    }

    public sealed class InspectionDecisionEvaluator : IInspectionDecisionEvaluator
    {
        public InspectionDecisionResult Evaluate(InspectionDecisionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            IReadOnlyList<YoloResult> detections = request.Detections ?? Array.Empty<YoloResult>();
            IReadOnlyList<string> labels = request.Labels ?? Array.Empty<string>();
            if (!TryFilterByRoi(
                    detections,
                    request.ImageWidth,
                    request.ImageHeight,
                    request.Roi,
                    out IReadOnlyList<YoloResult> filtered,
                    out _,
                    out string errorCode,
                    out string message))
            {
                return new InspectionDecisionResult
                {
                    Succeeded = false,
                    ErrorCode = errorCode,
                    Message = message,
                    JudgeResult = FailClosedJudge(message),
                    FilteredDetections = Array.Empty<YoloResult>()
                };
            }

            InspectionJudgeResult judgeResult = InspectionRuleEngine.Evaluate(
                request.RuleSet,
                filtered,
                labels);

            return new InspectionDecisionResult
            {
                Succeeded = true,
                JudgeResult = judgeResult,
                FilteredDetections = filtered
            };
        }

        public VisionDebugSnapshot Explain(InspectionDecisionRequest request) => EvaluateWithDebug(request);

        public VisionDebugSnapshot EvaluateWithDebug(InspectionDecisionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            IReadOnlyList<YoloResult> detections = request.Detections ?? Array.Empty<YoloResult>();
            IReadOnlyList<string> labels = request.Labels ?? Array.Empty<string>();
            if (!TryFilterByRoi(
                    detections,
                    request.ImageWidth,
                    request.ImageHeight,
                    request.Roi,
                    out IReadOnlyList<YoloResult> filtered,
                    out IReadOnlyList<YoloResult> excluded,
                    out string errorCode,
                    out string message))
            {
                return VisionDebugSnapshot.From(
                    request,
                    detections,
                    Array.Empty<YoloResult>(),
                    detections,
                    FailClosedJudge(message),
                    false,
                    errorCode,
                    message);
            }

            InspectionJudgeResult judgeResult = InspectionRuleEngine.Evaluate(
                request.RuleSet,
                filtered,
                labels);

            return VisionDebugSnapshot.From(
                request,
                detections,
                filtered,
                excluded,
                judgeResult,
                true,
                string.Empty,
                string.Empty);
        }

        public MultiModelCandidateEvaluator CreateCandidateEvaluator(
            InspectionRuleSet ruleSet,
            int imageWidth,
            int imageHeight,
            float[]? roi)
        {
            return candidate =>
            {
                InspectionDecisionResult decision = Evaluate(new InspectionDecisionRequest
                {
                    RuleSet = ruleSet,
                    Detections = candidate.Results?.ToList() ?? new List<YoloResult>(),
                    Labels = candidate.Labels ?? Array.Empty<string>(),
                    ImageWidth = imageWidth,
                    ImageHeight = imageHeight,
                    Roi = roi
                });

                return new MultiModelCandidateEvaluation
                {
                    IsMatch = decision.Succeeded && decision.JudgeResult.IsQualified,
                    Score = decision.Succeeded
                        ? ScoreRuleCandidate(decision.JudgeResult, decision.FilteredDetections.Count)
                        : int.MinValue,
                    Summary = decision.Succeeded ? decision.JudgeResult.Summary : decision.Message
                };
            };
        }

        private static bool TryFilterByRoi(
            IReadOnlyList<YoloResult> results,
            int imageWidth,
            int imageHeight,
            float[]? roi,
            out IReadOnlyList<YoloResult> filtered,
            out IReadOnlyList<YoloResult> excluded,
            out string errorCode,
            out string message)
        {
            filtered = results;
            excluded = Array.Empty<YoloResult>();
            errorCode = string.Empty;
            message = string.Empty;

            if (roi == null)
            {
                return true;
            }

            if (roi.Length != 4 ||
                imageWidth <= 0 ||
                imageHeight <= 0 ||
                roi.Any(value => float.IsNaN(value) || float.IsInfinity(value)) ||
                roi[0] < 0f ||
                roi[1] < 0f ||
                roi[2] <= 0.001f ||
                roi[3] <= 0.001f ||
                roi[0] + roi[2] > 1.0005f ||
                roi[1] + roi[3] > 1.0005f)
            {
                errorCode = "InvalidRoi";
                message = "ROI is invalid; decision failed closed.";
                filtered = Array.Empty<YoloResult>();
                excluded = results;
                return false;
            }

            float roiX = roi[0] * imageWidth;
            float roiY = roi[1] * imageHeight;
            float roiW = roi[2] * imageWidth;
            float roiH = roi[3] * imageHeight;

            Debug.WriteLine($"[ROI过滤] ROI区域: X={roiX:F0}, Y={roiY:F0}, W={roiW:F0}, H={roiH:F0}");
            var inside = new List<YoloResult>();
            var outside = new List<YoloResult>();
            foreach (YoloResult result in results)
            {
                if (result.DataKind == YoloResultDataKind.Classification)
                {
                    inside.Add(result);
                    continue;
                }

                float centerX = result.CenterX;
                float centerY = result.CenterY;
                bool inRoi = centerX >= roiX && centerX <= roiX + roiW &&
                             centerY >= roiY && centerY <= roiY + roiH;
                if (inRoi)
                {
                    inside.Add(result);
                    continue;
                }

                outside.Add(result);
                Debug.WriteLine($"[ROI过滤] 过滤掉: 中心点({centerX:F0},{centerY:F0}) 不在ROI内");
            }

            filtered = inside;
            excluded = outside;
            Debug.WriteLine($"[ROI过滤] 过滤前: {results.Count} 个, 过滤后: {filtered.Count} 个");
            return true;
        }

        private static InspectionJudgeResult FailClosedJudge(string message)
        {
            string reason = string.IsNullOrWhiteSpace(message) ? "判定失败，按 NG 处理" : message;
            return new InspectionJudgeResult
            {
                IsQualified = false,
                Summary = reason,
                PrimaryReason = reason,
                Details = new[] { reason },
                RuleResults = Array.Empty<InspectionRuleResult>()
            };
        }

        private static int ScoreRuleCandidate(InspectionJudgeResult judgeResult, int filteredCount)
        {
            int matchedRules = judgeResult.RuleResults.Count(result => result.IsMatch);
            int failedRules = judgeResult.RuleResults.Count - matchedRules;
            int score = matchedRules * 1000 - failedRules * 100 + Math.Min(filteredCount, 100);
            return judgeResult.IsQualified ? score + 1_000_000 : score;
        }
    }
}
