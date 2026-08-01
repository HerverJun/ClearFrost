using ClearFrost.Yolo;
using System.Text.Json;
using System.Text.Json.Serialization;

return YoloProbe.Run(args);

internal static class YoloProbe
{
    public static int Run(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            ProbeOptions.PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            ProbeOptions.PrintUsage();
            return 0;
        }

        try
        {
            YoloExportProbeReport report = YoloExportProbe.Inspect(
                options.ModelPath,
                options.YoloVersion,
                options.PreprocessingMode,
                options.TaskMode);

            PrintSummary(report.Descriptor);
            YoloBenchmarkReport? benchmark = null;
            if (options.RunBenchmark)
            {
                benchmark = YoloBenchmarkProbe.Run(new YoloBenchmarkOptions
                {
                    ModelPath = options.ModelPath,
                    ImagePath = options.ImagePath,
                    YoloVersion = options.YoloVersion,
                    WarmupIterations = options.WarmupIterations,
                    Iterations = options.BenchmarkIterations,
                    Confidence = options.Confidence,
                    IouThreshold = options.IouThreshold,
                    UseGpu = options.UseGpu,
                    PreprocessingMode = options.PreprocessingMode,
                    TaskMode = options.TaskMode
                });
                PrintBenchmark(benchmark);
            }

            ExecutionProviderValidationResult? providerValidation = null;
            if (!string.IsNullOrWhiteSpace(options.RequiredExecutionProvider))
            {
                providerValidation = ExecutionProviderValidation.Validate(
                    options.RequiredExecutionProvider,
                    benchmark?.ExecutionProvider);
                Console.WriteLine($"  Provider check: {providerValidation.Status}");
                if (!providerValidation.IsSatisfied)
                {
                    Console.Error.WriteLine($"Provider validation BLOCKED: {providerValidation.FailureReason}");
                }
            }

            if (!string.IsNullOrWhiteSpace(options.JsonOutputPath))
            {
                SaveJson(options.JsonOutputPath, report.Descriptor, benchmark, providerValidation);
                Console.WriteLine($"JSON report: {options.JsonOutputPath}");
            }

            if (providerValidation is { IsSatisfied: false })
            {
                return 3;
            }

            return report.Descriptor.IsSupported ? 0 : 1;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"YOLO probe failed: {ex.Message}");
            return 2;
        }
    }

    private static void PrintSummary(YoloModelDescriptor descriptor)
    {
        Console.WriteLine("ClearFrost YOLO export probe");
        Console.WriteLine($"  Model:        {descriptor.ModelPath}");
        Console.WriteLine($"  Task:         {descriptor.TaskType} / {descriptor.ExecutionTaskMode}");
        Console.WriteLine($"  Version:      {(string.IsNullOrWhiteSpace(descriptor.Version) ? descriptor.MajorVersion : descriptor.Version)}");
        Console.WriteLine($"  Input:        {descriptor.InputName} [{string.Join(", ", descriptor.InputDimensions)}]");
        Console.WriteLine($"  Outputs:      {descriptor.Outputs.Count}");
        foreach (YoloOutputDescriptor output in descriptor.Outputs)
        {
            Console.WriteLine($"    - {output.Name} [{string.Join(", ", output.Dimensions)}]");
        }
        Console.WriteLine($"  Classes:      {descriptor.Labels.Length}");
        Console.WriteLine($"  Preprocess:   {descriptor.PreprocessProfile.Mode}");
        Console.WriteLine($"  Postprocess:  {descriptor.PostprocessProfile.Layout}");
        Console.WriteLine($"  Built-in NMS: {descriptor.HasBuiltInNms}");
        Console.WriteLine($"  End-to-end:   {descriptor.IsEndToEndNmsFree}");
        Console.WriteLine($"  Supported:    {descriptor.IsSupported} ({descriptor.SupportMessage})");
    }

    private static void PrintBenchmark(YoloBenchmarkReport benchmark)
    {
        Console.WriteLine();
        Console.WriteLine("Benchmark");
        Console.WriteLine($"  Provider:      {benchmark.ExecutionProvider}");
        Console.WriteLine($"  Image:         {benchmark.ImageWidth}x{benchmark.ImageHeight}");
        Console.WriteLine($"  Warmup:        {benchmark.WarmupIterations}");
        Console.WriteLine($"  Iterations:    {benchmark.Iterations}");
        Console.WriteLine($"  Detections:    {benchmark.LastDetectionCount}");
        Console.WriteLine($"  Avg:           {benchmark.AverageMs:F2} ms");
        Console.WriteLine($"  P50:           {benchmark.P50Ms:F2} ms");
        Console.WriteLine($"  P95:           {benchmark.P95Ms:F2} ms");
        Console.WriteLine($"  FPS:           {benchmark.Fps:F1}");
        Console.WriteLine($"  Preprocess:    {benchmark.AveragePreprocessMs:F2} ms");
        Console.WriteLine($"  Inference:     {benchmark.AverageInferenceMs:F2} ms");
        Console.WriteLine($"  Postprocess:   {benchmark.AveragePostprocessMs:F2} ms");
    }

    private static void SaveJson(
        string path,
        YoloModelDescriptor descriptor,
        YoloBenchmarkReport? benchmark,
        ExecutionProviderValidationResult? providerValidation)
    {
        var payload = new
        {
            Descriptor = descriptor,
            Benchmark = benchmark,
            ProviderValidation = providerValidation
        };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        File.WriteAllText(path, json);
    }
}

