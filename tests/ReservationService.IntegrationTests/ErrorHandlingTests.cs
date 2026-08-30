using System.Net;
using System.Net.Http.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class ErrorHandlingTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;

    public ErrorHandlingTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task UnmatchedRoute_ReturnsProblemDetailsWith404Status()
    {
        var response = await client.GetAsync("/api/this-route-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions.Default);
        Assert.Equal(404, problem!.Status);
    }

    [Fact]
    public async Task WrongHttpMethodOnIngestRoute_ReturnsProblemDetails()
    {
        var response = await client.GetAsync("/api/reservations/ingest");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions.Default);
        Assert.Equal(405, problem!.Status);
    }

    [Fact]
    public async Task ThrottledResponse_ReturnsProblemDetailsShapeWith429Status()
    {
        var supplierId = TestIdentifiers.GuidLike();

        for (var i = 0; i < 100; i++)
        {
            await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));
        }

        var throttledResponse = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttledResponse.StatusCode);
        var problem = await throttledResponse.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions.Default);
        Assert.Equal(429, problem!.Status);
    }

    [Fact]
    public async Task InvalidBody_ReturnsValidationProblemDetailsShapeWithFieldErrors()
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
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetailsResponse>(JsonOptions.Default);
        Assert.Equal(400, problem!.Status);
        Assert.NotNull(problem.Errors);
        Assert.NotEmpty(problem.Errors!);
    }
}
