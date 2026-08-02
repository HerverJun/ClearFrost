using System.Diagnostics;
using System.Globalization;
using System.Management;
using ClearFrost;
using ClearFrost.Core.Inspection;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Yolo;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

internal sealed class SoakEvidence
{
    public string SchemaVersion { get; init; } = "v6-g2-soak-1.0";
    public string Status { get; set; } = "NOT_VERIFIED";
    public string PromotionEligibility { get; set; } = "NOT_VERIFIED";
    public string StartedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public string FinishedAt { get; set; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public string EvidenceType { get; init; } = "production-component harness";
    public SoakOptions Options { get; init; } = new SoakOptions();
    public V6G2EvidenceIdentity Identity { get; set; } = new V6G2EvidenceIdentity();
    public object? InputContract { get; set; }
    public object? Model { get; set; }
    public object? ValidationImage { get; set; }
    public object? ScenarioContract { get; set; }
    public string ScenarioCoverageStatus { get; set; } = "NOT_VERIFIED";
    public ScenarioExecutionEvidence ScenarioExecution { get; set; } = new ScenarioExecutionEvidence();
    public YoloModelDescriptor? ModelDescriptor { get; set; }
    public DetectionRuntimeStatus? Provider { get; set; }
    public RuntimeEvidence Runtime { get; set; } = new RuntimeEvidence();
    public CapabilityBoundaryEvidence CapabilityBoundary { get; set; } = CapabilityBoundaryEvidence.Create();
    public ResourceEvidence Resources { get; set; } = new ResourceEvidence();
    public StartupDiagnosticReport? Startup { get; set; }
    public CycleEvidenceSummary Cycles { get; set; } = new CycleEvidenceSummary();
    public FaultEvidence Faults { get; set; } = new FaultEvidence();
    public QueueEvidence Queues { get; set; } = new QueueEvidence();
    public HealthSnapshot? Health { get; set; }
    public ConsistencyEvidence FinalConsistency { get; set; } = new ConsistencyEvidence();
    public List<string> BlockingReasons { get; } = new List<string>();
    public List<string> NotVerifiedReasons { get; } = new List<string>();
    public List<string> RecentLogs { get; } = new List<string>();

    public static SoakEvidence Create(SoakOptions options, string commitSha)
    {
        var evidence = new SoakEvidence
        {
            CommitSha = commitSha,
            Options = options
        };
        evidence.Identity = V6G2EvidenceIdentity.Create(
            options.Root,
            options.ManifestPath,
            options.ModelPath,
            options.ImagePath,
            null,
            "NOT_VERIFIED",
            DateTimeOffset.Parse(evidence.StartedAt, CultureInfo.InvariantCulture));
        evidence.NotVerifiedReasons.Add("Real camera, PLC, and FAT/SAT were not exercised by this boundary-limited soak host.");
        evidence.NotVerifiedReasons.Add("This is a production-component harness; AppRuntime trigger listening, model admission, coordinator, busy/debounce, and production worker startup paths were not executed.");
        return evidence;
    }

    public void Complete()
    {
        FinishedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        DateTimeOffset started = DateTimeOffset.Parse(StartedAt, CultureInfo.InvariantCulture);
        string provider = Environment.GetEnvironmentVariable("CLEARFROST_V6_G2_PROVIDER") ?? "NOT_VERIFIED";
        Identity = V6G2EvidenceIdentity.Create(
            Options.Root,
            Options.ManifestPath,
            Options.ModelPath,
            Options.ImagePath,
            null,
            provider,
            started,
            DateTimeOffset.Parse(FinishedAt, CultureInfo.InvariantCulture));
    }
}

internal sealed class RuntimeEvidence
{
    public string Root { get; init; } = string.Empty;
    public string AppDataRoot { get; init; } = string.Empty;
    public string StorageRoot { get; init; } = string.Empty;
    public string ProfileRoot { get; init; } = string.Empty;
    public string DatabasePath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public bool IsolatedAppData { get; set; }
    public bool IsolatedStorage { get; set; }
    public bool SourceTreeReferenced { get; set; }
    public bool DevelopmentAppDataReferenced { get; set; }
    public bool StartupCompleted { get; set; }
    public bool NormalShutdownCompleted { get; set; }
    public bool CancellationShutdownCompleted { get; set; }
    public bool FileLocksReleased { get; set; }
    public int ProcessCountAfterShutdown { get; set; }
    public int ChildProcessCountAfterShutdown { get; set; }
    public int BaselineThreadCount { get; set; }
    public int ResidualThreadCount { get; set; }
    public int ResidualTaskCount { get; set; }
    public string QueueDrainStatus { get; set; } = "NOT_RUN";
    public long QueueDrainElapsedMs { get; set; }
    public string FileRenameVerification { get; set; } = "NOT_RUN";
    public string SqliteOpenVerification { get; set; } = "NOT_RUN";
    public string ProfileResidualStatus { get; set; } = "NOT_RUN";
    public string ChildProcessStatus { get; set; } = "NOT_RUN";
    public string ThreadStatus { get; set; } = "NOT_RUN";
    public string TaskStatus { get; set; } = "NOT_RUN";
}

internal sealed class CycleEvidenceSummary
{
    public int PreflightCycles { get; set; }
    public int MainCycles { get; set; }
    public int SuccessfulCycles { get; set; }
    public int QualifiedCycles { get; set; }
    public int UnqualifiedCycles { get; set; }
    public int ExplicitFailureCycles { get; set; }
    public int CancelledCycles { get; set; }
    public long DroppedTriggerCount { get; set; }
    public long DuplicateInspectionIdCount { get; set; }
    public long MissingTraceCount { get; set; }
    public long MissingRecordCount { get; set; }
    public double ThroughputCyclesPerSecond { get; set; }
    public ResourceSample? FirstResourceSample { get; set; }
    public ResourceSample? LastResourceSample { get; set; }
    public List<CycleEvidence> Samples { get; } = new List<CycleEvidence>();
}

internal sealed class CycleEvidence
{
    public string Phase { get; init; } = string.Empty;
    public int Sequence { get; init; }
    public string InspectionId { get; init; } = string.Empty;
    public string Fault { get; init; } = "None";
    public bool FaultExpected { get; init; }
    public bool TriggerAccepted { get; init; }
    public bool? Qualified { get; init; }
    public bool ProductFlowSucceeded { get; init; }
    public bool CycleSucceeded { get; init; }
    public bool TerminalHandshakeAttempted { get; init; }
    public bool TerminalHandshakeSucceeded { get; init; }
    public bool Cancelled { get; init; }
    public bool ExplicitFailure { get; init; }
    public bool RecoveryVerified { get; set; }
    public string RecoveryStatus { get; set; } = "NOT_APPLICABLE";
    public string ExpectedTerminalState { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public string TraceStatus { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public long TotalMs { get; init; }
    public int ResultCount { get; init; }
    public long ImageQueuePending { get; init; }
    public long RecordQueuePending { get; init; }
    public long ImageQueueLatencyMs { get; init; }
    public long RecordQueueLatencyMs { get; init; }
}

internal sealed class FaultEvidence
{
    public int Seed { get; set; }
    public bool Enabled { get; set; }
    public List<FaultEventEvidence> Events { get; } = new List<FaultEventEvidence>();
}

internal sealed class FaultEventEvidence
{
    public string InspectionId { get; init; } = string.Empty;
    public string Fault { get; init; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ExpectedErrorCode { get; set; } = string.Empty;
    public string ExpectedTerminalState { get; set; } = string.Empty;
    public string ExpectedTerminalErrorCode { get; set; } = string.Empty;
    public string ActualTerminalErrorCode { get; set; } = string.Empty;
    public string ActualTerminalState { get; set; } = string.Empty;
    public bool Planned { get; set; }
    public bool Injected { get; set; }
    public DateTimeOffset? InjectedAt { get; set; }
    public bool FaultCleared { get; set; }
    public DateTimeOffset? FaultClearedAt { get; set; }
    public bool NextHealthyCycleRecovered { get; set; }
    public string NextHealthyInspectionId { get; set; } = string.Empty;
    public long RecoveryDurationMs { get; set; }
    public bool Recovered { get; set; }
    public string RecoveryStatus { get; set; } = "NOT_RUN";
    public string Details { get; set; } = string.Empty;
    public string RecoveryDetails { get; set; } = string.Empty;
}

internal sealed class QueueEvidence
{
    public int ImageCapacity { get; init; }
    public long ImagePending { get; init; }
    public long ImagePendingBytes { get; init; }
    public long ImageInFlight { get; init; }
    public long ImageSaved { get; init; }
    public long ImageDropped { get; init; }
    public long ImageFailed { get; init; }
    public int RecordCapacity { get; init; }
    public long RecordPending { get; init; }
    public long RecordInFlight { get; init; }
    public long RecordSaved { get; init; }
    public long RecordDropped { get; init; }
    public long RecordFailed { get; init; }
    public SoakQueueWaitResult? Drain { get; set; }

    public static QueueEvidence From(ImageSaveQueue imageQueue, DetectionRecordQueue recordQueue)
    {
        return new QueueEvidence
        {
            ImageCapacity = imageQueue.Capacity,
            ImagePending = imageQueue.PendingCount,
            ImagePendingBytes = imageQueue.PendingBytes,
            ImageInFlight = imageQueue.InFlightCount,
            ImageSaved = imageQueue.SavedCount,
            ImageDropped = imageQueue.DroppedCount,
            ImageFailed = imageQueue.FailedCount,
            RecordCapacity = recordQueue.Capacity,
            RecordPending = recordQueue.PendingCount,
            RecordInFlight = recordQueue.InFlightCount,
            RecordSaved = recordQueue.SavedCount,
            RecordDropped = recordQueue.DroppedCount,
            RecordFailed = recordQueue.FailedCount
        };
    }
}

internal sealed class ConsistencyEvidence
{
    public string Status { get; set; } = "NOT_VERIFIED";
    public string ScanStartedAtUtc { get; set; } = string.Empty;
    public string ScanFinishedAtUtc { get; set; } = string.Empty;
    public string QueueStatus { get; set; } = "NOT_RUN";
    public int RecordsRead { get; set; }
    public int ExpectedInspectionIds { get; set; }
    public int MissingRecords { get; set; }
    public int DuplicateInspectionIds { get; set; }
    public int MissingImages { get; set; }
    public int MissingTraceRecords { get; set; }
    public int InvalidTraceRecords { get; set; }
    public List<string> MissingInspectionIds { get; } = new List<string>();
    public List<string> Findings { get; } = new List<string>();
}

internal sealed class ScenarioExecutionEvidence
{
    public string Status { get; set; } = "NOT_VERIFIED";
    public int ExpectedSamples { get; set; }
    public int ExecutedSamples { get; set; }
    public List<ScenarioExecutionResult> Samples { get; } = new List<ScenarioExecutionResult>();
    public List<string> Findings { get; } = new List<string>();
}

internal sealed class ScenarioExecutionResult
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string InspectionId { get; init; } = string.Empty;
    public string ExpectedOutcome { get; init; } = string.Empty;
    public string ActualOutcome { get; init; } = string.Empty;
    public string ExpectedErrorCode { get; init; } = string.Empty;
    public string ActualErrorCode { get; init; } = string.Empty;
    public string ExpectedTerminalState { get; init; } = string.Empty;
    public string ActualTerminalState { get; init; } = string.Empty;
    public string Status { get; init; } = "BLOCKED";
    public string Finding { get; init; } = string.Empty;
}

internal sealed class CapabilityBoundaryEvidence
{
    public string HarnessType { get; init; } = "production-component harness";
    public bool AppRuntimeTriggerListenerExecuted { get; init; }
    public bool ModelAdmissionExecuted { get; init; }
    public bool CoordinatorExecuted { get; init; }
    public bool WorkerPathExecuted { get; init; }
    public bool BusyDebounceExecuted { get; init; }
    public List<string> BypassedPaths { get; init; } = new List<string>();

    public static CapabilityBoundaryEvidence Create()
    {
        return new CapabilityBoundaryEvidence
        {
            BypassedPaths = new List<string>
            {
                "AppRuntime trigger listener",
                "production model approval/admission",
                "trigger source coordinator",
                "busy/debounce policy",
                "production worker startup path"
            }
        };
    }
}

internal sealed class ResourceEvidence
{
    public List<ResourceSample> Samples { get; } = new List<ResourceSample>();
    public ResourceTrendEvidence Trend { get; set; } = new ResourceTrendEvidence();
    public QueueLatencyEvidence QueueLatency { get; set; } = new QueueLatencyEvidence();
    public double ThroughputCyclesPerSecond { get; set; }
    public long MaxFaultRecoveryMs { get; set; }
}

internal sealed class ResourceSample
{
    public string AtUtc { get; init; } = string.Empty;
    public int Cycle { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public long GcHeapBytes { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public long ImageQueuePending { get; init; }
    public long RecordQueuePending { get; init; }
    public long ImageQueueLatencyMs { get; init; }
    public long RecordQueueLatencyMs { get; init; }
    public long CycleMs { get; init; }
    public long FaultRecoveryMs { get; init; }
}

internal sealed class ResourceTrendEvidence
{
    public string Status { get; set; } = "NOT_VERIFIED";
    public string Method { get; set; } = "periodic-sample-linear-trend";
    public int SampleCount { get; set; }
    public double WorkingSetSlopeBytesPerSample { get; set; }
    public double PrivateBytesSlopeBytesPerSample { get; set; }
    public double GcHeapSlopeBytesPerSample { get; set; }
    public string Finding { get; set; } = string.Empty;
}

internal sealed class QueueLatencyEvidence
{
    public QueuePercentileEvidence Image { get; set; } = new QueuePercentileEvidence();
    public QueuePercentileEvidence Record { get; set; } = new QueuePercentileEvidence();
    public QueuePercentileEvidence Cycle { get; set; } = new QueuePercentileEvidence();
}

internal sealed class QueuePercentileEvidence
{
    public int SampleCount { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double Maximum { get; set; }
}

internal enum SoakFaultKind
{
    None,
    CameraShortFrame,
    CameraCaptureFailure,
    PlcDisconnect,
    PlcWriteFailure,
    ResultAckTimeout,
    DatabaseLock,
    ImageTargetUnavailable,
    ImageQueueBackpressure,
    RecordQueueBackpressure,
    ModelUnavailable,
    Cancellation
}

internal sealed class FaultPlan
{
    private readonly object _sync = new object();
    private readonly Dictionary<string, SoakFaultKind> _scenarios = new Dictionary<string, SoakFaultKind>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cleared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _persistent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<FaultEventEvidence> _events = new List<FaultEventEvidence>();
    private readonly int _seed;
    private readonly bool _enabled;

    public FaultPlan(int seed, bool enabled)
    {
        _seed = seed;
        _enabled = enabled;
    }

    public int Seed => _seed;
    public bool Enabled => _enabled;
    public string CurrentInspectionId { get; private set; } = string.Empty;

    public SoakFaultKind BeginCycle(string inspectionId, int sequence, bool allowFaults)
    {
        SoakFaultKind fault = allowFaults && _enabled ? ChooseFault(sequence) : SoakFaultKind.None;
        lock (_sync)
        {
            CurrentInspectionId = inspectionId;
            _scenarios[inspectionId] = fault;
            if (fault != SoakFaultKind.None)
            {
                (string expectedErrorCode, string expectedTerminalState, string expectedTerminalErrorCode) = GetExpectedOutcome(fault);
                _events.Add(new FaultEventEvidence
                {
                    InspectionId = inspectionId,
                    Fault = fault.ToString(),
                    Planned = true,
                    ExpectedErrorCode = expectedErrorCode,
                    ExpectedTerminalState = expectedTerminalState,
                    ExpectedTerminalErrorCode = expectedTerminalErrorCode
                });
            }
        }
        return fault;
    }

    public SoakFaultKind GetScenario(string inspectionId)
    {
        lock (_sync)
        {
            return _scenarios.TryGetValue(inspectionId, out SoakFaultKind fault) ? fault : SoakFaultKind.None;
        }
    }

    public void ArmRuntimeFault(string inspectionId)
    {
        lock (_sync)
        {
            _persistent.Add($"armed:{inspectionId}");
        }
    }

    public void DisarmRuntimeFault(string inspectionId)
    {
        lock (_sync)
        {
            _persistent.Remove($"armed:{inspectionId}");
        }
    }

    public bool IsRuntimeFaultArmed(string inspectionId)
    {
        lock (_sync)
        {
            return _persistent.Contains($"armed:{inspectionId}");
        }
    }

    public bool ConsumeCameraShortFrame(string inspectionId)
    {
        return ConsumeUntilCleared(inspectionId, SoakFaultKind.CameraShortFrame, "CaptureFrameFailed");
    }

    public bool ConsumeCameraCaptureFailure(string inspectionId)
    {
        return ConsumeUntilCleared(inspectionId, SoakFaultKind.CameraCaptureFailure, "CaptureFrameFailed");
    }

    public bool ConsumePlcWriteFailure(string inspectionId)
    {
        if (!IsRuntimeFaultArmed(inspectionId))
        {
            return false;
        }

        return Consume(inspectionId, SoakFaultKind.PlcWriteFailure, "plc-write-failure", "HandshakeV1.WriteFailed");
    }

    public bool ShouldHoldResultAck(string inspectionId)
    {
        if (GetScenario(inspectionId) != SoakFaultKind.ResultAckTimeout || !IsRuntimeFaultArmed(inspectionId))
        {
            return false;
        }

        lock (_sync)
        {
            string key = $"persistent-ack:{inspectionId}";
            if (!_persistent.Contains(key))
            {
                _persistent.Add(key);
                MarkInjectedLocked(inspectionId, "HandshakeV1.AckTimeout", "ResultAck is held at zero for this cycle.");
            }
            return true;
        }
    }

    public bool TryBeginDatabaseLock(string inspectionId)
    {
        return Consume(inspectionId, SoakFaultKind.DatabaseLock, "database-lock", "SQLite.BusyWindow");
    }

    public bool TryFailImage(string path)
    {
        string? inspectionId = FindInspectionId(path);
        if (inspectionId == null)
        {
            return false;
        }

        return Consume(inspectionId, SoakFaultKind.ImageTargetUnavailable, "image-target-unavailable", "ImageTarget.Unavailable");
    }

    public void RecordHarnessInjection(string inspectionId, SoakFaultKind fault, string errorCode, string details)
    {
        lock (_sync)
        {
            if (!_scenarios.ContainsKey(inspectionId))
            {
                _scenarios[inspectionId] = fault;
                _events.Add(new FaultEventEvidence
                {
                    InspectionId = inspectionId,
                    Fault = fault.ToString(),
                    Planned = true,
                    ExpectedErrorCode = GetExpectedOutcome(fault).ExpectedErrorCode,
                    ExpectedTerminalState = GetExpectedOutcome(fault).ExpectedTerminalState,
                    ExpectedTerminalErrorCode = GetExpectedOutcome(fault).ExpectedTerminalErrorCode
                });
            }
            MarkInjectedLocked(inspectionId, errorCode, details);
        }
    }

    public void MarkFaultCleared(string inspectionId, string details)
    {
        lock (_sync)
        {
            FaultEventEvidence? item = FindEventLocked(inspectionId);
            if (item == null)
            {
                return;
            }

            _cleared.Add(inspectionId);
            item.FaultCleared = true;
            item.FaultClearedAt ??= DateTimeOffset.UtcNow;
            item.RecoveryDetails = details;
        }
    }

    public void MarkTerminalOutcome(string inspectionId, string terminalErrorCode, string terminalState)
    {
        lock (_sync)
        {
            FaultEventEvidence? item = FindEventLocked(inspectionId);
            if (item == null)
            {
                return;
            }

            item.ActualTerminalErrorCode = terminalErrorCode ?? string.Empty;
            item.ActualTerminalState = terminalState ?? string.Empty;
        }
    }

    public void MarkNextHealthyCycle(string inspectionId, string nextHealthyInspectionId, long recoveryDurationMs)
    {
        lock (_sync)
        {
            FaultEventEvidence? item = FindEventLocked(inspectionId);
            if (item == null)
            {
                return;
            }

            item.NextHealthyCycleRecovered = true;
            item.NextHealthyInspectionId = nextHealthyInspectionId;
            item.RecoveryDurationMs = Math.Max(0, recoveryDurationMs);
            item.Recovered = item.Planned && item.Injected && item.FaultCleared &&
                string.Equals(item.ErrorCode, item.ExpectedErrorCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ActualTerminalState, item.ExpectedTerminalState, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ActualTerminalErrorCode, item.ExpectedTerminalErrorCode, StringComparison.OrdinalIgnoreCase);
            item.RecoveryStatus = item.Recovered ? "RECOVERED" : "BLOCKED";
        }
    }

    public IReadOnlyList<FaultEventEvidence> SnapshotEvents()
    {
        lock (_sync)
        {
            return _events.Select(CloneEvent).ToArray();
        }
    }

    private bool Consume(string inspectionId, SoakFaultKind expectedFault, string action, string errorCode)
    {
        lock (_sync)
        {
            if (GetScenarioLocked(inspectionId) != expectedFault || !_consumed.Add($"{inspectionId}:{action}"))
            {
                return false;
            }

            MarkInjectedLocked(inspectionId, errorCode, action);
            return true;
        }
    }

    private bool ConsumeUntilCleared(string inspectionId, SoakFaultKind expectedFault, string errorCode)
    {
        lock (_sync)
        {
            if (GetScenarioLocked(inspectionId) != expectedFault || _cleared.Contains(inspectionId))
            {
                return false;
            }

            MarkInjectedLocked(
                inspectionId,
                errorCode,
                $"Injected {expectedFault} until the cycle reached its terminal path.");
            return true;
        }
    }

    private void MarkInjectedLocked(string inspectionId, string errorCode, string details)
    {
        FaultEventEvidence? item = _events.LastOrDefault(entry => string.Equals(entry.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            item = new FaultEventEvidence
            {
                InspectionId = inspectionId,
                Fault = GetScenarioLocked(inspectionId).ToString(),
                Planned = true,
                ExpectedErrorCode = GetExpectedOutcome(GetScenarioLocked(inspectionId)).ExpectedErrorCode,
                ExpectedTerminalState = GetExpectedOutcome(GetScenarioLocked(inspectionId)).ExpectedTerminalState,
                ExpectedTerminalErrorCode = GetExpectedOutcome(GetScenarioLocked(inspectionId)).ExpectedTerminalErrorCode
            };
            _events.Add(item);
        }

        item.Injected = true;
        item.InjectedAt ??= DateTimeOffset.UtcNow;
        item.ErrorCode = errorCode;
        item.Details = details;
    }

    private SoakFaultKind GetScenarioLocked(string inspectionId)
    {
        return _scenarios.TryGetValue(inspectionId, out SoakFaultKind fault) ? fault : SoakFaultKind.None;
    }

    private string? FindInspectionId(string value)
    {
        lock (_sync)
        {
            return _scenarios.Keys
                .Where(key => value.Contains(key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(key => key.Length)
                .FirstOrDefault();
        }
    }

    private SoakFaultKind ChooseFault(int sequence)
    {
        int slot = Math.Abs(sequence + _seed) % 12;
        return slot switch
        {
            1 => SoakFaultKind.CameraShortFrame,
            2 => SoakFaultKind.CameraCaptureFailure,
            3 => SoakFaultKind.PlcDisconnect,
            4 => SoakFaultKind.PlcWriteFailure,
            5 => SoakFaultKind.ResultAckTimeout,
            6 => SoakFaultKind.DatabaseLock,
            7 => SoakFaultKind.ImageTargetUnavailable,
            8 => SoakFaultKind.ImageQueueBackpressure,
            9 => SoakFaultKind.RecordQueueBackpressure,
            10 => SoakFaultKind.ModelUnavailable,
            11 => SoakFaultKind.Cancellation,
            _ => SoakFaultKind.None
        };
    }

    private FaultEventEvidence? FindEventLocked(string inspectionId)
    {
        return _events.LastOrDefault(entry => string.Equals(entry.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase));
    }

    private static (string ExpectedErrorCode, string ExpectedTerminalState, string ExpectedTerminalErrorCode) GetExpectedOutcome(SoakFaultKind fault)
    {
        return fault switch
        {
            SoakFaultKind.CameraShortFrame => ("CaptureFrameFailed", "ExplicitFailure", "CaptureFrameFailed"),
            SoakFaultKind.CameraCaptureFailure => ("CaptureFrameFailed", "ExplicitFailure", "CaptureFrameFailed"),
            SoakFaultKind.PlcDisconnect => ("PlcNotConnected", "ExplicitFailure", "PlcNotConnected"),
            SoakFaultKind.PlcWriteFailure => ("HandshakeV1.WriteFailed", "ExplicitFailure", "HandshakeV1.WriteFailed"),
            SoakFaultKind.ResultAckTimeout => ("HandshakeV1.AckTimeout", "ExplicitFailure", "HandshakeV1.AckTimeout"),
            SoakFaultKind.DatabaseLock => ("SQLite.BusyWindow", "Successful", string.Empty),
            SoakFaultKind.ImageTargetUnavailable => ("ImageTarget.Unavailable", "Successful", string.Empty),
            SoakFaultKind.ImageQueueBackpressure => ("ImageQueue.Backpressure", "Successful", string.Empty),
            SoakFaultKind.RecordQueueBackpressure => ("RecordQueue.Backpressure", "Successful", string.Empty),
            SoakFaultKind.ModelUnavailable => ("DetectionServiceError", "ExplicitFailure", "DetectionServiceError"),
            SoakFaultKind.Cancellation => ("OperationCanceled", "ExplicitFailure", "OperationCanceled"),
            _ => (string.Empty, string.Empty, string.Empty)
        };
    }

    private static FaultEventEvidence CloneEvent(FaultEventEvidence source)
    {
        return new FaultEventEvidence
        {
            InspectionId = source.InspectionId,
            Fault = source.Fault,
            ErrorCode = source.ErrorCode,
            ExpectedErrorCode = source.ExpectedErrorCode,
            ExpectedTerminalState = source.ExpectedTerminalState,
            ExpectedTerminalErrorCode = source.ExpectedTerminalErrorCode,
            ActualTerminalErrorCode = source.ActualTerminalErrorCode,
            ActualTerminalState = source.ActualTerminalState,
            Planned = source.Planned,
            Injected = source.Injected,
            InjectedAt = source.InjectedAt,
            FaultCleared = source.FaultCleared,
            FaultClearedAt = source.FaultClearedAt,
            NextHealthyCycleRecovered = source.NextHealthyCycleRecovered,
            NextHealthyInspectionId = source.NextHealthyInspectionId,
            RecoveryDurationMs = source.RecoveryDurationMs,
            Recovered = source.Recovered,
            RecoveryStatus = source.RecoveryStatus,
            Details = source.Details,
            RecoveryDetails = source.RecoveryDetails
        };
    }
}

internal sealed class SoakCameraService : ICameraService, ICameraCaptureDiagnostics
{
    private string _imagePath;
    private readonly FaultPlan _faultPlan;
    private readonly object _sync = new object();
    private Mat? _sourceFrame;
    private Mat? _lastFrame;
    private string _inspectionId = string.Empty;
    private bool _disposed;
    private bool _isOpen;
    private bool _isGrabbing;

    public SoakCameraService(string imagePath, FaultPlan faultPlan)
    {
        _imagePath = imagePath ?? throw new ArgumentNullException(nameof(imagePath));
        _faultPlan = faultPlan ?? throw new ArgumentNullException(nameof(faultPlan));
    }

    public event Action<Mat>? FrameCaptured;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ErrorOccurred;

    public bool IsOpen => _isOpen;
    public string CameraName => "V6 soak boundary camera";
    public string? LastError { get; private set; }
    public Mat? LastFrame => _lastFrame;
    public bool IsGrabbing => _isGrabbing;
    public CameraCaptureFailureKind LastCaptureFailureKind { get; private set; }

    public void BeginCycle(string inspectionId)
    {
        _inspectionId = inspectionId ?? string.Empty;
        LastError = null;
        LastCaptureFailureKind = CameraCaptureFailureKind.None;
    }

    public void SetSourceImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("An external scenario image path is required.", nameof(imagePath));
        }

        lock (_sync)
        {
            _imagePath = Path.GetFullPath(imagePath);
            _sourceFrame?.Dispose();
            _sourceFrame = null;
        }
    }

    public bool Open(string serialNumber, string manufacturer)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _isOpen = true;
            LastError = null;
        }
        ConnectionChanged?.Invoke(true);
        return true;
    }

    public void Close()
    {
        lock (_sync)
        {
            _isGrabbing = false;
            _isOpen = false;
        }
        ConnectionChanged?.Invoke(false);
    }

    public void StartCapture()
    {
        ThrowIfDisposed();
        if (!_isOpen)
        {
            LastError = "Boundary camera is not open.";
            LastCaptureFailureKind = CameraCaptureFailureKind.NotReady;
            ErrorOccurred?.Invoke(LastError);
            return;
        }
        _isGrabbing = true;
    }

    public void StopCapture()
    {
        _isGrabbing = false;
    }

    public void TriggerOnce()
    {
        if (!_isOpen || !_isGrabbing)
        {
            LastError = "Boundary camera is not ready.";
            LastCaptureFailureKind = CameraCaptureFailureKind.NotReady;
        }
    }

    public Mat? CaptureFrame(int timeoutMs = 3000)
    {
        ThrowIfDisposed();
        if (!_isOpen || !_isGrabbing)
        {
            return FailCapture(CameraCaptureFailureKind.NotReady, "Boundary camera is not grabbing.");
        }

        if (_faultPlan.ConsumeCameraShortFrame(_inspectionId))
        {
            return FailCapture(CameraCaptureFailureKind.ShortFrame, "Injected camera short frame.");
        }

        if (_faultPlan.ConsumeCameraCaptureFailure(_inspectionId))
        {
            return FailCapture(CameraCaptureFailureKind.GetFrameFailed, "Injected camera capture failure.");
        }

        try
        {
            _sourceFrame ??= Cv2.ImRead(_imagePath, ImreadModes.Color);
            if (_sourceFrame.Empty())
            {
                return FailCapture(CameraCaptureFailureKind.EmptyFrame, "The external validation image decoded as empty.");
            }

            Mat frame = _sourceFrame.Clone();
            _lastFrame?.Dispose();
            _lastFrame = frame.Clone();
            LastError = null;
            LastCaptureFailureKind = CameraCaptureFailureKind.None;
            FrameCaptured?.Invoke(frame.Clone());
            return frame;
        }
        catch (Exception ex)
        {
            return FailCapture(CameraCaptureFailureKind.ConversionFailed, ex.Message);
        }
    }

    public void SetExposure(double exposureUs)
    {
    }

    public void SetGain(double gain)
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        _lastFrame?.Dispose();
        _sourceFrame?.Dispose();
    }

    private Mat? FailCapture(CameraCaptureFailureKind kind, string message)
    {
        LastCaptureFailureKind = kind;
        LastError = message;
        ErrorOccurred?.Invoke(message);
        return null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SoakCameraService));
        }
    }
}

internal sealed class SoakPlcService : IPlcService
{
    private readonly FaultPlan _faultPlan;
    private readonly object _sync = new object();
    private readonly Dictionary<string, short> _words = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _connected;
    private bool _monitoring;
    private bool _resultPublished;
    private string _inspectionId = string.Empty;

