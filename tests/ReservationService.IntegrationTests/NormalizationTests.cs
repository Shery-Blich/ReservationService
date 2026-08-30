using System.Net;
using System.Net.Http.Json;
using ReservationService.IntegrationTests.Contracts;
using ReservationService.IntegrationTests.Infrastructure;

namespace ReservationService.IntegrationTests;

public sealed class NormalizationTests : IClassFixture<ReservationApiFactory>
{
    private readonly HttpClient client;
    private readonly string databasePath;

    public NormalizationTests(ReservationApiFactory factory)
    {
        client = factory.CreateClient();
        databasePath = factory.DatabasePath;
    }

    [Fact]
    public async Task SupplierIdCasingAndWhitespaceVariants_CollapseToSameIdentityForDedup()
    {
        var baseSupplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var first = IngestRequestFactory.CreateDefault(baseSupplierId.ToLowerInvariant(), reservationId);
        await client.PostIngestAsync(first);
        var second = first with { SupplierId = $"  {baseSupplierId.ToUpperInvariant()}  " };

        var response = await client.PostIngestAsync(second);

        var body = await response.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions.Default);
        Assert.Equal("duplicate", body!.Status);
        var rowCount = await ReservationDbAssertions.CountReservationsAsync(databasePath, baseSupplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.Equal(1, rowCount);
    }

    [Fact]
    public async Task NonZeroOffsetTimestamp_ParsesToSameInstantAsEquivalentZOffset()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var zOffsetReservationId = TestIdentifiers.GuidLike();
        var nonZOffsetReservationId = TestIdentifiers.GuidLike();
        var zOffsetRequest = IngestRequestFactory.CreateDefault(supplierId, zOffsetReservationId) with { CheckIn = "2026-01-10T00:00:00Z" };
        var nonZOffsetRequest = IngestRequestFactory.CreateDefault(supplierId, nonZOffsetReservationId) with { CheckIn = "2026-01-10T05:30:00+05:30" };

        await client.PostIngestAsync(zOffsetRequest);
        await client.PostIngestAsync(nonZOffsetRequest);

        var zOffsetRow = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), zOffsetReservationId.ToUpperInvariant());
        var nonZOffsetRow = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), nonZOffsetReservationId.ToUpperInvariant());
        var zInstant = DateTimeOffset.FromUnixTimeMilliseconds(zOffsetRow!.CheckIn).UtcDateTime;
        var nonZInstant = DateTimeOffset.FromUnixTimeMilliseconds(nonZOffsetRow!.CheckIn).UtcDateTime;
        Assert.Equal(zInstant, nonZInstant);
    }

    [Fact]
    public async Task NegativeOffsetTimestamp_ParsesToSameInstantAsEquivalentZOffset()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var zOffsetReservationId = TestIdentifiers.GuidLike();
        var negativeOffsetReservationId = TestIdentifiers.GuidLike();
        var zOffsetRequest = IngestRequestFactory.CreateDefault(supplierId, zOffsetReservationId) with { UpdatedAtUtc = "2026-01-10T08:00:00Z" };
        var negativeOffsetRequest = IngestRequestFactory.CreateDefault(supplierId, negativeOffsetReservationId) with { UpdatedAtUtc = "2026-01-10T00:00:00-08:00" };

        await client.PostIngestAsync(zOffsetRequest);
        await client.PostIngestAsync(negativeOffsetRequest);

        var zOffsetRow = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), zOffsetReservationId.ToUpperInvariant());
        var negativeOffsetRow = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), negativeOffsetReservationId.ToUpperInvariant());
        var zInstant = DateTimeOffset.FromUnixTimeMilliseconds(zOffsetRow!.UpdatedAtUtc).UtcDateTime;
        var negativeInstant = DateTimeOffset.FromUnixTimeMilliseconds(negativeOffsetRow!.UpdatedAtUtc).UtcDateTime;
        Assert.Equal(zInstant, negativeInstant);
    }

    [Fact]
    public async Task OffsetLessCheckIn_Returns400()
    {
        var request = IngestRequestFactory.CreateDefault(TestIdentifiers.GuidLike(), TestIdentifiers.GuidLike()) with { CheckIn = "2026-01-10T00:00:00" };

        var response = await client.PostIngestAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OffsetLessCheckOut_Returns400()
    {
        var request = IngestRequestFactory.CreateDefault(TestIdentifiers.GuidLike(), TestIdentifiers.GuidLike()) with { CheckOut = "2026-01-11T00:00:00" };

        var response = await client.PostIngestAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OffsetLessUpdatedAtUtc_Returns400()
    {
        var request = IngestRequestFactory.CreateDefault(TestIdentifiers.GuidLike(), TestIdentifiers.GuidLike()) with { UpdatedAtUtc = "2026-01-10T00:00:00" };

        var response = await client.PostIngestAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PriceWithHalfCent_RoundsAwayFromZeroRatherThanToEven()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var request = IngestRequestFactory.CreateDefault(supplierId, reservationId) with { Price = 12.325m };

        await client.PostIngestAsync(request);

        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.Equal(12.33m, row!.Price, 2);
    }

    [Fact]
    public async Task RoomIdCasingAndWhitespace_NormalizedBeforeStorage()
    {
        var supplierId = TestIdentifiers.GuidLike();
        var reservationId = TestIdentifiers.GuidLike();
        var request = IngestRequestFactory.CreateDefault(supplierId, reservationId) with { RoomId = "  room-42  " };

        await client.PostIngestAsync(request);

        var row = await ReservationDbAssertions.FindReservationAsync(databasePath, supplierId.ToUpperInvariant(), reservationId.ToUpperInvariant());
        Assert.Equal("ROOM-42", row!.RoomId);
    }
}
