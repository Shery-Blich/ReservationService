using System.Net;
using System.Net.Http.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class ConcurrencyTests : IClassFixture<ConcurrencyHarnessFixture>
{
    private readonly IReadOnlyList<HttpClient> clients;
    private readonly string databasePath;

    public ConcurrencyTests(ConcurrencyHarnessFixture fixture)
    {
        clients = fixture.Harness.Clients;
        databasePath = fixture.Harness.DatabasePath;
    }

    [Fact]
    public async Task ConcurrentRacingUpdatesAcrossInstances_ProduceNoDuplicateRowAndNoLostUpdate()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var baseline = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(0, 9)
            .Select(i => IngestRequestFactory.Create(
                supplierId,
                reservationId,
                roomId: $"ROOM-{i}",
                checkIn: baseline,
                checkOut: baseline.AddDays(2),
                price: 100m + i,
                updatedAtUtc: baseline.AddMinutes(i)))
            .ToArray();
        var winner = requests[^1];

        await Task.WhenAll(requests.Select((request, index) => clients[index % clients.Count].PostIngestAsync(request)));

        var rowCount = await ReservationDbAssertions.CountReservationsAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.Equal(1, rowCount);
        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.Equal(winner.RoomId.ToUpperInvariant(), row!.RoomId);
        Assert.Equal(winner.Price, row.Price, 2);
    }

    [Fact]
    public async Task AggregatedIngestedCountAcrossInstances_MatchesCombinedActivity()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var requests = Enumerable.Range(0, 30)
            .Select(_ => IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()))
            .ToArray();

        var responses = await Task.WhenAll(requests.Select((request, index) => clients[index % clients.Count].PostIngestAsync(request)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var statsResponse = await clients[0].GetStatsAsync(supplierId);
        var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        Assert.Equal(30, stats!.Ingested);
        Assert.Equal(0, stats.Throttled);
    }

    [Fact]
    public async Task RogueSupplierSplitAcrossInstances_IsCappedAtCombinedLimitNotPerInstance()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var requests = Enumerable.Range(0, 270)
            .Select(_ => IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()))
            .ToArray();
        var requestsPerInstance = 90;

        var responses = await Task.WhenAll(requests.Select((request, index) =>
        {
            var instanceIndex = index / requestsPerInstance;
            return clients[instanceIndex].PostIngestAsync(request);
        }));

        var successCount = responses.Count(response => response.StatusCode == HttpStatusCode.Created);
        var throttledCount = responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(270, successCount + throttledCount);
        Assert.True(successCount <= 100, $"Combined success count {successCount} exceeded the shared 100-request cap.");
        Assert.True(successCount >= 99, $"Combined success count {successCount} was implausibly low for a 270-request burst inside one fresh window.");
        var statsResponse = await clients[0].GetStatsAsync(supplierId);
        var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        Assert.Equal(successCount, stats!.Ingested);
        Assert.InRange(stats.Throttled, 1, 10);
    }
}
