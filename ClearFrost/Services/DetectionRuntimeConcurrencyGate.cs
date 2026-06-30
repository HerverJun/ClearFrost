using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClearFrost.Services
{
    internal static class DetectionRuntimeConcurrencyGate
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser();
        }

        private sealed class Releaser : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    Gate.Release();
                }
            }
        }
    }
}