    public SoakPlcService(FaultPlan faultPlan)
    {
        _faultPlan = faultPlan ?? throw new ArgumentNullException(nameof(faultPlan));
    }

    public event Action<bool>? ConnectionChanged;
    public event Action? TriggerReceived;
    public event Action<PlcTriggerContext>? TriggerContextReceived;
    public event Action<string>? ErrorOccurred;

    public bool IsConnected => _connected;
    public string ProtocolName { get; private set; } = "BoundaryAdapter";
    public string? LastError { get; private set; }

    public void BeginCycle(string inspectionId)
    {
        _inspectionId = inspectionId ?? string.Empty;
        _resultPublished = false;
        LastError = null;
        lock (_sync)
        {
            _words["D569"] = 0;
        }
    }

    public Task<bool> ConnectAsync(PlcConnectionOptions options)
    {
        ThrowIfDisposed();
        ProtocolName = string.IsNullOrWhiteSpace(options?.Protocol) ? "BoundaryAdapter" : options.Protocol;
        _connected = true;
        LastError = null;
        ConnectionChanged?.Invoke(true);
        return Task.FromResult(true);
    }

    public void Disconnect()
    {
        _monitoring = false;
        _connected = false;
        ConnectionChanged?.Invoke(false);
    }

    public bool StartMonitoring(string triggerAddress, int pollingIntervalMs = 500, int triggerDelayMs = 800, PlcMonitoringOptions? options = null)
    {
        if (!_connected)
        {
            LastError = "Boundary PLC is disconnected.";
            return false;
        }
        _monitoring = true;
        return true;
    }

