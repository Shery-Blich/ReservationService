namespace ReservationService.Api.Data;

public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Reservations
        (
            SupplierId TEXT NOT NULL,
            ReservationId TEXT NOT NULL,
            RoomId TEXT NOT NULL,
            CheckIn TEXT NOT NULL,
            CheckOut TEXT NOT NULL,
            Price TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (SupplierId, ReservationId)
        );

        CREATE TABLE IF NOT EXISTS ThrottleWindowCounters
        (
            SupplierId TEXT NOT NULL,
            BucketId INTEGER NOT NULL,
            RequestCount INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (SupplierId, BucketId)
        );

        CREATE TABLE IF NOT EXISTS SupplierStats
        (
            SupplierId TEXT NOT NULL PRIMARY KEY,
            IngestedCount INTEGER NOT NULL DEFAULT 0,
            ThrottledCount INTEGER NOT NULL DEFAULT 0,
            InvalidCount INTEGER NOT NULL DEFAULT 0
        );
        """;

    private readonly ISqliteConnectionFactory connectionFactory;

    public DatabaseInitializer(ISqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
