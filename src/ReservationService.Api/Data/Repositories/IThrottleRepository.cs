using System.Data;
using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public interface IThrottleRepository
{
    Task<ThrottleEvaluationResult> IncrementAndEvaluateAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string supplierId,
        long currentBucketId,
        long previousBucketId,
        double overlapWeight,
        double throttleThreshold,
        CancellationToken cancellationToken = default);
}
