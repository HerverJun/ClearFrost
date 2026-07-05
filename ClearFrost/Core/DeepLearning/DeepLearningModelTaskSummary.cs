// ============================================================================
// 文件名: DeepLearningModelTaskSummary.cs
// 描述:   深度学习模型任务摘要
// ============================================================================

using System;
using System.IO;
using System.Linq;
using ClearFrost.Yolo;

namespace ClearFrost.Core.DeepLearning
{
    /// <summary>
    /// 面向模型列表、导入确认和工程师调试的任务摘要。
    /// </summary>
    public sealed class DeepLearningModelTaskSummary
    {
        public string ModelName { get; init; } = string.Empty;
        public string ModelPath { get; init; } = string.Empty;
        public YoloModelTask TaskType { get; init; } = YoloModelTask.Auto;
        public string TaskTypeText { get; init; } = string.Empty;
        public YoloTaskType ExecutionTaskMode { get; init; } = YoloTaskType.Auto;
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public string[] Labels { get; init; } = Array.Empty<string>();
        public int LabelCount { get; init; }
        public YoloOutputLayout OutputLayout { get; init; } = YoloOutputLayout.Unknown;
        public bool HasBuiltInNms { get; init; }
        public bool IsEndToEndNmsFree { get; init; }
        public bool RequiresApplicationNms { get; init; }
        public YoloPreprocessingMode PreprocessingMode { get; init; } = YoloPreprocessingMode.StandardLetterBox;
        public bool SupportsMask { get; init; }
        public bool SupportsPose { get; init; }
        public bool SupportsObb { get; init; }
        public bool IsSupported { get; init; }
        public string SupportMessage { get; init; } = string.Empty;

        public static DeepLearningModelTaskSummary FromDescriptor(YoloModelDescriptor? descriptor)
        {
            if (descriptor == null)
            {
                return new DeepLearningModelTaskSummary
                {
                    TaskTypeText = DeepLearningTaskText.GetTaskDisplayName(YoloModelTask.Auto),
                    IsSupported = false,
                    SupportMessage = DeepLearningTaskText.UnsupportedModelMessage
                };
            }

            string[] labels = descriptor.Labels?.ToArray() ?? Array.Empty<string>();
            bool isSupported = descriptor.IsSupported && descriptor.PostprocessProfile.Layout != YoloOutputLayout.Unknown;
            return new DeepLearningModelTaskSummary
            {
                ModelName = ResolveModelName(descriptor.ModelPath),
                ModelPath = descriptor.ModelPath ?? string.Empty,
                TaskType = descriptor.TaskType,
                TaskTypeText = DeepLearningTaskText.GetTaskDisplayName(descriptor.TaskType),
                ExecutionTaskMode = descriptor.ExecutionTaskMode,
                InputWidth = descriptor.PreprocessProfile?.InputWidth ?? 0,
                InputHeight = descriptor.PreprocessProfile?.InputHeight ?? 0,
                Labels = labels,
                LabelCount = labels.Length,
                OutputLayout = descriptor.PostprocessProfile?.Layout ?? YoloOutputLayout.Unknown,
                HasBuiltInNms = descriptor.HasBuiltInNms,
                IsEndToEndNmsFree = descriptor.IsEndToEndNmsFree,
                RequiresApplicationNms = descriptor.PostprocessProfile?.RequiresApplicationNms ?? false,
                PreprocessingMode = descriptor.PreprocessProfile?.Mode ?? YoloPreprocessingMode.StandardLetterBox,
                SupportsMask = descriptor.PostprocessProfile?.SupportsMask ?? false,
                SupportsPose = descriptor.PostprocessProfile?.SupportsPose ?? false,
                SupportsObb = descriptor.PostprocessProfile?.SupportsObb ?? false,
                IsSupported = isSupported,
                SupportMessage = isSupported
                    ? (string.IsNullOrWhiteSpace(descriptor.SupportMessage) ? "Supported" : descriptor.SupportMessage)
                    : DeepLearningTaskText.UnsupportedModelMessage
            };
        }

        private static string ResolveModelName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileNameWithoutExtension(path) ?? path;
            }
            catch
            {
                return path;
            }
        }
    }

    public static class DeepLearningTaskText
    {
        public const string UnsupportedModelMessage = "当前模型输出格式暂不支持，请检查 ONNX 导出任务类型、输出张量和 labels metadata。";

        public static string GetTaskDisplayName(YoloModelTask task)
        {
            return task switch
            {
                YoloModelTask.Detect => "目标检测",
                YoloModelTask.Classify => "图像分类",
                YoloModelTask.Segment => "分割检测",
                YoloModelTask.Pose => "姿态/关键点",
                YoloModelTask.Obb => "旋转框检测",
                _ => "自动识别"
            };
        }
    }
}
