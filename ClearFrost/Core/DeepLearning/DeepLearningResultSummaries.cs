// ============================================================================
// 文件名: DeepLearningResultSummaries.cs
// 描述:   深度学习多任务结果摘要 DTO
// ============================================================================

using System;
using System.Collections.Generic;

namespace ClearFrost.Core.DeepLearning
{
    public sealed class ClassificationResultSummary
    {
        public string Top1Label { get; init; } = string.Empty;
        public int Top1ClassId { get; init; } = -1;
        public float Top1Confidence { get; init; }
        public IReadOnlyList<ClassificationTopKItem> TopK { get; init; } = Array.Empty<ClassificationTopKItem>();
        public string Message { get; init; } = string.Empty;
    }

    public sealed class ClassificationTopKItem
    {
        public int ClassId { get; init; }
        public string Label { get; init; } = string.Empty;
        public float Confidence { get; init; }
    }

    public sealed class SegmentationResultSummary
    {
        public int InstanceCount { get; init; }
        public IReadOnlyList<SegmentationInstanceSummary> Instances { get; init; } = Array.Empty<SegmentationInstanceSummary>();
        public string CoverageBasis { get; init; } = "MaskDataPixels";
        public string Message { get; init; } = string.Empty;
    }

    public sealed class SegmentationInstanceSummary
    {
        public int Index { get; init; }
        public int ClassId { get; init; }
        public string Label { get; init; } = string.Empty;
        public float Confidence { get; init; }
        public float CenterX { get; init; }
        public float CenterY { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public double MaskArea { get; init; }
        public double MaskCoverage { get; init; }
        public bool HasMask { get; init; }
    }

    public sealed class ObbResultSummary
    {
        public int InstanceCount { get; init; }
        public IReadOnlyList<ObbInstanceSummary> Instances { get; init; } = Array.Empty<ObbInstanceSummary>();
        public string Message { get; init; } = string.Empty;
    }

    public sealed class ObbInstanceSummary
    {
        public int Index { get; init; }
        public int ClassId { get; init; }
        public string Label { get; init; } = string.Empty;
        public float Confidence { get; init; }
        public float CenterX { get; init; }
        public float CenterY { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public float? Angle { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class PoseResultSummary
    {
        public int InstanceCount { get; init; }
        public int TotalKeyPointCount { get; init; }
        public float MaxKeyPointConfidence { get; init; }
        public float MinKeyPointConfidence { get; init; }
        public int LowConfidenceKeyPointCount { get; init; }
        public IReadOnlyList<PoseInstanceSummary> Instances { get; init; } = Array.Empty<PoseInstanceSummary>();
        public string Message { get; init; } = string.Empty;
    }

    public sealed class PoseInstanceSummary
    {
        public int Index { get; init; }
        public int ClassId { get; init; }
        public string Label { get; init; } = string.Empty;
        public float Confidence { get; init; }
        public int KeyPointCount { get; init; }
        public float MaxKeyPointConfidence { get; init; }
        public float MinKeyPointConfidence { get; init; }
        public int LowConfidenceKeyPointCount { get; init; }
    }

    public sealed class DeepLearningTraceSummary
    {
        public ClassificationResultSummary Classification { get; init; } = new ClassificationResultSummary();
        public SegmentationResultSummary Segmentation { get; init; } = new SegmentationResultSummary();
        public ObbResultSummary Obb { get; init; } = new ObbResultSummary();
        public PoseResultSummary Pose { get; init; } = new PoseResultSummary();
    }

    public readonly record struct MaskMeasurement(double Area, double Coverage, bool HasMask);
}
