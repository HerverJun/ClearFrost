// ============================================================================
// 文件名: ImageSaveQueue.cs
// 描述:   图像异步保存队列（有界队列，满时丢弃最旧项）
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenCvSharp;

namespace ClearFrost.Services
{
    /// <summary>
    /// 后台图像保存队列。入队仅做轻量操作，实际文件 IO 在后台线程执行。
    /// </summary>
    public sealed class ImageSaveQueue : IDisposable
    {
        private const long DefaultMaxBufferedBytes = 256L * 1024L * 1024L;

        private readonly Channel<ImageSavePayload> _channel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private readonly Func<ImageSavePayload, bool> _imageWriter;
        private readonly object _enqueueLock = new object();
        private readonly int _capacity;
        private readonly long _maxBufferedBytes;
        private long _pendingCount;
        private long _pendingBytes;
        private long _droppedCount;
        private long _droppedBytes;
        private long _savedCount;
        private long _failedCount;
        private bool _disposed;
        private bool _stopped;

        public ImageSaveQueue(int capacity = 64, long maxBufferedBytes = DefaultMaxBufferedBytes)
            : this(capacity, maxBufferedBytes, WriteImageWithOpenCv)
        {
        }

        internal ImageSaveQueue(
            int capacity,
            long maxBufferedBytes,
            Func<ImageSavePayload, bool>? imageWriter)
        {
            if (capacity <= 0)
            {
                capacity = 64;
            }

            _capacity = capacity;
            _maxBufferedBytes = maxBufferedBytes > 0 ? maxBufferedBytes : DefaultMaxBufferedBytes;
            _imageWriter = imageWriter ?? WriteImageWithOpenCv;
            _channel = Channel.CreateBounded<ImageSavePayload>(new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            _workerTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        }

        public int Capacity => _capacity;

        public long MaxBufferedBytes => _maxBufferedBytes;

        public long PendingCount => Interlocked.Read(ref _pendingCount);

        public long PendingBytes => Interlocked.Read(ref _pendingBytes);

        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

        public long SavedCount => Interlocked.Read(ref _savedCount);

        public long FailedCount => Interlocked.Read(ref _failedCount);

        /// <summary>
        /// 将图像入队。内部会 clone 一份，调用方可立即释放原 Mat。
        /// </summary>
        public bool Enqueue(
            Mat image,
            string path,
            int? jpegQuality = null,
            ImageSavePurpose purpose = ImageSavePurpose.General)
        {
            if (_disposed || image == null || image.Empty() || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            ImageSavePayload payload = ImageSavePayload.Create(image, path, jpegQuality, purpose);
            if (Enqueue(payload))
            {
                return true;
            }

            payload.Dispose();
            return false;
        }

        internal bool Enqueue(ImageSavePayload payload)
        {
            if (_disposed || payload == null || payload.Image.Empty() || string.IsNullOrWhiteSpace(payload.Path))
            {
                return false;
            }

            lock (_enqueueLock)
            {
                if (_disposed)
                {
                    return false;
                }

                long payloadBytes = payload.EstimatedBytes;
                DropOldestUntilRoomFor(payloadBytes);

                AddPending(payload);
                if (_channel.Writer.TryWrite(payload))
                {
                    return true;
                }

                RemovePending(payload);
                DropOldestPayload();

                AddPending(payload);
                if (_channel.Writer.TryWrite(payload))
                {
                    return true;
                }

                RemovePending(payload);
            }

            RecordRejectedPayload(payload);
            return false;
        }

        private void DropOldestUntilRoomFor(long payloadBytes)
        {
            while (PendingCount >= _capacity ||
                   (PendingCount > 0 && PendingBytes + payloadBytes > _maxBufferedBytes))
            {
                if (!DropOldestPayload())
                {
                    break;
                }
            }
        }

        private bool DropOldestPayload()
        {
            if (!_channel.Reader.TryRead(out ImageSavePayload? dropped))
            {
                return false;
            }

            RemovePending(dropped);
            RecordRejectedPayload(dropped);
            dropped.Dispose();
            return true;
        }

        private void AddPending(ImageSavePayload payload)
        {
            Interlocked.Increment(ref _pendingCount);
            Interlocked.Add(ref _pendingBytes, payload.EstimatedBytes);
        }

        private void RemovePending(ImageSavePayload payload)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Add(ref _pendingBytes, -payload.EstimatedBytes);
        }

        private void RecordRejectedPayload(ImageSavePayload payload)
        {
            Interlocked.Increment(ref _droppedCount);
            Interlocked.Add(ref _droppedBytes, payload.EstimatedBytes);
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
                    while (_channel.Reader.TryRead(out ImageSavePayload? item))
                    {
                        RemovePending(item);
                        try
                        {
                            string fullPath = Path.GetFullPath(item.Path);
                            string? dir = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrWhiteSpace(dir))
                            {
                                EnsureImageTargetSafe(fullPath, dir);
                                Directory.CreateDirectory(dir);
                                EnsureImageTargetSafe(fullPath, dir);
                            }

                            bool written = _imageWriter(item);
                            if (!written)
                            {
                                throw new IOException($"OpenCV returned false for {item.Path}");
                            }

                            Interlocked.Increment(ref _savedCount);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _failedCount);
                            Debug.WriteLine($"[ImageSaveQueue] 图像写入失败: {ex.Message}");
                        }
                        finally
                        {
                            item.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageSaveQueue] 后台保存循环异常: {ex.Message}");
            }
            finally
            {
                while (_channel.Reader.TryRead(out ImageSavePayload? remaining))
                {
                    remaining.Dispose();
                    RemovePending(remaining);
                }
            }
        }

        private static bool WriteImageWithOpenCv(ImageSavePayload item)
        {
            return Cv2.ImWrite(item.Path, item.Image, BuildEncodingParams(item));
        }

        private static void EnsureImageTargetSafe(string fullPath, string directory)
        {
            if (DirectoryPathHasReparsePoint(directory))
            {
                throw new IOException($"图像保存目录包含链接目录，拒绝写入: {directory}");
            }

            var target = new FileInfo(fullPath);
            target.Refresh();
            if (target.Exists && HasReparsePoint(target))
            {
                throw new IOException($"图像保存目标是链接文件，拒绝写入: {fullPath}");
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _channel.Writer.TryComplete();

            try
            {
                if (!_workerTask.Wait(1500))
                {
                    Debug.WriteLine("[ImageSaveQueue] 释放等待超时，取消后台任务。");
                    _cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageSaveQueue] 释放等待异常: {ex.Message}");
                _cts.Cancel();
            }
            finally
            {
                _cts.Dispose();
            }
        }

        internal static ImageEncodingParam[] BuildEncodingParams(ImageSavePayload payload)
        {
            if (payload.JpegQuality.HasValue && IsJpegPath(payload.Path))
            {
                return new[]
                {
                    new ImageEncodingParam(ImwriteFlags.JpegQuality, payload.JpegQuality.Value)
                };
            }

            return Array.Empty<ImageEncodingParam>();
        }

        private static bool IsJpegPath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }
    }
}
