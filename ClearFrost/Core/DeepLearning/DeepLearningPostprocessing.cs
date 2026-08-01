// ============================================================================
// 文件名: DeepLearningPostprocessing.cs
// 描述:   深度学习后处理扩展点，用于接入 YOLO 之外的 ONNX 输出解释器
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using ClearFrost.Yolo;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ClearFrost.Core.DeepLearning
{
    public enum DeepLearningScoreNormalization
    {
        None = 0,
        Softmax = 1,
        Sigmoid = 2
    }

    public sealed class DeepLearningOutputTensor
    {
        public DeepLearningOutputTensor(string name, Tensor<float> tensor)
        {
            Name = name ?? string.Empty;
            Tensor = tensor ?? throw new ArgumentNullException(nameof(tensor));
        }

        public string Name { get; }
        public Tensor<float> Tensor { get; }
    }

    public sealed class DeepLearningPostprocessRequest
    {
        public string AlgorithmKey { get; init; } = string.Empty;
        public string TaskKey { get; init; } = string.Empty;
        public IReadOnlyList<DeepLearningOutputTensor> Outputs { get; init; } = Array.Empty<DeepLearningOutputTensor>();
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
        public float ConfidenceThreshold { get; init; } = 0.5f;
        public float IouThreshold { get; init; } = 0.3f;
        public bool GlobalIou { get; init; }
        public DeepLearningScoreNormalization ScoreNormalization { get; init; } = DeepLearningScoreNormalization.None;
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }

        public Tensor<float>? PrimaryOutput => Outputs.Count == 0 ? null : Outputs[0].Tensor;
    }

    public interface IDeepLearningPostprocessor
    {
        string Key { get; }
        bool CanProcess(DeepLearningPostprocessRequest request);
        IReadOnlyList<YoloResult> Process(DeepLearningPostprocessRequest request);
    }

    public interface IDeepLearningPostprocessorDescriptor
    {
        IReadOnlyCollection<string> SupportedKeys { get; }
    }

    public static class DeepLearningPostprocessorConfiguration
    {
        private static readonly Lazy<DeepLearningPostprocessorRegistry> DefaultRegistry = new(DeepLearningPostprocessorRegistry.CreateDefault);

        public static IReadOnlyCollection<string> KnownPostprocessorKeys => DefaultRegistry.Value.KnownKeys;
        public static IReadOnlyCollection<string> KnownScoreNormalizationValues { get; } = new[]
        {
            "none",
            "raw",
            "identity",
            "probability",
            "probabilities",
            "softmax",
            "probability-softmax",
            "softmax-probability",
            "sigmoid",
            "logit-sigmoid",
            "sigmoid-logit"
        };

        public static bool IsKnownPostprocessorKey(string? key)
        {
            string normalized = (key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            return IsYoloPostprocessorKey(normalized) || DefaultRegistry.Value.IsKnownKey(normalized);
        }

        public static bool TryParseScoreNormalization(string? value, out DeepLearningScoreNormalization normalization)
        {
            string normalized = (value ?? string.Empty).Trim();
            normalization = DeepLearningScoreNormalization.None;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (int.TryParse(normalized, out _))
            {
                return false;
            }

            if (Enum.TryParse(normalized, ignoreCase: true, out DeepLearningScoreNormalization parsed) &&
                Enum.IsDefined(typeof(DeepLearningScoreNormalization), parsed))
            {
                normalization = parsed;
                return true;
            }

            switch (normalized.ToLowerInvariant())
            {
                case "raw":
                case "none":
                case "identity":
                case "probability":
                case "probabilities":
                    normalization = DeepLearningScoreNormalization.None;
                    return true;
                case "probability-softmax":
                case "softmax-probability":
                    normalization = DeepLearningScoreNormalization.Softmax;
                    return true;
                case "logit-sigmoid":
                case "sigmoid-logit":
                    normalization = DeepLearningScoreNormalization.Sigmoid;
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsYoloPostprocessorKey(string value)
        {
            string normalized = value.Trim().ToLowerInvariant();
            return normalized == "yolo" || normalized.StartsWith("yolov", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class DeepLearningPostprocessorRegistry
    {
        private readonly Dictionary<string, IDeepLearningPostprocessor> _processors = new(StringComparer.OrdinalIgnoreCase);

        public DeepLearningPostprocessorRegistry()
        {
        }

        public DeepLearningPostprocessorRegistry(IEnumerable<IDeepLearningPostprocessor> processors)
        {
            foreach (IDeepLearningPostprocessor processor in processors)
            {
                Register(processor);
            }
        }

        public static DeepLearningPostprocessorRegistry CreateDefault()
        {
            return new DeepLearningPostprocessorRegistry(new IDeepLearningPostprocessor[]
            {
                new ClassificationLogitsPostprocessor(),
                new DecodedDetectionPostprocessor(),
                new SemanticSegmentationPostprocessor(),
                new HeatmapAnomalyPostprocessor()
            });
        }

        public IReadOnlyCollection<IDeepLearningPostprocessor> Processors => _processors.Values.ToArray();

        public IReadOnlyCollection<string> KnownKeys => _processors.Values
            .SelectMany(GetKnownKeys)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        public bool IsKnownKey(string? key)
        {
            string normalized = (key ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(normalized) &&
                KnownKeys.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public void Register(IDeepLearningPostprocessor processor)
        {
            if (processor == null) throw new ArgumentNullException(nameof(processor));
            if (string.IsNullOrWhiteSpace(processor.Key))
                throw new ArgumentException("后处理器 Key 不能为空", nameof(processor));

            _processors[processor.Key.Trim()] = processor;
        }

        public bool TryResolve(DeepLearningPostprocessRequest request, out IDeepLearningPostprocessor? processor)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (TryGetConfiguredProcessor(request, out _, out IDeepLearningPostprocessor? configuredProcessor))
            {
                if (configuredProcessor!.CanProcess(request))
                {
                    processor = configuredProcessor;
                    return true;
                }

                processor = null;
                return false;
            }

            if (TryGetExplicitPostprocessorKey(request.Metadata, out _))
            {
                processor = null;
                return false;
            }

            processor = _processors.Values.FirstOrDefault(item => item.CanProcess(request));
            return processor != null;
        }

        public IDeepLearningPostprocessor Resolve(DeepLearningPostprocessRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (TryResolve(request, out IDeepLearningPostprocessor? processor))
            {
                return processor!;
            }

            string shape = FormatPrimaryShape(request);
            string outputShapes = FormatOutputShapes(request);
            string knownKeys = FormatKnownKeys();
            if (TryGetConfiguredProcessor(request, out string configuredKey, out IDeepLearningPostprocessor? configuredProcessor))
            {
                throw new NotSupportedException(
                    $"Configured deep learning postprocessor '{configuredKey}' resolved to '{configuredProcessor!.Key}' but cannot handle task={request.TaskKey}, primary_shape=[{shape}], output_shapes=[{outputShapes}]. Check model manifest PostprocessorKey/PostprocessOptions and model output shape. Known postprocessor keys: {knownKeys}");
            }

            if (TryGetExplicitPostprocessorKey(request.Metadata, out string explicitPostprocessorKey))
            {
                throw new NotSupportedException(
                    $"Configured deep learning postprocessor '{explicitPostprocessorKey}' is not registered for deep learning inference, task={request.TaskKey}, primary_shape=[{shape}], output_shapes=[{outputShapes}]. Check model manifest PostprocessorKey/PostprocessOptions. Known postprocessor keys: {knownKeys}");
            }

            throw new NotSupportedException(
                $"No registered deep learning postprocessor can handle algorithm={request.AlgorithmKey}, task={request.TaskKey}, primary_shape=[{shape}], output_shapes=[{outputShapes}]. Known postprocessor keys: {knownKeys}");
        }

        public IReadOnlyList<YoloResult> Process(DeepLearningPostprocessRequest request)
        {
            return Resolve(request).Process(request);
        }

        private static IEnumerable<string> GetKnownKeys(IDeepLearningPostprocessor processor)
        {
            yield return processor.Key;

            if (processor is IDeepLearningPostprocessorDescriptor descriptor)
            {
                foreach (string key in descriptor.SupportedKeys)
                {
                    yield return key;
                }
            }
        }

        private bool TryGetConfiguredProcessor(
            DeepLearningPostprocessRequest request,
            out string configuredKey,
            out IDeepLearningPostprocessor? processor)
        {
            if (TryGetConfiguredProcessor(request.AlgorithmKey, out configuredKey, out processor))
            {
                return true;
            }

            foreach (string metadataKey in new[] { "postprocessor", "postprocess", "postprocessor_key", "algorithm", "algorithm_key" })
            {
                if (TryGetMetadataValue(request.Metadata, metadataKey, out string value) &&
                    TryGetConfiguredProcessor(value, out configuredKey, out processor))
                {
                    return true;
                }
            }

            configuredKey = string.Empty;
            processor = null;
            return false;
        }

        private bool TryGetConfiguredProcessor(
            string? key,
            out string configuredKey,
            out IDeepLearningPostprocessor? processor)
        {
            string normalized = (key ?? string.Empty).Trim();
            configuredKey = normalized;
            processor = null;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            foreach (IDeepLearningPostprocessor candidate in _processors.Values)
            {
                if (GetKnownKeys(candidate).Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    processor = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetExplicitPostprocessorKey(
            IReadOnlyDictionary<string, string>? metadata,
            out string configuredKey)
        {
            foreach (string metadataKey in new[] { "postprocessor", "postprocess", "postprocessor_key" })
            {
                if (TryGetMetadataValue(metadata, metadataKey, out string value))
                {
                    configuredKey = value.Trim();
                    return true;
                }
            }

            configuredKey = string.Empty;
            return false;
        }

        private static bool TryGetMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            string key,
            out string value)
        {
            value = string.Empty;
            if (metadata == null || metadata.Count == 0)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> pair in metadata)
            {
                if (string.Equals(pair.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(value);
                }
            }

            return false;
        }

        private static string FormatPrimaryShape(DeepLearningPostprocessRequest request)
        {
            return request.PrimaryOutput == null
                ? "none"
                : string.Join(", ", request.PrimaryOutput.Dimensions.ToArray());
        }

        private static string FormatOutputShapes(DeepLearningPostprocessRequest request)
        {
            if (request.Outputs == null || request.Outputs.Count == 0)
            {
                return "none";
            }

            return string.Join("; ", request.Outputs.Select((output, index) =>
            {
                string name = string.IsNullOrWhiteSpace(output.Name)
                    ? $"#{index}"
                    : output.Name.Trim();
                string shape = output.Tensor == null
                    ? "null"
                    : string.Join(", ", output.Tensor.Dimensions.ToArray());
                return $"{name}=[{shape}]";
            }));
        }

        private string FormatKnownKeys()
        {
            return string.Join(", ", KnownKeys.Concat(new[] { "yolo", "yolov8" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        }
    }

    public sealed class DecodedDetectionPostprocessor : IDeepLearningPostprocessor, IDeepLearningPostprocessorDescriptor
    {
        private const int DefaultXIndex = 0;
        private const int DefaultYIndex = 1;
        private const int DefaultWidthOrRightIndex = 2;
        private const int DefaultHeightOrBottomIndex = 3;
        private const int DefaultScoreIndex = 4;
        private const int DefaultClassIndex = 5;

        private static readonly string[] SupportedKeyAliases =
        {
            "decoded-detection",
            "generic-detection",
            "object-detection",
            "detection",
            "detect",
            "ssd",
            "faster-rcnn",
            "retinanet",
            "detr"
        };

        public string Key => "decoded-detection";
        public IReadOnlyCollection<string> SupportedKeys => SupportedKeyAliases;

        public bool CanProcess(DeepLearningPostprocessRequest request)
        {
            Tensor<float>? output = request.PrimaryOutput;
            bool hasSupportedOutput =
                output != null && TryCreateView(output, out _) ||
                TryCreateMultiOutputView(request, out _);
            if (!hasSupportedOutput)
            {
                return false;
            }

            if (MatchesSupportedKey(request.AlgorithmKey) ||
                string.IsNullOrWhiteSpace(request.AlgorithmKey) && MatchesSupportedKey(request.TaskKey))
            {
                return true;
            }

            return TryGetAnyMetadataValue(
                request.Metadata,
                new[] { "task", "postprocessor", "postprocess", "postprocessor_key", "algorithm", "algorithm_key" },
                out string metadataKey) && MatchesSupportedKey(metadataKey);
        }

        public IReadOnlyList<YoloResult> Process(DeepLearningPostprocessRequest request)
        {
            Tensor<float> output = request.PrimaryOutput
                ?? throw new InvalidOperationException("Decoded detection postprocessor requires at least one output tensor.");

            if (TryCreateView(output, out DetectionTensorView view))
            {
                return ProcessSingleOutput(request, output, view);
            }

            if (TryCreateMultiOutputView(request, out MultiOutputDetectionView? multiOutputView))
            {
                return ProcessMultiOutput(request, multiOutputView!);
            }

            throw new NotSupportedException(
                $"Decoded detection postprocessor supports [N,6+], [1,N,6+] or separate boxes/scores/classes outputs, primary shape=[{string.Join(", ", output.Dimensions.ToArray())}]");
        }

        private static IReadOnlyList<YoloResult> ProcessSingleOutput(
            DeepLearningPostprocessRequest request,
            Tensor<float> output,
            DetectionTensorView view)
        {
            DetectionBoxFormat boxFormat = ResolveBoxFormat(request.Metadata);
            bool normalizedBoxes = ResolveNormalizedBoxes(request.Metadata);
            bool applyNms = ResolveBooleanMetadata(request.Metadata, new[] { "apply_nms", "nms" }, defaultValue: false);
            int scoreIndex = ResolveColumnIndex(request.Metadata, new[] { "score_index", "confidence_index", "conf_index" }, DefaultScoreIndex, view.Columns);
            int classIndex = ResolveColumnIndex(request.Metadata, new[] { "class_index", "class_id_index", "label_index" }, DefaultClassIndex, view.Columns);

            var results = new List<YoloResult>();
            for (int row = 0; row < view.Rows; row++)
            {
                float score = NormalizeScore(ReadValue(output, view, row, scoreIndex), request.ScoreNormalization);
                if (!float.IsFinite(score) || score < request.ConfidenceThreshold)
                {
                    continue;
                }

                float a = ReadValue(output, view, row, DefaultXIndex);
                float b = ReadValue(output, view, row, DefaultYIndex);
                float c = ReadValue(output, view, row, DefaultWidthOrRightIndex);
                float d = ReadValue(output, view, row, DefaultHeightOrBottomIndex);
                if (!TryCreateDetectionData(
                    a,
                    b,
                    c,
                    d,
                    boxFormat,
                    normalizedBoxes,
                    request.InputWidth,
                    request.InputHeight,
                    out float centerX,
                    out float centerY,
                    out float width,
                    out float height))
                {
                    continue;
                }

                int classId = Math.Max(0, (int)MathF.Round(ReadValue(output, view, row, classIndex)));
                var result = new YoloResult();
                result.SetDetectionData(centerX, centerY, width, height, score, classId);
                results.Add(result);
            }

            return FinalizeResults(results, applyNms, request.IouThreshold, request.GlobalIou);
        }

        private static IReadOnlyList<YoloResult> ProcessMultiOutput(
            DeepLearningPostprocessRequest request,
            MultiOutputDetectionView view)
        {
            DetectionBoxFormat boxFormat = ResolveBoxFormat(request.Metadata);
            bool normalizedBoxes = ResolveNormalizedBoxes(request.Metadata);
            bool applyNms = ResolveBooleanMetadata(request.Metadata, new[] { "apply_nms", "nms" }, defaultValue: false);
            int defaultClassId = ResolveMetadataClassId(request.Metadata);

            var results = new List<YoloResult>();
            for (int row = 0; row < view.Boxes.Rows; row++)
            {
                if (!TryResolveScoreAndClass(
                    view,
                    row,
                    request.ScoreNormalization,
                    request.Metadata,
                    out float score,
                    out int classId))
                {
                    continue;
                }

                if (!float.IsFinite(score) || score < request.ConfidenceThreshold)
                {
                    continue;
                }

                float a = ReadBoxValue(view.BoxesTensor, view.Boxes, row, 0);
                float b = ReadBoxValue(view.BoxesTensor, view.Boxes, row, 1);
                float c = ReadBoxValue(view.BoxesTensor, view.Boxes, row, 2);
                float d = ReadBoxValue(view.BoxesTensor, view.Boxes, row, 3);
                if (!TryCreateDetectionData(
                    a,
                    b,
                    c,
                    d,
                    boxFormat,
                    normalizedBoxes,
                    request.InputWidth,
                    request.InputHeight,
                    out float centerX,
                    out float centerY,
                    out float width,
                    out float height))
                {
                    continue;
                }

                if (classId < 0)
                {
                    classId = defaultClassId;
                }

                var result = new YoloResult();
                result.SetDetectionData(centerX, centerY, width, height, score, classId);
                results.Add(result);
            }

            return FinalizeResults(results, applyNms, request.IouThreshold, request.GlobalIou);
        }

        private static bool TryResolveScoreAndClass(
            MultiOutputDetectionView view,
            int row,
            DeepLearningScoreNormalization normalization,
            IReadOnlyDictionary<string, string>? metadata,
            out float score,
            out int classId)
        {
            score = 0;
            classId = -1;
            if (view.Scores.Kind == DetectionScoreTensorKind.Vector)
            {
                score = NormalizeScore(ReadVectorValue(view.ScoresTensor, view.Scores.Vector, row), normalization);
                classId = view.ClassesTensor != null && view.Classes.HasValue
                    ? Math.Max(0, (int)MathF.Round(ReadVectorValue(view.ClassesTensor, view.Classes.Value, row)))
                    : ResolveMetadataClassId(metadata);
                return true;
            }

            if (view.Scores.Kind != DetectionScoreTensorKind.Matrix)
            {
                return false;
            }

            DetectionScoreMatrixTensorView matrix = view.Scores.Matrix;
            int ignoredClassId = ResolveIgnoredClassId(metadata, matrix.ClassCount);
            int classIdOffset = ResolveClassIdOffset(metadata);
            if (normalization == DeepLearningScoreNormalization.Softmax)
            {
                return TryResolveSoftmaxMatrixScore(
                    view.ScoresTensor,
                    matrix,
                    row,
                    ignoredClassId,
                    classIdOffset,
                    out score,
                    out classId);
            }

            for (int candidateClass = 0; candidateClass < matrix.ClassCount; candidateClass++)
            {
                if (candidateClass == ignoredClassId)
                {
                    continue;
                }

                float candidateScore = NormalizeScore(ReadScoreMatrixValue(view.ScoresTensor, matrix, row, candidateClass), normalization);
                if (!float.IsFinite(candidateScore) || candidateScore <= score)
                {
                    continue;
                }

                score = candidateScore;
                classId = Math.Max(0, candidateClass + classIdOffset);
            }

            return classId >= 0;
        }

        private static bool TryResolveSoftmaxMatrixScore(
            Tensor<float> scores,
            DetectionScoreMatrixTensorView matrix,
            int row,
            int ignoredClassId,
            int classIdOffset,
            out float score,
            out int classId)
        {
            score = 0;
            classId = -1;
            float max = float.NegativeInfinity;
            for (int candidateClass = 0; candidateClass < matrix.ClassCount; candidateClass++)
            {
                float value = ReadScoreMatrixValue(scores, matrix, row, candidateClass);
                if (float.IsFinite(value))
                {
                    max = Math.Max(max, value);
                }
            }

            if (!float.IsFinite(max))
            {
                return false;
            }

            double sum = 0;
            var exps = new double[matrix.ClassCount];
            for (int candidateClass = 0; candidateClass < matrix.ClassCount; candidateClass++)
            {
                float value = ReadScoreMatrixValue(scores, matrix, row, candidateClass);
                if (!float.IsFinite(value))
                {
                    exps[candidateClass] = 0;
                    continue;
                }

                double exp = Math.Exp(value - max);
                exps[candidateClass] = exp;
                sum += exp;
            }

            if (sum <= 0)
            {
                return false;
            }

            for (int candidateClass = 0; candidateClass < matrix.ClassCount; candidateClass++)
            {
                if (candidateClass == ignoredClassId)
                {
                    continue;
                }

                float candidateScore = (float)(exps[candidateClass] / sum);
                if (candidateScore <= score)
                {
                    continue;
                }

                score = candidateScore;
                classId = Math.Max(0, candidateClass + classIdOffset);
            }

            return classId >= 0;
        }

        private static IReadOnlyList<YoloResult> FinalizeResults(
            List<YoloResult> results,
            bool applyNms,
            float iouThreshold,
            bool globalIou)
        {
            if (applyNms)
            {
                return ApplyNms(results, iouThreshold, globalIou);
            }

            return results
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.ClassId)
                .ToArray();
        }

        private static bool TryCreateView(Tensor<float> output, out DetectionTensorView view)
        {
            int[] dimensions = output.Dimensions.ToArray();
            view = default;
            if (dimensions.Any(dimension => dimension <= 0))
            {
                return false;
            }

            if (dimensions.Length == 2 && dimensions[1] >= 6)
            {
                view = new DetectionTensorView(dimensions[0], dimensions[1], false);
                return true;
            }

            if (dimensions.Length == 3 && dimensions[0] == 1 && dimensions[2] >= 6)
            {
                view = new DetectionTensorView(dimensions[1], dimensions[2], true);
                return true;
            }

            return false;
        }

        private static bool TryCreateMultiOutputView(
            DeepLearningPostprocessRequest request,
            out MultiOutputDetectionView? view)
        {
            view = null;
            if (request.Outputs == null || request.Outputs.Count < 2)
            {
                return false;
            }

            if (!TryFindBoxOutput(request, out DeepLearningOutputTensor? boxesOutput, out DetectionBoxTensorView boxesView))
            {
                return false;
            }

            if (!TryFindScoreOutput(
                request,
                boxesOutput!,
                boxesView.Rows,
                new[] { "scores_output", "score_output", "confidence_output", "scores_tensor", "score_tensor" },
                new[] { "scores", "score", "detection_scores", "confidence", "confidences", "probabilities" },
                out DeepLearningOutputTensor? scoresOutput,
                out DetectionScoreTensorView scoresView))
            {
                return false;
            }

            TryFindVectorOutput(
                request,
                boxesOutput!,
                boxesView.Rows,
                new[] { "classes_output", "class_output", "class_ids_output", "labels_output", "classes_tensor", "class_tensor" },
                new[] { "classes", "class", "class_ids", "detection_classes", "labels", "label_ids" },
                out DeepLearningOutputTensor? classesOutput,
                out DetectionVectorTensorView classesView);

            view = new MultiOutputDetectionView(
                boxesOutput!.Tensor,
                boxesView,
                scoresOutput!.Tensor,
                scoresView,
                classesOutput?.Tensor,
                classesOutput == null ? null : classesView);
            return true;
        }

        private static bool TryFindScoreOutput(
            DeepLearningPostprocessRequest request,
            DeepLearningOutputTensor excludedOutput,
            int expectedRows,
            IReadOnlyList<string> metadataKeys,
            IReadOnlyList<string> aliases,
            out DeepLearningOutputTensor? output,
            out DetectionScoreTensorView view)
        {
            bool Predicate(Tensor<float> tensor, out DetectionScoreTensorView candidateView) =>
                TryCreateScoreView(tensor, expectedRows, out candidateView);

            if (TryGetAnyMetadataValue(request.Metadata, metadataKeys, out string configuredName) &&
                TryFindOutputByName(request.Outputs, configuredName, Predicate, out output, out view))
            {
                return !ReferenceEquals(output, excludedOutput);
            }

            if (TryFindOutputByName(request.Outputs, aliases, Predicate, out output, out view) &&
                !ReferenceEquals(output, excludedOutput))
            {
                return true;
            }

            return TryFindOutput(
                request.Outputs.Where(item => !ReferenceEquals(item, excludedOutput)).ToArray(),
                Predicate,
                out output,
                out view);
        }

        private static bool TryFindBoxOutput(
            DeepLearningPostprocessRequest request,
            out DeepLearningOutputTensor? output,
            out DetectionBoxTensorView view)
        {
            if (TryGetAnyMetadataValue(
                request.Metadata,
                new[] { "boxes_output", "box_output", "bboxes_output", "boxes_tensor", "box_tensor" },
                out string configuredName) &&
                TryFindOutputByName(request.Outputs, configuredName, TryCreateBoxView, out output, out view))
            {
                return true;
            }

            if (TryFindOutputByName(
                request.Outputs,
                new[] { "boxes", "box", "bboxes", "bbox", "detection_boxes", "pred_boxes", "output_boxes" },
                TryCreateBoxView,
                out output,
                out view))
            {
                return true;
            }

            return TryFindOutput(request.Outputs, TryCreateBoxView, out output, out view);
        }

        private static bool TryFindVectorOutput(
            DeepLearningPostprocessRequest request,
            DeepLearningOutputTensor excludedOutput,
            int expectedRows,
            IReadOnlyList<string> metadataKeys,
            IReadOnlyList<string> aliases,
            out DeepLearningOutputTensor? output,
            out DetectionVectorTensorView view)
        {
            bool Predicate(Tensor<float> tensor, out DetectionVectorTensorView candidateView) =>
                TryCreateVectorView(tensor, expectedRows, out candidateView);

            if (TryGetAnyMetadataValue(request.Metadata, metadataKeys, out string configuredName) &&
                TryFindOutputByName(request.Outputs, configuredName, Predicate, out output, out view))
            {
                return !ReferenceEquals(output, excludedOutput);
            }

            if (TryFindOutputByName(request.Outputs, aliases, Predicate, out output, out view) &&
                !ReferenceEquals(output, excludedOutput))
            {
                return true;
            }

            return TryFindOutput(
                request.Outputs.Where(item => !ReferenceEquals(item, excludedOutput)).ToArray(),
                Predicate,
                out output,
                out view);
        }

        private static bool TryFindOutputByName<TView>(
            IReadOnlyList<DeepLearningOutputTensor> outputs,
            string configuredName,
            TryCreateTensorView<TView> tryCreateView,
            out DeepLearningOutputTensor? output,
            out TView view)
        {
            output = null;
            view = default!;
            if (string.IsNullOrWhiteSpace(configuredName))
            {
                return false;
            }

            DeepLearningOutputTensor? match = outputs.FirstOrDefault(item =>
                string.Equals(item.Name, configuredName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null || !tryCreateView(match.Tensor, out view))
            {
                return false;
            }

            output = match;
            return true;
        }

        private static bool TryFindOutputByName<TView>(
            IReadOnlyList<DeepLearningOutputTensor> outputs,
            IReadOnlyList<string> aliases,
            TryCreateTensorView<TView> tryCreateView,
            out DeepLearningOutputTensor? output,
            out TView view)
        {
            foreach (DeepLearningOutputTensor candidate in outputs)
            {
                if (!NameMatchesAnyAlias(candidate.Name, aliases) || !tryCreateView(candidate.Tensor, out view))
                {
                    continue;
                }

                output = candidate;
                return true;
            }

            output = null;
            view = default!;
            return false;
        }

        private static bool TryCreateScoreView(Tensor<float> output, int expectedRows, out DetectionScoreTensorView view)
        {
            view = default;
            if (TryCreateVectorView(output, expectedRows, out DetectionVectorTensorView vectorView))
            {
                view = DetectionScoreTensorView.FromVector(vectorView);
                return true;
            }

            if (TryCreateScoreMatrixView(output, expectedRows, out DetectionScoreMatrixTensorView matrixView))
            {
                view = DetectionScoreTensorView.FromMatrix(matrixView);
                return true;
            }

            return false;
        }

        private static bool TryCreateScoreMatrixView(Tensor<float> output, int expectedRows, out DetectionScoreMatrixTensorView view)
        {
            int[] dimensions = output.Dimensions.ToArray();
            view = default;
            if (expectedRows <= 0 || dimensions.Any(dimension => dimension <= 0))
            {
                return false;
            }

            if (dimensions.Length == 2 && dimensions[0] == expectedRows && dimensions[1] > 1)
            {
                view = new DetectionScoreMatrixTensorView(expectedRows, dimensions[1], DetectionScoreMatrixTensorLayout.Matrix);
                return true;
            }

            if (dimensions.Length == 3 && dimensions[0] == 1 && dimensions[1] == expectedRows && dimensions[2] > 1)
            {
                view = new DetectionScoreMatrixTensorView(expectedRows, dimensions[2], DetectionScoreMatrixTensorLayout.BatchMatrix);
                return true;
            }

            return false;
        }

        private static bool TryFindOutput<TView>(
            IReadOnlyList<DeepLearningOutputTensor> outputs,
            TryCreateTensorView<TView> tryCreateView,
            out DeepLearningOutputTensor? output,
            out TView view)
        {
            foreach (DeepLearningOutputTensor candidate in outputs)
            {
                if (!tryCreateView(candidate.Tensor, out view))
                {
                    continue;
                }

                output = candidate;
                return true;
            }

            output = null;
            view = default!;
            return false;
        }

        private static bool TryCreateBoxView(Tensor<float> output, out DetectionBoxTensorView view)
        {
            int[] dimensions = output.Dimensions.ToArray();
            view = default;
            if (dimensions.Any(dimension => dimension <= 0))
            {
                return false;
            }

            if (dimensions.Length == 2 && dimensions[1] == 4)
            {
                view = new DetectionBoxTensorView(dimensions[0], false);
                return true;
            }

            if (dimensions.Length == 3 && dimensions[0] == 1 && dimensions[2] == 4)
            {
                view = new DetectionBoxTensorView(dimensions[1], true);
                return true;
            }

            return false;
        }

        private static bool TryCreateVectorView(Tensor<float> output, int expectedRows, out DetectionVectorTensorView view)
        {
            int[] dimensions = output.Dimensions.ToArray();
            view = default;
            if (expectedRows <= 0 || dimensions.Any(dimension => dimension <= 0))
            {
                return false;
            }

            if (dimensions.Length == 1 && dimensions[0] == expectedRows)
            {
                view = new DetectionVectorTensorView(expectedRows, DetectionVectorTensorLayout.Vector);
                return true;
            }

            if (dimensions.Length == 2)
            {
                if (dimensions[0] == 1 && dimensions[1] == expectedRows)
                {
                    view = new DetectionVectorTensorView(expectedRows, DetectionVectorTensorLayout.BatchVector);
                    return true;
                }

                if (dimensions[0] == expectedRows && dimensions[1] == 1)
                {
                    view = new DetectionVectorTensorView(expectedRows, DetectionVectorTensorLayout.ColumnVector);
                    return true;
                }
            }

            if (dimensions.Length == 3 && dimensions[0] == 1 && dimensions[1] == expectedRows && dimensions[2] == 1)
            {
                view = new DetectionVectorTensorView(expectedRows, DetectionVectorTensorLayout.BatchColumnVector);
                return true;
            }

            return false;
        }

        private static float ReadValue(Tensor<float> output, DetectionTensorView view, int row, int column)
        {
            return view.HasBatchDimension
                ? output[0, row, column]
                : output[row, column];
        }

        private static float ReadBoxValue(Tensor<float> output, DetectionBoxTensorView view, int row, int column)
        {
            return view.HasBatchDimension
                ? output[0, row, column]
                : output[row, column];
        }

        private static float ReadVectorValue(Tensor<float> output, DetectionVectorTensorView view, int row)
        {
            return view.Layout switch
            {
                DetectionVectorTensorLayout.Vector => output[row],
                DetectionVectorTensorLayout.BatchVector => output[0, row],
                DetectionVectorTensorLayout.ColumnVector => output[row, 0],
                DetectionVectorTensorLayout.BatchColumnVector => output[0, row, 0],
                _ => 0f
            };
        }

        private static float ReadScoreMatrixValue(Tensor<float> output, DetectionScoreMatrixTensorView view, int row, int classIndex)
        {
            return view.Layout switch
            {
                DetectionScoreMatrixTensorLayout.Matrix => output[row, classIndex],
                DetectionScoreMatrixTensorLayout.BatchMatrix => output[0, row, classIndex],
                _ => 0f
            };
        }

        private static bool TryCreateDetectionData(
            float a,
            float b,
            float c,
            float d,
            DetectionBoxFormat boxFormat,
            bool normalizedBoxes,
            int inputWidth,
            int inputHeight,
            out float centerX,
            out float centerY,
            out float width,
            out float height)
        {
            centerX = 0;
            centerY = 0;
            width = 0;
            height = 0;
            if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c) || !float.IsFinite(d))
            {
                return false;
            }

            float xScale = normalizedBoxes && inputWidth > 0 ? inputWidth : 1f;
            float yScale = normalizedBoxes && inputHeight > 0 ? inputHeight : 1f;
            if (boxFormat == DetectionBoxFormat.Xywh)
            {
                centerX = a * xScale;
                centerY = b * yScale;
                width = Math.Abs(c * xScale);
                height = Math.Abs(d * yScale);
            }
            else if (boxFormat == DetectionBoxFormat.Yxyx)
            {
                float left = Math.Min(b, d) * xScale;
                float right = Math.Max(b, d) * xScale;
                float top = Math.Min(a, c) * yScale;
                float bottom = Math.Max(a, c) * yScale;
                width = right - left;
                height = bottom - top;
                centerX = (left + right) / 2f;
                centerY = (top + bottom) / 2f;
            }
            else
            {
                float left = Math.Min(a, c) * xScale;
                float right = Math.Max(a, c) * xScale;
                float top = Math.Min(b, d) * yScale;
                float bottom = Math.Max(b, d) * yScale;
                width = right - left;
                height = bottom - top;
                centerX = (left + right) / 2f;
                centerY = (top + bottom) / 2f;
            }

            return width > 0 && height > 0;
        }

        private static IReadOnlyList<YoloResult> ApplyNms(List<YoloResult> results, float iouThreshold, bool globalIou)
        {
            if (results.Count <= 1)
            {
                return results;
            }

            float threshold = iouThreshold <= 0 ? 0.3f : iouThreshold;
            var sorted = results
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.ClassId)
                .ToList();
            var kept = new List<YoloResult>();
            while (sorted.Count > 0)
            {
                YoloResult current = sorted[0];
                sorted.RemoveAt(0);
                kept.Add(current);

                for (int i = sorted.Count - 1; i >= 0; i--)
                {
                    YoloResult candidate = sorted[i];
                    if ((globalIou || candidate.ClassId == current.ClassId) &&
                        CalculateIou(current, candidate) > threshold)
                    {
                        candidate.Dispose();
                        sorted.RemoveAt(i);
                    }
                }
            }

            return kept;
        }

        private static float CalculateIou(YoloResult a, YoloResult b)
        {
            float interLeft = Math.Max(a.Left, b.Left);
            float interTop = Math.Max(a.Top, b.Top);
            float interRight = Math.Min(a.Right, b.Right);
            float interBottom = Math.Min(a.Bottom, b.Bottom);
            float interWidth = Math.Max(0, interRight - interLeft);
            float interHeight = Math.Max(0, interBottom - interTop);
            float interArea = interWidth * interHeight;
            float unionArea = a.Area + b.Area - interArea;
            return unionArea <= 0 ? 0 : interArea / unionArea;
        }

        private static DetectionBoxFormat ResolveBoxFormat(IReadOnlyDictionary<string, string>? metadata)
        {
            if (TryGetAnyMetadataValue(metadata, new[] { "box_format", "bbox_format", "coordinate_format" }, out string format))
            {
                string normalized = format.Trim().ToLowerInvariant();
                if (normalized is "xywh" or "cxcywh" or "center" or "center-xywh")
                {
                    return DetectionBoxFormat.Xywh;
                }

                if (normalized is "yxyx" or "yminxminymaxxmax" or "tensorflow" or "tf")
                {
                    return DetectionBoxFormat.Yxyx;
                }
            }

            return DetectionBoxFormat.Xyxy;
        }

        private static bool ResolveNormalizedBoxes(IReadOnlyDictionary<string, string>? metadata)
        {
            return ResolveBooleanMetadata(
                    metadata,
                    new[] { "normalized_boxes", "boxes_normalized", "normalized_coordinates", "coordinates_normalized" },
                    defaultValue: false) ||
                TryGetAnyMetadataValue(metadata, new[] { "box_units", "coordinate_units", "coordinates" }, out string units) &&
                IsNormalizedCoordinateUnit(units);
        }

        private static bool IsNormalizedCoordinateUnit(string? value)
        {
            string normalized = (value ?? string.Empty).Trim();
            return normalized.Equals("normalized", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("relative", StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveMetadataClassId(IReadOnlyDictionary<string, string>? metadata)
        {
            if (TryGetAnyMetadataValue(metadata, new[] { "class_id", "default_class_id", "foreground_class_id" }, out string value) &&
                int.TryParse(value, out int classId))
            {
                return Math.Max(0, classId);
            }

            return 0;
        }

        private static int ResolveClassIdOffset(IReadOnlyDictionary<string, string>? metadata)
        {
            if (TryGetAnyMetadataValue(metadata, new[] { "class_id_offset", "class_offset", "label_offset" }, out string value) &&
                int.TryParse(value, out int offset))
            {
                return offset;
            }

            return 0;
        }

        private static int ResolveIgnoredClassId(IReadOnlyDictionary<string, string>? metadata, int classCount)
        {
            if (!TryGetAnyMetadataValue(
                metadata,
                new[] { "background_class_id", "background_index", "ignore_class_id", "ignored_class_id", "no_object_class_id", "no_object_index" },
                out string value))
            {
                return -1;
            }

            string normalized = value.Trim();
            if (normalized.Equals("last", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("final", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(0, classCount - 1);
            }

            return int.TryParse(normalized, out int classId) && classId >= 0 && classId < classCount
                ? classId
                : -1;
        }

        private static int ResolveColumnIndex(IReadOnlyDictionary<string, string>? metadata, IReadOnlyList<string> keys, int defaultValue, int columnCount)
        {
            if (TryGetAnyMetadataValue(metadata, keys, out string value) &&
                int.TryParse(value, out int parsed) &&
                parsed >= 0 &&
                parsed < columnCount)
            {
                return parsed;
            }

            return Math.Min(defaultValue, columnCount - 1);
        }

        private static float NormalizeScore(float value, DeepLearningScoreNormalization normalization)
        {
            return normalization == DeepLearningScoreNormalization.Sigmoid
                ? 1f / (1f + MathF.Exp(-value))
                : value;
        }

        private static bool MatchesSupportedKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string normalized = key.Trim();
            return SupportedKeyAliases.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static bool NameMatchesAnyAlias(string name, IReadOnlyList<string> aliases)
        {
            string normalizedName = NormalizeOutputName(name);
            return aliases.Any(alias =>
            {
                string normalizedAlias = NormalizeOutputName(alias);
                return normalizedName == normalizedAlias ||
                    normalizedName.Contains(normalizedAlias, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string NormalizeOutputName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        private static bool ResolveBooleanMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            IReadOnlyList<string> keys,
            bool defaultValue)
        {
            if (!TryGetAnyMetadataValue(metadata, keys, out string value))
            {
                return defaultValue;
            }

            string normalized = value.Trim().ToLowerInvariant();
            return normalized switch
            {
                "1" or "true" or "yes" or "on" or "enabled" => true,
                "0" or "false" or "no" or "off" or "disabled" or "none" => false,
                _ => defaultValue
            };
        }

        private static bool TryGetAnyMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            IReadOnlyList<string> keys,
            out string value)
        {
            value = string.Empty;
            if (metadata == null) return false;

            foreach (string key in keys)
            {
                foreach (KeyValuePair<string, string> pair in metadata)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value ?? string.Empty;
                        return true;
                    }
                }
            }

            return false;
        }

        private delegate bool TryCreateTensorView<TView>(Tensor<float> tensor, out TView view);

        private sealed record MultiOutputDetectionView(
            Tensor<float> BoxesTensor,
            DetectionBoxTensorView Boxes,
            Tensor<float> ScoresTensor,
            DetectionScoreTensorView Scores,
            Tensor<float>? ClassesTensor,
            DetectionVectorTensorView? Classes);

        private readonly record struct DetectionTensorView(int Rows, int Columns, bool HasBatchDimension);

        private readonly record struct DetectionBoxTensorView(int Rows, bool HasBatchDimension);

        private readonly record struct DetectionVectorTensorView(int Rows, DetectionVectorTensorLayout Layout);

        private readonly record struct DetectionScoreTensorView(
            DetectionScoreTensorKind Kind,
            DetectionVectorTensorView Vector,
            DetectionScoreMatrixTensorView Matrix)
        {
            public static DetectionScoreTensorView FromVector(DetectionVectorTensorView view) =>
                new DetectionScoreTensorView(DetectionScoreTensorKind.Vector, view, default);

            public static DetectionScoreTensorView FromMatrix(DetectionScoreMatrixTensorView view) =>
                new DetectionScoreTensorView(DetectionScoreTensorKind.Matrix, default, view);
        }

        private readonly record struct DetectionScoreMatrixTensorView(int Rows, int ClassCount, DetectionScoreMatrixTensorLayout Layout);

        private enum DetectionScoreTensorKind
        {
            Vector,
            Matrix
        }

        private enum DetectionVectorTensorLayout
        {
            Vector,
            BatchVector,
            ColumnVector,
            BatchColumnVector
        }

        private enum DetectionScoreMatrixTensorLayout
        {
            Matrix,
            BatchMatrix
        }

        private enum DetectionBoxFormat
        {
            Xyxy,
            Xywh,
            Yxyx
        }
    }

    public sealed class SemanticSegmentationPostprocessor : IDeepLearningPostprocessor, IDeepLearningPostprocessorDescriptor
    {
        private static readonly string[] SupportedKeyAliases =
        {
            "semantic-segmentation",
            "multiclass-segmentation",
            "multi-class-segmentation",
            "segmentation",
            "deeplab",
            "unet-segmentation"
        };

        public string Key => "semantic-segmentation";
        public IReadOnlyCollection<string> SupportedKeys => SupportedKeyAliases;

        public bool CanProcess(DeepLearningPostprocessRequest request)
        {
            Tensor<float>? output = request.PrimaryOutput;
            if (output == null)
            {
                return false;
            }

            if (!HasSemanticSegmentationRequest(request) && !HasExplicitLabelMapHint(request.Metadata))
            {
                return false;
            }

            return TryCreateSemanticView(output, request.Metadata, request.Labels.Count, out SemanticSegmentationTensorView view) && view.ClassCount > 1 ||
                TryCreateLabelMapView(output, request.Metadata, out _);
        }

        public IReadOnlyList<YoloResult> Process(DeepLearningPostprocessRequest request)
        {
            Tensor<float> output = request.PrimaryOutput
                ?? throw new InvalidOperationException("Semantic segmentation postprocessor requires at least one output tensor.");

            if (TryCreateSemanticView(output, request.Metadata, request.Labels.Count, out SemanticSegmentationTensorView view))
            {
                return ProcessSemanticScores(output, view, request);
            }

            if (TryCreateLabelMapView(output, request.Metadata, out SemanticSegmentationLabelMapView labelMapView))
            {
                return ProcessLabelMap(output, labelMapView, request);
            }

            throw new NotSupportedException(
                $"Semantic segmentation postprocessor supports [C,H,W], [1,C,H,W], [H,W,C], [1,H,W,C], [H,W], [1,H,W] or [1,1,H,W] outputs, shape=[{string.Join(", ", output.Dimensions.ToArray())}]");
        }

        private static IReadOnlyList<YoloResult> ProcessSemanticScores(
            Tensor<float> output,
            SemanticSegmentationTensorView view,
            DeepLearningPostprocessRequest request)
        {
            int ignoredClassId = ResolveIgnoredClassId(request.Metadata, view.ClassCount);
            int classIdOffset = ResolveClassIdOffset(request.Metadata);
            var accumulators = new Dictionary<int, SemanticSegmentationAccumulator>();

            for (int row = 0; row < view.Height; row++)
            {
                for (int col = 0; col < view.Width; col++)
                {
                    if (!TryResolvePixelClass(
                        output,
                        view,
                        row,
                        col,
                        request.ScoreNormalization,
                        ignoredClassId,
                        out int channel,
                        out float score) ||
                        score < request.ConfidenceThreshold)
                    {
                        continue;
                    }

                    int classId = Math.Max(0, channel + classIdOffset);
                    if (!accumulators.TryGetValue(classId, out SemanticSegmentationAccumulator? accumulator))
                    {
                        accumulator = new SemanticSegmentationAccumulator(view.Height, view.Width);
                        accumulators[classId] = accumulator;
                    }

                    accumulator.Add(row, col, score);
                }
            }

            return BuildResultsFromAccumulators(accumulators, view.Height, view.Width, request.InputWidth, request.InputHeight);
        }

        private static IReadOnlyList<YoloResult> ProcessLabelMap(
            Tensor<float> output,
            SemanticSegmentationLabelMapView view,
            DeepLearningPostprocessRequest request)
        {
            const float labelMapConfidence = 1f;
            if (labelMapConfidence < request.ConfidenceThreshold)
            {
                return Array.Empty<YoloResult>();
            }

            int ignoredClassId = ResolveLabelMapIgnoredClassId(request.Metadata);
            int classIdOffset = ResolveClassIdOffset(request.Metadata);
            var accumulators = new Dictionary<int, SemanticSegmentationAccumulator>();

            for (int row = 0; row < view.Height; row++)
            {
                for (int col = 0; col < view.Width; col++)
                {
                    if (!TryReadLabelMapClassId(output, view, row, col, out int rawClassId) ||
                        rawClassId < 0 ||
                        rawClassId == ignoredClassId)
                    {
                        continue;
                    }

                    int classId = Math.Max(0, rawClassId + classIdOffset);
                    if (!accumulators.TryGetValue(classId, out SemanticSegmentationAccumulator? accumulator))
                    {
                        accumulator = new SemanticSegmentationAccumulator(view.Height, view.Width);
                        accumulators[classId] = accumulator;
                    }

                    accumulator.Add(row, col, labelMapConfidence);
                }
            }

            return BuildResultsFromAccumulators(accumulators, view.Height, view.Width, request.InputWidth, request.InputHeight);
        }

        private static IReadOnlyList<YoloResult> BuildResultsFromAccumulators(
            Dictionary<int, SemanticSegmentationAccumulator> accumulators,
            int height,
            int width,
            int inputWidth,
            int inputHeight)
        {
            if (accumulators.Count == 0)
            {
                return Array.Empty<YoloResult>();
            }

            float scaleX = inputWidth > 0 ? inputWidth / (float)width : 1f;
            float scaleY = inputHeight > 0 ? inputHeight / (float)height : 1f;
            var results = new List<YoloResult>();
            foreach (KeyValuePair<int, SemanticSegmentationAccumulator> pair in accumulators)
            {
                SemanticSegmentationAccumulator accumulator = pair.Value;
                if (!accumulator.HasPixels)
                {
                    accumulator.Dispose();
                    continue;
                }

                float left = accumulator.MinCol * scaleX;
                float top = accumulator.MinRow * scaleY;
                float right = (accumulator.MaxCol + 1) * scaleX;
                float bottom = (accumulator.MaxRow + 1) * scaleY;
                var result = new YoloResult();
                result.SetDetectionData(
                    centerX: (left + right) / 2f,
                    centerY: (top + bottom) / 2f,
                    width: Math.Max(0f, right - left),
                    height: Math.Max(0f, bottom - top),
                    confidence: accumulator.MaxScore,
                    classId: pair.Key);
                result.MaskData = accumulator.DetachMask();
                results.Add(result);
            }

            return results
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.ClassId)
                .ToArray();
        }

        private static bool HasSemanticSegmentationRequest(DeepLearningPostprocessRequest request)
        {
            if (MatchesSupportedKey(request.AlgorithmKey) || MatchesSupportedKey(request.TaskKey))
            {
                return true;
            }

            return TryGetAnyMetadataValue(
                request.Metadata,
                new[] { "task", "postprocessor", "postprocess", "postprocessor_key", "algorithm", "algorithm_key" },
                out string metadataKey) && MatchesSupportedKey(metadataKey);
        }

        private static bool TryCreateSemanticView(
            Tensor<float> output,
            IReadOnlyDictionary<string, string>? metadata,
            int labelCount,
            out SemanticSegmentationTensorView view)
        {
            int[] dimensions = output.Dimensions.ToArray();
            view = default;
            if (dimensions.Any(dimension => dimension <= 0))
            {
                return false;
            }

            string layout = ResolveLayout(metadata);
            if (dimensions.Length == 3)
            {
                if (layout == "hwc")
                {
                    view = new SemanticSegmentationTensorView(dimensions[2], dimensions[0], dimensions[1], SemanticSegmentationTensorLayout.Hwc);
                    return view.ClassCount > 1;
                }

                if (layout == "chw")
                {
                    view = new SemanticSegmentationTensorView(dimensions[0], dimensions[1], dimensions[2], SemanticSegmentationTensorLayout.Chw);
                    return view.ClassCount > 1;
                }

                if (labelCount > 0 && dimensions[2] == labelCount)
                {
                    view = new SemanticSegmentationTensorView(dimensions[2], dimensions[0], dimensions[1], SemanticSegmentationTensorLayout.Hwc);
                    return view.ClassCount > 1;
                }

                view = new SemanticSegmentationTensorView(dimensions[0], dimensions[1], dimensions[2], SemanticSegmentationTensorLayout.Chw);
                return view.ClassCount > 1;
            }

            if (dimensions.Length == 4 && dimensions[0] == 1)
            {
                if (layout == "nhwc" || labelCount > 0 && dimensions[3] == labelCount)
                {
                    view = new SemanticSegmentationTensorView(dimensions[3], dimensions[1], dimensions[2], SemanticSegmentationTensorLayout.Nhwc);
                    return view.ClassCount > 1;
                }

                if (layout == "nchw" || dimensions[1] > 1)
                {
                    view = new SemanticSegmentationTensorView(dimensions[1], dimensions[2], dimensions[3], SemanticSegmentationTensorLayout.Nchw);
                    return view.ClassCount > 1;
                }
            }

            return false;
        }

        private static bool TryCreateLabelMapView(
            Tensor<float> output,
            IReadOnlyDictionary<string, string>? metadata,
            out SemanticSegmentationLabelMapView view)
        {
            int[] dimensions = output.Dimensions.ToArray();
            view = default;
            if (dimensions.Any(dimension => dimension <= 0))
            {
                return false;
            }

            string layout = ResolveLayout(metadata);
            if (dimensions.Length == 2)
            {
                view = new SemanticSegmentationLabelMapView(dimensions[0], dimensions[1], SemanticSegmentationLabelMapLayout.Hw);
                return true;
            }

            if (dimensions.Length == 3)
            {
                if (layout == "hwc" || string.IsNullOrEmpty(layout) && dimensions[2] == 1)
                {
                    view = new SemanticSegmentationLabelMapView(dimensions[0], dimensions[1], SemanticSegmentationLabelMapLayout.Hwc);
                    return true;
                }

                if (layout == "chw" || layout == "bhw" || layout == "nchw" || string.IsNullOrEmpty(layout) && dimensions[0] == 1)
                {
                    view = new SemanticSegmentationLabelMapView(dimensions[1], dimensions[2], SemanticSegmentationLabelMapLayout.Chw);
                    return true;
                }
            }

            if (dimensions.Length == 4 && dimensions[0] == 1)
            {
                if (layout == "nhwc" || string.IsNullOrEmpty(layout) && dimensions[3] == 1)
                {
                    view = new SemanticSegmentationLabelMapView(dimensions[1], dimensions[2], SemanticSegmentationLabelMapLayout.Nhwc);
                    return true;
                }

                if (layout == "nchw" || string.IsNullOrEmpty(layout) && dimensions[1] == 1)
                {
                    view = new SemanticSegmentationLabelMapView(dimensions[2], dimensions[3], SemanticSegmentationLabelMapLayout.Nchw);
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolvePixelClass(
            Tensor<float> output,
            SemanticSegmentationTensorView view,
            int row,
            int col,
            DeepLearningScoreNormalization normalization,
            int ignoredClassId,
            out int classId,
            out float score)
        {
            classId = -1;
            score = 0;
            if (normalization == DeepLearningScoreNormalization.Softmax)
            {
                return TryResolveSoftmaxPixelClass(output, view, row, col, ignoredClassId, out classId, out score);
            }

            for (int channel = 0; channel < view.ClassCount; channel++)
            {
                if (channel == ignoredClassId)
                {
                    continue;
                }

                float candidate = NormalizeScore(ReadSemanticValue(output, view, channel, row, col), normalization);
                if (!float.IsFinite(candidate) || candidate <= score)
                {
                    continue;
                }

                score = candidate;
                classId = channel;
            }

            return classId >= 0;
        }

        private static bool TryResolveSoftmaxPixelClass(
            Tensor<float> output,
            SemanticSegmentationTensorView view,
            int row,
            int col,
            int ignoredClassId,
            out int classId,
            out float score)
        {
            classId = -1;
            score = 0;
            float max = float.NegativeInfinity;
            for (int channel = 0; channel < view.ClassCount; channel++)
            {
                float value = ReadSemanticValue(output, view, channel, row, col);
                if (float.IsFinite(value))
                {
                    max = Math.Max(max, value);
                }
            }

            if (!float.IsFinite(max))
            {
                return false;
            }

            double sum = 0;
            var exps = new double[view.ClassCount];
            for (int channel = 0; channel < view.ClassCount; channel++)
            {
                float value = ReadSemanticValue(output, view, channel, row, col);
                if (!float.IsFinite(value))
                {
                    continue;
                }

                double exp = Math.Exp(value - max);
                exps[channel] = exp;
                sum += exp;
            }

            if (sum <= 0)
            {
                return false;
            }

            for (int channel = 0; channel < view.ClassCount; channel++)
            {
                if (channel == ignoredClassId)
                {
                    continue;
                }

                float candidate = (float)(exps[channel] / sum);
                if (candidate <= score)
                {
                    continue;
                }

                score = candidate;
                classId = channel;
            }

            return classId >= 0;
        }

        private static float ReadSemanticValue(Tensor<float> output, SemanticSegmentationTensorView view, int channel, int row, int col)
        {
            return view.Layout switch
            {
                SemanticSegmentationTensorLayout.Chw => output[channel, row, col],
                SemanticSegmentationTensorLayout.Hwc => output[row, col, channel],
                SemanticSegmentationTensorLayout.Nchw => output[0, channel, row, col],
                SemanticSegmentationTensorLayout.Nhwc => output[0, row, col, channel],
                _ => 0f
            };
        }

        private static bool TryReadLabelMapClassId(
            Tensor<float> output,
            SemanticSegmentationLabelMapView view,
            int row,
            int col,
            out int classId)
        {
            classId = -1;
            float value = ReadLabelMapValue(output, view, row, col);
            if (!float.IsFinite(value))
            {
                return false;
            }

            int rounded = (int)MathF.Round(value);
            if (MathF.Abs(value - rounded) > 0.001f)
            {
                return false;
            }

            classId = rounded;
            return true;
        }

        private static float ReadLabelMapValue(Tensor<float> output, SemanticSegmentationLabelMapView view, int row, int col)
        {
            return view.Layout switch
            {
                SemanticSegmentationLabelMapLayout.Hw => output[row, col],
                SemanticSegmentationLabelMapLayout.Chw => output[0, row, col],
                SemanticSegmentationLabelMapLayout.Hwc => output[row, col, 0],
                SemanticSegmentationLabelMapLayout.Nchw => output[0, 0, row, col],
                SemanticSegmentationLabelMapLayout.Nhwc => output[0, row, col, 0],
                _ => 0f
            };
        }

        private static float NormalizeScore(float value, DeepLearningScoreNormalization normalization)
        {
            return normalization == DeepLearningScoreNormalization.Sigmoid
                ? 1f / (1f + MathF.Exp(-value))
                : value;
        }

        private static int ResolveClassIdOffset(IReadOnlyDictionary<string, string>? metadata)
        {
            if (TryGetAnyMetadataValue(metadata, new[] { "class_id_offset", "class_offset", "label_offset" }, out string value) &&
                int.TryParse(value, out int offset))
            {
                return offset;
            }

            return 0;
        }

        private static int ResolveIgnoredClassId(IReadOnlyDictionary<string, string>? metadata, int classCount)
        {
            if (!TryGetAnyMetadataValue(
                metadata,
                new[] { "background_class_id", "background_index", "ignore_class_id", "ignored_class_id", "no_object_class_id", "no_object_index" },
                out string value))
            {
                return -1;
            }

            string normalized = value.Trim();
            if (normalized.Equals("last", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("final", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(0, classCount - 1);
            }

            return int.TryParse(normalized, out int classId) && classId >= 0 && classId < classCount
                ? classId
                : -1;
        }

        private static int ResolveLabelMapIgnoredClassId(IReadOnlyDictionary<string, string>? metadata)
        {
            if (!TryGetAnyMetadataValue(
                metadata,
                new[] { "background_class_id", "background_index", "ignore_class_id", "ignored_class_id", "no_object_class_id", "no_object_index" },
                out string value))
            {
                return -1;
            }

            string normalized = value.Trim();
            return int.TryParse(normalized, out int classId) && classId >= 0
                ? classId
                : -1;
        }

        private static string ResolveLayout(IReadOnlyDictionary<string, string>? metadata)
        {
            if (!TryGetAnyMetadataValue(metadata, new[] { "segmentation_layout", "mask_layout", "output_layout", "layout" }, out string value))
            {
                return string.Empty;
            }

            return value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool MatchesSupportedKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string normalized = key.Trim();
            return SupportedKeyAliases.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasExplicitLabelMapHint(IReadOnlyDictionary<string, string>? metadata)
        {
            if (!TryGetAnyMetadataValue(
                metadata,
                new[] { "label_map", "is_label_map", "output_type", "output_format", "segmentation_format", "mask_format" },
                out string value))
            {
                return false;
            }

            string normalized = value.Trim().Replace("_", "-").ToLowerInvariant();
            return normalized is "1" or "true" or "yes" or "on" or "enabled" or "label-map" or "labelmap" or "class-map" or "class-id" or "class-ids" or "class-index" or "class-indices";
        }

        private static bool TryGetAnyMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            IReadOnlyList<string> keys,
            out string value)
        {
            value = string.Empty;
            if (metadata == null) return false;

            foreach (string key in keys)
            {
                foreach (KeyValuePair<string, string> pair in metadata)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value ?? string.Empty;
                        return true;
                    }
                }
            }

            return false;
        }

        private readonly record struct SemanticSegmentationTensorView(
            int ClassCount,
            int Height,
            int Width,
            SemanticSegmentationTensorLayout Layout);

        private enum SemanticSegmentationTensorLayout
        {
            Chw,
            Hwc,
            Nchw,
            Nhwc
        }

        private readonly record struct SemanticSegmentationLabelMapView(
            int Height,
            int Width,
            SemanticSegmentationLabelMapLayout Layout);

        private enum SemanticSegmentationLabelMapLayout
        {
            Hw,
            Chw,
            Hwc,
            Nchw,
            Nhwc
        }

        private sealed class SemanticSegmentationAccumulator : IDisposable
        {
            private Mat? _mask;

            public SemanticSegmentationAccumulator(int rows, int cols)
            {
                _mask = new Mat(rows, cols, MatType.CV_32FC1, Scalar.All(0));
            }

            public int MinRow { get; private set; } = int.MaxValue;
            public int MinCol { get; private set; } = int.MaxValue;
            public int MaxRow { get; private set; } = -1;
            public int MaxCol { get; private set; } = -1;
            public float MaxScore { get; private set; }
            public bool HasPixels => MaxRow >= 0 && MaxCol >= 0;

            public void Add(int row, int col, float score)
            {
                if (_mask != null)
                {
                    _mask.At<float>(row, col) = score;
                }

                MinRow = Math.Min(MinRow, row);
                MinCol = Math.Min(MinCol, col);
                MaxRow = Math.Max(MaxRow, row);
                MaxCol = Math.Max(MaxCol, col);
                MaxScore = Math.Max(MaxScore, score);
            }

            public Mat DetachMask()
            {
                Mat mask = _mask ?? new Mat();
                _mask = null;
                return mask;
            }

            public void Dispose()
            {
                _mask?.Dispose();
                _mask = null;
            }
        }
    }

    public sealed class HeatmapAnomalyPostprocessor : IDeepLearningPostprocessor, IDeepLearningPostprocessorDescriptor
    {
        private static readonly string[] SupportedKeyAliases =
        {
            "heatmap-anomaly",
            "anomaly-heatmap",
            "anomaly",
            "binary-segmentation",
            "segmentation-heatmap",
            "mask"
        };

        public string Key => "heatmap-anomaly";
        public IReadOnlyCollection<string> SupportedKeys => SupportedKeyAliases;

        public bool CanProcess(DeepLearningPostprocessRequest request)
        {
            Tensor<float>? output = request.PrimaryOutput;
            if (output == null || !TryCreateHeatmapView(output, out _))
            {
                return false;
            }

            if (MatchesSupportedKey(request.AlgorithmKey) || MatchesSupportedKey(request.TaskKey))
            {
                return true;
            }

            return TryGetAnyMetadataValue(
                request.Metadata,
                new[] { "task", "postprocessor", "postprocess", "postprocessor_key", "algorithm", "algorithm_key" },
                out string metadataKey) && MatchesSupportedKey(metadataKey);
        }

        public IReadOnlyList<YoloResult> Process(DeepLearningPostprocessRequest request)
        {
            Tensor<float> output = request.PrimaryOutput
                ?? throw new InvalidOperationException("热力图后处理需要至少一个输出张量。");
            if (!TryCreateHeatmapView(output, out HeatmapTensorView view))
            {
                throw new NotSupportedException(
                    $"热力图后处理仅支持 [H,W]、[1,H,W]、[H,W,1]、[1,1,H,W] 或 [1,H,W,1] 输出，当前 shape=[{string.Join(", ", output.Dimensions.ToArray())}]");
            }

            float threshold = request.ConfidenceThreshold;
            int minRow = view.Height;
            int minCol = view.Width;
            int maxRow = -1;
            int maxCol = -1;
            float maxScore = 0f;
            var mask = new Mat(view.Height, view.Width, MatType.CV_32FC1);

            for (int row = 0; row < view.Height; row++)
            {
                for (int col = 0; col < view.Width; col++)
                {
                    float score = NormalizeScore(ReadHeatmapValue(output, view, row, col), request.ScoreNormalization);
                    if (!float.IsFinite(score))
                    {
                        score = 0f;
                    }

                    mask.At<float>(row, col) = score;
                    if (score < threshold)
                    {
                        continue;
                    }

                    minRow = Math.Min(minRow, row);
                    minCol = Math.Min(minCol, col);
                    maxRow = Math.Max(maxRow, row);
                    maxCol = Math.Max(maxCol, col);
                    maxScore = Math.Max(maxScore, score);
                }
            }

            if (maxRow < 0 || maxCol < 0)
            {
                mask.Dispose();
                return Array.Empty<YoloResult>();
            }

            float scaleX = request.InputWidth > 0 ? request.InputWidth / (float)view.Width : 1f;
            float scaleY = request.InputHeight > 0 ? request.InputHeight / (float)view.Height : 1f;
            float left = minCol * scaleX;
            float top = minRow * scaleY;
            float right = (maxCol + 1) * scaleX;
            float bottom = (maxRow + 1) * scaleY;

            var result = new YoloResult();
            result.SetDetectionData(
                centerX: (left + right) / 2f,
                centerY: (top + bottom) / 2f,
                width: Math.Max(0f, right - left),
                height: Math.Max(0f, bottom - top),
                confidence: maxScore,
                classId: ResolveClassId(request.Metadata));
            result.MaskData = mask;

            return new[] { result };
        }

        private static bool TryCreateHeatmapView(Tensor<float> output, out HeatmapTensorView view)
        {
            int[] dimensions = output.Dimensions.ToArray();
            view = default;
            if (dimensions.Any(dimension => dimension <= 0))
            {
                return false;
            }

            if (dimensions.Length == 2)
            {
                view = new HeatmapTensorView(dimensions[0], dimensions[1], HeatmapTensorLayout.Hw);
                return true;
            }

            if (dimensions.Length == 3)
            {
                if (dimensions[2] == 1)
                {
                    view = new HeatmapTensorView(dimensions[0], dimensions[1], HeatmapTensorLayout.Hwc);
                    return true;
                }

                if (dimensions[0] >= 1)
                {
                    view = new HeatmapTensorView(dimensions[1], dimensions[2], HeatmapTensorLayout.Chw);
                    return true;
                }
            }

            if (dimensions.Length == 4 && dimensions[0] == 1)
            {
                if (dimensions[3] == 1)
                {
                    view = new HeatmapTensorView(dimensions[1], dimensions[2], HeatmapTensorLayout.Nhwc);
                    return true;
                }

                if (dimensions[1] >= 1)
                {
                    view = new HeatmapTensorView(dimensions[2], dimensions[3], HeatmapTensorLayout.Nchw);
                    return true;
                }
            }

            return false;
        }

        private static float ReadHeatmapValue(Tensor<float> output, HeatmapTensorView view, int row, int col)
        {
            return view.Layout switch
            {
                HeatmapTensorLayout.Hw => output[row, col],
                HeatmapTensorLayout.Chw => output[0, row, col],
                HeatmapTensorLayout.Hwc => output[row, col, 0],
                HeatmapTensorLayout.Nchw => output[0, 0, row, col],
                HeatmapTensorLayout.Nhwc => output[0, row, col, 0],
                _ => 0f
            };
        }

        private static float NormalizeScore(float value, DeepLearningScoreNormalization normalization)
        {
            return normalization == DeepLearningScoreNormalization.Sigmoid
                ? 1f / (1f + MathF.Exp(-value))
                : value;
        }

        private static int ResolveClassId(IReadOnlyDictionary<string, string>? metadata)
        {
            if (TryGetAnyMetadataValue(
                metadata,
                new[] { "class_id", "classId", "anomaly_class_id", "mask_class_id", "foreground_class_id" },
                out string value) &&
                int.TryParse(value, out int classId))
            {
                return Math.Max(0, classId);
            }

            return 0;
        }

        private static bool MatchesSupportedKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string normalized = key.Trim();
            return SupportedKeyAliases.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryGetAnyMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            IReadOnlyList<string> keys,
            out string value)
        {
            value = string.Empty;
            if (metadata == null) return false;

            foreach (string key in keys)
            {
                foreach (KeyValuePair<string, string> pair in metadata)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value ?? string.Empty;
                        return true;
                    }
                }
            }

            return false;
        }

        private readonly record struct HeatmapTensorView(int Height, int Width, HeatmapTensorLayout Layout);

        private enum HeatmapTensorLayout
        {
            Hw,
            Chw,
            Hwc,
            Nchw,
            Nhwc
        }
    }

    public sealed class ClassificationLogitsPostprocessor : IDeepLearningPostprocessor, IDeepLearningPostprocessorDescriptor
    {
        private static readonly string[] SupportedKeyAliases =
        {
            "classification",
            "classification-logits",
            "generic-classification",
            "image-classification",
            "resnet",
            "efficientnet"
        };

        public string Key => "classification";
        public IReadOnlyCollection<string> SupportedKeys => SupportedKeyAliases;

        public bool CanProcess(DeepLearningPostprocessRequest request)
        {
            Tensor<float>? output = request.PrimaryOutput;
            if (output == null || !IsSupportedClassificationTensor(output))
            {
                return false;
            }

            if (MatchesSupportedKey(request.AlgorithmKey) || MatchesSupportedKey(request.TaskKey))
            {
                return true;
            }

            return TryGetMetadataValue(request.Metadata, "task", out string task) && MatchesSupportedKey(task);
        }

        public IReadOnlyList<YoloResult> Process(DeepLearningPostprocessRequest request)
        {
            Tensor<float> output = request.PrimaryOutput
                ?? throw new InvalidOperationException("分类后处理需要至少一个输出张量。");
            if (!IsSupportedClassificationTensor(output))
            {
                throw new NotSupportedException(
                    $"分类后处理仅支持 rank-1 或 rank-2 输出，当前 shape=[{string.Join(", ", output.Dimensions.ToArray())}]");
            }

            float[] scores = ReadFirstBatchScores(output);
            if (request.ScoreNormalization == DeepLearningScoreNormalization.Softmax)
            {
                scores = ApplySoftmax(scores);
            }
            else if (request.ScoreNormalization == DeepLearningScoreNormalization.Sigmoid)
            {
                scores = scores.Select(ApplySigmoid).ToArray();
            }

            var results = new List<YoloResult>();
            for (int classId = 0; classId < scores.Length; classId++)
            {
                float score = scores[classId];
                if (!float.IsFinite(score) || score < request.ConfidenceThreshold)
                {
                    continue;
                }

                var result = new YoloResult();
                result.SetClassificationData(score, classId);
                results.Add(result);
            }

            IEnumerable<YoloResult> orderedResults = results
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.ClassId);
            int topK = ResolveTopK(request.Metadata);
            if (topK > 0)
            {
                orderedResults = orderedResults.Take(topK);
            }

            return orderedResults.ToArray();
        }

        private static bool IsSupportedClassificationTensor(Tensor<float> output)
        {
            int rank = output.Dimensions.Length;
            if (rank == 1)
            {
                return output.Dimensions[0] > 0;
            }

            return rank == 2 && output.Dimensions[0] > 0 && output.Dimensions[1] > 0;
        }

        private static float[] ReadFirstBatchScores(Tensor<float> output)
        {
            if (output.Dimensions.Length == 1)
            {
                int classCount = output.Dimensions[0];
                var scores = new float[classCount];
                for (int classId = 0; classId < classCount; classId++)
                {
                    scores[classId] = output[classId];
                }

                return scores;
            }

            int count = output.Dimensions[1];
            var batchedScores = new float[count];
            for (int classId = 0; classId < count; classId++)
            {
                batchedScores[classId] = output[0, classId];
            }

            return batchedScores;
        }

        private static float[] ApplySoftmax(IReadOnlyList<float> logits)
        {
            float max = logits
                .Where(float.IsFinite)
                .DefaultIfEmpty(float.NegativeInfinity)
                .Max();
            if (!float.IsFinite(max))
            {
                return Enumerable.Repeat(0f, logits.Count).ToArray();
            }

            double sum = 0;
            var exps = new double[logits.Count];
            for (int i = 0; i < logits.Count; i++)
            {
                if (!float.IsFinite(logits[i]))
                {
                    exps[i] = 0;
                    continue;
                }

                double value = Math.Exp(logits[i] - max);
                exps[i] = value;
                sum += value;
            }

            if (sum <= 0)
            {
                return Enumerable.Repeat(0f, logits.Count).ToArray();
            }

            return exps.Select(value => (float)(value / sum)).ToArray();
        }

        private static float ApplySigmoid(float value)
        {
            return float.IsFinite(value)
                ? 1f / (1f + MathF.Exp(-value))
                : 0f;
        }

        private static int ResolveTopK(IReadOnlyDictionary<string, string>? metadata)
        {
            if (!TryGetAnyMetadataValue(metadata, new[] { "top_k", "topk", "max_results", "classification_limit", "limit" }, out string value) ||
                !int.TryParse(value, out int topK))
            {
                return 0;
            }

            return Math.Max(0, topK);
        }

        private static bool MatchesSupportedKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string normalized = key.Trim();
            return SupportedKeyAliases.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryGetMetadataValue(IReadOnlyDictionary<string, string>? metadata, string key, out string value)
        {
            value = string.Empty;
            if (metadata == null) return false;

            foreach (KeyValuePair<string, string> pair in metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value ?? string.Empty;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetAnyMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            IReadOnlyList<string> keys,
            out string value)
        {
            value = string.Empty;
            if (metadata == null) return false;

            foreach (string key in keys)
            {
                if (TryGetMetadataValue(metadata, key, out value))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
