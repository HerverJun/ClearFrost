using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClearFrost.Core.Inspection;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using OpenCvSharp;

namespace ClearFrost.Services
{
    internal enum DetectionDropReason
    {
        Busy,
        Debounce,
        Shutdown
    }

    internal readonly record struct DetectionDropSnapshot(
        long BusyCount,
        long DebounceCount,
        long ShutdownCount);

    internal readonly record struct DetectionTriggerDecision(
        bool Accepted,
        DetectionDropReason? DropReason);

    public enum ImageSavePurpose
    {
        General,
        TraceOriginal,
        TraceRendered
    }

    internal sealed class DetectionTriggerGate : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private long _busyDropCount;
        private long _debounceDropCount;
        private long _shutdownDropCount;
        private bool _disposed;

        public async Task<DetectionTriggerDecision> TryEnterAsync(
            bool isShutdownInProgress,
            bool isDebounced = false)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DetectionTriggerGate));
            }

            if (isShutdownInProgress)
            {
                RecordDrop(DetectionDropReason.Shutdown);
                return new DetectionTriggerDecision(false, DetectionDropReason.Shutdown);
            }

            if (isDebounced)
            {
                RecordDrop(DetectionDropReason.Debounce);
                return new DetectionTriggerDecision(false, DetectionDropReason.Debounce);
            }

            if (!await _semaphore.WaitAsync(0).ConfigureAwait(false))
            {
                RecordDrop(DetectionDropReason.Busy);
                return new DetectionTriggerDecision(false, DetectionDropReason.Busy);
            }

            return new DetectionTriggerDecision(true, null);
        }

        public void Release()
        {
            if (_disposed)
            {
                return;
            }

            _semaphore.Release();
        }

        public DetectionDropSnapshot GetSnapshot()
        {
            return new DetectionDropSnapshot(
                Interlocked.Read(ref _busyDropCount),
                Interlocked.Read(ref _debounceDropCount),
                Interlocked.Read(ref _shutdownDropCount));
        }

        private void RecordDrop(DetectionDropReason reason)
        {
            switch (reason)
            {
                case DetectionDropReason.Busy:
                    Interlocked.Increment(ref _busyDropCount);
                    break;
                case DetectionDropReason.Debounce:
                    Interlocked.Increment(ref _debounceDropCount);
                    break;
                case DetectionDropReason.Shutdown:
                    Interlocked.Increment(ref _shutdownDropCount);
                    break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _semaphore.Dispose();
        }
    }

    internal sealed class DetectionPersistencePayload
    {
        public DateTime Timestamp { get; init; }
        public bool IsQualified { get; init; }
        public string InspectionId { get; set; } = string.Empty;
        public string TriggerSource { get; init; } = string.Empty;
        public int? TriggerSeq { get; init; }
        public int? PlcTriggerSeq { get; init; }
        public int? ResultSeq { get; init; }
        public bool TerminalHandshakeAttempted { get; set; }
        public bool TerminalHandshakeSucceeded { get; set; }
        public string TerminalHandshakeErrorCode { get; set; } = string.Empty;
        public string TerminalHandshakeSignalName { get; set; } = string.Empty;
        public string TerminalHandshakeAddress { get; set; } = string.Empty;
        public string TerminalHandshakeMessage { get; set; } = string.Empty;
        public bool CycleSucceeded { get; set; }
        public string ProductBarcode { get; init; } = string.Empty;
        public string Barcode { get; init; } = string.Empty;
        public bool? BarcodeReadSucceeded { get; init; }
        public string BarcodeError { get; init; } = string.Empty;
        public TraceStatus TraceStatus { get; set; } = TraceStatus.Unknown;
        public string QueueStatus { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public string RenderedImagePath { get; set; } = string.Empty;
        public string TraceImagePath { get; set; } = string.Empty;
        public string ErrorStage { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public long TotalMs { get; set; }
        public long CaptureMs { get; init; }
        public long RoiMs { get; init; }
        public long PlcWriteMs { get; init; }
        public long SaveImageMs { get; set; }
        public long SaveRecordMs { get; set; }
        public string RecipeId { get; init; } = string.Empty;
        public string RecipeVersion { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string ModelVersion { get; init; } = string.Empty;
        public string ModelHash { get; init; } = string.Empty;
        public bool WasFallback { get; init; }
        public string UsedModelName { get; init; } = string.Empty;
        public string TargetLabel { get; init; } = string.Empty;
        public int ExpectedCount { get; init; }
        public int ActualCount { get; init; }
        public int InferenceMs { get; init; }
        public string ModelName { get; init; } = string.Empty;
        public string CameraId { get; init; } = string.Empty;
        public string RuleSummary { get; init; } = string.Empty;
        public string RuleResultJson { get; init; } = string.Empty;
        public string RuleSetJson { get; init; } = string.Empty;
        public string ResultJson { get; init; } = string.Empty;

        public DetectionRecord ToDetectionRecord()
        {
            return new DetectionRecord
            {
                Timestamp = Timestamp,
                IsQualified = IsQualified,
                InspectionId = InspectionId,
                TriggerSource = TriggerSource,
                TriggerSeq = TriggerSeq,
                PlcTriggerSeq = PlcTriggerSeq,
                ResultSeq = ResultSeq,
                TerminalHandshakeAttempted = TerminalHandshakeAttempted,
                TerminalHandshakeSucceeded = TerminalHandshakeSucceeded,
                TerminalHandshakeErrorCode = TerminalHandshakeErrorCode,
                TerminalHandshakeSignalName = TerminalHandshakeSignalName,
                TerminalHandshakeAddress = TerminalHandshakeAddress,
                TerminalHandshakeMessage = TerminalHandshakeMessage,
                CycleSucceeded = CycleSucceeded,
                ProductBarcode = ProductBarcode,
                Barcode = Barcode,
                BarcodeReadSucceeded = BarcodeReadSucceeded,
                BarcodeError = BarcodeError,
                TraceStatus = TraceStatus,
                QueueStatus = QueueStatus,
                ImagePath = ImagePath,
                RenderedImagePath = RenderedImagePath,
                TraceImagePath = TraceImagePath,
                ErrorStage = ErrorStage,
                ErrorCode = ErrorCode,
                ErrorMessage = ErrorMessage,
                TotalMs = TotalMs,
                CaptureMs = CaptureMs,
                RoiMs = RoiMs,
                PlcWriteMs = PlcWriteMs,
                SaveImageMs = SaveImageMs,
                SaveRecordMs = SaveRecordMs,
                RecipeId = RecipeId,
                RecipeVersion = RecipeVersion,
                ModelId = ModelId,
                ModelVersion = ModelVersion,
                ModelHash = ModelHash,
                WasFallback = WasFallback,
                UsedModelName = UsedModelName,
                TargetLabel = TargetLabel,
                ExpectedCount = ExpectedCount,
                ActualCount = ActualCount,
                InferenceMs = InferenceMs,
                ModelName = ModelName,
                CameraId = CameraId,
                RuleSummary = RuleSummary,
                RuleResultJson = RuleResultJson,
                RuleSetJson = RuleSetJson,
                ResultJson = ResultJson
            };
        }
    }

    internal sealed class ImageSavePayload : IDisposable
    {
        public ImageSavePayload(
            Mat image,
            string path,
            int? jpegQuality = null,
            ImageSavePurpose purpose = ImageSavePurpose.General)
        {
            Image = image ?? throw new ArgumentNullException(nameof(image));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            JpegQuality = jpegQuality.HasValue ? Math.Clamp(jpegQuality.Value, 1, 100) : null;
            Purpose = purpose;
            EstimatedBytes = EstimateBytes(image);
        }

        public Mat Image { get; }

        public string Path { get; }

        public long EstimatedBytes { get; }

        public int? JpegQuality { get; }

        public ImageSavePurpose Purpose { get; }

        public static ImageSavePayload Create(
            Mat image,
            string path,
            int? jpegQuality = null,
            ImageSavePurpose purpose = ImageSavePurpose.General)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            return new ImageSavePayload(image.Clone(), path, jpegQuality, purpose);
        }

        public static ImageSavePayload CreateReadOnlyView(
            Mat image,
            string path,
            int? jpegQuality = null,
            ImageSavePurpose purpose = ImageSavePurpose.General)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (image.Empty()) throw new ArgumentException("图像为空", nameof(image));

            // Mat 视图持有同一像素缓冲的引用计数，避免保存队列再做整图深拷贝。
            Mat ownedView = image.SubMat(new Rect(0, 0, image.Width, image.Height));
            return new ImageSavePayload(ownedView, path, jpegQuality, purpose);
        }

        public void Dispose()
        {
            Image.Dispose();
        }

        private static long EstimateBytes(Mat image)
        {
            if (image == null || image.Empty())
            {
                return 0;
            }

            try
            {
                long strideBytes = image.Step();
                if (strideBytes > 0 && image.Rows > 0)
                {
                    return checked(strideBytes * image.Rows);
                }
            }
            catch
            {
            }

            try
            {
                return checked(image.Total() * image.ElemSize());
            }
            catch
            {
                return 0;
            }
        }
    }

    internal sealed class DetectionRecordQueue : IDisposable
    {
        private const int DefaultCapacity = 4096;

        private readonly IDatabaseService _databaseService;
        private readonly Channel<DetectionPersistencePayload> _channel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private readonly int _capacity;
        private long _pendingCount;
        private long _droppedCount;
        private long _savedCount;
        private long _failedCount;
        private bool _disposed;
        private bool _stopped;

        public DetectionRecordQueue(IDatabaseService databaseService, int capacity = DefaultCapacity)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));

            _capacity = capacity > 0 ? capacity : DefaultCapacity;
            _channel = Channel.CreateBounded<DetectionPersistencePayload>(new BoundedChannelOptions(_capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _workerTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        }

        public int Capacity => _capacity;

        public long PendingCount => Interlocked.Read(ref _pendingCount);

        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        public long SavedCount => Interlocked.Read(ref _savedCount);

        public long FailedCount => Interlocked.Read(ref _failedCount);

        public bool Enqueue(DetectionPersistencePayload payload)
        {
            if (_disposed || _stopped || payload == null)
            {
                return false;
            }

            Interlocked.Increment(ref _pendingCount);
            if (_channel.Writer.TryWrite(payload))
            {
                long pending = PendingCount;
                if (pending >= _capacity * 3L / 4L)
                {
                    Debug.WriteLine($"[DetectionRecordQueue] 数据库记录队列堆积: {pending}/{_capacity}");
                }

                return true;
            }

            Interlocked.Decrement(ref _pendingCount);
            long dropped = Interlocked.Increment(ref _droppedCount);
            DetectionTraceOutbox.Append(payload, "DetectionRecordQueueFull");
            Debug.WriteLine($"[DetectionRecordQueue] 数据库记录队列已满，丢弃新记录。Dropped={dropped}, Pending={PendingCount}/{_capacity}");
            return false;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_stopped)
            {
                await _workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _stopped = true;
            _channel.Writer.TryComplete();
            await _workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task ProcessLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out DetectionPersistencePayload? payload))
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        try
                        {
                            await _databaseService
                                .SaveDetectionRecordAsync(payload.ToDetectionRecord())
                                .ConfigureAwait(false);
                            Interlocked.Increment(ref _savedCount);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _failedCount);
                            DetectionTraceOutbox.Append(payload, $"DatabaseSaveFailed: {ex.Message}");
                            Debug.WriteLine($"[DetectionRecordQueue] 数据库写入失败: {ex.Message}");
                            Trace.TraceError($"[DetectionRecordQueue] 数据库写入失败: {ex}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DetectionRecordQueue] 后台保存循环异常: {ex.Message}");
            }
            finally
            {
                while (_channel.Reader.TryRead(out _))
                {
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _channel.Writer.TryComplete();

            try
            {
                if (!_workerTask.Wait(1500))
                {
                    Debug.WriteLine("[DetectionRecordQueue] 释放等待超时，取消后台任务。");
                    _cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DetectionRecordQueue] 释放等待异常: {ex.Message}");
                _cts.Cancel();
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    internal static class DetectionTraceOutbox
    {
        private static readonly object WriteLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static void Append(DetectionPersistencePayload payload, string reason)
        {
            Append(payload, reason, RuntimePaths.DataDirectory);
        }

        internal static void Append(DetectionPersistencePayload payload, string reason, string dataDirectory)
        {
            if (payload == null)
            {
                return;
            }

            try
            {
                string directory = Path.Combine(dataDirectory, "outbox");
                EnsureSafeOutboxDirectory(directory);
                Directory.CreateDirectory(directory);
                EnsureSafeOutboxDirectory(directory);
                string path = Path.Combine(directory, $"detection-trace-{DateTime.Now:yyyyMMdd}.ndjson");
                string json = JsonSerializer.Serialize(new
                {
                    Timestamp = DateTimeOffset.Now,
                    Reason = reason ?? string.Empty,
                    Payload = payload
                });

                lock (WriteLock)
                {
                    EnsureSafeOutboxFile(path);
                    File.AppendAllText(path, json + Environment.NewLine, Utf8NoBom);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DetectionTraceOutbox] 写入追溯 outbox 失败: {ex.Message}");
            }
        }

        private static void EnsureSafeOutboxDirectory(string directory)
        {
            string fullDirectory = Path.GetFullPath(directory);
            if (DirectoryPathHasReparsePoint(fullDirectory))
            {
                throw new IOException($"追溯 outbox 目录包含链接目录，拒绝写入: {fullDirectory}");
            }
        }

        private static void EnsureSafeOutboxFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                EnsureSafeOutboxDirectory(directory);
            }

            var file = new FileInfo(fullPath);
            file.Refresh();
            if (file.Exists && HasReparsePoint(file))
            {
                throw new IOException($"追溯 outbox 文件是链接文件，拒绝写入: {fullPath}");
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
        {
            try
            {
                var current = new DirectoryInfo(Path.GetFullPath(directory));
                while (current != null)
                {
                    current.Refresh();
                    if (current.Exists && HasReparsePoint(current))
                    {
                        return true;
                    }

                    current = current.Parent;
                }

                return false;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[DetectionTraceOutbox] 路径安全检查失败，按不安全处理: {ex.Message}");
                return true;
            }
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
    }
}
