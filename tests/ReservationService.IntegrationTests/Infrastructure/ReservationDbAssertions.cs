using Dapper;
using Microsoft.Data.Sqlite;

namespace ReservationService.IntegrationTests.Infrastructure;

public static class ReservationDbAssertions
{
    public static async Task<ReservationRow?> FindReservationAsync(string databasePath, string supplierId, string reservationId)
    {
        await using var connection = await OpenConnectionAsync(databasePath);

        return await connection.QuerySingleOrDefaultAsync<ReservationRow>(
            "SELECT SupplierId, ReservationId, RoomId, CheckIn, CheckOut, Price, UpdatedAtUtc FROM Reservations WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId",
            new { SupplierId = supplierId, ReservationId = reservationId });
    }

    public static async Task<int> CountReservationsAsync(string databasePath, string supplierId, string reservationId)
    {
        await using var connection = await OpenConnectionAsync(databasePath);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Reservations WHERE SupplierId = @SupplierId AND ReservationId = @ReservationId",
            new { SupplierId = supplierId, ReservationId = reservationId });
    }

    public static async Task<SupplierStatsRow?> FindSupplierStatsAsync(string databasePath, string supplierId)
    {
        await using var connection = await OpenConnectionAsync(databasePath);

        return await connection.QuerySingleOrDefaultAsync<SupplierStatsRow>(
            "SELECT SupplierId, IngestedCount, ThrottledCount, InvalidCount FROM SupplierStats WHERE SupplierId = @SupplierId",
            new { SupplierId = supplierId });
    }

    public static async Task<int> CountThrottleCounterRowsForBlankSupplierAsync(string databasePath)
    {
        await using var connection = await OpenConnectionAsync(databasePath);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ThrottleWindowCounters WHERE SupplierId = ''");
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await connection.ExecuteAsync("PRAGMA busy_timeout=5000;");
        return connection;
    }
}
