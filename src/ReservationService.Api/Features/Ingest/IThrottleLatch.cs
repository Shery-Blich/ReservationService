namespace ReservationService.Api.Features.Ingest;

public interface IThrottleLatch
{
    bool IsBlocked(string supplierId, long bucketId);

    void MarkBlocked(string supplierId, long bucketId);

    Task<IAsyncDisposable> AcquireGateAsync(string supplierId);
}
