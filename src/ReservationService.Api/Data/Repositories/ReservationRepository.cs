using System.Data;
using System.Globalization;
using Dapper;
using ReservationService.Api.Data.Models;

namespace ReservationService.Api.Data.Repositories;

public sealed class ReservationRepository : IReservationRepository
{
    private const string InsertSql = """
        INSERT OR IGNORE INTO Reservations (SupplierId, ReservationId, RoomId, CheckIn, CheckOut, Price, UpdatedAtUtc)
        VALUES (@SupplierId, @ReservationId, @RoomId, @CheckIn, @CheckOut, @Price, @UpdatedAtUtc);
        """;

    private const string ConditionalUpdateSql = """
        UPDATE Reservations
        SET RoomId = @RoomId, CheckIn = @CheckIn, CheckOut = @CheckOut, Price = @Price, UpdatedAtUtc = @UpdatedAtUtc
        WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId
          AND @UpdatedAtUtc >= UpdatedAtUtc
          AND (RoomId <> @RoomId OR CheckIn <> @CheckIn OR CheckOut <> @CheckOut OR Price <> @Price);
        """;

    private const string DuplicateCheckSql = """
        UPDATE Reservations
        SET RoomId = RoomId
        WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId
          AND RoomId = @RoomId AND CheckIn = @CheckIn AND CheckOut = @CheckOut AND Price = @Price;
        """;

    private readonly ISqliteConnectionFactory connectionFactory;

    public ReservationRepository(ISqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task<ReservationWriteResult> UpsertAsync(ReservationRecord reservation, CancellationToken cancellationToken = default)
    {
        var parameters = ToSqlParameters(reservation);

        using var connection = connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();

        var outcome = await TryInsertAsync(connection, transaction, parameters, cancellationToken)
            ? ReservationWriteOutcome.Created
            : await ApplyConditionalUpdateAsync(connection, transaction, parameters, cancellationToken);

        await SupplierStatsWriter.IncrementIngestedAsync(connection, transaction, reservation.SupplierId, cancellationToken);

        transaction.Commit();

        return new ReservationWriteResult(outcome);
    }

    private static async Task<bool> TryInsertAsync(IDbConnection connection, IDbTransaction transaction, ReservationSqlParameters parameters, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: cancellationToken);
        var rowsAffected = await connection.ExecuteAsync(command);

        return rowsAffected == 1;
    }

    private static async Task<ReservationWriteOutcome> ApplyConditionalUpdateAsync(IDbConnection connection, IDbTransaction transaction, ReservationSqlParameters parameters, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(ConditionalUpdateSql, parameters, transaction, cancellationToken: cancellationToken);
        var rowsAffected = await connection.ExecuteAsync(command);

        if (rowsAffected == 1)
        {
            return ReservationWriteOutcome.Updated;
        }

        return await ClassifyNoOpAsync(connection, transaction, parameters, cancellationToken);
    }

    private static async Task<ReservationWriteOutcome> ClassifyNoOpAsync(IDbConnection connection, IDbTransaction transaction, ReservationSqlParameters parameters, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(DuplicateCheckSql, parameters, transaction, cancellationToken: cancellationToken);
        var rowsAffected = await connection.ExecuteAsync(command);

        return rowsAffected == 1 ? ReservationWriteOutcome.Duplicate : ReservationWriteOutcome.StaleIgnored;
    }

    private static ReservationSqlParameters ToSqlParameters(ReservationRecord reservation)
        => new()
        {
            SupplierId = reservation.SupplierId,
            ReservationId = reservation.ReservationId,
            RoomId = reservation.RoomId,
            CheckIn = SqliteDateTimeFormat.ToStorageValue(reservation.CheckIn),
            CheckOut = SqliteDateTimeFormat.ToStorageValue(reservation.CheckOut),
            Price = reservation.Price.ToString("F2", CultureInfo.InvariantCulture),
            UpdatedAtUtc = SqliteDateTimeFormat.ToStorageValue(reservation.UpdatedAtUtc)
        };

    private sealed record ReservationSqlParameters
    {
        public required string SupplierId { get; init; }

        public required string ReservationId { get; init; }

        public required string RoomId { get; init; }

        public required string CheckIn { get; init; }

        public required string CheckOut { get; init; }

        public required string Price { get; init; }

        public required string UpdatedAtUtc { get; init; }
    }
}
