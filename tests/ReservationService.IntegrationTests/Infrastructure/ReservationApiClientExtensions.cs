using System.Net.Http.Json;
using System.Text;
using ReservationService.IntegrationTests.Contracts;

namespace ReservationService.IntegrationTests.Infrastructure;

public static class ReservationApiClientExtensions
{
    public static Task<HttpResponseMessage> PostIngestAsync(this HttpClient client, IngestRequest request)
    {
        return client.PostAsJsonAsync("/api/reservations/ingest", request, JsonOptions.Default);
    }

    public static Task<HttpResponseMessage> PostRawIngestAsync(this HttpClient client, string rawJson)
    {
        return client.PostAsync("/api/reservations/ingest", new StringContent(rawJson, Encoding.UTF8, "application/json"));
    }

    public static Task<HttpResponseMessage> GetStatsAsync(this HttpClient client, string supplierId)
    {
        return client.GetAsync($"/api/reservations/stats/{Uri.EscapeDataString(supplierId)}");
    }
}
