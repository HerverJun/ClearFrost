// ============================================================================
// 文件名: YoloBenchmarkProbe.cs
// 作者: 蘅芜君
// 描述:   YOLO ONNX 样本推理性能基准探针
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClearFrost.Yolo
{
    public sealed class YoloBenchmarkOptions
    {
        public string ModelPath { get; init; } = string.Empty;
        public string? ImagePath { get; init; }
        public int YoloVersion { get; init; }
        public int WarmupIterations { get; init; } = 2;
        public int Iterations { get; init; } = 10;
        public float Confidence { get; init; } = 0.25f;
        public float IouThreshold { get; init; } = 0.45f;
        public bool UseGpu { get; init; }
        public YoloPreprocessingMode PreprocessingMode { get; init; } = YoloPreprocessingMode.StandardLetterBox;
        public YoloTaskType TaskMode { get; init; } = YoloTaskType.Auto;
    }

    public sealed class YoloBenchmarkReport
    {
        public bool GpuRequested { get; init; }
        public bool GpuActive { get; init; }
        public string ExecutionProvider { get; init; } = string.Empty;
        public string GpuFailureReason { get; init; } = string.Empty;
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }
        public int WarmupIterations { get; init; }
        public int Iterations { get; init; }
        public int LastDetectionCount { get; init; }
        public double AverageMs { get; init; }
        public double P50Ms { get; init; }
        public double P95Ms { get; init; }
        public double Fps { get; init; }
        public double AveragePreprocessMs { get; init; }
        public double AverageInferenceMs { get; init; }
        public double AveragePostprocessMs { get; init; }
        public int TotalResultCount { get; init; }
        public int InvalidResultCount { get; init; }
        public int NaNResultCount { get; init; }
        public int OutOfBoundsResultCount { get; init; }
        public bool ResultStructureValid { get; init; }
        public long WorkingSetBeforeBytes { get; init; }
        public long WorkingSetAfterBytes { get; init; }
        public long PeakWorkingSetBytes { get; init; }
        public long PrivateBytesBefore { get; init; }
        public long PrivateBytesAfter { get; init; }
        public long PeakPrivateBytes { get; init; }
    }

    public static class YoloBenchmarkProbe
    {
        public static YoloBenchmarkReport Run(YoloBenchmarkOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.ModelPath))
                throw new ArgumentException("模型路径不能为空", nameof(options));
            if (options.Iterations <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.Iterations));
            if (options.WarmupIterations < 0)
                throw new ArgumentOutOfRangeException(nameof(options.WarmupIterations));

            using var detector = new YoloDetector(new YoloDetectorConfig
            {
                ModelPath = options.ModelPath,
                UseGpu = options.UseGpu,
                YoloVersion = options.YoloVersion,
                PreprocessingMode = options.PreprocessingMode,
                TaskType = options.TaskMode
            });
            using Mat image = LoadBenchmarkImage(options);

            for (int i = 0; i < options.WarmupIterations; i++)
            {
                using ResultDisposer _ = RunOnce(detector, image, options);
            }

            double[] totals = new double[options.Iterations];
            double[] preprocess = new double[options.Iterations];
            double[] inference = new double[options.Iterations];
            double[] postprocess = new double[options.Iterations];
            int lastDetectionCount = 0;
            int totalResultCount = 0;
            int invalidResultCount = 0;
            int nanResultCount = 0;
            int outOfBoundsResultCount = 0;
            Process process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBefore = process.WorkingSet64;
            long privateBytesBefore = process.PrivateMemorySize64;
            long peakWorkingSet = workingSetBefore;
            long peakPrivateBytes = privateBytesBefore;

            for (int i = 0; i < options.Iterations; i++)
            {
                using ResultDisposer result = RunOnce(detector, image, options);
                InferenceMetrics metrics = detector.LastMetrics ?? new InferenceMetrics();
                totals[i] = metrics.TotalMs;
                preprocess[i] = metrics.PreprocessMs;
                inference[i] = metrics.InferenceMs;
                postprocess[i] = metrics.PostprocessMs;
                lastDetectionCount = result.Count;
                totalResultCount += result.Count;
                invalidResultCount += result.InvalidResultCount;
                nanResultCount += result.NaNResultCount;
                outOfBoundsResultCount += result.OutOfBoundsResultCount;
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
            }

            process.Refresh();

            Array.Sort(totals);
            double average = totals.Average();
            return new YoloBenchmarkReport
            {
                GpuRequested = options.UseGpu,
                GpuActive = detector.GpuActive,
                ExecutionProvider = detector.ExecutionProvider,
                GpuFailureReason = detector.GpuFailureReason,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                WarmupIterations = options.WarmupIterations,
                Iterations = options.Iterations,
                LastDetectionCount = lastDetectionCount,
                AverageMs = average,
                P50Ms = PercentileSorted(totals, 0.50),
                P95Ms = PercentileSorted(totals, 0.95),
                Fps = average > 0 ? 1000.0 / average : 0,
                AveragePreprocessMs = preprocess.Average(),
                AverageInferenceMs = inference.Average(),
                AveragePostprocessMs = postprocess.Average(),
                TotalResultCount = totalResultCount,
                InvalidResultCount = invalidResultCount,
                NaNResultCount = nanResultCount,
                OutOfBoundsResultCount = outOfBoundsResultCount,
                ResultStructureValid = invalidResultCount == 0 && nanResultCount == 0 && outOfBoundsResultCount == 0,
                WorkingSetBeforeBytes = workingSetBefore,
                WorkingSetAfterBytes = process.WorkingSet64,
                PeakWorkingSetBytes = peakWorkingSet,
                PrivateBytesBefore = privateBytesBefore,
                PrivateBytesAfter = process.PrivateMemorySize64,
                PeakPrivateBytes = peakPrivateBytes
            };
        }

        private static Mat LoadBenchmarkImage(YoloBenchmarkOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.ImagePath))
            {
                throw new ArgumentException("真实验证图像路径不能为空；禁止使用 synthetic 图像替代外部验证输入。", nameof(options.ImagePath));
            }

            Mat image = Cv2.ImRead(options.ImagePath, ImreadModes.Color);
            if (image.Empty())
            {
                image.Dispose();
                throw new InvalidOperationException($"无法读取真实验证图片: {options.ImagePath}");
            }

            return image;
        }

        private static ResultDisposer RunOnce(YoloDetector detector, Mat image, YoloBenchmarkOptions options)
        {
            List<YoloResult> results = detector.Inference(
                image,
                options.Confidence,
                options.IouThreshold,
                globalIou: false,
                preprocessingMode: (int)options.PreprocessingMode);
            return new ResultDisposer(results, image.Width, image.Height);
        }

        private static double PercentileSorted(IReadOnlyList<double> values, double percentile)
        {
            double position = (values.Count - 1) * percentile;
            int left = (int)Math.Floor(position);
            int right = (int)Math.Ceiling(position);
            if (left == right)
            {
                return values[left];
            }

            double fraction = position - left;
            return values[left] + (values[right] - values[left]) * fraction;
        }

        private sealed class ResultDisposer : IDisposable
        {
            private readonly List<YoloResult> _results;
            private int _invalidResultCount;
            private int _nanResultCount;
            private int _outOfBoundsResultCount;

            public ResultDisposer(List<YoloResult> results, int imageWidth, int imageHeight)
            {
                _results = results;
                Count = results.Count;
                foreach (YoloResult result in results)
                {
                    if (ContainsNaN(result))
                    {
                        _nanResultCount++;
                    }

                    if (IsOutOfBounds(result, imageWidth, imageHeight))
                    {
                        _outOfBoundsResultCount++;
                    }

                    if (ContainsNaN(result) || IsOutOfBounds(result, imageWidth, imageHeight) ||
                        result.ClassId < 0 || result.Confidence < 0 || result.Confidence > 1)
                    {
                        _invalidResultCount++;
                    }
                }
            }

            public int Count { get; }
            public int InvalidResultCount => _invalidResultCount;
            public int NaNResultCount => _nanResultCount;
            public int OutOfBoundsResultCount => _outOfBoundsResultCount;

            private static bool ContainsNaN(YoloResult result)
            {
                return !float.IsFinite(result.CenterX) ||
                    !float.IsFinite(result.CenterY) ||
                    !float.IsFinite(result.Width) ||
                    !float.IsFinite(result.Height) ||
                    !float.IsFinite(result.Confidence) ||
                    (result.Angle.HasValue && !float.IsFinite(result.Angle.Value)) ||
                    result.KeyPoints.Any(point =>
                        !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Score));
            }

            private static bool IsOutOfBounds(YoloResult result, int imageWidth, int imageHeight)
            {
                if (result.DataKind == YoloResultDataKind.Classification)
                {
                    return false;
                }

                return result.Width < 0 || result.Height < 0 ||
                    result.Left < 0 || result.Top < 0 ||
                    result.Right > imageWidth || result.Bottom > imageHeight ||
                    result.Right < result.Left || result.Bottom < result.Top;
            }

            public void Dispose()
            {
                foreach (YoloResult result in _results)
                {
                    result.Dispose();
                }
            }
        }
    }
}
