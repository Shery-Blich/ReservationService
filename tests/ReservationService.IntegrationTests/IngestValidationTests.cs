using System.Net;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class IngestValidationTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;
    private readonly string databasePath;

    public IngestValidationTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
        databasePath = factory.DatabasePath;
    }

    [Fact]
    public async Task MissingRoomId_Returns400()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var payload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": 100.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MissingUpdatedAtUtc_Returns400()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var payload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": 100.00
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CheckInEqualToCheckOut_Returns400()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var sameInstant = "2026-01-10T00:00:00Z";
        var payload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "{{sameInstant}}",
          "checkOut": "{{sameInstant}}",
          "price": 100.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CheckInAfterCheckOut_Returns400()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var payload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-11T00:00:00Z",
          "checkOut": "2026-01-10T00:00:00Z",
          "price": 100.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NegativePrice_Returns400()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var payload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": -1.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ZeroPrice_IsValid_ReturnsCreated()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var request = IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()) with { Price = 0m };

        var response = await client.PostIngestAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task NonNumericPrice_Returns400()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var payload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": "not-a-number",
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        var response = await client.PostRawIngestAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidRequests_DoNotAffectIngestedOrThrottledCount()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var payload = $$"""
        {
          "supplierId": "{{supplierId}}",
          "reservationId": "{{TestIdentifiers.GuidLike()}}",
          "roomId": "{{TestIdentifiers.GuidLike()}}",
          "checkIn": "2026-01-10T00:00:00Z",
          "checkOut": "2026-01-11T00:00:00Z",
          "price": -5.00,
          "updatedAtUtc": "2026-01-10T00:00:00Z"
        }
        """;

        await client.PostRawIngestAsync(payload);
        await client.PostRawIngestAsync(payload);

        var stats = await ReservationDbAssertions.FindSupplierStatsAsync(databasePath, supplierId.ToUpperInvariant());
        Assert.Null(stats);
    }
}
