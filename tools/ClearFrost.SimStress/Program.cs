using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

var options = StressOptions.Parse(args);
var runner = new StressRunner(options);
StressReport report = await runner.RunAsync();

Directory.CreateDirectory(Path.GetDirectoryName(options.MarkdownReportPath) ?? ".");
Directory.CreateDirectory(Path.GetDirectoryName(options.JsonReportPath) ?? ".");

await File.WriteAllTextAsync(options.JsonReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true
}));
await File.WriteAllTextAsync(options.MarkdownReportPath, StressMarkdownReport.Build(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

Console.WriteLine("ClearFrost site acceptance stress completed");
Console.WriteLine($"Cycles={report.TotalCycles} Duration={report.DurationSeconds:F1}s Parallel={report.Parallelism}");
Console.WriteLine($"Average={report.AverageMs:F2}ms P95={report.P95Ms}ms P99={report.P99Ms}ms Failed={report.FailedCount}");
Console.WriteLine($"QueueBacklog={report.QueueBacklog} MemoryDelta={report.MemoryDeltaMb:F2}MB");
Console.WriteLine($"Markdown: {report.MarkdownReportPath}");
Console.WriteLine($"Json: {report.JsonReportPath}");

public sealed class StressOptions
{
    public int Cycles { get; init; } = 1000;
    public double DurationMinutes { get; init; }
    public int Parallelism { get; init; } = 1;
    public double FailureRate { get; init; } = 0.002;
    public int QueueCapacity { get; init; } = 4096;
    public string OutputPath { get; init; } = Path.Combine(
        Environment.CurrentDirectory,
        $"sim_stress_report_{DateTime.Now:yyyyMMdd_HHmmss}.md");

    public bool HasDurationLimit => DurationMinutes > 0;

    public bool HasCycleLimit => Cycles > 0;

    public string MarkdownReportPath => Path.ChangeExtension(OutputPath, ".md");

    public string JsonReportPath => Path.ChangeExtension(OutputPath, ".json");

    public static StressOptions Parse(string[] args)
    {
        bool cyclesSpecified = HasOption(args, "--cycles");
        bool durationSpecified = HasOption(args, "--duration-minutes");
        double durationMinutes = Math.Max(0, ReadDouble(args, "--duration-minutes", 0));
        int cycles = ReadInt(args, "--cycles", durationSpecified && !cyclesSpecified ? 0 : 1000);
        int parallelism = Math.Clamp(ReadInt(args, "--parallel", 1), 1, 256);
        int queueCapacity = Math.Max(1, ReadInt(args, "--queue-capacity", 4096));
        double failureRate = Math.Clamp(ReadDouble(args, "--failure-rate", 0.002), 0, 1);
        string outputPath = ReadString(args, "--output")
            ?? Path.Combine(Environment.CurrentDirectory, $"sim_stress_report_{DateTime.Now:yyyyMMdd_HHmmss}.md");

        return new StressOptions
        {
            Cycles = Math.Max(0, cycles),
            DurationMinutes = durationMinutes,
            Parallelism = parallelism,
            FailureRate = failureRate,
            QueueCapacity = queueCapacity,
            OutputPath = outputPath
        };
    }

    private static bool HasOption(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string? value = ReadString(args, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    private static double ReadDouble(string[] args, string name, double fallback)
    {
        string? value = ReadString(args, name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
    }

    private static string? ReadString(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

public sealed class StressRunner
{
    private readonly StressOptions _options;
    private readonly FakeCameraService _camera = new FakeCameraService();
    private readonly FakeDetectionService _detection = new FakeDetectionService();
    private readonly FakePlcService _plc = new FakePlcService();
    private readonly ConcurrentQueue<long> _durations = new ConcurrentQueue<long>();
    private readonly SemaphoreSlim _queueSlots;
    private long _failedCount;
    private long _maxBacklog;
    private long _issuedCycles;

    public StressRunner(StressOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _queueSlots = new SemaphoreSlim(options.QueueCapacity, options.QueueCapacity);
    }

    public async Task<StressReport> RunAsync()
    {
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        DateTimeOffset startedAt = DateTimeOffset.Now;
        DateTimeOffset? deadline = _options.HasDurationLimit
            ? startedAt.AddMinutes(_options.DurationMinutes)
            : null;

        Task[] workers = Enumerable.Range(0, _options.Parallelism)
            .Select(_ => Task.Run(() => RunWorkerAsync(deadline)))
            .ToArray();

        await Task.WhenAll(workers);

        DateTimeOffset completedAt = DateTimeOffset.Now;
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        long[] durations = _durations.OrderBy(v => v).ToArray();
        double durationSeconds = Math.Max(0.001, (completedAt - startedAt).TotalSeconds);

        return new StressReport
        {
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationSeconds = durationSeconds,
            RequestedCycles = _options.Cycles,
            DurationMinutes = _options.DurationMinutes,
            TotalCycles = durations.Length,
            Parallelism = _options.Parallelism,
            AverageMs = durations.Length == 0 ? 0 : durations.Average(),
            P95Ms = Percentile(durations, 0.95),
            P99Ms = Percentile(durations, 0.99),
            FailedCount = Interlocked.Read(ref _failedCount),
            QueueBacklog = Interlocked.Read(ref _maxBacklog),
            MemoryBeforeMb = memoryBefore / 1024d / 1024d,
            MemoryAfterMb = memoryAfter / 1024d / 1024d,
            MemoryDeltaMb = (memoryAfter - memoryBefore) / 1024d / 1024d,
            ThroughputCyclesPerMinute = durations.Length / durationSeconds * 60d,
            MarkdownReportPath = _options.MarkdownReportPath,
            JsonReportPath = _options.JsonReportPath
        };
    }

    private async Task RunWorkerAsync(DateTimeOffset? deadline)
    {
        while (true)
        {
            if (deadline.HasValue && DateTimeOffset.Now >= deadline.Value)
            {
                return;
            }

            long cycle = Interlocked.Increment(ref _issuedCycles);
            if (_options.HasCycleLimit && cycle > _options.Cycles)
            {
                return;
            }

            await RunCycleAsync(cycle).ConfigureAwait(false);
        }
    }

    private async Task RunCycleAsync(long cycle)
    {
        var sw = Stopwatch.StartNew();
        bool slotTaken = false;

        try
        {
            await _queueSlots.WaitAsync().ConfigureAwait(false);
            slotTaken = true;
            long backlog = _options.QueueCapacity - _queueSlots.CurrentCount;
            UpdateMaxBacklog(backlog);

            byte[] frame = await _camera.CaptureAsync(cycle).ConfigureAwait(false);
            DetectionDecision decision = await _detection.DetectAsync(frame, _options.FailureRate).ConfigureAwait(false);
            await _plc.WriteResultAsync(decision.IsQualified).ConfigureAwait(false);
            await Task.Delay(Random.Shared.Next(0, 3)).ConfigureAwait(false);

            if (!decision.Success)
            {
                Interlocked.Increment(ref _failedCount);
            }
        }
        catch
        {
            Interlocked.Increment(ref _failedCount);
        }
        finally
        {
            sw.Stop();
            _durations.Enqueue(sw.ElapsedMilliseconds);
            if (slotTaken)
            {
                _queueSlots.Release();
            }
        }
    }

    private void UpdateMaxBacklog(long backlog)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _maxBacklog);
            if (backlog <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maxBacklog, backlog, current) != current);
    }

    private static long Percentile(long[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(values.Length * percentile) - 1;
        index = Math.Clamp(index, 0, values.Length - 1);
        return values[index];
    }
}

public static class StressMarkdownReport
{
    public static string Build(StressReport report)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));

        var builder = new StringBuilder();
        builder.AppendLine("# ClearFrost 现场验收压测摘要");
        builder.AppendLine();
        builder.AppendLine($"- 开始时间: {report.StartedAt:O}");
        builder.AppendLine($"- 结束时间: {report.CompletedAt:O}");
        builder.AppendLine($"- 运行时长: {report.DurationSeconds:F1} 秒");
        builder.AppendLine($"- 循环数: {report.TotalCycles}");
        builder.AppendLine($"- 并发数: {report.Parallelism}");
        builder.AppendLine($"- 平均耗时: {report.AverageMs:F2} ms");
        builder.AppendLine($"- P95: {report.P95Ms} ms");
        builder.AppendLine($"- P99: {report.P99Ms} ms");
        builder.AppendLine($"- 失败数: {report.FailedCount}");
        builder.AppendLine($"- 队列 backlog: {report.QueueBacklog}");
        builder.AppendLine($"- 内存变化: {report.MemoryDeltaMb:F2} MB");
        builder.AppendLine($"- 吞吐: {report.ThroughputCyclesPerMinute:F2} cycles/min");
        builder.AppendLine();
        builder.AppendLine("## 上产线初判");
        builder.AppendLine(report.FailedCount == 0 && report.QueueBacklog < report.Parallelism * 2
            ? "- 当前模拟结果未发现明显阻塞，可进入现场硬件联调。"
            : "- 当前模拟结果存在失败或队列堆积，建议先排查后再进入长稳验收。");
        return builder.ToString();
    }
}

public sealed class FakeCameraService
{
    public async Task<byte[]> CaptureAsync(long cycle)
    {
        await Task.Delay(Random.Shared.Next(1, 5)).ConfigureAwait(false);
        return BitConverter.GetBytes(cycle);
    }
}

public sealed class FakeDetectionService
{
    public async Task<DetectionDecision> DetectAsync(byte[] frame, double failureRate)
    {
        await Task.Delay(Random.Shared.Next(8, 31)).ConfigureAwait(false);
        bool success = Random.Shared.NextDouble() >= failureRate;
        return new DetectionDecision(success, success && Random.Shared.Next(0, 10) != 0);
    }
}

public sealed class FakePlcService
{
    public async Task WriteResultAsync(bool isQualified)
    {
        await Task.Delay(Random.Shared.Next(1, 4)).ConfigureAwait(false);
    }
}

public readonly record struct DetectionDecision(bool Success, bool IsQualified);

public sealed class StressReport
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public double DurationSeconds { get; init; }
    public int RequestedCycles { get; init; }
    public double DurationMinutes { get; init; }
    public int TotalCycles { get; init; }
    public int Parallelism { get; init; }
    public double AverageMs { get; init; }
    public long P95Ms { get; init; }
    public long P99Ms { get; init; }
    public long FailedCount { get; init; }
    public long QueueBacklog { get; init; }
    public double MemoryBeforeMb { get; init; }
    public double MemoryAfterMb { get; init; }
    public double MemoryDeltaMb { get; init; }
    public double ThroughputCyclesPerMinute { get; init; }
    public string MarkdownReportPath { get; init; } = string.Empty;
    public string JsonReportPath { get; init; } = string.Empty;
}
