using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ReservationService.IntegrationTests.Infrastructure;

public sealed class ThreeInstanceHarness : IAsyncDisposable
{
    private readonly List<Process> processes;

    public IReadOnlyList<HttpClient> Clients { get; }

    public string DatabasePath { get; }

    private ThreeInstanceHarness(List<Process> processes, IReadOnlyList<HttpClient> clients, string databasePath)
    {
        this.processes = processes;
        Clients = clients;
        DatabasePath = databasePath;
    }

    public static async Task<ThreeInstanceHarness> StartAsync(int instanceCount = 3)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"reservation-concurrency-{Guid.NewGuid():N}.db");
        var apiAssemblyPath = ResolveApiAssemblyPath();
        var processes = new List<Process>();
        var clients = new List<HttpClient>();

        try
        {
            for (var i = 0; i < instanceCount; i++)
            {
                var port = GetFreePort();
                var process = StartInstance(apiAssemblyPath, port, databasePath);
                processes.Add(process);
                clients.Add(new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") });
            }

            foreach (var client in clients)
            {
                await WaitUntilReadyAsync(client);
            }
        }
        catch
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }

            KillAll(processes);
            throw;
        }

        return new ThreeInstanceHarness(processes, clients, databasePath);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in Clients)
        {
            client.Dispose();
        }

        KillAll(processes);

        DeleteDatabaseFiles();

        await Task.CompletedTask;
    }

    private static string ResolveApiAssemblyPath()
    {
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var candidate = Path.Combine(repoRoot, "src", "ReservationService.Api", "bin", configuration, "net10.0", "ReservationService.Api.dll");

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"Built API assembly not found at '{candidate}'. Build the solution (dotnet build) before running the 3-instance concurrency tests.",
                candidate);
        }

        return candidate;
    }

    private static string FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ReservationService.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate repository root containing ReservationService.slnx.");
        }

        return directory.FullName;
    }

    private static Process StartInstance(string apiAssemblyPath, int port, string databasePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(apiAssemblyPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(apiAssemblyPath);
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ConnectionStrings__ReservationDb"] = $"Data Source={databasePath}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start API instance process.");

        return process;
    }

    private static async Task WaitUntilReadyAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var probeSupplierId = $"HEALTHCHECK-{Guid.NewGuid():N}";

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await client.GetAsync($"/api/reservations/stats/{probeSupplierId}");
                return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException("API instance did not become ready in time.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void KillAll(IEnumerable<Process> processesToKill)
    {
        foreach (var process in processesToKill)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void DeleteDatabaseFiles()
    {
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
