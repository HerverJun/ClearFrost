// ============================================================================
// 文件名: AsyncDiagnosticLogger.cs
// 描述:   轻量级异步诊断日志追加器
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ClearFrost.Services
{
    internal sealed class AsyncDiagnosticLogger : IDisposable, IAsyncDisposable
    {
        private const int DefaultCapacity = 8192;
        private const int MaxBatchSize = 128;
        private static readonly TimeSpan FlushFailureBackoff = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(2);

        private readonly string _path;
        private readonly Channel<string> _channel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private long _droppedCount;
        private long _failedCount;
        private int _stopping;

        public AsyncDiagnosticLogger(string path, int capacity = DefaultCapacity)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("日志路径不能为空", nameof(path));
            }

            _path = path;
            int boundedCapacity = capacity > 0 ? capacity : DefaultCapacity;
            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(boundedCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _workerTask = Task.Run(ProcessLoopAsync);
        }

        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        public long FailedCount => Interlocked.Read(ref _failedCount);

        public bool Enqueue(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return true;
            }

            if (Volatile.Read(ref _stopping) != 0)
            {
                return false;
            }

            if (_channel.Writer.TryWrite(line))
            {
                return true;
            }

            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        private async Task ProcessLoopAsync()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await FlushAvailableAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _failedCount);
                        Debug.WriteLine($"[AsyncDiagnosticLogger] 写入失败，将继续接收后续日志: {ex.Message}");
                        await Task.Delay(FlushFailureBackoff, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AsyncDiagnosticLogger] 写入循环异常: {ex.Message}");
            }
            finally
            {
                try
                {
                    while (await FlushAvailableAsync(CancellationToken.None).ConfigureAwait(false) > 0)
                    {
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedCount);
                    Debug.WriteLine($"[AsyncDiagnosticLogger] 退出前刷新失败: {ex.Message}");
                }
            }
        }

        private async Task<int> FlushAvailableAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder(4096);
            int count = 0;

            while (count < MaxBatchSize && _channel.Reader.TryRead(out string? line))
            {
                builder.AppendLine(line);
                count++;
            }

            if (count == 0)
            {
                return 0;
            }

            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(
                _path,
                builder.ToString(),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            return count;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }

            _channel.Writer.TryComplete();

            try
            {
                await _workerTask.WaitAsync(DisposeFlushTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _cts.Cancel();
                try
                {
                    await _workerTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AsyncDiagnosticLogger] 释放失败: {ex.Message}");
            }
            finally
            {
                _cts.Dispose();
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
