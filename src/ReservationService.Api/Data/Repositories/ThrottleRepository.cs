using System.Data;
using Dapper;
using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public sealed class ThrottleRepository : IThrottleRepository
{
    private const string IncrementBucketSql = """
        INSERT INTO ThrottleWindowCounters (SupplierId, BucketId, RequestCount)
        VALUES (@SupplierId, @BucketId, 1)
        ON CONFLICT(SupplierId, BucketId) DO UPDATE SET RequestCount = RequestCount + 1
        RETURNING RequestCount;
        """;

    private const string ReadBucketSql = """
        SELECT RequestCount FROM ThrottleWindowCounters WHERE SupplierId = @SupplierId AND BucketId = @BucketId;
        """;

    private const string PruneOldBucketsSql = """
        DELETE FROM ThrottleWindowCounters WHERE SupplierId = @SupplierId AND BucketId < @PreviousBucketId;
        """;

    public async Task<ThrottleEvaluationResult> IncrementAndEvaluateAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string supplierId,
        long currentBucketId,
        long previousBucketId,
        double overlapWeight,
        double throttleThreshold,
        CancellationToken cancellationToken = default)
    {
        var currentBucketCount = await IncrementBucketAsync(connection, transaction, supplierId, currentBucketId, cancellationToken);
        var previousBucketCount = await ReadBucketCountAsync(connection, transaction, supplierId, previousBucketId, cancellationToken);
        var estimatedCount = currentBucketCount + (previousBucketCount * overlapWeight);
        var isThrottled = estimatedCount > throttleThreshold;

        if (isThrottled)
        {
            await SupplierStatsWriter.IncrementThrottledAsync(connection, transaction, supplierId, cancellationToken);
        }

        await PruneOldBucketsAsync(connection, transaction, supplierId, previousBucketId, cancellationToken);

        return new ThrottleEvaluationResult(currentBucketCount, previousBucketCount, estimatedCount, isThrottled);
    }

    private static Task<int> IncrementBucketAsync(IDbConnection connection, IDbTransaction transaction, string supplierId, long bucketId, CancellationToken cancellationToken)
    {
        var parameters = new { SupplierId = supplierId, BucketId = bucketId };
        var command = new CommandDefinition(IncrementBucketSql, parameters, transaction, cancellationToken: cancellationToken);

        return connection.ExecuteScalarAsync<int>(command);
    }

    private static async Task<int> ReadBucketCountAsync(IDbConnection connection, IDbTransaction transaction, string supplierId, long bucketId, CancellationToken cancellationToken)
    {
        var parameters = new { SupplierId = supplierId, BucketId = bucketId };
        var command = new CommandDefinition(ReadBucketSql, parameters, transaction, cancellationToken: cancellationToken);
        var count = await connection.ExecuteScalarAsync<int?>(command);

        return count ?? 0;
    }

    private static Task<int> PruneOldBucketsAsync(IDbConnection connection, IDbTransaction transaction, string supplierId, long previousBucketId, CancellationToken cancellationToken)
    {
        var parameters = new { SupplierId = supplierId, PreviousBucketId = previousBucketId };
        var command = new CommandDefinition(PruneOldBucketsSql, parameters, transaction, cancellationToken: cancellationToken);

        return connection.ExecuteAsync(command);
    }
}
