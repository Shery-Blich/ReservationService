using System.Data;
using Dapper;

namespace ReservationService.Api.Data;

internal static class SupplierStatsWriter
{
    private const string UpsertSql = """
        INSERT INTO SupplierStats (SupplierId, IngestedCount, ThrottledCount, InvalidCount)
        VALUES (@SupplierId, @IngestedDelta, @ThrottledDelta, @InvalidDelta)
        ON CONFLICT(SupplierId) DO UPDATE SET
            IngestedCount = IngestedCount + @IngestedDelta,
            ThrottledCount = ThrottledCount + @ThrottledDelta,
            InvalidCount = InvalidCount + @InvalidDelta;
        """;

    public static Task IncrementIngestedAsync(IDbConnection connection, IDbTransaction transaction, string supplierId, CancellationToken cancellationToken = default)
        => IncrementAsync(connection, transaction, supplierId, ingestedDelta: 1, throttledDelta: 0, invalidDelta: 0, cancellationToken);

    public static Task IncrementThrottledAsync(IDbConnection connection, IDbTransaction transaction, string supplierId, CancellationToken cancellationToken = default)
        => IncrementAsync(connection, transaction, supplierId, ingestedDelta: 0, throttledDelta: 1, invalidDelta: 0, cancellationToken);

    public static Task IncrementInvalidAsync(IDbConnection connection, IDbTransaction transaction, string supplierId, CancellationToken cancellationToken = default)
        => IncrementAsync(connection, transaction, supplierId, ingestedDelta: 0, throttledDelta: 0, invalidDelta: 1, cancellationToken);

    private static Task IncrementAsync(IDbConnection connection, IDbTransaction transaction, string supplierId, int ingestedDelta, int throttledDelta, int invalidDelta, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            SupplierId = supplierId,
            IngestedDelta = ingestedDelta,
            ThrottledDelta = throttledDelta,
            InvalidDelta = invalidDelta
        };
        var command = new CommandDefinition(UpsertSql, parameters, transaction, cancellationToken: cancellationToken);

        return connection.ExecuteAsync(command);
    }
}
