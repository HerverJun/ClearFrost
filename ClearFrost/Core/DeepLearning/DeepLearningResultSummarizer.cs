// ============================================================================
// 文件名: DeepLearningResultSummarizer.cs
// 描述:   深度学习多任务结果摘要生成器
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClearFrost.Yolo;
using OpenCvSharp;

namespace ClearFrost.Core.DeepLearning
{
    public static class DeepLearningResultSummarizer
    {
        public const int DefaultClassificationTopK = 5;
        public const float DefaultMaskThreshold = 0.5f;
        public const float DefaultLowKeyPointConfidence = 0.5f;

        public static DeepLearningTraceSummary CreateTraceSummary(
            IReadOnlyList<YoloResult>? results,
            IReadOnlyList<string>? labels)
        {
            List<YoloResult> resultList = results?.ToList() ?? new List<YoloResult>();
            bool hasMaskData = resultList.Any(result => result.MaskData != null && !result.MaskData.Empty());
            return new DeepLearningTraceSummary
            {
                Classification = CreateClassificationSummary(resultList, labels),
                Segmentation = CreateSegmentationSummary(resultList, labels, includeInstancesWithoutMask: hasMaskData),
                Obb = CreateObbSummary(resultList, labels),
                Pose = CreatePoseSummary(resultList, labels)
            };
        }

        public static ClassificationResultSummary CreateClassificationSummary(
            IReadOnlyList<YoloResult>? results,
            IReadOnlyList<string>? labels,
            int topK = DefaultClassificationTopK)
        {
            List<ClassificationTopKItem> items = (results ?? Array.Empty<YoloResult>())
                .Where(result => result.DataKind == YoloResultDataKind.Classification)
                .OrderByDescending(result => result.Confidence)
                .ThenBy(result => result.ClassId)
                .Take(Math.Max(1, topK))
                .Select(result => new ClassificationTopKItem
                {
                    ClassId = result.ClassId,
                    Label = ResolveLabel(result.ClassId, labels),
                    Confidence = result.Confidence
                })
                .ToList();

            if (items.Count == 0)
            {
                return new ClassificationResultSummary
                {
                    Message = "未找到分类结果"
                };
            }

            ClassificationTopKItem top1 = items[0];
            return new ClassificationResultSummary
            {
                Top1Label = top1.Label,
                Top1ClassId = top1.ClassId,
                Top1Confidence = top1.Confidence,
                TopK = items,
                Message = $"Top1 {top1.Label}，置信度 {top1.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}"
            };
        }

        public static SegmentationResultSummary CreateSegmentationSummary(
            IReadOnlyList<YoloResult>? results,
            IReadOnlyList<string>? labels,
            float maskThreshold = DefaultMaskThreshold,
            bool includeInstancesWithoutMask = true)
        {
            IEnumerable<YoloResult> source = results ?? Array.Empty<YoloResult>();
            if (!includeInstancesWithoutMask)
            {
                source = source.Where(result => result.MaskData != null && !result.MaskData.Empty());
            }

            List<SegmentationInstanceSummary> instances = source
                .Where(result => result.DataKind != YoloResultDataKind.Classification)
                .Select((result, index) =>
                {
                    MaskMeasurement measurement = MeasureMask(result.MaskData, maskThreshold);
                    return new SegmentationInstanceSummary
                    {
                        Index = index + 1,
                        ClassId = result.ClassId,
                        Label = ResolveLabel(result.ClassId, labels),
                        Confidence = result.Confidence,
                        CenterX = result.CenterX,
                        CenterY = result.CenterY,
                        Width = result.Width,
                        Height = result.Height,
                        MaskArea = measurement.Area,
                        MaskCoverage = measurement.Coverage,
                        HasMask = measurement.HasMask
                    };
                })
                .ToList();

            return new SegmentationResultSummary
            {
                InstanceCount = instances.Count,
                Instances = instances,
                CoverageBasis = "MaskDataPixels",
                Message = instances.Count == 0
                    ? "未找到分割结果"
                    : $"分割目标 {instances.Count} 个，覆盖率基于 MaskData 像素统计"
            };
        }

