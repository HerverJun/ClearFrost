using System.Diagnostics;

/// <summary>
/// Pure contracts used by the soak host and its behavior tests.
/// </summary>
public sealed class SoakConsistencyRecord
{
    public string InspectionId { get; init; } = string.Empty;
    public bool CycleSucceeded { get; init; }
    public bool ImagePresent { get; init; }
    public bool TracePresent { get; init; }
}

public sealed class SoakConsistencyResult
{
    public string Status { get; init; } = "BLOCKED";
    public DateTimeOffset ScanStartedAtUtc { get; init; }
    public DateTimeOffset ScanFinishedAtUtc { get; init; }
    public string QueueStatus { get; init; } = "UNKNOWN";
    public int RecordsRead { get; init; }
    public int ExpectedInspectionIds { get; init; }
    public int MissingRecords { get; init; }
    public int DuplicateInspectionIds { get; init; }
    public int MissingImages { get; init; }
    public int MissingTraceRecords { get; init; }
    public IReadOnlyList<string> MissingInspectionIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}

public static class SoakConsistencyEvaluator
{
    public static SoakConsistencyResult Evaluate(
        IEnumerable<string> expectedInspectionIds,
        IEnumerable<SoakConsistencyRecord> records,
        bool queuesDrained,
        DateTimeOffset scanStartedAtUtc,
        DateTimeOffset scanFinishedAtUtc,
        string queueStatus)
    {
        string[] expected = expectedInspectionIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SoakConsistencyRecord[] actualRecords = records.ToArray();
        var grouped = actualRecords
            .Where(record => !string.IsNullOrWhiteSpace(record.InspectionId))
            .GroupBy(record => record.InspectionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        string[] missingIds = expected
            .Where(id => !grouped.ContainsKey(id))
            .ToArray();
        int duplicateCount = grouped.Values.Sum(items => Math.Max(0, items.Length - 1));
        int missingImages = 0;
        int missingTraceRecords = 0;
        var findings = new List<string>();

        foreach (KeyValuePair<string, SoakConsistencyRecord[]> pair in grouped)
        {
            foreach (SoakConsistencyRecord record in pair.Value)
            {
                if (!record.CycleSucceeded)
                {
                    continue;
                }

                if (!record.ImagePresent)
                {
                    missingImages++;
                    findings.Add($"Successful record {record.InspectionId} has no persisted image.");
                }
                if (!record.TracePresent)
                {
                    missingTraceRecords++;
                    findings.Add($"Successful record {record.InspectionId} has no valid trace.");
                }
            }
        }

        foreach (string missingId in missingIds)
        {
            findings.Add($"Missing DetectionRecord for {missingId}.");
        }

        if (!queuesDrained)
        {
            findings.Add("Final consistency scan was not authorized because the persistence queues did not reach a confirmed drained state.");
        }
        if (duplicateCount > 0)
        {
            findings.Add($"Duplicate InspectionId count is {duplicateCount}.");
        }

        bool passed = queuesDrained && missingIds.Length == 0 && duplicateCount == 0 && missingImages == 0 && missingTraceRecords == 0;
        return new SoakConsistencyResult
        {
            Status = passed ? "PASS" : "BLOCKED",
            ScanStartedAtUtc = scanStartedAtUtc,
            ScanFinishedAtUtc = scanFinishedAtUtc,
            QueueStatus = string.IsNullOrWhiteSpace(queueStatus) ? "UNKNOWN" : queueStatus,
            RecordsRead = actualRecords.Length,
            ExpectedInspectionIds = expected.Length,
            MissingRecords = missingIds.Length,
            DuplicateInspectionIds = duplicateCount,
            MissingImages = missingImages,
            MissingTraceRecords = missingTraceRecords,
            MissingInspectionIds = missingIds,
            Findings = findings
        };
    }
}

public sealed class SoakQueueSnapshot
{
    public long ImagePending { get; init; }
    public long RecordPending { get; init; }
    public long ImageInFlight { get; init; }
    public long RecordInFlight { get; init; }

    public bool IsDrained => ImagePending == 0 && RecordPending == 0 &&
        ImageInFlight == 0 && RecordInFlight == 0;
}

public sealed class SoakQueueWaitResult
{
    public string Status { get; init; } = "TIMEOUT";
    public bool Drained { get; init; }
    public long ElapsedMs { get; init; }
    public long ImagePending { get; init; }
    public long RecordPending { get; init; }
    public long ImageInFlight { get; init; }
    public long RecordInFlight { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public static class SoakQueueWaiter
{
    public static async Task<SoakQueueWaitResult> WaitAsync(
        Func<SoakQueueSnapshot> readSnapshot,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(25);
        SoakQueueSnapshot snapshot = readSnapshot();
        while (!snapshot.IsDrained && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(interval).ConfigureAwait(false);
            snapshot = readSnapshot();
        }

        stopwatch.Stop();
        bool drained = snapshot.IsDrained;
        return new SoakQueueWaitResult
        {
            Status = drained ? "DRAINED" : "TIMEOUT",
            Drained = drained,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            ImagePending = snapshot.ImagePending,
            RecordPending = snapshot.RecordPending,
            ImageInFlight = snapshot.ImageInFlight,
            RecordInFlight = snapshot.RecordInFlight,
            Reason = drained
                ? "Both persistence queues reached zero pending items."
                : "The persistence queues did not reach zero pending and in-flight items before the deadline."
        };
    }
}

public sealed class FaultRecoveryContract
{
    public bool Planned { get; init; }
    public bool Injected { get; init; }
    public string ExpectedErrorCode { get; init; } = string.Empty;
    public string ActualErrorCode { get; init; } = string.Empty;
    public string ExpectedTerminalState { get; init; } = string.Empty;
    public string ActualTerminalState { get; init; } = string.Empty;
    public string ExpectedTerminalErrorCode { get; init; } = string.Empty;
    public string ActualTerminalErrorCode { get; init; } = string.Empty;
    public bool FaultCleared { get; init; }
    public bool NextHealthyCycleSucceeded { get; init; }

    public bool IsRecovered => Planned && Injected &&
        string.Equals(ExpectedErrorCode, ActualErrorCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ExpectedTerminalState, ActualTerminalState, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ExpectedTerminalErrorCode, ActualTerminalErrorCode, StringComparison.OrdinalIgnoreCase) &&
        FaultCleared && NextHealthyCycleSucceeded;
}
