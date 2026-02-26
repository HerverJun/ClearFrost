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
        private readonly Channel<(Mat Image, string Path)> _channel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private readonly object _enqueueLock = new object();
        private bool _disposed;

        public ImageSaveQueue(int capacity = 64)
        {
            if (capacity <= 0)
            {
                capacity = 64;
            }

            _channel = Channel.CreateBounded<(Mat Image, string Path)>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            _workerTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 将图像入队。内部会 clone 一份，调用方可立即释放原 Mat。
        /// </summary>
        public bool Enqueue(Mat image, string path)
        {
            if (_disposed || image == null || image.Empty() || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var cloned = image.Clone();

            lock (_enqueueLock)
            {
                if (_disposed)
                {
                    cloned.Dispose();
                    return false;
                }

                if (_channel.Writer.TryWrite((cloned, path)))
                {
                    return true;
                }

                // 队列满时丢弃最旧项，防止慢盘导致内存持续堆积。
                if (_channel.Reader.TryRead(out var dropped))
                {
                    dropped.Image.Dispose();
                }

                if (_channel.Writer.TryWrite((cloned, path)))
                {
                    return true;
                }
            }

            cloned.Dispose();
            return false;
        }

        private async Task ProcessLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var item))
                    {
                        try
                        {
                            string? dir = Path.GetDirectoryName(item.Path);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            Cv2.ImWrite(item.Path, item.Image);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ImageSaveQueue] 图像写入失败: {ex.Message}");
                        }
                        finally
                        {
                            item.Image.Dispose();
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
                while (_channel.Reader.TryRead(out var remaining))
                {
                    remaining.Image.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _channel.Writer.TryComplete();
            _cts.Cancel();

            try
            {
                _workerTask.Wait(1500);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageSaveQueue] 释放等待异常: {ex.Message}");
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}

