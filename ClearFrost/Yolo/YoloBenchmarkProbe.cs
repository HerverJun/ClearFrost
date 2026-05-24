// ============================================================================
// 文件名: YoloBenchmarkProbe.cs
// 作者: 蘅芜君
// 描述:   YOLO ONNX 样本推理性能基准探针
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;
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
        public string ExecutionProvider { get; init; } = string.Empty;
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
            using Mat image = LoadBenchmarkImage(options, detector.ModelDescriptor);

            for (int i = 0; i < options.WarmupIterations; i++)
            {
                using ResultDisposer _ = RunOnce(detector, image, options);
            }

            double[] totals = new double[options.Iterations];
            double[] preprocess = new double[options.Iterations];
            double[] inference = new double[options.Iterations];
            double[] postprocess = new double[options.Iterations];
            int lastDetectionCount = 0;

            for (int i = 0; i < options.Iterations; i++)
            {
                using ResultDisposer result = RunOnce(detector, image, options);
                InferenceMetrics metrics = detector.LastMetrics ?? new InferenceMetrics();
                totals[i] = metrics.TotalMs;
                preprocess[i] = metrics.PreprocessMs;
                inference[i] = metrics.InferenceMs;
                postprocess[i] = metrics.PostprocessMs;
                lastDetectionCount = result.Count;
            }

            Array.Sort(totals);
            double average = totals.Average();
            return new YoloBenchmarkReport
            {
                ExecutionProvider = detector.ExecutionProvider,
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
                AveragePostprocessMs = postprocess.Average()
            };
        }

        private static Mat LoadBenchmarkImage(YoloBenchmarkOptions options, YoloModelDescriptor descriptor)
        {
            if (!string.IsNullOrWhiteSpace(options.ImagePath))
            {
                Mat image = Cv2.ImRead(options.ImagePath, ImreadModes.Color);
                if (image.Empty())
                {
                    image.Dispose();
                    throw new InvalidOperationException($"无法读取基准图片: {options.ImagePath}");
                }

                return image;
            }

            int width = descriptor.PreprocessProfile.InputWidth > 0 ? descriptor.PreprocessProfile.InputWidth : 640;
            int height = descriptor.PreprocessProfile.InputHeight > 0 ? descriptor.PreprocessProfile.InputHeight : 640;
            Mat synthetic = new Mat(height, width, MatType.CV_8UC3, new Scalar(40, 60, 80));
            Cv2.Rectangle(
                synthetic,
                new Rect(width / 5, height / 5, Math.Max(8, width / 3), Math.Max(8, height / 4)),
                new Scalar(180, 180, 180),
                -1);
            Cv2.Circle(
                synthetic,
                new OpenCvSharp.Point(width * 2 / 3, height * 2 / 3),
                Math.Max(4, Math.Min(width, height) / 10),
                new Scalar(220, 130, 90),
                -1);
            return synthetic;
        }

        private static ResultDisposer RunOnce(YoloDetector detector, Mat image, YoloBenchmarkOptions options)
        {
            List<YoloResult> results = detector.Inference(
                image,
                options.Confidence,
                options.IouThreshold,
                globalIou: false,
                preprocessingMode: (int)options.PreprocessingMode);
            return new ResultDisposer(results);
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

            public ResultDisposer(List<YoloResult> results)
            {
                _results = results;
                Count = results.Count;
            }

            public int Count { get; }

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
