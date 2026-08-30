using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class StatsEndpointTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;

    public StatsEndpointTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task UnknownSupplierId_Returns404WithProblemDetails()
    {
        var supplierId = TestIdentifiers.GuidLike();

        var response = await client.GetStatsAsync(supplierId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions.Default);
        Assert.Equal(404, problem!.Status);
    }

    [Fact]
    public async Task BlankSupplierIdRouteParam_Returns400()
    {
        var response = await client.GetStatsAsync(" ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SupplierWithOnlyInvalidRequests_Returns404()
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
        await client.PostRawIngestAsync(invalidPayload);

        var response = await client.GetStatsAsync(supplierId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions.Default);
        Assert.Equal(404, problem!.Status);
    }

    [Fact]
    public async Task ResponseBody_ContainsOnlySupplierIdIngestedThrottled()
    {
        var supplierId = TestIdentifiers.GuidLike();
        await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));

        var response = await client.GetStatsAsync(supplierId);

        var document = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var propertyNames = document!.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "ingested", "supplierId", "throttled" }, propertyNames);
    }

    [Fact]
    public async Task CasingAndWhitespaceVariants_ReturnSameStats()
    {
        var canonicalSupplierId = TestIdentifiers.GuidLike();
        await client.PostIngestAsync(IngestRequestFactory.CreateDefault(canonicalSupplierId, TestIdentifiers.GuidLike()));
        await client.PostIngestAsync(IngestRequestFactory.CreateDefault(canonicalSupplierId, TestIdentifiers.GuidLike()));

        var lowerCaseResponse = await client.GetStatsAsync(canonicalSupplierId.ToLowerInvariant());
        var upperCaseResponse = await client.GetStatsAsync(canonicalSupplierId.ToUpperInvariant());
        var paddedResponse = await client.GetStatsAsync($"  {canonicalSupplierId}  ");

        var lowerCaseStats = await lowerCaseResponse.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        var upperCaseStats = await upperCaseResponse.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        var paddedStats = await paddedResponse.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        Assert.Equal(2, lowerCaseStats!.Ingested);
        Assert.Equal(2, upperCaseStats!.Ingested);
        Assert.Equal(2, paddedStats!.Ingested);
        Assert.Equal(canonicalSupplierId.ToUpperInvariant(), lowerCaseStats.SupplierId);
        Assert.Equal(canonicalSupplierId.ToUpperInvariant(), upperCaseStats.SupplierId);
        Assert.Equal(canonicalSupplierId.ToUpperInvariant(), paddedStats.SupplierId);
    }
}
