using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
        private readonly IDatabaseService _databaseService;
        private readonly Channel<DetectionPersistencePayload> _channel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private bool _disposed;
        private bool _stopped;

        public DetectionRecordQueue(IDatabaseService databaseService)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _channel = Channel.CreateUnbounded<DetectionPersistencePayload>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _workerTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        }

        public bool Enqueue(DetectionPersistencePayload payload)
        {
            if (_disposed || payload == null)
            {
                return false;
            }

            return _channel.Writer.TryWrite(payload);
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
                        try
                        {
                            await _databaseService
                                .SaveDetectionRecordAsync(payload.ToDetectionRecord())
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DetectionRecordQueue] 数据库写入失败: {ex.Message}");
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
