using System.Net;
using System.Net.Http.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class IdentifierShapeTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;
    private readonly string databasePath;

    public IdentifierShapeTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
        databasePath = factory.DatabasePath;
    }

    public static IEnumerable<object[]> IdentifierShapes()
    {
        yield return [(Func<string>)TestIdentifiers.GuidLike];
        yield return [(Func<string>)TestIdentifiers.IntegerLike];
        yield return [(Func<string>)TestIdentifiers.HashLike];
        yield return [(Func<string>)TestIdentifiers.MixedAlphanumeric];
    }

    [Theory]
    [MemberData(nameof(IdentifierShapes))]
    public async Task CreateDuplicateAndStats_BehaveIdenticallyRegardlessOfIdShape(Func<string> shapeFactory)
    {
        var supplierId = shapeFactory();
        var reservationId = shapeFactory();
        var request = IngestRequestFactory.CreateDefault(supplierId, reservationId) with { RoomId = shapeFactory() };

        var createResponse = await client.PostIngestAsync(request);
        var duplicateResponse = await client.PostIngestAsync(request);
        var statsResponse = await client.GetStatsAsync(supplierId);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var duplicateBody = await duplicateResponse.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions.Default);
        Assert.Equal("duplicate", duplicateBody!.Status);
        var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        Assert.Equal(2, stats!.Ingested);
        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.NotNull(row);
        Assert.Equal(request.RoomId.ToUpperInvariant(), row!.RoomId);
    }
}
