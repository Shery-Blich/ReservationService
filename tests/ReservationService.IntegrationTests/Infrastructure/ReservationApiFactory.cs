using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ReservationService.IntegrationTests.Infrastructure;

public sealed class ReservationApiFactory : WebApplicationFactory<Program>
{
    public ControllableTimeProvider TimeProvider { get; } = new(DateTimeOffset.UtcNow);

    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"reservation-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ReservationDb"] = $"Data Source={DatabasePath}"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(TimeProvider);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        DeleteDatabaseFiles();
    }

    private void DeleteDatabaseFiles()
    {
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var path = DatabasePath + suffix;

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
