using System.Collections.Concurrent;

namespace ReservationService.Api.Features.Ingest;

public sealed class ThrottleLatch : IThrottleLatch
{
    private readonly ConcurrentDictionary<string, long> blockedBucketBySupplierId = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> gatesBySupplierId = new();

    public bool IsBlocked(string supplierId, long bucketId)
        => blockedBucketBySupplierId.TryGetValue(supplierId, out var latchedBucketId) && latchedBucketId == bucketId;

    public void MarkBlocked(string supplierId, long bucketId)
        => blockedBucketBySupplierId[supplierId] = bucketId;

    public async Task<IAsyncDisposable> AcquireGateAsync(string supplierId)
    {
        var gate = gatesBySupplierId.GetOrAdd(supplierId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync();

        return new SemaphoreGateRelease(gate);
    }

    private sealed class SemaphoreGateRelease : IAsyncDisposable
    {
        private readonly SemaphoreSlim semaphore;

        private int released;

        public SemaphoreGateRelease(SemaphoreSlim semaphore)
        {
            this.semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
