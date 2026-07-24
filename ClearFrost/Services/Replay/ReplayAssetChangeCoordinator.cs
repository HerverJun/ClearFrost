// ============================================================================
// File: ReplayAssetChangeCoordinator.cs
// Description: Serializes Replay Evidence/Dataset asset mutations
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClearFrost.Services.Replay
{
    internal sealed class ReplayAssetChangeCoordinator : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public async Task<T> RunAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            ThrowIfDisposed();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gate.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReplayAssetChangeCoordinator));
            }
        }
    }
}
