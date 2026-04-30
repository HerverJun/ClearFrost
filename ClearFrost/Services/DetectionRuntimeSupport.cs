using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Inspection;
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

    internal static class PlcBarcodeDetectionGate
    {
        public static bool ShouldBlockDetection(
            AppConfig config,
            InspectionContext context,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = string.Empty;
            errorMessage = string.Empty;

            if (config == null || context == null)
            {
                return false;
            }

            bool isPlcTrigger = string.Equals(context.TriggerSource, "PLC", StringComparison.OrdinalIgnoreCase);
            if (!config.EnablePlcBarcodeReading || !config.PlcBarcodeRequired || !isPlcTrigger)
            {
                return false;
            }

            if (!context.BarcodeReadSucceeded)
            {
                errorCode = "BarcodeReadFailed";
                errorMessage = string.IsNullOrWhiteSpace(context.BarcodeError)
                    ? "PLC 条码读取失败"
                    : $"PLC 条码读取失败: {context.BarcodeError}";
                return true;
            }

            if (string.IsNullOrWhiteSpace(context.ProductBarcode) ||
                string.Equals(context.ProductBarcode.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "NoBarcode";
                errorMessage = "PLC 条码为空";
                return true;
            }

            return false;
        }
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
        public string ProductBarcode { get; init; } = string.Empty;
        public int? ResultSeq { get; init; }
        public TraceStatus TraceStatus { get; set; } = TraceStatus.Unknown;
        public string ImagePath { get; set; } = string.Empty;
        public string RenderedImagePath { get; set; } = string.Empty;
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
                ProductBarcode = ProductBarcode,
                ResultSeq = ResultSeq,
                TraceStatus = TraceStatus,
                ImagePath = ImagePath,
                RenderedImagePath = RenderedImagePath,
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
                ResultJson = ResultJson
            };
        }
    }

    internal sealed class ImageSavePayload : IDisposable
    {
        public ImageSavePayload(Mat image, string path)
        {
            Image = image ?? throw new ArgumentNullException(nameof(image));
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public Mat Image { get; }

        public string Path { get; }

        public static ImageSavePayload Create(Mat image, string path)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            return new ImageSavePayload(image.Clone(), path);
        }

        public void Dispose()
        {
            Image.Dispose();
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
}
