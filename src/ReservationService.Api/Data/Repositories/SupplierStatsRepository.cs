using Dapper;
using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public sealed class SupplierStatsRepository : ISupplierStatsRepository
{
    private const string SelectSql = """
        SELECT SupplierId, IngestedCount, ThrottledCount, InvalidCount
        FROM SupplierStats
        WHERE SupplierId = @SupplierId;
        """;

    private readonly ISqliteConnectionFactory connectionFactory;

    public SupplierStatsRepository(ISqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task<SupplierStatsRecord?> GetAsync(string supplierId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateOpenConnection();
        var command = new CommandDefinition(SelectSql, new { SupplierId = supplierId }, cancellationToken: cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<SupplierStatsRecord>(command);
    }

    public async Task IncrementInvalidAsync(string supplierId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();

        await SupplierStatsWriter.IncrementInvalidAsync(connection, transaction, supplierId, cancellationToken);

        transaction.Commit();
    }
}