        public static ObbResultSummary CreateObbSummary(
            IReadOnlyList<YoloResult>? results,
            IReadOnlyList<string>? labels)
        {
            List<ObbInstanceSummary> instances = (results ?? Array.Empty<YoloResult>())
                .Where(result => result.DataKind == YoloResultDataKind.Obb || result.Angle.HasValue)
                .Select((result, index) => new ObbInstanceSummary
                {
                    Index = index + 1,
                    ClassId = result.ClassId,
                    Label = ResolveLabel(result.ClassId, labels),
                    Confidence = result.Confidence,
                    CenterX = result.CenterX,
                    CenterY = result.CenterY,
                    Width = result.Width,
                    Height = result.Height,
                    Angle = result.Angle,
                    Message = result.Angle.HasValue
                        ? $"旋转框：label={ResolveLabel(result.ClassId, labels)}，角度 {result.Angle.Value.ToString("0.###", CultureInfo.InvariantCulture)}°，置信度 {result.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}"
                        : $"旋转框：label={ResolveLabel(result.ClassId, labels)}，角度未提供，置信度 {result.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}"
                })
                .ToList();

            return new ObbResultSummary
            {
                InstanceCount = instances.Count,
                Instances = instances,
                Message = instances.Count == 0 ? "未找到旋转框结果" : $"旋转框 {instances.Count} 个"
            };
        }

        public static PoseResultSummary CreatePoseSummary(
            IReadOnlyList<YoloResult>? results,
            IReadOnlyList<string>? labels,
            float lowConfidenceThreshold = DefaultLowKeyPointConfidence)
        {
            List<PoseInstanceSummary> instances = (results ?? Array.Empty<YoloResult>())
                .Where(result => result.KeyPoints != null && result.KeyPoints.Length > 0)
                .Select((result, index) =>
                {
                    PosePoint[] keyPoints = result.KeyPoints ?? Array.Empty<PosePoint>();
                    float max = keyPoints.Select(point => point.Score).DefaultIfEmpty(0f).Max();
                    float min = keyPoints.Select(point => point.Score).DefaultIfEmpty(0f).Min();
                    int lowCount = keyPoints.Count(point => point.Score < lowConfidenceThreshold);
                    return new PoseInstanceSummary
                    {
                        Index = index + 1,
                        ClassId = result.ClassId,
                        Label = ResolveLabel(result.ClassId, labels),
                        Confidence = result.Confidence,
                        KeyPointCount = keyPoints.Length,
                        MaxKeyPointConfidence = max,
                        MinKeyPointConfidence = min,
                        LowConfidenceKeyPointCount = lowCount
                    };
                })
                .ToList();

            List<float> allScores = (results ?? Array.Empty<YoloResult>())
                .SelectMany(result => result.KeyPoints ?? Array.Empty<PosePoint>())
                .Select(point => point.Score)
                .ToList();

            return new PoseResultSummary
            {
                InstanceCount = instances.Count,
                TotalKeyPointCount = instances.Sum(instance => instance.KeyPointCount),
                MaxKeyPointConfidence = allScores.DefaultIfEmpty(0f).Max(),
                MinKeyPointConfidence = allScores.DefaultIfEmpty(0f).Min(),
                LowConfidenceKeyPointCount = allScores.Count(score => score < lowConfidenceThreshold),
                Instances = instances,
                Message = instances.Count == 0 ? "未找到姿态关键点结果" : $"姿态目标 {instances.Count} 个，关键点 {instances.Sum(instance => instance.KeyPointCount)} 个"
            };
        }

