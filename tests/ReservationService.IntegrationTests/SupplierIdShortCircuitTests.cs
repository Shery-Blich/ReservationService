using System.Net;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class SupplierIdShortCircuitTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;
    private readonly string databasePath;

    public SupplierIdShortCircuitTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
        databasePath = factory.DatabasePath;
    }

    [Fact]
    public async Task BlankSupplierId_Returns400_AndIsNeverThrottled()
    {
        var payload = ValidBodyWithSupplierId("   ");

        for (var i = 0; i < 150; i++)
        {
            var response = await client.PostRawIngestAsync(payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var blankRowCount = await ReservationDbAssertions.CountThrottleCounterRowsForBlankSupplierAsync(databasePath);
        Assert.Equal(0, blankRowCount);
    }

    [Fact]
    public async Task EmptySupplierId_Returns400()
    {
        var payload = ValidBodyWithSupplierId(string.Empty);

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingSupplierIdField_Returns400()
    {
        var payload = $$"""
        {
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": 100.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonStringSupplierId_Returns400()
    {
        var payload = $$"""
        {
          "supplierId": 12345,
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": 100.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnparseableJson_Returns400()
    {
        var payload = "{ this is not valid json ";

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidBodyWithValidSupplierId_StillCountsTowardThrottleWindow()
    {
        var supplierId = TestIdentifiers.GuidLike();
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

        for (var i = 0; i < 100; i++)
        {
            var response = await client.PostRawIngestAsync(invalidPayload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var oneHundredFirstResponse = await client.PostRawIngestAsync(invalidPayload);

        Assert.Equal(HttpStatusCode.TooManyRequests, oneHundredFirstResponse.StatusCode);
    }

    private static string ValidBodyWithSupplierId(string supplierId)
    {
        return $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": 100.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;
    }
}