    public void StopMonitoring()
    {
        _monitoring = false;
    }

    public Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        _monitoring = false;
        return Task.CompletedTask;
    }

    public Task<bool> WriteResultAsync(string resultAddress, bool isQualified)
    {
        return WriteResultAsync(resultAddress, isQualified ? (short)1 : (short)0);
    }

    public Task<bool> WriteResultAsync(string resultAddress, short valueToWrite)
    {
        ThrowIfDisposed();
        if (!_connected)
        {
            return Task.FromResult(FailWrite("PLC is disconnected."));
        }

        if (_faultPlan.ConsumePlcWriteFailure(_inspectionId))
        {
            return Task.FromResult(FailWrite("Injected PLC write failure."));
        }

        lock (_sync)
        {
            _words[resultAddress ?? string.Empty] = valueToWrite;
            if (string.Equals(resultAddress, "D568", StringComparison.OrdinalIgnoreCase) && valueToWrite != 0)
            {
                _resultPublished = true;
            }
            if (string.Equals(resultAddress, "D568", StringComparison.OrdinalIgnoreCase) && valueToWrite == 0)
            {
                _resultPublished = false;
            }
        }
        LastError = null;
        return Task.FromResult(true);
    }

    public Task<(bool Success, short Value)> ReadWordAsync(string address)
    {
        ThrowIfDisposed();
        if (!_connected)
        {
            LastError = "PLC is disconnected.";
            return Task.FromResult((false, (short)0));
        }

        if (string.Equals(address, "D569", StringComparison.OrdinalIgnoreCase))
        {
            if (_resultPublished && !_faultPlan.ShouldHoldResultAck(_inspectionId))
            {
                return Task.FromResult((true, (short)1));
            }
            return Task.FromResult((true, (short)0));
        }

        lock (_sync)
        {
            return Task.FromResult((true, _words.TryGetValue(address ?? string.Empty, out short value) ? value : (short)0));
        }
    }