        public static MaskMeasurement MeasureMask(Mat? maskData, float threshold = DefaultMaskThreshold)
        {
            if (maskData == null || maskData.Empty())
            {
                return new MaskMeasurement(0, 0, false);
            }

            using var floatMask = new Mat();
            Mat source = maskData;
            if (maskData.Type() != MatType.CV_32FC1)
            {
                maskData.ConvertTo(floatMask, MatType.CV_32F);
                source = floatMask;
            }

            double area = 0;
            for (int row = 0; row < source.Rows; row++)
            {
                for (int col = 0; col < source.Cols; col++)
                {
                    float value = source.At<float>(row, col);
                    if (!float.IsNaN(value) && !float.IsInfinity(value) && value > threshold)
                    {
                        area++;
                    }
                }
            }

            double totalPixels = Math.Max(1, source.Rows * source.Cols);
            return new MaskMeasurement(area, area / totalPixels, true);
        }

        public static string ResolveLabel(int classId, IReadOnlyList<string>? labels)
        {
            return labels != null && classId >= 0 && classId < labels.Count
                ? labels[classId]
                : $"Class_{classId}";
        }

        public static string CreateTaskAwareLogSummary(
            IReadOnlyList<YoloResult>? results,
            IReadOnlyList<string>? labels,
            bool? isQualified = null,
            string? judgementReason = null)
        {
            List<YoloResult> resultList = results?.ToList() ?? new List<YoloResult>();
            if (resultList.Count == 0)
            {
                return "未检测到目标";
            }

            string judgement = FormatJudgement(isQualified, judgementReason);
            if (resultList.Any(result => result.DataKind == YoloResultDataKind.Classification))
            {
                ClassificationResultSummary classification = CreateClassificationSummary(resultList, labels);
                return $"分类结果：Top1={classification.Top1Label}，Confidence={classification.Top1Confidence.ToString("0.00", CultureInfo.InvariantCulture)}{judgement}";
            }

            if (resultList.Any(result => result.MaskData != null && !result.MaskData.Empty()))
            {
                SegmentationResultSummary segmentation = CreateSegmentationSummary(resultList, labels);
                SegmentationInstanceSummary? first = segmentation.Instances.FirstOrDefault();
                string measurement = first == null
                    ? "未找到分割实例"
                    : $"{first.Label} 实例 {segmentation.InstanceCount} 个，面积 {first.MaskArea.ToString("0", CultureInfo.InvariantCulture)}，覆盖率 {(first.MaskCoverage * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";
                return $"分割结果：{measurement}{judgement}";
            }

            if (resultList.Any(result => result.DataKind == YoloResultDataKind.Obb || result.Angle.HasValue))
            {
                ObbResultSummary obb = CreateObbSummary(resultList, labels);
                ObbInstanceSummary? first = obb.Instances.FirstOrDefault();
                string angleText = first?.Angle == null
                    ? "角度未提供"
                    : $"{first.Label} 角度 {first.Angle.Value.ToString("0.0", CultureInfo.InvariantCulture)}°";
                return $"旋转框结果：{angleText}{judgement}";
            }

            if (resultList.Any(result => result.KeyPoints != null && result.KeyPoints.Length > 0))
            {
                PoseResultSummary pose = CreatePoseSummary(resultList, labels);
                return $"姿态结果：目标 {pose.InstanceCount} 个，关键点 {pose.TotalKeyPointCount} 个，低置信度 {pose.LowConfidenceKeyPointCount} 个{judgement}";
            }

            string details = string.Join(", ", resultList.Take(8).Select(result =>
            {
                string label = ResolveLabel(result.ClassId, labels);
                return $"{label} {result.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}";
            }));
            string suffix = resultList.Count > 8 ? "..." : string.Empty;
            return $"检测结果：{resultList.Count} 个；{details}{suffix}";
        }

        private static string FormatJudgement(bool? isQualified, string? judgementReason)
        {
            if (!isQualified.HasValue)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append(isQualified.Value ? "，判定=OK" : "，判定=NG");
            if (!string.IsNullOrWhiteSpace(judgementReason))
            {
                builder.Append("，原因=");
                builder.Append(judgementReason.Trim());
            }

            return builder.ToString();
        }
    }
}