internal sealed class ProbeOptions
{
    public string ModelPath { get; private init; } = string.Empty;
    public string? JsonOutputPath { get; private init; }
    public string? ImagePath { get; private init; }
    public int YoloVersion { get; private init; }
    public int WarmupIterations { get; private init; } = 2;
    public int BenchmarkIterations { get; private init; } = 10;
    public float Confidence { get; private init; } = 0.25f;
    public float IouThreshold { get; private init; } = 0.45f;
    public bool UseGpu { get; private init; }
    public bool RunBenchmark { get; private init; }
    public string? RequiredExecutionProvider { get; private init; }
    public YoloPreprocessingMode PreprocessingMode { get; private init; } = YoloPreprocessingMode.StandardLetterBox;
    public YoloTaskType TaskMode { get; private init; } = YoloTaskType.Auto;
    public bool ShowHelp { get; private init; }

    public static ProbeOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new ProbeOptions { ShowHelp = true };
        }

        string? modelPath = null;
        string? jsonOutputPath = null;
        string? imagePath = null;
        int yoloVersion = 0;
        int warmupIterations = 2;
        int benchmarkIterations = 10;
        float confidence = 0.25f;
        float iouThreshold = 0.45f;
        bool useGpu = false;
        bool runBenchmark = false;
        string? requiredExecutionProvider = null;
        YoloPreprocessingMode preprocessingMode = YoloPreprocessingMode.StandardLetterBox;
        YoloTaskType taskMode = YoloTaskType.Auto;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help")
            {
                return new ProbeOptions { ShowHelp = true };
            }
            if (arg is "-m" or "--model")
            {
                modelPath = ReadValue(args, ref i, arg);
                continue;
            }
            if (arg is "-o" or "--out")
            {
                jsonOutputPath = ReadValue(args, ref i, arg);
                continue;
            }
            if (arg is "--image")
            {
                imagePath = ReadValue(args, ref i, arg);
                continue;
            }
            if (arg is "--benchmark")
            {
                runBenchmark = true;
                continue;
            }
            if (arg is "--gpu")
            {
                useGpu = true;
                continue;
            }
            if (arg is "--require-provider")
            {
                requiredExecutionProvider = ReadValue(args, ref i, arg).Trim();
                if (string.IsNullOrWhiteSpace(requiredExecutionProvider))
                {
                    throw new ArgumentException("--require-provider 不能为空");
                }

                runBenchmark = true;
                continue;
            }
            if (arg is "--warmup")
            {
                warmupIterations = ParseNonNegativeInt(ReadValue(args, ref i, arg), arg);
                continue;
            }
            if (arg is "--iterations")
            {
                benchmarkIterations = ParsePositiveInt(ReadValue(args, ref i, arg), arg);
                continue;
            }
            if (arg is "--confidence")
            {
                confidence = ParseProbability(ReadValue(args, ref i, arg), arg);
                continue;
            }
            if (arg is "--iou")
            {
                iouThreshold = ParseProbability(ReadValue(args, ref i, arg), arg);
                continue;
            }
            if (arg is "--version")
            {
                string versionText = ReadValue(args, ref i, arg);
                if (!int.TryParse(versionText, out yoloVersion) || yoloVersion < 0)
                {
                    throw new ArgumentException("--version 必须是非负整数");
                }
                continue;
            }
            if (arg is "--preprocess")
            {
                preprocessingMode = ParsePreprocessingMode(ReadValue(args, ref i, arg));
                continue;
            }
            if (arg is "--task")
            {
                taskMode = ParseTaskMode(ReadValue(args, ref i, arg));
                continue;
            }
            if (modelPath == null)
            {
                modelPath = arg;
                continue;
            }

            throw new ArgumentException($"未知参数: {arg}");
        }

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("缺少模型路径");
        }

        return new ProbeOptions
        {
            ModelPath = modelPath,
            JsonOutputPath = jsonOutputPath,
            ImagePath = imagePath,
            YoloVersion = yoloVersion,
            WarmupIterations = warmupIterations,
            BenchmarkIterations = benchmarkIterations,
            Confidence = confidence,
            IouThreshold = iouThreshold,
            UseGpu = useGpu,
            RunBenchmark = runBenchmark,
            RequiredExecutionProvider = requiredExecutionProvider,
            PreprocessingMode = preprocessingMode,
            TaskMode = taskMode
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project tools/ClearFrost.YoloProbe -- --model path/to/model.onnx [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -m, --model <path>          ONNX 模型路径，也可作为第一个位置参数");
        Console.WriteLine("  -o, --out <path>            保存 JSON 校验报告");
        Console.WriteLine("      --benchmark             运行推理性能基准");
        Console.WriteLine("      --iterations <n>        基准迭代次数，默认 10");
        Console.WriteLine("      --warmup <n>            预热迭代次数，默认 2");
        Console.WriteLine("      --image <path>          基准输入图片；省略时使用合成图片");
        Console.WriteLine("      --confidence <value>    置信度阈值，默认 0.25");
        Console.WriteLine("      --iou <value>           IoU 阈值，默认 0.45");
        Console.WriteLine("      --gpu                   使用 DirectML；失败时按检测器逻辑回退 CPU");
        Console.WriteLine("      --require-provider <n>  严格验证实际 provider；不匹配时返回非零");
        Console.WriteLine("      --version <n>           显式 YOLO 主版本，0 表示自动");
        Console.WriteLine("      --preprocess <mode>     standard 或 fast");
        Console.WriteLine("      --task <mode>           auto、detect、segment、pose、obb、classify");
        Console.WriteLine("  -h, --help                  显示帮助");
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} 缺少值");
        }

        index++;
        return args[index];
    }

    private static YoloPreprocessingMode ParsePreprocessingMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "standard" or "letterbox" or "standardletterbox" => YoloPreprocessingMode.StandardLetterBox,
            "fast" or "industrialfast" => YoloPreprocessingMode.IndustrialFast,
            _ => throw new ArgumentException("--preprocess 只支持 standard 或 fast")
        };
    }

    private static YoloTaskType ParseTaskMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => YoloTaskType.Auto,
            "classify" or "classification" => YoloTaskType.Classify,
            "detect" or "detection" => YoloTaskType.Detect,
            "segment" or "segmentation" => YoloTaskType.SegmentWithMask,
            "pose" => YoloTaskType.PoseWithKeypoints,
            "obb" => YoloTaskType.Obb,
            _ => throw new ArgumentException("--task 只支持 auto、detect、segment、pose、obb、classify")
        };
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, out int parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{option} 必须是正整数");
        }

        return parsed;
    }

    private static int ParseNonNegativeInt(string value, string option)
    {
        if (!int.TryParse(value, out int parsed) || parsed < 0)
        {
            throw new ArgumentException($"{option} 必须是非负整数");
        }

        return parsed;
    }

    private static float ParseProbability(string value, string option)
    {
        if (!float.TryParse(value, out float parsed) || parsed < 0 || parsed > 1)
        {
            throw new ArgumentException($"{option} 必须在 0 到 1 之间");
        }

        return parsed;
    }
}
