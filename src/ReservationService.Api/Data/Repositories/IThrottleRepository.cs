using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public interface IThrottleRepository
{
    Task<ThrottleEvaluationResult> IncrementAndEvaluateAsync(
        string supplierId,
        long currentBucketId,
        long previousBucketId,
        double overlapWeight,
        double throttleThreshold,
        CancellationToken cancellationToken = default);
}