    public Task<bool> WriteReleaseSignalAsync(string resultAddress)
    {
        return WriteResultAsync(resultAddress, (short)1);
    }

    public Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName)
    {
        if (!_connected)
        {
            LastError = "PLC is disconnected.";
            return Task.FromResult((false, string.Empty));
        }
        return Task.FromResult((true, string.Empty));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Disconnect();
    }

    private bool FailWrite(string message)
    {
        LastError = message;
        ErrorOccurred?.Invoke(message);
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SoakPlcService));
        }
    }
}

internal sealed class FaultInjectingSqliteDatabaseService : IDatabaseService
{
    private readonly SqliteDatabaseService _inner;
    private readonly string _dbPath;
    private readonly FaultPlan _faultPlan;

    public FaultInjectingSqliteDatabaseService(string dbPath, FaultPlan faultPlan)
    {
        _dbPath = Path.GetFullPath(dbPath ?? throw new ArgumentNullException(nameof(dbPath)));
        _faultPlan = faultPlan ?? throw new ArgumentNullException(nameof(faultPlan));
        _inner = new SqliteDatabaseService(_dbPath);
    }

    public Task InitializeAsync() => _inner.InitializeAsync();

    public async Task SaveDetectionRecordAsync(DetectionRecord record)
    {
        if (_faultPlan.TryBeginDatabaseLock(record.InspectionId))
        {
            await HoldExclusiveLockAsync().ConfigureAwait(false);
            _faultPlan.MarkFaultCleared(record.InspectionId, "SQLite exclusive lock window ended before the record write.");
        }

        if (record.InspectionId.Contains("SOAK-RECORD-QUEUE-PRESSURE", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(40).ConfigureAwait(false);
        }

        await _inner.SaveDetectionRecordAsync(record).ConfigureAwait(false);
    }

    public Task<List<DetectionRecord>> GetRecordsAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        bool? isQualified = null,
        int limit = 100) =>
        _inner.GetRecordsAsync(startDate, endDate, isQualified, limit);

