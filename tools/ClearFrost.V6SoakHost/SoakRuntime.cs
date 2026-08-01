using System.Diagnostics;
using System.Globalization;
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
    public SoakOptions Options { get; init; } = new SoakOptions();
    public object? InputContract { get; set; }
    public object? Model { get; set; }
    public object? ValidationImage { get; set; }
    public YoloModelDescriptor? ModelDescriptor { get; set; }
    public DetectionRuntimeStatus? Provider { get; set; }
    public RuntimeEvidence Runtime { get; set; } = new RuntimeEvidence();
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
        evidence.NotVerifiedReasons.Add("Real camera, PLC, and FAT/SAT were not exercised by this boundary-limited soak host.");
        return evidence;
    }

    public void Complete()
    {
        FinishedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }
}

internal sealed class RuntimeEvidence
{
    public string Root { get; init; } = string.Empty;
    public string AppDataRoot { get; init; } = string.Empty;
    public string StorageRoot { get; init; } = string.Empty;
    public string ProfileRoot { get; init; } = string.Empty;
    public bool IsolatedAppData { get; set; }
    public bool IsolatedStorage { get; set; }
    public bool SourceTreeReferenced { get; set; }
    public bool DevelopmentAppDataReferenced { get; set; }
    public bool StartupCompleted { get; set; }
    public bool NormalShutdownCompleted { get; set; }
    public bool CancellationShutdownCompleted { get; set; }
    public bool FileLocksReleased { get; set; }
    public int ProcessCountAfterShutdown { get; set; }
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
    public string ErrorCode { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public string TraceStatus { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public long TotalMs { get; init; }
    public int ResultCount { get; init; }
    public long ImageQueuePending { get; init; }
    public long RecordQueuePending { get; init; }
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
    public bool Planned { get; set; }
    public bool Injected { get; set; }
    public DateTimeOffset? InjectedAt { get; set; }
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
    public long ImageSaved { get; init; }
    public long ImageDropped { get; init; }
    public long ImageFailed { get; init; }
    public int RecordCapacity { get; init; }
    public long RecordPending { get; init; }
    public long RecordSaved { get; init; }
    public long RecordDropped { get; init; }
    public long RecordFailed { get; init; }

    public static QueueEvidence From(ImageSaveQueue imageQueue, DetectionRecordQueue recordQueue)
    {
        return new QueueEvidence
        {
            ImageCapacity = imageQueue.Capacity,
            ImagePending = imageQueue.PendingCount,
            ImagePendingBytes = imageQueue.PendingBytes,
            ImageSaved = imageQueue.SavedCount,
            ImageDropped = imageQueue.DroppedCount,
            ImageFailed = imageQueue.FailedCount,
            RecordCapacity = recordQueue.Capacity,
            RecordPending = recordQueue.PendingCount,
            RecordSaved = recordQueue.SavedCount,
            RecordDropped = recordQueue.DroppedCount,
            RecordFailed = recordQueue.FailedCount
        };
    }
}

internal sealed class ConsistencyEvidence
{
    public string Status { get; set; } = "NOT_VERIFIED";
    public int RecordsRead { get; set; }
    public int ExpectedInspectionIds { get; set; }
    public int MissingRecords { get; set; }
    public int DuplicateInspectionIds { get; set; }
    public int MissingImages { get; set; }
    public int InvalidTraceRecords { get; set; }
    public List<string> Findings { get; } = new List<string>();
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
                _events.Add(new FaultEventEvidence
                {
                    InspectionId = inspectionId,
                    Fault = fault.ToString(),
                    Planned = true
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
        return Consume(inspectionId, SoakFaultKind.CameraShortFrame, "camera-short-frame", "Camera.ShortFrame");
    }

    public bool ConsumeCameraCaptureFailure(string inspectionId)
    {
        return Consume(inspectionId, SoakFaultKind.CameraCaptureFailure, "camera-capture-failure", "Camera.CaptureFailed");
    }

    public bool ConsumePlcWriteFailure(string inspectionId)
    {
        if (!IsRuntimeFaultArmed(inspectionId))
        {
            return false;
        }

        return Consume(inspectionId, SoakFaultKind.PlcWriteFailure, "plc-write-failure", "PLC.WriteFailed");
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
                    Planned = true
                });
            }
            MarkInjectedLocked(inspectionId, errorCode, details);
        }
    }

    public void MarkRecovered(string inspectionId, string status, string details)
    {
        lock (_sync)
        {
            FaultEventEvidence? item = _events.LastOrDefault(entry => string.Equals(entry.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return;
            }

            item.Recovered = true;
            item.RecoveryStatus = status;
            item.RecoveryDetails = details;
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

    private void MarkInjectedLocked(string inspectionId, string errorCode, string details)
    {
        FaultEventEvidence? item = _events.LastOrDefault(entry => string.Equals(entry.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            item = new FaultEventEvidence
            {
                InspectionId = inspectionId,
                Fault = GetScenarioLocked(inspectionId).ToString(),
                Planned = true
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

    private static FaultEventEvidence CloneEvent(FaultEventEvidence source)
    {
        return new FaultEventEvidence
        {
            InspectionId = source.InspectionId,
            Fault = source.Fault,
            ErrorCode = source.ErrorCode,
            Planned = source.Planned,
            Injected = source.Injected,
            InjectedAt = source.InjectedAt,
            Recovered = source.Recovered,
            RecoveryStatus = source.RecoveryStatus,
            Details = source.Details,
            RecoveryDetails = source.RecoveryDetails
        };
    }
}

internal sealed class SoakCameraService : ICameraService, ICameraCaptureDiagnostics
{
    private readonly string _imagePath;
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
            _faultPlan.MarkRecovered(record.InspectionId, "RECOVERED", "SQLite exclusive lock window ended before the record write.");
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

        DateTimeOffset? deadline = _options.DurationMinutes > 0
            ? DateTimeOffset.UtcNow.AddMinutes(_options.DurationMinutes)
            : null;
        int mainCycleLimit = _options.Cycles > 0 ? _options.Cycles : (deadline.HasValue ? int.MaxValue : 1);
        await RunPhaseAsync("main", mainCycleLimit, allowFaults: _options.EnableFaultInjection, deadline).ConfigureAwait(false);
        await WaitForQueuesAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await ValidateConsistencyAsync().ConfigureAwait(false);

        _evidence.Provider = _runtime.DetectionService.RuntimeStatus;
        _evidence.Queues = QueueEvidence.From(_runtime.ImageSaveQueue, _runtime.DetectionRecordQueue);
        _evidence.Faults.Events.Clear();
        _evidence.Faults.Events.AddRange(_faultPlan.SnapshotEvents());

        if (_evidence.FinalConsistency.Status != "PASS")
        {
            _evidence.Status = "BLOCKED";
            _evidence.BlockingReasons.AddRange(_evidence.FinalConsistency.Findings);
        }
        else if (_evidence.BlockingReasons.Count == 0)
        {
            _evidence.Status = "PASS";
        }

        _evidence.PromotionEligibility = _evidence.Status == "PASS" ? "NOT_VERIFIED" : "BLOCKED";
    }

    private async Task RunPhaseAsync(string phase, int limit, bool allowFaults, DateTimeOffset? deadline)
    {
        int executed = 0;
        while (executed < limit && (!deadline.HasValue || DateTimeOffset.UtcNow < deadline.Value))
        {
            executed++;
            string inspectionId = $"SOAK-{phase.ToUpperInvariant()}-{executed:000000}";
            CycleEvidence cycle = await RunCycleAsync(phase, executed, inspectionId, allowFaults).ConfigureAwait(false);
            _allCycleEvidence.Add(cycle);
            _expectedInspectionIds.Add(inspectionId);

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

    private async Task<CycleEvidence> RunCycleAsync(string phase, int sequence, string inspectionId, bool allowFaults)
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
            _faultPlan.RecordHarnessInjection(inspectionId, fault, "PLC.Disconnected", "PLC boundary disconnected after trigger acceptance.");
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
            _faultPlan.RecordHarnessInjection(inspectionId, fault, "Detection.ModelUnavailable", "Primary model was unloaded for one production cycle.");
        }

        InspectionPipelineResult? result = null;
        bool cancelled = false;
        try
        {
            if (fault == SoakFaultKind.Cancellation)
            {
                using var cancellationSource = new CancellationTokenSource();
                cancellationSource.Cancel();
                _faultPlan.RecordHarnessInjection(inspectionId, fault, "Inspection.Cancelled", "The production graph received a pre-cancelled token.");
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
                _faultPlan.MarkRecovered(inspectionId, "RECOVERED", "Primary model was loaded again after the expected outage.");
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
                _faultPlan.MarkRecovered(inspectionId, "RECOVERED", "PLC boundary reconnected after the explicit terminal failure.");
            }
            else
            {
                _evidence.BlockingReasons.Add($"PLC boundary did not recover after {inspectionId}.");
            }
        }

        if (fault is SoakFaultKind.PlcWriteFailure or SoakFaultKind.ResultAckTimeout)
        {
            _faultPlan.DisarmRuntimeFault(inspectionId);
        }

        if (fault == SoakFaultKind.ImageTargetUnavailable)
        {
            await ExerciseImageTargetFaultAsync(inspectionId).ConfigureAwait(false);
        }
        else if (fault == SoakFaultKind.ImageQueueBackpressure)
        {
            await ExerciseImageQueuePressureAsync(inspectionId).ConfigureAwait(false);
        }
        else if (fault == SoakFaultKind.RecordQueueBackpressure)
        {
            await ExerciseRecordQueuePressureAsync(inspectionId).ConfigureAwait(false);
        }

        bool explicitFailure = cancelled ||
            !context.CycleSucceeded ||
            (context.TerminalHandshakeAttempted && !context.TerminalHandshakeSucceeded);
        bool recovered = fault == SoakFaultKind.None || context.CycleSucceeded || cancelled || explicitFailure;
        string recoveryStatus = fault == SoakFaultKind.None
            ? "NOT_APPLICABLE"
            : explicitFailure
                ? "EXPLICIT_FAILURE"
                : "RECOVERED";

        if (fault == SoakFaultKind.Cancellation)
        {
            recoveryStatus = "EXPLICIT_CANCELLATION";
        }
        if (fault == SoakFaultKind.ModelUnavailable && modelUnloaded && _runtime.DetectionService.IsModelLoaded)
        {
            recovered = true;
            recoveryStatus = "RECOVERED";
        }
        if (fault == SoakFaultKind.PlcDisconnect && _plc.IsConnected)
        {
            recovered = true;
            recoveryStatus = "RECOVERED";
        }
        if (fault is SoakFaultKind.ImageTargetUnavailable or SoakFaultKind.ImageQueueBackpressure or SoakFaultKind.RecordQueueBackpressure)
        {
            recovered = true;
            recoveryStatus = "PRESSURE_DRAINED";
        }

        if (fault != SoakFaultKind.None)
        {
            string details = !string.IsNullOrWhiteSpace(context.ErrorMessage)
                ? context.ErrorMessage!
                : context.TerminalHandshakeMessage;
            _faultPlan.MarkRecovered(
                inspectionId,
                recoveryStatus,
                string.IsNullOrWhiteSpace(details)
                    ? "Expected fault completed with an explicit terminal state."
                    : details);
        }

        string errorCode = !string.IsNullOrWhiteSpace(context.ErrorCode)
            ? context.ErrorCode!
            : context.TerminalHandshakeErrorCode;
        string errorMessage = !string.IsNullOrWhiteSpace(context.ErrorMessage)
            ? context.ErrorMessage!
            : context.TerminalHandshakeMessage;
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
            RecoveryVerified = recovered,
            RecoveryStatus = recoveryStatus,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            TraceStatus = context.TraceStatus.ToString(),
            ModelName = result?.UsedModelName ?? _runtime.DetectionService.CurrentModelName,
            TotalMs = context.TotalMs,
            ResultCount = result?.FinalResultCount ?? 0,
            ImageQueuePending = _runtime.ImageSaveQueue.PendingCount,
            RecordQueuePending = _runtime.DetectionRecordQueue.PendingCount
        };

        if (fault == SoakFaultKind.None && !cycleEvidence.CycleSucceeded)
        {
            _evidence.BlockingReasons.Add($"A normal cycle did not reach a successful terminal state: {inspectionId} ({errorCode}).");
        }
        if (fault != SoakFaultKind.None && !cycleEvidence.RecoveryVerified)
        {
            _evidence.BlockingReasons.Add($"Injected fault was neither recovered nor explicitly terminated: {inspectionId} ({fault}).");
        }

        return cycleEvidence;
    }

    private async Task ExerciseImageTargetFaultAsync(string inspectionId)
    {
        using Mat image = Cv2.ImRead(_input.Image.Path, ImreadModes.Color);
        if (image.Empty())
        {
            _evidence.BlockingReasons.Add("The external validation image could not be decoded for image target fault recovery.");
            return;
        }

        long failedBefore = _runtime.ImageSaveQueue.FailedCount;
        string directory = Path.Combine(_runtime.StorageService.ImageBasePath, "SoakFaults");
        string failedPath = Path.Combine(directory, $"{inspectionId}-image-save-failure.jpg");
        string recoveryPath = Path.Combine(directory, $"{inspectionId}-image-recovered.jpg");
        _runtime.ImageSaveQueue.Enqueue(image, failedPath);
        await WaitForQueuesAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (_runtime.ImageSaveQueue.FailedCount <= failedBefore)
        {
            _evidence.BlockingReasons.Add($"Injected image target failure was not observed for {inspectionId}.");
            return;
        }

        _runtime.ImageSaveQueue.Enqueue(image, recoveryPath);
        await WaitForQueuesAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!File.Exists(recoveryPath))
        {
            _evidence.BlockingReasons.Add($"Image target recovery did not produce a file for {inspectionId}.");
        }
    }

    private async Task ExerciseImageQueuePressureAsync(string inspectionId)
    {
        using Mat image = Cv2.ImRead(_input.Image.Path, ImreadModes.Color);
        if (image.Empty())
        {
            _evidence.BlockingReasons.Add("The external validation image could not be decoded for image queue pressure.");
            return;
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

        await WaitForQueuesAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (_runtime.ImageSaveQueue.DroppedCount <= droppedBefore)
        {
            _evidence.BlockingReasons.Add($"Image queue pressure did not produce an observable bounded-queue drop for {inspectionId}.");
        }
    }

    private async Task ExerciseRecordQueuePressureAsync(string inspectionId)
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

        await WaitForQueuesAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (_runtime.DetectionRecordQueue.DroppedCount <= droppedBefore)
        {
            _evidence.BlockingReasons.Add($"Record queue pressure did not produce an observable bounded-queue drop for {inspectionId}.");
        }
    }

    private async Task ValidateConsistencyAsync()
    {
        var consistency = new ConsistencyEvidence();
        try
        {
            List<DetectionRecord> records = await _database.GetRecordsAsync(limit: 1000).ConfigureAwait(false);
            consistency.RecordsRead = records.Count;
            consistency.ExpectedInspectionIds = _expectedInspectionIds.Count;

            var duplicateGroups = records
                .Where(record => !string.IsNullOrWhiteSpace(record.InspectionId))
                .GroupBy(record => record.InspectionId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToArray();
            consistency.DuplicateInspectionIds = duplicateGroups.Sum(group => group.Count() - 1);

            HashSet<string> fetchedIds = records
                .Select(record => record.InspectionId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (CycleEvidence cycle in _allCycleEvidence)
            {
                if (!fetchedIds.Contains(cycle.InspectionId) && !cycle.Cancelled)
                {
                    consistency.MissingRecords++;
                    if (consistency.Findings.Count < 20)
                    {
                        consistency.Findings.Add($"Missing DetectionRecord for {cycle.InspectionId}.");
                    }
                }
            }

            foreach (DetectionRecord record in records)
            {
                if (record.CycleSucceeded && (record.TraceStatus == TraceStatus.Queued || record.TraceStatus == TraceStatus.Full))
                {
                    if (string.IsNullOrWhiteSpace(record.ImagePath) || !File.Exists(record.ImagePath))
                    {
                        consistency.MissingImages++;
                        if (consistency.Findings.Count < 20)
                        {
                            consistency.Findings.Add($"Successful record {record.InspectionId} has no persisted original image.");
                        }
                    }
                }
            }

            if (_runtime.ImageSaveQueue.PendingCount < 0 || _runtime.DetectionRecordQueue.PendingCount < 0)
            {
                consistency.InvalidTraceRecords++;
                consistency.Findings.Add("A queue reported a negative pending count.");
            }

            consistency.Status = consistency.DuplicateInspectionIds > 0 ||
                consistency.MissingImages > 0 ||
                consistency.InvalidTraceRecords > 0
                ? "BLOCKED"
                : "PASS";
        }
        catch (Exception ex)
        {
            consistency.Status = "BLOCKED";
            consistency.Findings.Add($"Final consistency scan failed: {ex.Message}");
        }

        _evidence.Cycles.DuplicateInspectionIdCount = consistency.DuplicateInspectionIds;
        _evidence.Cycles.MissingRecordCount = consistency.MissingRecords;
        _evidence.Cycles.MissingTraceCount = consistency.MissingImages;
        _evidence.FinalConsistency = consistency;
    }

    private async Task WaitForQueuesAsync(TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (_runtime.ImageSaveQueue.PendingCount == 0 && _runtime.DetectionRecordQueue.PendingCount == 0)
            {
                return;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
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
