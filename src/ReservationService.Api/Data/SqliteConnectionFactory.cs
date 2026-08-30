using Microsoft.Data.Sqlite;

namespace ReservationService.Api.Data;

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 5000;

    private readonly string connectionString;

    public SqliteConnectionFactory(IConfiguration configuration)
    {
        connectionString = configuration.GetConnectionString("ReservationDb")
            ?? throw new InvalidOperationException("Missing required configuration value 'ConnectionStrings:ReservationDb'.");
    }

    public SqliteConnection CreateOpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyPragmas(connection);

        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }
}
