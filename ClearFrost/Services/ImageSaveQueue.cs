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
        private readonly Channel<ImageSavePayload> _channel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private readonly object _enqueueLock = new object();
        private readonly int _capacity;
        private long _pendingCount;
        private long _droppedCount;
        private long _savedCount;
        private long _failedCount;
        private bool _disposed;
        private bool _stopped;

        public ImageSaveQueue(int capacity = 64)
        {
            if (capacity <= 0)
            {
                capacity = 64;
            }

            _capacity = capacity;
            _channel = Channel.CreateBounded<ImageSavePayload>(new BoundedChannelOptions(capacity)
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

                Interlocked.Increment(ref _pendingCount);
                if (_channel.Writer.TryWrite(payload))
                {
                    return true;
                }
                Interlocked.Decrement(ref _pendingCount);

                // 队列满时丢弃最旧项，防止慢盘导致内存持续堆积。
                if (_channel.Reader.TryRead(out ImageSavePayload? dropped))
                {
                    dropped.Dispose();
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Increment(ref _droppedCount);
                }

                Interlocked.Increment(ref _pendingCount);
                if (_channel.Writer.TryWrite(payload))
                {
                    return true;
                }
                Interlocked.Decrement(ref _pendingCount);
            }

            Interlocked.Increment(ref _droppedCount);
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
                    while (_channel.Reader.TryRead(out ImageSavePayload? item))
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        try
                        {
                            string? dir = Path.GetDirectoryName(item.Path);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            bool written = Cv2.ImWrite(item.Path, item.Image, BuildEncodingParams(item));
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
