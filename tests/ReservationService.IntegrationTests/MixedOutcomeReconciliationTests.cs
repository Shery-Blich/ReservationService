using System.Net;
using System.Net.Http.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class MixedOutcomeReconciliationTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;
    private readonly string databasePath;

    public MixedOutcomeReconciliationTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
        databasePath = factory.DatabasePath;
    }

    [Fact]
    public async Task MixedSequenceOfOutcomes_ReconcilesExactlyAgainstStats()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var created = IngestRequestFactory.CreateDefault(supplierId, reservationId);
        var invalidPayload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": 100.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var createResponse = await client.PostIngestAsync(created);
        var duplicateResponse = await client.PostIngestAsync(created);
        var updated = created with { Price = 150.00m, UpdatedAtUtc = DateTimeOffset.Parse(created.UpdatedAtUtc).AddDays(1).ToString("O") };
        var updateResponse = await client.PostIngestAsync(updated);
        var stale = created with { Price = 999.00m, UpdatedAtUtc = DateTimeOffset.Parse(created.UpdatedAtUtc).AddDays(-1).ToString("O") };
        var staleResponse = await client.PostIngestAsync(stale);
        var secondCreated = IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike());
        var secondCreateResponse = await client.PostIngestAsync(secondCreated);

        var invalidResponses = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
        {
            invalidResponses.Add(await client.PostRawIngestAsync(invalidPayload));
        }

        var fillResponses = new List<HttpResponseMessage>();
        for (var i = 0; i < 95; i++)
        {
            fillResponses.Add(await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike())));
        }

        var overflowResponse = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondCreateResponse.StatusCode);
        Assert.All(invalidResponses, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.All(fillResponses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, overflowResponse.StatusCode);
        var statsResponse = await client.GetStatsAsync(supplierId);
        var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        Assert.Equal(100, stats!.Ingested);
        Assert.Equal(1, stats.Throttled);
    }
}
