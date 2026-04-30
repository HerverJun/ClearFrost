using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

var options = StressOptions.Parse(args);
var runner = new StressRunner(options);
StressReport report = await runner.RunAsync();

Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath) ?? ".");
await File.WriteAllTextAsync(
    options.ReportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"ClearFrost simulation stress completed: {report.TotalCycles} cycles");
Console.WriteLine($"Average={report.AverageMs:F2}ms P95={report.P95Ms}ms P99={report.P99Ms}ms Failed={report.FailedCount}");
Console.WriteLine($"QueueBacklog={report.QueueBacklog} MemoryDelta={report.MemoryDeltaMb:F2}MB");
Console.WriteLine($"Report: {options.ReportPath}");

internal sealed class StressOptions
{
    public int Cycles { get; init; } = 1000;
    public int Parallelism { get; init; } = 1;
    public double FailureRate { get; init; } = 0.002;
    public int QueueCapacity { get; init; } = 4096;
    public string ReportPath { get; init; } = Path.Combine(
        Environment.CurrentDirectory,
        $"sim_stress_report_{DateTime.Now:yyyyMMdd_HHmmss}.json");

    public static StressOptions Parse(string[] args)
    {
        int cycles = ReadInt(args, "--cycles", 1000);
        int parallelism = Math.Clamp(ReadInt(args, "--parallel", 1), 1, 64);
        int queueCapacity = Math.Max(1, ReadInt(args, "--queue-capacity", 4096));
        double failureRate = Math.Clamp(ReadDouble(args, "--failure-rate", 0.002), 0, 1);
        string reportPath = ReadString(args, "--output")
            ?? Path.Combine(Environment.CurrentDirectory, $"sim_stress_report_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        return new StressOptions
        {
            Cycles = Math.Max(1, cycles),
            Parallelism = parallelism,
            FailureRate = failureRate,
            QueueCapacity = queueCapacity,
            ReportPath = reportPath
        };
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string? value = ReadString(args, name);
        return int.TryParse(value, out int parsed) ? parsed : fallback;
    }

    private static double ReadDouble(string[] args, string name, double fallback)
    {
        string? value = ReadString(args, name);
        return double.TryParse(value, out double parsed) ? parsed : fallback;
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

internal sealed class StressRunner
{
    private readonly StressOptions _options;
    private readonly FakeCameraService _camera = new FakeCameraService();
    private readonly FakeDetectionService _detection = new FakeDetectionService();
    private readonly FakePlcService _plc = new FakePlcService();
    private readonly ConcurrentQueue<long> _durations = new ConcurrentQueue<long>();
    private readonly SemaphoreSlim _queueSlots;
    private long _failedCount;
    private long _maxBacklog;

    public StressRunner(StressOptions options)
    {
        _options = options;
        _queueSlots = new SemaphoreSlim(options.QueueCapacity, options.QueueCapacity);
    }

    public async Task<StressReport> RunAsync()
    {
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        using var throttler = new SemaphoreSlim(_options.Parallelism, _options.Parallelism);
        var tasks = new List<Task>(_options.Cycles);

        for (int i = 0; i < _options.Cycles; i++)
        {
            await throttler.WaitAsync();
            int cycle = i + 1;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await RunCycleAsync(cycle);
                }
                finally
                {
                    throttler.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        long[] durations = _durations.OrderBy(v => v).ToArray();

        return new StressReport
        {
            StartedAt = DateTimeOffset.Now,
            TotalCycles = _options.Cycles,
            Parallelism = _options.Parallelism,
            AverageMs = durations.Length == 0 ? 0 : durations.Average(),
            P95Ms = Percentile(durations, 0.95),
            P99Ms = Percentile(durations, 0.99),
            FailedCount = Interlocked.Read(ref _failedCount),
            QueueBacklog = Interlocked.Read(ref _maxBacklog),
            MemoryDeltaMb = (memoryAfter - memoryBefore) / 1024d / 1024d
        };
    }

    private async Task RunCycleAsync(int cycle)
    {
        var sw = Stopwatch.StartNew();
        bool slotTaken = false;

        try
        {
            await _queueSlots.WaitAsync();
            slotTaken = true;
            long backlog = _options.QueueCapacity - _queueSlots.CurrentCount;
            UpdateMaxBacklog(backlog);

            byte[] frame = await _camera.CaptureAsync(cycle);
            DetectionDecision decision = await _detection.DetectAsync(frame, _options.FailureRate);
            await _plc.WriteResultAsync(decision.IsQualified);
            await Task.Delay(Random.Shared.Next(0, 3));

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

internal sealed class FakeCameraService
{
    public async Task<byte[]> CaptureAsync(int cycle)
    {
        await Task.Delay(Random.Shared.Next(1, 5));
        return BitConverter.GetBytes(cycle);
    }
}

internal sealed class FakeDetectionService
{
    public async Task<DetectionDecision> DetectAsync(byte[] frame, double failureRate)
    {
        await Task.Delay(Random.Shared.Next(8, 31));
        bool success = Random.Shared.NextDouble() >= failureRate;
        return new DetectionDecision(success, success && Random.Shared.Next(0, 10) != 0);
    }
}

internal sealed class FakePlcService
{
    public async Task WriteResultAsync(bool isQualified)
    {
        await Task.Delay(Random.Shared.Next(1, 4));
    }
}

internal readonly record struct DetectionDecision(bool Success, bool IsQualified);

internal sealed class StressReport
{
    public DateTimeOffset StartedAt { get; init; }
    public int TotalCycles { get; init; }
    public int Parallelism { get; init; }
    public double AverageMs { get; init; }
    public long P95Ms { get; init; }
    public long P99Ms { get; init; }
    public long FailedCount { get; init; }
    public long QueueBacklog { get; init; }
    public double MemoryDeltaMb { get; init; }
}
