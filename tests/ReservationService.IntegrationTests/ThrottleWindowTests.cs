using System.Net;
using System.Net.Http.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class ThrottleWindowTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;
    private readonly ReservationApiFactory factory;

    public ThrottleWindowTests(ReservationApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task OneHundredRequestsSucceed_OneHundredFirstIsThrottled()
    {
        var supplierId = TestIdentifiers.GuidLike();

        for (var i = 0; i < 100; i++)
        {
            var response = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
        }

        var oneHundredFirstResponse = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));

        Assert.Equal(HttpStatusCode.TooManyRequests, oneHundredFirstResponse.StatusCode);
    }

    [Fact]
    public async Task WindowRollover_AfterAdvancingPastBucket_RequestsSucceedAgain()
    {
        var supplierId = TestIdentifiers.GuidLike();

        for (var i = 0; i < 100; i++)
        {
            await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));
        }

        var throttledResponse = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));
        factory.TimeProvider.Advance(TimeSpan.FromSeconds(130));
        var afterRolloverResponse = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttledResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, afterRolloverResponse.StatusCode);
    }

    [Fact]
    public async Task ThrottledCount_OnlyIncrementsOnActualThrottleRejection()
    {
        var supplierId = TestIdentifiers.GuidLike();

        for (var i = 0; i < 100; i++)
        {
            await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));
        }

        var statsAfterFullWindow = await client.GetStatsAsync(supplierId);
        await client.PostIngestAsync(IngestRequestFactory.CreateDefault(supplierId, TestIdentifiers.GuidLike()));
        var statsAfterThrottle = await client.GetStatsAsync(supplierId);

        var beforeThrottleStats = await statsAfterFullWindow.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        var afterThrottleStats = await statsAfterThrottle.Content.ReadFromJsonAsync<StatsResponse>(JsonOptions.Default);
        Assert.Equal(100, beforeThrottleStats!.Ingested);
        Assert.Equal(0, beforeThrottleStats.Throttled);
        Assert.Equal(100, afterThrottleStats!.Ingested);
        Assert.Equal(1, afterThrottleStats.Throttled);
    }

    [Fact]
    public async Task DifferentSuppliers_ThrottleIndependentlyOfEachOther()
    {
        var throttledSupplierId = TestIdentifiers.GuidLike();
        var unaffectedSupplierId = TestIdentifiers.GuidLike();

        for (var i = 0; i < 100; i++)
        {
            await client.PostIngestAsync(IngestRequestFactory.CreateDefault(throttledSupplierId, TestIdentifiers.GuidLike()));
        }

        var throttledResponse = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(throttledSupplierId, TestIdentifiers.GuidLike()));
        var unaffectedResponse = await client.PostIngestAsync(IngestRequestFactory.CreateDefault(unaffectedSupplierId, TestIdentifiers.GuidLike()));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttledResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, unaffectedResponse.StatusCode);
    }
}