    public Task<DetectionRecord?> GetDetectionRecordByIdAsync(long id) => _inner.GetDetectionRecordByIdAsync(id);
    public Task<List<DetectionRecord>> GetDetectionRecordsByInspectionIdAsync(string inspectionId) => _inner.GetDetectionRecordsByInspectionIdAsync(inspectionId);
    public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query) => _inner.GetTraceRecordsAsync(query);
    public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query) => _inner.GetTraceRecordPageAsync(query);
    public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query) => _inner.GetReplayRecordsAsync(query);
    public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60) => _inner.GetTraceDateKeysAsync(isQualified, limit);
    public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null) => _inner.GetTraceHourKeysAsync(date, isQualified);
    public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date) => _inner.GetStatisticsAsync(date);
    public Task<int> CleanupOldRecordsAsync(int daysToKeep) => _inner.CleanupOldRecordsAsync(daysToKeep);

    public void Dispose()
    {
        _inner.Dispose();
    }

    private async Task HoldExclusiveLockAsync()
    {
        string connectionString = $"Data Source={_dbPath};Cache=Shared;Default Timeout=5";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteCommand begin = connection.CreateCommand();
        begin.CommandText = "BEGIN EXCLUSIVE;";
        await begin.ExecuteNonQueryAsync().ConfigureAwait(false);
        try
        {
            await Task.Delay(120).ConfigureAwait(false);
        }
        finally
        {
            using SqliteCommand rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class ProductionGraphRunner
{
    private readonly SoakOptions _options;
    private readonly ExternalInputContract _input;
    private readonly SoakEvidence _evidence;
    private readonly AppRuntime _runtime;
    private readonly InspectionPipelineService _pipeline;
    private readonly SoakCameraService _camera;
    private readonly SoakPlcService _plc;
    private readonly FaultInjectingSqliteDatabaseService _database;
    private readonly FaultPlan _faultPlan;
    private readonly HashSet<string> _expectedInspectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<CycleEvidence> _allCycleEvidence = new List<CycleEvidence>();
    private readonly List<ResourceSample> _resourceSamples = new List<ResourceSample>();
    private readonly DateTimeOffset _runStartedAtUtc = DateTimeOffset.UtcNow;

    public ProductionGraphRunner(
        SoakOptions options,
        ExternalInputContract input,
        SoakEvidence evidence,
        AppRuntime runtime,
        InspectionPipelineService pipeline,
        SoakCameraService camera,
        SoakPlcService plc,
        FaultInjectingSqliteDatabaseService database,
        FaultPlan faultPlan)
    {
        _options = options;
        _input = input;
        _evidence = evidence;
        _runtime = runtime;
        _pipeline = pipeline;
        _camera = camera;
        _plc = plc;
        _database = database;
        _faultPlan = faultPlan;
        _evidence.Faults = new FaultEvidence
        {
            Seed = faultPlan.Seed,
            Enabled = faultPlan.Enabled
        };
    }

    public async Task RunAsync()
    {
        _evidence.Runtime.StartupCompleted = _runtime.StartupDiagnostics.CurrentReport != null;
        await RunPhaseAsync("preflight", _options.PreflightCycles, allowFaults: false, deadline: null).ConfigureAwait(false);
        if (_evidence.BlockingReasons.Count > 0)
        {
            _evidence.Status = "BLOCKED";
            _evidence.PromotionEligibility = "BLOCKED";
            return;
        }

        await RunScenarioCoverageAsync().ConfigureAwait(false);

        DateTimeOffset? deadline = _options.DurationMinutes > 0
            ? DateTimeOffset.UtcNow.AddMinutes(_options.DurationMinutes)
            : null;
        int mainCycleLimit = _options.Cycles > 0 ? _options.Cycles : (deadline.HasValue ? int.MaxValue : 1);
        await RunPhaseAsync("main", mainCycleLimit, allowFaults: _options.EnableFaultInjection, deadline).ConfigureAwait(false);
        SoakQueueWaitResult drain = await WaitForQueuesAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        _evidence.Queues.Drain = drain;
        _evidence.Runtime.QueueDrainStatus = drain.Status;
        _evidence.Runtime.QueueDrainElapsedMs = drain.ElapsedMs;
        if (!drain.Drained)
        {
            _evidence.BlockingReasons.Add(
                $"Persistence queues did not drain before final consistency scan: Image={drain.ImagePending}, Record={drain.RecordPending}.");
            _evidence.FinalConsistency = new ConsistencyEvidence
            {
                Status = "BLOCKED",
                QueueStatus = drain.Status,
                ScanStartedAtUtc = string.Empty,
                ScanFinishedAtUtc = string.Empty
            };
        }
        else
        {
            await ValidateConsistencyAsync(drain).ConfigureAwait(false);
        }

        _evidence.Provider = _runtime.DetectionService.RuntimeStatus;
        _evidence.Queues = QueueEvidence.From(_runtime.ImageSaveQueue, _runtime.DetectionRecordQueue);
        _evidence.Queues.Drain = drain;
        _evidence.Faults.Events.Clear();
        _evidence.Faults.Events.AddRange(_faultPlan.SnapshotEvents());
        _evidence.Resources.Samples.Clear();
        _evidence.Resources.Samples.AddRange(_resourceSamples);
        BuildResourceEvidence();
        ValidateFaultRecovery();
        _evidence.Faults.Events.Clear();
        _evidence.Faults.Events.AddRange(_faultPlan.SnapshotEvents());

        if (_evidence.FinalConsistency.Status != "PASS")
        {
            _evidence.Status = "BLOCKED";
            _evidence.BlockingReasons.AddRange(_evidence.FinalConsistency.Findings);
        }
        else if (_evidence.BlockingReasons.Count == 0 && _evidence.ScenarioCoverageStatus == "PASS")
        {
            _evidence.Status = "PASS";
        }
        else if (_evidence.BlockingReasons.Count == 0)
        {
            _evidence.Status = "NOT_VERIFIED";
        }

        _evidence.PromotionEligibility = "NOT_VERIFIED";
    }

    private async Task RunPhaseAsync(string phase, int limit, bool allowFaults, DateTimeOffset? deadline)
    {
        int executed = 0;
        while (executed < limit && (!deadline.HasValue || DateTimeOffset.UtcNow < deadline.Value))
        {
            executed++;
            string inspectionId = $"SOAK-{phase.ToUpperInvariant()}-{executed:000000}";
            bool allowFaultsThisCycle = allowFaults && (deadline.HasValue || executed < limit);
            CycleEvidence cycle = await RunCycleAsync(phase, executed, inspectionId, allowFaultsThisCycle).ConfigureAwait(false);
            _allCycleEvidence.Add(cycle);
            _expectedInspectionIds.Add(inspectionId);
            RecordResourceSample(cycle);

            if (cycle.Fault == SoakFaultKind.None.ToString() && cycle.CycleSucceeded)
            {
                FaultEventEvidence? pendingFault = _faultPlan.SnapshotEvents()
                    .Where(item => item.Injected && item.FaultCleared && !item.NextHealthyCycleRecovered)
                    .OrderBy(item => item.InjectedAt)
                    .FirstOrDefault();
                if (pendingFault != null)
                {
                    long recoveryMs = pendingFault.InjectedAt.HasValue
                        ? Math.Max(0, (long)(DateTimeOffset.UtcNow - pendingFault.InjectedAt.Value).TotalMilliseconds)
                        : 0;
                    _faultPlan.MarkNextHealthyCycle(pendingFault.InspectionId, inspectionId, recoveryMs);
                }
            }

            if (string.Equals(phase, "preflight", StringComparison.OrdinalIgnoreCase))
            {
                _evidence.Cycles.PreflightCycles++;
            }
            else
            {
                _evidence.Cycles.MainCycles++;
            }

            if (cycle.CycleSucceeded) _evidence.Cycles.SuccessfulCycles++;
            if (cycle.Qualified == true) _evidence.Cycles.QualifiedCycles++;
            if (cycle.Qualified == false) _evidence.Cycles.UnqualifiedCycles++;
            if (cycle.ExplicitFailure) _evidence.Cycles.ExplicitFailureCycles++;
            if (cycle.Cancelled) _evidence.Cycles.CancelledCycles++;

            if (_options.SampleEvery <= 1 || executed <= 3 || executed == limit || cycle.FaultExpected)
            {
                _evidence.Cycles.Samples.Add(cycle);
            }

            if (phase == "preflight" && cycle.ExplicitFailure)
            {
                _evidence.BlockingReasons.Add($"Preflight cycle {inspectionId} failed: {cycle.ErrorCode} {cycle.ErrorMessage}");
                return;
            }

            if (!cycle.FaultExpected && !cycle.CycleSucceeded && !cycle.Cancelled)
            {
                _evidence.BlockingReasons.Add($"Unexpected production graph failure at {inspectionId}: {cycle.ErrorCode} {cycle.ErrorMessage}");
                return;
            }

            if (executed % 25 == 0)
            {
                await Task.Yield();
            }
        }

        if (phase == "main" && executed == 0)
        {
            _evidence.NotVerifiedReasons.Add("The main soak phase did not execute because its duration or cycle limit was zero.");
        }
    }

    private async Task RunScenarioCoverageAsync()
    {
        ScenarioExecutionEvidence execution = new ScenarioExecutionEvidence
        {
            ExpectedSamples = _input.ScenarioContract.Samples.Count
        };
        _evidence.ScenarioExecution = execution;

        if (_input.ScenarioContract.Status != "PASS")
        {
            execution.Findings.Add("Scenario execution was not authorized because the external scenario manifest contract is not PASS.");
            return;
        }

        int sequence = 0;
        try
        {
            foreach (ExternalScenarioSample sample in _input.ScenarioContract.Samples)
            {
                sequence++;
                string inspectionId = $"SOAK-SCENARIO-{sequence:000000}";
                _camera.SetSourceImage(sample.Path);
                CycleEvidence cycle = await RunCycleAsync(
                    "scenario",
                    sequence,
                    inspectionId,
                    allowFaults: false,
                    allowExpectedFailure: true).ConfigureAwait(false);
                _allCycleEvidence.Add(cycle);
                _expectedInspectionIds.Add(inspectionId);
                RecordResourceSample(cycle);
                execution.ExecutedSamples++;

                string actualOutcome = cycle.Qualified == true ? "OK" : "NG";
                string actualTerminalState = cycle.CycleSucceeded ? "Successful" : "ExplicitFailure";
                bool outcomeMatches = string.Equals(sample.ExpectedOutcome, actualOutcome, StringComparison.OrdinalIgnoreCase);
                bool errorMatches = string.IsNullOrWhiteSpace(sample.ExpectedErrorCode) ||
                    string.Equals(sample.ExpectedErrorCode, cycle.ErrorCode, StringComparison.OrdinalIgnoreCase);
                bool terminalMatches = string.Equals(sample.ExpectedTerminalState, actualTerminalState, StringComparison.OrdinalIgnoreCase);
                bool passed = outcomeMatches && errorMatches && terminalMatches;
                string finding = passed
                    ? "Observed outcome, error code, and terminal state match the external scenario contract."
                    : $"Expected outcome={sample.ExpectedOutcome}, errorCode={sample.ExpectedErrorCode}, terminalState={sample.ExpectedTerminalState}; " +
                      $"actual outcome={actualOutcome}, errorCode={cycle.ErrorCode}, terminalState={actualTerminalState}.";
                execution.Samples.Add(new ScenarioExecutionResult
                {
                    Name = sample.Name,
                    Kind = sample.Kind,
                    InspectionId = inspectionId,
                    ExpectedOutcome = sample.ExpectedOutcome,
                    ActualOutcome = actualOutcome,
                    ExpectedErrorCode = sample.ExpectedErrorCode,
                    ActualErrorCode = cycle.ErrorCode,
                    ExpectedTerminalState = sample.ExpectedTerminalState,
                    ActualTerminalState = actualTerminalState,
                    Status = passed ? "PASS" : "BLOCKED",
                    Finding = finding
                });
                if (!passed)
                {
                    execution.Findings.Add($"Scenario '{sample.Name}' did not satisfy its execution contract: {finding}");
                }
            }

            execution.Status = execution.ExecutedSamples == execution.ExpectedSamples &&
                execution.Samples.All(sample => sample.Status == "PASS")
                ? "PASS"
                : "BLOCKED";
            _evidence.ScenarioCoverageStatus = execution.Status;
            if (execution.Status != "PASS")
            {
                _evidence.BlockingReasons.AddRange(execution.Findings);
            }
        }
        finally
        {
            _camera.SetSourceImage(_input.Image.Path);
        }
    }

    private async Task<CycleEvidence> RunCycleAsync(
        string phase,
        int sequence,
        string inspectionId,
        bool allowFaults,
        bool allowExpectedFailure = false)
    {
        SoakFaultKind fault = _faultPlan.BeginCycle(inspectionId, sequence, allowFaults);
        _camera.BeginCycle(inspectionId);
        _plc.BeginCycle(inspectionId);

        PlcTriggerContext triggerContext = new PlcTriggerContext
        {
            TriggerSource = "PLC",
            TriggerAddress = "D555",
            TriggerValue = 1,
            TriggerSeq = sequence,
            TriggerTime = DateTimeOffset.UtcNow
        };
        PlcHandshakeV1Result accepted = await new PlcHandshakeV1Coordinator(_plc, message => AddLog(message))
            .AcceptTriggerAsync(PlcHandshakeV1Addresses.FromConfig(_runtime.AppConfig), triggerContext)
            .ConfigureAwait(false);

        var context = new InspectionContext
        {
            InspectionId = inspectionId,
            TriggerTime = triggerContext.TriggerTime,
            TriggerSource = "PLC",
            TriggerSeq = sequence,
            PlcTriggerAccepted = accepted.Succeeded
        };

        if (!accepted.Succeeded)
        {
            _evidence.BlockingReasons.Add($"PLC trigger acceptance failed for {inspectionId}: {accepted.ErrorCode} {accepted.Message}");
        }

        if (fault == SoakFaultKind.PlcDisconnect)
        {
            _plc.Disconnect();
            _faultPlan.RecordHarnessInjection(inspectionId, fault, "PlcNotConnected", "PLC boundary disconnected after trigger acceptance.");
        }
        if (fault is SoakFaultKind.PlcWriteFailure or SoakFaultKind.ResultAckTimeout)
        {
            _faultPlan.ArmRuntimeFault(inspectionId);
        }

        bool modelUnloaded = false;
        if (fault == SoakFaultKind.ModelUnavailable)
        {
            _runtime.DetectionService.UnloadPrimaryModel();
            modelUnloaded = true;
            _faultPlan.RecordHarnessInjection(inspectionId, fault, "DetectionServiceError", "Primary model was unloaded for one production cycle.");
        }

        InspectionPipelineResult? result = null;
        bool cancelled = false;
        try
        {
            if (fault == SoakFaultKind.Cancellation)
            {
                using var cancellationSource = new CancellationTokenSource();
                cancellationSource.Cancel();
                _faultPlan.RecordHarnessInjection(inspectionId, fault, "OperationCanceled", "The production graph received a pre-cancelled token.");
                result = await _pipeline.ExecuteAsync(
                    new InspectionPipelineRequest("PLC", inspectionId, sequence, context),
                    cancellationSource.Token,
                    progress => RecordProgressAsync(progress)).ConfigureAwait(false);
            }
            else
            {
                result = await _pipeline.ExecuteAsync(
                    new InspectionPipelineRequest("PLC", inspectionId, sequence, context),
                    CancellationToken.None,
                    progress => RecordProgressAsync(progress)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _runtime.HealthMonitor.RecordInspection(context);
        }
        catch (Exception ex)
        {
            context.MarkFailed(InspectionStage.Failed, "SoakHostCycleException", ex.Message);
            _runtime.HealthMonitor.RecordError("V6SoakHost", ex.Message, inspectionId);
        }

        if (result != null)
        {
            _runtime.HealthMonitor.RecordInspection(result);
        }
        result?.Dispose();

        if ((fault is SoakFaultKind.CameraShortFrame or SoakFaultKind.CameraCaptureFailure) &&
            _faultPlan.SnapshotEvents().Any(item =>
                string.Equals(item.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase) && item.Injected))
        {
            _faultPlan.MarkFaultCleared(inspectionId, "The camera fault was consumed by the capture boundary and the cycle reached its terminal path.");
        }

        if (modelUnloaded)
        {
            bool reloaded = await _runtime.DetectionService.LoadModelAsync(
                _input.Model.Path,
                _options.UseGpu,
                _options.GpuIndex).ConfigureAwait(false);
            if (!reloaded)
            {
                _evidence.BlockingReasons.Add($"The model could not be reloaded after the injected outage at {inspectionId}.");
            }
            else
            {
                _faultPlan.MarkFaultCleared(inspectionId, "Primary model was loaded again after the expected outage.");
            }
        }

        if (fault == SoakFaultKind.PlcDisconnect)
        {
            bool reconnected = await _plc.ConnectAsync(new PlcConnectionOptions
            {
                Protocol = "BoundaryAdapter",
                DriverProvider = "BoundaryAdapter",
                Ip = "127.0.0.1",
                Port = 0,
                TriggerAddress = _runtime.AppConfig.PlcTriggerAddress
            }).ConfigureAwait(false);
            if (reconnected)
            {
                _faultPlan.MarkFaultCleared(inspectionId, "PLC boundary reconnected after the explicit terminal failure.");
            }
            else
            {
                _evidence.BlockingReasons.Add($"PLC boundary did not recover after {inspectionId}.");
            }
        }

        if (fault is SoakFaultKind.PlcWriteFailure or SoakFaultKind.ResultAckTimeout)
        {
            _faultPlan.DisarmRuntimeFault(inspectionId);
            if (_faultPlan.SnapshotEvents().Any(item =>
                    string.Equals(item.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase) && item.Injected))
            {
                _faultPlan.MarkFaultCleared(inspectionId, "The one-cycle PLC runtime fault was disarmed.");
            }
        }

        if (fault == SoakFaultKind.ImageTargetUnavailable)
        {
            if (await ExerciseImageTargetFaultAsync(inspectionId).ConfigureAwait(false))
            {
                _faultPlan.MarkFaultCleared(inspectionId, "The failed image target was observed and a recovery image was persisted.");
            }
        }
        else if (fault == SoakFaultKind.ImageQueueBackpressure)
        {
            _faultPlan.RecordHarnessInjection(
                inspectionId,
                fault,
                "ImageQueue.Backpressure",
                "The bounded image queue was filled beyond capacity by the deterministic harness.");
            if (await ExerciseImageQueuePressureAsync(inspectionId).ConfigureAwait(false))
            {
                _faultPlan.MarkFaultCleared(inspectionId, "Image queue pressure was observed and the queue drained.");
            }
        }
        else if (fault == SoakFaultKind.RecordQueueBackpressure)
        {
            _faultPlan.RecordHarnessInjection(
                inspectionId,
                fault,
                "RecordQueue.Backpressure",
                "The bounded record queue was filled beyond capacity by the deterministic harness.");
            if (await ExerciseRecordQueuePressureAsync(inspectionId).ConfigureAwait(false))
            {
                _faultPlan.MarkFaultCleared(inspectionId, "Record queue pressure was observed and the queue drained.");
            }
        }

        bool explicitFailure = cancelled ||
            !context.CycleSucceeded ||
            (context.TerminalHandshakeAttempted && !context.TerminalHandshakeSucceeded);
        bool injected = fault == SoakFaultKind.None || _faultPlan.SnapshotEvents().Any(item =>
            string.Equals(item.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase) && item.Injected);
        string recoveryStatus = fault == SoakFaultKind.None
            ? "NOT_APPLICABLE"
            : injected ? "AWAITING_NEXT_HEALTHY_CYCLE" : "NOT_INJECTED";

        string errorCode = !string.IsNullOrWhiteSpace(context.ErrorCode)
            ? context.ErrorCode!
            : context.TerminalHandshakeErrorCode;
        string errorMessage = !string.IsNullOrWhiteSpace(context.ErrorMessage)
            ? context.ErrorMessage!
            : context.TerminalHandshakeMessage;
        string terminalState = context.CycleSucceeded ? "Successful" : "ExplicitFailure";
        _faultPlan.MarkTerminalOutcome(inspectionId, errorCode, terminalState);
        var cycleEvidence = new CycleEvidence
        {
            Phase = phase,
            Sequence = sequence,
            InspectionId = inspectionId,
            Fault = fault.ToString(),
            FaultExpected = fault != SoakFaultKind.None,
            TriggerAccepted = accepted.Succeeded,
            Qualified = result?.FinalQualified,
            ProductFlowSucceeded = result?.ProductFlowSucceeded ?? context.CycleSucceeded,
            CycleSucceeded = context.CycleSucceeded,
            TerminalHandshakeAttempted = context.TerminalHandshakeAttempted,
            TerminalHandshakeSucceeded = context.TerminalHandshakeSucceeded,
            Cancelled = cancelled,
            ExplicitFailure = explicitFailure,
            RecoveryVerified = false,
            RecoveryStatus = recoveryStatus,
            ExpectedTerminalState = _faultPlan.SnapshotEvents()
                .FirstOrDefault(item => string.Equals(item.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase))?.ExpectedTerminalState ?? string.Empty,
            ImageQueueLatencyMs = context.SaveImageMs,
            RecordQueueLatencyMs = context.SaveRecordMs,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            TraceStatus = context.TraceStatus.ToString(),
            ModelName = result?.UsedModelName ?? _runtime.DetectionService.CurrentModelName,
            TotalMs = context.TotalMs,
            ResultCount = result?.FinalResultCount ?? 0,
            ImageQueuePending = _runtime.ImageSaveQueue.PendingCount,
            RecordQueuePending = _runtime.DetectionRecordQueue.PendingCount
        };

        if (fault == SoakFaultKind.None && !cycleEvidence.CycleSucceeded && !allowExpectedFailure)
        {
            _evidence.BlockingReasons.Add($"A normal cycle did not reach a successful terminal state: {inspectionId} ({errorCode}).");
        }
        return cycleEvidence;
    }

    private async Task<bool> ExerciseImageTargetFaultAsync(string inspectionId)
    {
        using Mat image = Cv2.ImRead(_input.Image.Path, ImreadModes.Color);
        if (image.Empty())
        {
            _evidence.BlockingReasons.Add("The external validation image could not be decoded for image target fault recovery.");
            return false;
        }

        long failedBefore = _runtime.ImageSaveQueue.FailedCount;
        string directory = Path.Combine(_runtime.StorageService.ImageBasePath, "SoakFaults");
        string failedPath = Path.Combine(directory, $"{inspectionId}-image-save-failure.jpg");
        string recoveryPath = Path.Combine(directory, $"{inspectionId}-image-recovered.jpg");
        _runtime.ImageSaveQueue.Enqueue(image, failedPath);
        SoakQueueWaitResult failedDrain = await WaitForQueuesAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!failedDrain.Drained)
        {
            _evidence.BlockingReasons.Add($"Image target fault queue did not drain for {inspectionId}.");
            return false;
        }
        if (_runtime.ImageSaveQueue.FailedCount <= failedBefore)
        {
            _evidence.BlockingReasons.Add($"Injected image target failure was not observed for {inspectionId}.");
            return false;
        }

        _runtime.ImageSaveQueue.Enqueue(image, recoveryPath);
        SoakQueueWaitResult recoveryDrain = await WaitForQueuesAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!recoveryDrain.Drained)
        {
            _evidence.BlockingReasons.Add($"Image target recovery queue did not drain for {inspectionId}.");
            return false;
        }
        if (!File.Exists(recoveryPath))
        {
            _evidence.BlockingReasons.Add($"Image target recovery did not produce a file for {inspectionId}.");
            return false;
        }
        return true;
    }

    private async Task<bool> ExerciseImageQueuePressureAsync(string inspectionId)
    {
        using Mat image = Cv2.ImRead(_input.Image.Path, ImreadModes.Color);
        if (image.Empty())
        {
            _evidence.BlockingReasons.Add("The external validation image could not be decoded for image queue pressure.");
            return false;
        }

        long droppedBefore = _runtime.ImageSaveQueue.DroppedCount;
        int payloadCount = Math.Max(32, _runtime.ImageSaveQueue.Capacity * 8);
        string directory = Path.Combine(_runtime.StorageService.ImageBasePath, "SoakFaults", "QueuePressure");
        for (int index = 0; index < payloadCount; index++)
        {
            _runtime.ImageSaveQueue.Enqueue(
                image,
                Path.Combine(directory, $"{inspectionId}-queue-pressure-{index:0000}.jpg"),
                purpose: ImageSavePurpose.General);
        }

        SoakQueueWaitResult drain = await WaitForQueuesAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (!drain.Drained)
        {
            _evidence.BlockingReasons.Add($"Image queue pressure did not drain for {inspectionId}.");
            return false;
        }
        if (_runtime.ImageSaveQueue.DroppedCount <= droppedBefore)
        {
            _evidence.BlockingReasons.Add($"Image queue pressure did not produce an observable bounded-queue drop for {inspectionId}.");
            return false;
        }
        return true;
    }

    private async Task<bool> ExerciseRecordQueuePressureAsync(string inspectionId)
    {
        long droppedBefore = _runtime.DetectionRecordQueue.DroppedCount;
        int payloadCount = Math.Max(64, _runtime.DetectionRecordQueue.Capacity * 3);
        for (int index = 0; index < payloadCount; index++)
        {
            _runtime.DetectionRecordQueue.Enqueue(new DetectionPersistencePayload
            {
                Timestamp = DateTime.UtcNow,
                IsQualified = false,
                InspectionId = $"SOAK-RECORD-QUEUE-PRESSURE-{inspectionId}-{index:0000}",
                TriggerSource = "SOAK-QUEUE-PRESSURE",
                TraceStatus = TraceStatus.Failed,
                QueueStatus = "harness-pressure",
                ErrorStage = "QueuePressure",
                ErrorCode = "QueuePressure.Expected",
                ErrorMessage = "Deterministic record queue pressure sample.",
                ModelName = Path.GetFileName(_input.Model.Path),
                UsedModelName = Path.GetFileName(_input.Model.Path)
            });
        }

        SoakQueueWaitResult drain = await WaitForQueuesAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!drain.Drained)
        {
            _evidence.BlockingReasons.Add($"Record queue pressure did not drain for {inspectionId}.");
            return false;
        }
        if (_runtime.DetectionRecordQueue.DroppedCount <= droppedBefore)
        {
            _evidence.BlockingReasons.Add($"Record queue pressure did not produce an observable bounded-queue drop for {inspectionId}.");
            return false;
        }
        return true;
    }

    private async Task ValidateConsistencyAsync(SoakQueueWaitResult drain)
    {
        DateTimeOffset scanStarted = DateTimeOffset.UtcNow;
        var records = new List<SoakConsistencyRecord>();
        try
        {
            Dictionary<string, CycleEvidence> expectedCycles = _allCycleEvidence
                .GroupBy(cycle => cycle.InspectionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (string inspectionId in _expectedInspectionIds)
            {
                List<DetectionRecord> inspectionRecords = await _database
                    .GetDetectionRecordsByInspectionIdAsync(inspectionId)
                    .ConfigureAwait(false);
                bool expectedSuccess = expectedCycles.TryGetValue(inspectionId, out CycleEvidence? cycle) && cycle.CycleSucceeded;
                foreach (DetectionRecord record in inspectionRecords)
                {
                    bool tracePresent = record.TraceStatus is TraceStatus.Queued or TraceStatus.Full &&
                        (!string.IsNullOrWhiteSpace(record.TraceImagePath) && File.Exists(record.TraceImagePath));
                    records.Add(new SoakConsistencyRecord
                    {
                        InspectionId = record.InspectionId,
                        CycleSucceeded = expectedSuccess,
                        ImagePresent = !string.IsNullOrWhiteSpace(record.ImagePath) && File.Exists(record.ImagePath),
                        TracePresent = tracePresent
                    });
                }
            }

            DateTimeOffset scanFinished = DateTimeOffset.UtcNow;
            SoakConsistencyResult result = SoakConsistencyEvaluator.Evaluate(
                _expectedInspectionIds,
                records,
                drain.Drained,
                scanStarted,
                scanFinished,
                drain.Status);
            var consistency = new ConsistencyEvidence
            {
                Status = result.Status,
                ScanStartedAtUtc = result.ScanStartedAtUtc.ToString("O"),
                ScanFinishedAtUtc = result.ScanFinishedAtUtc.ToString("O"),
                QueueStatus = result.QueueStatus,
                RecordsRead = result.RecordsRead,
                ExpectedInspectionIds = result.ExpectedInspectionIds,
                MissingRecords = result.MissingRecords,
                DuplicateInspectionIds = result.DuplicateInspectionIds,
                MissingImages = result.MissingImages,
                MissingTraceRecords = result.MissingTraceRecords
            };
            consistency.MissingInspectionIds.AddRange(result.MissingInspectionIds);
            consistency.Findings.AddRange(result.Findings);
            _evidence.FinalConsistency = consistency;
        }
        catch (Exception ex)
        {
            _evidence.FinalConsistency = new ConsistencyEvidence
            {
                Status = "BLOCKED",
                ScanStartedAtUtc = scanStarted.ToString("O"),
                ScanFinishedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                QueueStatus = drain.Status
            };
            _evidence.FinalConsistency.Findings.Add($"Final consistency scan failed: {ex.Message}");
        }

        _evidence.Cycles.DuplicateInspectionIdCount = _evidence.FinalConsistency.DuplicateInspectionIds;
        _evidence.Cycles.MissingRecordCount = _evidence.FinalConsistency.MissingRecords;
        _evidence.Cycles.MissingTraceCount = _evidence.FinalConsistency.MissingTraceRecords;
    }

    private Task<SoakQueueWaitResult> WaitForQueuesAsync(TimeSpan timeout)
    {
        return SoakQueueWaiter.WaitAsync(
            () => new SoakQueueSnapshot
            {
                ImagePending = _runtime.ImageSaveQueue.PendingCount,
                RecordPending = _runtime.DetectionRecordQueue.PendingCount,
                ImageInFlight = _runtime.ImageSaveQueue.InFlightCount,
                RecordInFlight = _runtime.DetectionRecordQueue.InFlightCount
            },
            timeout);
    }

    private void RecordResourceSample(CycleEvidence cycle)
    {
        using Process process = Process.GetCurrentProcess();
        int threadCount = ExternalFileIdentity.GetCurrentThreadCount();
        int handleCount = ExternalFileIdentity.GetCurrentHandleCount();
        if (threadCount < 0)
        {
            _evidence.BlockingReasons.Add("Unable to read the process thread count for a resource sample.");
        }
        if (handleCount < 0)
        {
            _evidence.BlockingReasons.Add("Unable to read the process handle count for a resource sample.");
        }

        _resourceSamples.Add(new ResourceSample
        {
            AtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Cycle = _resourceSamples.Count + 1,
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            GcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            ThreadCount = threadCount,
            HandleCount = handleCount,
            ImageQueuePending = cycle.ImageQueuePending,
            RecordQueuePending = cycle.RecordQueuePending,
            ImageQueueLatencyMs = cycle.ImageQueueLatencyMs,
            RecordQueueLatencyMs = cycle.RecordQueueLatencyMs,
            CycleMs = cycle.TotalMs,
            FaultRecoveryMs = 0
        });
    }

    private void BuildResourceEvidence()
    {
        ResourceSample[] samples = _resourceSamples.ToArray();
        _evidence.Resources.ThroughputCyclesPerSecond = GetThroughput(samples);
        _evidence.Cycles.ThroughputCyclesPerSecond = _evidence.Resources.ThroughputCyclesPerSecond;
        if (samples.Length > 0)
        {
            _evidence.Cycles.FirstResourceSample = samples[0];
            _evidence.Cycles.LastResourceSample = samples[^1];
        }

        _evidence.Resources.QueueLatency = new QueueLatencyEvidence
        {
            Image = CalculatePercentiles(samples.Select(sample => (double)sample.ImageQueueLatencyMs)),
            Record = CalculatePercentiles(samples.Select(sample => (double)sample.RecordQueueLatencyMs)),
            Cycle = CalculatePercentiles(samples.Select(sample => (double)sample.CycleMs))
        };

        if (samples.Length < 3)
        {
            _evidence.Resources.Trend = new ResourceTrendEvidence
            {
                Status = "NOT_VERIFIED",
                SampleCount = samples.Length,
                Finding = "At least three periodic samples are required for a resource trend decision."
            };
            _evidence.NotVerifiedReasons.Add("Resource trend is NOT_VERIFIED because fewer than three periodic samples were collected.");
            return;
        }

        double workingSetSlope = CalculateSlope(samples.Select(sample => (double)sample.WorkingSetBytes).ToArray());
        double privateBytesSlope = CalculateSlope(samples.Select(sample => (double)sample.PrivateBytes).ToArray());
        double gcHeapSlope = CalculateSlope(samples.Select(sample => (double)sample.GcHeapBytes).ToArray());
        bool unbounded = HasUnboundedTrend(samples.Select(sample => (double)sample.WorkingSetBytes).ToArray(), workingSetSlope) ||
            HasUnboundedTrend(samples.Select(sample => (double)sample.PrivateBytes).ToArray(), privateBytesSlope) ||
            HasUnboundedTrend(samples.Select(sample => (double)sample.GcHeapBytes).ToArray(), gcHeapSlope);
        _evidence.Resources.Trend = new ResourceTrendEvidence
        {
            Status = unbounded ? "BLOCKED" : "PASS",
            SampleCount = samples.Length,
            WorkingSetSlopeBytesPerSample = workingSetSlope,
            PrivateBytesSlopeBytesPerSample = privateBytesSlope,
            GcHeapSlopeBytesPerSample = gcHeapSlope,
            Finding = unbounded
                ? "Periodic resource samples show a sustained upward trend beyond the bounded-growth threshold."
                : "Periodic resource samples do not show a sustained upward trend beyond the bounded-growth threshold."
        };
        if (unbounded)
        {
            _evidence.BlockingReasons.Add(_evidence.Resources.Trend.Finding);
        }
    }

    private void ValidateFaultRecovery()
    {
        foreach (FaultEventEvidence item in _faultPlan.SnapshotEvents())
        {
            bool injectionMatches = item.Planned && item.Injected &&
                string.Equals(item.ErrorCode, item.ExpectedErrorCode, StringComparison.OrdinalIgnoreCase);
            bool terminalMatches = string.Equals(item.ActualTerminalState, item.ExpectedTerminalState, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ActualTerminalErrorCode, item.ExpectedTerminalErrorCode, StringComparison.OrdinalIgnoreCase);
            bool recovered = injectionMatches && terminalMatches && item.FaultCleared && item.NextHealthyCycleRecovered;
            if (!injectionMatches)
            {
                _evidence.BlockingReasons.Add($"Planned fault {item.Fault} at {item.InspectionId} was not injected with the expected error code.");
            }
            if (!terminalMatches)
            {
                _evidence.BlockingReasons.Add($"Planned fault {item.Fault} at {item.InspectionId} did not reach its expected terminal outcome.");
            }
            if (!item.FaultCleared)
            {
                _evidence.BlockingReasons.Add($"Planned fault {item.Fault} at {item.InspectionId} was not cleared.");
            }
            if (!item.NextHealthyCycleRecovered)
            {
                _evidence.BlockingReasons.Add($"Planned fault {item.Fault} at {item.InspectionId} has no successful subsequent healthy cycle.");
            }
            if (!recovered || item.RecoveryStatus != "RECOVERED")
            {
                _evidence.BlockingReasons.Add($"Planned fault {item.Fault} at {item.InspectionId} did not satisfy the complete recovery contract.");
            }
            if (recovered)
            {
                _evidence.Resources.MaxFaultRecoveryMs = Math.Max(_evidence.Resources.MaxFaultRecoveryMs, item.RecoveryDurationMs);
            }
        }
    }

    private double GetThroughput(ResourceSample[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double elapsedSeconds = Math.Max(0.001, (DateTimeOffset.UtcNow - _runStartedAtUtc).TotalSeconds);
        return _allCycleEvidence.Count(cycle => cycle.CycleSucceeded) / elapsedSeconds;
    }

    private static QueuePercentileEvidence CalculatePercentiles(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return new QueuePercentileEvidence();
        }

        return new QueuePercentileEvidence
        {
            SampleCount = sorted.Length,
            P50 = Percentile(sorted, 0.50),
            P95 = Percentile(sorted, 0.95),
            P99 = Percentile(sorted, 0.99),
            Maximum = sorted[^1]
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double CalculateSlope(double[] values)
    {
        if (values.Length < 2)
        {
            return 0;
        }

        double meanX = (values.Length - 1) / 2d;
        double meanY = values.Average();
        double numerator = 0;
        double denominator = 0;
        for (int index = 0; index < values.Length; index++)
        {
            double deltaX = index - meanX;
            numerator += deltaX * (values[index] - meanY);
            denominator += deltaX * deltaX;
        }
        return denominator == 0 ? 0 : numerator / denominator;
    }

    private static bool HasUnboundedTrend(double[] values, double slope)
    {
        int segmentLength = Math.Max(1, values.Length / 3);
        double head = values.Take(segmentLength).Average();
        double tail = values.Skip(values.Length - segmentLength).Average();
        return slope > Math.Max(1024d * 1024d, head * 0.02d) && tail > head * 1.25d;
    }

    private Task RecordProgressAsync(InspectionPipelineProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            AddLog($"{progress.Context.InspectionId}: {progress.Message}");
        }
        return Task.CompletedTask;
    }

    private void AddLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (_evidence.RecentLogs.Count >= 200)
        {
            _evidence.RecentLogs.RemoveAt(0);
        }
        _evidence.RecentLogs.Add(message);
    }
}
