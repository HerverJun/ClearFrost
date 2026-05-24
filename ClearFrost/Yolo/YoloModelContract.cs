// ============================================================================
// 文件名: YoloModelContract.cs
// 作者: 蘅芜君
// 描述:   YOLO ONNX 导出契约、预处理/后处理配置与模型探针
// ============================================================================
using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClearFrost.Yolo
{
    /// <summary>
    /// YOLO 预处理模式。
    /// </summary>
    public enum YoloPreprocessingMode
    {
        /// <summary>
        /// 官方 YOLO 导出默认契约：等比缩放 + 居中 114 灰色填充。
        /// </summary>
        StandardLetterBox = 0,

        /// <summary>
        /// ClearFrost 历史工业快速模式：按比例采样并放在左上角，不做居中填充。
        /// </summary>
        IndustrialFast = 1
    }

    /// <summary>
    /// 模型的高层任务类型，用于描述 ONNX 导出契约。
    /// </summary>
    public enum YoloModelTask
    {
        Auto = 0,
        Classify = 1,
        Detect = 2,
        Segment = 3,
        Pose = 4,
        Obb = 5
    }

    /// <summary>
    /// 主输出张量布局。
    /// </summary>
    public enum YoloOutputLayout
    {
        Unknown = 0,
        Classification = 1,
        RawYoloNoObjectness = 2,
        RawYoloObjectness = 3,
        DecodedXyxy = 4,
        SegmentRaw = 5,
        PoseRaw = 6,
        ObbRaw = 7
    }

    public sealed class YoloPreprocessProfile
    {
        public YoloPreprocessingMode Mode { get; init; }
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public int PaddingValue { get; init; } = 114;
    }

    public sealed class YoloPostprocessProfile
    {
        public YoloOutputLayout Layout { get; init; }
        public bool HasObjectness { get; init; }
        public bool UsesDecodedBoxes { get; init; }
        public bool RequiresApplicationNms { get; init; } = true;
        public bool SupportsMask { get; init; }
        public bool SupportsPose { get; init; }
        public bool SupportsObb { get; init; }
    }

    public sealed class YoloOutputDescriptor
    {
        public string Name { get; init; } = string.Empty;
        public int[] Dimensions { get; init; } = Array.Empty<int>();
    }

    /// <summary>
    /// 运行时识别出的 YOLO 模型契约。
    /// </summary>
    public sealed class YoloModelDescriptor
    {
        public string ModelPath { get; init; } = string.Empty;
        public string InputName { get; init; } = string.Empty;
        public int[] InputDimensions { get; init; } = Array.Empty<int>();
        public IReadOnlyList<YoloOutputDescriptor> Outputs { get; init; } = Array.Empty<YoloOutputDescriptor>();
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
        public string[] Labels { get; init; } = Array.Empty<string>();
        public string Version { get; init; } = string.Empty;
        public int MajorVersion { get; init; }
        public YoloModelTask TaskType { get; init; }
        public YoloTaskType ExecutionTaskMode { get; init; }
        public YoloPreprocessProfile PreprocessProfile { get; init; } = new YoloPreprocessProfile();
        public YoloPostprocessProfile PostprocessProfile { get; init; } = new YoloPostprocessProfile();
        public bool HasBuiltInNms { get; init; }
        public bool IsEndToEndNmsFree { get; init; }
        public bool IsSupported { get; init; }
        public string SupportMessage { get; init; } = string.Empty;
    }

    public sealed class YoloExportProbeReport
    {
        public YoloModelDescriptor Descriptor { get; init; } = new YoloModelDescriptor();

        public string ToJson(bool indented = true)
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = indented,
                Converters = { new JsonStringEnumConverter() }
            });
        }

        public void SaveJson(string path, bool indented = true)
        {
            File.WriteAllText(path, ToJson(indented));
        }
    }

    /// <summary>
    /// 轻量 ONNX 探针：不执行推理，只读取输入、输出与 metadata。
    /// </summary>
    public static class YoloExportProbe
    {
        public static YoloExportProbeReport Inspect(
            string modelPath,
            int requestedYoloVersion = 0,
            YoloPreprocessingMode preprocessingMode = YoloPreprocessingMode.StandardLetterBox,
            YoloTaskType requestedTaskMode = YoloTaskType.Auto)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("模型路径不能为空", nameof(modelPath));
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"模型文件不存在: {modelPath}", modelPath);

            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                EnableMemoryPattern = true,
                EnableCpuMemArena = true,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                InterOpNumThreads = 1
            };
            using var session = new InferenceSession(modelPath, options);

            string inputName = session.InputNames.First();
            int[] inputDimensions = YoloDetector.NormalizeInputTensorDimensions(session.InputMetadata[inputName].Dimensions);
            var outputs = session.OutputNames
                .Select(name => new YoloOutputDescriptor
                {
                    Name = name,
                    Dimensions = session.OutputMetadata[name].Dimensions.ToArray()
                })
                .ToArray();

            YoloModelDescriptor descriptor = YoloModelContractResolver.CreateDescriptor(
                modelPath,
                inputName,
                inputDimensions,
                outputs,
                session.ModelMetadata.CustomMetadataMap,
                requestedYoloVersion,
                preprocessingMode,
                requestedTaskMode);

            return new YoloExportProbeReport
            {
                Descriptor = descriptor
            };
        }
    }

    internal static class YoloModelContractResolver
    {
        public static YoloModelDescriptor CreateDescriptor(
            string modelPath,
            string inputName,
            int[] inputDimensions,
            IReadOnlyList<YoloOutputDescriptor> outputs,
            IReadOnlyDictionary<string, string> metadata,
            int requestedYoloVersion,
            YoloPreprocessingMode preprocessingMode,
            YoloTaskType requestedTaskMode)
        {
            Dictionary<string, string> normalizedMetadata = metadata
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            string[] labels = ParseLabelNames(GetMetadataValue(normalizedMetadata, "names"));
            string version = GetMetadataValue(normalizedMetadata, "version") ?? string.Empty;
            int majorVersion = ResolveMajorVersion(version, requestedYoloVersion);
            YoloModelTask task = ResolveTask(normalizedMetadata, outputs);
            YoloOutputLayout layout = ResolveOutputLayout(outputs.FirstOrDefault()?.Dimensions, labels.Length, task, majorVersion);
            bool hasBuiltInNms = ParseBool(GetMetadataValue(normalizedMetadata, "nms"));
            bool isEndToEnd = ParseBool(GetMetadataValue(normalizedMetadata, "end2end")) ||
                ParseBool(GetMetadataValue(normalizedMetadata, "end_to_end"));

            if (layout == YoloOutputLayout.DecodedXyxy && majorVersion >= 26)
            {
                isEndToEnd = true;
            }

            YoloTaskType executionTaskMode = ResolveExecutionTaskMode(task, requestedTaskMode);
            bool requiresApplicationNms = !(hasBuiltInNms || isEndToEnd);
            bool isSupported = layout != YoloOutputLayout.Unknown;
            string supportMessage = isSupported
                ? "Supported"
                : $"不支持的 YOLO 输出布局: [{string.Join(", ", outputs.FirstOrDefault()?.Dimensions ?? Array.Empty<int>())}]";

            return new YoloModelDescriptor
            {
                ModelPath = modelPath,
                InputName = inputName,
                InputDimensions = inputDimensions.ToArray(),
                Outputs = outputs
                    .Select(output => new YoloOutputDescriptor
                    {
                        Name = output.Name,
                        Dimensions = output.Dimensions.ToArray()
                    })
                    .ToArray(),
                Metadata = normalizedMetadata,
                Labels = labels,
                Version = version,
                MajorVersion = majorVersion,
                TaskType = task,
                ExecutionTaskMode = executionTaskMode,
                PreprocessProfile = new YoloPreprocessProfile
                {
                    Mode = preprocessingMode,
                    InputWidth = inputDimensions.Length > 3 ? inputDimensions[3] : 0,
                    InputHeight = inputDimensions.Length > 2 ? inputDimensions[2] : 0
                },
                PostprocessProfile = new YoloPostprocessProfile
                {
                    Layout = layout,
                    HasObjectness = layout == YoloOutputLayout.RawYoloObjectness,
                    UsesDecodedBoxes = layout == YoloOutputLayout.DecodedXyxy,
                    RequiresApplicationNms = requiresApplicationNms,
                    SupportsMask = task == YoloModelTask.Segment,
                    SupportsPose = task == YoloModelTask.Pose,
                    SupportsObb = task == YoloModelTask.Obb
                },
                HasBuiltInNms = hasBuiltInNms,
                IsEndToEndNmsFree = isEndToEnd,
                IsSupported = isSupported,
                SupportMessage = supportMessage
            };
        }

        public static string[] ParseLabelNames(string? names)
        {
            if (string.IsNullOrWhiteSpace(names))
            {
                return Array.Empty<string>();
            }

            string trimmed = names.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                try
                {
                    return JsonSerializer.Deserialize<string[]>(trimmed.Replace('\'', '"')) ?? Array.Empty<string>();
                }
                catch (JsonException)
                {
                    // 继续走 Python dict 风格解析。
                }
            }

            trimmed = trimmed.Trim('{', '}');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return Array.Empty<string>();
            }

            List<(int Index, string Name)> keyedLabels = new List<(int, string)>();
            List<string> fallbackLabels = new List<string>();
            foreach (string item in SplitMetadataItems(trimmed))
            {
                int colonIndex = item.IndexOf(':');
                if (colonIndex < 0)
                {
                    fallbackLabels.Add(TrimMetadataValue(item));
                    continue;
                }

                string keyText = item.Substring(0, colonIndex).Trim().Trim('"', '\'');
                string valueText = TrimMetadataValue(item.Substring(colonIndex + 1));
                if (int.TryParse(keyText, out int key))
                {
                    keyedLabels.Add((key, valueText));
                }
                else
                {
                    fallbackLabels.Add(valueText);
                }
            }

            if (keyedLabels.Count > 0)
            {
                return keyedLabels
                    .OrderBy(label => label.Index)
                    .Select(label => label.Name)
                    .ToArray();
            }

            return fallbackLabels.ToArray();
        }

        public static YoloOutputLayout ResolveOutputLayout(
            IReadOnlyList<int>? dimensions,
            int labelCount,
            YoloModelTask task,
            int majorVersion)
        {
            if (dimensions == null || dimensions.Count == 0)
            {
                return YoloOutputLayout.Unknown;
            }

            if (dimensions.Count == 2 && task == YoloModelTask.Classify)
            {
                return YoloOutputLayout.Classification;
            }

            int lastDimension = dimensions[dimensions.Count - 1];
            if ((dimensions.Count == 2 || dimensions.Count == 3) && lastDimension == 6)
            {
                return YoloOutputLayout.DecodedXyxy;
            }

            if (dimensions.Count == 2)
            {
                return YoloOutputLayout.Classification;
            }

            if (dimensions.Count != 3)
            {
                return YoloOutputLayout.Unknown;
            }

            int dim1 = dimensions[1];
            int dim2 = dimensions[2];
            if (task == YoloModelTask.Segment)
            {
                return YoloOutputLayout.SegmentRaw;
            }
            if (task == YoloModelTask.Pose)
            {
                return YoloOutputLayout.PoseRaw;
            }
            if (task == YoloModelTask.Obb)
            {
                return YoloOutputLayout.ObbRaw;
            }

            int featureCount = Math.Min(PositiveOrMax(dim1), PositiveOrMax(dim2));
            if (labelCount > 0)
            {
                if (featureCount == labelCount + 4)
                {
                    return YoloOutputLayout.RawYoloNoObjectness;
                }
                if (featureCount == labelCount + 5)
                {
                    return YoloOutputLayout.RawYoloObjectness;
                }
            }

            if (majorVersion >= 8)
            {
                return YoloOutputLayout.RawYoloNoObjectness;
            }

            if (majorVersion == 5 || majorVersion == 7)
            {
                return YoloOutputLayout.RawYoloObjectness;
            }

            return YoloOutputLayout.RawYoloNoObjectness;
        }

        private static YoloModelTask ResolveTask(
            IReadOnlyDictionary<string, string> metadata,
            IReadOnlyList<YoloOutputDescriptor> outputs)
        {
            string? task = GetMetadataValue(metadata, "task")?.Trim().ToLowerInvariant();
            return task switch
            {
                "classify" or "classification" => YoloModelTask.Classify,
                "detect" or "detection" => YoloModelTask.Detect,
                "segment" or "seg" or "segmentation" => YoloModelTask.Segment,
                "pose" => YoloModelTask.Pose,
                "obb" or "oriented-bounding-box" => YoloModelTask.Obb,
                _ => InferTaskFromOutputs(outputs)
            };
        }

        private static YoloModelTask InferTaskFromOutputs(IReadOnlyList<YoloOutputDescriptor> outputs)
        {
            if (outputs.Count == 0)
            {
                return YoloModelTask.Detect;
            }

            int[] first = outputs[0].Dimensions;
            if (first.Length == 2)
            {
                if (first[1] == 6 && first[0] != 1)
                {
                    return YoloModelTask.Detect;
                }

                return YoloModelTask.Classify;
            }

            if (outputs.Count >= 2 && YoloDetector.IsSegmentPrototypeOutputShape(outputs[1].Dimensions))
            {
                return YoloModelTask.Segment;
            }

            return YoloModelTask.Detect;
        }

        private static YoloTaskType ResolveExecutionTaskMode(YoloModelTask task, YoloTaskType requestedTaskMode)
        {
            if (requestedTaskMode != YoloTaskType.Auto && IsCompatibleTaskMode(task, requestedTaskMode))
            {
                return requestedTaskMode;
            }

            return task switch
            {
                YoloModelTask.Classify => YoloTaskType.Classify,
                YoloModelTask.Segment => YoloTaskType.SegmentWithMask,
                YoloModelTask.Pose => YoloTaskType.PoseWithKeypoints,
                YoloModelTask.Obb => YoloTaskType.Obb,
                _ => YoloTaskType.Detect
            };
        }

        private static bool IsCompatibleTaskMode(YoloModelTask task, YoloTaskType requestedTaskMode)
        {
            return task switch
            {
                YoloModelTask.Classify => requestedTaskMode == YoloTaskType.Classify,
                YoloModelTask.Detect => requestedTaskMode == YoloTaskType.Detect,
                YoloModelTask.Segment => requestedTaskMode is YoloTaskType.Detect or YoloTaskType.SegmentDetectOnly or YoloTaskType.SegmentWithMask,
                YoloModelTask.Pose => requestedTaskMode is YoloTaskType.Detect or YoloTaskType.PoseDetectOnly or YoloTaskType.PoseWithKeypoints,
                YoloModelTask.Obb => requestedTaskMode == YoloTaskType.Obb,
                _ => false
            };
        }

        private static int ResolveMajorVersion(string version, int requestedYoloVersion)
        {
            if (requestedYoloVersion > 0)
            {
                return requestedYoloVersion >= 26 ? 26 : requestedYoloVersion >= 8 ? 8 : requestedYoloVersion;
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                string majorText = version.Split('.', '-', '_').FirstOrDefault() ?? string.Empty;
                if (int.TryParse(majorText, out int majorVersion))
                {
                    return majorVersion >= 26 ? 26 : majorVersion >= 8 ? 8 : majorVersion;
                }
            }

            return 8;
        }

        private static string? GetMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
        {
            return metadata.TryGetValue(key, out string? value) ? value : null;
        }

        private static bool ParseBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value.Trim().ToLowerInvariant();
            return normalized is "1" or "true" or "yes" or "y";
        }

        private static int PositiveOrMax(int value)
        {
            return value > 0 ? value : int.MaxValue;
        }

        private static string TrimMetadataValue(string value)
        {
            return value.Trim().Trim('"', '\'');
        }

        private static IEnumerable<string> SplitMetadataItems(string text)
        {
            List<string> items = new List<string>();
            int start = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                if ((current == '\'' || current == '"') && (i == 0 || text[i - 1] != '\\'))
                {
                    quote = quote == '\0' ? current : quote == current ? '\0' : quote;
                    continue;
                }

                if (current == ',' && quote == '\0')
                {
                    items.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }

            items.Add(text.Substring(start).Trim());
            return items.Where(item => !string.IsNullOrWhiteSpace(item));
        }
    }
}
