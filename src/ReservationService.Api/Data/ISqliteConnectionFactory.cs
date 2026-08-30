using Microsoft.Data.Sqlite;

namespace ReservationService.Api.Data;

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateOpenConnection();
}
